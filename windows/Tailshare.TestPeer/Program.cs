using System.Net;
using Tailshare.Core;

// Verification tool, never shipped: a headless tailshare peer over Core, run on a Linux box on the tailnet.
//   devices
//   send <device> <path>...        |   send <device> --text "<text>"
//   listen --into <dir> [--accept | --decline | --ask]
// The port is the "port" in its own settings file (testpeer-settings.json beside the executable), like the app's.

var settingsPath = Path.Combine(AppContext.BaseDirectory, "testpeer-settings.json");
var settings = new SettingsStore(settingsPath);
var tailscale = new CliTailscale();

if (args.Length == 0) return Usage();

switch (args[0])
{
    case "devices": return await Devices();
    case "send": return await Send(args[1..]);
    case "listen": return await Listen(args[1..]);
    default: return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: devices | send <device> <path>... | send <device> --text <text> | listen --into <dir> [--accept|--decline|--ask]");
    return 2;
}

async Task<IReadOnlyList<PeerInfo>> Discover(Sender sender)
{
    var status = await tailscale.GetStatusAsync(CancellationToken.None);
    if (!status.Connected) { Console.Error.WriteLine("Tailscale not connected"); return []; }
    return await sender.DiscoverAsync(status, CancellationToken.None);
}

async Task<int> Devices()
{
    using var sender = new Sender(settings.Load().Value.EffectivePort);
    var found = await Discover(sender);
    foreach (var d in found) Console.WriteLine(d.Identity.Name);
    if (found.Count == 0) Console.WriteLine("No devices");
    return 0;
}

async Task<int> Send(string[] a)
{
    if (a.Length < 2) return Usage();
    using var sender = new Sender(settings.Load().Value.EffectivePort);
    var peer = (await Discover(sender)).FirstOrDefault(d => d.Identity.Name.Equals(a[0], StringComparison.OrdinalIgnoreCase));
    if (peer is null) { Console.Error.WriteLine($"{a[0]} not found"); return 1; }

    SendContent content = a[1] == "--text" && a.Length >= 3
        ? new SendContent.Text(a[2])
        : new SendContent.Paths(a[1..].Select(Path.GetFullPath).ToList());

    var last = "";
    var outcome = await sender.SendAsync(peer, content, new Progress<SendProgress>(p =>
    {
        var line = p.Stage == SendStage.Sending ? $"sending {Notices.FormatSize(p.BytesSent)} of {Notices.FormatSize(p.BytesTotal)}" : p.Stage.ToString().ToLowerInvariant();
        if (line != last) Console.WriteLine(line);
        last = line;
    }), CancellationToken.None);

    Console.WriteLine(outcome);
    return outcome == SendOutcome.Sent ? 0 : 1;
}

async Task<int> Listen(string[] a)
{
    var into = ValueAfter(a, "--into");
    if (into is null) return Usage();
    Directory.CreateDirectory(into);
    var mode = a.Contains("--accept") ? "accept" : a.Contains("--decline") ? "decline" : "ask";

    var status = await tailscale.GetStatusAsync(CancellationToken.None);
    if (!status.Connected) { Console.Error.WriteLine("Tailscale not connected"); return 1; }
    var port = settings.Load().Value.EffectivePort;

    await using var receiver = new Receiver(new ReceiverOptions
    {
        Settings = settings,
        Tailscale = tailscale,
        Host = new PeerHost(mode),
        DefaultReceiveFolder = Path.GetFullPath(into),
    });
    try { await receiver.StartAsync(status.SelfIp!, port); }
    catch (PortInUseException) { Console.Error.WriteLine($"port {port} is in use"); return 1; }

    Console.WriteLine($"listening on port {port}, into {Path.GetFullPath(into)} ({mode}); Ctrl+C to stop");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    // Kestrel's console lifetime takes SIGTERM, so `kill <pid>` did nothing; and a background job
    // started by a non-interactive shell ignores SIGINT. Stop on SIGTERM too.
    using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.TrySetResult(); });
    await stop.Task;
    return 0;
}

static string? ValueAfter(string[] a, string flag)
{
    var i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

sealed class PeerHost(string mode) : IReceiverHost
{
    public Task<bool> AskFirstContactAsync(Identity from, Offer offer, CancellationToken ct)
    {
        var what = Notices.FirstContact(from.Name, offer);
        Console.WriteLine($"first contact: {what.Title} {what.Body}");
        switch (mode)
        {
            case "accept": return Task.FromResult(true);
            case "decline": return Task.FromResult(false);
        }

        // --ask: answer on stdin. The prompt is withdrawn (and the answer ignored) on timeout or hang-up.
        return Task.Run(() =>
        {
            Console.Write("accept? [y/N] ");
            var line = Console.ReadLine();
            return !ct.IsCancellationRequested && line?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
        }, CancellationToken.None);
    }

    public IIncomingTransfer BeginIncoming(Identity from, Offer offer)
    {
        Console.WriteLine($"receiving {Notices.Describe(offer)} from {from.Name}");
        return new Transfer();
    }

    public void Received(Identity from, Offer offer, ReceivedItem item)
    {
        // This is a verification tool: the text is test data, and seeing it is the point.
        if (item.Text is not null) Console.WriteLine($"received text from {from.Name}: {item.Text}");
        else Console.WriteLine($"received from {from.Name}: {string.Join(", ", item.Paths)}");
    }

    private sealed class Transfer : IIncomingTransfer
    {
        public CancellationToken Cancelled => CancellationToken.None;
        public void Progress(long bytesReceived) { }
        public void End() { }
    }
}
