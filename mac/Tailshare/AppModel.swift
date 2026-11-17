import AppKit
import SwiftUI
import TailshareCore

struct ReceiveItem: Identifiable {
    let id: UUID
    let text: String
    var bytes: Int64
    let total: Int64
    let transfer: IncomingState

    /// Only a big transfer gets a progress line; a small one is over before it could be read.
    var showsProgress: Bool { total > Notices.progressThresholdBytes }
}

@MainActor
final class AppModel: ObservableObject {
    enum DeviceState { case loading, list, none, tailscaleDown }
    enum Mode: String { case files = "Files", text = "Text" }

    @Published var devices: [PeerInfo] = []
    @Published var deviceState = DeviceState.loading
    @Published var selectedID: String?
    @Published var mode = Mode.files
    @Published var items: [URL] = []
    @Published var text = ""
    @Published var nodeState = NodeState.starting
    @Published var receives: [ReceiveItem] = []

    /// Set by the popup controller: the file browser hides the popup while it is open and shows it again after.
    var hidePopup: (() -> Void)?
    var showPopup: (() -> Void)?

    let send = SendController()
    private(set) var node: Node!
    private let notifier: Notifier
    private var refreshing = false
    private var lastRefresh = Date.distantPast
    private var sendWindows: [SendWindowController] = []
    private var timer: Timer?

    init(notifier: Notifier) {
        self.notifier = notifier
    }

    var isReceiving: Bool { !receives.isEmpty }

    var selectedDevice: PeerInfo? { devices.first { $0.identity.id == selectedID } }

    var canSend: Bool {
        guard selectedDevice != nil, !send.isBusy else { return false }
        return mode == .files ? !items.isEmpty : !text.isEmpty
    }

    // MARK: Start

    func start() {
        let bridge = ReceiverBridge(model: self, notifier: notifier)
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
        node = Node(
            options: ReceiverOptions(settings: AppPaths.settingsStore, tailscale: CLITailscale(), host: bridge, defaultReceiveFolder: downloads),
            onChange: { [weak self] state in Task { @MainActor in self?.nodeChanged(state) } }
        )

        Task { await refreshDevices() }
        // Every 10 seconds: noticing Tailscale going down or coming back, a changed address, an edited port.
        timer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.tick() }
        }
    }

    // Tailscale coming back, or going away, shows in the device list without the popup being reopened.
    private func tick() async {
        let status = await node.refresh()
        if status.connected == (deviceState == .tailscaleDown) { await refreshDevices(force: true) }
    }

    private func nodeChanged(_ state: NodeState) {
        nodeState = state
        if case .portInUse(let port) = state { notifier.post(Notices.portInUse(port)) }
    }

    // MARK: Devices

    /// Refreshes the device list. Called whenever a popup or send window opens, and by Refresh.
    func refreshDevices(force: Bool = false) async {
        guard node != nil, !refreshing else { return }
        // Opening a window raises several "became key" events in a row; one refresh is enough.
        if !force && Date().timeIntervalSince(lastRefresh) < 1 { return }
        refreshing = true
        defer { refreshing = false; lastRefresh = Date() }

        let status = await node.refresh()
        guard status.connected else {
            devices = []
            deviceState = .tailscaleDown
            return
        }
        let found = await node.makeSender().discover(status)
        devices = found
        deviceState = found.isEmpty ? .none : .list
        if !found.contains(where: { $0.identity.id == selectedID }) { selectedID = nil }
    }

    // MARK: Popup sending

    func sendFromPopup() {
        guard let peer = selectedDevice else { return }
        let content: SendContent = mode == .files ? .paths(items) : .text(text)
        send.start(to: peer, content: content, sender: node.makeSender()) { [weak self] outcome in
            guard let self, outcome == .sent else { return }
            // What was sent is cleared; anything else stays so a retry is one click.
            self.items = []
            self.text = ""
        }
    }

    func add(_ urls: [URL]) {
        for url in urls where !items.contains(url) { items.append(url) }
        mode = .files
    }

    // MARK: Receiving

    func beginReceive(_ incoming: IncomingState, offer: Offer, device: String) {
        receives.append(ReceiveItem(id: incoming.id, text: Notices.progress(device: device, offer: offer), bytes: 0, total: offer.bytes, transfer: incoming))
    }

    func updateReceive(_ id: UUID, bytes: Int64) {
        if let i = receives.firstIndex(where: { $0.id == id }) { receives[i].bytes = bytes }
    }

    func endReceive(_ id: UUID) { receives.removeAll { $0.id == id } }

    // MARK: Share hand-off

    func openSendWindow(files: [URL]?, text: String?) {
        let controller = SendWindowController(model: self, files: files?.filter { FileManager.default.fileExists(atPath: $0.path) }, text: text, notifier: notifier) { [weak self] closed in
            self?.sendWindows.removeAll { $0 === closed }
        }
        sendWindows.append(controller)
        controller.show()
    }
}
