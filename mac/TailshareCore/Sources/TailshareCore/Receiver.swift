import Foundation
import Hummingbird

/// What the host (the app, or a test) does for the receiver. Core never shows UI itself.
public protocol ReceiverHost: AnyObject, Sendable {
    /// Asks the user whether to accept a first-time sender. The surrounding task is cancelled when the
    /// request times out or the sender hangs up; the host should then withdraw its prompt.
    func askFirstContact(from: Identity, offer: Offer) async -> Bool

    /// A file or bundle upload has started. Always followed by `IncomingTransfer.end()`.
    func beginIncoming(from: Identity, offer: Offer) -> IncomingTransfer

    /// A transfer finished and its content is in place (or, for text, in `item`).
    func received(from: Identity, offer: Offer, item: ReceivedItem)
}

public protocol IncomingTransfer: AnyObject, Sendable {
    /// True once the user pressed Cancel on the receiving side.
    var isCancelled: Bool { get }
    func progress(bytesReceived: Int64)
    func end()
}

/// `urls` are the top-level items written (empty for text); `text` is the text of a text transfer.
public struct ReceivedItem: Sendable {
    public var urls: [URL]
    public var text: String?
}

public struct ReceiverOptions: Sendable {
    public var settings: SettingsStore
    public var tailscale: Tailscale
    public var host: ReceiverHost
    /// Used when the settings file names no receive folder, or one that doesn't exist. The host decides what that is.
    public var defaultReceiveFolder: URL
    public var promptTimeout: TimeInterval
    public var tokenLifetime: TimeInterval
    /// Items held for phones and iPads to pick up. Shared across the receiver's rebinds: a hold outlives a changed address.
    public var holds: PickupHolds

    public init(settings: SettingsStore, tailscale: Tailscale, host: ReceiverHost, defaultReceiveFolder: URL,
                promptTimeout: TimeInterval = Wire.promptTimeout, tokenLifetime: TimeInterval = Wire.tokenLifetime,
                holds: PickupHolds = PickupHolds()) {
        self.holds = holds
        self.settings = settings
        self.tailscale = tailscale
        self.host = host
        self.defaultReceiveFolder = defaultReceiveFolder
        self.promptTimeout = promptTimeout
        self.tokenLifetime = tokenLifetime
    }
}

public struct PortInUseError: Error {}

struct TailshareContext: RequestContext {
    var coreContext: CoreRequestContextStorage
    /// The peer's address as the socket reports it; identity is never taken from anything but `whois` on this.
    let remoteIP: String?

    init(source: ApplicationRequestContextSource) {
        coreContext = .init(source: source)
        remoteIP = source.channel.remoteAddress?.ipAddress
    }
}

/// A one-shot signal: `wait()` returns once `fire()` has been called, whichever came first.
private final class Signal: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func fire() {
        lock.lock()
        fired = true
        let w = waiters
        waiters = []
        lock.unlock()
        w.forEach { $0.resume() }
    }

    func wait() async {
        await withCheckedContinuation { c in
            lock.lock()
            if fired { lock.unlock(); c.resume() } else { waiters.append(c); lock.unlock() }
        }
    }
}

/// Resumes its continuation with the first result offered; later ones are dropped.
final class Race<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T, Never>?
    private var result: T?

    func offer(_ value: T) {
        lock.lock()
        if result != nil { lock.unlock(); return }
        result = value
        let c = continuation
        continuation = nil
        lock.unlock()
        c?.resume(returning: value)
    }

    func value() async -> T {
        await withCheckedContinuation { c in
            lock.lock()
            if let result { lock.unlock(); c.resume(returning: result) } else { continuation = c; lock.unlock() }
        }
    }
}

private final class Tokens: @unchecked Sendable {
    struct Pending { var senderID: String; var offer: Offer; var expires: Date }

    private let lock = NSLock()
    private var map: [String: Pending] = [:]

    func issue(senderID: String, offer: Offer, lifetime: TimeInterval) -> String {
        lock.lock()
        defer { lock.unlock() }
        let now = Date()
        map = map.filter { $0.value.expires >= now }

        var bytes = [UInt8](repeating: 0, count: 16)
        // A failure of the system RNG is not something to carry on through: a guessable token is no token.
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { fatalError("no random bytes") }
        let token = base64URL(Data(bytes))
        map[token] = Pending(senderID: senderID, offer: offer, expires: now.addingTimeInterval(lifetime))
        return token
    }

    /// Consumes the token. A different sender can't burn someone else's token: the owner is checked before removal.
    func redeem(_ token: String, senderID: String) -> Offer? {
        lock.lock()
        defer { lock.unlock() }
        guard let p = map[token], p.senderID == senderID else { return nil }
        map[token] = nil
        return p.expires < Date() ? nil : p.offer
    }
}

func base64URL(_ data: Data) -> String {
    data.base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
}

public final class Receiver: @unchecked Sendable {
    let o: ReceiverOptions
    private let tokens = Tokens()
    private var serverTask: Task<Void, Never>?

    /// The port actually bound (differs from the one asked for only when 0 was asked for).
    public private(set) var port: Int = 0

    public init(options: ReceiverOptions) { o = options }

    /// Starts listening on `ip` only; the app passes this device's Tailscale address, never "any".
    public func start(ip: String, port: Int) async throws {
        let router = Router(context: TailshareContext.self)
        router.get("v1/hello") { [self] _, context in await hello(context) }
        router.post("v1/offer") { [self] request, context in await offer(request, context) }
        router.put("v1/upload/:token") { [self] request, context in await upload(request, context) }
        router.get("v1/pickup") { [self] _, context in await pickupList(context) }
        router.get("v1/pickup/:token") { [self] _, context in await pickupFetch(context) }

        // Resolves once with the bound port, or with the reason the server never came up.
        let started = Race<Result<Int, Error>>()
        let app = Application(
            router: router,
            configuration: .init(address: .hostname(ip, port: port), serverName: nil),
            onServerRunning: { channel in started.offer(.success(channel.localAddress?.port ?? port)) }
        )

        serverTask = Task {
            do { try await app.run() } catch { started.offer(.failure(error)) }
            // A server that stops without error before it was running still has to answer start().
            started.offer(.failure(CancellationError()))
        }

        switch await started.value() {
        case .success(let bound):
            self.port = bound
        case .failure(let error):
            serverTask = nil
            let text = "\(error)".lowercased()
            if text.contains("eaddrinuse") || text.contains("address already in use") { throw PortInUseError() }
            throw error
        }
    }

    /// Stops listening and waits until the port is free again.
    public func stop() async {
        serverTask?.cancel()
        await serverTask?.value
        serverTask = nil
    }

    // MARK: Requests

    // Every request starts here: who is on the other end, according to Tailscale.
    func who(_ context: TailshareContext) async -> Identity? {
        guard let ip = context.remoteIP else { return nil }
        return await o.tailscale.whois(ip: ip)
    }

    static func status(_ code: Int) -> Response { Response(status: .init(code: code)) }

    static func json<T: Encodable>(_ value: T, status: Int = 200) -> Response {
        let data = (try? JSONEncoder().encode(value)) ?? Data()
        var headers = HTTPFields()
        headers[.contentType] = "application/json"
        return Response(status: .init(code: status), headers: headers, body: .init(byteBuffer: ByteBuffer(data: data)))
    }

    static func error(_ status: Int, _ error: String) -> Response { json(ErrorResponse(error: error), status: status) }

    private func hello(_ context: TailshareContext) async -> Response {
        if await who(context) == nil { return Self.status(403) }
        return Self.json(HelloResponse(app: Wire.appName, protocol: Wire.version, hub: nil))
    }

    private func offer(_ request: Request, _ context: TailshareContext) async -> Response {
        guard let sender = await who(context) else { return Self.status(403) }

        let hungUp = Signal()
        do {
            return try await request.body.consumeWithInboundCloseHandler({ body in
                // An offer is a few hundred bytes; don't buffer anything bigger.
                var data = Data()
                for try await chunk in body {
                    data.append(contentsOf: chunk.readableBytesView)
                    if data.count > 64 * 1024 { return Self.status(400) }
                }
                return await self.decide(sender: sender, offerBody: data, hungUp: hungUp)
            }, onInboundClosed: { hungUp.fire() })
        } catch {
            return Self.status(400)
        }
    }

    private func decide(sender: Identity, offerBody: Data, hungUp: Signal) async -> Response {
        guard let offer = try? JSONDecoder().decode(Offer.self, from: offerBody), Wire.isValid(offer) else { return Self.status(400) }

        // Read fresh: the file is hand-edited and edits apply to the next offer.
        let settings = o.settings.load().value
        if settings.isBlocked(sender.id) { return Self.error(403, "blocked") }
        if offer.kind == .text, let bytes = offer.bytes, bytes > Wire.textLimitBytes { return Self.status(413) }

        if !settings.isTrusted(sender.id) {
            enum Answer: Sendable { case accepted, declined, timeout }
            let race = Race<Answer>()
            let host = o.host
            let timeout = o.promptTimeout

            let ask = Task { race.offer(await host.askFirstContact(from: sender, offer: offer) ? .accepted : .declined) }
            let clock = Task { try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000)); race.offer(.timeout) }
            let hangUp = Task { await hungUp.wait(); race.offer(.timeout) }

            let answer = await race.value()
            // Not awaited: a host that ignores cancellation must not keep this request open.
            ask.cancel(); clock.cancel(); hangUp.cancel()

            switch answer {
            case .timeout: return Self.error(403, "timeout")
            case .declined: return Self.error(403, "declined")
            case .accepted: break
            }

            // False means the file is malformed: this transfer goes through, the trust isn't kept.
            o.settings.addTrusted(sender)
        }

        return Self.json(TokenResponse(token: tokens.issue(senderID: sender.id, offer: offer, lifetime: o.tokenLifetime)))
    }

    private func upload(_ request: Request, _ context: TailshareContext) async -> Response {
        guard let sender = await who(context) else { return Self.status(403) }
        guard let token = context.parameters.get("token"), let offer = tokens.redeem(token, senderID: sender.id) else { return Self.status(403) }

        // An offer that declared `bytes` requires a Content-Length equal to it; a missing one (chunked) is just as wrong.
        // An offer without `bytes` takes whatever arrives.
        if let declared = offer.bytes {
            guard let length = request.headers[.contentLength].flatMap({ Int64($0) }), length == declared else { return Self.status(400) }
        }

        return offer.kind == .text
            ? await receiveText(request, sender, offer)
            : await receiveFile(request, sender, offer)
    }

    private func receiveText(_ request: Request, _ sender: Identity, _ offer: Offer) async -> Response {
        var data = Data()
        do {
            for try await chunk in request.body {
                data.append(contentsOf: chunk.readableBytesView)
                // Without a declared length nothing else bounds the body; with one it already equals what the offer was checked for.
                if Int64(data.count) > Wire.textLimitBytes { return Self.status(413) }
            }
        } catch {
            return Self.status(400)
        }
        guard offer.bytes.map({ Int64(data.count) == $0 }) ?? true, let text = String(data: data, encoding: .utf8) else { return Self.status(400) }

        o.host.received(from: sender, offer: offer, item: ReceivedItem(urls: [], text: text))
        return Self.json(["ok": true])
    }

    private func receiveFile(_ request: Request, _ sender: Identity, _ offer: Offer) async -> Response {
        let folder = receiveFolder()
        var part = Data(count: 8)
        _ = part.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 8, $0.baseAddress!) }
        let partURL = folder.appendingPathComponent(".tailshare-\(base64URL(part)).part")
        let incoming = o.host.beginIncoming(from: sender, offer: offer)

        let placed: [URL]
        do {
            defer { incoming.end() }
            guard FileManager.default.createFile(atPath: partURL.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
            let handle = try FileHandle(forWritingTo: partURL)
            do {
                try await copy(request.body, to: handle, expected: offer.bytes, incoming: incoming)
                try handle.close()
            } catch {
                try? handle.close()
                throw error
            }

            if offer.kind == .bundle {
                placed = try Bundles.extract(zipURL: partURL, into: folder, totalSize: offer.extractionCap, isCancelled: { incoming.isCancelled || Task.isCancelled })
                Placement.delete(partURL)
            } else {
                placed = [try Placement.placeFile(in: folder, rawName: offer.name!, from: partURL)]
            }
        } catch {
            Placement.delete(partURL)

            // A receiver-side Cancel and a sender that went away both end the same way; the sender sees a failure.
            if incoming.isCancelled || Task.isCancelled || error is CancellationError { return Self.status(499) }
            return Self.status(error is BundleError ? 400 : 500)
        }

        o.host.received(from: sender, offer: offer, item: ReceivedItem(urls: placed, text: nil))
        return Self.json(["ok": true])
    }

    // The upload handler has already required Content-Length to equal the offer's bytes; the count
    // here is the second check, for a body that ends early without an error.
    private func copy(_ body: RequestBody, to handle: FileHandle, expected: Int64?, incoming: IncomingTransfer) async throws {
        var total: Int64 = 0
        var lastReport = Date.distantPast

        for try await chunk in body {
            if incoming.isCancelled { throw CancellationError() }
            total += Int64(chunk.readableBytes)
            if let expected, total > expected { throw BundleError(reason: "body longer than offered") }
            try handle.write(contentsOf: Data(chunk.readableBytesView))

            let now = Date()
            if now.timeIntervalSince(lastReport) > 0.25 {
                lastReport = now
                incoming.progress(bytesReceived: total)
            }
        }
        if incoming.isCancelled { throw CancellationError() }
        if let expected, total != expected { throw BundleError(reason: "body shorter than offered") }
        incoming.progress(bytesReceived: total)
    }

    private func receiveFolder() -> URL {
        if let chosen = o.settings.load().value.receiveFolder, !chosen.trimmingCharacters(in: .whitespaces).isEmpty {
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: chosen, isDirectory: &isDir), isDir.boolValue { return URL(fileURLWithPath: chosen, isDirectory: true) }
        }
        return o.defaultReceiveFolder
    }
}
