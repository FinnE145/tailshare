import XCTest
import ZIPFoundation
@testable import TailshareCore

/// Spec E, pickup: the computer holds an item for one phone or iPad and serves it to that device only.
final class PickupTests: XCTestCase {
    private var h: Harness!
    private var holds: PickupHolds { h.receiver.o.holds }

    /// The phone the sender fixture stands for: whois answers `senderIdentity` for the test's loopback caller.
    private let phone = PeerInfo(identity: senderIdentity, ip: "", online: true, isMobile: true)

    override func tearDown() async throws {
        await h?.stop()
        h = nil
    }

    private func setUpHarness() async throws { h = try await Harness() }

    /// A GET with its headers, which `Harness.raw` doesn't return.
    private func get(_ path: String) async throws -> (status: Int, body: Data, headers: [String: String]) {
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (data, response) = try await session.data(from: h.url(path))
        let http = response as! HTTPURLResponse
        var headers: [String: String] = [:]
        for (k, v) in http.allHeaderFields { headers["\(k)".lowercased()] = "\(v)" }
        return (http.statusCode, data, headers)
    }

    private func listing() async throws -> [PickupHolds.Listing] {
        let r = try await get("/v1/pickup")
        XCTAssertEqual(r.status, 200)
        return try JSONDecoder().decode([PickupHolds.Listing].self, from: r.body)
    }

    /// Holds in a task and waits until the item shows in the caller's list.
    private func startHold(_ content: SendContent, for target: PeerInfo? = nil, lifetime: TimeInterval = 60,
                           progress: (@Sendable (SendProgress) -> Void)? = nil, expecting count: Int) async throws -> Task<SendOutcome, Never> {
        let target = target ?? phone
        let holds = self.holds
        let task = Task { await holds.hold(content, for: target, lifetime: lifetime, progress: progress) }
        let listed = await waitUntil { holds.listing(for: target.identity.id).count == count }
        XCTAssertTrue(listed)
        return task
    }

    /// The first listed item; a failed lookup is a failed test, not a crash.
    private func firstItem() async throws -> PickupHolds.Listing {
        let all = try await listing()
        return try XCTUnwrap(all.first)
    }

    private func tempFile(_ name: String, _ content: String) throws -> URL {
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent(name)
        try write(content, to: url)
        return url
    }

    private func tempZips() -> Set<String> {
        Set(((try? FileManager.default.contentsOfDirectory(atPath: FileManager.default.temporaryDirectory.path)) ?? [])
            .filter { $0.hasPrefix("tailshare-") && $0.hasSuffix(".zip") })
    }

    // MARK: Listing

    // spec E Tests/Pickup: GET /v1/pickup lists only the caller's items, oldest first; another device gets [].
    func testListIsTheCallersOwnOldestFirst() async throws {
        try await setUpHarness()
        // Names run against the order they were held in, so a list sorted by name or token would be wrong.
        let first = try tempFile("b.txt", "bbb")
        let second = try tempFile("a.txt", "aaaa")
        let t1 = try await startHold(.paths([first]), expecting: 1)
        let t2 = try await startHold(.paths([second]), expecting: 2)
        let other = PeerInfo(identity: otherIdentity, ip: "", online: true, isMobile: true)
        let t3 = try await startHold(.text("not yours"), for: other, expecting: 1)

        let mine = try await listing()
        XCTAssertEqual(mine.map(\.name), ["b.txt", "a.txt"])
        XCTAssertEqual(mine.count, 2)
        XCTAssertEqual(mine.map(\.bytes), [3, 4])
        XCTAssertEqual(mine.map(\.kind), [.file, .file])

        h.tailscale.whoisTable = ["127.0.0.1": otherIdentity]
        let theirs = try await listing()
        XCTAssertEqual(theirs.map(\.kind), [.text])
        XCTAssertEqual(theirs.map(\.name), [nil])
        XCTAssertEqual(theirs.map(\.bytes), [9])

        h.tailscale.whoisTable = ["127.0.0.1": Identity(id: "nodeNOBODY1", name: "nobody")]
        let none = try await listing()
        XCTAssertEqual(none, [])
        for t in [t1, t2, t3] { t.cancel() }
    }

    // spec E Tests/Pickup: whois failing is 403 on the list and the fetch, and the item stays held.
    func testWhoisFailureIs403() async throws {
        try await setUpHarness()
        let t = try await startHold(.text("secret"), expecting: 1)
        let token = try await firstItem().token

        h.tailscale.whoisTable = [:]
        try await eq(try await get("/v1/pickup").status, 403)
        try await eq(try await get("/v1/pickup/\(token)").status, 403)

        h.tailscale.whoisTable = ["127.0.0.1": senderIdentity]
        try await eq(try await get("/v1/pickup/\(token)").status, 200)
        let outcome = await t.value
        XCTAssertEqual(outcome, .pickedUp)
    }

    // MARK: Tokens

    // spec E Tests/Pickup: another StableID presenting the token gets 403 and doesn't burn it; the second use by the owner is 403 too.
    func testTokenIsBoundSingleUseAndNotBurnedByStrangers() async throws {
        try await setUpHarness()
        let t = try await startHold(.text("hi"), expecting: 1)
        let token = try await firstItem().token
        XCTAssertEqual(token.count, 22)

        h.tailscale.whoisTable = ["127.0.0.1": otherIdentity]
        try await eq(try await get("/v1/pickup/\(token)").status, 403)

        h.tailscale.whoisTable = ["127.0.0.1": senderIdentity]
        let ok = try await get("/v1/pickup/\(token)")
        XCTAssertEqual(ok.status, 200)
        XCTAssertEqual(ok.body, Data("hi".utf8))
        try await eq(try await get("/v1/pickup/\(token)").status, 403)
        let outcome = await t.value
        XCTAssertEqual(outcome, .pickedUp)
        try await eq(try await listing(), [])
    }

    // spec E Tests/Pickup: an unknown token is 403.
    func testUnknownTokenIs403() async throws {
        try await setUpHarness()
        let t = try await startHold(.text("hi"), expecting: 1)
        try await eq(try await get("/v1/pickup/AAAAAAAAAAAAAAAAAAAAAA").status, 403)
        t.cancel()
    }

    // MARK: Ending a hold

    // spec E Tests/Pickup: after the hold time (an injected short one) the token is 403, the item is gone and the sender's outcome is not picked up.
    func testExpiry() async throws {
        try await setUpHarness()
        let t = try await startHold(.text("late"), lifetime: 0.3, expecting: 1)
        let token = try await firstItem().token

        let outcome = await t.value
        XCTAssertEqual(outcome, .notPickedUp)
        try await eq(try await get("/v1/pickup/\(token)").status, 403)
        try await eq(try await listing(), [])
    }

    // spec E Tests/Pickup: cancel removes the item and its token, and a bundle's temp zip is deleted.
    func testCancelRemovesItemTokenAndTempZip() async throws {
        try await setUpHarness()
        let before = tempZips()
        let a = try tempFile("a.txt", "a"), b = try tempFile("b.txt", "b")
        let t = try await startHold(.paths([a, b]), expecting: 1)
        let token = try await firstItem().token
        XCTAssertEqual(tempZips().subtracting(before).count, 1)

        t.cancel()
        let outcome = await t.value
        XCTAssertEqual(outcome, .cancelled)
        try await eq(try await get("/v1/pickup/\(token)").status, 403)
        try await eq(try await listing(), [])
        XCTAssertEqual(tempZips().subtracting(before).count, 0)
    }

    // spec E Tests/Pickup: a bundle's temp zip is deleted after pickup and after expiry too.
    func testTempZipDeletedAfterPickupAndExpiry() async throws {
        try await setUpHarness()
        let before = tempZips()
        let a = try tempFile("a.txt", "a"), b = try tempFile("b.txt", "b")

        let picked = try await startHold(.paths([a, b]), expecting: 1)
        let token = try await firstItem().token
        try await eq(try await get("/v1/pickup/\(token)").status, 200)
        let pickedOutcome = await picked.value
        XCTAssertEqual(pickedOutcome, .pickedUp)
        XCTAssertEqual(tempZips().subtracting(before).count, 0)

        let expiring = try await startHold(.paths([a, b]), lifetime: 0.2, expecting: 1)
        XCTAssertEqual(tempZips().subtracting(before).count, 1)
        let expiredOutcome = await expiring.value
        XCTAssertEqual(expiredOutcome, .notPickedUp)
        XCTAssertEqual(tempZips().subtracting(before).count, 0)
    }

    // spec E pickup: a download that started before the time ran out is not cut off by it.
    func testExpiryDoesNotEndAHoldThatWasTaken() async throws {
        try await setUpHarness()
        let done = Flag()
        let holds = self.holds
        let task = Task { let o = await holds.hold(.text("x"), for: phone, lifetime: 0.2); done.set(); return o }
        _ = await waitUntil { holds.listing(for: senderIdentity.id).count == 1 }
        let token = try XCTUnwrap(holds.listing(for: senderIdentity.id).first).token

        guard case .download = holds.take(token: token, for: senderIdentity.id) else { return XCTFail("not taken") }
        try await Task.sleep(nanoseconds: 600_000_000)
        XCTAssertFalse(done.value, "the hold ended on the clock although its download had started")

        task.cancel()
        let outcome = await task.value
        XCTAssertEqual(outcome, .cancelled)
    }

    // spec E Tests/Pickup: a held file deleted before pickup makes the download fail with 404, and the sender sees Failed.
    func testHeldFileDeletedBeforePickupFails() async throws {
        try await setUpHarness()
        let file = try tempFile("gone.txt", "soon gone")
        let t = try await startHold(.paths([file]), expecting: 1)
        let token = try await firstItem().token

        try FileManager.default.removeItem(at: file)
        let r = try await get("/v1/pickup/\(token)")
        XCTAssertEqual(r.status, 404)
        XCTAssertNotEqual(r.body, Data("soon gone".utf8))
        let outcome = await t.value
        XCTAssertEqual(outcome, .failed)
        try await eq(try await listing(), [])
    }

    // MARK: What is served

    // spec E Tests/Pickup: the body served is exactly the text, with a text content type and no name.
    func testTextBody() async throws {
        try await setUpHarness()
        let t = try await startHold(.text("héllo\nworld"), expecting: 1)
        let token = try await firstItem().token
        let r = try await get("/v1/pickup/\(token)")
        XCTAssertEqual(r.body, Data("héllo\nworld".utf8))
        XCTAssertEqual(r.headers["content-type"], "text/plain; charset=utf-8")
        XCTAssertNil(r.headers["content-disposition"])
        _ = await t.value
    }

    // spec E Tests/Pickup: the body served is exactly the file's bytes, named in Content-Disposition.
    func testFileBodyAndName() async throws {
        try await setUpHarness()
        let content = String(repeating: "0123456789", count: 20_000)
        let file = try tempFile("report 1.txt", content)
        var reports: [SendProgress] = []
        let lock = NSLock()
        let t = try await startHold(.paths([file]), progress: { p in lock.withLock { reports.append(p) } }, expecting: 1)
        let token = try await firstItem().token

        let r = try await get("/v1/pickup/\(token)")
        XCTAssertEqual(r.body, Data(content.utf8))
        XCTAssertEqual(r.headers["content-type"], "application/octet-stream")
        XCTAssertEqual(r.headers["content-disposition"], #"attachment; filename="report 1.txt""#)
        let outcome = await t.value
        XCTAssertEqual(outcome, .pickedUp)

        // Waiting first, then the download's last report is the whole file.
        let seen = lock.withLock { reports }
        XCTAssertEqual(seen.first?.stage, .waiting)
        XCTAssertEqual(seen.first?.bytesTotal, 200_000)
        XCTAssertEqual(seen.last?.stage, .sending)
        XCTAssertEqual(seen.last?.bytesSent, 200_000)
        XCTAssertEqual(seen.last?.bytesTotal, 200_000)
    }

    // spec E Tests/Pickup: a name with a quote or non-ASCII characters can't break out of the header, and still arrives in full.
    func testDispositionEscapesTheName() {
        XCTAssertEqual(Receiver.disposition(#"a"b\c.txt"#), #"attachment; filename="a_b_c.txt"; filename*=UTF-8''a%22b%5Cc.txt"#)
        XCTAssertEqual(Receiver.disposition("café.txt"), #"attachment; filename="caf_.txt"; filename*=UTF-8''caf%C3%A9.txt"#)
        XCTAssertEqual(Receiver.disposition("plain.txt"), #"attachment; filename="plain.txt""#)
    }

    // spec E Tests/Pickup: the bundle is served as its zip, named <name>.zip, with every file inside; the list names it without .zip.
    func testBundleBody() async throws {
        try await setUpHarness()
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let folder = dir.appendingPathComponent("Trip")
        try write("one", to: folder.appendingPathComponent("1.txt"))
        try write("two", to: folder.appendingPathComponent("sub/2.txt"))
        let t = try await startHold(.paths([folder]), expecting: 1)

        let item = try await firstItem()
        XCTAssertEqual(item.kind, .bundle)
        XCTAssertEqual(item.name, "Trip")
        let r = try await get("/v1/pickup/\(item.token)")
        XCTAssertEqual(r.headers["content-disposition"], #"attachment; filename="Trip.zip""#)
        XCTAssertEqual(Int64(r.body.count), item.bytes)

        let out = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: out) }
        let zipURL = out.appendingPathComponent("got.zip")
        try r.body.write(to: zipURL)
        let placed = try Bundles.extract(zipURL: zipURL, into: out, totalSize: 6)
        XCTAssertEqual(placed.map(\.lastPathComponent), ["Trip"])
        XCTAssertEqual(try String(contentsOf: out.appendingPathComponent("Trip/1.txt")), "one")
        XCTAssertEqual(try String(contentsOf: out.appendingPathComponent("Trip/sub/2.txt")), "two")
        let outcome = await t.value
        XCTAssertEqual(outcome, .pickedUp)
    }

    // spec E: a hold doesn't block another: two to the same device sit side by side and are fetched in either order.
    func testTwoHoldsAreIndependent() async throws {
        try await setUpHarness()
        let t1 = try await startHold(.text("first"), expecting: 1)
        let t2 = try await startHold(.text("second"), expecting: 2)
        let items = try await listing()
        XCTAssertEqual(items.count, 2)
        guard items.count == 2 else { return }
        let second = try await get("/v1/pickup/\(items[1].token)").body
        XCTAssertEqual(second, Data("second".utf8))
        let remaining = try await listing().count
        XCTAssertEqual(remaining, 1)
        let first = try await get("/v1/pickup/\(items[0].token)").body
        XCTAssertEqual(first, Data("first".utf8))
        let (o1, o2) = await (t1.value, t2.value)
        XCTAssertEqual([o1, o2], [.pickedUp, .pickedUp])
    }

    // spec E: a hold made while the task is already cancelled never lists anything and leaves no zip.
    func testHoldOfACancelledTaskLeavesNothing() async throws {
        try await setUpHarness()
        let before = tempZips()
        let a = try tempFile("a.txt", "a"), b = try tempFile("b.txt", "b")
        let holds = self.holds, phone = self.phone
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await holds.hold(.paths([a, b]), for: phone, lifetime: 60)
        }
        let outcome = await task.value
        XCTAssertEqual(outcome, .cancelled)
        XCTAssertEqual(holds.listing(for: senderIdentity.id), [])
        XCTAssertEqual(tempZips().subtracting(before).count, 0)
    }

    // spec E Notices: the texts of the waiting status and the two notifications are built by Core.
    func testNotices() {
        XCTAssertEqual(Notices.waitingForPickup(device: "my-phone"), "Waiting for my-phone to pick up")
        XCTAssertEqual(Notices.pickedUp(by: "my-phone"), Notice(title: "Picked up by my-phone", body: ""))
        XCTAssertEqual(Notices.notPickedUp(by: "my-phone"), Notice(title: "Not picked up by my-phone", body: ""))
    }
}

private final class Flag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag = false
    func set() { lock.withLock { flag = true } }
    var value: Bool { lock.withLock { flag } }
}
