import Foundation
import Network
import XCTest
@testable import TailshareCore

/// Fixtures use placeholders only: no real device names, tailnet names or addresses.
let senderIdentity = Identity(id: "nodeSENDER1", name: "sender-box")
let otherIdentity = Identity(id: "nodeOTHER22", name: "other-box")

final class StubTailscale: Tailscale, @unchecked Sendable {
    private let lock = NSLock()
    private var _whois: [String: Identity] = ["127.0.0.1": senderIdentity]
    private var _status = TailnetStatus(connected: true, selfIP: "127.0.0.1", peers: [])

    var whoisTable: [String: Identity] {
        get { lock.withLock { _whois } }
        set { lock.withLock { _whois = newValue } }
    }
    var statusValue: TailnetStatus {
        get { lock.withLock { _status } }
        set { lock.withLock { _status = newValue } }
    }

    func status() async -> TailnetStatus { statusValue }
    func whois(ip: String) async -> Identity? { whoisTable[ip] }
}

final class TestHost: ReceiverHost, @unchecked Sendable {
    enum Prompt { case accept, decline, wait }

    final class Incoming: IncomingTransfer, @unchecked Sendable {
        let cancelled: Bool
        private let lock = NSLock()
        private var _progress: [Int64] = []
        private var _ended = false
        init(cancelled: Bool) { self.cancelled = cancelled }
        var isCancelled: Bool { cancelled }
        func progress(bytesReceived: Int64) { lock.withLock { _progress.append(bytesReceived) } }
        func end() { lock.withLock { _ended = true } }
        var progressReports: [Int64] { lock.withLock { _progress } }
        var ended: Bool { lock.withLock { _ended } }
    }

    private let lock = NSLock()
    private var _prompt = Prompt.accept
    private var _cancelIncoming = false
    private var _prompts: [(Identity, Offer)] = []
    private var _withdrawn = 0
    private var _received: [(Identity, Offer, ReceivedItem)] = []
    private var _incoming: [Incoming] = []

    var prompt: Prompt {
        get { lock.withLock { _prompt } }
        set { lock.withLock { _prompt = newValue } }
    }
    var cancelIncoming: Bool {
        get { lock.withLock { _cancelIncoming } }
        set { lock.withLock { _cancelIncoming = newValue } }
    }
    var prompts: [(Identity, Offer)] { lock.withLock { _prompts } }
    var withdrawn: Int { lock.withLock { _withdrawn } }
    var received: [(Identity, Offer, ReceivedItem)] { lock.withLock { _received } }
    var incoming: [Incoming] { lock.withLock { _incoming } }

    func askFirstContact(from: Identity, offer: Offer) async -> Bool {
        let mode = lock.withLock { () -> Prompt in _prompts.append((from, offer)); return _prompt }
        switch mode {
        case .accept: return true
        case .decline: return false
        case .wait:
            try? await Task.sleep(nanoseconds: 3600 * 1_000_000_000)
            if Task.isCancelled { lock.withLock { _withdrawn += 1 } }
            return false
        }
    }

    func beginIncoming(from: Identity, offer: Offer) -> IncomingTransfer {
        let t = Incoming(cancelled: cancelIncoming)
        lock.withLock { _incoming.append(t) }
        return t
    }

    func received(from: Identity, offer: Offer, item: ReceivedItem) {
        lock.withLock { _received.append((from, offer, item)) }
    }
}

/// A real receiver on loopback, with its own receive folder and settings file.
final class Harness {
    let root: URL
    let receiveDir: URL
    let defaultDir: URL
    let settingsURL: URL
    let store: SettingsStore
    let tailscale = StubTailscale()
    let host = TestHost()
    let receiver: Receiver
    let peer: PeerInfo

    init(promptTimeout: TimeInterval = 120, tokenLifetime: TimeInterval = 60, settingsJSON: String? = nil) async throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("tailshare-tests-\(UUID().uuidString)")
        receiveDir = root.appendingPathComponent("receive")
        defaultDir = root.appendingPathComponent("default")
        settingsURL = root.appendingPathComponent("settings.json")
        try FileManager.default.createDirectory(at: receiveDir, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: defaultDir, withIntermediateDirectories: true)
        if let settingsJSON { try settingsJSON.write(to: settingsURL, atomically: true, encoding: .utf8) }

        store = SettingsStore(fileURL: settingsURL)
        // Receives into `defaultDir` unless the settings file names a folder, as the app does with Downloads.
        receiver = Receiver(options: ReceiverOptions(settings: store, tailscale: tailscale, host: host, defaultReceiveFolder: receiveDir,
                                                     promptTimeout: promptTimeout, tokenLifetime: tokenLifetime))
        try await receiver.start(ip: "127.0.0.1", port: 0)
        peer = PeerInfo(identity: Identity(id: "nodeRECEIVER", name: "receiver-box"), ip: "127.0.0.1", online: true)
    }

    deinit { try? FileManager.default.removeItem(at: root) }

    func stop() async { await receiver.stop() }

    func sender(offerTimeout: TimeInterval = 130) -> Sender { Sender(port: receiver.port, offerTimeout: offerTimeout) }

    func trustSender() throws {
        try #"{"trusted":[{"id":"\#(senderIdentity.id)","name":"\#(senderIdentity.name)"}]}"#.write(to: settingsURL, atomically: true, encoding: .utf8)
    }

    var receivedNames: [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: receiveDir.path)) ?? []).sorted()
    }

    func url(_ path: String) -> URL { URL(string: "http://127.0.0.1:\(receiver.port)\(path)")! }

    /// A raw request, for the cases the Sender is too well behaved to produce.
    func raw(_ method: String, _ path: String, body: Data? = nil, json: Any? = nil) async throws -> (status: Int, body: Data) {
        var request = URLRequest(url: url(path))
        request.httpMethod = method
        if let json { request.httpBody = try JSONSerialization.data(withJSONObject: json); request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (data, response) = body != nil ? try await session.upload(for: request, from: body!) : try await session.data(for: request)
        return ((response as! HTTPURLResponse).statusCode, data)
    }

    func offerJSON(kind: String = "file", name: String? = "a.txt", items: Int = 1, isFolder: Bool = false, bytes: Int, totalSize: Int? = nil) -> [String: Any] {
        var o: [String: Any] = ["kind": kind, "items": items, "isFolder": isFolder, "bytes": bytes, "totalSize": totalSize ?? bytes]
        if let name { o["name"] = name }
        return o
    }

    func token(from body: Data) -> String? { (try? JSONSerialization.jsonObject(with: body) as? [String: Any])?["token"] as? String }
    func error(from body: Data) -> String? { (try? JSONSerialization.jsonObject(with: body) as? [String: Any])?["error"] as? String }
}

func makeTempDir() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("tailshare-tests-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

func write(_ text: String, to url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data(text.utf8).write(to: url)
}

func waitUntil(timeout: TimeInterval = 5, _ condition: () -> Bool) async -> Bool {
    let end = Date().addingTimeInterval(timeout)
    while Date() < end {
        if condition() { return true }
        try? await Task.sleep(nanoseconds: 20_000_000)
    }
    return condition()
}

/// A port nothing is listening on, found by binding port 0 and letting go.
func freePort() -> Int {
    let fd = socket(AF_INET, SOCK_STREAM, 0)
    defer { close(fd) }
    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_addr.s_addr = inet_addr("127.0.0.1")
    addr.sin_port = 0
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    _ = withUnsafeMutablePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, len) } }
    _ = withUnsafeMutablePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) } }
    return Int(UInt16(bigEndian: addr.sin_port))
}

/// Answers every request with one fixed HTTP response, for a peer that misbehaves.
final class FakeServer {
    private let listener: NWListener
    let port: Int

    init(status: Int, body: String) throws {
        let p = freePort()
        port = p
        listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: UInt16(p))!)
        listener.newConnectionHandler = { conn in
            conn.start(queue: .global())
            conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { _, _, _, _ in
                let text = "HTTP/1.1 \(status) X\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
                conn.send(content: Data(text.utf8), completion: .contentProcessed { _ in conn.cancel() })
            }
        }
        listener.start(queue: .global())
    }

    func stop() { listener.cancel() }
}

/// XCTAssertEqual can't take `await` in its autoclosure.
func eq<T: Equatable>(_ actual: @autoclosure () async throws -> T, _ expected: T, _ message: String = "",
                      file: StaticString = #filePath, line: UInt = #line) async throws {
    let value = try await actual()
    XCTAssertEqual(value, expected, message, file: file, line: line)
}
