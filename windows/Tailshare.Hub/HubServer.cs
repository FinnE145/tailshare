using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tailshare.Core;

namespace Tailshare.Hub;

public sealed class HubOptions
{
    public required ITailscale Tailscale { get; init; }
    public required RegistryStore Registry { get; init; }

    /// <summary>The port the computers listen on, which the hub probes them on. Every tailshare device uses one port.</summary>
    public int PeerPort { get; init; } = Protocol.DefaultPort;

    public TimeSpan HelloTimeout { get; init; } = Protocol.HelloTimeout;

    /// <summary>How often the hub re-probes the computers in the background. A request never probes: it reads the last result.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>One line per request: the device's name and the endpoint, never anything the request carried.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The hub's HTTP side: a device list and a registry. It never receives or sends files, so offers and
/// uploads are not routes here.
/// </summary>
public sealed class HubServer(HubOptions options) : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly CancellationTokenSource _stop = new();
    private Task? _probeLoop;
    private volatile IReadOnlyList<PeerInfo> _computers = [];

    public int Port { get; private set; }

    public async Task StartAsync(IPAddress ip, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(ip, port);
            k.AddServerHeader = false;
        });

        // Probed before listening, so the first request already has a list to answer from.
        await TryProbeAsync(_stop.Token);

        var app = builder.Build();
        // One terminal handler, so every path and method gets the same whois check first.
        app.Run(HandleAsync);

        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            if (IsAddressInUse(ex)) throw new PortInUseException(ex);
            throw;
        }

        _app = app;
        _probeLoop = Task.Run(ProbeLoopAsync);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        _stop.Cancel();
        if (_probeLoop is not null) await _probeLoop;
        await _app.StopAsync(TimeSpan.FromSeconds(1));
        await _app.DisposeAsync();
        _app = null;
    }

    /// <summary>Probes the computers now and keeps the result for the requests that follow. The background loop calls this; tests call it to avoid waiting for the loop.</summary>
    public Task ProbeNowAsync(CancellationToken ct = default) => TryProbeAsync(ct);

    // A request used to probe every peer itself, and one that didn't answer cost it a whole hello
    // timeout, so every phone Shortcut and every app refresh was slow whenever a computer was off
    // (and the app's wait had to be longer than the hub's). Now only this loop pays that.
    private async Task ProbeLoopAsync()
    {
        using var timer = new PeriodicTimer(options.ProbeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token)) await TryProbeAsync(_stop.Token);
        }
        catch (OperationCanceledException) { }
    }

    // A probe that fails keeps the previous list rather than ending the loop or emptying it.
    private async Task TryProbeAsync(CancellationToken ct)
    {
        try
        {
            var status = await options.Tailscale.GetStatusAsync(ct);
            if (!status.Connected)
            {
                _computers = [];
                return;
            }
            using var sender = new Sender(options.PeerPort) { HelloTimeout = options.HelloTimeout };
            _computers = await sender.DiscoverAsync(status, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log?.Invoke("probe failed: " + ex.GetType().Name);
        }
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }) return true;
        }
        return false;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        var who = ip is null ? null : await options.Tailscale.WhoisAsync(ip, ctx.RequestAborted);
        if (who is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var path = ctx.Request.Path;
        if (HttpMethods.IsGet(ctx.Request.Method) && path == "/v1/hello")
        {
            options.Log?.Invoke($"{who.Name} hello");
            await ctx.Response.WriteAsJsonAsync(new HelloResponse(Protocol.AppName, Protocol.Version, Hub: true), Protocol.Json);
        }
        else if (HttpMethods.IsGet(ctx.Request.Method) && path == "/v1/devices")
        {
            options.Log?.Invoke($"{who.Name} devices");
            await ctx.Response.WriteAsJsonAsync(await DevicesAsync(who, ctx.RequestAborted), Protocol.Json);
        }
        else if (HttpMethods.IsPost(ctx.Request.Method) && path == "/v1/register")
        {
            options.Log?.Invoke($"{who.Name} register");
            // The name stored is whois's, never one the request carries; the body isn't read at all.
            // False is a malformed registry file: the list is still answered, nothing is saved.
            options.Registry.Register(who);
            await ctx.Response.WriteAsJsonAsync(await DevicesAsync(who, ctx.RequestAborted), Protocol.Json);
        }
        else
        {
            options.Log?.Invoke($"{who.Name} unknown");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task<HubDevices> DevicesAsync(Identity caller, CancellationToken ct)
    {
        var status = await options.Tailscale.GetStatusAsync(ct);
        if (!status.Connected) return new([], []);

        // The computers come from the last probe, but a device Tailscale now shows offline is dropped at once.
        var online = status.Peers.Where(p => p.Online).GroupBy(p => p.Identity.Id).ToDictionary(g => g.Key, g => g.First().Identity);
        var computers = _computers
            .Where(p => p.Identity.Id != caller.Id && online.ContainsKey(p.Identity.Id))
            .OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new HubComputer(p.Identity.Name, p.Ip.ToString()))
            .ToList();

        // Offline, expired and removed devices drop out because Tailscale stops showing them online.
        // The name shown is Tailscale's current one, so a rename needs no new registration.
        var mobiles = options.Registry.Load().Devices
            .Where(d => d.Id != caller.Id && online.ContainsKey(d.Id))
            .Select(d => online[d.Id])
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(computers, mobiles);
    }
}
