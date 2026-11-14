using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class SelectionTests
{
    private static readonly string Dest = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));
    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static ZipArchive Tree() => ZipArchive.Open(new ZipBuilder()
        .AddDirectory("dir").Add("dir/a.txt", B("A")).AddDirectory("dir/sub").Add("dir/sub/b.txt", B("B"))
        .AddDirectory("dir2").Add("dir2/c.txt", B("C")).Add("top.txt", B("T")).Build());

    private static int Index(ZipArchive a, string name) => a.Entries.Single(e => e.Name == name).Index;

    private static string[] Names(ZipArchive a, IEnumerable<int> idx) => [.. idx.Order().Select(i => a.Entries[i].Name)];

    // ---- EntrySelection.Expand ----
    [Fact]
    public void Selecting_a_file_selects_only_that_file()
    {
        var a = Tree();
        Assert.Equal(["top.txt"], Names(a, EntrySelection.Expand(a.Entries, [Index(a, "top.txt")])));
    }

    [Fact]
    public void Selecting_a_folder_selects_everything_inside_it_but_not_look_alike_siblings()
    {
        var a = Tree();
        var picked = Names(a, EntrySelection.Expand(a.Entries, [Index(a, "dir/")]));
        Assert.Equal(["dir/", "dir/a.txt", "dir/sub/", "dir/sub/b.txt"], picked.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("dir2/c.txt", picked);
    }

    [Fact]
    public void Selecting_a_nested_folder_and_a_file_combines_them()
    {
        var a = Tree();
        var picked = Names(a, EntrySelection.Expand(a.Entries, [Index(a, "dir/sub/"), Index(a, "top.txt")]));
        Assert.Equal(["dir/sub/", "dir/sub/b.txt", "top.txt"], picked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Backslash_names_and_duplicate_selections_are_handled()
    {
        var zip = ZipArchive.Open(new ZipBuilder().Add("win\\x.txt", B("x")).Add("win\\deep\\y.txt", B("y")).Add("other.txt", B("o")).AddDirectory("win").Build());
        var a = zip;
        var dir = a.Entries.Single(e => e.Kind == EntryKind.Directory).Index;
        var picked = Names(a, EntrySelection.Expand(a.Entries, [dir, dir, dir]));
        Assert.Equal(["win/", "win\\deep\\y.txt", "win\\x.txt"], picked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Empty_selection_is_empty_and_bad_indexes_are_rejected()
    {
        var a = Tree();
        Assert.Empty(EntrySelection.Expand(a.Entries, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntrySelection.Expand(a.Entries, [a.Entries.Count]));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntrySelection.Expand(a.Entries, [-1]));
    }

    [Fact]
    public void Normalize_uses_forward_slashes_and_no_trailing_separator()
    {
        Assert.Equal("a/b", EntrySelection.Normalize("a\\b"));
        Assert.Equal("a/b", EntrySelection.Normalize("a/b/"));
        Assert.Equal("a/b/c", EntrySelection.Normalize("a//b\\\\c"));
        Assert.Equal("", EntrySelection.Normalize("/"));
    }

    [Fact]
    public void Expansion_scales_to_many_entries()
    {
        var b = new ZipBuilder();
        for (var d = 0; d < 300; d++)
        {
            b.AddDirectory($"d{d}");
            for (var f = 0; f < 100; f++) b.Add($"d{d}/f{f}.txt", []);
        }

        var a = ZipArchive.Open(b.Build());
        var picks = a.Entries.Where(e => e.Kind == EntryKind.Directory && e.Index % 2 == 0).Select(e => e.Index).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var set = EntrySelection.Expand(a.Entries, picks);
        Assert.True(clock.ElapsedMilliseconds < 2000);
        Assert.Equal(picks.Length * 101, set.Count);
    }

    // ---- ExtractOptions.EntryFilter through planner and extractor ----
    [Fact]
    public void Filter_limits_planning_to_the_selected_entries()
    {
        var a = Tree();
        var keep = EntrySelection.Expand(a.Entries, [Index(a, "dir/sub/")]);
        var plan = ExtractionPlanner.Plan(a, Dest, new ExtractOptions { EntryFilter = e => keep.Contains(e.Index) });
        Assert.Equal(["dir/sub", "dir/sub/b.txt"], plan.Items.Select(i => i.RelativePath));
        Assert.Equal(1, plan.TotalBytes);
    }

    [Fact]
    public void Unselected_hostile_entries_are_not_validated_and_do_not_abort()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("good.txt", B("g")).Add("../evil.txt", B("e")).Add("CON", B("c")).Build());
        var good = Index(a, "good.txt");
        var plan = ExtractionPlanner.Plan(a, Dest, new ExtractOptions { EntryFilter = e => e.Index == good });
        Assert.Equal(["good.txt"], plan.Items.Select(i => i.RelativePath));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void A_hostile_entry_that_is_selected_still_aborts()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("good.txt", B("g")).Add("../evil.txt", B("e")).Build());
        var ex = Assert.Throws<ArchiveException>(() => ExtractionPlanner.Plan(a, Dest, new ExtractOptions { EntryFilter = _ => true }));
        Assert.Equal(ArchiveError.UnsafePath, ex.Error);
    }

    [Fact]
    public void Collisions_and_size_limits_only_count_selected_entries()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("Readme", B("1")).Add("README", B("2")).Add("big.bin", new byte[600]).Add("small", B("s")).Build());
        var readme = Index(a, "Readme");
        var small = Index(a, "small");
        var plan = ExtractionPlanner.Plan(a, Dest, new ExtractOptions { EntryFilter = e => e.Index == readme || e.Index == small, MaxTotalSize = 100 });
        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void Selective_extraction_writes_only_the_selection_with_its_folder_structure()
    {
        var a = Tree();
        var fs = new InMemoryFileSystem(Dest);
        var keep = EntrySelection.Expand(a.Entries, [Index(a, "dir/sub/b.txt"), Index(a, "top.txt")]);
        var r = ArchiveExtractor.Extract(a, Dest, new ExtractOptions { EntryFilter = e => keep.Contains(e.Index) }, fs);
        Assert.Equal(2, r.Files);
        Assert.Equal(["B", "T"], new[] { fs.Files[Path.Combine(Dest, "dir", "sub", "b.txt")], fs.Files[Path.Combine(Dest, "top.txt")] }.Select(x => System.Text.Encoding.UTF8.GetString(x)).ToArray());
        Assert.DoesNotContain(fs.Files.Keys, k => k.Contains("dir2", StringComparison.Ordinal) || k.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void No_filter_means_everything()
    {
        var a = Tree();
        Assert.Equal(a.Entries.Count, ExtractionPlanner.Plan(a, Dest, new ExtractOptions()).Items.Count);
    }
}
