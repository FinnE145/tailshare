using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Tailshare.Core;

public sealed record PeerInfo(Identity Identity, IPAddress Ip, bool Online);

/// <param name="Connected">False when Tailscale is down, logged out or has no IPv4: nothing can be received or sent.</param>
public sealed record TailnetStatus(bool Connected, IPAddress? SelfIp, IReadOnlyList<PeerInfo> Peers)
{
    public static TailnetStatus Disconnected { get; } = new(false, null, []);
}

public interface ITailscale
{
    Task<TailnetStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Who is behind a tailnet address, or null if Tailscale doesn't know it.</summary>
    Task<Identity?> WhoisAsync(IPAddress ip, CancellationToken ct);
}

/// <summary>Reads the JSON the Tailscale CLI prints. Separate from the process call so it can be tested on fixtures.</summary>
public static class TailscaleJson
{
    public static TailnetStatus ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("BackendState", out var state) || state.GetString() != "Running")
                return TailnetStatus.Disconnected;
            if (!root.TryGetProperty("Self", out var self)) return TailnetStatus.Disconnected;

            var selfIp = FirstTailnetIPv4(self);
            if (selfIp is null) return TailnetStatus.Disconnected;

            var peers = new List<PeerInfo>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in peerMap.EnumerateObject())
                {
                    var p = entry.Value;
                    var ip = FirstTailnetIPv4(p);
                    var id = p.TryGetProperty("ID", out var idEl) ? idEl.GetString() : null;
                    var dns = p.TryGetProperty("DNSName", out var dnsEl) ? dnsEl.GetString() : null;
                    if (ip is null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(dns)) continue;

                    var online = p.TryGetProperty("Online", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                    peers.Add(new PeerInfo(new Identity(id, FirstLabel(dns)), ip, online));
                }
            }
            return new TailnetStatus(true, selfIp, peers);
        }
        catch (JsonException) { return TailnetStatus.Disconnected; }
    }

    public static Identity? ParseWhois(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Node", out var node)) return null;
            var id = node.TryGetProperty("StableID", out var idEl) ? idEl.GetString() : null;
            var name = node.TryGetProperty("ComputedName", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;
            return new Identity(id, name);
        }
        catch (JsonException) { return null; }
    }

    // "my-laptop.tailnet-name.ts.net." -> "my-laptop"
    private static string FirstLabel(string dnsName) => dnsName.TrimEnd('.').Split('.')[0];

    private static IPAddress? FirstTailnetIPv4(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) return null;
        foreach (var el in ips.EnumerateArray())
        {
            if (IPAddress.TryParse(el.GetString(), out var ip) && IsTailnetIPv4(ip)) return ip;
        }
        return null;
    }

    /// <summary>100.64.0.0/10, the carrier-grade NAT range Tailscale hands out.</summary>
    public static bool IsTailnetIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }
}

public sealed class CliTailscale : ITailscale
{
    private const string WindowsInstallPath = @"C:\Program Files\Tailscale\tailscale.exe";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public async Task<TailnetStatus> GetStatusAsync(CancellationToken ct)
    {
        var json = await RunAsync(["status", "--json"], ct);
        return json is null ? TailnetStatus.Disconnected : TailscaleJson.ParseStatus(json);
    }

    public async Task<Identity?> WhoisAsync(IPAddress ip, CancellationToken ct)
    {
        var json = await RunAsync(["whois", "--json", ip.ToString()], ct);
        return json is null ? null : TailscaleJson.ParseWhois(json);
    }

    // Null on any failure: a missing CLI, a non-zero exit (whois of an unknown address) and a
    // timeout all mean "no answer", and callers treat no answer as no identity.
    private static async Task<string?> RunAsync(string[] args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);

        try
        {
            return await StartAndRead("tailscale", args, timeout.Token);
        }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows() && File.Exists(WindowsInstallPath))
        {
            try { return await StartAndRead(WindowsInstallPath, args, timeout.Token); }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<string?> StartAndRead(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            _ = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var text = await stdout;
            return proc.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
