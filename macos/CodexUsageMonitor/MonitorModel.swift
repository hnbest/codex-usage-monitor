import AppKit
import Darwin
import ServiceManagement
import SwiftUI
import UserNotifications

enum DisplaySize: String, CaseIterable, Identifiable {
    case small = "小", medium = "中", large = "大"

    var id: Self { self }
    var scale: CGFloat {
        switch self {
        case .small: return 1
        case .medium: return 1.16
        case .large: return 1.32
        }
    }
}

struct MonitorSettings: Codable {
    var interval = 60
    var opacity = 0.80
    var size = DisplaySize.small.rawValue
    var notifyThreshold = 0
    var displayID = ""
    var overview = false
    var showQuotaInMenuBar = true

    var displaySize: DisplaySize { DisplaySize(rawValue: size) ?? .small }
}

@MainActor
final class MonitorModel: ObservableObject {
    @Published var settings: MonitorSettings {
        didSet {
            if let data = try? JSONEncoder().encode(settings) {
                UserDefaults.standard.set(data, forKey: "monitorSettings")
            }
            if oldValue.interval != settings.interval {
                nextPoll = Date().addingTimeInterval(TimeInterval(settings.interval))
            }
            panel?.alphaValue = settings.opacity
        }
    }
    @Published private(set) var accounts: [Account] = []
    @Published private(set) var usage: [String: Usage] = [:]
    @Published private(set) var errors: [String: String] = [:]
    @Published private(set) var requiresLogin: Set<String> = []
    @Published private(set) var refreshing = false
    @Published private(set) var floatVisible = false
    @Published private(set) var launchAtLogin = SMAppService.mainApp.status == .enabled
    @Published private(set) var canRestart = false
    @Published private(set) var waitingForLogin = false
    @Published var status = ""
    @Published private(set) var updatedAt: Date?

    private let fileStore = AccountStore()
    private let api = UsageAPI()
    private var panel: NSPanel?
    private var lockDescriptor: Int32 = -1
    private var nextPoll = Date.distantPast
    private var observedAuth: [URL: Date] = [:]
    private var notices: [String: Date] = [:]
    private var restarting = false
    private var pending: [URL: LoginRequest] = [:] {
        didSet { waitingForLogin = pending.values.contains { !$0.cancelled } }
    }

    private struct LoginRequest {
        let account: Account?
        let original: Data?
        var cancelled = false
    }

    init() {
        let saved = UserDefaults.standard.data(forKey: "monitorSettings")
        settings = saved.flatMap { try? JSONDecoder().decode(MonitorSettings.self, from: $0) } ?? MonitorSettings()
    }

    var selected: Account? {
        accounts.first { $0.id == settings.displayID } ?? accounts.first
    }

    var menuBarTitle: String {
        guard settings.showQuotaInMenuBar else { return "" }
        guard let selected, let item = usage[selected.id] else { return "—" }
        return [item.fiveHours.remaining, item.week.remaining].map {
            $0.map { "\(Int($0.rounded()))%" } ?? "—"
        }.joined(separator: " / ")
    }

    var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
    }

    func run() async {
        guard lockDescriptor == -1 else { return }
        do {
            try fileStore.ensurePrivateDirectory(fileStore.dataDirectory)
            let lockURL = fileStore.dataDirectory.appendingPathComponent("monitor.lock")
            let descriptor = open(lockURL.path, O_CREAT | O_RDWR | O_NOFOLLOW, mode_t(0o600))
            guard descriptor >= 0 else { throw MonitorError.message("无法打开监控程序锁") }
            guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
                close(descriptor)
                NSApp.terminate(nil)
                return
            }
            lockDescriptor = descriptor
        } catch {
            status = error.localizedDescription
            return
        }

        defer {
            close(lockDescriptor)
            lockDescriptor = -1
        }
        while !Task.isCancelled {
            if !refreshing {
                let imported = collectLogins()
                let authChanged = authVersions() != observedAuth
                if imported || authChanged || Date() >= nextPoll { await refresh() }
            }
            do { try await Task.sleep(nanoseconds: 2_000_000_000) }
            catch { break }
        }
    }

    private func authVersions() -> [URL: Date] {
        let homes = Set(accounts.map(\.home) + [fileStore.liveHome])
        var versions: [URL: Date] = [:]
        for home in homes {
            let file = fileStore.authURL(home)
            versions[home] = (try? file.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        }
        return versions
    }

    func refresh() async {
        guard lockDescriptor >= 0, !refreshing else { return }
        refreshing = true
        defer {
            refreshing = false
            nextPoll = Date().addingTimeInterval(TimeInterval(settings.interval))
        }
        reloadAccounts()
        let before = authVersions()
        var retryChanged = false
        for account in accounts {
            if Task.isCancelled { return }
            do {
                let item = try await api.fetch(for: account, store: fileStore)
                usage[account.id] = item
                errors.removeValue(forKey: account.id)
                requiresLogin.remove(account.id)
                do { try fileStore.markLogin(account, required: false) }
                catch { status = error.localizedDescription }
                await notifyLowQuota(account, usage: item)
            } catch MonitorError.credentialsChanged {
                errors[account.id] = MonitorError.credentialsChanged.localizedDescription
                retryChanged = true
            } catch MonitorError.loginRequired {
                errors[account.id] = MonitorError.loginRequired.localizedDescription
                usage.removeValue(forKey: account.id)
                requiresLogin.insert(account.id)
                do { try fileStore.markLogin(account, required: true) }
                catch { status = error.localizedDescription }
            } catch {
                errors[account.id] = error.localizedDescription
            }
        }
        reloadAccounts()
        let after = authVersions()
        observedAuth = retryChanged || before != after ? before : after
        updatedAt = Date()
    }

    private func reloadAccounts() {
        accounts = fileStore.accounts()
        let ids = Set(accounts.map(\.id))
        usage = usage.filter { ids.contains($0.key) }
        errors = errors.filter { ids.contains($0.key) }
        requiresLogin.formIntersection(ids)
    }

    func select(_ account: Account) {
        settings.displayID = account.id
        settings.overview = false
    }

    private func changeAccounts(_ action: () throws -> String) {
        guard lockDescriptor >= 0, !refreshing else { return }
        do {
            status = try action()
            reloadAccounts()
            Task { await refresh() }
        } catch {
            status = error.localizedDescription
        }
    }

    func archiveCurrent() {
        changeAccounts {
            guard let credential = try? fileStore.credential(at: fileStore.liveHome) else {
                throw MonitorError.loginRequired
            }
            try fileStore.archive(credential)
            return "已保存当前 CLI 账号"
        }
    }

    func switchCLI(_ account: Account) {
        changeAccounts {
            try fileStore.switchCLI(to: account)
            select(account)
            canRestart = true
            return "已切为 CLI 账号，重新打开终端后使用"
        }
    }

    func rename(_ account: Account, to label: String) {
        changeAccounts {
            try fileStore.renameAccount(account, to: label)
            return "已更新账号备注"
        }
    }

    func remove(_ account: Account) {
        changeAccounts {
            try fileStore.remove(account)
            return "已将账号快照移到废纸篓"
        }
    }

    func startLogin(for account: Account? = nil) {
        guard lockDescriptor >= 0, !refreshing else { return }
        if pending.values.contains(where: { !$0.cancelled && $0.account?.id == account?.id }) {
            status = "该登录正在等待终端完成"
            return
        }
        guard let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else {
            status = "找不到终端应用"
            return
        }
        do {
            let original: Data?
            if let account { original = try fileStore.currentAuthData(at: account.home) }
            else { original = nil }
            let directory = try fileStore.newLoginDirectory()
            let scriptURL = directory.appendingPathComponent("登录 Codex.command")
            let script = """
            #!/bin/zsh -il
            export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"
            export CODEX_HOME=\(shellQuote(directory.path))
            if command -v codex >/dev/null 2>&1; then
              if codex -c 'cli_auth_credentials_store="file"' login; then
                /usr/bin/touch "$CODEX_HOME/.login-complete"
              else
                /usr/bin/touch "$CODEX_HOME/.login-failed"
              fi
            else
              echo '找不到 Codex CLI，请安装并确认终端能运行 codex。'
              /usr/bin/touch "$CODEX_HOME/.login-failed"
            fi
            """
            try Data(script.utf8).write(to: scriptURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: scriptURL.path)
            pending[directory] = LoginRequest(account: account, original: original)
            status = "请在终端完成登录"
            Task {
                do {
                    _ = try await NSWorkspace.shared.open([scriptURL], withApplicationAt: terminal,
                                                          configuration: NSWorkspace.OpenConfiguration())
                } catch {
                    pending.removeValue(forKey: directory)
                    try? FileManager.default.removeItem(at: directory)
                    status = error.localizedDescription
                }
            }
        } catch {
            status = error.localizedDescription
        }
    }

    func stopWaitingForLogin() {
        for directory in Array(pending.keys) {
            pending[directory]?.cancelled = true
        }
        status = "已停止接收登录结果，可以关闭对应终端后重试"
    }

    private func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private func collectLogins() -> Bool {
        var changed = false
        let fm = FileManager.default
        for (directory, request) in Array(pending) {
            let failed = fm.fileExists(atPath: directory.appendingPathComponent(".login-failed").path)
            let complete = fm.fileExists(atPath: directory.appendingPathComponent(".login-complete").path)
            guard failed || complete else { continue }
            pending.removeValue(forKey: directory)
            defer {
                do { try fm.removeItem(at: directory) }
                catch { status += "。临时登录目录清理失败" }
            }
            guard !request.cancelled else { continue }
            do {
                guard complete && !failed,
                      let credential = try? fileStore.credential(at: directory),
                      let id = credential.userID, !credential.accessToken.isEmpty else {
                    throw MonitorError.message("未取得登录凭证，请检查终端后重试")
                }
                if let target = request.account {
                    guard id == target.id else { throw MonitorError.message("登录账号与所选账号不同，请重试") }
                    guard fileStore.accounts().first(where: { $0.id == id })?.home == target.home else {
                        throw MonitorError.credentialsChanged
                    }
                    try fileStore.writeCredential(credential.data, to: target.home, replacing: request.original)
                    try fileStore.markLogin(target, required: false)
                    status = "已恢复账号登录"
                } else {
                    if (try? fileStore.credential(at: fileStore.liveHome).userID) == id {
                        throw MonitorError.message("这个账号已经是当前 CLI 账号，可保存当前登录")
                    }
                    try fileStore.archive(credential)
                    status = "独立登录已保存，可在账号列表设为 CLI"
                }
                changed = true
            } catch {
                status = error.localizedDescription
            }
        }
        return changed
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            if SMAppService.mainApp.status == .requiresApproval {
                status = "请在系统设置的登录项中允许自动启动"
                SMAppService.openSystemSettingsLoginItems()
            }
        } catch {
            status = error.localizedDescription
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    func setNotificationThreshold(_ threshold: Int) {
        settings.notifyThreshold = threshold
        guard threshold > 0 else { return }
        Task {
            do {
                let granted = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
                if !granted { status = "请在系统设置中允许此应用发送通知" }
            } catch {
                status = error.localizedDescription
            }
        }
    }

    private func notifyLowQuota(_ account: Account, usage item: Usage) async {
        let threshold = settings.notifyThreshold
        guard threshold > 0 else { return }
        for (label, window) in [("5 小时", item.fiveHours), ("每周", item.week)] {
            guard let remaining = window.remaining, remaining <= Double(threshold),
                  let reset = window.resetAt, reset > Date() else { continue }
            let key = "\(account.id)-\(label)"
            if let previous = notices[key], abs(previous.timeIntervalSince(reset)) < 60 { continue }
            let content = UNMutableNotificationContent()
            content.title = "Codex 额度提醒"
            content.body = "\(account.displayName) 的\(label)剩余额度为 \(Int(remaining.rounded()))%"
            do {
                try await UNUserNotificationCenter.current().add(
                    UNNotificationRequest(identifier: key, content: content, trigger: nil)
                )
                notices[key] = reset
            } catch {
                status = "未能发送额度提醒，请检查系统通知权限"
            }
        }
    }

    func restartCodex() async {
        guard !restarting else { return }
        restarting = true
        defer { restarting = false }

        let workspace = NSWorkspace.shared
        let appID = "com.openai.codex"
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: appID)
        guard let applicationURL = running.first?.bundleURL ?? workspace.urlForApplication(withBundleIdentifier: appID) else {
            status = "找不到 Codex 桌面应用"
            return
        }
        for app in running where !app.isTerminated {
            guard app.terminate() else {
                status = "Codex 拒绝退出，请在 Codex 中手动重启"
                return
            }
        }
        do {
            for _ in 0..<50 {
                if running.allSatisfy(\.isTerminated) { break }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            guard running.allSatisfy(\.isTerminated) else {
                status = "等待 Codex 退出超时，请检查是否有待确认的窗口"
                return
            }
            _ = try await workspace.openApplication(at: applicationURL, configuration: NSWorkspace.OpenConfiguration())
            canRestart = false
            status = "已重新打开 Codex"
        } catch {
            status = error.localizedDescription
        }
    }

    func toggleFloat() {
        if let panel, panel.isVisible {
            panel.orderOut(nil)
            floatVisible = false
            return
        }
        if panel == nil {
            let floating = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 380, height: 420),
                                   styleMask: [.titled, .fullSizeContentView, .resizable],
                                   backing: .buffered, defer: false)
            floating.title = "Codex Usage Monitor"
            floating.titlebarAppearsTransparent = true
            floating.titleVisibility = .hidden
            floating.isMovableByWindowBackground = true
            floating.isFloatingPanel = true
            floating.hidesOnDeactivate = false
            floating.isExcludedFromWindowsMenu = true
            floating.level = .floating
            floating.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            floating.minSize = NSSize(width: 320, height: 230)
            floating.alphaValue = settings.opacity
            floating.contentView = NSHostingView(rootView: MonitorView(model: self, floating: true))
            if !floating.setFrameUsingName("QuotaFloat") { position(floating) }
            clamp(floating)
            floating.setFrameAutosaveName("QuotaFloat")
            panel = floating
        }
        panel?.orderFrontRegardless()
        floatVisible = true
    }

    func resetFloat() {
        guard let panel else {
            UserDefaults.standard.removeObject(forKey: "NSWindow Frame QuotaFloat")
            return
        }
        position(panel)
        panel.saveFrame(usingName: "QuotaFloat")
    }

    private func position(_ panel: NSPanel) {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        panel.setFrameOrigin(NSPoint(x: visible.maxX - panel.frame.width - 18, y: visible.minY + 18))
        clamp(panel)
    }

    private func clamp(_ panel: NSPanel) {
        let screen = NSScreen.screens.first { $0.frame.intersects(panel.frame) } ?? NSScreen.main
        guard let frame = screen?.visibleFrame else { return }
        let size = NSSize(width: min(panel.frame.width, frame.width), height: min(panel.frame.height, frame.height))
        let origin = NSPoint(x: min(max(panel.frame.minX, frame.minX), frame.maxX - size.width),
                             y: min(max(panel.frame.minY, frame.minY), frame.maxY - size.height))
        panel.setFrame(NSRect(origin: origin, size: size), display: false)
    }
}
