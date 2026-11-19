import Foundation
import Hummingbird

/// Items a computer holds for a phone or iPad to pick up (spec E). Lives in the app's receiver process:
/// nothing is held across a restart, and every hold ends by pickup, Cancel, the timeout or the app quitting.
public final class PickupHolds: @unchecked Sendable {
    private enum Source { case text(Data), file(URL), bundle(URL) }

    private final class Hold {
        let token: String
        let targetID: String
        let kind: OfferKind
        let name: String?
        let bytes: Int64
        let source: Source
        let created = Date()
        let result = Race<SendOutcome>()
        var consumed = false
        var ended = false
        var expiry: Task<Void, Never>?
        var progress: (@Sendable (SendProgress) -> Void)?

        init(token: String, targetID: String, kind: OfferKind, name: String?, bytes: Int64, source: Source) {
            self.token = token
            self.targetID = targetID
            self.kind = kind
            self.name = name
            self.bytes = bytes
            self.source = source
        }
    }

    private let lock = NSLock()
    private var holds: [String: Hold] = [:]
    /// Starts at zero and counts up, so "oldest first" doesn't depend on the clock's resolution.
    private var order = 0
    private var sequence: [String: Int] = [:]

    public init() {}

    /// Holds `content` for `target` and returns once the hold ends: `.pickedUp`, `.notPickedUp` (it ran out),
    /// `.cancelled` (the calling task was cancelled) or `.failed`. The 120 seconds count from when the item is listed,
    /// not from the zipping of a bundle before it.
    public func hold(_ content: SendContent, for target: PeerInfo, lifetime: TimeInterval = Wire.holdLifetime,
                     progress: (@Sendable (SendProgress) -> Void)? = nil) async -> SendOutcome {
        var zipURL: URL?
        let hold: Hold
        do {
            let (kind, name, bytes, source): (OfferKind, String?, Int64, Source)
            switch content {
            case .text(let value):
                let data = Data(value.utf8)
                (kind, name, bytes, source) = (.text, nil, Int64(data.count), .text(data))
            case .paths(let paths):
                var isDir: ObjCBool = false
                if paths.count == 1, FileManager.default.fileExists(atPath: paths[0].path, isDirectory: &isDir), !isDir.boolValue {
                    let size = (try FileManager.default.attributesOfItem(atPath: paths[0].path)[.size] as? NSNumber)?.int64Value ?? 0
                    (kind, name, bytes, source) = (.file, paths[0].lastPathComponent, size, .file(paths[0]))
                } else {
                    progress?(SendProgress(stage: .preparing, bytesSent: 0, bytesTotal: 0))
                    let zip = FileManager.default.temporaryDirectory.appendingPathComponent("tailshare-\(UUID().uuidString).zip")
                    zipURL = zip
                    let info = try Bundles.create(paths: paths, zipURL: zip, isCancelled: { Task.isCancelled })
                    let size = (try FileManager.default.attributesOfItem(atPath: zip.path)[.size] as? NSNumber)?.int64Value ?? 0
                    (kind, name, bytes, source) = (.bundle, info.name, size, .bundle(zip))
                }
            }
            hold = Hold(token: Self.newToken(), targetID: target.identity.id, kind: kind, name: name, bytes: bytes, source: source)
        } catch {
            if let zipURL { try? FileManager.default.removeItem(at: zipURL) }
            return error is CancellationError || Task.isCancelled ? .cancelled : .failed
        }

        hold.progress = progress
        if Task.isCancelled { discard(hold, with: .cancelled); return .cancelled }

        lock.withLock {
            holds[hold.token] = hold
            order += 1
            sequence[hold.token] = order
        }
        hold.expiry = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(lifetime * 1_000_000_000))
            // A download that started before the time ran out is allowed to finish.
            self?.end(hold, with: .notPickedUp, onlyIfUntouched: true)
        }
        progress?(SendProgress(stage: .waiting, bytesSent: 0, bytesTotal: hold.bytes))

        return await withTaskCancellationHandler {
            await hold.result.value()
        } onCancel: {
            self.end(hold, with: .cancelled)
        }
    }

    // MARK: Served to the caller

    public struct Listing: Codable, Equatable, Sendable {
        public var token: String
        public var kind: OfferKind
        public var name: String?
        public var bytes: Int64
    }

    /// What is held for `id`, oldest first. Never anything held for someone else.
    func listing(for id: String) -> [Listing] {
        lock.withLock {
            holds.values
                .filter { $0.targetID == id && !$0.consumed && !$0.ended }
                .sorted { (sequence[$0.token] ?? 0) < (sequence[$1.token] ?? 0) }
                .map { Listing(token: $0.token, kind: $0.kind, name: $0.name, bytes: $0.bytes) }
        }
    }

    struct Download {
        let name: String?
        let kind: OfferKind
        let length: Int64
        /// Writes the body. Ends the hold: picked up when it completes, failed when it doesn't.
        let stream: @Sendable (inout any ResponseBodyWriter) async throws -> Void
    }

    enum Taken { case download(Download), denied, unreadable }

    /// Consumes the token for `id`. `denied`, with nothing consumed, when the token is unknown, expired, used, or someone else's:
    /// another device can't burn a token it doesn't own. A held file that is gone by now ends the hold as failed.
    func take(token: String, for id: String) -> Taken {
        let hold: Hold? = lock.withLock {
            guard let h = holds[token], h.targetID == id, !h.consumed, !h.ended else { return nil }
            h.consumed = true
            return h
        }
        guard let hold else { return .denied }

        switch hold.source {
        case .text(let data):
            return .download(Download(name: nil, kind: .text, length: Int64(data.count)) { writer in
                try await writer.write(ByteBuffer(data: data))
                try await writer.finish(nil)
                self.end(hold, with: .pickedUp)
            })

        case .file(let url), .bundle(let url):
            // Opened now, so a file deleted since it was chosen fails here, and one deleted mid-download keeps being readable.
            guard let handle = try? FileHandle(forReadingFrom: url), let size = try? handle.seekToEnd() else {
                end(hold, with: .failed)
                return .unreadable
            }
            try? handle.seek(toOffset: 0)
            let length = Int64(size)
            let wireName = hold.kind == .bundle ? (hold.name ?? "bundle") + ".zip" : hold.name
            return .download(Download(name: wireName, kind: hold.kind, length: length) { writer in
                defer { try? handle.close() }
                var sent: Int64 = 0
                var lastReport = Date.distantPast
                do {
                    while let chunk = try handle.read(upToCount: 64 * 1024), !chunk.isEmpty {
                        if self.isCancelled(hold) { throw CancellationError() }
                        try await writer.write(ByteBuffer(data: chunk))
                        sent += Int64(chunk.count)
                        let now = Date()
                        if now.timeIntervalSince(lastReport) > 0.25 {
                            lastReport = now
                            hold.progress?(SendProgress(stage: .sending, bytesSent: sent, bytesTotal: length))
                        }
                    }
                    if sent != length { throw BundleError(reason: "file changed while it was read") }
                    try await writer.finish(nil)
                } catch {
                    self.end(hold, with: self.isCancelled(hold) ? .cancelled : .failed)
                    throw error
                }
                hold.progress?(SendProgress(stage: .sending, bytesSent: sent, bytesTotal: length))
                self.end(hold, with: .pickedUp)
            })
        }
    }

    // MARK: Ending

    private func isCancelled(_ hold: Hold) -> Bool { lock.withLock { hold.ended } }

    /// Ends the hold once, whichever cause comes first: later ones find it already gone.
    private func end(_ hold: Hold, with outcome: SendOutcome, onlyIfUntouched: Bool = false) {
        let first: Bool = lock.withLock {
            if hold.ended || (onlyIfUntouched && hold.consumed) { return false }
            hold.ended = true
            holds[hold.token] = nil
            sequence[hold.token] = nil
            return true
        }
        guard first else { return }
        hold.expiry?.cancel()
        // The temp zip goes however the hold ended.
        if case .bundle(let zip) = hold.source { try? FileManager.default.removeItem(at: zip) }
        hold.result.offer(outcome)
    }

    private func discard(_ hold: Hold, with outcome: SendOutcome) {
        if case .bundle(let zip) = hold.source { try? FileManager.default.removeItem(at: zip) }
    }

    private static func newToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        // A failure of the system RNG is not something to carry on through: a guessable token is no token.
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { fatalError("no random bytes") }
        return base64URL(Data(bytes))
    }
}

extension Receiver {
    func pickupList(_ context: TailshareContext) async -> Response {
        guard let caller = await who(context) else { return Self.status(403) }
        return Self.json(o.holds.listing(for: caller.id))
    }

    func pickupFetch(_ context: TailshareContext) async -> Response {
        guard let caller = await who(context) else { return Self.status(403) }
        guard let token = context.parameters.get("token") else { return Self.status(403) }
        let download: PickupHolds.Download
        switch o.holds.take(token: token, for: caller.id) {
        case .download(let d): download = d
        case .denied: return Self.status(403)
        case .unreadable: return Self.status(404)
        }

        var headers = HTTPFields()
        if download.kind == .text {
            headers[.contentType] = "text/plain; charset=utf-8"
        } else {
            headers[.contentType] = "application/octet-stream"
            headers[.contentDisposition] = Self.disposition(download.name ?? "file")
        }
        return Response(status: .ok, headers: headers, body: ResponseBody(contentLength: Int(download.length), download.stream))
    }

    /// The name goes into a header: quotes, backslashes and control characters are replaced, and a name with anything
    /// outside ASCII also carries its real spelling in the `filename*` form.
    static func disposition(_ name: String) -> String {
        let fallback = String(name.unicodeScalars.map { u -> Character in
            (u.value < 0x20 || u.value > 0x7E || u == "\"" || u == "\\") ? "_" : Character(u)
        })
        guard fallback != name else { return "attachment; filename=\"\(fallback)\"" }
        let encoded = name.addingPercentEncoding(withAllowedCharacters: CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")) ?? fallback
        return "attachment; filename=\"\(fallback)\"; filename*=UTF-8''\(encoded)"
    }
}
