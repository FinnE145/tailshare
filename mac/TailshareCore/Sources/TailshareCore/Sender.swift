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

/// `pickedUp` and `notPickedUp` are the ends of a hold for a phone or iPad (`PickupHolds`), never of a send to a computer.
public enum SendOutcome: Equatable, Sendable { case sent, declined, didntRespond, failed, cancelled, pickedUp, notPickedUp }

public final class Sender: @unchecked Sendable {
    private let session: URLSession
    private let port: Int
    private let offerTimeout: TimeInterval
    private let helloTimeout: TimeInterval
    private let devicesTimeout: TimeInterval
    /// Per-address ports, for tests: a peer listed here is reached on loopback at that port, since only the port tells the test peers apart.
    private let portOverrides: [String: Int]

    public convenience init(port: Int = Wire.defaultPort, offerTimeout: TimeInterval = Wire.offerTimeout, helloTimeout: TimeInterval = Wire.helloTimeout,
                            devicesTimeout: TimeInterval = Wire.devicesTimeout) {
        self.init(port: port, offerTimeout: offerTimeout, helloTimeout: helloTimeout, devicesTimeout: devicesTimeout, portOverrides: [:])
    }

    init(port: Int, offerTimeout: TimeInterval = Wire.offerTimeout, helloTimeout: TimeInterval = Wire.helloTimeout,
         devicesTimeout: TimeInterval = Wire.devicesTimeout, portOverrides: [String: Int]) {
        self.devicesTimeout = devicesTimeout
        self.portOverrides = portOverrides
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

    private func url(_ ip: String, _ path: String) -> URL {
        if let overridden = portOverrides[ip] { return URL(string: "http://127.0.0.1:\(overridden)\(path)")! }
        return URL(string: "http://\(ip):\(port)\(path)")!
    }
    private func url(_ peer: PeerInfo, _ path: String) -> URL { url(peer.ip, path) }

    /// The online peers that answer `hello` as tailshare protocol 1 and are not hubs, probed in parallel.
    public func discover(_ status: TailnetStatus) async -> [PeerInfo] {
        await probe(status).computers
    }

    /// Computers (as `discover`), plus the phones and iPads the hub lists. The hub is the `hub` setting when it names one,
    /// otherwise the first peer that answered `hello` with `hub: true`; one that is down or slow costs only the mobiles.
    public func discoverDevices(_ status: TailnetStatus, hubSetting: String?) async -> [PeerInfo] {
        let found = await probe(status)
        guard let hubAddress = Self.hubAddress(setting: hubSetting, status: status, answered: found.hubs) else { return found.computers }

        let mobiles = await mobiles(fromHubAt: hubAddress)
        let listed = Set(found.computers.map(\.identity.id))
        let extra = mobiles.filter { !listed.contains($0.identity.id) }
        return found.computers + extra
    }

    /// Which address to ask for the list of phones. With a setting: that machine name's address, or the IPv4 itself.
    static func hubAddress(setting: String?, status: TailnetStatus, answered hubs: [PeerInfo]) -> String? {
        if let named = setting?.trimmingCharacters(in: .whitespaces), !named.isEmpty {
            if TailscaleJSON.isTailnetIPv4(named) { return named }
            return status.peers.first { $0.identity.name.caseInsensitiveCompare(named) == .orderedSame }?.ip
        }
        return hubs.first?.ip
    }

    private struct Probed { var computers: [PeerInfo]; var hubs: [PeerInfo] }

    private func probe(_ status: TailnetStatus) async -> Probed {
        await withTaskGroup(of: (PeerInfo, HelloResponse)?.self) { group in
            for peer in status.peers where peer.online {
                group.addTask { await self.hello(peer).map { (peer, $0) } }
            }
            var computers: [PeerInfo] = []
            var hubs: [PeerInfo] = []
            for await answer in group {
                guard let (peer, hello) = answer, hello.app == Wire.appName, hello.protocol == Wire.version else { continue }
                if hello.isHub { hubs.append(peer) } else { computers.append(peer) }
            }
            let byName: (PeerInfo, PeerInfo) -> Bool = { $0.identity.name.localizedCaseInsensitiveCompare($1.identity.name) == .orderedAscending }
            return Probed(computers: computers.sorted(by: byName), hubs: hubs.sorted(by: byName))
        }
    }

    private func hello(_ peer: PeerInfo) async -> HelloResponse? {
        var request = URLRequest(url: url(peer.ip, "/v1/hello"))
        request.timeoutInterval = helloTimeout
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200
        else { return nil }
        return try? JSONDecoder().decode(HelloResponse.self, from: data)
    }

    private struct HubDevices: Decodable {
        struct Mobile: Decodable { var name: String; var id: String }
        var mobiles: [Mobile]
    }

    private func mobiles(fromHubAt address: String) async -> [PeerInfo] {
        var request = URLRequest(url: url(address, "/v1/devices"))
        request.timeoutInterval = devicesTimeout
        guard let (data, response) = try? await session.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let list = try? JSONDecoder().decode(HubDevices.self, from: data)
        else { return [] }
        return list.mobiles
            .filter { !$0.id.isEmpty && !$0.name.isEmpty }
            .map { PeerInfo(identity: Identity(id: $0.id, name: $0.name), ip: "", online: true, isMobile: true) }
            .sorted { $0.identity.name.localizedCaseInsensitiveCompare($1.identity.name) == .orderedAscending }
    }

    public func send(to peer: PeerInfo, content: SendContent, progress: (@Sendable (SendProgress) -> Void)? = nil) async -> SendOutcome {
        // A phone or iPad has no address: it is sent to by holding the item (`PickupHolds`).
        if peer.isMobile { return .failed }

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

            progress?(SendProgress(stage: .waiting, bytesSent: 0, bytesTotal: offer.progressTotal))
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

        let reporter = ProgressReporter(total: offer.progressTotal, report: progress)
        progress?(SendProgress(stage: .sending, bytesSent: 0, bytesTotal: offer.progressTotal))

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
