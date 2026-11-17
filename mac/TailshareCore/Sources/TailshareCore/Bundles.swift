import Foundation
import ZIPFoundation

public struct BundleInfo: Equatable, Sendable {
    public var name: String
    public var items: Int
    public var isFolder: Bool
    public var totalSize: Int64
}

public struct BundleError: Error, Equatable {
    public var reason: String
}

/// Zip creation for the sender and extraction for the receiver.
public enum Bundles {
    private static let chunk = 64 * 1024

    /// Writes `paths` into a zip at `zipURL`, each top-level item at the root. Entries use "/" so the
    /// Mac and Windows apps read each other's zips. Symbolic links inside a folder are left out: a link
    /// followed on one machine is a file the other never asked for.
    public static func create(paths: [URL], zipURL: URL, isCancelled: () -> Bool = { false }) throws -> BundleInfo {
        let fm = FileManager.default
        var taken = Set<String>()
        var total: Int64 = 0
        var firstName: String?
        var firstIsFolder = false

        try? fm.removeItem(at: zipURL)
        let archive = try Archive(url: zipURL, accessMode: .create)

        for path in paths {
            if isCancelled() { throw CancellationError() }
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: path.path, isDirectory: &isDir) else { throw CocoaError(.fileNoSuchFile) }
            let isFolder = isDir.boolValue
            let original = path.standardizedFileURL.lastPathComponent
            // Two picked items can share a name (a.txt from two folders); one root entry per name.
            let top = Names.unique(original, isFolder: isFolder) { taken.contains($0.lowercased()) }
            taken.insert(top.lowercased())

            if firstName == nil { firstName = top; firstIsFolder = isFolder }

            if isFolder {
                try addDirectory(archive, entryName: top + "/")
                // Relative paths come straight from the enumerator: building them by slicing absolute
                // paths broke when /var and /private/var spelled the same folder differently.
                let walker = fm.enumerator(atPath: path.path)
                while let rel = walker?.nextObject() as? String {
                    if isCancelled() { throw CancellationError() }
                    let full = path.appendingPathComponent(rel)
                    let type = try fm.attributesOfItem(atPath: full.path)[.type] as? FileAttributeType
                    if type == .typeSymbolicLink { walker?.skipDescendants(); continue }

                    if type == .typeDirectory {
                        try addDirectory(archive, entryName: "\(top)/\(rel)/")
                    } else {
                        total += try addFile(archive, file: full, entryName: "\(top)/\(rel)", isCancelled: isCancelled)
                    }
                }
            } else {
                total += try addFile(archive, file: path, entryName: top, isCancelled: isCancelled)
            }
        }

        return BundleInfo(name: firstName ?? "", items: paths.count, isFolder: paths.count == 1 && firstIsFolder, totalSize: total)
    }

    private static func addDirectory(_ archive: Archive, entryName: String) throws {
        try archive.addEntry(with: entryName, type: .directory, uncompressedSize: 0) { (_: Int64, _: Int) -> Data in Data() }
    }

    private static func addFile(_ archive: Archive, file: URL, entryName: String, isCancelled: () -> Bool) throws -> Int64 {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        let size = Int64(try handle.seekToEnd())
        try handle.seek(toOffset: 0)

        // Deflate at ZIPFoundation's default level. A's "Fastest" is a Windows setting; any deflate level is valid on the wire.
        try archive.addEntry(with: entryName, type: .file, uncompressedSize: size, compressionMethod: .deflate) { _, count in
            if isCancelled() { throw CancellationError() }
            return try handle.read(upToCount: count) ?? Data()
        }
        return size
    }

    /// Extracts `zipURL` into `receiveFolder`, each top-level item under its own collision-free name,
    /// and returns those URLs. Any bad entry, or more output than `totalSize`, throws `BundleError` and
    /// leaves nothing behind.
    public static func extract(zipURL: URL, into receiveFolder: URL, totalSize: Int64, isCancelled: () -> Bool = { false }) throws -> [URL] {
        let archive: Archive
        do { archive = try Archive(url: zipURL, accessMode: .read) } catch { throw BundleError(reason: "not a zip") }

        // Validate every entry before writing any, so an escape attempt never touches the disk.
        var layout: [(entry: Entry, segments: [String], isDir: Bool)] = []
        for entry in archive {
            if entry.type == .symlink { throw BundleError(reason: "link in bundle") }
            let isDir = entry.path.hasSuffix("/")
            layout.append((entry, try splitEntryName(entry.path, isDir: isDir), isDir))
        }
        if layout.isEmpty { throw BundleError(reason: "empty bundle") }

        // A top-level item is a folder if anything sits beneath it or it is itself a directory entry.
        var topOrder: [String] = []
        var topIsFolder: [String: Bool] = [:]
        for (_, segments, isDir) in layout {
            if topIsFolder[segments[0]] == nil { topOrder.append(segments[0]) }
            topIsFolder[segments[0], default: false] = topIsFolder[segments[0], default: false] || isDir || segments.count > 1
        }

        var placed: [String: URL] = [:]
        var created: [URL] = []
        do {
            for name in topOrder {
                let url = try Placement.reserve(in: receiveFolder, rawName: name, isFolder: topIsFolder[name]!)
                created.append(url)
                placed[name] = url
            }

            var written: Int64 = 0
            var writtenFiles = Set<String>()
            let fm = FileManager.default
            for (entry, segments, isDir) in layout {
                if isCancelled() { throw CancellationError() }
                let top = placed[segments[0]]!
                let inner = segments.dropFirst().map(Names.sanitize)
                var target = top
                for part in inner { target.appendPathComponent(part) }
                target = target.standardizedFileURL

                // Belt and braces after the name checks: nothing may resolve outside its own item.
                if !inner.isEmpty && !target.path.hasPrefix(top.standardizedFileURL.path + "/") {
                    throw BundleError(reason: "entry escapes its folder")
                }

                if isDir {
                    try fm.createDirectory(at: target, withIntermediateDirectories: true)
                    continue
                }
                if !inner.isEmpty { try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true) }

                // Two entries that end up at one path (a repeated name, or "a?" and "a_" after
                // sanitizing) would silently overwrite each other. macOS's default volume ignores
                // case, so the check does too.
                if !writtenFiles.insert(target.path.lowercased()).inserted { throw BundleError(reason: "two entries share a path") }

                // A loose top-level file was reserved as an empty file, so this truncates it.
                guard fm.createFile(atPath: target.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
                let handle = try FileHandle(forWritingTo: target)
                defer { try? handle.close() }
                _ = try archive.extract(entry, bufferSize: chunk) { data in
                    written += Int64(data.count)
                    // The declared sizes inside a zip can lie; count what is actually written.
                    if written > totalSize { throw BundleError(reason: "bundle is larger than offered") }
                    if isCancelled() { throw CancellationError() }
                    try handle.write(contentsOf: data)
                }
            }
            return created
        } catch {
            for url in created { Placement.delete(url) }
            throw error
        }
    }

    private static func splitEntryName(_ fullName: String, isDir: Bool) throws -> [String] {
        if fullName.isEmpty || fullName.contains("\\") { throw BundleError(reason: "bad entry name") }

        let segments = (isDir ? String(fullName.dropLast()) : fullName).split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        for s in segments {
            // An empty segment also rejects a leading "/" (an absolute path).
            // ':' covers drive letters and NTFS alternate data streams.
            if s.isEmpty || s == "." || s == ".." || s.contains(":") || s.unicodeScalars.contains(where: isControl) {
                throw BundleError(reason: "bad entry name")
            }
        }
        return segments
    }

    private static func isControl(_ u: Unicode.Scalar) -> Bool { u.value < 0x20 || (0x7F...0x9F).contains(u.value) }
}
