using System.IO.Compression;
using System.Text;
using Tailshare.Core;

namespace Tailshare.Tests;

public class BundleTests
{
    private static string MakeZip(TempDir dir, params (string Name, string Content)[] entries)
    {
        var path = dir.Combine("in-" + Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        return path;
    }

    private static (TempDir Dir, string Folder) Setup()
    {
        var dir = new TempDir();
        var folder = dir.Combine("inbox");
        Directory.CreateDirectory(folder);
        return (dir, folder);
    }

    [Theory] // spec Receiving/Finishing: absolute, drive, ".." and backslash entries fail the whole transfer, nothing left
    [InlineData("../evil.txt")]
    [InlineData("Photos/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("C:x.txt")]
    [InlineData("Photos/a.txt:stream")]
    [InlineData("..\\evil.txt")]
    [InlineData("Photos\\..\\evil.txt")]
    [InlineData("Photos//a.txt")]
    [InlineData("./a.txt")]
    [InlineData("Photos/a\u0001b.txt")]
    [InlineData("Photos/a\u007Fb.txt")]
    public void Bad_entry_fails_the_transfer_and_leaves_nothing(string badName)
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        // The good entries come first: rolling back must remove what they would have written.
        var zip = MakeZip(dir, ("ok.txt", "1"), ("Photos/ok.txt", "2"), (badName, "x"));

        Assert.Throws<InvalidDataException>(() => Bundles.Extract(zip, folder, 1000));

        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Assert.False(File.Exists(dir.Combine("evil.txt")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.txt")));
    }

    [Fact] // spec Receiving/Finishing: extraction fails once it has written more than totalSize, leaving nothing
    public void Extraction_past_total_size_fails_and_leaves_nothing()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir, ("a.txt", new string('a', 100)), ("Dir/b.txt", new string('b', 100)));

        Assert.Throws<InvalidDataException>(() => Bundles.Extract(zip, folder, 199));

        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact] // spec Receiving/Finishing: writing exactly totalSize is within the cap
    public void Extraction_of_exactly_total_size_succeeds()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir, ("a.txt", new string('a', 100)), ("Dir/b.txt", new string('b', 100)));

        var placed = Bundles.Extract(zip, folder, 200);

        Assert.Equal(2, placed.Count);
        Assert.Equal(new string('b', 100), File.ReadAllText(Path.Combine(folder, "Dir", "b.txt")));
    }

    [Fact] // spec Receiving/Finishing: folders and loose files land under collision-free top-level names
    public void Top_level_items_get_collision_free_names()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        Directory.CreateDirectory(Path.Combine(folder, "Photos"));
        File.WriteAllText(Path.Combine(folder, "Photos", "old.txt"), "old");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "old-a");
        var zip = MakeZip(dir, ("Photos/", ""), ("Photos/x.txt", "new-x"), ("Photos/sub/y.txt", "new-y"), ("a.txt", "new-a"));

        var placed = Bundles.Extract(zip, folder, 1000);

        Assert.Equal([Path.Combine(folder, "Photos (1)"), Path.Combine(folder, "a (1).txt")], placed);
        Assert.Equal("new-x", File.ReadAllText(Path.Combine(folder, "Photos (1)", "x.txt")));
        Assert.Equal("new-y", File.ReadAllText(Path.Combine(folder, "Photos (1)", "sub", "y.txt")));
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(folder, "a (1).txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(folder, "Photos", "old.txt")));
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(folder, "a.txt")));
    }

    [Fact] // spec Receiving/Names: names inside a bundle are sanitized too
    public void Names_inside_a_bundle_are_sanitized()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir, ("Dir/a?b.txt", "x"), ("Dir/CON", "y"));

        Bundles.Extract(zip, folder, 1000);

        Assert.True(File.Exists(Path.Combine(folder, "Dir", "a_b.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "Dir", "_CON")));
    }

    [Theory] // spec Receiving/Finishing: two entries that land on one path fail the transfer instead of overwriting
    [InlineData("Dir/a.txt", "Dir/a.txt")]
    [InlineData("Dir/a?b.txt", "Dir/a_b.txt")]
    [InlineData("a.txt", "a.txt")]
    public void Entries_sharing_a_path_fail_and_leave_nothing(string first, string second)
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir, (first, "one"), (second, "two"));

        Assert.Throws<InvalidDataException>(() => Bundles.Extract(zip, folder, 1000));

        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact] // characterization: a zip with no entries fails (the app's received notification needs at least one path)
    public void Empty_bundle_fails()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir);

        Assert.Throws<InvalidDataException>(() => Bundles.Extract(zip, folder, 1000));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact] // spec Bundles: a folder as "Photos/…"; an empty folder is only its directory entry and still lands as a folder
    public void Empty_folder_alone_lands_as_a_folder()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var zip = MakeZip(dir, ("Empty/", ""));

        var placed = Bundles.Extract(zip, folder, 0);

        Assert.Equal([Path.Combine(folder, "Empty")], placed);
        Assert.True(Directory.Exists(Path.Combine(folder, "Empty")));
    }

    [Fact] // spec Receiving/Finishing: a file that is not a zip fails the transfer, nothing left
    public void Garbage_is_not_a_bundle()
    {
        var (dir, folder) = Setup();
        using var _ = dir;
        var path = dir.Combine("junk.zip");
        File.WriteAllText(path, "this is not a zip");

        Assert.Throws<InvalidDataException>(() => Bundles.Extract(path, folder, 1000));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact] // spec Bundles: a zip with "/" separators, each top-level item at the root, folders kept (empty ones too)
    public void Create_then_extract_round_trips_files_and_folders()
    {
        using var dir = new TempDir();
        var src = dir.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "Photos", "sub"));
        Directory.CreateDirectory(Path.Combine(src, "Photos", "empty"));
        File.WriteAllText(Path.Combine(src, "Photos", "sub", "y.txt"), "yy");
        File.WriteAllText(Path.Combine(src, "loose.txt"), "loose");
        var zipPath = dir.Combine("out.zip");

        var info = Bundles.Create([Path.Combine(src, "Photos"), Path.Combine(src, "loose.txt")], zipPath);

        Assert.Equal(new BundleInfo("Photos", 2, false, 7), info);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(["Photos/", "Photos/empty/", "Photos/sub/", "Photos/sub/y.txt", "loose.txt"], names);
        }

        var inbox = dir.Combine("inbox");
        Directory.CreateDirectory(inbox);
        Bundles.Extract(zipPath, inbox, info.TotalSize);
        Assert.Equal("yy", File.ReadAllText(Path.Combine(inbox, "Photos", "sub", "y.txt")));
        Assert.True(Directory.Exists(Path.Combine(inbox, "Photos", "empty")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(inbox, "loose.txt")));
    }

    [Fact] // spec Sending: a bundle of one folder reports isFolder, with the folder's name
    public void Single_folder_bundle_is_marked_as_a_folder()
    {
        using var dir = new TempDir();
        var src = dir.Combine("Docs");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "abc");

        var info = Bundles.Create([src + Path.DirectorySeparatorChar], dir.Combine("o.zip"));

        Assert.Equal(new BundleInfo("Docs", 1, true, 3), info);
    }

    [Fact] // spec Sending: two picked items with the same name both arrive, the second renamed
    public void Same_named_items_from_different_folders_both_survive()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("one"));
        Directory.CreateDirectory(dir.Combine("two"));
        File.WriteAllText(dir.Combine("one", "a.txt"), "first");
        File.WriteAllText(dir.Combine("two", "a.txt"), "second");
        var zipPath = dir.Combine("o.zip");

        var info = Bundles.Create([dir.Combine("one", "a.txt"), dir.Combine("two", "a.txt")], zipPath);
        var inbox = dir.Combine("inbox");
        Directory.CreateDirectory(inbox);
        Bundles.Extract(zipPath, inbox, info.TotalSize);

        Assert.Equal("first", File.ReadAllText(Path.Combine(inbox, "a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(inbox, "a (1).txt")));
    }
}
