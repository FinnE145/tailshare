using Tailshare.Core;
using Tailshare.Hub;

// The hub: a device list and a registry of phones and iPads, run on an always-on machine on the tailnet.
//   Tailshare.Hub [--data <dir>]
// <dir> holds registry.json; the container passes /data. It never receives or sends files.

var data = ValueAfter(args, "--data") ?? "data";
var options = new HubOptions
{
    Tailscale = new CliTailscale(),
    Registry = new RegistryStore(Path.Combine(data, "registry.json")),
    Log = Log,
};

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
// `docker stop` sends SIGTERM, which nothing else here is listening for.
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });

Log($"hub starting, data in {Path.GetFullPath(data)}");
await HubHost.RunAsync(options, Protocol.DefaultPort, TimeSpan.FromSeconds(10), Log, stop.Token);
return 0;

static void Log(string line) => Console.WriteLine($"{DateTime.UtcNow:s}Z {line}");

static string? ValueAfter(string[] a, string flag)
{
    var i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
