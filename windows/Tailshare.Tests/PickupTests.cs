using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Tailshare.Core;

namespace Tailshare.Tests;

/// <summary>Holding items for a phone: the store, and the receiver's pickup endpoints over loopback.</summary>
public class PickupTests
{
    // The caller of every request is whoever the fake Tailscale says: by default the phone being held for.
    private static readonly Identity Phone = FakeTailscale.Sender;
    private static readonly Identity Other = new("node-other", "other-phone");

    private sealed record Hold(Task<HoldOutcome> Outcome, CancellationTokenSource Cts, SyncProgress Progress);

    /// <summary>Starts a hold and waits until it is listed for the phone (or, when it is for someone else, until it is registered).</summary>
    private static async Task<Hold> StartHold(Rig rig, SendContent content, Identity? target = null)
    {
        target ??= Phone;
        var before = rig.Pickups.ListFor(target.Id).Count;
        var cts = new CancellationTokenSource();
        var progress = new SyncProgress();
        var outcome = rig.Pickups.HoldAsync(target.Id, content, progress, cts.Token);
        Assert.True(await Wait.Until(() => rig.Pickups.ListFor(target.Id).Count == before + 1));
        return new Hold(outcome, cts, progress);
    }

    private static async Task<HoldOutcome> Ended(Hold hold) => await hold.Outcome.WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task<PickupListing[]> List(Rig rig)
    {
        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonSerializer.Deserialize<PickupListing[]>(await r.Content.ReadAsStringAsync(), Protocol.Json)!;
    }

    private static Task<HttpResponseMessage> Download(Rig rig, string token) => rig.Http.GetAsync(rig.Url("/v1/pickup/" + token));

    private static string WriteFile(Rig rig, string name, byte[] content)
    {
        var path = Path.Combine(rig.Root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static SendContent Paths(params string[] paths) => new SendContent.Paths(paths);

    // ---- listing

    [Fact] // spec Pickup: items for the caller, oldest first (not by name or token), nothing held for anyone else
    public async Task The_listing_is_the_callers_items_oldest_first()
    {
        await using var rig = await Rig.StartAsync();
        var first = await StartHold(rig, Paths(WriteFile(rig, "z-first.txt", [1, 2, 3])));
        var second = await StartHold(rig, Paths(WriteFile(rig, "a-second.txt", [1, 2, 3, 4, 5])));
        var someoneElses = await StartHold(rig, new SendContent.Text("not yours"), Other);

        var mine = await List(rig);

        Assert.Equal([("z-first.txt", 3L), ("a-second.txt", 5L)], mine.Select(i => (i.Name!, i.Bytes)));
        Assert.All(mine, i => Assert.Equal(OfferKind.File, i.Kind));
        rig.Tailscale.Who = Other;
        var theirs = await List(rig);
        Assert.Equal(OfferKind.Text, Assert.Single(theirs).Kind);

        foreach (var h in new[] { first, second, someoneElses }) h.Cts.Cancel();
    }

    [Fact] // spec Pickup: [] when there are none
    public async Task Nothing_held_is_an_empty_array()
    {
        await using var rig = await Rig.StartAsync();
        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup"));
        Assert.Equal("[]", await r.Content.ReadAsStringAsync());
    }

    [Fact] // spec Pickup: name is absent for text; bytes is the UTF-8 length
    public async Task A_texts_listing_has_no_name_and_counts_utf8_bytes()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, new SendContent.Text("é!"));

        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup"));
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());

        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("text", item.GetProperty("kind").GetString());
        Assert.Equal(3, item.GetProperty("bytes").GetInt64());
        Assert.False(item.TryGetProperty("name", out _));
        hold.Cts.Cancel();
    }

    [Fact] // spec Pickup: a bundle is listed under its own name (the download adds .zip), with the zip's size
    public async Task A_bundle_is_listed_by_name_with_the_zips_size()
    {
        await using var rig = await Rig.StartAsync();
        var folder = Path.Combine(rig.Root, "Photos");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), new string('a', 1000));
        var hold = await StartHold(rig, Paths(folder));

        var item = Assert.Single(await List(rig));

        Assert.Equal(OfferKind.Bundle, item.Kind);
        Assert.Equal("Photos", item.Name);
        var zip = Assert.Single(Directory.GetFiles(rig.HoldTemp));
        Assert.Equal(new FileInfo(zip).Length, item.Bytes);
        hold.Cts.Cancel();
    }

    [Fact] // spec Pickup token: 128 random bits as base64url, a new one for every item
    public async Task Tokens_are_22_chars_of_base64url_and_differ()
    {
        await using var rig = await Rig.StartAsync();
        var a = await StartHold(rig, new SendContent.Text("one"));
        var b = await StartHold(rig, new SendContent.Text("two"));

        var tokens = (await List(rig)).Select(i => i.Token).ToArray();

        Assert.Equal(2, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{22}$", t));
        a.Cts.Cancel();
        b.Cts.Cancel();
    }

    // ---- what is served

    [Fact] // spec Pickup: text is served as UTF-8 text/plain, exactly; the hold ends picked up and the item is gone
    public async Task Text_is_served_exactly_and_the_hold_ends_picked_up()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, new SendContent.Text("héllo ✓ 😀\r\nline two"));
        var token = (await List(rig)).Single().Token;

        using var r = await Download(rig, token);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("héllo ✓ 😀\r\nline two", await r.Content.ReadAsStringAsync());
        Assert.Equal("text/plain; charset=utf-8", r.Content.Headers.ContentType!.ToString());
        Assert.Null(r.Content.Headers.ContentDisposition);
        Assert.Equal(HoldOutcome.PickedUp, await Ended(hold));
        Assert.Empty(await List(rig));
    }

    [Fact] // spec Pickup: a file is served byte for byte as an attachment under its name; progress ends at the full size
    public async Task A_file_is_served_byte_for_byte_with_its_name()
    {
        await using var rig = await Rig.StartAsync();
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 11)).ToArray();
        var hold = await StartHold(rig, Paths(WriteFile(rig, "photo.bin", content)));
        var token = (await List(rig)).Single().Token;

        using var r = await Download(rig, token);

        Assert.Equal(content, await r.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=\"photo.bin\"", string.Join("", r.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal(200_000, r.Content.Headers.ContentLength);
        Assert.Equal(HoldOutcome.PickedUp, await Ended(hold));
        Assert.Equal(new SendProgress(SendStage.Sending, 200_000, 200_000), hold.Progress.Reports[^1]);
        Assert.DoesNotContain(hold.Progress.Reports, p => p.Stage == SendStage.Sending && p.BytesSent > 200_000);
    }

    [Fact] // spec Pickup: a name that isn't ASCII gets an ASCII fallback and the real name in filename*
    public async Task A_non_ascii_name_is_carried_in_filename_star()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, Paths(WriteFile(rig, "é.txt", [1])));
        var token = (await List(rig)).Single().Token;

        using var r = await Download(rig, token);

        Assert.Equal("""attachment; filename="_.txt"; filename*=UTF-8''%C3%A9.txt""", string.Join("", r.Content.Headers.GetValues("Content-Disposition")));
        await Ended(hold);
    }

    [Fact] // spec Pickup: a bundle is zipped when the send starts and served as <name>.zip; its temp zip is gone afterwards
    public async Task A_bundle_is_served_as_a_zip_and_its_temp_file_is_deleted()
    {
        await using var rig = await Rig.StartAsync();
        var folder = Path.Combine(rig.Root, "Photos");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "bbbbb");
        var hold = await StartHold(rig, Paths(folder));
        Assert.Single(Directory.GetFiles(rig.HoldTemp));
        var token = (await List(rig)).Single().Token;

        using var r = await Download(rig, token);

        Assert.Equal("attachment; filename=\"Photos.zip\"", string.Join("", r.Content.Headers.GetValues("Content-Disposition")));
        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Photos/", "Photos/a.txt", "Photos/sub/", "Photos/sub/b.txt"], zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(HoldOutcome.PickedUp, await Ended(hold));
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Sending to a phone: Preparing for a bundle, then Waiting; text and files go straight to Waiting
    public async Task Progress_starts_with_preparing_only_for_a_bundle()
    {
        await using var rig = await Rig.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "x");
        var text = await StartHold(rig, new SendContent.Text("t"));
        var file = await StartHold(rig, Paths(WriteFile(rig, "f.bin", [1])));
        var bundle = await StartHold(rig, Paths(Path.Combine(rig.Root, "Dir")));

        Assert.Equal([SendStage.Waiting], text.Progress.Reports.Select(r => r.Stage));
        Assert.Equal([SendStage.Waiting], file.Progress.Reports.Select(r => r.Stage));
        Assert.Equal([SendStage.Preparing, SendStage.Waiting], bundle.Progress.Reports.Select(r => r.Stage));
        foreach (var h in new[] { text, file, bundle }) h.Cts.Cancel();
    }

    // ---- who may take it, and once

    [Fact] // spec Pickup token: bound to the target's StableID; another identity gets 403 and can't burn it, the owner still can
    public async Task A_token_presented_by_another_device_is_403_and_stays_usable_by_its_owner()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, new SendContent.Text("mine"));
        var token = (await List(rig)).Single().Token;

        rig.Tailscale.Who = Other;
        using (var thief = await Download(rig, token)) Assert.Equal(HttpStatusCode.Forbidden, thief.StatusCode);
        Assert.False(hold.Outcome.IsCompleted);

        rig.Tailscale.Who = Phone;
        using var owner = await Download(rig, token);
        Assert.Equal("mine", await owner.Content.ReadAsStringAsync());
        Assert.Equal(HoldOutcome.PickedUp, await Ended(hold));
    }

    [Fact] // spec Pickup token: single use
    public async Task A_token_works_once()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, new SendContent.Text("once"));
        var token = (await List(rig)).Single().Token;

        using var first = await Download(rig, token);
        await first.Content.ReadAsStringAsync();
        using var second = await Download(rig, token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        await Ended(hold);
    }

    [Fact] // spec Pickup: an unknown token is 403
    public async Task An_unknown_token_is_403()
    {
        await using var rig = await Rig.StartAsync();
        using var r = await Download(rig, "nonsense");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact] // spec Pickup: whois failure is 403 on both endpoints
    public async Task Failed_whois_is_403_on_both_pickup_endpoints()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, new SendContent.Text("x"));
        var token = (await List(rig)).Single().Token;
        rig.Tailscale.Who = null;

        using var list = await rig.Http.GetAsync(rig.Url("/v1/pickup"));
        using var get = await Download(rig, token);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.False(hold.Outcome.IsCompleted);
        hold.Cts.Cancel();
    }

    [Fact] // spec Pickup: one item picked up leaves the others waiting
    public async Task Picking_up_one_item_leaves_the_other_held()
    {
        await using var rig = await Rig.StartAsync();
        var a = await StartHold(rig, new SendContent.Text("first"));
        var b = await StartHold(rig, new SendContent.Text("second"));
        var listed = await List(rig);

        using var r = await Download(rig, listed[0].Token);
        Assert.Equal("first", await r.Content.ReadAsStringAsync());
        await Ended(a);

        Assert.Equal([listed[1].Token], (await List(rig)).Select(i => i.Token));
        Assert.False(b.Outcome.IsCompleted);
        b.Cts.Cancel();
    }

    // ---- how a hold ends

    [Fact] // spec Pickup: after 120 seconds (injected short) -> not picked up, 403, the item is gone
    public async Task An_unclaimed_hold_expires()
    {
        await using var rig = await Rig.StartAsync(holdLifetime: TimeSpan.FromMilliseconds(200));
        var hold = await StartHold(rig, new SendContent.Text("late"));
        var token = (await List(rig)).Single().Token;

        Assert.Equal(HoldOutcome.NotPickedUp, await Ended(hold));

        using var r = await Download(rig, token);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(await List(rig));
    }

    [Fact] // spec Pickup: a bundle's temp zip is deleted when the hold expires
    public async Task An_expired_bundle_leaves_no_temp_file()
    {
        await using var rig = await Rig.StartAsync(holdLifetime: TimeSpan.FromMilliseconds(200));
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "x");
        var hold = await StartHold(rig, Paths(Path.Combine(rig.Root, "Dir")));
        Assert.Single(Directory.GetFiles(rig.HoldTemp));

        Assert.Equal(HoldOutcome.NotPickedUp, await Ended(hold));

        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Pickup: cancel removes the item and its token, and a bundle's temp zip
    public async Task Cancelling_removes_the_item_its_token_and_the_temp_zip()
    {
        await using var rig = await Rig.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "x");
        var hold = await StartHold(rig, Paths(Path.Combine(rig.Root, "Dir")));
        var token = (await List(rig)).Single().Token;

        hold.Cts.Cancel();

        Assert.Equal(HoldOutcome.Cancelled, await Ended(hold));
        Assert.Empty(await List(rig));
        using var r = await Download(rig, token);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Pickup: a hold ends when the app quits; CancelAll ends them all and removes their temp files
    public async Task Cancel_all_ends_every_hold()
    {
        await using var rig = await Rig.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "x");
        var text = await StartHold(rig, new SendContent.Text("t"));
        var bundle = await StartHold(rig, Paths(Path.Combine(rig.Root, "Dir")));
        var forOther = await StartHold(rig, new SendContent.Text("o"), Other);

        rig.Pickups.CancelAll();

        Assert.Equal(HoldOutcome.Cancelled, await Ended(text));
        Assert.Equal(HoldOutcome.Cancelled, await Ended(bundle));
        Assert.Equal(HoldOutcome.Cancelled, await Ended(forOther));
        Assert.Empty(await List(rig));
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Pickup: a download that started before the 120 seconds ran out runs to the end
    public async Task A_download_that_has_started_outlives_the_lifetime()
    {
        await using var rig = await Rig.StartAsync(holdLifetime: TimeSpan.FromMilliseconds(500));
        var size = 30_000_000;
        var hold = await StartHold(rig, Paths(WriteFile(rig, "big.bin", new byte[size])));
        var token = (await List(rig)).Single().Token;

        // The body is larger than the socket buffers, so the server is stuck mid-write until this reads on.
        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup/" + token), HttpCompletionOption.ResponseHeadersRead);
        // Over the buffers Kestrel can't work the length out itself: only the explicit header gives it.
        Assert.Equal(size, r.Content.Headers.ContentLength);
        await Task.Delay(1200);
        Assert.False(hold.Outcome.IsCompleted);
        // Taken: it is no longer listed, and a second request for it is refused while the first still runs.
        Assert.Empty(await List(rig));
        using (var again = await Download(rig, token)) Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        long total = 0;
        await using (var body = await r.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0) total += n;
        }

        Assert.Equal(size, total);
        Assert.Equal(HoldOutcome.PickedUp, await Ended(hold));
    }

    [Fact] // spec Pickup: cancelling mid-download stops it
    public async Task Cancelling_during_a_download_cuts_it_off()
    {
        await using var rig = await Rig.StartAsync();
        var size = 30_000_000;
        var hold = await StartHold(rig, Paths(WriteFile(rig, "big.bin", new byte[size])));
        var token = (await List(rig)).Single().Token;

        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup/" + token), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await r.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[1000]);
        hold.Cts.Cancel();

        Assert.Equal(HoldOutcome.Cancelled, await Ended(hold));
        long total = 1000;
        try
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0) total += n;
        }
        catch (IOException) { }
        Assert.True(total < size);
    }

    [Fact] // spec Pickup: a held file is read when it's picked up, and holding it doesn't lock the user's own file
    public async Task A_file_being_downloaded_can_still_be_deleted()
    {
        await using var rig = await Rig.StartAsync();
        var path = WriteFile(rig, "big.bin", new byte[30_000_000]);
        var hold = await StartHold(rig, Paths(path));
        var token = (await List(rig)).Single().Token;

        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup/" + token), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await r.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[1000]);

        File.Delete(path);

        Assert.False(File.Exists(path));
        hold.Cts.Cancel();
        await Ended(hold);
    }

    [Fact] // spec Pickup: a bundle's temp zip is deleted however the hold ends, here by a cancel while the zip is still being read
    public async Task Cancelling_during_a_bundle_download_still_deletes_the_zip()
    {
        await using var rig = await Rig.StartAsync();
        var folder = Path.Combine(rig.Root, "Big");
        Directory.CreateDirectory(folder);
        // Random bytes don't compress, so the zip is big enough to outrun the socket buffers.
        var noise = new byte[30_000_000];
        new Random(1).NextBytes(noise);
        File.WriteAllBytes(Path.Combine(folder, "noise.bin"), noise);
        var hold = await StartHold(rig, Paths(folder));
        var token = (await List(rig)).Single().Token;

        using var r = await rig.Http.GetAsync(rig.Url("/v1/pickup/" + token), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await r.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[1000]);
        hold.Cts.Cancel();

        Assert.Equal(HoldOutcome.Cancelled, await Ended(hold));
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Pickup: held items wait 120 seconds
    public void The_default_lifetime_is_120_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), new PickupStore().Lifetime);
        Assert.Equal(TimeSpan.FromSeconds(120), Protocol.HoldLifetime);
    }

    [Fact] // spec Pickup: a phone that drops mid-download leaves the sender with Failed
    public async Task A_dropped_download_is_failed()
    {
        await using var rig = await Rig.StartAsync();
        var hold = await StartHold(rig, Paths(WriteFile(rig, "big.bin", new byte[30_000_000])));
        var token = (await List(rig)).Single().Token;

        using (var r = await rig.Http.GetAsync(rig.Url("/v1/pickup/" + token), HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await r.Content.ReadAsStreamAsync();
            await body.ReadExactlyAsync(new byte[1000]);
        }

        Assert.Equal(HoldOutcome.Failed, await Ended(hold));
    }

    [Fact] // spec Pickup: a held file that is gone when it's picked up fails the download, and the sender sees Failed
    public async Task A_held_file_deleted_before_pickup_fails()
    {
        await using var rig = await Rig.StartAsync();
        var path = WriteFile(rig, "gone.txt", [1, 2, 3]);
        var hold = await StartHold(rig, Paths(path));
        var token = (await List(rig)).Single().Token;
        File.Delete(path);

        using var r = await Download(rig, token);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HoldOutcome.Failed, await Ended(hold));
        Assert.Empty(await List(rig));
    }

    [Fact] // spec Sending to a phone: a path that doesn't exist fails the hold, listing nothing
    public async Task Holding_a_missing_path_is_failed()
    {
        await using var rig = await Rig.StartAsync();

        var outcome = await rig.Pickups.HoldAsync(Phone.Id, Paths(Path.Combine(rig.Root, "nope.txt")), null, CancellationToken.None);

        Assert.Equal(HoldOutcome.Failed, outcome);
        Assert.Empty(await List(rig));
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }

    [Fact] // spec Sending to a phone: cancelling while a bundle is still being prepared is Cancelled, not Failed
    public async Task Cancelling_before_the_hold_starts_is_cancelled()
    {
        await using var rig = await Rig.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Root, "Dir"));
        File.WriteAllText(Path.Combine(rig.Root, "Dir", "x.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await rig.Pickups.HoldAsync(Phone.Id, Paths(Path.Combine(rig.Root, "Dir")), null, cts.Token);

        Assert.Equal(HoldOutcome.Cancelled, outcome);
        Assert.Empty(Directory.GetFiles(rig.HoldTemp));
    }
}
