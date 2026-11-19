using System.Text.Json;
using Tailshare.Core;

namespace Tailshare.Hub;

public sealed record RegistryDevice(string Id, string Name);

public sealed record LoadedRegistry(IReadOnlyList<RegistryDevice> Devices, bool Malformed);

// Only to give System.Text.Json a shape; the file is {"devices":[{"id":"...","name":"..."}]}.
internal sealed record RegistryFile(List<RegistryDevice?>? Devices);

/// <summary>
/// The registered phones and iPads, and the only writer of <c>registry.json</c>. Like the apps'
/// settings file it can be hand-edited (that is how a device is unregistered), so a file that
/// doesn't parse is treated as empty and never overwritten.
/// </summary>
public sealed class RegistryStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();

    public string FilePath { get; } = filePath;

    public LoadedRegistry Load()
    {
        lock (_gate) return LoadUnlocked();
    }

    private LoadedRegistry LoadUnlocked()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (FileNotFoundException) { return new([], false); }
        catch (DirectoryNotFoundException) { return new([], false); }
        catch (IOException) { return new([], true); }
        catch (UnauthorizedAccessException) { return new([], true); }

        try
        {
            var file = JsonSerializer.Deserialize<RegistryFile>(text, Options);
            if (file is null) return new([], true);
            var devices = (file.Devices ?? []).Where(d => d is not null && !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name)).Select(d => d!).ToList();
            return new(devices, false);
        }
        catch (JsonException) { return new([], true); }
    }

    /// <summary>
    /// Records <paramref name="who"/> under its stable ID, updating the stored name if it changed.
    /// False when nothing was saved because the file is malformed.
    /// </summary>
    public bool Register(Identity who)
    {
        lock (_gate)
        {
            var loaded = LoadUnlocked();
            if (loaded.Malformed) return false;

            var devices = loaded.Devices.ToList();
            var at = devices.FindIndex(d => d.Id == who.Id);
            var entry = new RegistryDevice(who.Id, who.Name);
            if (at >= 0)
            {
                if (devices[at] == entry) return true;
                devices[at] = entry;
            }
            else
            {
                devices.Add(entry);
            }
            Write(devices);
            return true;
        }
    }

    // Temp file then replace, so a crash can't leave half a file where the registry was.
    private void Write(List<RegistryDevice> devices)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".registry-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { devices }, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
