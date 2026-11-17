using Tailshare.Core;

namespace Tailshare.Tests;

public class NamesTests
{
    private static Func<string, bool> Taken(params string[] names) => n => names.Contains(n);

    [Fact] // spec Receiving/Names: first free suffix, "a (1).ext"
    public void Taken_file_name_gets_suffix_before_the_extension() =>
        Assert.Equal("a (1).txt", Names.Unique("a.txt", Taken("a.txt"), isFolder: false));

    [Fact] // spec Receiving/Names: "(2)" when "(1)" is taken too
    public void Suffix_counts_up_past_taken_ones() =>
        Assert.Equal("a (2).txt", Names.Unique("a.txt", Taken("a.txt", "a (1).txt"), isFolder: false));

    [Fact] // spec Receiving/Names: a free name is kept as is
    public void Free_name_is_unchanged() =>
        Assert.Equal("a.txt", Names.Unique("a.txt", Taken("b.txt"), isFolder: false));

    [Fact] // spec Tests/Names: names without an extension follow the same pattern
    public void Name_without_extension_gets_suffix_at_the_end() =>
        Assert.Equal("notes (1)", Names.Unique("notes", Taken("notes"), isFolder: false));

    [Fact] // spec Receiving/Names: a folder is "Photos (1)"; a dot in a folder name is not an extension
    public void Folder_suffix_goes_at_the_end_even_with_a_dot() =>
        Assert.Equal("v1.2 (1)", Names.Unique("v1.2", Taken("v1.2"), isFolder: true));

    [Fact] // spec Receiving/Names: a dotfile's whole name is its stem
    public void Dotfile_is_not_split_into_empty_stem_and_extension() =>
        Assert.Equal(".gitignore (1)", Names.Unique(".gitignore", Taken(".gitignore"), isFolder: false));

    [Theory] // spec Receiving/Names: path components stripped
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("/etc/passwd", "passwd")]
    public void Path_components_are_stripped(string raw, string expected) =>
        Assert.Equal(expected, Names.Sanitize(raw));

    [Theory] // spec Receiving/Names: invalid characters replaced with "_"
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("q\"<>|.txt", "q____.txt")]
    [InlineData("a\u0001b", "a_b")]
    public void Invalid_characters_become_underscores(string raw, string expected) =>
        Assert.Equal(expected, Names.Sanitize(raw));

    [Theory] // spec Receiving/Names: Windows reserved names are prefixed
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("Lpt9.log", "_Lpt9.log")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("COM10", "COM10")]
    public void Reserved_names_are_prefixed(string raw, string expected) =>
        Assert.Equal(expected, Names.Sanitize(raw));

    [Fact] // spec Receiving/Names: length capped at 200
    public void Long_name_is_capped_at_200_and_keeps_its_extension()
    {
        var result = Names.Sanitize(new string('a', 300) + ".txt");
        Assert.Equal(200, result.Length);
        Assert.Equal(new string('a', 196) + ".txt", result);
    }

    [Fact] // spec Receiving/Names: length capped at 200
    public void Long_name_without_extension_is_capped_at_200() =>
        Assert.Equal(new string('b', 200), Names.Sanitize(new string('b', 300)));

    [Fact] // spec Receiving/Names: 201 is one over the cap
    public void Name_of_201_is_capped_to_200() =>
        Assert.Equal(new string('d', 200), Names.Sanitize(new string('d', 201)));

    [Fact] // spec Receiving/Names: exactly 200 is left alone
    public void Name_of_200_is_unchanged() =>
        Assert.Equal(new string('c', 200), Names.Sanitize(new string('c', 200)));

    [Theory] // spec Receiving/Names: an empty result becomes "file"
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("dir/")]
    [InlineData(null)]
    public void Empty_result_becomes_file(string? raw) => Assert.Equal("file", Names.Sanitize(raw));
}
