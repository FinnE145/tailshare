import AppKit
import SwiftUI
import TailshareCore

/// The receiver's view of one upload; the popup's Cancel flips the flag, Core's copy loop reads it.
final class IncomingState: IncomingTransfer, @unchecked Sendable {
    let id = UUID()
    private let lock = NSLock()
    private var cancelled = false
    private weak var model: AppModel?

    init(model: AppModel?) { self.model = model }

    var isCancelled: Bool { lock.withLock { cancelled } }
    func cancel() { lock.withLock { cancelled = true } }

    func progress(bytesReceived: Int64) {
        let id = id
        Task { @MainActor [weak model] in model?.updateReceive(id, bytes: bytesReceived) }
    }

    func end() {
        let id = id
        Task { @MainActor [weak model] in model?.endReceive(id) }
    }
}

/// Everything the receiver needs from the app: windows, notifications, the receiving state of the popup.
final class ReceiverBridge: ReceiverHost, @unchecked Sendable {
    private weak var model: AppModel?
    private let notifier: Notifier

    init(model: AppModel, notifier: Notifier) {
        self.model = model
        self.notifier = notifier
    }

    func askFirstContact(from: Identity, offer: Offer) async -> Bool {
        await FirstContactPrompt().run(device: from.name, what: Notices.describe(offer))
    }

    func beginIncoming(from: Identity, offer: Offer) -> IncomingTransfer {
        let incoming = IncomingState(model: model)
        let device = from.name
        Task { @MainActor [weak model] in model?.beginReceive(incoming, offer: offer, device: device) }
        return incoming
    }

    func received(from: Identity, offer: Offer, item: ReceivedItem) {
        if let text = item.text {
            notifier.postReceivedText(device: from.name, text: text)
        } else {
            notifier.postReceivedFiles(Notices.received(device: from.name, offer: offer), urls: item.urls)
        }
    }
}
