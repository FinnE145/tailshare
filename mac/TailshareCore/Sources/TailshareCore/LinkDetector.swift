import Foundation

public enum LinkDetector {
    /// A lone absolute http or https URL, or nil. This is what decides whether a click opens a
    /// browser, so every other scheme (file:, javascript:, x-apple.systempreferences:…) must stay plain text.
    public static func link(in text: String) -> URL? {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty || t.unicodeScalars.contains(where: { CharacterSet.whitespacesAndNewlines.contains($0) }) { return nil }

        // URL(string:) also accepts "https:example.com"; require the real form.
        let lower = t.lowercased()
        guard lower.hasPrefix("http://") || lower.hasPrefix("https://") else { return nil }

        guard let url = URL(string: t), let host = url.host, !host.isEmpty else { return nil }
        return url
    }
}
