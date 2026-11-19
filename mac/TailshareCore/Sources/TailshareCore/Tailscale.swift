import Foundation
import os

public struct PeerInfo: Equatable, Sendable {
    public var identity: Identity
    public var ip: String
    public var online: Bool
    /// A phone or iPad the hub listed: it has no address (`ip` is empty) and is sent to by holding the item for it.
    public var isMobile: Bool

    public init(identity: Identity, ip: String, online: Bool, isMobile: Bool = false) {
        self.identity = identity
        self.ip = ip
        self.online = online
        self.isMobile = isMobile
    }
}

/// `connected` is false when Tailscale is down, logged out or has no IPv4: nothing can be received or sent.
public struct TailnetStatus: Equatable, Sendable {
    public var connected: Bool
    public var selfIP: String?
    public var peers: [PeerInfo]

    public init(connected: Bool, selfIP: String?, peers: [PeerInfo]) {
        self.connected = connected
        self.selfIP = selfIP
        self.peers = peers
    }

    public static let disconnected = TailnetStatus(connected: false, selfIP: nil, peers: [])
}

public protocol Tailscale: Sendable {
    func status() async -> TailnetStatus

    /// Who is behind a tailnet address, or nil if Tailscale doesn't know it.
    func whois(ip: String) async -> Identity?
}

/// Reads the JSON the Tailscale CLI prints. Separate from the process call so it can be tested on fixtures.
public enum TailscaleJSON {
    public static func parseStatus(_ data: Data) -> TailnetStatus {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              root["BackendState"] as? String == "Running",
              let me = root["Self"] as? [String: Any],
              let selfIP = firstTailnetIPv4(me)
        else { return .disconnected }

        var peers: [PeerInfo] = []
        if let map = root["Peer"] as? [String: Any] {
            for case let p as [String: Any] in map.values {
                guard let ip = firstTailnetIPv4(p),
                      let id = p["ID"] as? String, !id.isEmpty,
                      let dns = p["DNSName"] as? String, !dns.isEmpty
                else { continue }
                peers.append(PeerInfo(identity: Identity(id: id, name: firstLabel(dns)), ip: ip, online: p["Online"] as? Bool == true))
            }
        }
        peers.sort { $0.identity.name.localizedCaseInsensitiveCompare($1.identity.name) == .orderedAscending }
        return TailnetStatus(connected: true, selfIP: selfIP, peers: peers)
    }

    /// The shape of a status answer for the log: the backend state, which top-level and `Self` keys exist, and how many
    /// addresses `Self` lists. Never a name or an address.
    public static func describe(_ data: Data) -> String {
        // Text that is not JSON is the CLI talking about itself (an error message), never device data, so it is quoted.
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return "not JSON, \(data.count) bytes: \(String(decoding: data.prefix(200), as: UTF8.self))"
        }
        let me = root["Self"] as? [String: Any]
        let ips = (me?["TailscaleIPs"] as? [String])?.count
        return "BackendState=\(root["BackendState"] as? String ?? "none") keys=\(root.keys.sorted()) selfKeys=\(me?.keys.sorted() ?? []) selfIPs=\(ips.map(String.init) ?? "none")"
    }

    public static func parseWhois(_ data: Data) -> Identity? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let node = root["Node"] as? [String: Any],
              let id = node["StableID"] as? String, !id.isEmpty,
              let name = node["ComputedName"] as? String, !name.isEmpty
        else { return nil }
        return Identity(id: id, name: name)
    }

    // "my-laptop.tailnet-name.ts.net." -> "my-laptop"
    private static func firstLabel(_ dns: String) -> String {
        let trimmed = dns.hasSuffix(".") ? String(dns.dropLast()) : dns
        return String(trimmed.split(separator: ".", omittingEmptySubsequences: false).first ?? "")
    }

    private static func firstTailnetIPv4(_ node: [String: Any]) -> String? {
        (node["TailscaleIPs"] as? [String])?.first(where: isTailnetIPv4)
    }

    /// 100.64.0.0/10, the carrier-grade NAT range Tailscale hands out.
    public static func isTailnetIPv4(_ ip: String) -> Bool {
        let parts = ip.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4, let octets = Optional(parts.compactMap { UInt8($0) }), octets.count == 4 else { return false }
        return octets[0] == 100 && (octets[1] & 0xC0) == 64
    }
}

public struct CLITailscale: Tailscale {
    /// A GUI app gets no shell PATH, so the CLI is looked for at fixed places. The App Store and the
    /// standalone build both put it inside the app bundle.
    public static let defaultCandidates = [
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
        "/usr/local/bin/tailscale",
        "/opt/homebrew/bin/tailscale",
    ]

    private static let callTimeout: TimeInterval = 5
    // Why a call failed goes to the system log (`log show --predicate 'subsystem == "io.github.finne145.tailshare"'`):
    // from the outside "Tailscale not connected" looks the same for every cause. Never the output itself, which names devices.
    private static let log = Logger(subsystem: "io.github.finne145.tailshare", category: "tailscale")

    private let candidates: [String]
    private let exists: @Sendable (String) -> Bool

    public init(candidates: [String] = CLITailscale.defaultCandidates,
                exists: @escaping @Sendable (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) }) {
        self.candidates = candidates
        self.exists = exists
    }

    /// The first candidate that exists. Looked up on every call, so installing Tailscale later is noticed.
    public static func locate(in candidates: [String], exists: (String) -> Bool) -> String? {
        candidates.first(where: exists)
    }

    public func status() async -> TailnetStatus {
        guard let out = await run(["status", "--json"]) else { return .disconnected }
        let status = TailscaleJSON.parseStatus(out)
        if !status.connected { Self.log.error("status answered, but not a running tailnet with an IPv4 address: \(TailscaleJSON.describe(out), privacy: .public)") }
        return status
    }

    public func whois(ip: String) async -> Identity? {
        // Only a tailnet address is ever looked up: the argument is never something a peer chose.
        guard TailscaleJSON.isTailnetIPv4(ip), let out = await run(["whois", "--json", ip]) else { return nil }
        return TailscaleJSON.parseWhois(out)
    }

    // Nil on any failure: a missing CLI, a non-zero exit (whois of an unknown address) and a
    // timeout all mean "no answer", and callers treat no answer as no identity.
    private func run(_ args: [String]) async -> Data? {
        guard let exe = Self.locate(in: candidates, exists: exists) else { return nil }

        return await withCheckedContinuation { continuation in
            DispatchQueue.global().async {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: exe)
                process.arguments = args
                // The macOS app and its CLI are one binary, and which one it acts as is guessed from how it was
                // started. Launched from the GUI (Launchpad, Spotlight) it guessed "GUI" for a child of ours and
                // answered "The Tailscale GUI failed to start". Tailscale's own /usr/local/bin/tailscale wrapper
                // sets this variable for the same reason.
                process.environment = ProcessInfo.processInfo.environment.merging(["TAILSCALE_BE_CLI": "1"]) { _, new in new }
                let out = Pipe()
                let err = Pipe()
                process.standardOutput = out
                process.standardError = err
                process.standardInput = FileHandle.nullDevice

                do { try process.run() } catch {
                    Self.log.error("could not start \(args[0], privacy: .public): \(error.localizedDescription, privacy: .public)")
                    continuation.resume(returning: nil)
                    return
                }

                let killer = DispatchWorkItem { if process.isRunning { process.terminate() } }
                DispatchQueue.global().asyncAfter(deadline: .now() + Self.callTimeout, execute: killer)

                let data = out.fileHandleForReading.readDataToEndOfFile()
                let stderr = err.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                killer.cancel()

                if process.terminationStatus != 0 {
                    // The first line of what the CLI said about itself ("Failed to load preferences" and the like), capped.
                    let said = String(decoding: stderr.prefix(160), as: UTF8.self).split(separator: "\n").first.map(String.init) ?? ""
                    Self.log.error("\(args[0], privacy: .public) exited \(process.terminationStatus): \(said, privacy: .public)")
                }
                continuation.resume(returning: process.terminationStatus == 0 ? data : nil)
            }
        }
    }
}
