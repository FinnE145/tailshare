import Foundation

public struct Notice: Equatable, Sendable {
    public var title: String
    public var body: String
}

/// Titles and bodies of every notification, built here so they can be tested without a shell.
public enum Notices {
    public static let progressThresholdBytes: Int64 = 20 * 1024 * 1024
    public static let textPreviewLength = 200

    public static func formatSize(_ bytes: Int64) -> String {
        if bytes < 1024 { return "\(bytes) B" }

        var value = Double(bytes)
        let units = ["B", "KB", "MB", "GB"]
        var unit = 0
        // Compare the rounded value: 1048575 bytes is "1024.0 KB" if only the raw value is checked.
        while unit < units.count - 1 && (value * 10).rounded(.toNearestOrEven) / 10 >= 1024 {
            value /= 1024
            unit += 1
        }
        return String(format: "%.1f", value) + " " + units[unit]
    }

    /// What an offer is, for a notification: `photo.jpg, 2.4 MB`, `Photos (folder), 1.2 GB`, `text`.
    public static func describe(_ o: Offer) -> String {
        if o.kind == .text { return "text" }

        // Names here come from a peer and are shown before anything is saved; show the form that
        // would be written, which has no control characters or path parts.
        let name = Names.sanitize(o.name)
        let size = formatSize(o.shownSize)
        if o.items > 1 { return "\(name) and \(o.items - 1) more, \(size)" }
        if o.kind == .bundle && o.isFolder { return "\(name) (folder), \(size)" }
        return "\(name), \(size)"
    }

    public static func firstContact(device: String, offer: Offer) -> Notice {
        Notice(title: device, body: "wants to send " + describe(offer))
    }

    public static func received(device: String, offer: Offer) -> Notice {
        Notice(title: "From " + device, body: describe(offer))
    }

    public static func receivedText(device: String, text: String) -> Notice {
        // A link shows in full: a truncated URL can't be judged before clicking it.
        if LinkDetector.link(in: text) != nil {
            return Notice(title: "From " + device, body: text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return Notice(title: "From " + device, body: truncate(text))
    }

    public static func progress(device: String, offer: Offer) -> String {
        "Receiving \(describe(offer)) from \(device)"
    }

    public static func truncate(_ text: String) -> String {
        let units = Array(text.utf16)
        if units.count <= textPreviewLength { return text }
        var cut = textPreviewLength
        // Never end on half a surrogate pair.
        if UTF16.isLeadSurrogate(units[cut - 1]) { cut -= 1 }
        return String(decoding: units[..<cut], as: UTF16.self) + "…"
    }

    public static func sent(to device: String) -> Notice { Notice(title: "Sent to " + device, body: "") }
    public static func declined(by device: String) -> Notice { Notice(title: device + " declined", body: "") }
    public static func noResponse(from device: String) -> Notice { Notice(title: device + " didn't respond", body: "") }
    public static func failed(to device: String) -> Notice { Notice(title: "Couldn't send to " + device, body: "") }
    public static func waitingForPickup(device: String) -> String { "Waiting for \(device) to pick up" }
    public static func pickedUp(by device: String) -> Notice { Notice(title: "Picked up by " + device, body: "") }
    public static func notPickedUp(by device: String) -> Notice { Notice(title: "Not picked up by " + device, body: "") }
    public static func portInUse(_ port: Int) -> Notice { Notice(title: "Can't receive: port \(port) is in use", body: "") }
}
