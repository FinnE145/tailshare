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
        /// Held for a phone or iPad (spec E): waiting for it to fetch, then being fetched.
        case waitingPickup(device: String)
        case pickingUp(sent: Int64, total: Int64)
        case done(SendOutcome, device: String)
    }

    @Published private(set) var status = Status.idle
    private var task: Task<Void, Never>?
    /// Which send the status belongs to: a hold that ends after a newer send started must not overwrite that send's status.
    private var generation = 0

    /// A send to a computer is under way: another can't start. A hold is not this; it waits on its own.
    var isBusy: Bool {
        switch status {
        case .preparing, .waiting, .sending: return true
        default: return false
        }
    }

    var isHolding: Bool {
        switch status {
        case .waitingPickup, .pickingUp: return true
        default: return false
        }
    }

    /// Whether the status line shows Cancel.
    var canCancel: Bool { isBusy || isHolding }

    func start(to peer: PeerInfo, content: SendContent, sender: Sender, holds: PickupHolds, onFinish: @escaping @MainActor (SendOutcome) -> Void = { _ in }) {
        guard !isBusy else { return }
        generation += 1
        let mine = generation
        let device = peer.identity.name
        let mobile = peer.isMobile
        status = mobile ? .waitingPickup(device: device) : .waiting(device: device)

        // Cancelling this one must not leave an older hold's task as "the" task: only the displayed send is cancellable.
        task = Task { [weak self] in
            let report: @Sendable (SendProgress) -> Void = { progress in
                Task { @MainActor in self?.apply(progress, device: device, mobile: mobile, generation: mine) }
            }
            let outcome = mobile
                ? await holds.hold(content, for: peer, progress: report)
                : await sender.send(to: peer, content: content, progress: report)
            guard let self else { return }
            if self.generation == mine {
                self.status = .done(outcome, device: device)
                self.task = nil
            }
            onFinish(outcome)
        }
    }

    func cancel() { task?.cancel() }

    func clearResult() {
        if case .done = status { status = .idle }
    }

    // A progress report can reach the main actor after the send has finished; it must not undo the result.
    private func apply(_ p: SendProgress, device: String, mobile: Bool, generation: Int) {
        guard generation == self.generation, canCancel else { return }
        switch p.stage {
        case .preparing: status = .preparing
        case .waiting: status = mobile ? .waitingPickup(device: device) : .waiting(device: device)
        case .sending: status = mobile ? .pickingUp(sent: p.bytesSent, total: p.bytesTotal) : .sending(sent: p.bytesSent, total: p.bytesTotal)
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
        case .waitingPickup(let device): return ("hourglass", Notices.waitingForPickup(device: device))
        case .pickingUp(let sent, let total): return ("arrow.down.circle", total > 0 ? "Picking up \(Notices.formatSize(sent)) of \(Notices.formatSize(total))" : "Picking up")
        case .sending(let sent, let total): return ("arrow.up.circle", total > 0 ? "Sending \(Notices.formatSize(sent)) of \(Notices.formatSize(total))" : "Sending")
        case .done(let outcome, let device):
            switch outcome {
            case .sent: return ("checkmark.circle", "Sent")
            case .declined: return ("hand.raised", "\(device) declined")
            case .didntRespond: return ("clock", "\(device) didn't respond")
            case .failed: return ("exclamationmark.triangle", "Failed")
            case .cancelled: return ("xmark.circle", "Cancelled")
            case .pickedUp: return ("checkmark.circle", "Picked up by \(device)")
            case .notPickedUp: return ("clock", "Not picked up")
            }
        }
    }

    /// 0...1 while sending, nil for an indeterminate bar, and no bar at all when finished or idle.
    var fraction: Double?? {
        switch self {
        case .idle, .done: return .none
        case .preparing, .waiting, .waitingPickup: return .some(nil)
        case .sending(let sent, let total), .pickingUp(let sent, let total): return .some(total > 0 ? Double(sent) / Double(total) : nil)
        }
    }
}
