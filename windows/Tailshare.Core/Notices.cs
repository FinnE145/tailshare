using System.Globalization;

namespace Tailshare.Core;

public sealed record Notice(string Title, string Body);

/// <summary>Titles and bodies of every notification, built here so they can be tested without a shell.</summary>
public static class Notices
{
    public const long ProgressThresholdBytes = 20L * 1024 * 1024;
    public const int TextPreviewLength = 200;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";

        var value = (double)bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        // Compare the rounded value: 1048575 bytes is "1024.0 KB" if only the raw value is checked.
        while (unit < units.Length - 1 && Math.Round(value, 1) >= 1024)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>What an offer is, for a notification: <c>photo.jpg, 2.4 MB</c>, <c>Photos (folder), 1.2 GB</c>, <c>text</c>.</summary>
    public static string Describe(Offer o)
    {
        if (o.Kind == OfferKind.Text) return "text";

        // Names here come from a peer and are shown before anything is saved; show the form that
        // would be written, which has no control characters or path parts.
        var name = Names.Sanitize(o.Name);
        var size = FormatSize(o.TotalSize);
        if (o.Items > 1) return $"{name} and {o.Items - 1} more, {size}";
        if (o.Kind == OfferKind.Bundle && o.IsFolder) return $"{name} (folder), {size}";
        return $"{name}, {size}";
    }

    public static Notice FirstContact(string device, Offer o) => new(device, "wants to send " + Describe(o));

    public static Notice Received(string device, Offer o) => new("From " + device, Describe(o));

    public static Notice ReceivedText(string device, string text)
    {
        // A link shows in full: a truncated URL can't be judged before clicking it.
        if (LinkDetector.TryGetLink(text, out var uri)) return new("From " + device, uri.OriginalString);
        return new("From " + device, Truncate(text));
    }

    public static Notice Progress(string device, Offer o) => new($"Receiving {Describe(o)} from {device}", "");

    public static string Truncate(string text)
    {
        if (text.Length <= TextPreviewLength) return text;
        var cut = TextPreviewLength;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
