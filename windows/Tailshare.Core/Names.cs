namespace Tailshare.Core;

/// <summary>Turns names that arrive from a peer into names that are safe to create on disk.</summary>
public static class Names
{
    public const int MaxLength = 200;

    // One fixed set on every platform, not Path.GetInvalidFileNameChars(): the Linux test peer must
    // produce the same names as the Windows app, and a file that lands on a Linux box may be
    // copied to Windows later.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name)
    {
        var s = name ?? "";

        var cut = s.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) s = s[(cut + 1)..];

        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || Array.IndexOf(Invalid, chars[i]) >= 0) chars[i] = '_';
        }
        // Windows silently drops trailing dots and spaces, which would make "..." collide with "".
        s = new string(chars).Trim().TrimEnd('.', ' ');

        if (s.Length == 0) s = "file";

        var dot = s.IndexOf('.');
        var stem = dot < 0 ? s : s[..dot];
        // "CON.txt" is reserved on Windows just like "CON".
        if (Reserved.Contains(stem)) s = "_" + s;

        if (s.Length > MaxLength) s = CapKeepingExtension(s);
        return s;
    }

    private static string CapKeepingExtension(string s)
    {
        var ext = Path.GetExtension(s);
        if (ext.Length > 20 || ext.Length == s.Length) ext = "";
        var head = s[..(MaxLength - ext.Length)];
        // Never end on half a surrogate pair.
        if (char.IsHighSurrogate(head[^1])) head = head[..^1];
        head = head.TrimEnd('.', ' ');
        return (head.Length == 0 ? "file" : head) + ext;
    }

    /// <summary>The first of <c>name</c>, <c>name (1).ext</c>, <c>name (2).ext</c>… that <paramref name="exists"/> says is free.</summary>
    public static string Unique(string name, Func<string, bool> exists, bool isFolder)
    {
        if (!exists(name)) return name;

        var ext = isFolder ? "" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        // ".gitignore" is a name, not an extension: " (1).gitignore" would be a worse result.
        if (stem.Length == 0) { stem = name; ext = ""; }

        for (var n = 1; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }
}
