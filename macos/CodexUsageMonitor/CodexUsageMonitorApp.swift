import AppKit
import SwiftUI

@MainActor
final class MonitorDelegate: NSObject, NSApplicationDelegate {
    let model = MonitorModel()
    private var polling: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        polling = Task { await model.run() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        polling?.cancel()
    }
}

@main
@MainActor
struct CodexUsageMonitorApp: App {
    @NSApplicationDelegateAdaptor(MonitorDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra {
            MonitorMenuContent(model: delegate.model)
        } label: {
            MonitorMenuLabel(model: delegate.model)
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
private struct MonitorMenuContent: View {
    @ObservedObject var model: MonitorModel

    var body: some View {
        MonitorView(model: model, floating: false)
            .frame(width: 365 * model.settings.displaySize.scale, height: 490)
    }
}

@MainActor
private struct MonitorMenuLabel: View {
    @ObservedObject var model: MonitorModel

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: "gauge")
            if !model.menuBarTitle.isEmpty {
                Text(model.menuBarTitle)
                    .monospacedDigit()
            }
        }
        .accessibilityLabel("Codex 剩余额度 \(model.menuBarTitle)")
    }
}
