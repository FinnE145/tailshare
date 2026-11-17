namespace Tailshare.Core;

public static class LinkDetector
{
    /// <summary>
    /// True only for a lone absolute http or https URL. This is what decides whether a click opens
    /// a browser, so every other scheme (file:, javascript:, ms-settings:…) must stay plain text.
    /// </summary>
    public static bool TryGetLink(string text, out Uri uri)
    {
        uri = null!;
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return false;

        // Uri.TryCreate also accepts "https:example.com"; require the real form.
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

        // The prefix check above already limits the scheme to http and https.
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (string.IsNullOrEmpty(u.Host)) return false;

        uri = u;
        return true;
    }
}
