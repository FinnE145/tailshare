import XCTest
@testable import TailshareCore

final class NodeTests: XCTestCase {
    private var dirs: [URL] = []
    private var nodes: [Node] = []

    override func tearDown() async throws {
        for n in nodes { await n.stop() }
        nodes = []
        dirs.forEach { try? FileManager.default.removeItem(at: $0) }
        dirs = []
    }

    private func make(settingsJSON: String? = nil) throws -> (Node, StubTailscale, SettingsStore, TestHost, StateLog) {
        let dir = try makeTempDir()
        dirs.append(dir)
        let settingsURL = dir.appendingPathComponent("settings.json")
        if let settingsJSON { try settingsJSON.write(to: settingsURL, atomically: true, encoding: .utf8) }
        let store = SettingsStore(fileURL: settingsURL)
        let ts = StubTailscale()
        let host = TestHost()
        let log = StateLog()
        let node = Node(options: ReceiverOptions(settings: store, tailscale: ts, host: host, defaultReceiveFolder: dir), onChange: { log.add($0) })
        nodes.append(node)
        return (node, ts, store, host, log)
    }

    private func hello(port: Int) async -> Bool {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/v1/hello")!)
        request.timeoutInterval = 1
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        return (try? await session.data(for: request)).map { ($0.1 as? HTTPURLResponse)?.statusCode == 200 } ?? false
    }

    // spec B Tests/Port: a valid port from the settings file is used for listening, and a sender made by the node connects to it.
    func testConfiguredPortIsUsedToListenAndToConnect() async throws {
        let port = freePort()
        let (node, ts, _, host, log) = try make(settingsJSON: #"{"port": \#(port), "trusted":[{"id":"nodeSENDER1","name":"x"}]}"#)
        await node.refresh()
        XCTAssertEqual(log.all, [.listening(ip: "127.0.0.1", port: port)])
        let listening = await hello(port: port)
        XCTAssertTrue(listening)

        let peer = PeerInfo(identity: otherIdentity, ip: "127.0.0.1", online: true)
        let outcome = await node.makeSender().send(to: peer, content: .text("hi"))
        XCTAssertEqual(outcome, .sent)
        XCTAssertEqual(host.received.first?.2.text, "hi")

        let found = await node.makeSender().discover(TailnetStatus(connected: true, selfIP: "100.64.0.1", peers: [peer]))
        XCTAssertEqual(found, [peer])
        _ = ts
    }

    // spec B Tests/Port: a changed port is picked up on the next re-check, and the old one is let go.
    func testChangedPortIsPickedUpOnTheNextRefresh() async throws {
        let first = freePort()
        let (node, _, store, _, log) = try make(settingsJSON: #"{"port": \#(first)}"#)
        await node.refresh()
        var listening = await hello(port: first)
        XCTAssertTrue(listening)

        var second = freePort()
        while second == first { second = freePort() }
        try #"{"port": \#(second)}"#.write(to: store.fileURL, atomically: true, encoding: .utf8)

        // Not before the re-check.
        listening = await hello(port: second)
        XCTAssertFalse(listening)

        await node.refresh()
        listening = await hello(port: second)
        XCTAssertTrue(listening)
        listening = await hello(port: first)
        XCTAssertFalse(listening, "the old port is still being listened on")
        XCTAssertEqual(log.all, [.listening(ip: "127.0.0.1", port: first), .listening(ip: "127.0.0.1", port: second)])
    }

    // spec B Tests/Port: an out-of-range configured port falls back to 45611 (here: not usable, so only the state is checked).
    func testBadPortFallsBackToTheDefaultPort() async throws {
        XCTAssertEqual(Settings(port: 80).effectivePort, 45611)
        let (node, _, _, _, _) = try make(settingsJSON: #"{"port": 80}"#)
        // The default port may genuinely be taken on a machine that runs the app; either way it is never 80.
        await node.refresh()
        let state = await node.state
        XCTAssertTrue([NodeState.listening(ip: "127.0.0.1", port: 45611), .portInUse(45611)].contains(state), "\(state)")
    }

    // spec B Tailscale integration: down or logged out means not listening and "Tailscale not connected"; coming back rebinds.
    func testTailscaleDownStopsListeningAndRecovers() async throws {
        let port = freePort()
        let (node, ts, _, _, log) = try make(settingsJSON: #"{"port": \#(port)}"#)
        await node.refresh()
        var up = await hello(port: port)
        XCTAssertTrue(up)

        ts.statusValue = .disconnected
        await node.refresh()
        up = await hello(port: port)
        XCTAssertFalse(up)

        ts.statusValue = TailnetStatus(connected: true, selfIP: "127.0.0.1", peers: [])
        await node.refresh()
        up = await hello(port: port)
        XCTAssertTrue(up)
        XCTAssertEqual(log.all, [.listening(ip: "127.0.0.1", port: port), .tailscaleDown, .listening(ip: "127.0.0.1", port: port)])
    }

    // spec B Tailscale integration: refreshes that overlap (startup, the timer and a popup opening) bind once and report nothing but listening.
    func testOverlappingRefreshesBindOnce() async throws {
        let port = freePort()
        let (node, _, _, _, log) = try make(settingsJSON: #"{"port": \#(port)}"#)
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<8 { group.addTask { await node.refresh() } }
        }
        XCTAssertEqual(log.all, [.listening(ip: "127.0.0.1", port: port)])
        let up = await hello(port: port)
        XCTAssertTrue(up)
    }

    // spec B Receiving: a port already in use is reported, and receiving starts by itself once the port is free.
    func testPortInUseIsReportedThenRetried() async throws {
        let port = freePort()
        let blocker = Receiver(options: ReceiverOptions(settings: SettingsStore(fileURL: URL(fileURLWithPath: "/nonexistent/s.json")), tailscale: StubTailscale(), host: TestHost(), defaultReceiveFolder: FileManager.default.temporaryDirectory))
        try await blocker.start(ip: "127.0.0.1", port: port)

        let (node, _, _, _, log) = try make(settingsJSON: #"{"port": \#(port)}"#)
        await node.refresh()
        await node.refresh()
        XCTAssertEqual(log.all, [.portInUse(port)])

        await blocker.stop()
        await node.refresh()
        XCTAssertEqual(log.all, [.portInUse(port), .listening(ip: "127.0.0.1", port: port)])
        let up = await hello(port: port)
        XCTAssertTrue(up)
    }

    // spec B Binding: the receiver binds only to the address it is given, never all interfaces.
    func testBindsOnlyToTheGivenAddress() async throws {
        let port = freePort()
        let (node, _, _, _, _) = try make(settingsJSON: #"{"port": \#(port)}"#)
        await node.refresh()
        let out = Process()
        out.executableURL = URL(fileURLWithPath: "/usr/sbin/lsof")
        out.arguments = ["-nP", "-iTCP:\(port)", "-sTCP:LISTEN", "-Fn"]
        let pipe = Pipe()
        out.standardOutput = pipe
        try out.run()
        let text = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        out.waitUntilExit()
        let names = text.split(separator: "\n").filter { $0.hasPrefix("n") }.map(String.init)
        XCTAssertEqual(Set(names), ["n127.0.0.1:\(port)"])
    }
}

final class StateLog: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [NodeState] = []
    func add(_ s: NodeState) { lock.withLock { items.append(s) } }
    var all: [NodeState] { lock.withLock { items } }
}
