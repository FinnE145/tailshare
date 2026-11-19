using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tailshare.Core;

namespace Tailshare.Tests;

/// <summary>Step E's additions to the wire protocol: optional bytes, estimated sizes.</summary>
public class ProtocolAdditionsTests
{
    private static readonly Identity Sender = FakeTailscale.Sender;

    /// <summary>A PUT with Transfer-Encoding: chunked and no Content-Length; returns the status code, or -1 if no answer came.</summary>
    private static async Task<int> PutChunked(Rig rig, string token, byte[] body)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Receiver.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PUT /v1/upload/{token} HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n"));
        try
        {
            for (var i = 0; i < body.Length; i += 8192)
            {
                var n = Math.Min(8192, body.Length - i);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{n:X}\r\n"));
                await stream.WriteAsync(body.AsMemory(i, n));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
        }
        catch (IOException)
        {
            // The receiver may answer (413) and hang up before the whole body is out; its answer is still readable.
        }

        var buffer = new byte[64];
        int read;
        try { read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { return -1; }
        var line = Encoding.ASCII.GetString(buffer, 0, read);
        return line.StartsWith("HTTP/1.1 ") ? int.Parse(line.Substring(9, 3)) : -1;
    }

    private static byte[] ZipWithOneEntry(long entrySize)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("big.bin").Open();
            var chunk = new byte[65536];
            for (var left = entrySize; left > 0; left -= chunk.Length) entry.Write(chunk, 0, (int)Math.Min(chunk.Length, left));
        }
        return ms.ToArray();
    }

    // ---- bytes is optional

    [Fact] // spec offer: bytes may be omitted; the upload is then taken at whatever Content-Length it has
    public async Task An_offer_without_bytes_accepts_an_upload_of_any_length()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        // totalSize says 5 and the body is 7: with no bytes, only the body counts.
        var token = await rig.TokenFor(new Offer(OfferKind.File, "a.bin", 1, false, null, 5));

        using var r = await rig.Put(token, [1, 2, 3, 4, 5, 6, 7]);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], File.ReadAllBytes(Path.Combine(rig.Downloads, "a.bin")));
    }

    [Fact] // spec offer: bytes may be omitted; a chunked upload (no Content-Length at all) is taken until it ends
    public async Task An_offer_without_bytes_accepts_a_chunked_upload()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(new Offer(OfferKind.File, "a.bin", 1, false, null, 1));
        var body = Enumerable.Range(0, 30_000).Select(i => (byte)(i * 13)).ToArray();

        Assert.Equal(200, await PutChunked(rig, token, body));

        Assert.Equal(body, File.ReadAllBytes(Path.Combine(rig.Downloads, "a.bin")));
        // The progress notification's total comes from totalSize when there are no bytes.
        Assert.Equal(body.Length, Assert.Single(rig.Host.Incoming).LastProgress);
    }

    [Fact] // spec offer: when bytes is present, Content-Length must equal it, so a chunked upload is 400 with nothing written
    public async Task An_offer_with_bytes_rejects_a_chunked_upload()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(3));

        Assert.Equal(400, await PutChunked(rig, token, [1, 2, 3]));

        Assert.Empty(rig.EverythingInDownloads());
        Assert.Empty(rig.Host.Received);
    }

    [Fact] // spec offer: totalSize may be omitted for text
    public async Task A_text_offer_without_bytes_or_total_size_is_accepted()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(new Offer(OfferKind.Text, null, 1, false, null, null));

        using var r = await rig.Put(token, Encoding.UTF8.GetBytes("from a phone"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("from a phone", Assert.Single(rig.Host.Received).Item.Text);
    }

    [Theory] // spec offer: without bytes the text limit is enforced on the upload, for both Content-Length and chunked bodies; exactly 1 MiB passes
    [InlineData(false, 1024 * 1024, 200)]
    [InlineData(false, 1024 * 1024 + 1, 413)]
    [InlineData(true, 1024 * 1024, 200)]
    [InlineData(true, 1024 * 1024 + 1, 413)]
    public async Task Text_without_bytes_is_limited_to_one_mebibyte_as_it_arrives(bool chunked, int size, int expected)
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(new Offer(OfferKind.Text, null, 1, false, null, null));
        var body = Encoding.ASCII.GetBytes(new string('a', size));

        int status;
        if (chunked)
        {
            status = await PutChunked(rig, token, body);
        }
        else
        {
            using var r = await rig.Put(token, body);
            status = (int)r.StatusCode;
        }

        Assert.Equal(expected, status);
        if (expected == 200) Assert.Equal(size, Assert.Single(rig.Host.Received).Item.Text!.Length);
        else Assert.Empty(rig.Host.Received);
    }

    [Fact] // spec offer: the wire shape of an offer the apps send: bytes present, sizeIsEstimate left out
    public void An_app_offer_is_serialized_with_bytes_and_without_the_estimate_flag()
    {
        var json = JsonSerializer.Serialize(new Offer(OfferKind.File, "a", 1, false, 3, 3), Protocol.Json);
        Assert.Equal("""{"kind":"file","name":"a","items":1,"isFolder":false,"bytes":3,"totalSize":3}""", json);
    }

    [Fact] // spec offer: a phone's offer, with no bytes and an estimate, arrives at the prompt as sent
    public async Task A_phones_offer_reaches_the_prompt_with_its_estimate_flag()
    {
        await using var rig = await Rig.StartAsync();
        Offer? seen = null;
        rig.Host.Ask = (_, o, _) => { seen = o; return Task.FromResult(true); };
        using var content = new StringContent("""{"kind":"bundle","name":"a.jpg","items":2,"isFolder":false,"totalSize":900,"sizeIsEstimate":true}""", Encoding.UTF8, "application/json");

        using var r = await rig.Http.PostAsync(rig.Url("/v1/offer"), content);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new Offer(OfferKind.Bundle, "a.jpg", 2, false, null, 900, true), seen);
    }

    // ---- estimated size

    private const long Total = 4001;
    // 4001 * 1.25 + 1 MiB, rounded down: 4001 + 1000 + 1048576.
    private const long Cap = 1_053_577;

    [Theory] // spec Estimated size: a flagged bundle may write up to totalSize x 1.25 + 1 MiB; one byte more fails and leaves nothing
    [InlineData(Total + 1, 200)]
    [InlineData(Cap, 200)]
    [InlineData(Cap + 1, 400)]
    public async Task A_flagged_bundle_gets_headroom_over_its_total_size(long written, int expected)
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var zip = ZipWithOneEntry(written);
        var token = await rig.TokenFor(new Offer(OfferKind.Bundle, "big.bin", 1, false, zip.Length, Total, SizeIsEstimate: true));

        using var r = await rig.Put(token, zip);

        Assert.Equal(expected, (int)r.StatusCode);
        if (expected == 200) Assert.Equal([Path.Combine(rig.Downloads, "big.bin")], Assert.Single(rig.Host.Received).Item.Paths);
        else Assert.Empty(rig.EverythingInDownloads());
    }

    [Theory] // spec Estimated size: an unflagged bundle keeps A's rule, totalSize exactly; one byte over fails
    [InlineData(Total, 200)]
    [InlineData(Total + 1, 400)]
    public async Task An_unflagged_bundle_gets_no_headroom(long written, int expected)
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var zip = ZipWithOneEntry(written);
        var token = await rig.TokenFor(new Offer(OfferKind.Bundle, "big.bin", 1, false, zip.Length, Total));

        using var r = await rig.Put(token, zip);

        Assert.Equal(expected, (int)r.StatusCode);
        if (expected == 400) Assert.Empty(rig.EverythingInDownloads());
    }

    [Theory] // spec Estimated size: the cap is totalSize x 1.25 + 1 MiB (rounded down) when flagged, totalSize otherwise
    [InlineData(4001L, true, 1_053_577L)]
    [InlineData(0L, true, 1_048_576L)]
    [InlineData(4001L, false, 4001L)]
    [InlineData(0L, false, 0L)]
    public void Extraction_cap(long total, bool estimate, long expected) =>
        Assert.Equal(expected, Protocol.ExtractionCap(new Offer(OfferKind.Bundle, "a", 1, false, null, total, estimate)));
}
