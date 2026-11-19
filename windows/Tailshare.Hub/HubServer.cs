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
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(TimeSpan.FromSeconds(1));
        await _app.DisposeAsync();
        _app = null;
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

        using var sender = new Sender(options.PeerPort) { HelloTimeout = options.HelloTimeout };
        var computers = (await sender.DiscoverAsync(status, ct))
            .Where(p => p.Identity.Id != caller.Id)
            .OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new HubComputer(p.Identity.Name, p.Ip.ToString()))
            .ToList();

        // Offline, expired and removed devices drop out because Tailscale stops showing them online.
        // The name shown is Tailscale's current one, so a rename needs no new registration.
        var online = status.Peers.Where(p => p.Online).GroupBy(p => p.Identity.Id).ToDictionary(g => g.Key, g => g.First().Identity);
        var mobiles = options.Registry.Load().Devices
            .Where(d => d.Id != caller.Id && online.ContainsKey(d.Id))
            .Select(d => online[d.Id])
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(computers, mobiles);
    }
}
