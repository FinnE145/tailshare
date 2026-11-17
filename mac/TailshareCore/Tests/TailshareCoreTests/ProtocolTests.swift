import XCTest
import ZIPFoundation
@testable import TailshareCore

final class ProtocolTests: XCTestCase {
    private var h: Harness!

    override func tearDown() async throws {
        await h?.stop()
        h = nil
    }

    private func put(_ token: String, _ body: Data) async throws -> Int {
        try await h.raw("PUT", "/v1/upload/\(token)", body: body).status
    }

    /// An accepted offer's token, from a trusted sender.
    private func tokenFor(bytes: Int, kind: String = "file", name: String? = "a.txt", totalSize: Int? = nil) async throws -> String {
        try h.trustSender()
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(kind: kind, name: name, bytes: bytes, totalSize: totalSize))
        XCTAssertEqual(r.status, 200)
        return try XCTUnwrap(h.token(from: r.body))
    }

    // MARK: hello

    // spec A Wire protocol: GET /v1/hello answers 200 {"app":"tailshare","protocol":1}.
    func testHello() async throws {
        h = try await Harness()
        let r = try await h.raw("GET", "/v1/hello")
        XCTAssertEqual(r.status, 200)
        let json = try JSONSerialization.jsonObject(with: r.body) as! [String: Any]
        XCTAssertEqual(json["app"] as? String, "tailshare")
        XCTAssertEqual(json["protocol"] as? Int, 1)
    }

    // spec B Tests/Protocol (A's rule, replacing "no check at all" for hello): hello with whois failing gives 403.
    func testHelloWithWhoisFailingIs403() async throws {
        h = try await Harness()
        h.tailscale.whoisTable = [:]
        let r = try await h.raw("GET", "/v1/hello")
        XCTAssertEqual(r.status, 403)
        XCTAssertFalse(String(decoding: r.body, as: UTF8.self).contains("tailshare"))
    }

    // spec A Wire protocol: the device list is the online peers that answer hello with protocol 1, and offline ones are never probed.
    func testDiscoveryListsOnlineTailsharePeersOnly() async throws {
        h = try await Harness()
        let fake = try FakeServer(status: 200, body: #"{"app":"other","protocol":1}"#)
        defer { fake.stop() }
        let wrongVersion = try FakeServer(status: 200, body: #"{"app":"tailshare","protocol":2}"#)
        defer { wrongVersion.stop() }

        let status = TailnetStatus(connected: true, selfIP: "100.64.0.1", peers: [h.peer, PeerInfo(identity: otherIdentity, ip: "127.0.0.1", online: false)])
        try await eq(await h.sender().discover(status), [h.peer])
        // Another app on the port, a newer protocol, and nothing listening at all are all "not tailshare".
        try await eq(await Sender(port: fake.port).discover(status), [])
        try await eq(await Sender(port: wrongVersion.port).discover(status), [])
        try await eq(await Sender(port: freePort()).discover(status), [])
    }

    // MARK: offer and trust

    // spec A offer: a first-time sender is prompted once, with the offer, and Accept adds it to trusted with its machine name.
    func testUnknownDeviceIsPromptedAndAcceptTrustsIt() async throws {
        h = try await Harness()
        h.host.prompt = .accept
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(r.status, 200)
        XCTAssertNotNil(h.token(from: r.body))

        XCTAssertEqual(h.host.prompts.count, 1)
        XCTAssertEqual(h.host.prompts[0].0, senderIdentity)
        XCTAssertEqual(h.host.prompts[0].1.name, "a.txt")
        XCTAssertEqual(h.store.load().value.trusted, [senderIdentity])

        // Now trusted: the next offer goes straight through with no second prompt.
        h.host.prompt = .decline
        let again = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(again.status, 200)
        XCTAssertEqual(h.host.prompts.count, 1)
    }

    // spec A offer: a trusted sender gets a token at once and is never prompted.
    func testTrustedDeviceIsNotPrompted() async throws {
        h = try await Harness()
        try h.trustSender()
        h.host.prompt = .decline
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(r.status, 200)
        XCTAssertEqual(h.host.prompts.count, 0)
    }

    // spec A offer: blocked wins over trusted, and is rejected at once with no prompt.
    func testBlockedBeatsTrustedWithoutPrompt() async throws {
        h = try await Harness(settingsJSON: #"{"trusted":[{"id":"nodeSENDER1","name":"x"}],"blocked":[{"id":"nodeSENDER1","name":"x"}]}"#)
        h.host.prompt = .accept
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(r.status, 403)
        XCTAssertEqual(h.error(from: r.body), "blocked")
        XCTAssertEqual(h.host.prompts.count, 0)
    }

    // spec A offer: Decline answers declined and adds the sender to neither list.
    func testDeclineAddsToNeitherList() async throws {
        h = try await Harness()
        h.host.prompt = .decline
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(r.status, 403)
        XCTAssertEqual(h.error(from: r.body), "declined")
        XCTAssertEqual(h.store.load().value.trusted, [])
        XCTAssertEqual(h.store.load().value.blocked, [])
        XCTAssertFalse(FileManager.default.fileExists(atPath: h.settingsURL.path))
    }

    // spec A offer: an unanswered prompt times out with "timeout" (short injected timeout) and trusts nobody.
    func testPromptTimeout() async throws {
        h = try await Harness(promptTimeout: 0.3)
        h.host.prompt = .wait
        let started = Date()
        let r = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(r.status, 403)
        XCTAssertEqual(h.error(from: r.body), "timeout")
        XCTAssertGreaterThan(Date().timeIntervalSince(started), 0.25)
        XCTAssertEqual(h.store.load().value.trusted, [])
        // The host is told to withdraw its prompt.
        let withdrawn = await waitUntil { self.h.host.withdrawn == 1 }
        XCTAssertTrue(withdrawn)
    }

    // spec A offer: if the sender closes the connection while waiting, the prompt is withdrawn.
    func testSenderHangingUpWithdrawsThePrompt() async throws {
        h = try await Harness()
        h.host.prompt = .wait
        let call = Task { try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5)) }

        let prompted = await waitUntil { self.h.host.prompts.count == 1 }
        XCTAssertTrue(prompted)
        XCTAssertEqual(h.host.withdrawn, 0)
        call.cancel()

        let withdrawn = await waitUntil { self.h.host.withdrawn == 1 }
        XCTAssertTrue(withdrawn, "prompt was still open after the sender hung up")
        XCTAssertEqual(h.store.load().value.trusted, [])
    }

    // spec A offer: settings are read fresh for every offer, so a hand edit applies without a restart.
    func testHandEditApplyToTheNextOffer() async throws {
        h = try await Harness()
        try h.trustSender()
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5)).status, 200)

        try #"{"blocked":[{"id":"nodeSENDER1","name":"x"}]}"#.write(to: h.settingsURL, atomically: true, encoding: .utf8)
        let blocked = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5))
        XCTAssertEqual(blocked.status, 403)
        XCTAssertEqual(h.error(from: blocked.body), "blocked")

        try #"{"trusted":[{"id":"nodeSENDER1","name":"x"}]}"#.write(to: h.settingsURL, atomically: true, encoding: .utf8)
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 5)).status, 200)
    }

    // spec A Settings file: a malformed file prompts everyone, an Accept lets that transfer through, and the file is never overwritten.
    func testMalformedSettingsPromptsAndIsNotOverwritten() async throws {
        h = try await Harness(settingsJSON: "{ this is not json")
        h.host.prompt = .accept
        let offerReply = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 3))
        let token = try XCTUnwrap(h.token(from: offerReply.body))
        XCTAssertEqual(h.host.prompts.count, 1)
        try await eq(try await put(token, Data("abc".utf8)), 200)
        XCTAssertEqual(h.receivedNames, ["a.txt"])

        XCTAssertEqual(try String(contentsOf: h.settingsURL, encoding: .utf8), "{ this is not json")
        // And the next offer prompts again: nothing was remembered.
        _ = try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 3))
        XCTAssertEqual(h.host.prompts.count, 2)
    }

    // spec A offer: a malformed offer is 400 (not JSON, unknown kind, a file with no name, a file with two items, negative sizes).
    func testMalformedOffersAre400() async throws {
        h = try await Harness()
        try h.trustSender()
        let bad: [[String: Any]] = [
            h.offerJSON(kind: "folder", bytes: 1),
            h.offerJSON(kind: "file", name: nil, bytes: 1),
            h.offerJSON(kind: "file", name: "", bytes: 1),
            h.offerJSON(kind: "file", name: "a", items: 2, bytes: 1),
            h.offerJSON(kind: "text", name: nil, items: 2, bytes: 1),
            h.offerJSON(kind: "file", bytes: -1),
            h.offerJSON(kind: "file", items: 0, bytes: 1),
            ["kind": "file", "name": "a"],
        ]
        for json in bad { try await eq(try await h.raw("POST", "/v1/offer", json: json).status, 400, "\(json)") }
        try await eq(try await h.raw("POST", "/v1/offer", body: Data("not json".utf8)).status, 400)
        XCTAssertEqual(h.host.prompts.count, 0)
    }

    // spec A offer (A's Verify test): an offer body over 64 KB is refused as a bad request, not buffered; one just under is read.
    func testOfferBodyOver64KBIs400() async throws {
        h = try await Harness()
        try h.trustSender()
        var big = h.offerJSON(bytes: 5)
        big["padding"] = String(repeating: "x", count: 70_000)
        try await eq(try await h.raw("POST", "/v1/offer", json: big).status, 400)

        var fits = h.offerJSON(bytes: 5)
        fits["padding"] = String(repeating: "x", count: 60_000)
        try await eq(try await h.raw("POST", "/v1/offer", json: fits).status, 200)
    }

    // spec A offer: text over 1 MiB is 413, and exactly 1 MiB is accepted.
    func testTextOverOneMiBIs413() async throws {
        h = try await Harness()
        try h.trustSender()
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(kind: "text", name: nil, bytes: 1_048_577)).status, 413)
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(kind: "text", name: nil, bytes: 1_048_576)).status, 200)
        // The limit is for text only: a file of that size is fine.
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(kind: "file", bytes: 5_000_000)).status, 200)
    }

    // spec B Tests/Protocol (A's rule): whois failing gives 403 on offer and upload too, and nothing is prompted or written.
    func testWhoisFailureIs403Everywhere() async throws {
        h = try await Harness()
        let token = try await tokenFor(bytes: 3)
        h.tailscale.whoisTable = [:]
        try await eq(try await h.raw("POST", "/v1/offer", json: h.offerJSON(bytes: 3)).status, 403)
        try await eq(try await put(token, Data("abc".utf8)), 403)
        XCTAssertEqual(h.host.prompts.count, 0)
        XCTAssertEqual(h.receivedNames, [])
    }

    // MARK: tokens and upload

    // spec A token: single use. The second upload with the same token is 403 and writes nothing more.
    func testTokenIsSingleUse() async throws {
        h = try await Harness()
        let token = try await tokenFor(bytes: 3)
        try await eq(try await put(token, Data("abc".utf8)), 200)
        try await eq(try await put(token, Data("abc".utf8)), 403)
        XCTAssertEqual(h.receivedNames, ["a.txt"])
    }

    // spec A token: bound to the sender's StableID. Another device presenting it gets 403, and the owner can still use it.
    func testTokenIsBoundToItsSender() async throws {
        h = try await Harness()
        let token = try await tokenFor(bytes: 3)

        h.tailscale.whoisTable = ["127.0.0.1": otherIdentity]
        try await eq(try await put(token, Data("abc".utf8)), 403)
        XCTAssertEqual(h.receivedNames, [])

        h.tailscale.whoisTable = ["127.0.0.1": senderIdentity]
        try await eq(try await put(token, Data("abc".utf8)), 200)
    }

    // spec A token: expires after its lifetime (short injected one) if no upload starts.
    func testTokenExpires() async throws {
        h = try await Harness(tokenLifetime: 0.3)
        let token = try await tokenFor(bytes: 3)
        try await Task.sleep(nanoseconds: 500_000_000)
        try await eq(try await put(token, Data("abc".utf8)), 403)
        XCTAssertEqual(h.receivedNames, [])
    }

    // spec A token: an unknown or made-up token is 403.
    func testUnknownTokenIs403() async throws {
        h = try await Harness()
        try h.trustSender()
        try await eq(try await put("AAAAAAAAAAAAAAAAAAAAAA", Data("abc".utf8)), 403)
    }

    // spec A token: 128 random bits, base64url (22 characters, no padding), different every time.
    func testTokenShape() async throws {
        h = try await Harness()
        let a = try await tokenFor(bytes: 3)
        let b = try await tokenFor(bytes: 3)
        XCTAssertNotEqual(a, b)
        for t in [a, b] {
            XCTAssertEqual(t.count, 22)
            XCTAssertNil(t.rangeOfCharacter(from: CharacterSet(charactersIn: "+/=").union(.whitespaces)))
        }
    }

    // spec A upload: Content-Length not matching bytes is rejected with no file left (shorter, longer).
    func testContentLengthMismatchIsRejectedWithNoFileLeft() async throws {
        h = try await Harness()
        for body in ["abc", "abcdefghij"] {
            let token = try await tokenFor(bytes: 5)
            try await eq(try await put(token, Data(body.utf8)), 400, body)
            XCTAssertEqual(h.receivedNames, [], body)
        }
    }

    // spec A upload: a connection dropped mid-body leaves no temp file and no received file, and the host is told the transfer ended.
    func testDroppedUploadLeavesNothing() async throws {
        h = try await Harness()
        let token = try await tokenFor(bytes: 50_000_000)

        // Claim 50 MB, send a little, then go away.
        let session = URLSession(configuration: .ephemeral)
        var request = URLRequest(url: h.url("/v1/upload/\(token)"))
        request.httpMethod = "PUT"
        let stream = InputStream(data: Data(repeating: 7, count: 1_000_000))
        request.httpBodyStream = stream
        request.setValue("50000000", forHTTPHeaderField: "Content-Length")
        let task = Task { try? await session.data(for: request) }
        _ = await waitUntil { self.h.host.incoming.count == 1 }
        try await Task.sleep(nanoseconds: 200_000_000)
        task.cancel()
        session.invalidateAndCancel()

        let ended = await waitUntil { self.h.host.incoming.first?.ended == true }
        XCTAssertTrue(ended)
        XCTAssertEqual(h.receivedNames, [])
        XCTAssertEqual(h.host.received.count, 0)
    }

    // spec A upload: a receiver-side Cancel deletes the temp file, the sender sees a failure, and nothing is reported received.
    func testReceiverSideCancel() async throws {
        h = try await Harness()
        try h.trustSender()
        h.host.cancelIncoming = true
        let file = try makeTempDir().appendingPathComponent("big.bin")
        try Data(repeating: 1, count: 2_000_000).write(to: file)

        let outcome = await h.sender().send(to: h.peer, content: .paths([file]))
        XCTAssertEqual(outcome, .failed)
        XCTAssertEqual(h.receivedNames, [])
        XCTAssertEqual(h.host.received.count, 0)
        XCTAssertEqual(h.host.incoming.first?.ended, true)
    }

    // MARK: sender outcomes

    // spec B Tests/Protocol: declined → declined, timed out → timeout (short injected timeout), blocked → blocked, as the sender reports them;
    // a blocked reply is shown as declined, the same outcome as a declined one (A's rule).
    func testSenderOutcomes() async throws {
        h = try await Harness(promptTimeout: 0.3)

        h.host.prompt = .decline
        let declined = await h.sender().send(to: h.peer, content: .text("x"))
        XCTAssertEqual(declined, .declined)

        h.host.prompt = .wait
        let timedOut = await h.sender().send(to: h.peer, content: .text("x"))
        XCTAssertEqual(timedOut, .didntRespond)

        try #"{"blocked":[{"id":"nodeSENDER1","name":"x"}]}"#.write(to: h.settingsURL, atomically: true, encoding: .utf8)
        h.host.prompt = .accept
        let blocked = await h.sender().send(to: h.peer, content: .text("x"))
        XCTAssertEqual(blocked, .declined)
        XCTAssertEqual(h.host.received.count, 0)
    }

    // spec B Sending: the offer has its own timeout on the sender's side (injected short one) and reports didn't respond.
    func testSenderOfferTimeout() async throws {
        h = try await Harness()
        h.host.prompt = .wait
        let outcome = await h.sender(offerTimeout: 0.3).send(to: h.peer, content: .text("x"))
        XCTAssertEqual(outcome, .didntRespond)
    }

    // spec B Sending: Cancel while waiting for the answer ends the send as Cancelled and the receiver's prompt is withdrawn.
    func testCancelWhileWaiting() async throws {
        h = try await Harness()
        h.host.prompt = .wait
        let sender = h.sender()
        let task = Task { await sender.send(to: h.peer, content: .text("x")) }
        _ = await waitUntil { self.h.host.prompts.count == 1 }
        task.cancel()
        try await eq(await task.value, .cancelled)
        let withdrawn = await waitUntil { self.h.host.withdrawn == 1 }
        XCTAssertTrue(withdrawn)
    }

    // spec B Sending: a peer that can't be reached, or that answers 200 with something that isn't JSON, is a failed send, not a crash.
    func testUnreachableAndGarbageRepliesFail() async throws {
        let peer = PeerInfo(identity: otherIdentity, ip: "127.0.0.1", online: true)
        try await eq(await Sender(port: freePort()).send(to: peer, content: .text("x")), .failed)

        let fake = try FakeServer(status: 200, body: "<html>not json</html>")
        defer { fake.stop() }
        try await eq(await Sender(port: fake.port).send(to: peer, content: .text("x")), .failed)

        let noToken = try FakeServer(status: 200, body: #"{"token":""}"#)
        defer { noToken.stop() }
        try await eq(await Sender(port: noToken.port).send(to: peer, content: .text("x")), .failed)

        let odd403 = try FakeServer(status: 403, body: "nope")
        defer { odd403.stop() }
        try await eq(await Sender(port: odd403.port).send(to: peer, content: .text("x")), .failed)
    }

    // spec B Sending: a path that no longer exists is a failed send.
    func testMissingFileFails() async throws {
        h = try await Harness()
        try h.trustSender()
        let gone = FileManager.default.temporaryDirectory.appendingPathComponent("tailshare-gone-\(UUID().uuidString)")
        try await eq(await h.sender().send(to: h.peer, content: .paths([gone])), .failed)
        try await eq(await h.sender().send(to: h.peer, content: .paths([gone, gone])), .failed)
    }

    // MARK: sender kind

    // spec B Tests/Sender kind: one file gives file; two files, a folder, or a mix gives bundle; text gives text.
    func testSenderKind() async throws {
        h = try await Harness()
        try h.trustSender()
        let src = try makeTempDir()
        try write("one", to: src.appendingPathComponent("one.txt"))
        try write("two", to: src.appendingPathComponent("two.txt"))
        try write("in", to: src.appendingPathComponent("Folder/in.txt"))
        let s = h.sender()
        let peer = h.peer

        let cases: [(SendContent, OfferKind, Int, Bool)] = [
            (.paths([src.appendingPathComponent("one.txt")]), .file, 1, false),
            (.paths([src.appendingPathComponent("one.txt"), src.appendingPathComponent("two.txt")]), .bundle, 2, false),
            (.paths([src.appendingPathComponent("Folder")]), .bundle, 1, true),
            (.paths([src.appendingPathComponent("one.txt"), src.appendingPathComponent("Folder")]), .bundle, 2, false),
            (.text("hello"), .text, 1, false),
        ]
        for (content, kind, items, folder) in cases {
            let before = h.host.received.count
            let outcome = await s.send(to: peer, content: content)
            XCTAssertEqual(outcome, .sent, "\(kind)")
            let offer = try XCTUnwrap(h.host.received.dropFirst(before).first).1
            XCTAssertEqual(offer.kind, kind)
            XCTAssertEqual(offer.items, items, "\(kind)")
            XCTAssertEqual(offer.isFolder, folder, "\(kind)")
        }
    }

    // MARK: round trips

    // spec A/B Tests/Protocol: a successful file round trip: content, name, size, progress reports, and host calls in order.
    func testFileRoundTrip() async throws {
        h = try await Harness()
        try h.trustSender()
        let src = try makeTempDir().appendingPathComponent("photo (final).jpg")
        let content = Data((0..<3_000_000).map { UInt8($0 % 251) })
        try content.write(to: src)

        let reports = ReportBox()
        let outcome = await h.sender().send(to: h.peer, content: .paths([src])) { reports.add($0) }
        XCTAssertEqual(outcome, .sent)

        XCTAssertEqual(h.receivedNames, ["photo (final).jpg"])
        XCTAssertEqual(try Data(contentsOf: h.receiveDir.appendingPathComponent("photo (final).jpg")), content)

        let (from, offer, item) = try XCTUnwrap(h.host.received.first)
        XCTAssertEqual(from, senderIdentity)
        XCTAssertEqual(offer, Offer(kind: .file, name: "photo (final).jpg", items: 1, isFolder: false, bytes: 3_000_000, totalSize: 3_000_000))
        XCTAssertEqual(item.urls.map(\.lastPathComponent), ["photo (final).jpg"])
        XCTAssertNil(item.text)

        let transfer = try XCTUnwrap(h.host.incoming.first)
        XCTAssertTrue(transfer.ended)
        XCTAssertEqual(transfer.progressReports.last, 3_000_000)
        XCTAssertEqual(reports.all.first?.stage, .waiting)
        XCTAssertEqual(reports.all.last?.stage, .sending)
        XCTAssertEqual(reports.all.last?.bytesSent, 3_000_000)
        XCTAssertEqual(reports.all.last?.bytesTotal, 3_000_000)

        // A second send of the same name gets the next free one, and the first is untouched.
        _ = await h.sender().send(to: h.peer, content: .paths([src]))
        XCTAssertEqual(h.receivedNames, ["photo (final) (1).jpg", "photo (final).jpg"])
    }

    // spec A/B Tests/Protocol: a successful bundle round trip, folder and loose file, with their contents.
    func testBundleRoundTrip() async throws {
        h = try await Harness()
        try h.trustSender()
        let src = try makeTempDir()
        try write("alpha", to: src.appendingPathComponent("Album/a.txt"))
        try write("beta", to: src.appendingPathComponent("Album/deep/b.txt"))
        try write("loose", to: src.appendingPathComponent("loose.txt"))

        let outcome = await h.sender().send(to: h.peer, content: .paths([src.appendingPathComponent("Album"), src.appendingPathComponent("loose.txt")]))
        XCTAssertEqual(outcome, .sent)

        XCTAssertEqual(h.receivedNames, ["Album", "loose.txt"])
        XCTAssertEqual(try String(contentsOf: h.receiveDir.appendingPathComponent("Album/a.txt"), encoding: .utf8), "alpha")
        XCTAssertEqual(try String(contentsOf: h.receiveDir.appendingPathComponent("Album/deep/b.txt"), encoding: .utf8), "beta")
        XCTAssertEqual(try String(contentsOf: h.receiveDir.appendingPathComponent("loose.txt"), encoding: .utf8), "loose")

        let (_, offer, item) = try XCTUnwrap(h.host.received.first)
        XCTAssertEqual(offer.kind, .bundle)
        XCTAssertEqual(offer.name, "Album")
        XCTAssertEqual(offer.totalSize, 5 + 4 + 5)
        XCTAssertEqual(item.urls.map(\.lastPathComponent), ["Album", "loose.txt"])
        // The temp zip and the .part file are gone on both sides.
        XCTAssertEqual(h.receivedNames.filter { $0.hasPrefix(".") }, [])
    }

    // spec A Receiving/Finishing: a bundle with an entry that escapes (here after a good one) is rejected as bad data (400),
    // nothing is left in the receive folder, nothing is reported received, and the host is told the transfer ended.
    func testHostileBundleIsRejectedByTheReceiver() async throws {
        h = try await Harness()
        try h.trustSender()

        let zipURL = try makeTempDir().appendingPathComponent("evil.zip")
        let archive = try Archive(url: zipURL, accessMode: .create)
        for (name, content) in [("good.txt", "ok"), ("../escape.txt", "evil")] {
            let data = Data(content.utf8)
            var pos = 0
            try archive.addEntry(with: name, type: .file, uncompressedSize: Int64(data.count)) { (_: Int64, count: Int) -> Data in
                defer { pos += count }
                return data.subdata(in: min(pos, data.count)..<min(pos + count, data.count))
            }
        }
        let zip = try Data(contentsOf: zipURL)

        let offer = try await h.raw("POST", "/v1/offer", json: h.offerJSON(kind: "bundle", name: "good.txt", items: 2, bytes: zip.count, totalSize: 100))
        let token = try XCTUnwrap(h.token(from: offer.body))
        try await eq(try await put(token, zip), 400)
        XCTAssertEqual(h.receivedNames, [])
        XCTAssertEqual(h.host.received.count, 0)
        XCTAssertEqual(h.host.incoming.first?.ended, true)
        XCTAssertFalse(FileManager.default.fileExists(atPath: h.root.appendingPathComponent("escape.txt").path))
    }

    // spec A/B Tests/Protocol: a successful text round trip, including non-ASCII, with nothing written to disk.
    func testTextRoundTrip() async throws {
        h = try await Harness()
        try h.trustSender()
        let text = "héllo 日本語 😀\nsecond line"
        try await eq(await h.sender().send(to: h.peer, content: .text(text)), .sent)

        let (_, offer, item) = try XCTUnwrap(h.host.received.first)
        XCTAssertEqual(item.text, text)
        XCTAssertEqual(offer.kind, .text)
        XCTAssertEqual(offer.bytes, Int64(text.utf8.count))
        XCTAssertNil(offer.name)
        XCTAssertEqual(h.receivedNames, [])
        XCTAssertEqual(h.host.incoming.count, 0)
    }

    // spec A upload: text bytes that aren't UTF-8 are rejected, not shown mangled.
    func testInvalidUTF8TextIsRejected() async throws {
        h = try await Harness()
        let token = try await tokenFor(bytes: 3, kind: "text", name: nil)
        try await eq(try await put(token, Data([0xFF, 0xFE, 0x41])), 400)
        XCTAssertEqual(h.host.received.count, 0)
    }

    // spec B Tests/Receive folder: unset, null, set, and set to a missing folder (falls back to the default).
    func testReceiveFolderChoice() async throws {
        let elsewhere = try makeTempDir()
        let missing = elsewhere.appendingPathComponent("not-there")
        let cases: [(String?, URL?)] = [
            (nil, nil),
            (#"{"receiveFolder": null}"#, nil),
            (#"{"receiveFolder": "\#(elsewhere.path)"}"#, elsewhere),
            (#"{"receiveFolder": "\#(missing.path)"}"#, nil),
            (#"{"receiveFolder": ""}"#, nil),
        ]
        for (json, expected) in cases {
            h = try await Harness(settingsJSON: json.map { String($0.dropLast()) + #","trusted":[{"id":"nodeSENDER1","name":"x"}]}"# } ?? #"{"trusted":[{"id":"nodeSENDER1","name":"x"}]}"#)
            // The trusted entry above is appended to each case's JSON, except where the case is the bare default.
            let outcome = await h.sender().send(to: h.peer, content: .paths([try { let f = try makeTempDir().appendingPathComponent("f.txt"); try write("x", to: f); return f }()]))
            XCTAssertEqual(outcome, .sent, json ?? "unset")
            let dir = expected ?? h.receiveDir
            XCTAssertTrue(FileManager.default.fileExists(atPath: dir.appendingPathComponent("f.txt").path), json ?? "unset")
            if expected != nil { XCTAssertEqual(h.receivedNames, [], json ?? "unset") }
            try? FileManager.default.removeItem(at: dir.appendingPathComponent("f.txt"))
            await h.stop()
        }
        h = nil
    }
}

final class ReportBox: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [SendProgress] = []
    func add(_ p: SendProgress) { lock.withLock { items.append(p) } }
    var all: [SendProgress] { lock.withLock { items } }
}
