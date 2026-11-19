using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Tailshare.Core;

namespace Tailshare.Tests;

/// <summary>The wire protocol against an in-process receiver on loopback, with whois stubbed.</summary>
public class ProtocolTests
{
    private static readonly Identity Sender = FakeTailscale.Sender;

    private static async Task<SendOutcome> SendText(Rig rig, string text, CancellationToken ct = default) =>
        await rig.Sender.SendAsync(rig.Peer, new SendContent.Text(text), null, ct);

    private static async Task<SendOutcome> SendPaths(Rig rig, params string[] paths) =>
        await rig.Sender.SendAsync(rig.Peer, new SendContent.Paths(paths), null, CancellationToken.None);

    // ---- hello and discovery

    [Fact] // spec GET /v1/hello: 200 {"app":"tailshare","protocol":1}
    public async Task Hello_answers_with_app_and_protocol()
    {
        await using var rig = await Rig.StartAsync();
        using var r = await rig.Http.GetAsync(rig.Url("/v1/hello"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("""{"app":"tailshare","protocol":1}""", await r.Content.ReadAsStringAsync());
    }

    [Fact] // spec Discovery: online peers that answer hello with protocol 1; offline and unreachable ones are left out
    public async Task Discovery_lists_only_online_peers_that_answer()
    {
        await using var rig = await Rig.StartAsync();
        var answers = new PeerInfo(new Identity("p1", "answers"), IPAddress.Parse("127.0.0.1"), true);
        var refuses = new PeerInfo(new Identity("p2", "refuses"), IPAddress.Parse("127.0.0.2"), true);
        var offline = new PeerInfo(new Identity("p3", "offline"), IPAddress.Parse("127.0.0.1"), false);
        var status = new TailnetStatus(true, IPAddress.Parse("100.64.0.1"), [answers, refuses, offline]);

        var found = await rig.Sender.DiscoverAsync(status, CancellationToken.None);

        Assert.Equal([answers], found);
    }

    [Fact] // spec Discovery: a peer that answers hello with another protocol number is not a tailshare device
    public async Task Discovery_ignores_other_protocol_versions()
    {
        using var listener = new HttpListenerHost("""{"app":"tailshare","protocol":2}""");
        using var sender = new Sender(listener.Port);
        var peer = new PeerInfo(new Identity("p1", "future"), IPAddress.Loopback, true);

        var found = await sender.DiscoverAsync(new TailnetStatus(true, null, [peer]), CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact] // spec Discovery: hello must say app "tailshare"; another app speaking "protocol 1" is not listed
    public async Task Discovery_ignores_another_app()
    {
        using var listener = new HttpListenerHost("""{"app":"something-else","protocol":1}""");
        using var sender = new Sender(listener.Port);
        var peer = new PeerInfo(new Identity("p1", "other"), IPAddress.Loopback, true);

        var found = await sender.DiscoverAsync(new TailnetStatus(true, null, [peer]), CancellationToken.None);

        Assert.Empty(found);
    }

    // ---- every request is identified by whois

    [Fact] // spec Wire protocol: whois failure answers 403 on every endpoint and does nothing more
    public async Task Failed_whois_is_403_everywhere_and_nothing_is_prompted_or_written()
    {
        await using var rig = await Rig.StartAsync();
        rig.Tailscale.Who = null;

        using var hello = await rig.Http.GetAsync(rig.Url("/v1/hello"));
        using var offer = await rig.PostOffer(Rig.FileOffer(3));
        using var put = await rig.Put("anything", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.Forbidden, hello.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, offer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(0, rig.Host.AskCalls);
        Assert.Empty(rig.EverythingInDownloads());
    }

    // ---- trust

    [Fact] // spec POST /v1/offer: an unknown device is prompted; Decline -> 403 declined, in neither list
    public async Task Unknown_device_that_is_declined_gets_declined_and_is_remembered_nowhere()
    {
        await using var rig = await Rig.StartAsync();
        rig.Host.Ask = (_, _, _) => Task.FromResult(false);

        using var r = await rig.PostOffer(Rig.FileOffer(3));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("""{"error":"declined"}""", await r.Content.ReadAsStringAsync());
        Assert.Equal(1, rig.Host.AskCalls);
        var s = rig.Settings.Load().Value;
        Assert.False(s.IsTrusted(Sender.Id));
        Assert.False(s.IsBlocked(Sender.Id));
        Assert.False(File.Exists(rig.Settings.FilePath));
    }

    [Fact] // spec POST /v1/offer: the sender sees "declined" as Declined
    public async Task Sender_reports_declined()
    {
        await using var rig = await Rig.StartAsync();
        rig.Host.Ask = (_, _, _) => Task.FromResult(false);
        Assert.Equal(SendOutcome.Declined, await SendText(rig, "hi"));
    }

    [Fact] // spec POST /v1/offer: the prompt gets the sender's whois identity and the offer
    public async Task Prompt_receives_the_whois_identity_and_the_offer()
    {
        await using var rig = await Rig.StartAsync();
        Identity? seenFrom = null;
        Offer? seenOffer = null;
        rig.Host.Ask = (from, offer, _) => { seenFrom = from; seenOffer = offer; return Task.FromResult(true); };
        var offer = new Offer(OfferKind.File, "a.txt", 1, false, 3, 3);

        using var r = await rig.PostOffer(offer);

        Assert.Equal(Sender, seenFrom);
        Assert.Equal(offer, seenOffer);
    }

    [Fact] // spec POST /v1/offer: Accept adds the sender to trusted with its name; the next offer isn't prompted
    public async Task Accept_trusts_the_sender_with_its_name_and_is_not_asked_again()
    {
        await using var rig = await Rig.StartAsync();

        Assert.Equal(SendOutcome.Sent, await SendText(rig, "one"));
        Assert.Equal(SendOutcome.Sent, await SendText(rig, "two"));

        Assert.Equal(1, rig.Host.AskCalls);
        Assert.Equal([Sender], rig.Settings.Load().Value.Trusted);
    }

    [Fact] // spec POST /v1/offer: a trusted device is not prompted
    public async Task Trusted_device_is_not_prompted()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);

        using var r = await rig.PostOffer(Rig.FileOffer(3));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, rig.Host.AskCalls);
    }

    [Fact] // spec POST /v1/offer: blocked wins over trusted, 403 blocked at once with no prompt
    public async Task Blocked_device_is_rejected_without_a_prompt_even_if_also_trusted()
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteSettings($$"""
            {"trusted":[{"id":"{{Sender.Id}}","name":"x"}],"blocked":[{"id":"{{Sender.Id}}","name":"x"}]}
            """);

        using var r = await rig.PostOffer(Rig.FileOffer(3));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("""{"error":"blocked"}""", await r.Content.ReadAsStringAsync());
        Assert.Equal(0, rig.Host.AskCalls);
    }

    [Fact] // spec POST /v1/offer: the sender can't tell blocked from declined
    public async Task Sender_reports_a_block_as_declined()
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteSettings($$"""{"blocked":[{"id":"{{Sender.Id}}","name":"x"}]}""");
        Assert.Equal(SendOutcome.Declined, await SendText(rig, "hi"));
    }

    [Fact] // spec POST /v1/offer: settings are read fresh for every offer, so hand edits apply without a restart
    public async Task A_hand_edit_applies_to_the_next_offer_on_the_same_receiver()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        using (var first = await rig.PostOffer(Rig.FileOffer(3))) Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        rig.WriteSettings($$"""{"blocked":[{"id":"{{Sender.Id}}","name":"x"}]}""");
        using (var second = await rig.PostOffer(Rig.FileOffer(3))) Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);

        rig.WriteSettings("{}");
        rig.Host.Ask = (_, _, _) => Task.FromResult(false);
        using (var third = await rig.PostOffer(Rig.FileOffer(3))) Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Equal(1, rig.Host.AskCalls);
    }

    [Fact] // spec Settings file: malformed -> everyone is prompted, an Accept lets the transfer through but is not saved
    public async Task Malformed_settings_prompt_everyone_and_are_never_overwritten()
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteSettings("{ not json");

        var outcome = await SendText(rig, "hello");

        Assert.Equal(SendOutcome.Sent, outcome);
        Assert.Equal(1, rig.Host.AskCalls);
        Assert.Equal("{ not json", File.ReadAllText(rig.Settings.FilePath));
        Assert.Equal("hello", Assert.Single(rig.Host.Received).Item.Text);
    }

    [Fact] // spec POST /v1/offer: no answer in 120 s (injected short here) -> 403 timeout, prompt withdrawn, not remembered
    public async Task Unanswered_prompt_times_out_and_is_withdrawn()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromMilliseconds(150));
        rig.Host.Ask = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };

        using var r = await rig.PostOffer(Rig.FileOffer(3));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("""{"error":"timeout"}""", await r.Content.ReadAsStringAsync());
        Assert.True(rig.Host.AskWasCancelled);
        Assert.False(rig.Settings.Load().Value.IsTrusted(Sender.Id));
    }

    [Fact] // spec POST /v1/offer: the request stays open only until answered or the timeout, even if the host never answers
    public async Task Prompt_times_out_even_when_the_host_ignores_cancellation()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromMilliseconds(150));
        rig.Host.Ask = (_, _, _) => new TaskCompletionSource<bool>().Task;

        using var r = await rig.PostOffer(Rig.FileOffer(3));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("""{"error":"timeout"}""", await r.Content.ReadAsStringAsync());
        Assert.False(rig.Settings.Load().Value.IsTrusted(Sender.Id));
    }

    [Fact] // spec POST /v1/offer: the sender sees "timeout" as DidntRespond
    public async Task Sender_reports_a_prompt_timeout_as_didnt_respond()
    {
        await using var rig = await Rig.StartAsync(promptTimeout: TimeSpan.FromMilliseconds(150));
        rig.Host.Ask = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };
        Assert.Equal(SendOutcome.DidntRespond, await SendText(rig, "hi"));
    }

    [Fact] // spec POST /v1/offer: if the sender closes the connection while waiting, the prompt is withdrawn
    public async Task Sender_cancelling_while_waiting_withdraws_the_prompt()
    {
        await using var rig = await Rig.StartAsync();
        rig.Host.Ask = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };
        using var cts = new CancellationTokenSource();

        var sending = SendText(rig, "hi", cts.Token);
        Assert.True(await Wait.Until(() => rig.Host.AskCalls == 1));
        cts.Cancel();

        Assert.Equal(SendOutcome.Cancelled, await sending);
        Assert.True(await Wait.Until(() => rig.Host.AskWasCancelled));
        Assert.False(rig.Settings.Load().Value.IsTrusted(Sender.Id));
    }

    [Fact] // spec Sending: the sender's offer timeout is its own (130 s; injected short here) -> DidntRespond
    public async Task Sender_gives_up_waiting_for_an_answer()
    {
        await using var rig = await Rig.StartAsync(offerTimeout: TimeSpan.FromMilliseconds(150));
        rig.Host.Ask = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };
        Assert.Equal(SendOutcome.DidntRespond, await SendText(rig, "hi"));
    }

    [Fact] // spec Sending: a peer that can't be reached is Failed
    public async Task Unreachable_peer_is_failed()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var sender = new Sender(port);
        var peer = new PeerInfo(new Identity("p", "gone"), IPAddress.Loopback, true);

        Assert.Equal(SendOutcome.Failed, await sender.SendAsync(peer, new SendContent.Text("x"), null, CancellationToken.None));
    }

    [Fact] // spec Sending: a peer whose answer isn't the protocol's JSON is Failed, not an exception
    public async Task Peer_answering_an_offer_with_non_json_is_failed()
    {
        using var listener = new HttpListenerHost("not json");
        using var sender = new Sender(listener.Port);
        var peer = new PeerInfo(new Identity("p", "odd"), IPAddress.Loopback, true);

        Assert.Equal(SendOutcome.Failed, await sender.SendAsync(peer, new SendContent.Text("x"), null, CancellationToken.None));
    }

    // ---- offer validation

    [Fact] // spec POST /v1/offer: text over 1 MiB -> 413
    public async Task Text_over_one_mebibyte_is_413_and_not_prompted()
    {
        await using var rig = await Rig.StartAsync();
        var big = new Offer(OfferKind.Text, null, 1, false, 1024 * 1024 + 1, 1024 * 1024 + 1);

        using var r = await rig.PostOffer(big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(0, rig.Host.AskCalls);
    }

    [Fact] // spec POST /v1/offer: text of exactly 1 MiB is allowed
    public async Task Text_of_exactly_one_mebibyte_is_accepted()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        using var r = await rig.PostOffer(new Offer(OfferKind.Text, null, 1, false, 1024 * 1024, 1024 * 1024));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact] // spec POST /v1/offer: files have no size limit beyond text's
    public async Task A_huge_file_offer_is_accepted()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        using var r = await rig.PostOffer(Rig.FileOffer(5L * 1024 * 1024 * 1024));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact] // characterization: an offer body over 64 KB is refused before it is parsed or prompted
    public async Task Offer_body_over_64_KB_is_400()
    {
        await using var rig = await Rig.StartAsync();
        using var r = await rig.PostOffer(Rig.FileOffer(3, name: new string('a', 70_000)));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, rig.Host.AskCalls);
    }

    [Theory] // spec POST /v1/offer: malformed offer -> 400
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"kind":"video","name":"a","items":1,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":1,"name":"a","items":1,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":"file","items":1,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":"file","name":"a","items":2,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":"file","name":"a","items":0,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":"file","name":"a","items":1,"bytes":-1,"totalSize":1}""")]
    [InlineData("""{"kind":"file","name":"a","items":1,"bytes":1,"totalSize":-1}""")]
    [InlineData("""{"kind":"text","items":2,"bytes":1,"totalSize":1}""")]
    [InlineData("""{"kind":"bundle","items":1,"bytes":1,"totalSize":1}""")]
    // spec Offer additions: totalSize stays required for a file and a bundle (only text may leave it out)
    [InlineData("""{"kind":"file","name":"a","items":1,"bytes":1}""")]
    [InlineData("""{"kind":"bundle","name":"a","items":1,"bytes":1}""")]
    public async Task Malformed_offer_is_400_and_not_prompted(string body)
    {
        await using var rig = await Rig.StartAsync();
        using var r = await rig.Http.PostAsync(rig.Url("/v1/offer"), new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, rig.Host.AskCalls);
    }

    // ---- tokens

    [Fact] // spec Token: single use
    public async Task Token_works_once()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(3));

        using var first = await rig.Put(token, [1, 2, 3]);
        using var second = await rig.Put(token, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Single(rig.EverythingInDownloads());
    }

    [Fact] // spec Token: 128 random bits as base64url, a new one each time
    public async Task Tokens_are_22_chars_of_base64url_and_differ()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var a = await rig.TokenFor(Rig.FileOffer(3));
        var b = await rig.TokenFor(Rig.FileOffer(3));

        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", a);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", b);
    }

    [Fact] // spec Token: bound to the sender's StableID; another identity presenting it gets 403, nothing written
    public async Task Token_presented_by_another_identity_is_403_and_stays_usable_by_its_owner()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(3));

        rig.Tailscale.Who = new Identity("node-other", "other-box");
        using var thief = await rig.Put(token, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.Forbidden, thief.StatusCode);
        Assert.Empty(rig.EverythingInDownloads());

        rig.Tailscale.Who = Sender;
        using var owner = await rig.Put(token, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact] // spec Token: expires if no upload starts within its lifetime (60 s; injected short here)
    public async Task Expired_token_is_403_and_nothing_is_written()
    {
        await using var rig = await Rig.StartAsync(tokenLifetime: TimeSpan.FromMilliseconds(50));
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(3));
        await Task.Delay(300);

        using var r = await rig.Put(token, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(rig.EverythingInDownloads());
    }

    [Fact] // spec Token: an unknown token is 403
    public async Task Unknown_token_is_403()
    {
        await using var rig = await Rig.StartAsync();
        using var r = await rig.Put("nonsense", [1, 2, 3]);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ---- upload integrity

    [Fact] // spec PUT /v1/upload: Content-Length must equal the offer's bytes, else rejected with no file left
    public async Task Content_length_not_matching_the_offer_is_rejected_with_no_file_left()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(10));

        using var r = await rig.Put(token, [1, 2, 3, 4, 5]);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(rig.EverythingInDownloads());
        Assert.Empty(rig.Host.Received);
        // Rejected before the transfer began, not after receiving the bytes.
        Assert.Empty(rig.Host.Incoming);
    }

    [Fact] // spec PUT /v1/upload: a dropped connection deletes the temp file and the sender's file never appears
    public async Task Short_body_with_a_dropped_connection_leaves_nothing()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(10));

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, rig.Receiver.Port);
            var head = $"PUT /v1/upload/{token} HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\nabcde";
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
            Assert.True(await Wait.Until(() => rig.Host.Incoming.Count == 1));
        }

        Assert.True(await Wait.Until(() => rig.Host.Incoming[0].Ended == 1 && rig.EverythingInDownloads().Length == 0));
        Assert.Empty(rig.Host.Received);
    }

    [Fact] // spec Receiving/Notifications: the receiver's Cancel stops the transfer, deletes the temp file, the sender sees a failure
    public async Task Receiver_side_cancel_removes_the_partial_file_and_closes_the_connection()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(Rig.FileOffer(10));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Receiver.Port);
        var stream = tcp.GetStream();
        var head = $"PUT /v1/upload/{token} HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\nabcde";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        Assert.True(await Wait.Until(() => rig.Host.Incoming.Count == 1 && rig.EverythingInDownloads().Length == 1));

        // spec PUT /v1/upload: a hidden temp file named .tailshare-<random>.part in the receive folder
        var temp = rig.EverythingInDownloads()[0];
        Assert.Matches(@"^\.tailshare-.+\.part$", Path.GetFileName(temp));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(temp).HasFlag(FileAttributes.Hidden));

        rig.Host.Incoming[0].Cancel();

        Assert.True(await Wait.Until(() => rig.Host.Incoming[0].Ended == 1 && rig.EverythingInDownloads().Length == 0));
        Assert.Empty(rig.Host.Received);
        var buffer = new byte[256];
        int read;
        try { read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (IOException) { read = 0; }
        Assert.Equal(0, read);
    }

    [Fact] // spec PUT /v1/upload: text that isn't valid UTF-8 is rejected
    public async Task Text_that_is_not_utf8_is_400()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(new Offer(OfferKind.Text, null, 1, false, 2, 2));

        using var r = await rig.Put(token, [0xC3, 0x28]);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(rig.Host.Received);
    }

    [Fact] // spec Bundles: a bundle with an escaping entry fails over the wire and leaves nothing
    public async Task Bundle_with_a_zip_slip_entry_is_rejected_over_the_wire()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ok.txt").Open())) w.Write("fine");
            using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("bad");
        }
        var bytes = ms.ToArray();
        var token = await rig.TokenFor(new Offer(OfferKind.Bundle, "ok.txt", 2, false, bytes.Length, 1000));

        using var r = await rig.Put(token, bytes);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(rig.EverythingInDownloads());
        Assert.False(File.Exists(Path.Combine(rig.Root, "escaped.txt")));
        Assert.Empty(rig.Host.Received);
    }

    // ---- round trips through the real sender

    [Fact] // spec Sending: one plain file is sent as kind "file"; it lands in the receive folder, byte for byte
    public async Task A_file_round_trips_as_a_file()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var src = Path.Combine(rig.Root, "photo.bin");
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(src, content);

        Assert.Equal(SendOutcome.Sent, await SendPaths(rig, src));

        var (from, offer, item) = Assert.Single(rig.Host.Received);
        Assert.Equal(Sender, from);
        Assert.Equal(new Offer(OfferKind.File, "photo.bin", 1, false, 300_000, 300_000), offer);
        var landed = Assert.Single(item.Paths);
        Assert.Equal(Path.Combine(rig.Downloads, "photo.bin"), landed);
        Assert.Equal(content, File.ReadAllBytes(landed));
        Assert.Equal([landed], rig.EverythingInDownloads());
        // The temp file is hidden; the received file must not be (Verify found it was).
        Assert.False(File.GetAttributes(landed).HasFlag(FileAttributes.Hidden));
        // The last progress report is the whole file, so a progress bar ends full.
        Assert.Equal(300_000, Assert.Single(rig.Host.Incoming).LastProgress);
    }

    [Fact] // spec Receiving/Names: a taken file name gets a suffix, the old file is untouched
    public async Task A_second_file_with_the_same_name_is_suffixed()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        File.WriteAllText(Path.Combine(rig.Downloads, "photo.txt"), "old");
        var src = Path.Combine(rig.Root, "photo.txt");
        File.WriteAllText(src, "new");

        await SendPaths(rig, src);

        Assert.Equal("old", File.ReadAllText(Path.Combine(rig.Downloads, "photo.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(rig.Downloads, "photo (1).txt")));
    }

    [Fact] // spec Receiving/Names: a hostile name from the peer is sanitized before use
    public async Task A_hostile_file_name_in_an_offer_lands_inside_the_receive_folder()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var token = await rig.TokenFor(new Offer(OfferKind.File, "..\\..\\evil.txt", 1, false, 3, 3));

        using var r = await rig.Put(token, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([Path.Combine(rig.Downloads, "evil.txt")], rig.EverythingInDownloads());
    }

    [Fact] // spec Sending: a folder is a bundle with isFolder; it extracts under its own name with its contents
    public async Task A_folder_round_trips_as_a_bundle()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var folder = Path.Combine(rig.Root, "Photos");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "bbbbb");

        Assert.Equal(SendOutcome.Sent, await SendPaths(rig, folder));

        var (_, offer, item) = Assert.Single(rig.Host.Received);
        Assert.Equal(OfferKind.Bundle, offer.Kind);
        Assert.Equal("Photos", offer.Name);
        Assert.Equal(1, offer.Items);
        Assert.True(offer.IsFolder);
        Assert.Equal(8, offer.TotalSize);
        Assert.Equal([Path.Combine(rig.Downloads, "Photos")], item.Paths);
        Assert.Equal("bbbbb", File.ReadAllText(Path.Combine(rig.Downloads, "Photos", "sub", "b.txt")));
        Assert.Single(rig.EverythingInDownloads());
    }

    [Fact] // spec Sending: two files are a bundle
    public async Task Two_files_are_sent_as_a_bundle()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        File.WriteAllText(Path.Combine(rig.Root, "one.txt"), "1");
        File.WriteAllText(Path.Combine(rig.Root, "two.txt"), "22");

        await SendPaths(rig, Path.Combine(rig.Root, "one.txt"), Path.Combine(rig.Root, "two.txt"));

        var (_, offer, item) = Assert.Single(rig.Host.Received);
        Assert.Equal(new Offer(OfferKind.Bundle, "one.txt", 2, false, offer.Bytes, 3), offer);
        Assert.Equal(2, item.Paths.Count);
        Assert.Equal("22", File.ReadAllText(Path.Combine(rig.Downloads, "two.txt")));
    }

    [Fact] // spec Sending: a file plus a folder is a bundle of two items, isFolder false
    public async Task A_file_and_a_folder_are_sent_as_a_bundle()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        File.WriteAllText(Path.Combine(rig.Root, "one.txt"), "1");
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "xx");

        await SendPaths(rig, Path.Combine(rig.Root, "one.txt"), Path.Combine(rig.Root, "Dir"));

        var (_, offer, _) = Assert.Single(rig.Host.Received);
        Assert.Equal(OfferKind.Bundle, offer.Kind);
        Assert.Equal(2, offer.Items);
        Assert.False(offer.IsFolder);
        Assert.Equal("xx", File.ReadAllText(Path.Combine(rig.Downloads, "Dir", "x.txt")));
    }

    [Fact] // spec Sending: a lone folder is a bundle even though it is "one path"; the sender's temp zip is deleted
    public async Task The_senders_temp_zip_is_deleted_after_sending()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var folder = Path.Combine(rig.Root, "Dir");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
        var before = Directory.GetFiles(Path.GetTempPath(), "tailshare-*.zip").Length;

        await SendPaths(rig, folder);

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "tailshare-*.zip").Length);
    }

    [Fact] // spec Sending: text is kind "text"; nothing is written to disk
    public async Task Text_round_trips_without_touching_the_disk()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);

        Assert.Equal(SendOutcome.Sent, await SendText(rig, "héllo ✓ 😀\r\nline two"));

        var (_, offer, item) = Assert.Single(rig.Host.Received);
        Assert.Equal(OfferKind.Text, offer.Kind);
        Assert.Equal("héllo ✓ 😀\r\nline two", item.Text);
        Assert.Empty(item.Paths);
        Assert.Empty(rig.EverythingInDownloads());
    }

    [Fact] // spec Sending: the sender reports progress, ending at the full byte count
    public async Task Sender_reports_waiting_then_sending_up_to_the_full_size()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        var src = Path.Combine(rig.Root, "big.bin");
        File.WriteAllBytes(src, new byte[500_000]);
        var progress = new SyncProgress();

        await rig.Sender.SendAsync(rig.Peer, new SendContent.Paths([src]), progress, CancellationToken.None);

        Assert.Equal(new SendProgress(SendStage.Waiting, 0, 500_000), progress.Reports[0]);
        Assert.Equal(new SendProgress(SendStage.Sending, 500_000, 500_000), progress.Reports[^1]);
        Assert.DoesNotContain(progress.Reports, r => r.BytesSent > 500_000);
    }

    [Fact] // spec Sending: a path that doesn't exist fails the send
    public async Task Missing_file_is_failed()
    {
        await using var rig = await Rig.StartAsync();
        rig.Trust(Sender);
        Assert.Equal(SendOutcome.Failed, await SendPaths(rig, Path.Combine(rig.Root, "nope.txt")));
    }

    // ---- receive folder

    [Fact] // spec Receive folder: unset or null -> the host's default (Downloads)
    public async Task Unset_receive_folder_uses_the_default()
    {
        await using var rig = await Rig.StartAsync();
        rig.WriteSettings($$"""{"receiveFolder":null,"trusted":[{"id":"{{Sender.Id}}","name":"x"}]}""");
        File.WriteAllText(Path.Combine(rig.Root, "f.txt"), "x");

        await SendPaths(rig, Path.Combine(rig.Root, "f.txt"));

        Assert.True(File.Exists(Path.Combine(rig.Downloads, "f.txt")));
    }

    [Fact] // spec Receive folder: a set, existing folder is used instead of the default
    public async Task Set_receive_folder_is_used()
    {
        await using var rig = await Rig.StartAsync();
        var inbox = Path.Combine(rig.Root, "inbox");
        Directory.CreateDirectory(inbox);
        rig.WriteSettings(System.Text.Json.JsonSerializer.Serialize(new
        {
            receiveFolder = inbox,
            trusted = new[] { new { id = Sender.Id, name = "x" } },
        }));
        File.WriteAllText(Path.Combine(rig.Root, "f.txt"), "x");

        await SendPaths(rig, Path.Combine(rig.Root, "f.txt"));

        Assert.True(File.Exists(Path.Combine(inbox, "f.txt")));
        Assert.Empty(rig.EverythingInDownloads());
    }

    [Fact] // spec Receive folder: a set folder that doesn't exist falls back to the default
    public async Task Missing_receive_folder_falls_back_to_the_default()
    {
        await using var rig = await Rig.StartAsync();
        var missing = Path.Combine(rig.Root, "not-there");
        rig.WriteSettings(System.Text.Json.JsonSerializer.Serialize(new
        {
            receiveFolder = missing,
            trusted = new[] { new { id = Sender.Id, name = "x" } },
        }));
        File.WriteAllText(Path.Combine(rig.Root, "f.txt"), "x");

        await SendPaths(rig, Path.Combine(rig.Root, "f.txt"));

        Assert.True(File.Exists(Path.Combine(rig.Downloads, "f.txt")));
        Assert.False(Directory.Exists(missing));
    }

    // ---- startup

    [Fact] // spec Receiving: port already in use is reported distinctly so the host can say so
    public async Task Starting_on_a_taken_port_throws_PortInUse()
    {
        await using var rig = await Rig.StartAsync();
        await using var second = new Receiver(new ReceiverOptions
        {
            Settings = rig.Settings,
            Tailscale = rig.Tailscale,
            Host = rig.Host,
            DefaultReceiveFolder = rig.Downloads,
        });

        await Assert.ThrowsAsync<PortInUseException>(() => second.StartAsync(IPAddress.Loopback, rig.Receiver.Port));
    }
}

/// <summary>A minimal fixed-answer HTTP server, for a peer that isn't a protocol-1 tailshare.</summary>
internal sealed class HttpListenerHost : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public HttpListenerHost(string body) : this(_ => body) { }

    /// <param name="bodyFor">The answer for a request path, or null to read the request and never answer it.</param>
    public HttpListenerHost(Func<string, string?> bodyFor, IPAddress? ip = null, int port = 0)
    {
        _listener = new(ip ?? IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // Not disposed on return: a request that is never answered must stay open.
                    var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => AnswerAsync(c, bodyFor), _cts.Token);
                }
                catch (Exception) { }
            }
        });
    }

    private async Task AnswerAsync(TcpClient c, Func<string, string?> bodyFor)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var buf = new byte[1024];
                var n = await s.ReadAsync(buf, _cts.Token);
                // "GET /v1/devices HTTP/1.1": the second word.
                var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                var body = bodyFor(path);
                if (body is null)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }

                var payload = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
                await s.WriteAsync(payload, _cts.Token);
            }
            catch (Exception) { }
        }
    }

    public void Dispose() { _cts.Cancel(); _listener.Stop(); }
}
