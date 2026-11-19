using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Tailshare.Core;

/// <summary>What is being sent: some text, or a set of files and folders.</summary>
public abstract record SendContent
{
    public sealed record Text(string Value) : SendContent;
    public sealed record Paths(IReadOnlyList<string> Items) : SendContent;
}

public enum SendStage { Preparing, Waiting, Sending }

public sealed record SendProgress(SendStage Stage, long BytesSent, long BytesTotal);

public enum SendOutcome { Sent, Declined, DidntRespond, Failed, Cancelled }

/// <summary>Something the device list offers to send to: a computer we can reach, or a phone or iPad the hub knows.</summary>
public sealed record Target(Identity Identity, PeerInfo? Computer)
{
    public bool IsMobile => Computer is null;
}

/// <param name="Mobiles">Phones and iPads from the hub; empty when there is no hub or it didn't answer.</param>
public sealed record DeviceList(IReadOnlyList<PeerInfo> Computers, IReadOnlyList<Identity> Mobiles);

public sealed class Sender : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _port;

    // Settable so tests can use short ones.
    public TimeSpan OfferTimeout { get; init; } = Protocol.OfferTimeout;
    public TimeSpan HelloTimeout { get; init; } = Protocol.HelloTimeout;

    public Sender(int port = Protocol.DefaultPort)
    {
        _port = port;
        // Timeouts are per request (below): an upload of any size must not hit a client-wide one.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    private Uri Url(PeerInfo to, string path) => new($"http://{to.Ip}:{_port}{path}");

    /// <summary>The online peers that answer <c>hello</c> as a tailshare computer (protocol 1, not a hub), probed in parallel.</summary>
    public async Task<IReadOnlyList<PeerInfo>> DiscoverAsync(TailnetStatus status, CancellationToken ct) =>
        (await ProbeAsync(status, ct)).Where(a => a.Hello.Hub != true).Select(a => a.Peer).ToList();

    /// <summary>
    /// The computers, plus the phones and iPads of the hub. The hello probe already reaches every
    /// online peer, so finding a hub costs nothing extra; only listing its phones is another request.
    /// A hub that is missing or slow costs the phones and nothing else.
    /// </summary>
    /// <param name="hubSetting">The settings file's <c>hub</c>: a machine name or Tailscale IPv4, or null to use whichever hub answered.</param>
    public async Task<DeviceList> DiscoverDevicesAsync(TailnetStatus status, string? hubSetting, CancellationToken ct)
    {
        var answers = await ProbeAsync(status, ct);
        var computers = answers.Where(a => a.Hello.Hub != true).Select(a => a.Peer).ToList();

        var hub = hubSetting is null
            ? answers.Where(a => a.Hello.Hub == true).Select(a => a.Peer).OrderBy(p => p.Identity.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : FindConfiguredHub(status, hubSetting);

        var mobiles = hub is null ? [] : await FetchMobilesAsync(hub, ct);
        // A device is listed once: a computer that also turns up as a phone stays a computer.
        return new(computers, mobiles.Where(m => computers.All(c => c.Identity.Id != m.Id)).ToList());
    }

    private static PeerInfo? FindConfiguredHub(TailnetStatus status, string setting)
    {
        if (System.Net.IPAddress.TryParse(setting, out var ip))
        {
            // An address needs no lookup, so it works for a hub Tailscale lists under another name too.
            return status.Peers.FirstOrDefault(p => p.Ip.Equals(ip)) ?? new PeerInfo(new Identity("", setting), ip, true);
        }
        return status.Peers.FirstOrDefault(p => p.Online && p.Identity.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<Identity>> FetchMobilesAsync(PeerInfo hub, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        try
        {
            var devices = await _http.GetFromJsonAsync<HubDevices>(Url(hub, "/v1/devices"), Protocol.Json, timeout.Token);
            return (devices?.Mobiles ?? []).Where(m => m is not null && !string.IsNullOrEmpty(m.Id) && !string.IsNullOrEmpty(m.Name)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private sealed record Answer(PeerInfo Peer, HelloResponse Hello);

    private async Task<IReadOnlyList<Answer>> ProbeAsync(TailnetStatus status, CancellationToken ct)
    {
        var probes = status.Peers.Where(p => p.Online).Select(async p => await HelloAsync(p, ct) is { } hello ? new Answer(p, hello) : null);
        var found = await Task.WhenAll(probes);
        return found.Where(a => a is not null).Select(a => a!).ToList();
    }

    private async Task<HelloResponse?> HelloAsync(PeerInfo peer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        try
        {
            var hello = await _http.GetFromJsonAsync<HelloResponse>(Url(peer, "/v1/hello"), Protocol.Json, timeout.Token);
            return hello is { App: Protocol.AppName, Protocol: Protocol.Version } ? hello : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<SendOutcome> SendAsync(PeerInfo to, SendContent content, IProgress<SendProgress>? progress, CancellationToken ct)
    {
        string? zipPath = null;
        try
        {
            Offer offer;
            Func<Stream> openBody;

            switch (content)
            {
                case SendContent.Text t:
                    var bytes = Encoding.UTF8.GetBytes(t.Value);
                    offer = new Offer(OfferKind.Text, null, 1, false, bytes.Length, bytes.Length);
                    openBody = () => new MemoryStream(bytes, writable: false);
                    break;

                case SendContent.Paths p:
                    if (p.Items.Count == 1 && File.Exists(p.Items[0]))
                    {
                        var path = p.Items[0];
                        var size = new FileInfo(path).Length;
                        offer = new Offer(OfferKind.File, Path.GetFileName(path), 1, false, size, size);
                        openBody = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    else
                    {
                        progress?.Report(new SendProgress(SendStage.Preparing, 0, 0));
                        var zip = zipPath = Path.Combine(Path.GetTempPath(), $"tailshare-{Guid.NewGuid():N}.zip");
                        var info = await Task.Run(() => Bundles.Create(p.Items, zip, ct), ct);
                        offer = new Offer(OfferKind.Bundle, info.Name, info.Items, info.IsFolder, new FileInfo(zip).Length, info.TotalSize);
                        openBody = () => new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    break;

                default:
                    return SendOutcome.Failed;
            }

            progress?.Report(new SendProgress(SendStage.Waiting, 0, offer.Bytes ?? 0));
            var token = await OfferAsync(to, offer, ct);
            if (token.Outcome is not null) return token.Outcome.Value;

            return await UploadAsync(to, token.Token!, offer, openBody, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SendOutcome.Cancelled;
        }
        // JsonException and NotSupportedException: a peer that answers 200 with a body that isn't the
        // expected JSON. Uncaught, they escaped into the app's click handler and would end the process.
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or NotSupportedException)
        {
            return SendOutcome.Failed;
        }
        finally
        {
            if (zipPath is not null) { try { File.Delete(zipPath); } catch (IOException) { } }
        }
    }

    private sealed record OfferResult(string? Token, SendOutcome? Outcome);

    private async Task<OfferResult> OfferAsync(PeerInfo to, Offer offer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OfferTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(Url(to, "/v1/offer"), offer, Protocol.Json, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, SendOutcome.DidntRespond);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<TokenResponse>(Protocol.Json, ct);
                return string.IsNullOrEmpty(body?.Token) ? new(null, SendOutcome.Failed) : new(body.Token, null);
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                ErrorResponse? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Protocol.Json, ct); }
                catch (System.Text.Json.JsonException) { }

                return error?.Error switch
                {
                    // A blocked sender learns no more than a declined one.
                    "declined" or "blocked" => new(null, SendOutcome.Declined),
                    "timeout" => new(null, SendOutcome.DidntRespond),
                    _ => new(null, SendOutcome.Failed),
                };
            }
            return new(null, SendOutcome.Failed);
        }
    }

    private async Task<SendOutcome> UploadAsync(PeerInfo to, string token, Offer offer, Func<Stream> openBody,
        IProgress<SendProgress>? progress, CancellationToken ct)
    {
        await using var body = openBody();
        var content = new StreamContent(new ProgressStream(body, n => progress?.Report(new SendProgress(SendStage.Sending, n, offer.Bytes ?? 0))));
        content.Headers.ContentLength = offer.Bytes;

        progress?.Report(new SendProgress(SendStage.Sending, 0, offer.Bytes ?? 0));
        using var response = await _http.PutAsync(Url(to, "/v1/upload/" + Uri.EscapeDataString(token)), content, ct);
        return response.IsSuccessStatusCode ? SendOutcome.Sent : SendOutcome.Failed;
    }

    /// <summary>Reports how much has been read, which is how much has been handed to the network.</summary>
    private sealed class ProgressStream(Stream inner, Action<long> report) : Stream
    {
        private long _read;
        private long _lastReport = Environment.TickCount64;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            _read += n;
            var now = Environment.TickCount64;
            if (n == 0 || now - _lastReport > 250)
            {
                _lastReport = now;
                report(_read);
            }
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
