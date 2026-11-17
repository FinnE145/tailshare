import Foundation

public enum OfferKind: String, Codable, Sendable { case text, file, bundle }

/// The body of `POST /v1/offer`. Field names are the wire names (camelCase), as docs/specs/windows-app-A.md defines them.
public struct Offer: Codable, Equatable, Sendable {
    public var kind: OfferKind
    public var name: String?
    public var items: Int
    public var isFolder: Bool
    public var bytes: Int64
    public var totalSize: Int64

    public init(kind: OfferKind, name: String?, items: Int, isFolder: Bool, bytes: Int64, totalSize: Int64) {
        self.kind = kind
        self.name = name
        self.items = items
        self.isFolder = isFolder
        self.bytes = bytes
        self.totalSize = totalSize
    }
}

public struct HelloResponse: Codable, Equatable, Sendable {
    public var app: String
    public var `protocol`: Int
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

    public static let textLimitBytes: Int64 = 1024 * 1024

    /// Whether the offer is well formed; the receiver answers 400 when it is not.
    static func isValid(_ o: Offer) -> Bool {
        if o.items < 1 || o.bytes < 0 || o.totalSize < 0 { return false }
        if o.kind == .text { return o.items == 1 }
        if (o.name ?? "").isEmpty { return false }
        if o.kind == .file && o.items != 1 { return false }
        return true
    }
}
