import Network
import XCTest
@testable import TailshareCore

/// Answers by request path, one fixed reply each, optionally after a delay: a computer, or a hub, on one loopback port.
final class PathServer {
    private let listener: NWListener
    let port: Int

    init(routes: [String: (status: Int, body: String)], delay: TimeInterval = 0, delays: [String: TimeInterval] = [:]) throws {
        let p = freePort()
        port = p
        listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: UInt16(p))!)
        listener.newConnectionHandler = { conn in
            conn.start(queue: .global())
            conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, _ in
                let line = data.map { String(decoding: $0, as: UTF8.self) }?.split(separator: "\r\n").first.map(String.init) ?? ""
                let path = line.split(separator: " ").dropFirst().first.map(String.init) ?? ""
                let reply = routes[path] ?? (404, "")
                let text = "HTTP/1.1 \(reply.status) X\r\nContent-Length: \(reply.body.utf8.count)\r\nConnection: close\r\n\r\n\(reply.body)"
                DispatchQueue.global().asyncAfter(deadline: .now() + (delays[path] ?? delay)) {
                    conn.send(content: Data(text.utf8), completion: .contentProcessed { _ in conn.cancel() })
                }
            }
        }
        listener.start(queue: .global())
    }

    func stop() { listener.cancel() }
}

/// Spec E, the device list: computers as before, plus the phones and iPads the hub lists.
final class HubDiscoveryTests: XCTestCase {
    private var servers: [PathServer] = []

    override func tearDown() {
        servers.forEach { $0.stop() }
        servers = []
    }

    private func serve(_ routes: [String: (status: Int, body: String)], delay: TimeInterval = 0, delays: [String: TimeInterval] = [:]) throws -> Int {
        let s = try PathServer(routes: routes, delay: delay, delays: delays)
        servers.append(s)
        return s.port
    }

    private func app() throws -> Int { try serve(["/v1/hello": (200, #"{"app":"tailshare","protocol":1}"#)]) }

    private func hub(mobiles: String, helloDelay: TimeInterval = 0, devicesStatus: Int = 200) throws -> Int {
        try serve([
            "/v1/hello": (200, #"{"app":"tailshare","protocol":1,"hub":true}"#),
            "/v1/devices": (devicesStatus, #"{"computers":[{"name":"ignored-box","address":"100.64.9.9"}],"mobiles":\#(mobiles)}"#),
        ], delay: helloDelay)
    }

    private func peer(_ name: String, _ ip: String) -> PeerInfo { PeerInfo(identity: Identity(id: "node-\(name)", name: name), ip: ip, online: true) }

    private func sender(_ ports: [String: Int]) -> Sender { Sender(port: 1, portOverrides: ports) }

    private func status(_ peers: [PeerInfo]) -> TailnetStatus { TailnetStatus(connected: true, selfIP: "100.64.0.1", peers: peers) }

    private let phones = #"[{"name":"b-phone","id":"nodePHONEB"},{"name":"a-ipad","id":"nodePADA"}]"#

    // spec E Tests/hello and the hub mark: a device answering hub: true is never in the send list.
    func testAHubIsNotASendTarget() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let s = sender(["127.0.0.11": try app(), "127.0.0.12": try hub(mobiles: "[]")])
        let found = await s.discover(status([computer, homeHub]))
        XCTAssertEqual(found, [computer])
    }

    // spec E Refresh: the hub's mobiles are added after the computers, as phone rows (no address), by name; the hub's own `computers` are ignored.
    func testMobilesAreAddedFromTheHub() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let s = sender(["127.0.0.11": try app(), "127.0.0.12": try hub(mobiles: phones)])
        let found = await s.discoverDevices(status([computer, homeHub]), hubSetting: nil)

        XCTAssertEqual(found.map(\.identity.name), ["desk", "a-ipad", "b-phone"])
        XCTAssertEqual(found.map(\.isMobile), [false, true, true])
        XCTAssertEqual(found.map(\.identity.id), ["node-desk", "nodePADA", "nodePHONEB"])
        XCTAssertEqual(found.map(\.ip), ["127.0.0.11", "", ""])
        XCTAssertTrue(found.allSatisfy(\.online))
    }

    // spec E Refresh: a mobile the hub lists that is already in the list (same id as a computer) is excluded.
    func testMobileAlreadyListedIsExcluded() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let both = #"[{"name":"desk-again","id":"node-desk"},{"name":"b-phone","id":"nodePHONEB"}]"#
        let s = sender(["127.0.0.11": try app(), "127.0.0.12": try hub(mobiles: both)])
        let found = await s.discoverDevices(status([computer, homeHub]), hubSetting: nil)
        XCTAssertEqual(found.map(\.identity.name), ["desk", "b-phone"])
        XCTAssertEqual(found.map(\.isMobile), [false, true])
    }

    // spec E Refresh: a mobile the hub lists without an id or a name is not a row.
    func testMobilesWithoutIdOrNameAreDropped() async throws {
        let homeHub = peer("home", "127.0.0.12")
        let odd = #"[{"name":"","id":"nodeX"},{"name":"no-id","id":""},{"name":"ok-phone","id":"nodeOK"}]"#
        let s = sender(["127.0.0.12": try hub(mobiles: odd)])
        let found = await s.discoverDevices(status([homeHub]), hubSetting: nil)
        XCTAssertEqual(found.map(\.identity.name), ["ok-phone"])
    }

    // spec E Refresh: with no hub on the tailnet the list is the computers, and no error.
    func testNoHubMeansNoMobiles() async throws {
        let computer = peer("desk", "127.0.0.11")
        let s = sender(["127.0.0.11": try app()])
        let found = await s.discoverDevices(status([computer]), hubSetting: nil)
        XCTAssertEqual(found, [computer])
    }

    // spec E Refresh: a hub that is down, errors or answers rubbish costs only the mobiles.
    func testHubFailuresCostOnlyTheMobiles() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let appPort = try app()

        // Answers hello but nothing else was routed: the devices call 404s.
        let broken = try serve(["/v1/hello": (200, #"{"app":"tailshare","protocol":1,"hub":true}"#)])
        let garbage = try serve(["/v1/hello": (200, #"{"app":"tailshare","protocol":1,"hub":true}"#), "/v1/devices": (200, "not json")])
        let refused = try hub(mobiles: phones, devicesStatus: 403)
        for hubPort in [broken, garbage, refused] {
            let s = sender(["127.0.0.11": appPort, "127.0.0.12": hubPort])
            let found = await s.discoverDevices(status([computer, homeHub]), hubSetting: nil)
            XCTAssertEqual(found, [computer], "hub on port \(hubPort)")
        }
    }

    private func slowDevices(delay: TimeInterval) throws -> Int {
        try serve([
            "/v1/hello": (200, #"{"app":"tailshare","protocol":1,"hub":true}"#),
            "/v1/devices": (200, #"{"computers":[],"mobiles":\#(phones)}"#),
        ], delays: ["/v1/devices": delay])
    }

    // spec E Refresh (changed 2026-11-19): the hub probes its peers before it answers /v1/devices, so that wait is longer than a
    // hello's; a hub slower than a hello timeout still gives its phones.
    func testHubSlowerThanAHelloTimeoutStillGivesItsMobiles() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let s = Sender(port: 1, helloTimeout: 0.3, devicesTimeout: 5, portOverrides: ["127.0.0.11": try app(), "127.0.0.12": try slowDevices(delay: 0.8)])
        let found = await s.discoverDevices(status([computer, homeHub]), hubSetting: nil)
        XCTAssertEqual(found.map(\.identity.name), ["desk", "a-ipad", "b-phone"])
    }

    // spec E Refresh: a hub that doesn't answer /v1/devices in time adds nothing (a short timeout here), and the computers stay.
    func testHubThatMissesTheDevicesTimeoutAddsNothing() async throws {
        let computer = peer("desk", "127.0.0.11"), homeHub = peer("home", "127.0.0.12")
        let s = Sender(port: 1, helloTimeout: 0.3, devicesTimeout: 0.4, portOverrides: ["127.0.0.11": try app(), "127.0.0.12": try slowDevices(delay: 1.5)])
        let found = await s.discoverDevices(status([computer, homeHub]), hubSetting: nil)
        XCTAssertEqual(found, [computer])
    }

    // spec E Refresh: the wait for the device list is 5 seconds and a hello's is 2.
    func testTimeoutValues() {
        XCTAssertEqual(Wire.devicesTimeout, 5)
        XCTAssertEqual(Wire.helloTimeout, 2)
    }

    // spec E Refresh: several hubs and no setting: the first by name supplies the mobiles.
    func testFirstHubByNameWins() async throws {
        let zed = peer("zed-hub", "127.0.0.12"), alpha = peer("alpha-hub", "127.0.0.13")
        let s = sender([
            "127.0.0.12": try hub(mobiles: #"[{"name":"from-zed","id":"nodeZ"}]"#),
            "127.0.0.13": try hub(mobiles: #"[{"name":"from-alpha","id":"nodeA"}]"#),
        ])
        let found = await s.discoverDevices(status([zed, alpha]), hubSetting: nil)
        XCTAssertEqual(found.map(\.identity.name), ["from-alpha"])
    }

    // spec E Tests/hello and the hub mark: with `hub` set, that device is used even when another hub answers (by machine name, any case).
    func testHubSettingByNameOverridesTheOtherHub() async throws {
        let zed = peer("zed-hub", "127.0.0.12"), alpha = peer("alpha-hub", "127.0.0.13")
        let s = sender([
            "127.0.0.12": try hub(mobiles: #"[{"name":"from-zed","id":"nodeZ"}]"#),
            "127.0.0.13": try hub(mobiles: #"[{"name":"from-alpha","id":"nodeA"}]"#),
        ])
        let found = await s.discoverDevices(status([zed, alpha]), hubSetting: "ZED-hub")
        XCTAssertEqual(found.map(\.identity.name), ["from-zed"])
    }

    // spec E Settings: `hub` as a Tailscale IPv4 is used as it is, even for a machine that isn't among the peers.
    func testHubSettingByAddress() async throws {
        let computer = peer("desk", "127.0.0.11")
        let s = sender(["127.0.0.11": try app(), "100.100.5.5": try hub(mobiles: phones)])
        let found = await s.discoverDevices(status([computer]), hubSetting: "100.100.5.5")
        XCTAssertEqual(found.map(\.identity.name), ["desk", "a-ipad", "b-phone"])
    }

    // spec E Settings: a `hub` naming nothing on the tailnet, or not a tailnet address, finds no mobiles, even if another hub answers.
    func testHubSettingThatMatchesNothing() async throws {
        let homeHub = peer("home", "127.0.0.12")
        let s = sender(["127.0.0.12": try hub(mobiles: phones)])
        let named = await s.discoverDevices(status([homeHub]), hubSetting: "nowhere")
        XCTAssertEqual(named, [])
        // A hub setting is never turned into a request to an arbitrary host.
        let outside = await s.discoverDevices(status([homeHub]), hubSetting: "example.com")
        XCTAssertEqual(outside, [])
        let notTailnet = await s.discoverDevices(status([homeHub]), hubSetting: "192.168.1.1")
        XCTAssertEqual(notTailnet, [])
    }

    // spec E: a phone has no address, so the Sender never tries to send to one.
    func testSenderRefusesAMobile() async {
        let phone = PeerInfo(identity: Identity(id: "nodeP", name: "p"), ip: "", online: true, isMobile: true)
        let outcome = await Sender(port: 1).send(to: phone, content: .text("x"))
        XCTAssertEqual(outcome, .failed)
    }
}
