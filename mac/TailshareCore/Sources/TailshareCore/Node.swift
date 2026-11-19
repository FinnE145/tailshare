import Foundation

public enum NodeState: Equatable, Sendable {
    case starting
    case listening(ip: String, port: Int)
    case tailscaleDown
    case portInUse(Int)
    case failed
}

/// Keeps the receiver bound to this device's Tailscale address and the configured port. The host calls
/// `refresh()` every 10 seconds (and when it opens its popup): that is the whole of "Tailscale down or
/// logged out", "the address changed" and "`port` was edited".
public actor Node {
    private let tailscale: Tailscale
    private let settings: SettingsStore
    /// Items held for phones and iPads; shared with every receiver this node binds.
    public nonisolated let holds: PickupHolds
    private let options: ReceiverOptions
    private let onChange: @Sendable (NodeState) -> Void

    private var receiver: Receiver?
    private var bound: (ip: String, port: Int)?

    public private(set) var state: NodeState = .starting

    public init(options: ReceiverOptions, onChange: @escaping @Sendable (NodeState) -> Void = { _ in }) {
        self.options = options
        tailscale = options.tailscale
        settings = options.settings
        holds = options.holds
        self.onChange = onChange
    }

    /// A sender that connects to peers on this device's configured port: every device must use the same one.
    public nonisolated func makeSender() -> Sender { Sender(port: settings.load().value.effectivePort) }

    private var running: Task<TailnetStatus, Never>?

    /// Overlapping calls (startup, the timer and a popup opening land together) share one run: an actor
    /// is re-entered at every await, and two runs binding at once would see each other's port as in use.
    @discardableResult
    public func refresh() async -> TailnetStatus {
        if let running { return await running.value }
        let task = Task { await self.perform() }
        running = task
        let status = await task.value
        running = nil
        return status
    }

    private func perform() async -> TailnetStatus {
        let status = await tailscale.status()
        guard status.connected, let ip = status.selfIP else {
            await unbind()
            set(.tailscaleDown)
            return status
        }

        let port = settings.load().value.effectivePort
        // Already listening where it should be. A port that was in use is retried every time instead,
        // so receiving starts by itself once whatever held it lets go.
        if let bound, bound.ip == ip, bound.port == port { return status }

        await unbind()
        let candidate = Receiver(options: options)
        do {
            try await candidate.start(ip: ip, port: port)
            receiver = candidate
            bound = (ip, candidate.port)
            set(.listening(ip: ip, port: candidate.port))
        } catch is PortInUseError {
            set(.portInUse(port))
        } catch {
            set(.failed)
        }
        return status
    }

    public func stop() async {
        await unbind()
        set(.starting)
    }

    private func unbind() async {
        await receiver?.stop()
        receiver = nil
        bound = nil
    }

    private func set(_ new: NodeState) {
        if new == state { return }
        state = new
        onChange(new)
    }
}
