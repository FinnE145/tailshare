import Foundation

/// The `tailshare://share` link the Share Extension opens to hand what was shared to the app.
/// Any app or web page can open such a link, so parsing is strict and the caller must also check who sent it.
public enum HandOff {
    public static let scheme = "tailshare"
    public static let host = "share"
    public static let extensionBundleID = "io.github.finne145.tailshare.ShareExtension"

    public enum Request: Equatable, Sendable {
        /// Paths of the files and folders that were shared.
        case files([String])
        /// Text is on the private pasteboard; the link carries only the marker.
        case text
    }

    public static func filesLink(_ paths: [String]) -> URL? {
        var c = URLComponents()
        c.scheme = scheme
        c.host = host
        c.queryItems = paths.map { URLQueryItem(name: "file", value: $0) }
        return c.url
    }

    public static func textLink() -> URL? {
        var c = URLComponents()
        c.scheme = scheme
        c.host = host
        c.queryItems = [URLQueryItem(name: "text", value: "1")]
        return c.url
    }

    /// Nil for a link that is not exactly one of the two forms.
    public static func parse(_ url: URL) -> Request? {
        guard url.scheme == scheme, url.host == host,
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        else { return nil }

        let files = items.filter { $0.name == "file" }.compactMap(\.value)
        let texts = items.filter { $0.name == "text" }
        // Anything beyond file and text items, or a file item with no value, makes the link foreign.
        if items.contains(where: { $0.name != "file" && $0.name != "text" }) { return nil }
        if files.count != items.filter({ $0.name == "file" }).count { return nil }

        if texts.isEmpty { return files.isEmpty || files.contains(where: \.isEmpty) ? nil : .files(files) }
        return files.isEmpty && texts.count == 1 && texts[0].value == "1" ? .text : nil
    }

    /// Whether a link came from the extension. `bundleID` is that of the app that sent the Apple Event, nil if unknown.
    public static func isFromExtension(_ bundleID: String?) -> Bool { bundleID == extensionBundleID }
}
