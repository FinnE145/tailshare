using Tailshare.Core;

namespace Tailshare.Tests;

public class LinkDetectorTests
{
    [Theory] // spec Receiving/Notifications: a single absolute http/https URL is a link
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("  https://example.com  ")]
    [InlineData("HTTP://EXAMPLE.COM")]
    public void Lone_http_url_is_a_link(string text) => Assert.True(LinkDetector.TryGetLink(text, out _));

    [Theory] // spec Receiving/Notifications: text containing a URL, and other schemes, are text
    [InlineData("see https://example.com")]
    [InlineData("https://example.com and more")]
    // Uri.TryCreate accepts this one (it escapes the space), so only the whitespace check rejects it.
    [InlineData("https://example.com/a b")]
    [InlineData("file:///C:/Windows/system32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("ms-settings:network")]
    [InlineData("https://")]
    [InlineData("https:example.com")]
    [InlineData("https:/example.com")]
    [InlineData("http:\\\\example.com")]
    [InlineData("https://example.com\nhttps://evil.example")]
    [InlineData("https://example.com\thttps://evil.example")]
    [InlineData("")]
    public void Anything_else_is_text(string text) => Assert.False(LinkDetector.TryGetLink(text, out _));
}

public class NoticesTests
{
    [Theory] // spec Receiving/Sizes: binary units, one decimal; bytes under 1 KB as "512 B"
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048575L, "1.0 MB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(2516582L, "2.4 MB")]
    [InlineData(1073741823L, "1.0 GB")]
    [InlineData(1073741824L, "1.0 GB")]
    [InlineData(1288490189L, "1.2 GB")]
    public void Sizes_are_formatted_in_binary_units(long bytes, string expected) =>
        Assert.Equal(expected, Notices.FormatSize(bytes));

    [Fact] // spec Receiving/Notifications: "photo.jpg, 2.4 MB"
    public void Single_file_description() =>
        Assert.Equal("photo.jpg, 2.4 MB", Notices.Describe(new Offer(OfferKind.File, "photo.jpg", 1, false, 2516582, 2516582)));

    [Fact] // spec Receiving/Notifications: "photo.jpg and 2 more, 12.4 MB"; size is the total
    public void Several_items_description_counts_the_rest_and_uses_total_size() =>
        Assert.Equal("photo.jpg and 2 more, 12.4 MB",
            Notices.Describe(new Offer(OfferKind.Bundle, "photo.jpg", 3, false, 100, 13002342)));

    [Fact] // spec Receiving/Notifications: two items is "and 1 more"
    public void Two_items_description() =>
        Assert.Equal("a.txt and 1 more, 512 B", Notices.Describe(new Offer(OfferKind.Bundle, "a.txt", 2, false, 9, 512)));

    [Fact] // spec Receiving/Notifications: "Photos (folder), 1.2 GB"
    public void Folder_description() =>
        Assert.Equal("Photos (folder), 1.2 GB", Notices.Describe(new Offer(OfferKind.Bundle, "Photos", 1, true, 5, 1288490189)));

    [Fact] // spec Receiving/Notifications: "text"
    public void Text_description() =>
        Assert.Equal("text", Notices.Describe(new Offer(OfferKind.Text, null, 1, false, 5, 5)));

    [Fact] // spec Receiving/Notifications: a peer's name is shown in its sanitized form
    public void Description_shows_the_sanitized_name() =>
        Assert.Equal("evil.txt, 3 B", Notices.Describe(new Offer(OfferKind.File, "..\\..\\evil.txt", 1, false, 3, 3)));

    [Fact] // spec Receiving/Notifications: first contact title and body
    public void First_contact_notice()
    {
        var n = Notices.FirstContact("sender-box", new Offer(OfferKind.File, "photo.jpg", 1, false, 2516582, 2516582));
        Assert.Equal(new Notice("sender-box", "wants to send photo.jpg, 2.4 MB"), n);
    }

    [Fact] // spec Receiving/Notifications: received file title and body
    public void Received_notice()
    {
        var n = Notices.Received("sender-box", new Offer(OfferKind.Bundle, "Photos", 1, true, 5, 1288490189));
        Assert.Equal(new Notice("From sender-box", "Photos (folder), 1.2 GB"), n);
    }

    [Fact] // spec Receiving/Notifications: progress title
    public void Progress_notice_title() =>
        Assert.Equal("Receiving big.iso, 1.0 GB from sender-box",
            Notices.Progress("sender-box", new Offer(OfferKind.File, "big.iso", 1, false, 1073741824, 1073741824)).Title);

    [Fact] // spec Receiving/Notifications: text up to 200 characters is shown whole
    public void Text_of_200_characters_is_not_truncated()
    {
        var text = new string('x', 200);
        Assert.Equal(text, Notices.ReceivedText("d", text).Body);
    }

    [Fact] // spec Receiving/Notifications: truncated to 200 characters with an ellipsis
    public void Text_of_201_characters_is_truncated_with_an_ellipsis() =>
        Assert.Equal(new string('x', 200) + "…", Notices.ReceivedText("d", new string('x', 201)).Body);

    [Fact] // spec Receiving/Notifications: truncation never leaves half a surrogate pair
    public void Truncation_does_not_split_a_surrogate_pair()
    {
        // 199 chars, then a two-char emoji straddling the cut at 200.
        var text = new string('x', 199) + "😀" + "tail";
        Assert.Equal(new string('x', 199) + "…", Notices.ReceivedText("d", text).Body);
    }

    [Fact] // spec Receiving/Notifications: a link's body is the whole URL, not truncated
    public void Link_body_is_the_full_url()
    {
        var url = "https://example.com/" + new string('p', 300);
        Assert.Equal(url, Notices.ReceivedText("d", "  " + url + "\n").Body);
    }

    [Fact] // spec Receiving/Notifications: title is "From <device>"
    public void Text_notice_title() =>
        Assert.Equal("From sender-box", Notices.ReceivedText("sender-box", "hi").Title);

    [Fact] // spec Offer additions: text may omit totalSize; a missing size reads as 0 and the notice still says "text"
    public void A_text_offer_without_sizes_is_described_as_text() =>
        Assert.Equal("text", Notices.Describe(new Offer(OfferKind.Text, null, 1, false, null, null)));

    [Fact] // spec Offer additions: a file offer without bytes (a phone's) is described from totalSize
    public void A_file_offer_without_bytes_is_described_from_total_size() =>
        Assert.Equal("a.jpg, 2.0 KB", Notices.Describe(new Offer(OfferKind.File, "a.jpg", 1, false, null, 2048, true)));

    [Fact] // spec Offer additions: progress is measured against bytes, or totalSize when there are no bytes
    public void Expected_bytes_is_bytes_else_total_size()
    {
        Assert.Equal(7, new Offer(OfferKind.File, "a", 1, false, 7, 9).ExpectedBytes);
        Assert.Equal(9, new Offer(OfferKind.File, "a", 1, false, null, 9).ExpectedBytes);
        Assert.Equal(0, new Offer(OfferKind.Text, null, 1, false, null, null).ExpectedBytes);
    }

    [Fact] // spec Sending to a phone: the waiting status text
    public void Hold_waiting_text() =>
        Assert.Equal("Waiting for pixel-box to pick up", Notices.HoldWaiting("pixel-box"));

    [Theory] // spec Sending to a phone: status line after a hold, "Not picked up" without the device
    [InlineData(HoldOutcome.PickedUp, "Picked up by pixel-box")]
    [InlineData(HoldOutcome.NotPickedUp, "Not picked up")]
    [InlineData(HoldOutcome.Failed, "Failed")]
    [InlineData(HoldOutcome.Cancelled, "Cancelled")]
    public void Hold_status_text(HoldOutcome outcome, string expected) =>
        Assert.Equal(expected, Notices.HoldStatus(outcome, "pixel-box"));

    [Theory] // spec Sending to a phone: every hold ends in a notification except a cancel (which raises none)
    [InlineData(HoldOutcome.PickedUp, "Picked up by pixel-box")]
    [InlineData(HoldOutcome.NotPickedUp, "Not picked up by pixel-box")]
    [InlineData(HoldOutcome.Failed, "Couldn't send to pixel-box")]
    [InlineData(HoldOutcome.Cancelled, null)]
    public void Hold_notice_text(HoldOutcome outcome, string? expected) =>
        Assert.Equal(expected, Notices.HoldNotice(outcome, "pixel-box"));
}
