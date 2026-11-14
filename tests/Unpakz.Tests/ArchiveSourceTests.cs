using Unpakz.Core;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

[Collection("ArchiveSource")]
public sealed class ArchiveSourceTests : IDisposable
{
    private readonly string _dir = Oracle.NewWorkDir("src");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name, byte[]? content = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content ?? [1]);
        return path;
    }

    private static string[] Names(IEnumerable<string> paths) => [.. paths.Select(Path.GetFileName).Select(n => n!)];

    // ---- VolumeLocator ----
    [Fact]
    public void Finds_every_volume_from_any_volume_in_numeric_order()
    {
        foreach (var n in new[] { "b.part1.rar", "b.part2.rar", "b.part10.rar", "b.part3.rar", "b.part4.rar", "b.part5.rar", "b.part6.rar", "b.part7.rar", "b.part8.rar", "b.part9.rar" })
            Touch(n);

        var expected = Enumerable.Range(1, 10).Select(i => $"b.part{i}.rar").ToArray();
        foreach (var start in new[] { "b.part1.rar", "b.part7.rar", "b.part10.rar" })
            Assert.Equal(expected, Names(VolumeLocator.Find(Path.Combine(_dir, start))));
    }

    [Fact]
    public void Zero_padding_width_does_not_matter_and_other_sets_and_files_are_ignored()
    {
        Touch("a.part01.rar"); Touch("a.part02.rar"); Touch("a.part3.rar");
        Touch("other.part1.rar"); Touch("a.rar"); Touch("a.part1.zip"); Touch("a.part1.rar.bak"); Touch("a.partx.rar");
        Assert.Equal(["a.part01.rar", "a.part02.rar", "a.part3.rar"], Names(VolumeLocator.Find(Path.Combine(_dir, "a.part02.rar"))));
    }

    [Fact]
    public void Matching_ignores_letter_case()
    {
        Touch("Doc.PART1.RAR"); Touch("doc.part2.rar");
        Assert.Equal(["Doc.PART1.RAR", "doc.part2.rar"], Names(VolumeLocator.Find(Path.Combine(_dir, "doc.part2.rar"))));
    }

    [Fact]
    public void A_gap_is_reported_as_the_first_missing_volume_number()
    {
        Touch("g.part1.rar"); Touch("g.part2.rar"); Touch("g.part4.rar");
        var ex = Assert.Throws<ArchiveException>(() => VolumeLocator.Find(Path.Combine(_dir, "g.part1.rar")));
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(3, ex.Volume);
    }

    [Fact]
    public void Starting_from_a_later_part_without_part_one_reports_volume_one()
    {
        Touch("h.part3.rar"); Touch("h.part4.rar");
        var ex = Assert.Throws<ArchiveException>(() => VolumeLocator.Find(Path.Combine(_dir, "h.part3.rar")));
        Assert.Equal(1, ex.Volume);
    }

    [Fact]
    public void A_plain_file_is_a_set_of_one_and_a_missing_file_is_an_error()
    {
        var p = Touch("plain.rar");
        Assert.Equal([p], VolumeLocator.Find(p));
        Assert.Throws<FileNotFoundException>(() => VolumeLocator.Find(Path.Combine(_dir, "nope.rar")));
    }

    [Fact]
    public void Absurd_part_numbers_are_ignored_not_fatal()
    {
        Touch("z.part1.rar"); Touch("z.part99999999999999999999999.rar");
        Assert.Equal(["z.part1.rar"], Names(VolumeLocator.Find(Path.Combine(_dir, "z.part1.rar"))));
    }

    [Fact]
    public void Part_zero_is_not_a_volume()
    {
        Touch("w.part0.rar"); Touch("w.part1.rar");
        Assert.Equal(["w.part1.rar"], Names(VolumeLocator.Find(Path.Combine(_dir, "w.part1.rar"))));
    }

    [Fact]
    public void The_volume_limit_is_enforced()
    {
        for (var i = 1; i <= 12; i++) Touch($"m.part{i}.rar");
        var ex = Assert.Throws<ArchiveException>(() => VolumeLocator.Find(Path.Combine(_dir, "m.part1.rar"), maxVolumes: 10));
        Assert.Equal(ArchiveError.LimitExceeded, ex.Error);
        Assert.Equal(12, VolumeLocator.Find(Path.Combine(_dir, "m.part1.rar"), maxVolumes: 12).Count);
    }

    // ---- MappedFile ----
    [Fact]
    public void Mapped_file_exposes_the_exact_bytes_without_copying_into_the_managed_heap()
    {
        var data = new byte[200_000];
        new Random(5).NextBytes(data);
        using var mapped = MappedFile.Open(Touch("data.bin", data));
        Assert.Equal(data.Length, mapped.Memory.Length);
        Assert.True(mapped.Memory.Span.SequenceEqual(data));
        Assert.Equal(data.AsSpan(1000, 50).ToArray(), mapped.Memory.Slice(1000, 50).ToArray());
    }

    [Fact]
    public void Using_the_memory_after_dispose_throws_instead_of_touching_unmapped_memory()
    {
        var mapped = MappedFile.Open(Touch("late.bin", [1, 2, 3, 4]));
        var memory = mapped.Memory;
        Assert.Equal(4, memory.Span.Length);
        mapped.Dispose();
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
    }

    [Fact]
    public void An_archive_used_after_its_source_is_disposed_fails_with_a_catchable_error()
    {
        var source = ArchiveSource.Open(Touch("stale.zip", new ZipBuilder().Add("a", "data"u8.ToArray()).Build()));
        var archive = source.Archive;
        var entry = archive.Entries[0];
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => archive.Extract(entry, new MemorySink(), new ArchiveLimits()));
    }

    [Fact]
    public void An_empty_file_maps_to_empty_memory()
    {
        using var mapped = MappedFile.Open(Touch("empty.bin", []));
        Assert.True(mapped.Memory.IsEmpty);
    }

    [Fact]
    public void Disposing_twice_is_harmless_and_a_missing_file_throws()
    {
        var mapped = MappedFile.Open(Touch("d.bin", [1, 2, 3]));
        mapped.Dispose();
        mapped.Dispose();
        Assert.Throws<FileNotFoundException>(() => MappedFile.Open(Path.Combine(_dir, "missing.bin")));
    }

    [Fact]
    public void A_file_beyond_two_gibibytes_is_refused_with_a_clear_error()
    {
        var path = Path.Combine(_dir, "huge.bin");
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(int.MaxValue + 1L);      // sparse: cheap
        var ex = Assert.Throws<ArchiveException>(() => MappedFile.Open(path));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
        Assert.Contains("2 GiB", ex.Message, StringComparison.Ordinal);
    }

    // ---- ArchiveSource ----
    [Fact]
    public void Opens_a_single_zip_from_disk_and_extracts_it()
    {
        var zip = new ZipBuilder().Add("a.txt", "hello"u8.ToArray()).Add("d/b.txt", "world"u8.ToArray()).Build();
        using var source = ArchiveSource.Open(Touch("one.zip", zip));
        Assert.Equal("zip", source.Archive.Format);
        Assert.Equal(["one.zip"], Names(source.VolumePaths));

        var dest = Path.Combine(_dir, "out");
        ArchiveExtractor.Extract(source.Archive, dest);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(dest, "d", "b.txt")));
    }

    [Fact]
    public void Opens_a_volume_set_from_any_part_and_extracts_it()
    {
        var a = new byte[9000]; new Random(1).NextBytes(a);
        var b = new byte[3000]; new Random(2).NextBytes(b);
        var vols = new Rar5Builder().AddFile("a.bin", a).AddFile("b.bin", b).BuildVolumes(2500);
        for (var i = 0; i < vols.Count; i++) Touch($"set.part{i + 1}.rar", vols[i]);

        using var source = ArchiveSource.Open(Path.Combine(_dir, "set.part3.rar"));
        Assert.Equal(vols.Count, source.VolumePaths.Count);
        var dest = Path.Combine(_dir, "out");
        ArchiveExtractor.Extract(source.Archive, dest);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(dest, "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(dest, "b.bin")));
    }

    [Fact]
    public void A_set_with_a_missing_volume_fails_to_open_and_leaves_no_file_handles_behind()
    {
        var vols = new Rar5Builder().AddFile("a.bin", new byte[9000]).BuildVolumes(2500);
        for (var i = 0; i < vols.Count; i++) if (i != 1) Touch($"gap.part{i + 1}.rar", vols[i]);

        var before = MappedFile.LiveMappings;
        var ex = Assert.Throws<ArchiveException>(() => ArchiveSource.Open(Path.Combine(_dir, "gap.part1.rar")));
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(before, MappedFile.LiveMappings);                        // nothing was left mapped
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);           // would throw on Windows if a mapping were still open
    }

    [Fact]
    public void A_corrupt_file_fails_to_open_cleanly_and_releases_its_mappings()
    {
        var p = Touch("junk.rar", new byte[500]);
        var before = MappedFile.LiveMappings;
        Assert.Throws<ArchiveException>(() => ArchiveSource.Open(p));
        Assert.Equal(before, MappedFile.LiveMappings);
        File.Delete(p);

        // a set whose later volume is garbage: the earlier, valid mappings must be released too
        var vols = new Rar5Builder().AddFile("a.bin", new byte[9000]).BuildVolumes(2500);
        for (var i = 0; i < vols.Count; i++) Touch($"half.part{i + 1}.rar", i == 2 ? new byte[300] : vols[i]);
        Assert.Throws<ArchiveException>(() => ArchiveSource.Open(Path.Combine(_dir, "half.part1.rar")));
        Assert.Equal(before, MappedFile.LiveMappings);
    }

    [Fact]
    public void Disposing_the_source_releases_the_files()
    {
        var p = Touch("rel.zip", new ZipBuilder().Add("a", [1]).Build());
        var before = MappedFile.LiveMappings;
        var source = ArchiveSource.Open(p);
        Assert.Equal(before + 1, MappedFile.LiveMappings);
        source.Dispose();
        source.Dispose();
        Assert.Equal(before, MappedFile.LiveMappings);
        File.Delete(p);
    }
}
