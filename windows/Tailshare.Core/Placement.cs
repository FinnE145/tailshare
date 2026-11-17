namespace Tailshare.Core;

/// <summary>
/// Picks collision-free names in the receive folder and claims them. One lock covers all of it so
/// two simultaneous transfers of "a.txt" can't pick the same free name.
/// </summary>
internal static class Placement
{
    private static readonly object Gate = new();

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Claims a free name for a top-level item (empty file or folder) and returns its full path.</summary>
    public static string Reserve(string folder, string rawName, bool isFolder)
    {
        lock (Gate)
        {
            var name = Names.Unique(Names.Sanitize(rawName), n => Exists(Path.Combine(folder, n)), isFolder);
            var path = Path.Combine(folder, name);
            if (isFolder) Directory.CreateDirectory(path);
            else File.Create(path).Dispose();
            return path;
        }
    }

    /// <summary>Moves a finished temp file to its final, collision-free name.</summary>
    public static string PlaceFile(string folder, string rawName, string partPath)
    {
        lock (Gate)
        {
            var name = Names.Unique(Names.Sanitize(rawName), n => Exists(Path.Combine(folder, n)), isFolder: false);
            var path = Path.Combine(folder, name);
            File.Move(partPath, path, overwrite: false);
            // The temp file is hidden and a rename keeps that: every received file was invisible
            // in Explorer until Verify caught it.
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
            return path;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
