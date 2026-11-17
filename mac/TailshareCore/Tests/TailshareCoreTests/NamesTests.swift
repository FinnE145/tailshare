import XCTest
@testable import TailshareCore

final class NamesTests: XCTestCase {
    private func unique(_ name: String, taken: Set<String>, folder: Bool = false) -> String {
        Names.unique(name, isFolder: folder) { taken.contains($0) }
    }

    // spec B Tests/Names: an existing a.txt yields a (1).txt, and a (2).txt when that is taken too.
    func testCollisionSuffixCounts() {
        XCTAssertEqual(unique("a.txt", taken: ["a.txt"]), "a (1).txt")
        XCTAssertEqual(unique("a.txt", taken: ["a.txt", "a (1).txt"]), "a (2).txt")
        XCTAssertEqual(unique("a.txt", taken: []), "a.txt")
    }

    // spec B Tests/Names: .gitignore yields .gitignore (1), names without an extension and folders follow the same pattern.
    func testCollisionSuffixForDotfilesExtensionlessNamesAndFolders() {
        XCTAssertEqual(unique(".gitignore", taken: [".gitignore"]), ".gitignore (1)")
        XCTAssertEqual(unique("Makefile", taken: ["Makefile"]), "Makefile (1)")
        // A folder's dot is part of its name; only a file has an extension.
        XCTAssertEqual(unique("Photos.2024", taken: ["Photos.2024"], folder: true), "Photos.2024 (1)")
        XCTAssertEqual(unique("Photos", taken: ["Photos", "Photos (1)"], folder: true), "Photos (2)")
        XCTAssertEqual(unique("archive.tar.gz", taken: ["archive.tar.gz"]), "archive.tar (1).gz")
        // A trailing dot is not an extension either.
        XCTAssertEqual(unique("a.", taken: ["a."]), "a. (1)")
    }

    // spec B Tests/Names: path components are stripped, with / and \ both treated as separators.
    func testPathComponentsAreStripped() {
        XCTAssertEqual(Names.sanitize("../../etc/passwd"), "passwd")
        XCTAssertEqual(Names.sanitize("C:\\Users\\x\\a.txt"), "a.txt")
        XCTAssertEqual(Names.sanitize("dir/sub\\b.txt"), "b.txt")
        XCTAssertEqual(Names.sanitize("/abs/c.txt"), "c.txt")
    }

    // spec B Tests/Names: each of < > : " | ? * and a control character is replaced with _ (the slashes are separators, above).
    func testInvalidCharactersAreReplaced() {
        XCTAssertEqual(Names.sanitize("a<b>c:d\"e|f?g*h.txt"), "a_b_c_d_e_f_g_h.txt")
        XCTAssertEqual(Names.sanitize("tab\there"), "tab_here")
        XCTAssertEqual(Names.sanitize("nul\u{0}byte"), "nul_byte")
        XCTAssertEqual(Names.sanitize("ok name (1).txt"), "ok name (1).txt")
    }

    // spec B Tests/Names: CON and con.txt get the _ prefix; names that merely start with a reserved word don't.
    func testReservedNames() {
        XCTAssertEqual(Names.sanitize("CON"), "_CON")
        XCTAssertEqual(Names.sanitize("con.txt"), "_con.txt")
        XCTAssertEqual(Names.sanitize("COM1"), "_COM1")
        XCTAssertEqual(Names.sanitize("LPT9.log"), "_LPT9.log")
        let all = ["CON", "PRN", "AUX", "NUL"] + (1...9).map { "COM\($0)" } + (1...9).map { "LPT\($0)" }
        for name in all {
            XCTAssertEqual(Names.sanitize(name), "_" + name, name)
            XCTAssertEqual(Names.sanitize(name.lowercased() + ".txt"), "_" + name.lowercased() + ".txt", name)
        }
        XCTAssertEqual(Names.sanitize("Console"), "Console")
        XCTAssertEqual(Names.sanitize("COM10"), "COM10")
    }

    // spec B Tests/Names: trailing dots and spaces are trimmed, and an empty name becomes file.
    func testTrimmingAndEmptyNames() {
        XCTAssertEqual(Names.sanitize("name. . "), "name")
        XCTAssertEqual(Names.sanitize("..."), "file")
        XCTAssertEqual(Names.sanitize(""), "file")
        XCTAssertEqual(Names.sanitize(nil), "file")
        XCTAssertEqual(Names.sanitize("dir/"), "file")
    }

    // spec B Tests/Names: a name over 200 characters keeps its extension.
    func testLongNameKeepsExtension() {
        let long = String(repeating: "a", count: 250) + ".txt"
        XCTAssertEqual(Names.sanitize(long), String(repeating: "a", count: 196) + ".txt")
        XCTAssertEqual(Names.sanitize(String(repeating: "b", count: 200)), String(repeating: "b", count: 200))
        XCTAssertEqual(Names.sanitize(String(repeating: "b", count: 201)), String(repeating: "b", count: 200))
    }

    // spec A Names (A's Verify test): when capping, an extension over 20 characters is not kept (it would be most of the name).
    func testLongExtensionIsNotKeptWhenCapping() {
        let name = String(repeating: "a", count: 190) + "." + String(repeating: "x", count: 30)
        XCTAssertEqual(Names.sanitize(name), String(name.prefix(200)).trimmingCharacters(in: CharacterSet(charactersIn: ". ")))
        XCTAssertEqual(Names.sanitize(name).count, 200)
    }

    // spec B Tests/Names (Mac): a name over 255 UTF-8 bytes but under 200 characters is cut to fit without splitting a character.
    func testMultibyteNameIsCutToAPFSByteLimit() {
        let cjk = String(repeating: "日", count: 150) + ".txt"
        XCTAssertEqual(Names.sanitize(cjk), String(repeating: "日", count: 83) + ".txt")

        let emoji = String(repeating: "😀", count: 100)
        XCTAssertEqual(Names.sanitize(emoji), String(repeating: "😀", count: 63))
    }

    // spec B Tests/Names (Mac): the collision suffix still fits in 255 bytes when the name was already at the limit.
    func testCollisionSuffixOnAMaximumLengthName() {
        let name = Names.sanitize(String(repeating: "日", count: 150) + ".txt")
        let result = Names.unique(name, isFolder: false) { $0 == name }
        XCTAssertLessThanOrEqual(result.utf8.count, 255)
        XCTAssertTrue(result.hasSuffix(" (1).txt"))
        XCTAssertTrue(result.hasPrefix("日"))
    }
}
