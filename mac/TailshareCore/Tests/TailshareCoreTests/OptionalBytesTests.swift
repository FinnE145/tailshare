import XCTest
import ZIPFoundation
@testable import TailshareCore

/// Spec E, wire protocol additions: optional `bytes`, estimated size, the hub mark in `hello`.
final class OptionalBytesTests: XCTestCase {
    private var h: Harness!

    override func tearDown() async throws {
        await h?.stop()
        h = nil
    }

    private func offer(_ json: [String: Any]) async throws -> String {
        try h.trustSender()
        let r = try await h.raw("POST", "/v1/offer", json: json)
        XCTAssertEqual(r.status, 200, "\(json)")
        return try XCTUnwrap(h.token(from: r.body))
    }

    /// A PUT with or without a Content-Length: a body stream makes URLSession send it chunked.
    private func put(_ token: String, _ body: Data, chunked: Bool = false) async throws -> Int {
        guard chunked else { return try await h.raw("PUT", "/v1/upload/\(token)", body: body).status }
        var request = URLRequest(url: h.url("/v1/upload/\(token)"))
        request.httpMethod = "PUT"
        request.httpBodyStream = InputStream(data: body)
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (_, response) = try await session.data(for: request)
        return (response as! HTTPURLResponse).statusCode
    }

    /// A zip holding one file of `size` zero bytes (zeros compress to almost nothing, so the zip stays small).
    private func zip(fileSize size: Int) throws -> Data {
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("z.bin")
        try Data(count: size).write(to: file)
        let out = dir.appendingPathComponent("z.zip")
        _ = try Bundles.create(paths: [file], zipURL: out)
        return try Data(contentsOf: out)
    }

    // MARK: Offer shape

    // spec E offer: `bytes` may be omitted or null; `totalSize` may be omitted for text only; file and bundle need it.
    func testOfferValidityWithoutBytesAndTotalSize() async throws {
        h = try await Harness()
        try h.trustSender()
        let accepted: [[String: Any]] = [
            ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false, "totalSize": 5],
            ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false, "totalSize": 5, "bytes": NSNull()],
            ["kind": "text", "items": 1, "isFolder": false],
            ["kind": "bundle", "name": "p", "items": 2, "isFolder": false, "totalSize": 5, "sizeIsEstimate": true],
        ]
        for json in accepted { try await eq(try await h.raw("POST", "/v1/offer", json: json).status, 200, "\(json)") }

        let rejected: [[String: Any]] = [
            ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false],
            ["kind": "bundle", "name": "p", "items": 2, "isFolder": false, "bytes": 5],
            ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false, "totalSize": -1],
        ]
        for json in rejected { try await eq(try await h.raw("POST", "/v1/offer", json: json).status, 400, "\(json)") }
    }

    // spec E offer: the progress total is `bytes`, or `totalSize` when `bytes` is absent.
    func testProgressTotal() {
        XCTAssertEqual(Offer(kind: .file, name: "a", items: 1, isFolder: false, bytes: 7, totalSize: 99).progressTotal, 7)
        XCTAssertEqual(Offer(kind: .file, name: "a", items: 1, isFolder: false, bytes: nil, totalSize: 99).progressTotal, 99)
        XCTAssertEqual(Offer(kind: .text, name: nil, items: 1, isFolder: false, bytes: nil, totalSize: nil).progressTotal, 0)
    }

    // spec E offer: the apps' own senders keep sending `bytes` and never set `sizeIsEstimate`.
    func testSenderStillDeclaresBytes() async throws {
        h = try await Harness()
        h.host.prompt = .accept
        _ = await h.sender().send(to: h.peer, content: .text("hello"))
        let sent = try XCTUnwrap(h.host.prompts.first?.1)
        XCTAssertEqual(sent.bytes, 5)
        XCTAssertNil(sent.sizeIsEstimate)
        let wire = try JSONSerialization.jsonObject(with: JSONEncoder().encode(sent)) as! [String: Any]
        XCTAssertNil(wire["sizeIsEstimate"])
        XCTAssertEqual(wire["bytes"] as? Int, 5)
    }

    // MARK: Upload without a declared length

    // spec E Tests/Optional bytes: an offer without `bytes` is accepted; its upload is taken at whatever length arrives, with Content-Length and chunked.
    func testUploadWithoutBytesTakesAnyLength() async throws {
        h = try await Harness()
        let json: [String: Any] = ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false, "totalSize": 3, "sizeIsEstimate": true]

        // Longer than the estimate, with a Content-Length.
        try await eq(try await put(try await offer(json), Data("0123456789".utf8)), 200)
        XCTAssertEqual(try Data(contentsOf: h.receiveDir.appendingPathComponent("a.txt")), Data("0123456789".utf8))

        // Chunked, with no Content-Length at all.
        try await eq(try await put(try await offer(json), Data("chunked body".utf8), chunked: true), 200)
        XCTAssertEqual(try Data(contentsOf: h.receiveDir.appendingPathComponent("a (1).txt")), Data("chunked body".utf8))
        XCTAssertEqual(h.host.received.count, 2)
    }

    // spec E Tests/Optional bytes: an offer WITH bytes and a different Content-Length is still 400, nothing written (chunked too).
    func testDeclaredBytesStillEnforced() async throws {
        h = try await Harness()
        let json: [String: Any] = ["kind": "file", "name": "a.txt", "items": 1, "isFolder": false, "bytes": 5, "totalSize": 5]
        try await eq(try await put(try await offer(json), Data("abcdefgh".utf8)), 400)
        try await eq(try await put(try await offer(json), Data("abcde".utf8), chunked: true), 400)
        XCTAssertEqual(h.receivedNames, [])
    }

    // spec E Tests/Optional bytes: text without `bytes` is accepted at exactly 1 MiB; one byte more fails with 413 and nothing is kept (either framing).
    func testTextWithoutBytesIsCappedAtOneMiB() async throws {
        h = try await Harness()
        let json: [String: Any] = ["kind": "text", "items": 1, "isFolder": false]
        let limit = 1_048_576

        try await eq(try await put(try await offer(json), Data(repeating: 0x61, count: limit)), 200)
        XCTAssertEqual(h.host.received.compactMap { $0.2.text?.utf8.count }, [limit])

        try await eq(try await put(try await offer(json), Data(repeating: 0x61, count: limit + 1)), 413)
        try await eq(try await put(try await offer(json), Data(repeating: 0x61, count: limit + 1), chunked: true), 413)
        XCTAssertEqual(h.host.received.count, 1)
        XCTAssertEqual(h.receivedNames, [])
    }

    // spec E Tests/Optional bytes: text without `bytes` or `totalSize` is delivered as the text it was.
    func testTextWithoutBytesIsDelivered() async throws {
        h = try await Harness()
        let json: [String: Any] = ["kind": "text", "items": 1, "isFolder": false]
        try await eq(try await put(try await offer(json), Data("héllo wörld".utf8), chunked: true), 200)
        XCTAssertEqual(h.host.received.first?.2.text, "héllo wörld")
        // The first-contact prompt and notice say `text`, as before.
        XCTAssertEqual(h.host.received.first.map { Notices.describe($0.1) }, "text")
    }

    // MARK: Estimated size

    private func bundleOffer(totalSize: Int, estimate: Bool?, bytes: Int? = nil) -> [String: Any] {
        var json: [String: Any] = ["kind": "bundle", "name": "pics", "items": 1, "isFolder": true, "totalSize": totalSize]
        if let estimate { json["sizeIsEstimate"] = estimate }
        if let bytes { json["bytes"] = bytes }
        return json
    }

    // spec E Tests/Estimated size: a flagged bundle that writes more than totalSize but no more than totalSize x 1.25 + 1 MiB is received.
    func testFlaggedBundleWithinTheLooseCapIsReceived() async throws {
        h = try await Harness()
        // Cap for 1,000,000 is 1,250,000 + 1,048,576 = 2,298,576; this writes 2,298,576 exactly.
        let data = try zip(fileSize: 2_298_576)
        try await eq(try await put(try await offer(bundleOffer(totalSize: 1_000_000, estimate: true)), data), 200)
        XCTAssertEqual(h.host.received.count, 1)
        XCTAssertEqual(try Data(contentsOf: h.receiveDir.appendingPathComponent("z.bin")).count, 2_298_576)
    }

    // spec E Tests/Estimated size: a flagged bundle that writes more than that fails, and nothing is left behind.
    func testFlaggedBundleOverTheLooseCapFailsAndLeavesNothing() async throws {
        h = try await Harness()
        let data = try zip(fileSize: 2_298_577)
        try await eq(try await put(try await offer(bundleOffer(totalSize: 1_000_000, estimate: true)), data), 400)
        XCTAssertEqual(h.receivedNames, [])
        XCTAssertEqual(h.host.received.count, 0)
    }

    // spec E Tests/Estimated size: an unflagged bundle (or one flagged false) that writes one byte over totalSize still fails; exactly totalSize is fine.
    func testUnflaggedBundleKeepsTheExactCap() async throws {
        h = try await Harness()
        for estimate in [nil, false] as [Bool?] {
            try await eq(try await put(try await offer(bundleOffer(totalSize: 1000, estimate: estimate)), try zip(fileSize: 1001)), 400, "\(String(describing: estimate))")
        }
        XCTAssertEqual(h.receivedNames, [])
        try await eq(try await put(try await offer(bundleOffer(totalSize: 1000, estimate: nil)), try zip(fileSize: 1000)), 200)
        XCTAssertEqual(h.receivedNames, ["z.bin"])
    }

    // spec E Tests/Estimated size: the loose cap is for a bundle that omits `bytes` too: a missing `bytes` alone lifts nothing.
    func testMissingBytesAloneDoesNotLoosenTheCap() async throws {
        h = try await Harness()
        let json = bundleOffer(totalSize: 1000, estimate: nil)
        XCTAssertNil(json["bytes"])
        try await eq(try await put(try await offer(json), try zip(fileSize: 1500), chunked: true), 400)
        XCTAssertEqual(h.receivedNames, [])
    }

    // MARK: hello and the hub mark

    // spec E hello: an app's answer carries no `hub` field (false); a hub's `hub: true` reads as a hub.
    func testHelloHubMark() async throws {
        h = try await Harness()
        let r = try await h.raw("GET", "/v1/hello")
        let json = try JSONSerialization.jsonObject(with: r.body) as! [String: Any]
        XCTAssertNil(json["hub"])
        XCTAssertEqual(json["protocol"] as? Int, 1)

        let hub = try JSONDecoder().decode(HelloResponse.self, from: Data(#"{"app":"tailshare","protocol":1,"hub":true}"#.utf8))
        XCTAssertTrue(hub.isHub)
        for plain in [#"{"app":"tailshare","protocol":1}"#, #"{"app":"tailshare","protocol":1,"hub":false}"#, #"{"app":"tailshare","protocol":1,"hub":null}"#] {
            XCTAssertFalse(try JSONDecoder().decode(HelloResponse.self, from: Data(plain.utf8)).isHub, plain)
        }
    }
}
