import AppKit
import SwiftUI
import TailshareCore

/// The window a share-sheet hand-off opens: what was shared, a device to pick, Send. It closes when the
/// send ends and the result is a notification. The app never sends by itself; this is where the user decides.
@MainActor
final class SendWindowController: NSObject, NSWindowDelegate {
    private let model: AppModel
    private let send = SendController()
    private let notifier: Notifier
    private let onClose: (SendWindowController) -> Void
    private let files: [URL]?
    private let text: String?
    private var window: NSWindow?
    private var selection = SelectionBox()

    init(model: AppModel, files: [URL]?, text: String?, notifier: Notifier, onClose: @escaping (SendWindowController) -> Void) {
        self.model = model
        self.files = files
        self.text = text
        self.notifier = notifier
        self.onClose = onClose
    }

    func show() {
        let view = SendWindowView(model: model, send: send, selection: selection, files: files, text: text) { [weak self] peer in
            self?.start(to: peer)
        }
        let w = NSWindow(contentViewController: NSHostingController(rootView: view))
        w.title = "Send with Tailshare"
        w.styleMask = [.titled, .closable]
        w.isReleasedWhenClosed = false
        w.delegate = self
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
        Task { await model.refreshDevices(force: true) }
    }

    private func start(to peer: PeerInfo) {
        let content: SendContent
        if let text { content = .text(text) } else { content = .paths(files ?? []) }
        let name = peer.identity.name
        send.start(to: peer, content: content, sender: model.node.makeSender()) { [weak self] outcome in
            guard let self else { return }
            switch outcome {
            case .sent: self.notifier.post(Notices.sent(to: name))
            case .declined: self.notifier.post(Notices.declined(by: name))
            case .didntRespond: self.notifier.post(Notices.noResponse(from: name))
            case .failed: self.notifier.post(Notices.failed(to: name))
            case .cancelled: break
            }
            self.window?.delegate = nil
            self.window?.close()
            self.window = nil
            self.onClose(self)
        }
    }

    // Closing the window mid-send is Cancel.
    func windowWillClose(_ notification: Notification) {
        send.cancel()
        onClose(self)
    }
}

@MainActor
final class SelectionBox: ObservableObject {
    @Published var id: String?
}

private struct SendWindowView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var send: SendController
    @ObservedObject var selection: SelectionBox
    let files: [URL]?
    let text: String?
    let start: (PeerInfo) -> Void

    private var nothingToSend: Bool { text == nil && (files ?? []).isEmpty }
    private var selected: PeerInfo? { model.devices.first { $0.identity.id == selection.id } }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Sending").font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                if nothingToSend {
                    Label("Nothing to send", systemImage: "tray").foregroundStyle(.secondary)
                } else if let text {
                    ItemRow(name: text.split(whereSeparator: \.isNewline).first.map(String.init) ?? text, symbol: "text.alignleft")
                } else {
                    ScrollView {
                        VStack(spacing: 4) {
                            ForEach(files ?? [], id: \.self) { ItemRow(name: $0.lastPathComponent, symbol: symbol(for: $0)) }
                        }
                    }
                    .frame(maxHeight: 96)
                }
            }

            DeviceListView(model: model, selection: $selection.id)

            SendStatusView(send: send)

            HStack {
                Spacer()
                Button {
                    if let selected { start(selected) }
                } label: {
                    Label(selected.map { "Send to \($0.identity.name)" } ?? "Send", systemImage: "paperplane")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .keyboardShortcut(.defaultAction)
                .disabled(selected == nil || nothingToSend || send.isBusy)
            }
        }
        .padding(20)
        .frame(width: 380)
    }
}
