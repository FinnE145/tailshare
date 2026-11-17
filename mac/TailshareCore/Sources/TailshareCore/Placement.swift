import Foundation

/// Picks collision-free names in the receive folder and claims them. One lock covers all of it so
/// two simultaneous transfers of "a.txt" can't pick the same free name.
enum Placement {
    private static let gate = NSLock()

    private static func exists(_ url: URL) -> Bool { FileManager.default.fileExists(atPath: url.path) }

    /// Claims a free name for a top-level item (empty file or folder) and returns its URL.
    static func reserve(in folder: URL, rawName: String, isFolder: Bool) throws -> URL {
        gate.lock()
        defer { gate.unlock() }

        let name = Names.unique(Names.sanitize(rawName), isFolder: isFolder) { exists(folder.appendingPathComponent($0)) }
        let url = folder.appendingPathComponent(name)
        if isFolder {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: false)
        } else if !FileManager.default.createFile(atPath: url.path, contents: nil) {
            throw CocoaError(.fileWriteUnknown)
        }
        return url
    }

    /// Moves a finished temp file to its final, collision-free name.
    static func placeFile(in folder: URL, rawName: String, from part: URL) throws -> URL {
        gate.lock()
        defer { gate.unlock() }

        let name = Names.unique(Names.sanitize(rawName), isFolder: false) { exists(folder.appendingPathComponent($0)) }
        let url = folder.appendingPathComponent(name)
        try FileManager.default.moveItem(at: part, to: url)
        return url
    }

    static func delete(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }
}
