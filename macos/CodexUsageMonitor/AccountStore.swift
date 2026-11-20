import CryptoKit
import Darwin
import Foundation

typealias JSONObject = [String: Any]

enum MonitorError: LocalizedError {
    case message(String)
    case loginRequired
    case credentialsChanged
    case http(Int)

    var errorDescription: String? {
        switch self {
        case .message(let text): return text
        case .loginRequired: return "会话需要重新登录"
        case .credentialsChanged: return "登录凭证已在其他程序中更新，请刷新后重试"
        case .http(let status): return "请求失败（HTTP \(status)）"
        }
    }
}

struct Account: Identifiable, Equatable {
    let id: String
    let name: String
    let label: String
    let home: URL
    let isLive: Bool
    let needsLogin: Bool

    var displayName: String { label.isEmpty ? name : label }
}

struct Credential {
    let data: Data
    let json: JSONObject

    init(data: Data) throws {
        self.data = data
        guard let json = try JSONSerialization.jsonObject(with: data) as? JSONObject else {
            throw MonitorError.loginRequired
        }
        self.json = json
    }

    var tokens: JSONObject { json["tokens"] as? JSONObject ?? [:] }

    var accessToken: String { tokens["access_token"] as? String ?? "" }

    var dataForCLI: Data {
        get throws {
            guard let legacy = json["last_refresh"] as? NSNumber,
                  legacy.doubleValue >= 0, legacy.doubleValue < 253_402_300_799
            else { return data }
            var normalized = json
            normalized["last_refresh"] = ISO8601DateFormatter().string(
                from: Date(timeIntervalSince1970: legacy.doubleValue)
            )
            return try JSONSerialization.data(withJSONObject: normalized)
        }
    }

    var claims: JSONObject {
        guard let token = tokens["id_token"] as? String,
              let payload = token.split(separator: ".", omittingEmptySubsequences: false).dropFirst().first
        else { return [:] }
        var base64 = payload.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.utf8.count % 4) % 4)
        guard let bytes = Data(base64Encoded: base64) else { return [:] }
        return (try? JSONSerialization.jsonObject(with: bytes)) as? JSONObject ?? [:]
    }

    var userID: String? {
        for value in [claims["sub"], tokens["account_id"]] {
            if let value = value as? String, !value.isEmpty { return value }
        }
        return nil
    }

    var name: String {
        for value in [claims["email"], claims["name"]] {
            if let value = value as? String, !value.isEmpty { return value }
        }
        return "Codex 账号"
    }
}

struct AccountStore {
    private let fm = FileManager.default
    let dataDirectory: URL
    let liveHome: URL

    init(dataDirectory: URL? = nil, liveHome: URL? = nil) {
        let home = FileManager.default.homeDirectoryForCurrentUser
        self.dataDirectory = dataDirectory ?? home
            .appendingPathComponent("Library/Application Support/CodexUsageMonitor", isDirectory: true)
        let customHome = ProcessInfo.processInfo.environment["CODEX_HOME"] ?? ""
        self.liveHome = liveHome ?? (customHome.isEmpty
            ? home.appendingPathComponent(".codex", isDirectory: true)
            : URL(fileURLWithPath: (customHome as NSString).expandingTildeInPath, isDirectory: true))
            .standardizedFileURL
    }

    var accountsDirectory: URL { dataDirectory.appendingPathComponent("accounts", isDirectory: true) }

    func ensurePrivateDirectory(_ directory: URL) throws {
        try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    }

    func authURL(_ home: URL) -> URL { home.appendingPathComponent("auth.json") }

    func credential(at home: URL) throws -> Credential {
        try Credential(data: Data(contentsOf: authURL(home)))
    }

    func currentAuthData(at home: URL) throws -> Data? {
        do { return try Data(contentsOf: authURL(home)) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    func writeCredential(_ data: Data, to home: URL, replacing original: Data?) throws {
        guard try currentAuthData(at: home) == original else { throw MonitorError.credentialsChanged }
        try ensurePrivateDirectory(home)
        let temporary = home.appendingPathComponent(".auth-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: temporary) }
        guard fm.createFile(atPath: temporary.path, contents: data, attributes: [.posixPermissions: 0o600]) else {
            throw MonitorError.message("无法写入本地凭证")
        }
        guard try currentAuthData(at: home) == original else { throw MonitorError.credentialsChanged }
        guard rename(temporary.path, authURL(home).path) == 0 else {
            throw MonitorError.message("无法替换本地凭证")
        }
    }

    private func metadata(at home: URL) -> JSONObject {
        guard let data = try? Data(contentsOf: home.appendingPathComponent("meta.json")) else { return [:] }
        return (try? JSONSerialization.jsonObject(with: data)) as? JSONObject ?? [:]
    }

    private func saveMetadata(_ json: JSONObject, at home: URL) throws {
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: home.appendingPathComponent("meta.json"), options: .atomic)
    }

    private func directories(prefix: Bool) -> [URL] {
        let directories = (try? fm.contentsOfDirectory(at: accountsDirectory,
            includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [.skipsHiddenFiles])) ?? []
        return directories.filter { url in
            guard let info = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else {
                return false
            }
            return info.isDirectory == true && info.isSymbolicLink != true
                && url.lastPathComponent.hasPrefix("_pending_") == prefix
        }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    private func snapshot(for id: String) -> URL? {
        directories(prefix: false).first { url in
            if let storedID = try? credential(at: url).userID { return storedID == id }
            return metadata(at: url)["uid"] as? String == id
        }
    }

    func accounts() -> [Account] {
        var seen = Set<String>()
        return ([liveHome] + directories(prefix: false)).compactMap { home in
            guard let credential = try? credential(at: home), let id = credential.userID,
                  seen.insert(id).inserted else { return nil }
            let live = home == liveHome
            let info = live ? snapshot(for: id).map { metadata(at: $0) } ?? [:] : metadata(at: home)
            return Account(id: id, name: credential.name, label: info["label"] as? String ?? "",
                           home: home, isLive: live, needsLogin: !live && info["needs_login"] as? Bool == true)
        }
    }

    @discardableResult
    func archive(_ credential: Credential) throws -> String {
        guard let id = credential.userID else { throw MonitorError.loginRequired }
        let hash = SHA256.hash(data: Data(id.utf8)).map { String(format: "%02x", $0) }.joined()
        try ensurePrivateDirectory(accountsDirectory)
        let directory = snapshot(for: id) ?? accountsDirectory.appendingPathComponent(hash, isDirectory: true)
        try writeCredential(credential.data, to: directory, replacing: currentAuthData(at: directory))
        var info = metadata(at: directory)
        info["uid"] = id
        info["nick"] = credential.name
        info["saved_at"] = Date().timeIntervalSince1970
        info.removeValue(forKey: "needs_login")
        info.removeValue(forKey: "bad_at")
        try saveMetadata(info, at: directory)
        return id
    }

    func switchCLI(to account: Account) throws {
        guard !account.isLive, let directory = snapshot(for: account.id),
              directory == account.home, metadata(at: directory)["needs_login"] as? Bool != true,
              let next = try? credential(at: directory), next.userID == account.id, !next.accessToken.isEmpty
        else { throw MonitorError.message("目标账号不可用，请重新登录后再切换") }
        let original = try currentAuthData(at: liveHome)
        if let original {
            let current = try Credential(data: original)
            guard let id = current.userID else {
                throw MonitorError.message("当前 CLI 凭证无法识别，请先在终端处理后再切换")
            }
            if id == account.id { return }
            try archive(current)
        }
        try writeCredential(next.dataForCLI, to: liveHome, replacing: original)
    }

    func renameAccount(_ account: Account, to label: String) throws {
        if account.isLive && snapshot(for: account.id) == nil {
            let active = try credential(at: liveHome)
            guard active.userID == account.id else { throw MonitorError.credentialsChanged }
            try archive(active)
        }
        guard let directory = snapshot(for: account.id) else { throw MonitorError.loginRequired }
        var info = metadata(at: directory)
        info["label"] = label.trimmingCharacters(in: .whitespacesAndNewlines)
        try saveMetadata(info, at: directory)
    }

    func remove(_ account: Account) throws {
        guard !account.isLive, (try? credential(at: liveHome).userID) != account.id,
              let directory = snapshot(for: account.id), directory == account.home else {
            throw MonitorError.message("当前 CLI 账号不能删除")
        }
        try fm.trashItem(at: directory, resultingItemURL: nil)
    }

    func markLogin(_ account: Account, required: Bool) throws {
        guard !account.isLive, (try? credential(at: account.home).userID) == account.id else { return }
        var info = metadata(at: account.home)
        guard (info["needs_login"] as? Bool == true) != required else { return }
        info["needs_login"] = required ? true : nil
        info["bad_at"] = required ? Date().timeIntervalSince1970 : nil
        try saveMetadata(info, at: account.home)
    }

    func newLoginDirectory() throws -> URL {
        let directory = accountsDirectory.appendingPathComponent("_pending_\(UUID().uuidString)", isDirectory: true)
        try ensurePrivateDirectory(dataDirectory)
        try ensurePrivateDirectory(accountsDirectory)
        try ensurePrivateDirectory(directory)
        return directory
    }

    func pendingLogins() -> [URL] { directories(prefix: true) }
}
