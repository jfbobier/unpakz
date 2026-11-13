using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class ZipExtractorTests
{
    private static readonly string Dest = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));
    private static string P(string rel) => Path.Combine(Dest, rel.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Txt(int n) { var b = new byte[n]; for (var i = 0; i < n; i++) b[i] = (byte)"hello "[i % 6]; return b; }

    private static byte[] SampleZip(out Dictionary<string, byte[]> expected)
    {
        expected = new()
        {
            ["a.txt"] = Rnd(100), ["dir/b.bin"] = Rnd(70_000, 2), ["dir/sub/c.txt"] = Txt(50_000),
            ["empty"] = [], ["日本/é.txt"] = Txt(300),
        };
        var b = new ZipBuilder().Add("a.txt", expected["a.txt"]).AddDirectory("dir").Add("dir/b.bin", expected["dir/b.bin"], deflate: true)
            .Add("dir/sub/c.txt", expected["dir/sub/c.txt"], deflate: true).AddDirectory("emptydir").Add("empty", [])
            .Add("日本/é.txt", expected["日本/é.txt"], deflate: true);
        return b.Build();
    }

    private static ExtractionResult Run(byte[] zip, InMemoryFileSystem fs, ExtractOptions? o = null, IProgress<ExtractionProgress>? pr = null, CancellationToken ct = default) =>
        ZipExtractor.Extract(zip, Dest, o, fs, pr, ct);

    // ---- happy path ----
    [Fact]
    public void Extracts_tree_with_correct_content_directories_and_timestamps()
    {
        var zip = SampleZip(out var expected);
        var fs = new InMemoryFileSystem(Dest);
        var r = Run(zip, fs);

        foreach (var (rel, data) in expected) Assert.Equal(data, fs.Files[P(rel)]);
        Assert.Contains(P("emptydir"), fs.Dirs);
        Assert.Equal(expected.Count, r.Files);
        Assert.Equal(expected.Values.Sum(v => (long)v.Length), r.Bytes);
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local), fs.LastWrite[P("a.txt")]);
    }

    [Fact]
    public void Can_skip_timestamp_restoration()
    {
        var fs = new InMemoryFileSystem(Dest);
        Run(SampleZip(out _), fs, new ExtractOptions { RestoreTimestamps = false });
        Assert.All(fs.LastWrite.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Invalid_dos_timestamp_leaves_time_unset()
    {
        var zip = new ZipBuilder().Add("a", Rnd(5)).Build();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 14), 0);   // date = 0
        var fs = new InMemoryFileSystem(Dest);
        Run(zip, fs);
        Assert.Null(fs.LastWrite[P("a")]);
    }

    // ---- the single-write / no-temp-folder proofs ----
    [Fact]
    public void Every_byte_is_written_exactly_once_and_no_temp_directories_exist()
    {
        var zip = SampleZip(out var expected);
        var fs = new InMemoryFileSystem(Dest);
        Run(zip, fs);

        Assert.Equal(expected.Values.Sum(v => (long)v.Length), fs.BytesWritten);          // one write per byte
        Assert.Equal(expected.Count, fs.TempFiles.Count);                                  // one temp *file* per file
        Assert.All(fs.TempFiles, t => Assert.Equal(Path.GetDirectoryName(t.Final), Path.GetDirectoryName(t.Temp)));   // same dir => rename, not copy
        Assert.Equal(expected.Count, fs.Commits);
        Assert.Empty(fs.LeftoverTemps());

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Dest, P("dir"), P("dir/sub"), P("emptydir"), P("日本") };
        Assert.All(fs.CreatedDirs, d => Assert.Contains(d, allowed));                      // no scratch directories
        Assert.Equal(allowed.Count, fs.CreatedDirs.Count);                                 // each created once
    }

    // ---- security: refuse before touching anything ----
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("good/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:\\evil.txt")]
    public void Zip_slip_archive_is_refused_without_any_filesystem_change(string evil)
    {
        var zip = new ZipBuilder().Add("good.txt", Rnd(10)).Add(evil, Rnd(10)).Build();
        var fs = new InMemoryFileSystem(Dest);
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => Run(zip, fs)).Error);
        Assert.Empty(fs.CreatedDirs);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.TempFiles);
    }

    [Fact]
    public void Existing_symlinked_directory_in_destination_is_not_followed()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest);
        fs.Links.Add(P("link"));                       // link -> somewhere outside
        var zip = new ZipBuilder().Add("ok.txt", Rnd(5)).Add("link/x.txt", Rnd(5)).Build();
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => Run(zip, fs)).Error);
        Assert.DoesNotContain(fs.Files.Keys, k => k.Contains("x.txt", StringComparison.Ordinal));
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void Nested_symlinked_directory_is_detected_below_the_root()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest); fs.Dirs.Add(P("a"));
        fs.Links.Add(P("a/b"));
        var zip = new ZipBuilder().Add("a/b/c/x.txt", Rnd(5)).Build();
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => Run(zip, fs)).Error);
    }

    [Fact]
    public void Symlink_entries_in_archive_are_skipped_and_reported_not_created()
    {
        var fs = new InMemoryFileSystem(Dest);
        var zip = new ZipBuilder().Add("l", "/etc/shadow"u8.ToArray(), externalAttrs: ZipBuilder.SymlinkAttr).Add("f", Rnd(3)).Build();
        var r = Run(zip, fs);
        Assert.Equal(SkipReason.Symlink, Assert.Single(r.Skipped).Reason);
        Assert.False(fs.Files.ContainsKey(P("l")));
        Assert.Empty(fs.Links);
    }

    [Fact]
    public void Resolve_under_root_rejects_escapes_as_defence_in_depth()
    {
        Assert.Equal(P("a/b"), ZipExtractor.ResolveUnderRoot(Dest, "a/b"));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, "../x"));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, "a/../../x"));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, ".."));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, ""));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, "."));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, "/abs"));
        Assert.Throws<ArchiveException>(() => ZipExtractor.ResolveUnderRoot(Dest, "../" + Path.GetFileName(Dest) + "x/y"));   // sibling with same prefix
    }

    // ---- existing content policies ----
    private static (byte[] Zip, InMemoryFileSystem Fs) WithExisting()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest);
        fs.Files[P("a.txt")] = "OLD"u8.ToArray();
        return (new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Add("b.txt", "B"u8.ToArray()).Build(), fs);
    }

    [Fact]
    public void Existing_file_fails_by_default_and_is_left_untouched()
    {
        var (zip, fs) = WithExisting();
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() => Run(zip, fs)).Error);
        Assert.Equal("OLD"u8.ToArray(), fs.Files[P("a.txt")]);
        Assert.Empty(fs.LeftoverTemps());
        Assert.Empty(fs.TempFiles);                    // refused up front: no temp file, no decoding work
    }

    [Fact]
    public void Skip_policy_keeps_existing_file_and_reports_it()
    {
        var (zip, fs) = WithExisting();
        var r = Run(zip, fs, new ExtractOptions { Overwrite = OverwritePolicy.Skip });
        Assert.Equal("OLD"u8.ToArray(), fs.Files[P("a.txt")]);
        Assert.Equal("B"u8.ToArray(), fs.Files[P("b.txt")]);
        Assert.Equal(SkipReason.AlreadyExists, Assert.Single(r.Skipped).Reason);
    }

    [Fact]
    public void Overwrite_policy_replaces_existing_file()
    {
        var (zip, fs) = WithExisting();
        Run(zip, fs, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite });
        Assert.Equal("NEW"u8.ToArray(), fs.Files[P("a.txt")]);
    }

    [Fact]
    public void Overwrite_replaces_a_symlink_itself_not_its_target()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest);
        fs.Files["/outside-target"] = "PRECIOUS"u8.ToArray();
        fs.Links.Add(P("a.txt"));
        Run(new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Build(), fs, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite });
        Assert.Equal("PRECIOUS"u8.ToArray(), fs.Files["/outside-target"]);
        Assert.Equal("NEW"u8.ToArray(), fs.Files[P("a.txt")]);
        Assert.Empty(fs.Links);
    }

    [Fact]
    public void Existing_symlink_at_final_path_conflicts_under_default_policy()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest);
        fs.Links.Add(P("a.txt"));
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            Run(new ZipBuilder().Add("a.txt", Rnd(3)).Build(), fs)).Error);
    }

    [Fact]
    public void Directory_where_a_file_should_go_is_a_conflict_even_with_overwrite()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest); fs.Dirs.Add(P("a.txt"));
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            Run(new ZipBuilder().Add("a.txt", Rnd(3)).Build(), fs, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite })).Error);
        Assert.Empty(fs.TempFiles);                    // refused up front
    }

    [Fact]
    public void File_created_by_someone_else_after_our_check_is_never_overwritten()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.OnCreateFile = final => fs.Files[final] = "RACE"u8.ToArray();      // appears between the check and the commit
        var zip = new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Build();
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() => Run(zip, fs)).Error);
        Assert.Equal("RACE"u8.ToArray(), fs.Files[P("a.txt")]);
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void Destination_that_is_a_file_is_a_conflict()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Files[Dest] = [1];
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            Run(new ZipBuilder().Add("a", Rnd(3)).Build(), fs)).Error);
        Assert.Empty(fs.TempFiles);
    }

    [Fact]
    public void Preallocation_trusts_declared_size_only_below_the_limit()
    {
        var zip = new ZipBuilder().Add("small", Rnd(1000)).Add("big", Txt(50_000), deflate: true).Build();
        var fs = new InMemoryFileSystem(Dest);
        Run(zip, fs, new ExtractOptions { PreallocateLimit = 10_000 });
        Assert.Equal([1000L, 0L], fs.Preallocations);
    }

    [Fact]
    public void File_where_a_directory_is_needed_is_a_conflict()
    {
        var fs = new InMemoryFileSystem(Dest);
        fs.Dirs.Add(Dest);
        fs.Files[P("d")] = [1];
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            Run(new ZipBuilder().Add("d/x", Rnd(3)).Build(), fs)).Error);
    }

    [Fact]
    public void Destination_is_created_including_missing_parents()
    {
        var deep = Path.Combine(Dest, "x", "y", "z");
        var fs = new InMemoryFileSystem(Dest);
        ZipExtractor.Extract(new ZipBuilder().Add("f", Rnd(3)).Build(), deep, null, fs);
        Assert.True(fs.Files.ContainsKey(Path.Combine(deep, "f")));
    }

    // ---- failure handling ----
    [Fact]
    public void Corrupt_entry_aborts_cleanly_leaving_no_partial_file_or_temp()
    {
        var zip = new ZipBuilder().Add("one", Rnd(50)).Add("two", Rnd(50, 2)).Add("three", Rnd(50, 3)).Build();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        var secondLho = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 46 + 3 + 42));
        zip[secondLho + 30 + 3 + 10] ^= 0x55;                      // corrupt "two" data => CRC mismatch
        var fs = new InMemoryFileSystem(Dest);
        Assert.Throws<ArchiveException>(() => Run(zip, fs));
        Assert.True(fs.Files.ContainsKey(P("one")));
        Assert.False(fs.Files.ContainsKey(P("two")));
        Assert.False(fs.Files.ContainsKey(P("three")));
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void Cancellation_stops_extraction_and_cleans_up()
    {
        // b and c are empty, so they perform no writes: only the per-entry check can stop them.
        var zip = new ZipBuilder().Add("a", Rnd(1000)).Add("b", []).Add("c", []).Build();
        var fs = new InMemoryFileSystem(Dest);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.FilesCompleted == 1) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => Run(zip, fs, null, progress, cts.Token));
        Assert.True(fs.Files.ContainsKey(P("a")));
        Assert.False(fs.Files.ContainsKey(P("b")));
        Assert.False(fs.Files.ContainsKey(P("c")));
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void Cancellation_in_the_middle_of_a_large_file_discards_it()
    {
        var zip = new ZipBuilder().Add("big", Rnd(3 << 20)).Build();      // stored: written in 1 MiB chunks
        var fs = new InMemoryFileSystem(Dest);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.BytesWritten >= 1 << 20) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => Run(zip, fs, null, progress, cts.Token));
        Assert.False(fs.Files.ContainsKey(P("big")));
        Assert.Empty(fs.LeftoverTemps());
        Assert.True(fs.BytesWritten < 3 << 20);
    }

    [Fact]
    public void Cancelled_before_start_touches_nothing()
    {
        var fs = new InMemoryFileSystem(Dest);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Run(new ZipBuilder().Add("a", Rnd(5)).Build(), fs, null, null, cts.Token));
        Assert.Empty(fs.Files);
    }

    private sealed class SyncProgress(Action<ExtractionProgress> a) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => a(value);
    }

    [Fact]
    public void Progress_reports_monotonic_totals_ending_at_the_full_amount()
    {
        var zip = SampleZip(out var expected);
        var seen = new List<ExtractionProgress>();
        Run(zip, new InMemoryFileSystem(Dest), null, new SyncProgress(seen.Add));
        var total = expected.Values.Sum(v => (long)v.Length);
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(total, p.TotalBytes));
        for (var i = 1; i < seen.Count; i++) Assert.True(seen[i].BytesWritten >= seen[i - 1].BytesWritten);
        Assert.Equal(total, seen[^1].BytesWritten);
        Assert.Equal(expected.Count, seen[^1].FilesCompleted);
    }

    // ---- robustness through the whole pipeline ----
    private static void Try(byte[] zip)
    {
        var fs = new InMemoryFileSystem(Dest);
        try { Run(zip, fs, new ExtractOptions { Limits = new ZipLimits { MaxEntrySize = 1 << 20 } }); }
        catch (ArchiveException) { }
        Assert.Empty(fs.LeftoverTemps());                              // invariant: failure never leaves temp files
        Assert.All(fs.Files.Keys, k => Assert.StartsWith(Dest, k, StringComparison.Ordinal));   // nothing outside the destination
        Assert.All(fs.CreatedDirs, d => Assert.StartsWith(Dest, d, StringComparison.Ordinal));
    }

    private static byte[] SmallZip() => new ZipBuilder()
        .Add("a.txt", Rnd(120)).AddDirectory("dir").Add("dir/b.txt", Txt(1500), deflate: true).Add("dir/s/c", Rnd(40, 5)).Build();

    [Fact]
    public void Every_truncation_leaves_no_temp_files_and_nothing_outside_destination()
    {
        var zip = SmallZip();
        for (var len = 0; len < zip.Length; len++) Try(zip.AsSpan(0, len).ToArray());
    }

    [Fact]
    public void Every_byte_corruption_leaves_no_temp_files_and_nothing_outside_destination()
    {
        var original = SmallZip();
        var zip = (byte[])original.Clone();
        for (var i = 0; i < zip.Length; i++)
            foreach (var mask in new byte[] { 0x01, 0x20, 0xFF })
            {
                zip[i] ^= mask;
                Try(zip);
                zip[i] = original[i];
            }
    }
}
