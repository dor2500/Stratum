using Stratum.Core;

namespace Stratum.Core.Tests;

public sealed class PathSafetyTests
{
    private const string Root = @"C:\dest";

    [Theory]
    [InlineData("a.txt", @"C:\dest\a.txt")]
    [InlineData("dir/sub/a.txt", @"C:\dest\dir\sub\a.txt")]
    [InlineData(@"dir\a.txt", @"C:\dest\dir\a.txt")]
    [InlineData("/leading/slash.txt", @"C:\dest\leading\slash.txt")]
    [InlineData("./a.txt", @"C:\dest\a.txt")]
    [InlineData("C:/Windows/evil.dll", @"C:\dest\C_\Windows\evil.dll")]
    public void Safe_paths_stay_under_the_destination(string entry, string expected) =>
        Assert.Equal(expected, PathSafety.ResolveTarget(Root, entry));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("dir/../../evil.txt")]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("")]
    [InlineData("/")]
    public void Escaping_paths_are_rejected(string entry) =>
        Assert.Null(PathSafety.ResolveTarget(Root, entry));

    [Fact]
    public void Tree_lists_implicit_folders_once()
    {
        var entries = new List<ArchiveEntry>
        {
            new(0, "a/b/one.txt", false, 10, null, false, 0, null),
            new(1, "a/b/two.txt", false, 20, null, false, 0, null),
            new(2, "a/top.txt", false, 5, null, false, 0, null),
            new(3, "root.txt", false, 1, null, false, 0, null),
        };
        var tree = new ArchiveTree(entries);

        var root = tree.List("");
        Assert.Equal(["a", "root.txt"], root.Select(i => i.Name));
        Assert.Equal(35, root[0].Size);

        var a = tree.List("a");
        Assert.Equal(["b", "top.txt"], a.Select(i => i.Name));
        Assert.Equal([0, 1], tree.IndexesUnder([a[0]]).Order());
        Assert.True(tree.FolderExists("a/b"));
        Assert.False(tree.FolderExists("a/missing"));
    }
}
