import AppKit
import SwiftUI

/// The window that asks whether to accept a device for the first time. It stays until answered, and
/// closes by itself when the receiver gives up (timeout, or the sender hung up): then the surrounding
/// task is cancelled and `run` returns false.
final class FirstContactPrompt: NSObject, NSWindowDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Bool, Never>?
    private var finished = false
    private var window: NSWindow?

    func run(device: String, what: String) async -> Bool {
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                lock.lock()
                if finished { lock.unlock(); c.resume(returning: false); return }
                continuation = c
                lock.unlock()
                DispatchQueue.main.async { self.show(device: device, what: what) }
            }
        } onCancel: {
            self.finish(false)
        }
    }

    private func finish(_ accepted: Bool) {
        lock.lock()
        finished = true
        let c = continuation
        continuation = nil
        lock.unlock()
        c?.resume(returning: accepted)
        DispatchQueue.main.async {
            self.window?.delegate = nil
            self.window?.close()
            self.window = nil
        }
    }

    @MainActor
    private func show(device: String, what: String) {
        lock.lock()
        let already = finished
        lock.unlock()
        if already { return }

        let view = FirstContactView(device: device, what: what, accept: { self.finish(true) }, decline: { self.finish(false) })
        let w = NSWindow(contentViewController: NSHostingController(rootView: view))
        w.title = device
        w.styleMask = [.titled, .closable]
        w.isReleasedWhenClosed = false
        w.level = .floating
        w.delegate = self
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    // The close button is Decline.
    func windowWillClose(_ notification: Notification) { finish(false) }
}

struct FirstContactView: View {
    let device: String
    let what: String
    let accept: () -> Void
    let decline: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "tray.and.arrow.down")
                    .font(.system(size: 30, weight: .regular))
                    .foregroundStyle(.secondary)
                    .frame(width: 36)
                VStack(alignment: .leading, spacing: 4) {
                    Text(device).font(.headline)
                    Text("wants to send \(what)")
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack {
                Spacer()
                Button("Decline", action: decline)
                Button("Accept", action: accept)
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 360)
    }
}
