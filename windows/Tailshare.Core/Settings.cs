using System.Text.Json;

namespace Tailshare.Core;

public sealed record Settings(
    string? ReceiveFolder = null,
    IReadOnlyList<Identity>? Trusted = null,
    IReadOnlyList<Identity>? Blocked = null,
    int? Port = null,
    string? Hub = null)
{
    /// <summary>The configured port if it is a usable one (1024 to 65535), else the default. Ports below 1024 need admin rights.</summary>
    // Ignored: it was written into the hand-edited file, where editing it would change nothing.
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectivePort => Port is >= 1024 and <= 65535 ? Port.Value : Protocol.DefaultPort;

    /// <summary>
    /// The machine name or Tailscale IPv4 of the hub, or null to find one automatically. Hand-edited:
    /// nothing in tailshare ever writes it, and a rewrite of the file keeps whatever it said.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveHub => string.IsNullOrWhiteSpace(Hub) ? null : Hub.Trim();

    public bool IsTrusted(string id) => Trusted?.Any(t => t?.Id == id) == true;
    public bool IsBlocked(string id) => Blocked?.Any(t => t?.Id == id) == true;
}

public sealed record LoadedSettings(Settings Value, bool Malformed);

/// <summary>
/// The only writer of the settings file. It is hand-edited, so it is read fresh every time and a
/// file that doesn't parse is never overwritten.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();

    public string FilePath { get; } = filePath;

    public LoadedSettings Load()
    {
        lock (_gate) return LoadUnlocked();
    }

    private LoadedSettings LoadUnlocked()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (FileNotFoundException) { return new(new Settings(), false); }
        catch (DirectoryNotFoundException) { return new(new Settings(), false); }
        catch (IOException) { return new(new Settings(), true); }
        catch (UnauthorizedAccessException) { return new(new Settings(), true); }

        try
        {
            var s = JsonSerializer.Deserialize<Settings>(text, Options);
            return s is null ? new(new Settings(), true) : new(s, false);
        }
        catch (JsonException) { return new(new Settings(), true); }
    }

    /// <summary>Adds <paramref name="who"/> to <c>trusted</c>. False when nothing was saved because the file is malformed.</summary>
    public bool AddTrusted(Identity who)
    {
        lock (_gate)
        {
            var loaded = LoadUnlocked();
            if (loaded.Malformed) return false;
            if (loaded.Value.IsTrusted(who.Id)) return true;

            var trusted = (loaded.Value.Trusted ?? []).Append(who).ToList();
            Write(loaded.Value with { Trusted = trusted });
            return true;
        }
    }

    // Temp file then replace, so a crash can't leave half a file where the user's edits were.
    private void Write(Settings s)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(s with
            {
                Trusted = s.Trusted ?? [],
                Blocked = s.Blocked ?? [],
            }, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
