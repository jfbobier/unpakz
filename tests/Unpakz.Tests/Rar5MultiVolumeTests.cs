using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Rar;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class Rar5MultiVolumeTests
{
    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    private static IReadOnlyList<ReadOnlyMemory<byte>> Mem(IEnumerable<byte[]> volumes) => [.. volumes.Select(v => (ReadOnlyMemory<byte>)v)];

    private static Rar5Archive Open(IEnumerable<byte[]> volumes, ArchiveLimits? limits = null) => Rar5Archive.Open(Mem(volumes), limits ?? new ArchiveLimits());

    private static ArchiveException OpenFails(IEnumerable<byte[]> volumes, ArchiveLimits? limits = null) =>
        Assert.Throws<ArchiveException>(() => Open(volumes, limits));

    private static byte[] Extract(Rar5Archive a, string name)
    {
        var entry = a.Entries.Single(e => e.Name == name);
        var sink = new MemorySink();
        a.CheckSupported(entry);
        a.Extract(entry, sink, new ArchiveLimits());
        return sink.ToArray();
    }

    private static (List<byte[]> Volumes, byte[] A, byte[] B, byte[] C) Sample(int perVolume = 4000)
    {
        var a = Rnd(10_000, 1); var b = Rnd(2500, 2); var c = Rnd(9000, 3);
        var vols = new Rar5Builder().AddFile("a.bin", a).AddDirectory("d").AddFile("d/b.bin", b).AddFile("empty", []).AddFile("c.bin", c).BuildVolumes(perVolume);
        return (vols, a, b, c);
    }

    // ---- happy path ----
    [Fact]
    public void A_set_lists_and_extracts_exactly_like_one_archive()
    {
        var (vols, a, b, c) = Sample();
        Assert.True(vols.Count >= 5, "the sample should span several volumes");
        var set = Open(vols);
        var single = Rar5Archive.Open(new Rar5Builder().AddFile("a.bin", a).AddDirectory("d").AddFile("d/b.bin", b).AddFile("empty", []).AddFile("c.bin", c).Build(), new ArchiveLimits());

        Assert.Equal(single.Entries.Select(e => (e.Name, e.Kind, e.UncompressedSize)), set.Entries.Select(e => (e.Name, e.Kind, e.UncompressedSize)));
        Assert.Equal(a, Extract(set, "a.bin"));
        Assert.Equal(b, Extract(set, "d/b.bin"));
        Assert.Equal(c, Extract(set, "c.bin"));
        Assert.Empty(Extract(set, "empty"));
        Assert.Equal("rar5", set.Format);
        Assert.Equal((ulong)a.Length, set.Entries.Single(e => e.Name == "a.bin").CompressedSize);     // packed size is the sum of the parts
    }

    [Fact]
    public void A_file_can_span_many_volumes_and_a_volume_can_hold_several_files()
    {
        var data = Rnd(50_000, 4);
        var vols = new Rar5Builder().AddFile("small1", [1, 2, 3]).AddFile("big", data).AddFile("small2", [4, 5]).BuildVolumes(700);
        Assert.True(vols.Count > 60);
        var set = Open(vols);
        Assert.Equal(data, Extract(set, "big"));
        Assert.Equal([1, 2, 3], Extract(set, "small1"));
        Assert.Equal([4, 5], Extract(set, "small2"));
    }

    [Fact]
    public void A_single_volume_set_is_just_an_archive()
    {
        var vols = new Rar5Builder().AddFile("a", [1, 2, 3]).BuildVolumes(100);
        Assert.Single(vols);
        Assert.Equal([1, 2, 3], Extract(Open(vols), "a"));
    }

    [Fact]
    public void Spans_across_volumes_do_not_overlap()
    {
        var set = Open(Sample().Volumes);
        var spans = set.Entries.Select(set.GetSpan).ToArray();
        for (var i = 0; i < spans.Length; i++) Assert.True(spans[i].Start < spans[i].End);
        for (var i = 1; i < spans.Length; i++) Assert.True(spans[i].Start >= spans[i - 1].End, $"entry {i} overlaps entry {i - 1}");
    }

    [Fact]
    public void Corruption_in_a_middle_volume_is_caught_by_the_whole_file_crc()
    {
        var (vols, _, _, _) = Sample();
        var probe = Open(vols);
        var (start, end) = probe.GetSpan(probe.Entries.Single(e => e.Name == "a.bin"));
        Assert.True(end > start);
        vols[1][vols[1].Length / 2] ^= 0x40;
        var ex = Assert.Throws<ArchiveException>(() => Extract(Open(vols), "a.bin"));
        Assert.Equal(ArchiveError.Corrupt, ex.Error);
    }

    // ---- volume sequencing ----
    [Fact]
    public void A_missing_middle_volume_is_reported_by_number()
    {
        var (vols, _, _, _) = Sample();
        vols.RemoveAt(2);
        var ex = OpenFails(vols);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(3, ex.Volume);                      // 1-based: part 3 is the one missing
    }

    [Fact]
    public void A_missing_first_volume_is_reported()
    {
        var (vols, _, _, _) = Sample();
        vols.RemoveAt(0);
        var ex = OpenFails(vols);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(1, ex.Volume);
    }

    [Fact]
    public void A_missing_last_volume_is_reported_because_the_end_block_says_more_follow()
    {
        var (vols, _, _, _) = Sample();
        var n = vols.Count;
        vols.RemoveAt(n - 1);
        var ex = OpenFails(vols);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(n, ex.Volume);
    }

    [Fact]
    public void Only_the_first_volume_of_a_set_is_an_incomplete_set()
    {
        var (vols, _, _, _) = Sample();
        var ex = OpenFails([vols[0]]);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(2, ex.Volume);
    }

    [Fact]
    public void Volumes_out_of_order_or_duplicated_are_corrupt()
    {
        var (vols, _, _, _) = Sample();
        // a repeated volume 1 where volume 2 belongs
        Assert.Equal(ArchiveError.Corrupt, OpenFails([vols[0], vols[0], .. vols.Skip(1)]).Error);

        // volume 2 first: volume 1 is missing
        var second = OpenFails([vols[1], vols[0], .. vols.Skip(2)]);
        Assert.Equal(ArchiveError.MissingVolume, second.Error);
        Assert.Equal(1, second.Volume);

        // volumes 3 and 4 swapped: the third slot holds a later volume, so part 3 is missing from where it belongs
        var swapped = new List<byte[]>(vols);
        (swapped[2], swapped[3]) = (swapped[3], swapped[2]);
        var ex = OpenFails(swapped);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(3, ex.Volume);
    }

    [Fact]
    public void A_volume_from_another_set_is_rejected()
    {
        var (vols, _, _, _) = Sample();
        var other = new Rar5Builder().AddFile("x", Rnd(9000, 9)).BuildVolumes(4000);
        var mixed = new List<byte[]>(vols) { [1] = other[1] };
        Assert.Throws<ArchiveException>(() => Extract(Open(mixed), "a.bin"));
    }

    [Fact]
    public void Several_volumes_given_but_the_first_is_not_part_of_a_set_is_corrupt()
    {
        var single = new Rar5Builder().AddFile("a", [1]).Build();
        var (vols, _, _, _) = Sample();
        Assert.Equal(ArchiveError.Corrupt, OpenFails([single, vols[1]]).Error);
    }

    [Fact]
    public void A_middle_volume_whose_end_block_says_it_is_last_is_corrupt()
    {
        var (vols, _, _, _) = Sample();
        SetEndFlags(vols[1], EndBlockStart(vols[1]), 0);
        Assert.Equal(ArchiveError.Corrupt, OpenFails(vols).Error);
    }

    [Fact]
    public void A_last_volume_that_claims_more_is_missing_a_volume()
    {
        var (vols, _, _, _) = Sample();
        var last = vols[^1];
        SetEndFlags(last, EndBlockStart(last), 1);
        var ex = OpenFails(vols);
        Assert.Equal(ArchiveError.MissingVolume, ex.Error);
        Assert.Equal(vols.Count + 1, ex.Volume);
    }

    private static int EndBlockStart(byte[] volume) => volume.Length - 8;       // crc(4) size(1) type(1) flags(1) endflags(1)

    private static void SetEndFlags(byte[] volume, int blockStart, byte endFlags)
    {
        volume[blockStart + 7] = endFlags;
        Rar5Builder.FixHeaderCrc(volume, blockStart);
    }

    // ---- split-file continuity ----
    private static int FirstFileBlock(byte[] volume)
    {
        var p = Rar5Builder.Signature.Length;
        Assert.True(VInt.TryRead(volume.AsSpan(p + 4), out var size, out var n));
        return p + 4 + n + (int)size;
    }

    [Fact]
    public void Continuation_parts_must_agree_with_the_first_part()
    {
        // names differ
        var a = new Rar5Builder().AddFile("aaaa", Rnd(300, 1)).BuildVolumes(100);
        var b = new Rar5Builder().AddFile("bbbb", Rnd(300, 1)).BuildVolumes(100);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([a[0], b[1], b[2]]).Error);

        // declared sizes differ
        var c = new Rar5Builder().AddFile("aaaa", Rnd(400, 1)).BuildVolumes(100);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([a[0], c[1], c[2]]).Error);
    }

    [Fact]
    public void Compression_info_and_host_must_match_across_parts()
    {
        var m0 = new Rar5Builder().AddFile("f", Rnd(300, 1)).BuildVolumes(100);
        var m3 = new Rar5Builder().AddFile("f", Rnd(300, 1), new FileOpts { Method = 3 }).BuildVolumes(100);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([m0[0], m3[1], m3[2]]).Error);
        var win = new Rar5Builder().AddFile("f", Rnd(300, 1), new FileOpts { Host = 0 }).BuildVolumes(100);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([m0[0], win[1], win[2]]).Error);
    }

    [Fact]
    public void A_file_that_never_continues_is_corrupt()
    {
        var vols = new Rar5Builder().AddFile("f", Rnd(300, 1)).AddFile("g", Rnd(100, 2)).BuildVolumes(100);
        // drop the volume holding the continuation by making the previous end block say "last"
        var v0 = vols[0];
        SetEndFlags(v0, EndBlockStart(v0), 0);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([v0]).Error);
    }

    [Fact]
    public void An_entry_that_does_not_continue_the_pending_split_is_corrupt()
    {
        // Set Y: "f" starts in volume 1 and continues. Set X: its volume 2 begins with a brand-new file "g" (no continuation flag).
        var y = new Rar5Builder().AddFile("f", Rnd(250, 1)).BuildVolumes(100);
        var x = new Rar5Builder().AddFile("pad", Rnd(100, 2)).AddFile("g", Rnd(50, 3)).BuildVolumes(100);
        Assert.Equal(2, x.Count);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([y[0], x[1]]).Error);
    }

    [Fact]
    public void A_continuation_part_with_nothing_before_it_is_corrupt()
    {
        var vols = new Rar5Builder().AddFile("f", Rnd(300, 1)).BuildVolumes(100);
        // a "split before" flag on the very first part of the very first volume has nothing to continue
        var v0 = (byte[])vols[0].Clone();
        var block = FirstFileBlock(v0);
        var flagsPos = FlagsPosition(v0, block);
        v0[flagsPos] |= 0x08;                                   // "split before" on the very first part
        Rar5Builder.FixHeaderCrc(v0, block);
        Assert.Equal(ArchiveError.Corrupt, OpenFails([v0, vols[1], vols[2]]).Error);
    }

    private static int FlagsPosition(byte[] rar, int blockStart)
    {
        var p = blockStart + 4;
        Assert.True(VInt.TryRead(rar.AsSpan(p), out _, out var n1));
        p += n1;
        Assert.True(VInt.TryRead(rar.AsSpan(p), out _, out var n2));
        return p + n2;
    }

    // ---- limits and non-RAR ----
    [Fact]
    public void Volume_count_and_entry_limits_are_enforced()
    {
        var vols = new Rar5Builder().AddFile("f", Rnd(500, 1)).BuildVolumes(100);
        Assert.Equal(ArchiveError.LimitExceeded, OpenFails(vols, new ArchiveLimits { MaxVolumes = 3 }).Error);
        Open(vols, new ArchiveLimits { MaxVolumes = vols.Count });

        var many = new Rar5Builder().AddFile("a", [1]).AddFile("b", [2]).AddFile("c", [3]).BuildVolumes(1);
        Assert.Equal(ArchiveError.LimitExceeded, OpenFails(many, new ArchiveLimits { MaxEntries = 2 }).Error);
    }

    [Fact]
    public void Zero_volumes_and_non_rar_volumes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Rar5Archive.Open((IReadOnlyList<ReadOnlyMemory<byte>>)[], new ArchiveLimits()));
        var (vols, _, _, _) = Sample();
        Assert.Throws<ArchiveException>(() => Open([vols[0], new byte[100]]));
    }

    [Fact]
    public void Reader_routes_several_volumes_to_rar_and_refuses_them_for_other_formats()
    {
        var (vols, _, _, _) = Sample();
        Assert.Equal("rar5", ArchiveReader.Open(Mem(vols)).Format);
        Assert.Equal(ArchiveError.MissingVolume, Assert.Throws<ArchiveException>(() => ArchiveReader.Open(Mem([vols[0]]))).Error);

        var zip = new ZipBuilder().Add("a", [1]).Build();
        var ex = Assert.Throws<ArchiveException>(() => ArchiveReader.Open(Mem([zip, zip])));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
        Assert.Equal("zip", ArchiveReader.Open(Mem([zip])).Format);
    }

    // ---- through the whole pipeline ----
    private static readonly string Dest = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));

    [Fact]
    public void Pipeline_extracts_a_volume_set()
    {
        var (vols, a, b, c) = Sample();
        var fs = new InMemoryFileSystem(Dest);
        var r = ArchiveExtractor.Extract(Mem(vols), Dest, null, fs);
        Assert.Equal(a, fs.Files[Path.Combine(Dest, "a.bin")]);
        Assert.Equal(b, fs.Files[Path.Combine(Dest, "d", "b.bin")]);
        Assert.Equal(c, fs.Files[Path.Combine(Dest, "c.bin")]);
        Assert.Equal(4, r.Files);
        Assert.Equal(a.Length + b.Length + c.Length, fs.BytesWritten);
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public void Pipeline_refuses_an_incomplete_set_before_writing_anything()
    {
        var (vols, _, _, _) = Sample();
        vols.RemoveAt(3);
        var fs = new InMemoryFileSystem(Dest);
        Assert.Equal(ArchiveError.MissingVolume, Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(Mem(vols), Dest, null, fs)).Error);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.CreatedDirs);
    }

    [Fact]
    public void Hostile_names_inside_a_volume_set_are_still_refused()
    {
        var vols = new Rar5Builder().AddFile("ok", Rnd(500, 1)).AddFile("../evil", Rnd(500, 2)).BuildVolumes(200);
        var fs = new InMemoryFileSystem(Dest);
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(Mem(vols), Dest, null, fs)).Error);
        Assert.Empty(fs.Files);
    }

    // ---- robustness ----
    [Fact]
    public void Every_truncation_and_corruption_of_every_volume_is_handled()
    {
        var vols = new Rar5Builder().AddFile("a", Rnd(260, 1)).AddFile("b", Rnd(140, 2)).BuildVolumes(120);
        void TryAll(List<byte[]> set)
        {
            try
            {
                var a = Open(set);
                foreach (var e in a.Entries.Where(e => e.Kind == EntryKind.File))
                {
                    a.CheckSupported(e);
                    a.Extract(e, new MemorySink(), new ArchiveLimits { MaxEntrySize = 1 << 20 });
                }
            }
            catch (ArchiveException) { }
        }

        for (var v = 0; v < vols.Count; v++)
        {
            for (var len = 0; len < vols[v].Length; len++)
            {
                var set = vols.Select((x, i) => i == v ? x[..len] : x).ToList();
                TryAll(set);
            }

            for (var i = 0; i < vols[v].Length; i++)
                foreach (var mask in new byte[] { 0x01, 0x80, 0xFF })
                {
                    var copy = vols.Select(x => (byte[])x.Clone()).ToList();
                    copy[v][i] ^= mask;
                    TryAll(copy);
                }
        }
    }
}
