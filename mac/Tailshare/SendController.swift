import Foundation
import TailshareCore

/// One send in flight (or just finished): the popup has one, every send window has its own.
@MainActor
final class SendController: ObservableObject {
    enum Status: Equatable {
        case idle
        case preparing
        case waiting(device: String)
        case sending(sent: Int64, total: Int64)
        case done(SendOutcome, device: String)
    }

    @Published private(set) var status = Status.idle
    private var task: Task<Void, Never>?

    var isBusy: Bool {
        switch status {
        case .idle, .done: return false
        default: return true
        }
    }

    func start(to peer: PeerInfo, content: SendContent, sender: Sender, onFinish: @escaping @MainActor (SendOutcome) -> Void = { _ in }) {
        guard !isBusy else { return }
        let device = peer.identity.name
        status = .waiting(device: device)

        task = Task { [weak self] in
            let outcome = await sender.send(to: peer, content: content) { progress in
                Task { @MainActor in self?.apply(progress, device: device) }
            }
            guard let self else { return }
            self.status = .done(outcome, device: device)
            self.task = nil
            onFinish(outcome)
        }
    }

    func cancel() { task?.cancel() }

    func clearResult() {
        if case .done = status { status = .idle }
    }

    // A progress report can reach the main actor after the send has finished; it must not undo the result.
    private func apply(_ p: SendProgress, device: String) {
        guard isBusy else { return }
        switch p.stage {
        case .preparing: status = .preparing
        case .waiting: status = .waiting(device: device)
        case .sending: status = .sending(sent: p.bytesSent, total: p.bytesTotal)
        }
    }
}

extension SendController.Status {
    /// The line under the send button: an SF Symbol and a few words, never colour alone.
    var line: (symbol: String, text: String)? {
        switch self {
        case .idle: return nil
        case .preparing: return ("archivebox", "Preparing")
        case .waiting(let device): return ("hourglass", "Waiting for \(device)")
        case .sending(let sent, let total): return ("arrow.up.circle", total > 0 ? "Sending \(Notices.formatSize(sent)) of \(Notices.formatSize(total))" : "Sending")
        case .done(let outcome, let device):
            switch outcome {
            case .sent: return ("checkmark.circle", "Sent")
            case .declined: return ("hand.raised", "\(device) declined")
            case .didntRespond: return ("clock", "\(device) didn't respond")
            case .failed: return ("exclamationmark.triangle", "Failed")
            case .cancelled: return ("xmark.circle", "Cancelled")
            }
        }
    }

    /// 0...1 while sending, nil for an indeterminate bar, and no bar at all when finished or idle.
    var fraction: Double?? {
        switch self {
        case .idle, .done: return .none
        case .preparing, .waiting: return .some(nil)
        case .sending(let sent, let total): return .some(total > 0 ? Double(sent) / Double(total) : nil)
        }
    }
}
