using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tailshare.Core;
using Tailshare.Hub;

namespace Tailshare.Tests;

/// <summary>The hub over loopback, with whois and Tailscale's status stubbed and its peers fake listeners.</summary>
internal sealed class HubRig : IAsyncDisposable
{
    private readonly TempDir _dir = new();

    public RegistryStore Registry { get; }
    public FakeTailscale Tailscale { get; } = new();
    public Tailnet Net { get; } = new();
    public List<string> Log { get; } = [];
    public HubServer Server { get; private set; } = null!;
    public HttpClient Http { get; } = new();

    private HubRig() => Registry = new RegistryStore(_dir.Combine("data", "registry.json"));

    /// <param name="beforeStart">Runs before the server starts, for what its first probe must already see.</param>
    public static async Task<HubRig> StartAsync(TimeSpan? probeInterval = null, Action<HubRig>? beforeStart = null)
    {
        var rig = new HubRig();
        beforeStart?.Invoke(rig);
        rig.Server = new HubServer(new HubOptions
        {
            Tailscale = rig.Tailscale,
            Registry = rig.Registry,
            PeerPort = rig.Net.Port,
            HelloTimeout = TimeSpan.FromMilliseconds(500),
            // Long by default: tests probe by hand (ShowAsync) so what they see doesn't depend on a timer.
            ProbeInterval = probeInterval ?? TimeSpan.FromMinutes(10),
            Log = line => { lock (rig.Log) rig.Log.Add(line); },
        });
        await rig.Server.StartAsync(IPAddress.Loopback, 0);
        return rig;
    }

    public string DataDir => _dir.Combine("data");

    public Uri Url(string path) => new($"http://127.0.0.1:{Server.Port}{path}");

    /// <summary>Tailscale now shows these peers, and the hub has probed them.</summary>
    public async Task ShowAsync(params PeerInfo[] peers)
    {
        Tailscale.Status = Tailnet.Status(peers);
        await Server.ProbeNowAsync();
    }

    public async Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path)
    {
        using var r = await Http.SendAsync(new HttpRequestMessage(method, Url(path)));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    public Task<(HttpStatusCode Status, string Body)> Get(string path) => Send(HttpMethod.Get, path);

    public Task<(HttpStatusCode Status, string Body)> Register() => Send(HttpMethod.Post, "/v1/register");

    public static HubDevices Parse(string body) => JsonSerializer.Deserialize<HubDevices>(body, Protocol.Json)!;

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        Net.Dispose();
        _dir.Dispose();
    }
}

public class HubTests
{
    private const string AppHello = """{"app":"tailshare","protocol":1}""";
    private const string HubHello = """{"app":"tailshare","protocol":1,"hub":true}""";

    private static readonly Identity Phone = new("node-phone", "pixel");

    private static PeerInfo PhonePeer(string id, string name, int octet, bool online = true) =>
        new(new Identity(id, name), IPAddress.Parse($"100.64.1.{octet}"), online);

    [Fact] // spec hello: a hub answers {"app":"tailshare","protocol":1,"hub":true}
    public async Task Hello_marks_the_hub()
    {
        await using var rig = await HubRig.StartAsync();

        var (status, body) = await rig.Get("/v1/hello");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"app":"tailshare","protocol":1,"hub":true}""", body);
    }

    // ---- register

    [Fact] // spec POST /v1/register: stores the caller's StableID and whois name, never a name from the request
    public async Task Register_stores_the_stable_id_and_the_whois_name()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Tailscale.Who = Phone;

        using var r = await rig.Http.PostAsync(rig.Url("/v1/register"), JsonContent.Create(new { name = "evil", id = "other" }));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([new RegistryDevice("node-phone", "pixel")], rig.Registry.Load().Devices);
        // spec Hub: registry.json is {"devices":[{"id":…,"name":…}]}
        using var doc = JsonDocument.Parse(File.ReadAllText(rig.Registry.FilePath));
        var device = Assert.Single(doc.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal("node-phone", device.GetProperty("id").GetString());
        Assert.Equal("pixel", device.GetProperty("name").GetString());
    }

    [Fact] // spec POST /v1/register: calling it again is harmless, and a new name updates the row instead of adding one
    public async Task Registering_again_updates_the_name_without_a_new_row()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Tailscale.Who = Phone;
        await rig.Register();
        await rig.Register();
        Assert.Single(rig.Registry.Load().Devices);

        rig.Tailscale.Who = Phone with { Name = "pixel-renamed" };
        await rig.Register();
        rig.Tailscale.Who = new Identity("node-ipad", "ipad");
        await rig.Register();

        Assert.Equal([new RegistryDevice("node-phone", "pixel-renamed"), new RegistryDevice("node-ipad", "ipad")], rig.Registry.Load().Devices);
    }

    [Fact] // spec Hub: written atomically, only the registry file is left in the data directory
    public async Task Register_leaves_only_the_registry_file()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Tailscale.Who = Phone;

        await rig.Register();

        Assert.Equal([rig.Registry.FilePath], Directory.GetFileSystemEntries(rig.DataDir));
    }

    [Fact] // spec POST /v1/register: answers exactly as GET /v1/devices
    public async Task Register_answers_with_the_device_list()
    {
        await using var rig = await HubRig.StartAsync();
        var other = PhonePeer("node-ipad", "ipad", 7);
        rig.Registry.Register(new Identity("node-ipad", "ipad"));
        await rig.ShowAsync(other, PhonePeer("node-phone", "pixel", 8));
        rig.Tailscale.Who = Phone;

        var (status, registered) = await rig.Register();
        var (_, listed) = await rig.Get("/v1/devices");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(listed, registered);
        // The caller just registered and is online, and still isn't in its own list.
        Assert.Equal([new Identity("node-ipad", "ipad")], HubRig.Parse(registered).Mobiles);
    }

    // ---- devices

    [Fact] // spec GET /v1/devices: mobiles are registered devices Tailscale shows online right now, never the caller, under Tailscale's name
    public async Task Mobiles_are_registered_online_devices_other_than_the_caller()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Registry.Register(new Identity("node-ipad", "old-name"));
        rig.Registry.Register(new Identity("node-offline", "asleep"));
        rig.Registry.Register(Phone);
        await rig.ShowAsync(
            PhonePeer("node-ipad", "ipad", 1),
            PhonePeer("node-offline", "asleep", 2, online: false),
            PhonePeer("node-phone", "pixel", 3),
            PhonePeer("node-unregistered", "stranger", 4));
        rig.Tailscale.Who = Phone;

        var (_, body) = await rig.Get("/v1/devices");

        Assert.Equal([new Identity("node-ipad", "ipad")], HubRig.Parse(body).Mobiles);
    }

    [Fact] // spec Hub: unregistering is a hand edit, read fresh
    public async Task A_hand_edit_of_the_registry_applies_at_once()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Registry.Register(new Identity("node-ipad", "ipad"));
        await rig.ShowAsync(PhonePeer("node-ipad", "ipad", 1));
        Assert.Single(HubRig.Parse((await rig.Get("/v1/devices")).Body).Mobiles!);

        File.WriteAllText(rig.Registry.FilePath, """{"devices":[]}""");

        Assert.Empty(HubRig.Parse((await rig.Get("/v1/devices")).Body).Mobiles!);
    }

    [Fact] // spec GET /v1/devices: computers are online peers answering hello protocol 1, not hubs, with their IPv4, never the caller
    public async Task Computers_are_the_peers_that_answer_hello_as_computers()
    {
        await using var rig = await HubRig.StartAsync();
        var b = rig.Net.Add("c-b", "b-desk", 1, AppHello);
        var a = rig.Net.Add("c-a", "a-laptop", 2, AppHello);
        var future = rig.Net.Add("c-v2", "future", 3, """{"app":"tailshare","protocol":2}""");
        var hub = rig.Net.Add("c-hub", "other-hub", 4, HubHello);
        var asleep = rig.Net.Add("c-off", "asleep", 5, AppHello, online: false);
        var silent = new PeerInfo(new Identity("c-gone", "gone"), IPAddress.Parse("127.0.0.6"), true);
        var caller = rig.Net.Add("c-me", "me", 7, AppHello);
        await rig.ShowAsync(b, a, future, hub, asleep, silent, caller);
        rig.Tailscale.Who = caller.Identity;

        var (_, body) = await rig.Get("/v1/devices");

        Assert.Equal([new HubComputer("a-laptop", "127.0.0.2"), new HubComputer("b-desk", "127.0.0.1")], HubRig.Parse(body).Computers);
    }

    [Fact] // a request reads the last probe and never probes: a computer that appeared since is not listed until the next probe
    public async Task Computers_come_from_the_last_probe_not_from_the_request()
    {
        await using var rig = await HubRig.StartAsync();
        var a = rig.Net.Add("c-a", "a-laptop", 1, AppHello);
        var b = rig.Net.Add("c-b", "b-desk", 2, AppHello);
        var caller = rig.Net.Add("c-me", "me", 7, AppHello);
        rig.Tailscale.Who = caller.Identity;
        await rig.ShowAsync(a, caller);

        rig.Tailscale.Status = Tailnet.Status(a, b, caller);
        var before = HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers;
        await rig.Server.ProbeNowAsync();
        var after = HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers;

        Assert.Equal([new HubComputer("a-laptop", "127.0.0.1")], before);
        Assert.Equal([new HubComputer("a-laptop", "127.0.0.1"), new HubComputer("b-desk", "127.0.0.2")], after);
    }

    [Fact] // a computer Tailscale shows offline drops out at once, without waiting for the next probe
    public async Task A_computer_that_goes_offline_drops_out_before_the_next_probe()
    {
        await using var rig = await HubRig.StartAsync();
        var a = rig.Net.Add("c-a", "a-laptop", 1, AppHello);
        var caller = rig.Net.Add("c-me", "me", 7, AppHello);
        rig.Tailscale.Who = caller.Identity;
        await rig.ShowAsync(a, caller);

        rig.Tailscale.Status = Tailnet.Status(a with { Online = false }, caller);

        Assert.Empty(HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers);
    }

    [Fact] // the background loop keeps the list current on its own
    public async Task The_background_probe_picks_up_a_new_computer()
    {
        await using var rig = await HubRig.StartAsync(TimeSpan.FromMilliseconds(100));
        var a = rig.Net.Add("c-a", "a-laptop", 1, AppHello);
        var caller = rig.Net.Add("c-me", "me", 7, AppHello);
        rig.Tailscale.Who = caller.Identity;

        rig.Tailscale.Status = Tailnet.Status(a, caller);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<HubComputer> computers;
        do
        {
            computers = HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers;
            if (computers.Count == 0) await Task.Delay(50);
        } while (computers.Count == 0 && DateTime.UtcNow < deadline);

        Assert.Equal([new HubComputer("a-laptop", "127.0.0.1")], computers);
    }

    [Fact] // spec GET /v1/devices: the hub probes once before it starts listening, so its first answer already has the computers
    public async Task The_first_request_is_answered_from_a_probe_made_before_listening()
    {
        PeerInfo a = null!;
        await using var rig = await HubRig.StartAsync(beforeStart: r =>
        {
            a = r.Net.Add("c-a", "a-laptop", 1, AppHello);
            var caller = r.Net.Add("c-me", "me", 7, AppHello);
            r.Tailscale.Who = caller.Identity;
            r.Tailscale.Status = Tailnet.Status(a, caller);
        });

        var computers = HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers;

        Assert.Equal([new HubComputer("a-laptop", "127.0.0.1")], computers);
    }

    [Fact] // a probe that fails keeps the last list rather than emptying it (HubServer's comment), and says so in the log
    public async Task A_failed_probe_keeps_the_last_list()
    {
        await using var rig = await HubRig.StartAsync();
        var a = rig.Net.Add("c-a", "a-laptop", 1, AppHello);
        var caller = rig.Net.Add("c-me", "me", 7, AppHello);
        rig.Tailscale.Who = caller.Identity;
        await rig.ShowAsync(a, caller);

        rig.Tailscale.FailNextStatus = 1;
        await rig.Server.ProbeNowAsync();

        Assert.Equal([new HubComputer("a-laptop", "127.0.0.1")], HubRig.Parse((await rig.Get("/v1/devices")).Body).Computers);
        lock (rig.Log) Assert.Contains("probe failed: IOException", rig.Log);
    }

    [Fact] // spec GET /v1/devices: Tailscale down means nothing to list, not an error
    public async Task Devices_with_tailscale_down_are_empty()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Registry.Register(Phone);

        var (status, body) = await rig.Get("/v1/devices");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"computers":[],"mobiles":[]}""", body);
    }

    // ---- who may ask, and what is not served

    [Fact] // spec Hub endpoints: whois failure -> 403 on every endpoint, and nothing is registered
    public async Task Failed_whois_is_403_everywhere_and_registers_nothing()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Tailscale.Who = null;

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/v1/hello"), (HttpMethod.Get, "/v1/devices"), (HttpMethod.Post, "/v1/register"),
            (HttpMethod.Post, "/v1/offer"), (HttpMethod.Put, "/v1/upload/x"), (HttpMethod.Get, "/nothing"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.Send(method, path)).Status);
        }
        Assert.False(File.Exists(rig.Registry.FilePath));
    }

    [Fact] // spec Hub endpoints: anything else, including offer and upload, is 404
    public async Task Other_endpoints_are_404()
    {
        await using var rig = await HubRig.StartAsync();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, "/v1/offer"), (HttpMethod.Put, "/v1/upload/x"), (HttpMethod.Get, "/v1/pickup"),
            (HttpMethod.Get, "/nothing"), (HttpMethod.Get, "/v1/register"), (HttpMethod.Post, "/v1/devices"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await rig.Send(method, path)).Status);
        }
    }

    [Fact] // spec Hub: a malformed registry is answered as empty and left byte for byte unchanged after a register
    public async Task A_malformed_registry_is_answered_as_empty_and_never_overwritten()
    {
        await using var rig = await HubRig.StartAsync();
        Directory.CreateDirectory(rig.DataDir);
        File.WriteAllText(rig.Registry.FilePath, "{ not json");
        await rig.ShowAsync(PhonePeer("node-ipad", "ipad", 1));
        rig.Tailscale.Who = Phone;

        var (listStatus, list) = await rig.Get("/v1/devices");
        var (registerStatus, registered) = await rig.Register();

        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(HttpStatusCode.OK, registerStatus);
        Assert.Empty(HubRig.Parse(list).Mobiles!);
        Assert.Empty(HubRig.Parse(registered).Mobiles!);
        Assert.Equal("{ not json", File.ReadAllText(rig.Registry.FilePath));
    }

    [Fact] // spec Hub: logs requests by device name and endpoint only
    public async Task The_log_has_the_device_name_and_endpoint_and_nothing_from_the_request()
    {
        await using var rig = await HubRig.StartAsync();
        rig.Tailscale.Who = Phone;

        await rig.Send(HttpMethod.Post, "/v1/register?secret=hunter2");
        await rig.Get("/v1/devices?token=abc");

        Assert.Equal(["pixel register", "pixel devices"], rig.Log);
    }

    // ---- the registry file on its own

    [Fact] // spec Hub: a missing file is an empty list, not malformed
    public void A_missing_registry_is_empty()
    {
        using var dir = new TempDir();
        var loaded = new RegistryStore(dir.Combine("nope", "registry.json")).Load();
        Assert.Empty(loaded.Devices);
        Assert.False(loaded.Malformed);
    }

    [Fact] // spec Hub: a missing file in an existing directory is empty too, and the first register creates it
    public void A_missing_registry_file_in_an_existing_directory_is_empty_and_registrable()
    {
        using var dir = new TempDir();
        var store = new RegistryStore(dir.Combine("registry.json"));

        Assert.False(store.Load().Malformed);
        Assert.True(store.Register(Phone));
        Assert.Equal([new RegistryDevice("node-phone", "pixel")], store.Load().Devices);
    }

    [Theory] // spec Hub: a file that doesn't parse is malformed (so it is never written over)
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"devices":5}""")]
    public void A_registry_that_does_not_parse_is_malformed(string content)
    {
        using var dir = new TempDir();
        var path = dir.Combine("registry.json");
        File.WriteAllText(path, content);
        var store = new RegistryStore(path);

        Assert.True(store.Load().Malformed);
        Assert.False(store.Register(Phone));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact] // spec Hub: the file is hand-edited; a blank or empty row is skipped, not read as a device, and the rest still count
    public void Rows_without_an_id_or_a_name_are_skipped()
    {
        using var dir = new TempDir();
        var path = dir.Combine("registry.json");
        File.WriteAllText(path, """{"devices":[{"id":"","name":"x"},{"id":"a","name":""},null,{"id":"node-phone","name":"pixel"}]}""");

        var loaded = new RegistryStore(path).Load();

        Assert.False(loaded.Malformed);
        Assert.Equal([new RegistryDevice("node-phone", "pixel")], loaded.Devices);
    }

    [Fact] // spec Hub (A's settings rule): a file that can't be read is treated like a malformed one, so a register never writes over it
    public void A_registry_that_cannot_be_read_is_never_written_over()
    {
        using var dir = new TempDir();
        var path = dir.Combine("registry.json");
        const string content = """{"devices":[{"id":"node-ipad","name":"ipad"}]}""";
        File.WriteAllText(path, content);
        var store = new RegistryStore(path);

        bool registered;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(store.Load().Malformed);
            registered = store.Register(Phone);
        }

        Assert.False(registered);
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ---- binding to the tailnet address

    [Fact] // spec Hub: listens on the Tailscale address found at runtime, with a re-check that rebinds and stops when Tailscale goes
    public async Task The_host_rebinds_when_the_address_changes_and_stops_when_tailscale_goes_away()
    {
        var tailscale = new FakeTailscale { Status = new TailnetStatus(true, IPAddress.Parse("127.0.0.1"), []) };
        using var dir = new TempDir();
        var log = new List<string>();
        var options = new HubOptions { Tailscale = tailscale, Registry = new RegistryStore(dir.Combine("registry.json")) };
        using var stop = new CancellationTokenSource();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        var running = HubHost.RunAsync(options, 0, TimeSpan.FromMilliseconds(100), line => { lock (log) log.Add(line); }, stop.Token);

        int PortOfListening(int nth)
        {
            lock (log) return log.Count(l => l.StartsWith("listening on port ")) > nth ? int.Parse(log.Where(l => l.StartsWith("listening on port ")).ElementAt(nth)[18..]) : 0;
        }
        async Task<bool> Answers(string ip, int port)
        {
            try { return (await http.GetAsync($"http://{ip}:{port}/v1/hello")).IsSuccessStatusCode; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
        }

        Assert.True(await Wait.Until(() => PortOfListening(0) != 0));
        var first = PortOfListening(0);
        Assert.True(await Answers("127.0.0.1", first));

        tailscale.Status = new TailnetStatus(true, IPAddress.Parse("127.0.0.2"), []);
        Assert.True(await Wait.Until(() => PortOfListening(1) != 0));
        var second = PortOfListening(1);
        Assert.True(await Answers("127.0.0.2", second));
        Assert.False(await Answers("127.0.0.1", first));

        tailscale.Status = TailnetStatus.Disconnected;
        Assert.True(await Wait.Until(() => !Answers("127.0.0.2", second).GetAwaiter().GetResult()));
        // Stopped because Tailscale went away, not because a start on no address failed: a few re-checks
        // later there is still no attempt to listen.
        await Task.Delay(400);
        lock (log) Assert.DoesNotContain(log, l => l.StartsWith("can't listen"));

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
