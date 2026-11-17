import Foundation

/// Turns names that arrive from a peer into names that are safe to create on disk.
/// Matches windows/Tailshare.Core/Names.cs, so every app names a received file the same way.
public enum Names {
    public static let maxLength = 200
    /// APFS's limit on a name, in UTF-8 bytes. The Mac's one addition to A's rules.
    public static let maxBytes = 255

    // The same fixed set as on Windows: a file received here may be copied to Windows later.
    private static let invalid: Set<UInt16> = Set("<>:\"/\\|?*".utf16)

    private static let reserved: Set<String> = [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ]

    // Lengths are UTF-16 units, as in the C# the Windows app and the test peer use.
    public static func sanitize(_ name: String?) -> String {
        var units = Array((name ?? "").utf16)

        if let cut = units.lastIndex(where: { $0 == 0x2F || $0 == 0x5C }) { units = Array(units[(cut + 1)...]) }

        for i in units.indices where units[i] < 0x20 || invalid.contains(units[i]) { units[i] = 0x5F }

        var s = String(decoding: units, as: UTF16.self).trimmingCharacters(in: .whitespacesAndNewlines)
        // Windows silently drops trailing dots and spaces, which would make "..." collide with "".
        while let last = s.utf16.last, last == 0x2E || last == 0x20 { s = String(decoding: Array(s.utf16.dropLast()), as: UTF16.self) }

        if s.isEmpty { s = "file" }

        let stem = s.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? s
        // "CON.txt" is reserved on Windows just like "CON".
        if reserved.contains(stem.uppercased()) { s = "_" + s }

        if s.utf16.count > maxLength || s.utf8.count > maxBytes { s = capKeepingExtension(s) }
        return s
    }

    private static func fits(_ s: String) -> Bool { s.utf16.count <= maxLength && s.utf8.count <= maxBytes }

    private static func extensionOf(_ s: String) -> String {
        // Like Path.GetExtension: none for a leading dot (".gitignore") or a trailing one ("a.").
        guard let dot = s.lastIndex(of: "."), dot != s.startIndex, s.index(after: dot) != s.endIndex else { return "" }
        return String(s[dot...])
    }

    private static func capKeepingExtension(_ s: String) -> String {
        var ext = extensionOf(s)
        if ext.utf16.count > 20 || ext.utf8.count > 40 || ext.count == s.count { ext = "" }

        // Drop whole unicode scalars from the end, so neither a surrogate pair nor a multibyte
        // sequence is ever cut in half.
        var head = String(s.dropLast(ext.count)).unicodeScalars
        while !head.isEmpty && !fits(String(head) + ext) { head.removeLast() }

        var trimmed = String(head)
        while let last = trimmed.utf16.last, last == 0x2E || last == 0x20 { trimmed = String(decoding: Array(trimmed.utf16.dropLast()), as: UTF16.self) }
        return (trimmed.isEmpty ? "file" : trimmed) + ext
    }

    /// The first of `name`, `name (1).ext`, `name (2).ext`… that `exists` says is free.
    public static func unique(_ name: String, isFolder: Bool, exists: (String) -> Bool) -> String {
        if !exists(name) { return name }

        // extensionOf gives none for ".gitignore": it is a name, not an extension, and " (1).gitignore" would be a worse result.
        let ext = isFolder ? "" : extensionOf(name)
        let stem = String(name.dropLast(ext.count))

        var n = 1
        while true {
            let suffix = " (\(n))" + ext
            // Only reachable past APFS's limit, which A's 200-unit cap never gets to: shorten the stem so the suffix still fits.
            var s = stem.unicodeScalars
            while s.count > 1 && String(s).utf8.count + suffix.utf8.count > maxBytes { s.removeLast() }
            let candidate = String(s) + suffix
            if !exists(candidate) { return candidate }
            n += 1
        }
    }
}
