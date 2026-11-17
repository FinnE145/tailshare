import XCTest
@testable import TailshareCore

final class SettingsTests: XCTestCase {
    private func store(_ json: String?) throws -> (SettingsStore, URL) {
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("settings.json")
        if let json { try json.write(to: url, atomically: true, encoding: .utf8) }
        return (SettingsStore(fileURL: url), url)
    }

    // spec B Tests/Port: missing, null, below 1024 or above 65535 gives 45611; a valid port is used.
    func testEffectivePort() throws {
        XCTAssertEqual(Settings().effectivePort, 45611)
        XCTAssertEqual(Settings(port: 1023).effectivePort, 45611)
        XCTAssertEqual(Settings(port: 65536).effectivePort, 45611)
        XCTAssertEqual(Settings(port: 0).effectivePort, 45611)
        XCTAssertEqual(Settings(port: -5).effectivePort, 45611)
        XCTAssertEqual(Settings(port: 1024).effectivePort, 1024)
        XCTAssertEqual(Settings(port: 65535).effectivePort, 65535)
        XCTAssertEqual(Settings(port: 50123).effectivePort, 50123)

        XCTAssertEqual(try store(#"{"port": 50123}"#).0.load().value.effectivePort, 50123)
        XCTAssertEqual(try store(#"{"port": null}"#).0.load().value.effectivePort, 45611)
        XCTAssertEqual(try store(#"{}"#).0.load().value.effectivePort, 45611)
        XCTAssertEqual(try store(nil).0.load().value.effectivePort, 45611)
    }

    // spec B Tests/Receive folder (settings side): unset, null and set are read as written.
    func testReceiveFolderIsReadFromTheFile() throws {
        XCTAssertNil(try store(nil).0.load().value.receiveFolder)
        XCTAssertNil(try store(#"{"receiveFolder": null}"#).0.load().value.receiveFolder)
        XCTAssertEqual(try store(#"{"receiveFolder": "/tmp/somewhere"}"#).0.load().value.receiveFolder, "/tmp/somewhere")
    }

    // spec B Tests/Trust: Accept adds the device to trusted with its name, in A's file shape.
    func testAddTrustedWritesAFileInAsShape() throws {
        let (s, url) = try store(#"{"receiveFolder": "/tmp/x", "port": 50000, "blocked": [{"id": "b1", "name": "bad-box"}]}"#)
        XCTAssertTrue(s.addTrusted(senderIdentity))

        let loaded = s.load()
        XCTAssertFalse(loaded.malformed)
        XCTAssertEqual(loaded.value.trusted, [senderIdentity])
        // Everything else the person wrote survives the save.
        XCTAssertEqual(loaded.value.receiveFolder, "/tmp/x")
        XCTAssertEqual(loaded.value.port, 50000)
        XCTAssertEqual(loaded.value.blocked, [Identity(id: "b1", name: "bad-box")])

        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        XCTAssertEqual(Set(json.keys), ["receiveFolder", "port", "trusted", "blocked"])
        XCTAssertEqual((json["trusted"] as! [[String: String]]), [["id": senderIdentity.id, "name": senderIdentity.name]])
    }

    // spec B Tests/Trust: a fresh file lists every key, with explicit nulls, so a person editing it sees them all.
    func testNewFileHasEveryKey() throws {
        let (s, url) = try store(nil)
        s.addTrusted(senderIdentity)
        let text = try String(contentsOf: url, encoding: .utf8)
        XCTAssertTrue(text.contains("\"receiveFolder\" : null"), text)
        XCTAssertTrue(text.contains("\"port\" : null"), text)
        XCTAssertTrue(text.contains("\"blocked\" : ["), text)
    }

    // spec B Tests/Trust: adding a device that is already trusted doesn't duplicate it.
    func testAddTrustedTwiceKeepsOneEntry() throws {
        let (s, _) = try store(nil)
        s.addTrusted(senderIdentity)
        s.addTrusted(Identity(id: senderIdentity.id, name: "renamed"))
        XCTAssertEqual(s.load().value.trusted, [senderIdentity])
    }

    // spec B Tests/Trust: a malformed file prompts everyone (all defaults), isn't overwritten, and an Accept while it's malformed doesn't persist.
    func testMalformedFileIsNeverOverwritten() throws {
        for bad in ["{ not json", "[]", "null", #"{"port": "45611"}"#, #"{"trusted": 5}"#, ""] {
            let (s, url) = try store(bad)
            let loaded = s.load()
            XCTAssertTrue(loaded.malformed, bad)
            XCTAssertEqual(loaded.value, Settings(), bad)
            XCTAssertFalse(s.addTrusted(senderIdentity), bad)
            XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), bad)
        }
    }

    // spec B Tests/Trust: a hand edit is seen on the next read without a restart.
    func testHandEditsAreSeenOnTheNextLoad() throws {
        let (s, url) = try store(#"{"trusted": [{"id": "a", "name": "A"}]}"#)
        XCTAssertTrue(s.load().value.isTrusted("a"))
        try #"{"trusted": [], "blocked": [{"id": "a", "name": "A"}]}"#.write(to: url, atomically: true, encoding: .utf8)
        XCTAssertFalse(s.load().value.isTrusted("a"))
        XCTAssertTrue(s.load().value.isBlocked("a"))
    }

    // spec B Settings file: any field may be missing or null, and a list entry may be null; those aren't malformed.
    func testLenientFileIsNotMalformed() throws {
        let loaded = try store(#"{"trusted": null, "blocked": [null, {"id": "z", "name": "Z"}], "extra": 1}"#).0.load()
        XCTAssertFalse(loaded.malformed)
        XCTAssertEqual(loaded.value.blocked, [Identity(id: "z", name: "Z")])
        XCTAssertEqual(loaded.value.trusted, [])
    }
}

final class TailscaleTests: XCTestCase {
    private let statusJSON = """
    {
      "BackendState": "Running",
      "Self": {"ID": "nSELF", "DNSName": "this-box.example-tailnet.ts.net.", "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"]},
      "Peer": {
        "k1": {"ID": "nAAA", "DNSName": "zeta-box.example-tailnet.ts.net.", "TailscaleIPs": ["fd7a:115c:a1e0::2", "100.100.5.7"], "Online": true},
        "k2": {"ID": "nBBB", "DNSName": "alpha-box.example-tailnet.ts.net.", "TailscaleIPs": ["100.127.255.255"], "Online": false},
        "k3": {"ID": "nCCC", "DNSName": "noaddr.example-tailnet.ts.net.", "TailscaleIPs": ["192.168.1.5"], "Online": true},
        "k4": {"DNSName": "noid.example-tailnet.ts.net.", "TailscaleIPs": ["100.64.9.9"], "Online": true},
        "k5": {"ID": "nDDD", "DNSName": "quiet-box.example-tailnet.ts.net.", "TailscaleIPs": ["100.64.7.7"]}
      }
    }
    """

    // spec B Tailscale on the Mac (A's rules): status gives this device's tailnet IPv4 and the peers, named by the first label of DNSName.
    func testParseStatus() {
        let s = TailscaleJSON.parseStatus(Data(statusJSON.utf8))
        XCTAssertTrue(s.connected)
        XCTAssertEqual(s.selfIP, "100.64.0.1")
        XCTAssertEqual(s.peers, [
            PeerInfo(identity: Identity(id: "nBBB", name: "alpha-box"), ip: "100.127.255.255", online: false),
            // No Online key at all counts as offline: a device that isn't known to be up is never probed or listed.
            PeerInfo(identity: Identity(id: "nDDD", name: "quiet-box"), ip: "100.64.7.7", online: false),
            PeerInfo(identity: Identity(id: "nAAA", name: "zeta-box"), ip: "100.100.5.7", online: true),
        ])
    }

    // spec B Tailscale on the Mac (A's rules): down, logged out, or no IPv4 means not connected.
    func testDownLoggedOutAndGarbageAreDisconnected() {
        for json in [#"{"BackendState": "NeedsLogin", "Self": {"TailscaleIPs": ["100.64.0.1"]}}"#,
                     #"{"BackendState": "Stopped"}"#,
                     #"{"BackendState": "Running", "Self": {"TailscaleIPs": ["fd7a:115c:a1e0::1"]}}"#,
                     "not json", ""] {
            XCTAssertEqual(TailscaleJSON.parseStatus(Data(json.utf8)), .disconnected, json)
        }
    }

    // spec B Tailscale on the Mac (A's rules): the tailnet range is 100.64.0.0/10 and nothing else.
    func testTailnetRangeBoundaries() {
        for ip in ["100.64.0.0", "100.127.255.255", "100.100.1.1"] { XCTAssertTrue(TailscaleJSON.isTailnetIPv4(ip), ip) }
        for ip in ["100.63.255.255", "100.128.0.0", "101.64.0.1", "127.0.0.1", "10.0.0.1", "100.64.0", "100.64.0.1.2", "100.64.0.256", "::1", ""] {
            XCTAssertFalse(TailscaleJSON.isTailnetIPv4(ip), ip)
        }
    }

    // spec B Tailscale on the Mac (A's rules): whois gives StableID and ComputedName; either missing gives nobody.
    func testParseWhois() {
        XCTAssertEqual(TailscaleJSON.parseWhois(Data(#"{"Node": {"StableID": "nXYZ", "ComputedName": "some-box"}, "UserProfile": {}}"#.utf8)),
                       Identity(id: "nXYZ", name: "some-box"))
        XCTAssertNil(TailscaleJSON.parseWhois(Data(#"{"Node": {"StableID": "nXYZ"}}"#.utf8)))
        XCTAssertNil(TailscaleJSON.parseWhois(Data(#"{"Node": {"ComputedName": "x"}}"#.utf8)))
        XCTAssertNil(TailscaleJSON.parseWhois(Data("nope".utf8)))
    }

    // spec B Tests/Tailscale CLI lookup: the first existing path in the order given is chosen.
    func testCLILookupOrder() {
        let order = CLITailscale.defaultCandidates
        XCTAssertEqual(order, ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"])

        XCTAssertEqual(CLITailscale.locate(in: order) { _ in true }, order[0])
        XCTAssertEqual(CLITailscale.locate(in: order) { $0 != order[0] }, order[1])
        XCTAssertEqual(CLITailscale.locate(in: order) { $0 == order[2] }, order[2])
        XCTAssertNil(CLITailscale.locate(in: order) { _ in false })
        // Order, not name: with both of the last two present the earlier one wins.
        XCTAssertEqual(CLITailscale.locate(in: order) { $0 == order[2] || $0 == order[1] }, order[1])
    }

    // spec B Tailscale on the Mac: a missing CLI is "no answer", not a crash.
    func testMissingCLIGivesNoAnswer() async {
        let ts = CLITailscale(candidates: ["/nonexistent/tailscale"], exists: { _ in false })
        let status = await ts.status()
        let who = await ts.whois(ip: "100.64.0.1")
        XCTAssertEqual(status, .disconnected)
        XCTAssertNil(who)
    }

    // spec B Tailscale on the Mac (found on the first real run): the CLI is started with TAILSCALE_BE_CLI=1, without which the
    // macOS binary may act as the GUI and answer with a message instead of JSON. The fake answers only when the variable is set.
    func testCLIIsStartedInCLIMode() async throws {
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let script = dir.appendingPathComponent("fake-tailscale")
        let body = """
        #!/bin/sh
        if [ "$TAILSCALE_BE_CLI" != "1" ]; then echo "The Tailscale GUI failed to start"; exit 0; fi
        echo '{"BackendState": "Running", "Self": {"TailscaleIPs": ["100.64.0.1"]}, "Peer": {}}'
        """
        try body.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)

        let status = await CLITailscale(candidates: [script.path]).status()
        XCTAssertTrue(status.connected)
        XCTAssertEqual(status.selfIP, "100.64.0.1")
    }

    // spec B Security checklist: whois is only ever run on a tailnet address, never on something a peer chose.
    // The fake CLI answers any whois with a valid identity, so only the address check can make the result nil.
    func testWhoisRefusesNonTailnetAddresses() async throws {
        let dir = try makeTempDir()
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let script = dir.appendingPathComponent("fake-tailscale")
        try "#!/bin/sh\necho '{\"Node\": {\"StableID\": \"nFAKE\", \"ComputedName\": \"fake-box\"}}'\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let ts = CLITailscale(candidates: [script.path])

        let ok = await ts.whois(ip: "100.64.0.1")
        XCTAssertEqual(ok, Identity(id: "nFAKE", name: "fake-box"))
        for bad in ["127.0.0.1", "--help", "100.64.0.1; rm -rf /", "fd7a:115c:a1e0::1", ""] {
            let who = await ts.whois(ip: bad)
            XCTAssertNil(who, bad)
        }
    }
}
