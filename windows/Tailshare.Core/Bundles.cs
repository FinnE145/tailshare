using System.IO.Compression;

namespace Tailshare.Core;

public sealed record BundleInfo(string Name, int Items, bool IsFolder, long TotalSize);

/// <summary>Zip creation for the sender and extraction for the receiver.</summary>
public static class Bundles
{
    /// <summary>
    /// Writes <paramref name="paths"/> into a zip at <paramref name="zipPath"/>, each top-level item
    /// at the root. Entries use "/" so the Mac and Windows apps read each other's zips.
    /// </summary>
    public static BundleInfo Create(IReadOnlyList<string> paths, string zipPath, CancellationToken ct = default)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        string? firstName = null;
        var firstIsFolder = false;

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var isFolder = Directory.Exists(path);
            var original = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            // Two picked items can share a name (a.txt from two folders); one root entry per name.
            var top = Names.Unique(original, taken.Contains, isFolder);
            taken.Add(top);

            if (firstName is null) { firstName = top; firstIsFolder = isFolder; }

            if (isFolder)
            {
                zip.CreateEntry(top + "/");
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(path, entry).Replace(Path.DirectorySeparatorChar, '/');
                    if (Directory.Exists(entry)) zip.CreateEntry($"{top}/{rel}/");
                    else total += AddFile(zip, entry, $"{top}/{rel}");
                }
            }
            else
            {
                total += AddFile(zip, path, top);
            }
        }

        return new BundleInfo(firstName ?? "", paths.Count, paths.Count == 1 && firstIsFolder, total);
    }

    private static long AddFile(ZipArchive zip, string file, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = entry.Open();
        src.CopyTo(dst);
        return src.Length;
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="receiveFolder"/>, each top-level item
    /// under its own collision-free name, and returns those paths. Any bad entry, or more output than
    /// <paramref name="totalSize"/>, throws <see cref="InvalidDataException"/> and leaves nothing behind.
    /// </summary>
    public static IReadOnlyList<string> Extract(string zipPath, string receiveFolder, long totalSize, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // Validate every entry before writing any, so an escape attempt never touches the disk.
        var layout = new List<(ZipArchiveEntry Entry, string[] Segments, bool IsDir)>();
        foreach (var e in zip.Entries)
        {
            var isDir = e.FullName.EndsWith('/');
            var segments = SplitEntryName(e.FullName, isDir);
            layout.Add((e, segments, isDir));
        }
        if (layout.Count == 0) throw new InvalidDataException("empty bundle");

        // A top-level item is a folder if anything sits beneath it or it is itself a directory entry.
        var topIsFolder = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (_, segments, isDir) in layout)
        {
            var folder = isDir || segments.Length > 1;
            topIsFolder[segments[0]] = topIsFolder.GetValueOrDefault(segments[0]) || folder;
        }

        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<string>();
        try
        {
            foreach (var (name, isFolder) in topIsFolder)
            {
                var path = Placement.Reserve(receiveFolder, name, isFolder);
                created.Add(path);
                placed[name] = path;
            }

            long written = 0;
            var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, segments, isDir) in layout)
            {
                ct.ThrowIfCancellationRequested();
                var top = placed[segments[0]];
                var inner = segments.Skip(1).Select(Names.Sanitize).ToArray();
                var target = Path.GetFullPath(Path.Combine([top, .. inner]));

                // Belt and braces after the name checks: nothing may resolve outside its own item.
                if (inner.Length > 0 && !target.StartsWith(Path.GetFullPath(top) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("entry escapes its folder");

                if (isDir)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (inner.Length > 0) Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Two entries that end up at one path (a repeated name, or "a?" and "a_" after
                // sanitizing) would silently overwrite each other.
                if (!writtenFiles.Add(target)) throw new InvalidDataException("two entries share a path");

                // A loose top-level file was reserved as an empty file, so Create here truncates it.
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write);
                using var src = entry.Open();
                var buffer = new byte[81920];
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    // The declared sizes inside a zip can lie; count what is actually written.
                    if (written > totalSize) throw new InvalidDataException("bundle is larger than offered");
                    dst.Write(buffer, 0, n);
                }
            }
            return created;
        }
        catch
        {
            foreach (var p in created) Placement.Delete(p);
            throw;
        }
    }

    private static string[] SplitEntryName(string fullName, bool isDir)
    {
        if (fullName.Length == 0 || fullName.Contains('\\'))
            throw new InvalidDataException("bad entry name");

        var segments = (isDir ? fullName[..^1] : fullName).Split('/');
        foreach (var s in segments)
        {
            // An empty segment also rejects a leading "/" (an absolute path).
            // ':' covers drive letters and NTFS alternate data streams.
            if (s.Length == 0 || s == "." || s == ".." || s.Contains(':') || s.Any(char.IsControl))
                throw new InvalidDataException("bad entry name");
        }
        return segments;
    }
}
