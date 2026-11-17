import Foundation

/// What is being sent: some text, or a set of files and folders.
public enum SendContent: Sendable {
    case text(String)
    case paths([URL])
}

public enum SendStage: Sendable { case preparing, waiting, sending }

public struct SendProgress: Sendable {
    public var stage: SendStage
    public var bytesSent: Int64
    public var bytesTotal: Int64
}

public enum SendOutcome: Equatable, Sendable { case sent, declined, didntRespond, failed, cancelled }

public final class Sender: @unchecked Sendable {
    private let session: URLSession
    private let port: Int
    private let offerTimeout: TimeInterval
    private let helloTimeout: TimeInterval

    public init(port: Int = Wire.defaultPort, offerTimeout: TimeInterval = Wire.offerTimeout, helloTimeout: TimeInterval = Wire.helloTimeout) {
        self.port = port
        self.offerTimeout = offerTimeout
        self.helloTimeout = helloTimeout

        let config = URLSessionConfiguration.ephemeral
        // Timeouts are per request (below): an upload of any size, and a receiver that takes a while
        // to unpack a big bundle before it answers, must not hit a session-wide one.
        config.timeoutIntervalForRequest = 24 * 3600
        config.timeoutIntervalForResource = 7 * 24 * 3600
        // A tailnet address is reached directly; a system proxy has no business in the way.
        config.connectionProxyDictionary = [:]
        session = URLSession(configuration: config)
    }

    deinit { session.invalidateAndCancel() }

    private func url(_ peer: PeerInfo, _ path: String) -> URL { URL(string: "http://\(peer.ip):\(port)\(path)")! }

    /// The online peers that answer `hello` as tailshare protocol 1, probed in parallel.
    public func discover(_ status: TailnetStatus) async -> [PeerInfo] {
        await withTaskGroup(of: PeerInfo?.self) { group in
            for peer in status.peers where peer.online {
                group.addTask { await self.isTailshare(peer) ? peer : nil }
            }
            var found: [PeerInfo] = []
            for await peer in group { if let peer { found.append(peer) } }
            return found.sorted { $0.identity.name.localizedCaseInsensitiveCompare($1.identity.name) == .orderedAscending }
        }
    }

    private func isTailshare(_ peer: PeerInfo) async -> Bool {
        var request = URLRequest(url: url(peer, "/v1/hello"))
        request.timeoutInterval = helloTimeout
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let hello = try? JSONDecoder().decode(HelloResponse.self, from: data)
        else { return false }
        return hello.app == Wire.appName && hello.protocol == Wire.version
    }

    public func send(to peer: PeerInfo, content: SendContent, progress: (@Sendable (SendProgress) -> Void)? = nil) async -> SendOutcome {
        var zipURL: URL?
        defer { if let zipURL { try? FileManager.default.removeItem(at: zipURL) } }

        do {
            let offer: Offer
            var source: Upload

            switch content {
            case .text(let value):
                let bytes = Data(value.utf8)
                offer = Offer(kind: .text, name: nil, items: 1, isFolder: false, bytes: Int64(bytes.count), totalSize: Int64(bytes.count))
                source = .data(bytes)

            case .paths(let paths):
                var isDir: ObjCBool = false
                if paths.count == 1, FileManager.default.fileExists(atPath: paths[0].path, isDirectory: &isDir), !isDir.boolValue {
                    let size = (try FileManager.default.attributesOfItem(atPath: paths[0].path)[.size] as? NSNumber)?.int64Value ?? 0
                    offer = Offer(kind: .file, name: paths[0].lastPathComponent, items: 1, isFolder: false, bytes: size, totalSize: size)
                    source = .file(paths[0])
                } else {
                    progress?(SendProgress(stage: .preparing, bytesSent: 0, bytesTotal: 0))
                    let zip = FileManager.default.temporaryDirectory.appendingPathComponent("tailshare-\(UUID().uuidString).zip")
                    zipURL = zip
                    let info = try Bundles.create(paths: paths, zipURL: zip, isCancelled: { Task.isCancelled })
                    let size = (try FileManager.default.attributesOfItem(atPath: zip.path)[.size] as? NSNumber)?.int64Value ?? 0
                    offer = Offer(kind: .bundle, name: info.name, items: info.items, isFolder: info.isFolder, bytes: size, totalSize: info.totalSize)
                    source = .file(zip)
                }
            }

            progress?(SendProgress(stage: .waiting, bytesSent: 0, bytesTotal: offer.bytes))
            switch try await requestToken(peer, offer) {
            case .outcome(let outcome): return outcome
            case .token(let token): return try await upload(peer, token, offer, source, progress)
            }
        } catch is CancellationError {
            return .cancelled
        } catch let error as URLError where error.code == .cancelled {
            return .cancelled
        } catch {
            // Anything else — a refused connection, a reply that isn't the expected JSON, a file that
            // can't be read — is a failed send, never an escape into the app.
            return Task.isCancelled ? .cancelled : .failed
        }
    }

    private enum Upload { case data(Data), file(URL) }
    private enum OfferResult { case token(String), outcome(SendOutcome) }

    private func requestToken(_ peer: PeerInfo, _ offer: Offer) async throws -> OfferResult {
        var request = URLRequest(url: url(peer, "/v1/offer"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(offer)
        request.timeoutInterval = offerTimeout

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError where error.code == .timedOut {
            return .outcome(.didntRespond)
        }

        switch (response as? HTTPURLResponse)?.statusCode {
        case 200:
            let body = try JSONDecoder().decode(TokenResponse.self, from: data)
            return body.token.isEmpty ? .outcome(.failed) : .token(body.token)
        case 403:
            switch (try? JSONDecoder().decode(ErrorResponse.self, from: data))?.error {
            // A blocked sender learns no more than a declined one.
            case "declined", "blocked": return .outcome(.declined)
            case "timeout": return .outcome(.didntRespond)
            default: return .outcome(.failed)
            }
        default:
            return .outcome(.failed)
        }
    }

    private func upload(_ peer: PeerInfo, _ token: String, _ offer: Offer, _ source: Upload, _ progress: (@Sendable (SendProgress) -> Void)?) async throws -> SendOutcome {
        var request = URLRequest(url: url(peer, "/v1/upload/" + (token.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? token)))
        request.httpMethod = "PUT"
        // Set on an upload from a file or data by URLSession itself, which sends no "Expect: 100-continue".
        request.timeoutInterval = 24 * 3600

        let reporter = ProgressReporter(total: offer.bytes, report: progress)
        progress?(SendProgress(stage: .sending, bytesSent: 0, bytesTotal: offer.bytes))

        let response: URLResponse
        switch source {
        case .data(let data): (_, response) = try await session.upload(for: request, from: data, delegate: reporter)
        case .file(let url): (_, response) = try await session.upload(for: request, fromFile: url, delegate: reporter)
        }
        return (response as? HTTPURLResponse)?.statusCode == 200 ? .sent : .failed
    }

    private final class ProgressReporter: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
        let total: Int64
        let report: (@Sendable (SendProgress) -> Void)?

        init(total: Int64, report: (@Sendable (SendProgress) -> Void)?) {
            self.total = total
            self.report = report
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64, totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
            report?(SendProgress(stage: .sending, bytesSent: totalBytesSent, bytesTotal: total))
        }
    }
}
