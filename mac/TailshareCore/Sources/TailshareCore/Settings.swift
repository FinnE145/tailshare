import Foundation

public struct Settings: Equatable, Sendable {
    public var receiveFolder: String?
    public var port: Int?
    public var trusted: [Identity]
    public var blocked: [Identity]
    /// A Tailscale machine name or IPv4 that overrides finding the hub automatically (spec E). Only a person edits it.
    public var hub: String?

    public init(receiveFolder: String? = nil, port: Int? = nil, trusted: [Identity] = [], blocked: [Identity] = [], hub: String? = nil) {
        self.hub = hub
        self.receiveFolder = receiveFolder
        self.port = port
        self.trusted = trusted
        self.blocked = blocked
    }

    /// The configured port if it is a usable one (1024 to 65535), else the default. Ports below 1024 need root.
    public var effectivePort: Int {
        if let port, (1024...65535).contains(port) { return port }
        return Wire.defaultPort
    }

    /// The hub to use instead of the one found by `hello`; null, absent and "" all mean "find it".
    public var hubSetting: String? {
        let trimmed = hub?.trimmingCharacters(in: .whitespaces) ?? ""
        return trimmed.isEmpty ? nil : trimmed
    }

    public func isTrusted(_ id: String) -> Bool { trusted.contains { $0.id == id } }
    public func isBlocked(_ id: String) -> Bool { blocked.contains { $0.id == id } }
}

public struct LoadedSettings: Sendable {
    public var value: Settings
    public var malformed: Bool
}

/// The file as it is on disk, with the lenience a hand-edited file needs: any field may be missing
/// or null, and an entry of a list may be null.
private struct SettingsFile: Codable {
    struct Entry: Codable {
        var id: String?
        var name: String?
    }

    var receiveFolder: String?
    var port: Int?
    var trusted: [Entry?]?
    var blocked: [Entry?]?
    var hub: String?

    init(_ s: Settings) {
        hub = s.hub
        receiveFolder = s.receiveFolder
        port = s.port
        trusted = s.trusted.map { Entry(id: $0.id, name: $0.name) }
        blocked = s.blocked.map { Entry(id: $0.id, name: $0.name) }
    }

    var settings: Settings {
        func identities(_ list: [Entry?]?) -> [Identity] {
            (list ?? []).compactMap { e in e?.id.map { Identity(id: $0, name: e?.name ?? "") } }
        }
        return Settings(receiveFolder: receiveFolder, port: port, trusted: identities(trusted), blocked: identities(blocked), hub: hub)
    }

    enum CodingKeys: String, CodingKey { case receiveFolder, port, trusted, blocked, hub }

    // Written with explicit nulls, so a person editing the file can see every key there is.
    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        if let receiveFolder { try c.encode(receiveFolder, forKey: .receiveFolder) } else { try c.encodeNil(forKey: .receiveFolder) }
        if let port { try c.encode(port, forKey: .port) } else { try c.encodeNil(forKey: .port) }
        try c.encode(trusted ?? [], forKey: .trusted)
        try c.encode(blocked ?? [], forKey: .blocked)
        // Never written by the app, only carried through, so an Accept leaves it as the person set it.
        if let hub { try c.encode(hub, forKey: .hub) } else { try c.encodeNil(forKey: .hub) }
    }
}

/// The only writer of the settings file. It is hand-edited, so it is read fresh every time and a
/// file that doesn't parse is never overwritten.
public final class SettingsStore: @unchecked Sendable {
    public let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL) { self.fileURL = fileURL }

    public func load() -> LoadedSettings {
        lock.lock()
        defer { lock.unlock() }
        return loadUnlocked()
    }

    private func loadUnlocked() -> LoadedSettings {
        let data: Data
        do {
            data = try Data(contentsOf: fileURL)
        } catch let error as NSError where error.domain == NSCocoaErrorDomain
            && (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError) {
            return LoadedSettings(value: Settings(), malformed: false)
        } catch {
            return LoadedSettings(value: Settings(), malformed: true)
        }

        guard let file = try? JSONDecoder().decode(SettingsFile.self, from: data) else {
            return LoadedSettings(value: Settings(), malformed: true)
        }
        return LoadedSettings(value: file.settings, malformed: false)
    }

    /// Adds `who` to `trusted`. False when nothing was saved because the file is malformed or couldn't be written.
    @discardableResult
    public func addTrusted(_ who: Identity) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        let loaded = loadUnlocked()
        if loaded.malformed { return false }
        if loaded.value.isTrusted(who.id) { return true }

        var updated = loaded.value
        updated.trusted.append(who)
        return write(updated)
    }

    // The atomic option writes a temp file and then renames it over the real one, so a crash can't
    // leave half a file where the user's edits were.
    private func write(_ s: Settings) -> Bool {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try encoder.encode(SettingsFile(s)).write(to: fileURL, options: .atomic)
            return true
        } catch {
            return false
        }
    }
}
