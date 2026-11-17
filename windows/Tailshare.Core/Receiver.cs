using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tailshare.Core;

/// <summary>What the host (the app, or the test peer) does for the receiver. Core never shows UI itself.</summary>
public interface IReceiverHost
{
    /// <summary>
    /// Asks the user whether to accept a first-time sender. <paramref name="ct"/> is cancelled when the
    /// request times out or the sender hangs up; the host should then withdraw its prompt.
    /// </summary>
    Task<bool> AskFirstContactAsync(Identity from, Offer offer, CancellationToken ct);

    /// <summary>A file or bundle upload has started. Always followed by <see cref="IIncomingTransfer.End"/>.</summary>
    IIncomingTransfer BeginIncoming(Identity from, Offer offer);

    /// <summary>A transfer finished and its content is in place (or, for text, in <paramref name="item"/>).</summary>
    void Received(Identity from, Offer offer, ReceivedItem item);
}

public interface IIncomingTransfer
{
    /// <summary>Cancelled when the user presses Cancel on the receiving side.</summary>
    CancellationToken Cancelled { get; }

    void Progress(long bytesReceived);

    void End();
}

/// <param name="Paths">Full paths of the top-level items written; empty for text.</param>
/// <param name="Text">The text, for a text transfer.</param>
public sealed record ReceivedItem(IReadOnlyList<string> Paths, string? Text);

public sealed class ReceiverOptions
{
    public required SettingsStore Settings { get; init; }
    public required ITailscale Tailscale { get; init; }
    public required IReceiverHost Host { get; init; }

    /// <summary>Used when the settings file names no receive folder, or one that doesn't exist. The host decides what that is.</summary>
    public required string DefaultReceiveFolder { get; init; }

    public TimeSpan PromptTimeout { get; init; } = Protocol.PromptTimeout;
    public TimeSpan TokenLifetime { get; init; } = Protocol.TokenLifetime;
}

public sealed class PortInUseException(Exception inner) : Exception("The port is already in use", inner);

public sealed class Receiver : IAsyncDisposable
{
    private sealed record Pending(string SenderId, Offer Offer, DateTime ExpiresUtc);

    private readonly ReceiverOptions _o;
    private readonly ConcurrentDictionary<string, Pending> _tokens = new();
    private WebApplication? _app;

    public Receiver(ReceiverOptions options) => _o = options;

    /// <summary>The port actually bound (differs from the one asked for only when 0 was asked for).</summary>
    public int Port { get; private set; }

    /// <summary>Starts listening on <paramref name="ip"/> only; the app passes this device's Tailscale address, never "any".</summary>
    public async Task StartAsync(IPAddress ip, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // No request logging: it would carry file names.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(ip, port);
            k.AddServerHeader = false;
            // Files have no size limit, and a slow link must not trip the minimum-rate cutoff.
            k.Limits.MaxRequestBodySize = null;
            k.Limits.MinRequestBodyDataRate = null;
        });

        var app = builder.Build();
        app.MapGet("/v1/hello", (Delegate)HelloAsync);
        app.MapPost("/v1/offer", (Delegate)OfferAsync);
        app.MapPut("/v1/upload/{token}", (Delegate)UploadAsync);

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
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
        }
        return false;
    }

    // Every request starts here: who is on the other end, according to Tailscale.
    private async Task<Identity?> WhoAsync(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return await _o.Tailscale.WhoisAsync(ip, ctx.RequestAborted);
    }

    private static IResult Error(int status, string error) => Results.Json(new ErrorResponse(error), Protocol.Json, statusCode: status);

    private async Task<IResult> HelloAsync(HttpContext ctx)
    {
        if (await WhoAsync(ctx) is null) return Results.StatusCode(403);
        return Results.Json(new HelloResponse(Protocol.AppName, Protocol.Version), Protocol.Json);
    }

    private async Task<IResult> OfferAsync(HttpContext ctx)
    {
        var who = await WhoAsync(ctx);
        if (who is null) return Results.StatusCode(403);

        // An offer is a few hundred bytes; don't buffer anything bigger.
        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 64 * 1024;
        Offer? offer;
        try
        {
            offer = await JsonSerializer.DeserializeAsync<Offer>(ctx.Request.Body, Protocol.Json, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or IOException)
        {
            return Results.StatusCode(400);
        }
        if (offer is null || !Protocol.IsValid(offer)) return Results.StatusCode(400);

        // Read fresh: the file is hand-edited and edits apply to the next offer.
        var settings = _o.Settings.Load().Value;
        if (settings.IsBlocked(who.Id)) return Error(403, "blocked");
        if (offer.Kind == OfferKind.Text && offer.Bytes > Protocol.TextLimitBytes) return Results.StatusCode(413);

        if (!settings.IsTrusted(who.Id))
        {
            using var prompt = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            prompt.CancelAfter(_o.PromptTimeout);

            bool accepted;
            // WaitAsync: the timeout must hold even for a host that ignores the token (the test
            // peer's --ask blocks on stdin and would otherwise keep the request open for good).
            try { accepted = await _o.Host.AskFirstContactAsync(who, offer, prompt.Token).WaitAsync(prompt.Token); }
            catch (OperationCanceledException) { accepted = false; }

            // A sender that hung up is cancelled too and gets this answer; nobody is there to read it.
            if (prompt.IsCancellationRequested) return Error(403, "timeout");
            if (!accepted) return Error(403, "declined");

            // False means the file is malformed: this transfer goes through, the trust isn't kept.
            _o.Settings.AddTrusted(who);
        }

        return Results.Json(new TokenResponse(IssueToken(who.Id, offer)), Protocol.Json);
    }

    private string IssueToken(string senderId, Offer offer)
    {
        var now = DateTime.UtcNow;
        foreach (var (k, v) in _tokens)
        {
            if (v.ExpiresUtc < now) _tokens.TryRemove(k, out _);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = new Pending(senderId, offer, now + _o.TokenLifetime);
        return token;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<IResult> UploadAsync(HttpContext ctx, string token)
    {
        var who = await WhoAsync(ctx);
        if (who is null) return Results.StatusCode(403);

        // Checked before removing, so a different sender can't burn someone else's token.
        if (!_tokens.TryGetValue(token, out var pending) || pending.SenderId != who.Id) return Results.StatusCode(403);
        if (!_tokens.TryRemove(token, out _)) return Results.StatusCode(403);
        if (pending.ExpiresUtc < DateTime.UtcNow) return Results.StatusCode(403);

        var offer = pending.Offer;
        if (ctx.Request.ContentLength != offer.Bytes) return Results.StatusCode(400);

        return offer.Kind == OfferKind.Text
            ? await ReceiveTextAsync(ctx, who, offer)
            : await ReceiveFileAsync(ctx, who, offer);
    }

    private async Task<IResult> ReceiveTextAsync(HttpContext ctx, Identity who, Offer offer)
    {
        var buffer = new byte[offer.Bytes];
        try
        {
            await ctx.Request.Body.ReadExactlyAsync(buffer, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or BadHttpRequestException)
        {
            return Results.StatusCode(400);
        }

        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer); }
        catch (DecoderFallbackException) { return Results.StatusCode(400); }

        _o.Host.Received(who, offer, new ReceivedItem([], text));
        return Results.Json(new { ok = true }, Protocol.Json);
    }

    private async Task<IResult> ReceiveFileAsync(HttpContext ctx, Identity who, Offer offer)
    {
        var folder = ReceiveFolder();
        var part = Path.Combine(folder, $".tailshare-{Base64Url(RandomNumberGenerator.GetBytes(8))}.part");
        var incoming = _o.Host.BeginIncoming(who, offer);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, incoming.Cancelled);

        IReadOnlyList<string> placed;
        try
        {
            await using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(part, FileAttributes.Hidden);
                await CopyAsync(ctx.Request.Body, fs, incoming, cts.Token);
            }

            if (offer.Kind == OfferKind.Bundle)
            {
                placed = Bundles.Extract(part, folder, offer.TotalSize, cts.Token);
                File.Delete(part);
            }
            else
            {
                placed = [Placement.PlaceFile(folder, offer.Name!, part)];
            }
        }
        catch (Exception ex)
        {
            Placement.Delete(part);

            if (incoming.Cancelled.IsCancellationRequested || ctx.RequestAborted.IsCancellationRequested)
            {
                // Receiver-side Cancel, or the sender went away: close the connection so the sender sees a failure.
                ctx.Abort();
                return Results.StatusCode(499);
            }
            return ex is InvalidDataException ? Results.StatusCode(400) : Results.StatusCode(500);
        }
        finally
        {
            // Before Received, so the host swaps the progress notification for the received one.
            incoming.End();
        }

        _o.Host.Received(who, offer, new ReceivedItem(placed, null));
        return Results.Json(new { ok = true }, Protocol.Json);
    }

    // No length checks here: the upload handler has already required Content-Length to equal the
    // offer's bytes, and Kestrel itself fails the read on a short body and never yields a long one.
    private static async Task CopyAsync(Stream from, Stream to, IIncomingTransfer incoming, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        int n;
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            await to.WriteAsync(buffer.AsMemory(0, n), ct);

            if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = clock.Elapsed;
                incoming.Progress(total);
            }
        }
        incoming.Progress(total);
    }

    private string ReceiveFolder()
    {
        var chosen = _o.Settings.Load().Value.ReceiveFolder;
        return !string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen) ? chosen : _o.DefaultReceiveFolder;
    }
}
