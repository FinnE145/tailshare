import XCTest
@testable import TailshareCore

final class LinkDetectorTests: XCTestCase {
    // spec B Tests/Link detection: a single https:// URL is a link.
    func testSingleURLIsALink() {
        XCTAssertEqual(LinkDetector.link(in: "https://example.com/a?b=1")?.absoluteString, "https://example.com/a?b=1")
        XCTAssertEqual(LinkDetector.link(in: "  http://example.com  \n")?.host, "example.com")
        XCTAssertNotNil(LinkDetector.link(in: "HTTPS://EXAMPLE.COM"))
    }

    // spec B Tests/Link detection: text containing a URL is text.
    func testTextContainingAURLIsText() {
        XCTAssertNil(LinkDetector.link(in: "look at https://example.com now"))
        XCTAssertNil(LinkDetector.link(in: "https://example.com https://example.org"))
        XCTAssertNil(LinkDetector.link(in: "https://example.com\nsecond line"))
        XCTAssertNil(LinkDetector.link(in: ""))
    }

    // spec B Tests/Link detection: file:, javascript: and other schemes are text, and so are malformed http forms.
    func testOtherSchemesAreText() {
        for t in ["file:///etc/passwd", "javascript:alert(1)", "ftp://example.com", "x-apple.systempreferences:com.apple.preference.security",
                  "tailshare://share?text=1", "https:example.com", "http://", "https://", "example.com"] {
            XCTAssertNil(LinkDetector.link(in: t), t)
        }
    }
}

final class NoticesTests: XCTestCase {
    private func offer(_ kind: OfferKind, name: String? = "photo.jpg", items: Int = 1, folder: Bool = false, size: Int64 = 2_516_582) -> Offer {
        Offer(kind: kind, name: name, items: items, isFolder: folder, bytes: size, totalSize: size)
    }

    // spec B Tests/Notification text: size formatting at the B/KB/MB/GB boundaries (binary units, one decimal).
    func testSizeFormattingBoundaries() {
        let cases: [(Int64, String)] = [
            (0, "0 B"), (512, "512 B"), (1023, "1023 B"), (1024, "1.0 KB"), (1536, "1.5 KB"),
            (1_048_575, "1.0 MB"), (1_048_576, "1.0 MB"), (2_516_582, "2.4 MB"),
            (1_073_741_823, "1.0 GB"), (1_073_741_824, "1.0 GB"), (1_288_490_189, "1.2 GB"),
        ]
        for (bytes, text) in cases { XCTAssertEqual(Notices.formatSize(bytes), text, "\(bytes)") }
    }

    // spec B Tests/Notification text: `and N more` and the folder form, with sanitized names.
    func testDescribeForms() {
        XCTAssertEqual(Notices.describe(offer(.file)), "photo.jpg, 2.4 MB")
        XCTAssertEqual(Notices.describe(offer(.bundle, items: 3)), "photo.jpg and 2 more, 2.4 MB")
        XCTAssertEqual(Notices.describe(offer(.bundle, name: "Photos", folder: true, size: 1_288_490_189)), "Photos (folder), 1.2 GB")
        XCTAssertEqual(Notices.describe(offer(.bundle, name: "Photos")), "Photos, 2.4 MB")
        XCTAssertEqual(Notices.describe(offer(.text, name: nil)), "text")
        // A peer's name is shown in the form that would be written.
        XCTAssertEqual(Notices.describe(offer(.file, name: "../../a\u{7}b.txt")), "a_b.txt, 2.4 MB")
    }

    // spec B Tests/Notification text and Notifications: titles and bodies for first contact, received, text and links.
    func testTitlesAndBodies() {
        XCTAssertEqual(Notices.firstContact(device: "sender-box", offer: offer(.file)), Notice(title: "sender-box", body: "wants to send photo.jpg, 2.4 MB"))
        XCTAssertEqual(Notices.received(device: "sender-box", offer: offer(.file)), Notice(title: "From sender-box", body: "photo.jpg, 2.4 MB"))
        XCTAssertEqual(Notices.receivedText(device: "sender-box", text: "hi there"), Notice(title: "From sender-box", body: "hi there"))
        XCTAssertEqual(Notices.progress(device: "sender-box", offer: offer(.file)), "Receiving photo.jpg, 2.4 MB from sender-box")
        XCTAssertEqual(Notices.sent(to: "d").title, "Sent to d")
        XCTAssertEqual(Notices.declined(by: "d").title, "d declined")
        XCTAssertEqual(Notices.noResponse(from: "d").title, "d didn't respond")
        XCTAssertEqual(Notices.failed(to: "d").title, "Couldn't send to d")
        XCTAssertEqual(Notices.portInUse(45611).title, "Can't receive: port 45611 is in use")
    }

    // spec B Tests/Notification text: text over 200 characters shows its first 200 plus …, never splitting a character.
    func testTextTruncation() {
        XCTAssertEqual(Notices.truncate(String(repeating: "a", count: 200)), String(repeating: "a", count: 200))
        XCTAssertEqual(Notices.truncate(String(repeating: "a", count: 201)), String(repeating: "a", count: 200) + "…")
        // The emoji's two UTF-16 units straddle the 200 cut: it is dropped whole, not cut in half.
        let straddle = String(repeating: "a", count: 199) + "😀" + "tail"
        XCTAssertEqual(Notices.truncate(straddle), String(repeating: "a", count: 199) + "…")
        // One that fits entirely stays.
        let fits = String(repeating: "a", count: 198) + "😀" + "tail"
        XCTAssertEqual(Notices.truncate(fits), String(repeating: "a", count: 198) + "😀…")
    }

    // spec B Notifications: a link is shown in full, however long; other long text is cut.
    func testLinkIsNeverTruncated() {
        let url = "https://example.com/" + String(repeating: "p", count: 300)
        XCTAssertEqual(Notices.receivedText(device: "d", text: url).body, url)
        XCTAssertEqual(Notices.receivedText(device: "d", text: String(repeating: "t", count: 300)).body, String(repeating: "t", count: 200) + "…")
    }
}

final class HandOffTests: XCTestCase {
    // spec B Tests/Hand-off link parsing: file items decode to their paths (spaces, non-ASCII, and characters that are special in a query).
    func testFileItemsRoundTrip() throws {
        let paths = ["/Users/someone/My Photos/été & co/a+b #1 100%.txt", "/tmp/日本語.txt", "/tmp/plain"]
        let url = try XCTUnwrap(HandOff.filesLink(paths))
        XCTAssertEqual(HandOff.parse(url), .files(paths))
        // Parsed from the text form too, as it arrives in the Apple Event.
        XCTAssertEqual(HandOff.parse(try XCTUnwrap(URL(string: url.absoluteString))), .files(paths))
    }

    // spec B Tests/Hand-off link parsing: text=1 is text.
    func testTextLink() throws {
        XCTAssertEqual(HandOff.parse(try XCTUnwrap(HandOff.textLink())), .text)
        XCTAssertEqual(HandOff.parse(URL(string: "tailshare://share?text=1")!), .text)
    }

    // spec B Tests/Hand-off link parsing: a link with both, neither, or another host is rejected.
    func testForeignLinksAreRejected() {
        let rejected = [
            "tailshare://share?text=1&file=/tmp/a",
            "tailshare://share",
            "tailshare://share?",
            "tailshare://other?text=1",
            "tailshare://other?file=/tmp/a",
            "tailshare://share?text=0",
            "tailshare://share?text=",
            "tailshare://share?text=1&text=1",
            "tailshare://share?file=",
            "tailshare://share?file=/tmp/a&extra=1",
            "https://share?text=1",
            "other://share?text=1",
            "tailshare:///share?text=1",
        ]
        for link in rejected { XCTAssertNil(HandOff.parse(URL(string: link)!), link) }
    }

    // spec B Tests/Hand-off link parsing: the sender check accepts only the extension's bundle ID.
    func testOnlyTheExtensionMayHandOff() {
        XCTAssertTrue(HandOff.isFromExtension("io.github.finne145.tailshare.ShareExtension"))
        XCTAssertFalse(HandOff.isFromExtension("io.github.finne145.tailshare"))
        XCTAssertFalse(HandOff.isFromExtension("com.apple.Terminal"))
        XCTAssertFalse(HandOff.isFromExtension("io.github.finne145.tailshare.ShareExtension.evil"))
        XCTAssertFalse(HandOff.isFromExtension(nil))
        XCTAssertFalse(HandOff.isFromExtension(""))
    }
}
