import XCTest
import ZIPFoundation
@testable import TailshareCore

final class BundleTests: XCTestCase {
    private var dirs: [URL] = []

    private func dir() throws -> URL {
        let d = try makeTempDir()
        dirs.append(d)
        return d
    }

    override func tearDown() {
        dirs.forEach { try? FileManager.default.removeItem(at: $0) }
        dirs = []
    }

    /// A zip with exactly these entries, named as given (nothing sanitizes them on the way in).
    private func zip(_ entries: [(name: String, content: String)]) throws -> URL {
        let url = try dir().appendingPathComponent("b.zip")
        let archive = try Archive(url: url, accessMode: .create)
        for (name, content) in entries {
            let data = Data(content.utf8)
            let type: Entry.EntryType = name.hasSuffix("/") ? .directory : .file
            var pos = 0
            try archive.addEntry(with: name, type: type, uncompressedSize: Int64(data.count), compressionMethod: .deflate) { (_: Int64, count: Int) -> Data in
                defer { pos += count }
                return data.subdata(in: min(pos, data.count)..<min(pos + count, data.count))
            }
        }
        return url
    }

    private enum Item: Equatable, ExpressibleByStringLiteral {
        case dir
        case file(String?)
        init(stringLiteral value: String) { self = .file(value) }
    }

    private func tree(_ root: URL) -> [String: Item] {
        var result: [String: Item] = [:]
        let walker = FileManager.default.enumerator(atPath: root.path)!
        for case let rel as String in walker {
            let url = root.appendingPathComponent(rel)
            let isDir = (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true
            result[rel] = isDir ? .dir : .file(try? String(contentsOf: url, encoding: .utf8))
        }
        return result
    }

    // spec B Tests/Bundles: an entry with .., an absolute path, a drive, \ or : in a segment, an empty or . segment, or one
    // escaping its item's folder fails the transfer and leaves nothing in the receive folder. A good entry comes first, so
    // "nothing left" also proves what had been written was removed.
    func testBadEntriesFailTheWholeTransferAndLeaveNothing() throws {
        let bad = ["../evil.txt", "/abs.txt", "C:/x.txt", "a\\b.txt", "Photos/a:b.txt", "Photos//b.txt", "Photos/./b.txt",
                   "Photos/../../esc.txt", "Photos/../Other/x.txt", "Photos/..", ".", "Photos/s\u{1}ep.txt"]
        for name in bad {
            let receive = try dir()
            let z = try zip([("good.txt", "ok"), ("Photos/ok.jpg", "x"), (name, "evil")])
            XCTAssertThrowsError(try Bundles.extract(zipURL: z, into: receive, totalSize: 1_000_000), "entry \(name)") { error in
                XCTAssertTrue(error is BundleError, "entry \(name): \(error)")
            }
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: receive.path), [], "entry \(name) left files behind")
        }
    }

    // spec B Tests/Bundles: a symbolic link entry fails the transfer (a link written here could point anywhere).
    func testSymlinkEntryIsRejected() throws {
        let url = try dir().appendingPathComponent("l.zip")
        let archive = try Archive(url: url, accessMode: .create)
        let target = Data("/etc/passwd".utf8)
        var pos = 0
        try archive.addEntry(with: "Photos/link", type: .symlink, uncompressedSize: Int64(target.count)) { (_: Int64, count: Int) -> Data in
            defer { pos += count }
            return target.subdata(in: min(pos, target.count)..<min(pos + count, target.count))
        }
        let receive = try dir()
        XCTAssertThrowsError(try Bundles.extract(zipURL: url, into: receive, totalSize: 1000))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: receive.path), [])
    }

    // spec A Bundles (A's code comment): two entries that end up at one path ("a?" and "a_" both sanitize to "a_", or a repeated
    // name in another case) would silently overwrite each other, so the transfer fails and leaves nothing.
    func testTwoEntriesAtOnePathFailTheTransfer() throws {
        for pair in [("Photos/a?.txt", "Photos/a_.txt"), ("Photos/x.txt", "Photos/X.txt"), ("Photos/x.txt", "Photos/x.txt")] {
            let receive = try dir()
            let z = try zip([(pair.0, "one"), (pair.1, "two")])
            XCTAssertThrowsError(try Bundles.extract(zipURL: z, into: receive, totalSize: 1000), "\(pair)")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: receive.path), [], "\(pair)")
        }
    }

    // spec B Sending (departure): a symbolic link inside a sent folder is left out, whatever it points at (a file, or a folder
    // that would otherwise be walked).
    func testSymlinksInsideASentFolderAreLeftOut() throws {
        let src = try dir()
        let outside = try dir()
        try write("secret", to: outside.appendingPathComponent("secret.txt"))
        try write("real", to: src.appendingPathComponent("Folder/real.txt"))
        try FileManager.default.createSymbolicLink(at: src.appendingPathComponent("Folder/link-to-file"), withDestinationURL: outside.appendingPathComponent("secret.txt"))
        try FileManager.default.createSymbolicLink(at: src.appendingPathComponent("Folder/link-to-dir"), withDestinationURL: outside)

        let zipURL = try dir().appendingPathComponent("o.zip")
        let info = try Bundles.create(paths: [src.appendingPathComponent("Folder")], zipURL: zipURL)
        XCTAssertEqual(try Archive(url: zipURL, accessMode: .read).map(\.path).sorted(), ["Folder/", "Folder/real.txt"])
        XCTAssertEqual(info.totalSize, 4)
    }

    // spec A Bundles (A's Verify tests): a zip with no entries fails the transfer, and a bundle that is one empty folder lands as a folder.
    func testEmptyBundleFailsAndALoneEmptyFolderLandsAsAFolder() throws {
        let receive = try dir()
        XCTAssertThrowsError(try Bundles.extract(zipURL: try zip([]), into: receive, totalSize: 100))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: receive.path), [])

        let placed = try Bundles.extract(zipURL: try zip([("Empty/", "")]), into: receive, totalSize: 100)
        XCTAssertEqual(placed.map(\.lastPathComponent), ["Empty"])
        XCTAssertEqual(tree(receive), ["Empty": .dir])
    }

    // spec B Tests/Bundles: extraction past totalSize fails and leaves nothing; exactly totalSize is fine.
    func testExtractionPastTotalSizeFails() throws {
        let content = String(repeating: "x", count: 100)
        let z = try zip([("Photos/a.txt", content)])

        let tooSmall = try dir()
        XCTAssertThrowsError(try Bundles.extract(zipURL: z, into: tooSmall, totalSize: 99))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: tooSmall.path), [])

        let exact = try dir()
        let placed = try Bundles.extract(zipURL: z, into: exact, totalSize: 100)
        XCTAssertEqual(placed.map(\.lastPathComponent), ["Photos"])
    }

    // spec B Tests/Bundles: folder and loose-file bundles land under collision-free top-level names.
    func testTopLevelItemsLandUnderFreeNames() throws {
        let receive = try dir()
        try write("old file", to: receive.appendingPathComponent("a.txt"))
        try write("old photo", to: receive.appendingPathComponent("Photos/old.jpg"))

        let z = try zip([("Photos/", ""), ("Photos/new.jpg", "new photo"), ("a.txt", "new file"), ("b.txt", "bee")])
        let placed = try Bundles.extract(zipURL: z, into: receive, totalSize: 1000)

        XCTAssertEqual(placed.map(\.lastPathComponent).sorted(), ["Photos (1)", "a (1).txt", "b.txt"])
        XCTAssertEqual(tree(receive), [
            "Photos": .dir, "Photos/old.jpg": "old photo",
            "Photos (1)": .dir, "Photos (1)/new.jpg": "new photo",
            "a.txt": "old file", "a (1).txt": "new file", "b.txt": "bee",
        ])
    }

    // spec A Bundles: a zip with no explicit folder entry (only "Photos/a.jpg") still lands as a folder, not a file named Photos.
    func testFolderWithoutAnExplicitDirectoryEntry() throws {
        let receive = try dir()
        let z = try zip([("Photos/a.jpg", "pic"), ("loose.txt", "l")])
        let placed = try Bundles.extract(zipURL: z, into: receive, totalSize: 100)
        XCTAssertEqual(placed.map(\.lastPathComponent), ["Photos", "loose.txt"])
        XCTAssertEqual(tree(receive), ["Photos": .dir, "Photos/a.jpg": "pic", "loose.txt": "l"])
    }

    // spec B Tests/Bundles: a bundle the Mac writes is read back by the Mac's own extraction with the same entries.
    func testBundleWrittenHereIsReadBackWithSameEntries() throws {
        let src = try dir()
        try write("one", to: src.appendingPathComponent("Folder/one.txt"))
        try write("two", to: src.appendingPathComponent("Folder/sub/two.txt"))
        try FileManager.default.createDirectory(at: src.appendingPathComponent("Folder/empty"), withIntermediateDirectories: true)
        try write("loose", to: src.appendingPathComponent("loose.txt"))

        let zipURL = try dir().appendingPathComponent("out.zip")
        let info = try Bundles.create(paths: [src.appendingPathComponent("Folder"), src.appendingPathComponent("loose.txt")], zipURL: zipURL)
        XCTAssertEqual(info, BundleInfo(name: "Folder", items: 2, isFolder: false, totalSize: 3 + 3 + 5))

        let receive = try dir()
        let placed = try Bundles.extract(zipURL: zipURL, into: receive, totalSize: info.totalSize)
        XCTAssertEqual(placed.map(\.lastPathComponent).sorted(), ["Folder", "loose.txt"])
        XCTAssertEqual(tree(receive), [
            "Folder": .dir, "Folder/one.txt": "one", "Folder/sub": .dir, "Folder/sub/two.txt": "two",
            "Folder/empty": .dir, "loose.txt": "loose",
        ])
    }

    // spec B Tests/Bundles (A's wire shape): entries use "/" and each top-level item is at the root; one folder is reported as a folder.
    func testCreatedEntriesUseSlashesAtTheRootAndTwoSameNamedItemsGetOneRootEach() throws {
        let a = try dir(), b = try dir()
        try write("1", to: a.appendingPathComponent("same.txt"))
        try write("2", to: b.appendingPathComponent("same.txt"))
        try write("3", to: a.appendingPathComponent("Dir/in.txt"))

        let zipURL = try dir().appendingPathComponent("o.zip")
        _ = try Bundles.create(paths: [a.appendingPathComponent("same.txt"), b.appendingPathComponent("same.txt")], zipURL: zipURL)
        let names = try Archive(url: zipURL, accessMode: .read).map(\.path).sorted()
        XCTAssertEqual(names, ["same (1).txt", "same.txt"])

        let single = try dir().appendingPathComponent("s.zip")
        let info = try Bundles.create(paths: [a.appendingPathComponent("Dir")], zipURL: single)
        XCTAssertEqual(try Archive(url: single, accessMode: .read).map(\.path).sorted(), ["Dir/", "Dir/in.txt"])
        XCTAssertTrue(info.isFolder)
    }
}
