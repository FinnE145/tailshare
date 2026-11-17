import AppKit
import Darwin
import ServiceManagement
import SwiftUI
import TailshareCore

@main
struct TailshareApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(model: delegate.model)
        } label: {
            MenuBarIcon(model: delegate.model)
        }
        .menuBarExtraStyle(.window)
    }
}

/// `paperplane` normally and `arrow.down.circle` while anything is being received: two shapes, not two colours.
private struct MenuBarIcon: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Image(systemName: model.isReceiving ? "arrow.down.circle" : "paperplane")
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let notifier: Notifier
    let model: AppModel

    override init() {
        let notifier = Notifier()
        self.notifier = notifier
        model = MainActor.assumeIsolated { AppModel(notifier: notifier) }
        super.init()
    }

    // Registered before launch finishes so a link that launches the app (the extension's hand-off when
    // the app isn't running) is delivered here too.
    func applicationWillFinishLaunching(_ notification: Notification) {
        NSAppleEventManager.shared().setEventHandler(
            self, andSelector: #selector(handleURL(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        notifier.setUp()
        registerLoginItemOnce()
        MainActor.assumeIsolated { model.start() }
    }

    // First run only. A person who turns it off in System Settings stays turned off: it is never re-registered.
    private func registerLoginItemOnce() {
        let key = "didRegisterLoginItem"
        guard !UserDefaults.standard.bool(forKey: key) else { return }
        try? SMAppService.mainApp.register()
        UserDefaults.standard.set(true, forKey: key)
    }

    /// By bundle ID through Launch Services, and failing that (an extension process may not be a
    /// "running application" to it) by the executable the process was started from: it must be the
    /// extension binary inside this very app. Anything else, or a process that is already gone, is no.
    private func senderIsShareExtension(_ pid: pid_t) -> Bool {
        if HandOff.isFromExtension(NSRunningApplication(processIdentifier: pid)?.bundleIdentifier) { return true }

        var buffer = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        guard proc_pidpath(pid, &buffer, UInt32(buffer.count)) > 0,
              let plugIns = Bundle.main.builtInPlugInsURL else { return false }
        let expected = plugIns.appendingPathComponent("ShareExtension.appex/Contents/MacOS/ShareExtension")
        return URL(fileURLWithPath: String(cString: buffer)).resolvingSymlinksInPath().path == expected.resolvingSymlinksInPath().path
    }

    @objc private func handleURL(_ event: NSAppleEventDescriptor, withReplyEvent reply: NSAppleEventDescriptor) {
        guard let string = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue,
              let url = URL(string: string) else { return }

        // Any app or web page can open a tailshare: link, so only the Share Extension's counts.
        let senderPID = event.attributeDescriptor(forKeyword: AEKeyword(keySenderPIDAttr))?.int32Value
        guard let senderPID, senderIsShareExtension(pid_t(senderPID)), let request = HandOff.parse(url) else { return }

        MainActor.assumeIsolated {
            switch request {
            case .files(let paths):
                model.openSendWindow(files: paths.map { URL(fileURLWithPath: $0) }, text: nil)
            case .text:
                // Read once and cleared at once: it is the user's text and nothing else should find it there.
                let pasteboard = NSPasteboard(name: NSPasteboard.Name(AppPaths.handOffPasteboard))
                let text = pasteboard.string(forType: .string)
                pasteboard.clearContents()
                model.openSendWindow(files: nil, text: text)
            }
        }
    }
}
