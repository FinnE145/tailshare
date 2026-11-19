import Foundation

public enum OfferKind: String, Codable, Sendable { case text, file, bundle }

/// The body of `POST /v1/offer`. Field names are the wire names (camelCase), as docs/specs/windows-app-A.md defines them.
public struct Offer: Codable, Equatable, Sendable {
    public var kind: OfferKind
    public var name: String?
    public var items: Int
    public var isFolder: Bool
    /// Omitted by a sender that doesn't know the length up front (a phone's Shortcut); the upload then has no declared length.
    public var bytes: Int64?
    /// Required for a file or bundle, may be omitted for text (spec E).
    public var totalSize: Int64?
    /// `totalSize` is a guess; for a bundle the extraction cap is loosened (spec E).
    public var sizeIsEstimate: Bool?

    public init(kind: OfferKind, name: String?, items: Int, isFolder: Bool, bytes: Int64?, totalSize: Int64?, sizeIsEstimate: Bool? = nil) {
        self.kind = kind
        self.name = name
        self.items = items
        self.isFolder = isFolder
        self.bytes = bytes
        self.totalSize = totalSize
        self.sizeIsEstimate = sizeIsEstimate
    }

    /// What the size lines of a prompt or notification show.
    public var shownSize: Int64 { totalSize ?? 0 }

    /// The total a progress bar counts against: the declared length, else the announced size.
    public var progressTotal: Int64 { bytes ?? totalSize ?? 0 }

    /// How much extraction may write. A flagged bundle gets room for a wrong guess; nothing else does.
    var extractionCap: Int64 {
        let size = totalSize ?? 0
        guard kind == .bundle, sizeIsEstimate == true else { return size }
        return Int64(Double(size) * 1.25) + 1024 * 1024
    }
}

public struct HelloResponse: Codable, Equatable, Sendable {
    public var app: String
    public var `protocol`: Int
    /// Present and true only in a hub's answer; an app's answer has no such field.
    public var hub: Bool?

    public var isHub: Bool { hub == true }
}

struct TokenResponse: Codable { var token: String }
struct ErrorResponse: Codable { var error: String }

/// Someone on the tailnet: the stable node ID and the Tailscale machine name.
public struct Identity: Codable, Equatable, Sendable {
    public var id: String
    public var name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }
}

public enum Wire {
    /// Every device must use the same port; settings.json may override this (see `Settings.effectivePort`).
    public static let defaultPort = 45611
    public static let version = 1
    public static let appName = "tailshare"

    public static let helloTimeout: TimeInterval = 2
    public static let offerTimeout: TimeInterval = 130
    public static let promptTimeout: TimeInterval = 120
    public static let tokenLifetime: TimeInterval = 60
    /// How long a computer holds an item for a phone or iPad (spec E).
    public static let holdLifetime: TimeInterval = 120
    /// The hub answers /v1/devices only after probing every peer, and one that doesn't answer costs it a whole `helloTimeout`:
    /// waiting no longer than a hello lost every phone whenever a single computer was off (found 2026-11-19 on the Windows app).
    public static let devicesTimeout: TimeInterval = 5

    public static let textLimitBytes: Int64 = 1024 * 1024

    /// Whether the offer is well formed; the receiver answers 400 when it is not.
    static func isValid(_ o: Offer) -> Bool {
        if o.items < 1 || (o.bytes ?? 0) < 0 || (o.totalSize ?? 0) < 0 { return false }
        if o.kind == .text { return o.items == 1 }
        if o.totalSize == nil { return false }
        if (o.name ?? "").isEmpty { return false }
        if o.kind == .file && o.items != 1 { return false }
        return true
    }
}
