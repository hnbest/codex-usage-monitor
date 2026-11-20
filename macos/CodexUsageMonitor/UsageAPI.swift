import Foundation

struct QuotaWindow: Equatable {
    let remaining: Double?
    let resetAt: Date?
    let period: TimeInterval

    init(_ json: JSONObject, fallbackPeriod: TimeInterval, now: Date) {
        remaining = number(json["used_percent"]).map { min(100, max(0, 100 - $0)) }
        period = max(1, number(json["limit_window_seconds"]) ?? fallbackPeriod)
        resetAt = timestamp(json["reset_at"])
            ?? number(json["reset_after_seconds"]).map { now.addingTimeInterval(max(0, $0)) }
    }
}

struct ResetCard {
    let title: String
    let expiresAt: Date?
}

struct Usage {
    let plan: String
    let fiveHours: QuotaWindow
    let week: QuotaWindow
    let subscriptionStarts: Date?
    let subscriptionEnds: Date?
    let resetCount: Int?
    let applicableCount: Int?
    let cards: [ResetCard]?
    let nextCardExpiry: Date?
    let creditBalance: String?
    let unlimitedCredits: Bool
    let limitReached: Bool

    init(summary: JSONObject, details: JSONObject?, claims: JSONObject, now: Date = Date()) {
        plan = summary["plan_type"] as? String ?? ""
        let limits = summary["rate_limit"] as? JSONObject ?? [:]
        fiveHours = QuotaWindow(limits["primary_window"] as? JSONObject ?? [:], fallbackPeriod: 18_000, now: now)
        week = QuotaWindow(limits["secondary_window"] as? JSONObject ?? [:], fallbackPeriod: 604_800, now: now)
        limitReached = limits["limit_reached"] as? Bool ?? false
        let subscription = claims["https://api.openai.com/auth"] as? JSONObject ?? [:]
        subscriptionStarts = timestamp(subscription["chatgpt_subscription_active_start"])
        subscriptionEnds = timestamp(subscription["chatgpt_subscription_active_until"])
        let resets = summary["rate_limit_reset_credits"] as? JSONObject ?? [:]
        let creditInfo = summary["credits"] as? JSONObject ?? [:]
        creditBalance = creditInfo["balance"] as? String ?? (creditInfo["balance"] as? NSNumber)?.stringValue
        unlimitedCredits = creditInfo["unlimited"] as? Bool ?? false
        cards = (details?["credits"] as? [JSONObject])?.compactMap { row in
            guard (row["status"] as? String)?.lowercased() == "available" else { return nil }
            return ResetCard(title: row["title"] as? String ?? "重置卡", expiresAt: timestamp(row["expires_at"]))
        }.sorted { ($0.expiresAt ?? .distantFuture) < ($1.expiresAt ?? .distantFuture) }
        resetCount = (cards == nil ? nil : integer(details?["available_count"])) ?? integer(resets["available_count"])
        applicableCount = integer(resets["applicable_available_count"])
        nextCardExpiry = cards?.compactMap(\.expiresAt).first { $0 > now }
            ?? ["next_expiring_at", "earliest_expires_at", "expires_at", "expiration_timestamp", "expiry"]
                .compactMap { timestamp(resets[$0]) }.first
    }
}

private func number(_ raw: Any?) -> Double? {
    let value = (raw as? NSNumber)?.doubleValue ?? (raw as? String).flatMap(Double.init)
    return value.flatMap { $0.isFinite ? $0 : nil }
}

private func integer(_ raw: Any?) -> Int? {
    number(raw).flatMap { Int(exactly: $0) }
}

private func timestamp(_ raw: Any?) -> Date? {
    if var value = number(raw) {
        if value > 1e12 { value /= 1000 }
        return value > 0 && value < 253_402_300_799 ? Date(timeIntervalSince1970: value) : nil
    }
    guard let value = raw as? String else { return nil }
    let formatter = ISO8601DateFormatter()
    if let date = formatter.date(from: value) { return date }
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return formatter.date(from: value)
}

@MainActor
final class UsageAPI {
    private let usageURL = URL(string: "https://chatgpt.com/backend-api/wham/usage")!
    private let creditsURL = URL(string: "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits")!
    private let refreshURL = URL(string: "https://auth.openai.com/oauth/token")!
    private let session: URLSession
    private var refreshCooldown: [URL: (data: Data, until: Date)] = [:]

    init(session: URLSession = URLSession(configuration: .ephemeral)) {
        self.session = session
    }

    func fetch(for account: Account, store: AccountStore) async throws -> Usage {
        guard var credential = try? store.credential(at: account.home),
              credential.userID == account.id, !credential.accessToken.isEmpty
        else { throw MonitorError.loginRequired }

        let summary: JSONObject
        do {
            summary = try await request(usageURL, credential: credential)
        } catch MonitorError.http(let status) where status == 401 || status == 403 {
            guard try store.currentAuthData(at: account.home) == credential.data
            else { throw MonitorError.credentialsChanged }

            let cooldown = refreshCooldown[account.home]
            guard cooldown?.data != credential.data || (cooldown?.until ?? .distantPast) <= Date()
            else { throw MonitorError.loginRequired }

            do {
                credential = try await renew(credential, at: account.home, store: store)
                summary = try await request(usageURL, credential: credential)
            } catch let error as MonitorError {
                switch error {
                case .loginRequired, .http(401), .http(403):
                    guard try store.currentAuthData(at: account.home) == credential.data
                    else { throw MonitorError.credentialsChanged }
                    refreshCooldown[account.home] = (credential.data, Date().addingTimeInterval(300))
                    throw MonitorError.loginRequired
                default:
                    throw error
                }
            }
        }
        refreshCooldown.removeValue(forKey: account.home)
        let resets = summary["rate_limit_reset_credits"] as? JSONObject ?? [:]
        let details: JSONObject?
        if integer(resets["available_count"]) == 0 {
            details = nil
        } else {
            details = try? await request(creditsURL, credential: credential, timeout: 5)
        }
        guard try store.currentAuthData(at: account.home) == credential.data
        else { throw MonitorError.credentialsChanged }
        return Usage(summary: summary, details: details, claims: credential.claims)
    }

    private func renew(_ credential: Credential, at home: URL, store: AccountStore) async throws -> Credential {
        guard try store.currentAuthData(at: home) == credential.data else { throw MonitorError.credentialsChanged }
        guard let refreshToken = credential.tokens["refresh_token"] as? String, !refreshToken.isEmpty
        else { throw MonitorError.loginRequired }
        let response: JSONObject
        do {
            response = try await request(refreshURL, body: [
                "client_id": "app_EMoamEEZ73f0CkXaXp7hrann",
                "grant_type": "refresh_token",
                "refresh_token": refreshToken
            ])
        } catch MonitorError.http(let status) where status == 400 || status == 401 || status == 403 {
            throw MonitorError.loginRequired
        }
        guard let access = response["access_token"] as? String, !access.isEmpty else {
            throw MonitorError.loginRequired
        }
        var json = credential.json
        var tokens = credential.tokens
        for field in ["access_token", "id_token", "refresh_token"] {
            if let value = response[field] as? String, !value.isEmpty { tokens[field] = value }
        }
        json["tokens"] = tokens
        json["last_refresh"] = ISO8601DateFormatter().string(from: Date())
        let updated = try Credential(data: JSONSerialization.data(withJSONObject: json))
        guard updated.userID == credential.userID else { throw MonitorError.credentialsChanged }
        try store.writeCredential(updated.data, to: home, replacing: credential.data)
        return updated
    }

    private func request(_ url: URL, credential: Credential? = nil, body: JSONObject? = nil,
                         timeout: TimeInterval = 20) async throws -> JSONObject {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let credential {
            request.setValue("Bearer \(credential.accessToken)", forHTTPHeaderField: "Authorization")
            if let accountID = credential.tokens["account_id"] as? String, !accountID.isEmpty {
                request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-ID")
            }
        }
        if let body {
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else {
            throw MonitorError.message("服务返回了无效响应")
        }
        guard (200..<300).contains(response.statusCode) else { throw MonitorError.http(response.statusCode) }
        guard let json = try JSONSerialization.jsonObject(with: data) as? JSONObject else {
            throw MonitorError.message("服务返回了无效数据")
        }
        return json
    }
}
