import AppKit
import SwiftUI

@MainActor
struct MonitorView: View {
    @ObservedObject var model: MonitorModel
    let floating: Bool
    @State private var showSettings = false
    @State private var showCredits = false
    @State private var showRename = false
    @State private var showRemove = false
    @State private var showRestart = false
    @State private var target: Account?
    @State private var label = ""

    private var scale: CGFloat { model.settings.displaySize.scale }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            toolbar

            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    if showSettings {
                        settings
                    } else if model.accounts.isEmpty {
                        emptyState
                    } else if model.settings.overview {
                        ForEach(model.accounts) { account in overview(account) }
                    } else if let account = model.selected {
                        details(account)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 2)
            }

            footer
        }
        .padding(floating ? 17 : 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .font(.system(size: 13 * scale))
        .alert("编辑账号备注", isPresented: $showRename) {
            TextField("备注", text: $label)
            Button("取消", role: .cancel) {}
            Button("保存") { if let target { model.rename(target, to: label) } }
        }
        .alert("移除账号快照？", isPresented: $showRemove) {
            Button("取消", role: .cancel) {}
            Button("移到废纸篓", role: .destructive) { if let target { model.remove(target) } }
        } message: {
            Text(target?.displayName ?? "")
        }
        .alert("重新打开 Codex？", isPresented: $showRestart) {
            Button("取消", role: .cancel) {}
            Button("重启") { Task { await model.restartCodex() } }
        } message: {
            Text("Codex 将先退出，再重新打开。正在运行的任务可能会中断。")
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text(showSettings ? "设置" : "Codex 用量")
                .font(.system(size: 17 * scale, weight: .semibold))
            Spacer()
            if !showSettings, !model.accounts.isEmpty {
                Button(model.settings.overview ? "详情" : "总览") {
                    model.settings.overview.toggle()
                }
            }
            Button {
                showSettings.toggle()
            } label: {
                Image(systemName: showSettings ? "chevron.backward" : "gearshape")
            }
            .help(showSettings ? "返回" : "设置")
            .accessibilityLabel(showSettings ? "返回" : "设置")
            Menu {
                Button(model.floatVisible ? "隐藏置顶浮窗" : "显示置顶浮窗") { model.toggleFloat() }
                Button("复位浮窗位置") { model.resetFloat() }
                if model.waitingForLogin {
                    Button("停止等待终端登录") { model.stopWaitingForLogin() }
                }
                if model.canRestart {
                    Button("重启 Codex 桌面应用…") { showRestart = true }
                }
                Divider()
                Button("退出监控") { NSApp.terminate(nil) }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .accessibilityLabel("更多操作")
        }
        .buttonStyle(.borderless)
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("未检测到 Codex 登录", systemImage: "person.crop.circle")
                .fontWeight(.semibold)
            Text("可以从终端登录，或在这里添加独立账号。")
                .foregroundStyle(.secondary)
            Button("添加独立账号") { model.startLogin() }
                .disabled(model.refreshing)
        }
        .frame(maxWidth: .infinity, minHeight: 180, alignment: .center)
    }

    private func details(_ account: Account) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text(account.displayName).fontWeight(.semibold).lineLimit(1)
                    if account.isLive {
                        Text("当前 CLI 账号").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if model.accounts.count > 1 {
                    Menu("切换显示") {
                        ForEach(model.accounts) { item in
                            Button(item.displayName) { model.select(item) }
                        }
                    }
                    .fixedSize()
                }
            }

            if let usage = model.usage[account.id] {
                HStack(spacing: 14) {
                    QuotaRing(title: "5 小时", quota: usage.fiveHours, scale: scale)
                    QuotaRing(title: "每周", quota: usage.week, scale: scale)
                }
                .frame(maxWidth: .infinity)
                if !usage.plan.isEmpty || usage.limitReached {
                    HStack {
                        Text(usage.plan.uppercased()).fontWeight(.semibold)
                        if usage.limitReached { Text("额度已达上限").foregroundStyle(.orange) }
                    }
                }
                if let starts = usage.subscriptionStarts {
                    LabeledContent("订阅起始", value: displayDate(starts))
                }
                if let ends = usage.subscriptionEnds {
                    LabeledContent("订阅到期", value: displayDate(ends))
                        .foregroundStyle(ends.timeIntervalSinceNow < 259_200 ? Color.red : Color.primary)
                }
                if let expiry = usage.nextCardExpiry, (usage.resetCount ?? 0) > 0 {
                    let remaining = expiry.timeIntervalSinceNow
                    if remaining > 0 && remaining <= 172_800 {
                        Label("重置卡即将到期 · \(displayDate(expiry))", systemImage: "exclamationmark.triangle")
                            .foregroundStyle(remaining <= 86_400 ? Color.red : Color.orange)
                    }
                }
                if usage.unlimitedCredits {
                    LabeledContent("点数余额", value: "不限")
                } else if let balance = usage.creditBalance {
                    LabeledContent("点数余额", value: balance)
                }
                let count = usage.resetCount.map { "\($0) 张" } ?? "—"
                DisclosureGroup("重置卡 \(count)", isExpanded: $showCredits) {
                    creditDetails(usage)
                }
            }

            if let error = model.errors[account.id] {
                Text(error).foregroundStyle(.orange)
            }
            accountButtons(account)
        }
    }

    private func creditDetails(_ usage: Usage) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            if let applicable = usage.applicableCount {
                LabeledContent("当前可用", value: "\(applicable) 张")
            }
            if let deadline = usage.nextCardExpiry {
                LabeledContent("最近到期", value: displayDate(deadline))
                    .foregroundStyle(deadline.timeIntervalSinceNow <= 172_800 ? Color.orange : Color.primary)
            }
            if let cards = usage.cards, !cards.isEmpty {
                ForEach(cards.indices, id: \.self) { index in
                    let card = cards[index]
                    LabeledContent(card.title, value: card.expiresAt.map(displayDate) ?? "到期时间未知")
                }
            } else {
                let empty = usage.resetCount == 0 || usage.cards?.isEmpty == true
                Text(empty ? "暂无可用重置卡" : "暂未取得重置卡明细").foregroundStyle(.secondary)
            }
            if let url = URL(string: "https://chatgpt.com/codex/settings/usage") {
                Link("在浏览器打开 Codex 用量页", destination: url)
            }
        }
        .font(.system(size: 12 * scale))
        .padding(.top, 6)
    }

    private func overview(_ account: Account) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Button(account.displayName) { model.select(account) }
                    .buttonStyle(.plain)
                    .fontWeight(.semibold)
                    .lineLimit(1)
                Spacer()
                if account.isLive {
                    Text("CLI").font(.caption).foregroundStyle(.secondary)
                }
                if let usage = model.usage[account.id], !usage.plan.isEmpty {
                    Text(usage.plan.uppercased()).font(.caption).foregroundStyle(.secondary)
                }
            }
            if let usage = model.usage[account.id] {
                quotaBar("5 小时", quota: usage.fiveHours)
                quotaBar("每周", quota: usage.week)
            }
            if let error = model.errors[account.id] {
                Text(error).foregroundStyle(.orange)
            }
            accountButtons(account)
        }
        .padding(10)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 9))
    }

    private func quotaBar(_ title: String, quota: QuotaWindow) -> some View {
        HStack {
            Text(title).frame(width: 52 * scale, alignment: .leading)
            ProgressView(value: quota.remaining ?? 0, total: 100)
                .tint(quotaColor(quota.remaining))
                .accessibilityLabel("\(title)剩余额度")
            Text(quota.remaining.map { "\(Int($0.rounded()))%" } ?? "—")
                .monospacedDigit()
                .frame(width: 44 * scale, alignment: .trailing)
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text(countdown(quota.resetAt, now: context.date))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .frame(width: 69 * scale, alignment: .trailing)
        }
    }

    private func accountButtons(_ account: Account) -> some View {
        HStack {
            if account.needsLogin || model.requiresLogin.contains(account.id) {
                Button("重新登录") { model.startLogin(for: account) }
            } else if !account.isLive {
                Button("切为 CLI") { model.switchCLI(account) }
            }
            if model.canRestart && account.isLive {
                Button("重启 Codex…") { showRestart = true }
            }
        }
        .buttonStyle(.borderless)
        .disabled(model.refreshing)
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("采集间隔", selection: $model.settings.interval) {
                ForEach([15, 30, 60, 120, 300, 600, 1800, 3600], id: \.self) {
                    Text("\($0) 秒").tag($0)
                }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("浮窗透明度 \(Int(model.settings.opacity * 100))%")
                Slider(value: $model.settings.opacity, in: 0.4...1, step: 0.01)
                    .accessibilityLabel("浮窗透明度")
            }
            Picker("界面字号", selection: $model.settings.size) {
                ForEach(DisplaySize.allCases) { size in Text(size.rawValue).tag(size.rawValue) }
            }
            .pickerStyle(.segmented)
            Toggle("菜单栏显示额度", isOn: $model.settings.showQuotaInMenuBar)
            Toggle("登录时启动", isOn: Binding(get: { model.launchAtLogin }, set: model.setLaunchAtLogin))
            Picker("低额度提醒", selection: Binding(get: { model.settings.notifyThreshold },
                                                        set: model.setNotificationThreshold)) {
                Text("关闭").tag(0)
                ForEach([10, 20, 30], id: \.self) { Text("≤ \($0)%").tag($0) }
            }

            Divider()
            HStack {
                Text("账号管理").fontWeight(.semibold)
                Spacer()
                Button("添加独立账号") { model.startLogin() }
                    .disabled(model.refreshing)
            }
            if model.accounts.contains(where: \.isLive) {
                Button("保存当前 CLI 账号") { model.archiveCurrent() }
                    .disabled(model.refreshing)
            }

            ForEach(model.accounts) { account in
                HStack {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(account.displayName).lineLimit(1)
                        if account.isLive { Text("当前 CLI").font(.caption).foregroundStyle(.secondary) }
                    }
                    Spacer()
                    Menu {
                        Button("查看额度") { model.select(account); showSettings = false }
                        Button("重新登录") { model.startLogin(for: account) }
                        Button("重命名…") {
                            target = account
                            label = account.label
                            showRename = true
                        }
                        if !account.isLive {
                            Button("切为 CLI") { model.switchCLI(account) }
                                .disabled(account.needsLogin || model.requiresLogin.contains(account.id))
                            Button("移除账号…", role: .destructive) { target = account; showRemove = true }
                        }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .accessibilityLabel("管理 \(account.displayName)")
                    .disabled(model.refreshing)
                }
            }
            Text("版本 \(model.version)").font(.caption).foregroundStyle(.secondary)
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 5) {
            if !model.status.isEmpty {
                Text(model.status).foregroundStyle(.secondary).font(.caption)
            }
            HStack {
                if model.refreshing {
                    ProgressView().controlSize(.small).accessibilityLabel("正在刷新")
                    Text("刷新中")
                } else if let updated = model.updatedAt {
                    Text("更新于 \(updated.formatted(date: .omitted, time: .standard))")
                }
                Spacer()
                Button("每 \(model.settings.interval) 秒 · 刷新") { Task { await model.refresh() } }
                    .buttonStyle(.borderless)
                    .disabled(model.refreshing)
            }
            .font(.system(size: 11 * scale))
            .foregroundStyle(.secondary)
        }
    }
}

private struct QuotaRing: View {
    let title: String
    let quota: QuotaWindow
    let scale: CGFloat
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 5) {
            TimelineView(.periodic(from: .now, by: 1)) { context in
                let fraction = quota.resetAt.map {
                    min(1, max(0, $0.timeIntervalSince(context.date) / quota.period))
                } ?? 0
                ZStack {
                    TimelineView(.animation(minimumInterval: 1 / 18, paused: reduceMotion || quota.resetAt == nil)) { frame in
                        let phase = reduceMotion ? 0 : frame.date.timeIntervalSinceReferenceDate
                        WaveFill(fraction: fraction, phase: phase)
                            .fill(quotaColor(quota.remaining).opacity(0.24))
                            .clipShape(Circle())
                            .padding(7)
                    }
                    Circle().strokeBorder(.quaternary, lineWidth: 5)
                    Circle()
                        .trim(from: 0, to: CGFloat((quota.remaining ?? 0) / 100))
                        .stroke(quotaColor(quota.remaining), style: StrokeStyle(lineWidth: 5, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .padding(2.5)

                    VStack(spacing: 2) {
                        Text(quota.remaining.map { "\(Int($0.rounded()))%" } ?? "—")
                            .font(.system(size: 23 * scale, weight: .semibold, design: .rounded))
                        Text(countdown(quota.resetAt, now: context.date))
                            .font(.system(size: 10 * scale, weight: .medium))
                            .foregroundStyle(.secondary)
                    }
                    .monospacedDigit()
                }
                .frame(width: 106 * scale, height: 106 * scale)
            }
            Text(title).fontWeight(.medium)
        }
        .accessibilityElement(children: .combine)
    }
}

private struct WaveFill: Shape {
    let fraction: Double
    let phase: Double

    func path(in rect: CGRect) -> Path {
        var path = Path()
        guard fraction > 0 else { return path }
        let fill = CGFloat(fraction)
        let level = rect.maxY - rect.height * fill
        let amplitude = min(3, rect.height * min(fill, 1 - fill))
        let offset = CGFloat(phase.truncatingRemainder(dividingBy: .pi * 2)) * 2
        path.move(to: CGPoint(x: rect.minX, y: rect.maxY))
        for step in 0...32 {
            let position = CGFloat(step) / 32
            let x = rect.minX + position * rect.width
            let y = level + amplitude * sin(position * 2 * .pi + offset)
            path.addLine(to: CGPoint(x: x, y: y))
        }
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        path.closeSubpath()
        return path
    }
}

private func quotaColor(_ remaining: Double?) -> Color {
    guard let remaining else { return .secondary }
    if remaining <= 10 { return .red }
    if remaining <= 30 { return .orange }
    return .green
}

private func countdown(_ date: Date?, now: Date) -> String {
    guard let date else { return "—" }
    let seconds = max(0, Int(date.timeIntervalSince(now)))
    if seconds >= 86_400 { return "\(seconds / 86_400)天\(seconds % 86_400 / 3600)时" }
    return String(format: "%02d:%02d:%02d", seconds / 3600, seconds % 3600 / 60, seconds % 60)
}

private func displayDate(_ value: Date) -> String {
    value.formatted(date: .abbreviated, time: .shortened)
}
