using Unpakz.Interop;
using Unpakz.Tests.Support;

namespace Unpakz.Interop.Tests;

public class NativeArchiveTests
{
    public static IEnumerable<object[]> Formats() => Archives.All().Select(a => new object[] { a.Format });

    private static string Make(string format, TempDir d) => Archives.All().Single(a => a.Format == format).Make(d);

    [Fact]
    public void The_library_speaks_the_abi_this_build_expects() => Assert.Equal(1u, NativeArchive.AbiVersion());

    [Theory, MemberData(nameof(Formats))]
    public void Lists_every_entry_with_guarded_names_and_sizes(string format)
    {
        using var d = new TempDir();
        var entries = NativeArchive.List(Make(format, d));
        var files = entries.Where(e => e.Kind == EntryKind.File).ToDictionary(e => e.RelativePath, e => e.Size);
        Assert.Equal(Archives.Tree.ToDictionary(kv => kv.Key, kv => (ulong)kv.Value.Length), files);
        Assert.All(entries, e => Assert.Equal(e.Index, entries.ToList().IndexOf(e)));
    }

    [Theory, MemberData(nameof(Formats))]
    public void Extracts_every_format_exactly_and_leaves_no_temp_files(string format)
    {
        using var d = new TempDir();
        var out1 = d.File("out");
        var result = NativeArchive.Extract(Make(format, d), out1);
        Archives.AssertTree(out1, Archives.Tree);
        Assert.Equal((ulong)Archives.Tree.Count, result.Files);
        Assert.Equal((ulong)Archives.Tree.Values.Sum(v => v.Length), result.Bytes);
    }

    [Fact]
    public void Extracts_the_committed_real_rar_fixtures()
    {
        foreach (var name in new[] { "rar5-m3-mix.rar", "rar5-m5-solid.rar", "rar5-delta.rar", "rar5-x86.rar" })
        {
            using var d = new TempDir();
            var r = NativeArchive.Extract(Env.Fixture(name), d.File("o"));
            Assert.True(r.Files > 0, name);
        }
    }

    [Fact]
    public void Extracts_a_multi_volume_set_found_from_any_part()
    {
        using var d = new TempDir();
        foreach (var f in Directory.GetFiles(Env.Fixture("mv-solid"))) File.Copy(f, d.File(Path.GetFileName(f)));
        var r = NativeArchive.Extract(d.File("a.part02.rar"), d.File("o"));
        Assert.True(r.Files >= 1);
        var ex = Assert.Throws<UnpakzException>(() => { File.Delete(d.File("a.part02.rar")); NativeArchive.List(d.File("a.part01.rar")); });
        Assert.Equal(UnpakzStatus.MissingVolume, ex.Status);
        Assert.Equal(2, ex.Volume);
    }

    [Fact]
    public void Hostile_names_are_refused_before_anything_is_written()
    {
        using var d = new TempDir();
        var zip = d.Write("evil.zip", new ZipBuilder().Add("ok.txt", [1]).Add("../escape.txt", [2]).Build());
        var ex = Assert.Throws<UnpakzException>(() => NativeArchive.Extract(zip, d.File("o")));
        Assert.Equal(UnpakzStatus.UnsafePath, ex.Status);
        Assert.False(File.Exists(d.File("o/ok.txt")));
        Assert.False(File.Exists(d.File("escape.txt")));
    }

    [Fact]
    public void Conflicts_follow_the_overwrite_policy()
    {
        using var d = new TempDir();
        var zip = Archives.Zip(d, tree: new() { ["a.txt"] = [1, 2, 3] });
        Directory.CreateDirectory(d.File("o"));
        File.WriteAllBytes(d.File("o/a.txt"), [9]);
        Assert.Equal(UnpakzStatus.Conflict, Assert.Throws<UnpakzException>(() => NativeArchive.Extract(zip, d.File("o"))).Status);
        Assert.Equal([9], File.ReadAllBytes(d.File("o/a.txt")));
        var skipped = NativeArchive.Extract(zip, d.File("o"), new ExtractOptions { Overwrite = OverwritePolicy.Skip });
        Assert.Equal(1UL, skipped.Skipped);
        Assert.Equal([9], File.ReadAllBytes(d.File("o/a.txt")));
        NativeArchive.Extract(zip, d.File("o"), new ExtractOptions { Overwrite = OverwritePolicy.Replace });
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.File("o/a.txt")));
    }

    [Fact]
    public void Garbage_and_missing_files_are_typed_errors()
    {
        using var d = new TempDir();
        var junk = d.Write("junk.zip", TestData.Random(4000, 5));
        var st = Assert.Throws<UnpakzException>(() => NativeArchive.List(junk)).Status;
        Assert.True(st is UnpakzStatus.Unsupported or UnpakzStatus.Corrupt);
        Assert.Equal(UnpakzStatus.Io, Assert.Throws<UnpakzException>(() => NativeArchive.List(d.File("missing.zip"))).Status);
    }

    [Fact]
    public void Filter_progress_and_cancellation_work_through_the_callbacks()
    {
        using var d = new TempDir();
        var zip = Archives.Zip(d, tree: new() { ["a.bin"] = TestData.Random(2_000_000, 1), ["b.txt"] = [1], ["c.txt"] = [2] });
        var seen = new List<ulong>();
        NativeArchive.Extract(zip, d.File("o1"), new ExtractOptions { Filter = e => e.RelativePath != "b.txt", Progress = (done, _, _) => seen.Add(done) });
        Assert.True(File.Exists(d.File("o1/a.bin")) && !File.Exists(d.File("o1/b.txt")) && File.Exists(d.File("o1/c.txt")));
        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(x => x), seen);

        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => NativeArchive.Extract(zip, d.File("o2"), new ExtractOptions { Cancellation = cts.Token, Progress = (_, _, _) => cts.Cancel() }));
        if (Directory.Exists(d.File("o2"))) Assert.Empty(Directory.EnumerateFiles(d.File("o2"), ".unpakz-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_throwing_callback_surfaces_as_an_error_not_a_crash()
    {
        using var d = new TempDir();
        var zip = Archives.Zip(d);
        Assert.Throws<InvalidOperationException>(() => NativeArchive.Extract(zip, d.File("o"), new ExtractOptions { Filter = _ => throw new FormatException("boom") }));
    }

    [Theory, MemberData(nameof(Formats))]
    public void ReadEntry_streams_exactly_one_entry(string format)
    {
        using var d = new TempDir();
        var path = Make(format, d);
        foreach (var e in NativeArchive.List(path).Where(e => e.Kind == EntryKind.File))
        {
            using var ms = new MemoryStream();
            NativeArchive.ReadEntry(path, e.Index, (buf, n) => { ms.Write(buf, 0, n); return true; });
            Assert.Equal(Archives.Tree[e.RelativePath], ms.ToArray());
        }

        var big = NativeArchive.List(path).Single(e => e.RelativePath == "data/blob.bin");
        var calls = 0;
        Assert.Equal(UnpakzStatus.Cancelled, Assert.Throws<UnpakzException>(() => NativeArchive.ReadEntry(path, big.Index, (_, _) => ++calls < 1)).Status);
        Assert.Equal(1, calls);
        Assert.Equal(UnpakzStatus.InvalidArgument, Assert.Throws<UnpakzException>(() => NativeArchive.ReadEntry(path, 999, (_, _) => true)).Status);
    }

    [Fact]
    public void The_native_guard_is_reachable_for_front_ends()
    {
        Assert.True(NativeArchive.TryCheckName("dir\\sub/a.txt", out var rel, out _));
        Assert.Equal("dir/sub/a.txt", rel);
        Assert.False(NativeArchive.TryCheckName("..\\x", out _, out var why));
        Assert.NotEmpty(why);
        Assert.False(NativeArchive.TryCheckName("CON", out _, out _));
    }
}
