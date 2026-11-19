using System.Net;
using System.Net.Http.Json;
using Tailshare.Core;

namespace Tailshare.Tests;

internal static class Wait
{
    public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 4000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tailshare-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class FakeTailscale : ITailscale
{
    public static readonly Identity Sender = new("node-sender", "sender-box");

    /// <summary>Who whois reports for any address; null makes whois fail.</summary>
    public Identity? Who { get; set; } = Sender;

    /// <summary>What status reports; tests that need peers set it.</summary>
    public TailnetStatus Status { get; set; } = TailnetStatus.Disconnected;

    public Task<TailnetStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public Task<Identity?> WhoisAsync(IPAddress ip, CancellationToken ct) => Task.FromResult(Who);
}

internal sealed class FakeTransfer(Offer offer) : IIncomingTransfer
{
    private readonly CancellationTokenSource _cts = new();

    public Offer Offer { get; } = offer;
    public int Ended { get; private set; }
    public long LastProgress { get; private set; }
    public CancellationToken Cancelled => _cts.Token;

    public void Cancel() => _cts.Cancel();
    public void Progress(long bytesReceived) => LastProgress = bytesReceived;
    public void End() => Ended++;
}

internal sealed class FakeHost : IReceiverHost
{
    public Func<Identity, Offer, CancellationToken, Task<bool>> Ask { get; set; } = (_, _, _) => Task.FromResult(true);

    public int AskCalls;
    public bool AskWasCancelled;
    public List<FakeTransfer> Incoming { get; } = [];
    public List<(Identity From, Offer Offer, ReceivedItem Item)> Received { get; } = [];

    public async Task<bool> AskFirstContactAsync(Identity from, Offer offer, CancellationToken ct)
    {
        Interlocked.Increment(ref AskCalls);
        try { return await Ask(from, offer, ct); }
        catch (OperationCanceledException) { AskWasCancelled = true; throw; }
    }

    public IIncomingTransfer BeginIncoming(Identity from, Offer offer)
    {
        var t = new FakeTransfer(offer);
        lock (Incoming) Incoming.Add(t);
        return t;
    }

    void IReceiverHost.Received(Identity from, Offer offer, ReceivedItem item)
    {
        lock (Received) Received.Add((from, offer, item));
    }
}

/// <summary>An in-process receiver on loopback with whois stubbed, a sender aimed at it, and temp folders.</summary>
internal sealed class Rig : IAsyncDisposable
{
    private readonly TempDir _dir = new();

    public string Downloads { get; }
    public SettingsStore Settings { get; }
    public FakeHost Host { get; } = new();
    public FakeTailscale Tailscale { get; } = new();
    public PickupStore Pickups { get; private set; } = null!;
    public Receiver Receiver { get; private set; } = null!;
    public Sender Sender { get; private set; } = null!;
    public HttpClient Http { get; } = new();
    public PeerInfo Peer => new(new Identity("node-receiver", "receiver-box"), IPAddress.Loopback, true);

    public string Root => _dir.Path;

    public string HoldTemp => _dir.Combine("hold-temp");

    private Rig()
    {
        Downloads = _dir.Combine("downloads");
        Directory.CreateDirectory(Downloads);
        Settings = new SettingsStore(_dir.Combine("settings.json"));
    }

    public static async Task<Rig> StartAsync(TimeSpan? promptTimeout = null, TimeSpan? tokenLifetime = null, TimeSpan? offerTimeout = null,
        TimeSpan? holdLifetime = null)
    {
        var rig = new Rig();
        // The zips of held bundles go in the rig's own folder, so a test can see exactly which are left.
        Directory.CreateDirectory(rig.HoldTemp);
        rig.Pickups = new PickupStore(holdLifetime, rig.HoldTemp);
        rig.Receiver = new Receiver(new ReceiverOptions
        {
            Settings = rig.Settings,
            Tailscale = rig.Tailscale,
            Host = rig.Host,
            DefaultReceiveFolder = rig.Downloads,
            PromptTimeout = promptTimeout ?? Protocol.PromptTimeout,
            TokenLifetime = tokenLifetime ?? Protocol.TokenLifetime,
            Pickups = rig.Pickups,
        });
        await rig.Receiver.StartAsync(IPAddress.Loopback, 0);
        rig.Sender = new Sender(rig.Receiver.Port) { OfferTimeout = offerTimeout ?? Protocol.OfferTimeout };
        return rig;
    }

    public Uri Url(string path) => new($"http://127.0.0.1:{Receiver.Port}{path}");

    public void WriteSettings(string json) => File.WriteAllText(Settings.FilePath, json);

    public void Trust(Identity who) => WriteSettings($$"""{"trusted":[{"id":"{{who.Id}}","name":"{{who.Name}}"}]}""");

    public Task<HttpResponseMessage> PostOffer(Offer offer) => Http.PostAsJsonAsync(Url("/v1/offer"), offer, Protocol.Json);

    public async Task<string> TokenFor(Offer offer)
    {
        using var r = await PostOffer(offer);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<TokenResponse>(Protocol.Json))!.Token;
    }

    public Task<HttpResponseMessage> Put(string token, byte[] body) =>
        Http.PutAsync(Url("/v1/upload/" + token), new ByteArrayContent(body));

    public string[] EverythingInDownloads() => Directory.GetFileSystemEntries(Downloads);

    public static Offer FileOffer(long bytes, string name = "a.txt") => new(OfferKind.File, name, 1, false, bytes, bytes);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        Sender?.Dispose();
        if (Receiver is not null) await Receiver.DisposeAsync();
        _dir.Dispose();
    }
}

/// <summary>Progress that runs inline, so a test can read the reports as soon as SendAsync returns.</summary>
internal sealed class SyncProgress : IProgress<SendProgress>
{
    public List<SendProgress> Reports { get; } = [];
    public void Report(SendProgress value) { lock (Reports) Reports.Add(value); }
}

/// <summary>Peers on loopback addresses 127.0.0.N that all share one port, as real peers share 45611.</summary>
internal sealed class Tailnet : IDisposable
{
    private readonly List<HttpListenerHost> _hosts = [];

    public Tailnet()
    {
        // A free port chosen up front, so a hub can be told where its peers listen before any exist.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    public int Port { get; }

    /// <param name="hello">The answer to /v1/hello.</param>
    /// <param name="devices">The answer to anything else, or null to never answer it.</param>
    public PeerInfo Add(string id, string name, int lastOctet, string hello, string? devices = "", bool online = true)
    {
        var ip = IPAddress.Parse($"127.0.0.{lastOctet}");
        _hosts.Add(new HttpListenerHost(path => path == "/v1/hello" ? hello : devices, ip, Port));
        return new PeerInfo(new Identity(id, name), ip, online);
    }

    public static TailnetStatus Status(params PeerInfo[] peers) => new(true, IPAddress.Parse("100.64.0.1"), peers);

    public Sender Sender(TimeSpan? helloTimeout = null) => new(Port) { HelloTimeout = helloTimeout ?? Protocol.HelloTimeout };

    public void Dispose() { foreach (var h in _hosts) h.Dispose(); }
}
