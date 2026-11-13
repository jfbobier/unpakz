using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Rar;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class Rar5ArchiveTests
{
    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    private static Rar5Archive Open(byte[] rar, ArchiveLimits? limits = null) => Rar5Archive.Open(rar, limits ?? new ArchiveLimits());

    private static ArchiveError OpenError(byte[] rar, ArchiveLimits? limits = null) =>
        Assert.Throws<ArchiveException>(() => Open(rar, limits)).Error;

    private static byte[] ExtractAll(Rar5Archive a, int index)
    {
        var sink = new MemorySink();
        a.CheckSupported(a.Entries[index]);
        a.Extract(a.Entries[index], sink, new ArchiveLimits());
        return sink.ToArray();
    }

    private static byte[] Sample(out byte[] a, out byte[] b)
    {
        a = Rnd(300); b = Rnd(5000, 2);
        return new Rar5Builder().AddFile("a.bin", a).AddDirectory("dir").AddFile("dir/日本 b.bin", b).AddFile("empty", []).Build();
    }

    // ---- signatures ----
    [Fact]
    public void Detects_signatures()
    {
        Assert.True(Rar5Archive.HasSignature(Rar5Builder.Signature));
        Assert.False(Rar5Archive.HasSignature(Rar5Builder.Signature.AsSpan(0, 7)));
        Assert.False(Rar5Archive.HasSignature("PK\u0003\u0004"u8));
        Assert.True(Rar5Archive.HasRar4Signature([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00]));
        Assert.False(Rar5Archive.HasRar4Signature(Rar5Builder.Signature));
    }

    [Fact]
    public void Reader_routes_rar5_and_reports_rar4_as_unsupported()
    {
        Assert.Equal("rar5", ArchiveReader.Open(Sample(out _, out _)).Format);
        var ex = Assert.Throws<ArchiveException>(() => ArchiveReader.Open(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 1, 2, 3, 4, 5 }));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
    }

    // ---- happy path ----
    [Fact]
    public void Lists_entries_with_kinds_sizes_names_and_times()
    {
        var a = Open(Sample(out _, out _));
        Assert.Equal("rar5", a.Format);
        Assert.Equal(["a.bin", "dir", "dir/日本 b.bin", "empty"], a.Entries.Select(e => e.Name));
        Assert.Equal([EntryKind.File, EntryKind.Directory, EntryKind.File, EntryKind.File], a.Entries.Select(e => e.Kind));
        Assert.Equal([300UL, 0UL, 5000UL, 0UL], a.Entries.Select(e => e.UncompressedSize));
        Assert.Equal([300UL, 0UL, 5000UL, 0UL], a.Entries.Select(e => e.CompressedSize));
        Assert.Equal([0, 1, 2, 3], a.Entries.Select(e => e.Index));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).LocalDateTime, a.Entries[0].LastWriteLocal);
    }

    [Fact]
    public void Extracts_stored_files()
    {
        var a = Open(Sample(out var d1, out var d2));
        Assert.Equal(d1, ExtractAll(a, 0));
        Assert.Equal(d2, ExtractAll(a, 2));
        Assert.Empty(ExtractAll(a, 3));
    }

    [Fact]
    public void Spans_tile_the_archive_without_overlap()
    {
        var a = Open(Sample(out _, out _));
        var spans = a.Entries.Select(a.GetSpan).ToArray();
        for (var i = 1; i < spans.Length; i++) Assert.Equal(spans[i - 1].End, spans[i].Start);
        Assert.True(spans[0].Start > (ulong)Rar5Builder.Signature.Length);        // main header sits before the first entry
    }

    [Fact]
    public void Time_extras_override_header_time()
    {
        var unix = new Rar5Builder().AddFile("u", [1], new FileOpts { Extras = [Rar5Builder.UnixTimeExtra(1_600_000_000)] }).Build();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000).LocalDateTime, Open(unix).Entries[0].LastWriteLocal);

        const ulong ft = 133_000_000_000_000_000UL;   // FILETIME
        var win = new Rar5Builder().AddFile("w", [1], new FileOpts { Mtime = null, Extras = [Rar5Builder.WindowsTimeExtra(ft)] }).Build();
        Assert.Equal(DateTime.FromFileTimeUtc((long)ft).ToLocalTime(), Open(win).Entries[0].LastWriteLocal);

        var none = new Rar5Builder().AddFile("n", [1], new FileOpts { Mtime = null }).Build();
        Assert.Null(Open(none).Entries[0].LastWriteLocal);

        var absurd = new Rar5Builder().AddFile("x", [1], new FileOpts { Mtime = null, Extras = [Rar5Builder.WindowsTimeExtra(ulong.MaxValue)] }).Build();
        Assert.Null(Open(absurd).Entries[0].LastWriteLocal);
    }

    [Fact]
    public void Unknown_blocks_flagged_skippable_are_ignored_and_trailing_bytes_tolerated()
    {
        var zip = new Rar5Builder { Trailing = [1, 2, 3, 4] }
            .AddBlock(99, 0x04, [1, 2, 3])
            .AddBlock(3, 0, Rar5Builder.VInt(0), data: [9, 9, 9])          // service header with data
            .AddFile("a", [7]).Build();
        Assert.Equal("a", Assert.Single(Open(zip).Entries).Name);
    }

    // ---- kinds ----
    [Fact]
    public void Redirection_records_make_a_link_entry()
    {
        foreach (var t in new ulong[] { 1, 2, 3, 4, 5 })
        {
            var rar = new Rar5Builder().AddFile("l", [], new FileOpts { Extras = [Rar5Builder.RedirectionExtra(t, "/etc/passwd")] }).Build();
            Assert.Equal(EntryKind.Symlink, Assert.Single(Open(rar).Entries).Kind);
        }
    }

    [Fact]
    public void Unix_attributes_decide_symlink_special_and_directory_kinds()
    {
        var rar = new Rar5Builder()
            .AddFile("sym", [], new FileOpts { Attr = 0xA1FF })
            .AddFile("fifo", [], new FileOpts { Attr = 0x11A4 })
            .AddFile("chr", [], new FileOpts { Attr = 0x21A4 })
            .AddFile("dirbymode", [], new FileOpts { Attr = 0x41ED })
            .AddFile("winfile", [1], new FileOpts { Host = 0, Attr = 0xA000 })   // Windows host: Unix type bits are meaningless
            .Build();
        Assert.Equal([EntryKind.Symlink, EntryKind.Special, EntryKind.Special, EntryKind.Directory, EntryKind.File], Open(rar).Entries.Select(e => e.Kind));
    }

    // ---- unsupported (honestly reported, never mis-extracted) ----
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void Reserved_compression_methods_are_unsupported(int method)
    {
        var a = Open(new Rar5Builder().AddFile("c", [1, 2, 3], new FileOpts { Method = method }).Build());
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.Extract(a.Entries[0], new MemorySink(), new ArchiveLimits())).Error);
    }

    [Fact]
    public void Garbage_in_a_compressed_entry_is_corrupt_not_unsupported()
    {
        var a = Open(new Rar5Builder().AddFile("c", [1, 2, 3, 4, 5, 6, 7, 8], new FileOpts { Method = 3, UnpackedOverride = 100 }).Build());
        a.CheckSupported(a.Entries[0]);
        Assert.Equal(ArchiveError.Corrupt, Assert.Throws<ArchiveException>(() => a.Extract(a.Entries[0], new MemorySink(), new ArchiveLimits())).Error);
    }

    [Fact]
    public void Newer_compression_versions_are_unsupported()
    {
        var a = Open(new Rar5Builder().AddFile("c", [1, 2, 3], new FileOpts { Method = 3, CompressionVersion = 1 }).Build());
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
    }

    [Fact]
    public void Encrypted_file_is_unsupported()
    {
        var a = Open(new Rar5Builder().AddFile("e", [1], new FileOpts { Extras = [Rar5Builder.EncryptionExtra()] }).Build());
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
    }

    [Fact]
    public void Entry_with_only_a_blake2_hash_is_unsupported_not_unverified()
    {
        var a = Open(new Rar5Builder().AddFile("h", [1], new FileOpts { OmitCrc = true, Extras = [Rar5Builder.HashExtra()] }).Build());
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
    }

    [Fact]
    public void Entry_of_unknown_size_is_unsupported()
    {
        var a = Open(new Rar5Builder().AddFile("u", [1], new FileOpts { UnknownSize = true }).Build());
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
    }

    [Fact]
    public void Volumes_encrypted_headers_and_unknown_mandatory_blocks_are_unsupported()
    {
        Assert.Equal(ArchiveError.Unsupported, OpenError(new Rar5Builder { ArchiveFlags = 0x01 | 0x02 }.AddFile("a", [1]).Build()));
        Assert.Equal(ArchiveError.Unsupported, OpenError(new Rar5Builder { OmitMain = true }.AddBlock(4, 0, new byte[40]).AddFile("a", [1]).Build()));
        Assert.Equal(ArchiveError.Unsupported, OpenError(new Rar5Builder().AddBlock(99, 0, [1]).AddFile("a", [1]).Build()));
    }

    [Fact]
    public void Data_continued_from_or_into_another_volume_is_unsupported()
    {
        foreach (var flag in new ulong[] { 0x08, 0x10 })
        {
            var rar = new Rar5Builder().AddBlock(2, flag, [], null).Build();
            Assert.Contains(OpenError(rar), new[] { ArchiveError.Unsupported, ArchiveError.Corrupt });
        }

        var cont = new Rar5Builder().AddFile("a", [1, 2, 3]).Build();
        // set "data continues in next volume" (0x10) on the file header and re-sign it
        var blockStart = Rar5Builder.Signature.Length + MainBlockLength(cont);
        var flagsPos = FlagsPosition(cont, blockStart);
        cont[flagsPos] |= 0x10;
        Rar5Builder.FixHeaderCrc(cont, blockStart);
        Assert.Equal(ArchiveError.Unsupported, OpenError(cont));
    }

    private static int MainBlockLength(byte[] rar)
    {
        var p = Rar5Builder.Signature.Length + 4;
        Assert.True(VInt.TryRead(rar.AsSpan(p), out var size, out var n));
        return 4 + n + (int)size;
    }

    private static int FlagsPosition(byte[] rar, int blockStart)
    {
        var p = blockStart + 4;
        Assert.True(VInt.TryRead(rar.AsSpan(p), out _, out var n1));
        p += n1;
        Assert.True(VInt.TryRead(rar.AsSpan(p), out _, out var n2));   // type
        return p + n2;
    }

    // ---- structural rejections ----
    [Fact]
    public void Rejects_short_or_missing_signature()
    {
        Assert.Throws<ArchiveException>(() => Open(new byte[4]));
        Assert.Throws<ArchiveException>(() => Open(new byte[100]));
    }

    [Fact]
    public void Rejects_missing_end_and_missing_main_header()
    {
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder { OmitEnd = true }.AddFile("a", [1]).Build()));
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder { OmitMain = true }.AddFile("a", [1]).Build()));
    }

    [Fact]
    public void Rejects_second_main_header()
    {
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddBlock(1, 0, Rar5Builder.VInt(0)).AddFile("a", [1]).Build()));
    }

    [Fact]
    public void Rejects_any_header_crc_mismatch()
    {
        var rar = Sample(out _, out _);
        var firstFile = Rar5Builder.Signature.Length + MainBlockLength(rar);
        rar[firstFile + 4 + 3] ^= 0x01;   // inside the header, CRC now stale
        Assert.Equal(ArchiveError.Corrupt, OpenError(rar));
    }

    [Fact]
    public void Rejects_header_size_zero_oversized_or_past_end()
    {
        var rar = new Rar5Builder().AddFile("a", [1]).Build();
        var p = Rar5Builder.Signature.Length + MainBlockLength(rar) + 4;   // header-size field of the file block

        var zero = (byte[])rar.Clone(); zero[p] = 0;
        Assert.Equal(ArchiveError.Corrupt, OpenError(zero));

        var huge = rar[..p].Concat(Rar5Builder.VInt(1UL << 40)).Concat(rar[(p + 1)..]).ToArray();
        Assert.Equal(ArchiveError.Corrupt, OpenError(huge));

        var over = rar[..p].Concat(Rar5Builder.VInt(2_000_000)).Concat(rar[(p + 1)..]).ToArray();   // within spec max, beyond the file
        Assert.Equal(ArchiveError.Corrupt, OpenError(over));
    }

    [Fact]
    public void Rejects_data_size_beyond_archive()
    {
        var rar = new Rar5Builder().AddFile("a", [1, 2, 3], new FileOpts { DataSizeField = 1UL << 40 }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(rar));
        var past = new Rar5Builder().AddFile("a", [1, 2, 3], new FileOpts { DataSizeField = 4 }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(past));   // claims one byte more than exists before the end block
    }

    [Fact]
    public void Rejects_invalid_utf8_names_and_malformed_extra_areas()
    {
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("x", [1], new FileOpts { NameBytes = [0x61, 0xFF, 0x62] }).Build()));
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("x", [1], new FileOpts { Extras = [[0x7F, 0x01]] }).Build()));   // record claims 127 bytes
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("x", [1], new FileOpts { Extras = [[0x00]] }).Build()));          // zero-size record
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("x", [1], new FileOpts { Extras = [Rar5Builder.Extra(3, [0x03])] }).Build()));  // time flags, no time
    }

    [Fact]
    public void Honours_entry_count_and_entry_size_limits()
    {
        var rar = new Rar5Builder().AddFile("a", [1]).AddFile("b", [1]).AddFile("c", [1]).Build();
        Assert.Equal(ArchiveError.LimitExceeded, OpenError(rar, new ArchiveLimits { MaxEntries = 2 }));

        var a = Open(new Rar5Builder().AddFile("f", new byte[100]).Build());
        var ex = Assert.Throws<ArchiveException>(() => a.Extract(a.Entries[0], new MemorySink(), new ArchiveLimits { MaxEntrySize = 99 }));
        Assert.Equal(ArchiveError.LimitExceeded, ex.Error);
    }

    [Fact]
    public void Extra_record_size_beyond_32_bits_is_rejected_not_truncated()
    {
        // size vint = 2^32 + 2 would wrap to 2 if narrowed to int before the range check
        byte[] huge = [.. Rar5Builder.VInt((1UL << 32) + 2), 0x09, 0x09];
        Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("x", [1], new FileOpts { Extras = [huge] }).Build()));
    }

    [Fact]
    public void Encryption_header_in_the_middle_is_unsupported_even_if_marked_skippable()
    {
        var rar = new Rar5Builder().AddFile("a", [1]).AddBlock(4, 0x04, new byte[20]).AddFile("b", [2]).Build();
        Assert.Equal(ArchiveError.Unsupported, OpenError(rar));
    }

    [Fact]
    public void Rar5_body_behind_a_different_signature_is_not_opened()
    {
        var rar = Sample(out _, out _);
        rar[6] = 0x00;                                    // RAR 4 signature byte, RAR 5 blocks
        Assert.Throws<ArchiveException>(() => Open(rar));
        rar[6] = 0x01; rar[0] = 0x00;
        Assert.Throws<ArchiveException>(() => Open(rar));
    }

    [Fact]
    public void Extracting_a_directory_that_claims_data_is_corrupt_but_a_clean_directory_is_a_no_op()
    {
        var bad = Open(new Rar5Builder().AddDirectory("d", new FileOpts { Attr = 0x41ED, UnpackedOverride = 5 }).Build());
        Assert.Equal(ArchiveError.Corrupt, Assert.Throws<ArchiveException>(() => bad.Extract(bad.Entries[0], new MemorySink(), new ArchiveLimits())).Error);

        var ok = Open(new Rar5Builder().AddDirectory("d").Build());
        var sink = new MemorySink();
        ok.Extract(ok.Entries[0], sink, new ArchiveLimits());
        Assert.Equal(0, sink.Writes);
    }

    [Fact]
    public void Encryption_checks_do_not_apply_to_directories()
    {
        var a = Open(new Rar5Builder().AddDirectory("d", new FileOpts { Attr = 0x41ED, OmitCrc = true }).Build());
        a.CheckSupported(a.Entries[0]);   // must not throw: directories carry no data or CRC
    }

    [Fact]
    public void Rejects_tampering_with_the_stored_header_crc_itself()
    {
        // Flipping the CRC field leaves the structure intact, so only the CRC check can notice.
        var rar = Small();
        var blockStarts = new List<int> { Rar5Builder.Signature.Length };
        blockStarts.Add(blockStarts[0] + MainBlockLength(rar));
        foreach (var start in blockStarts)
            for (var i = 0; i < 4; i++)
            {
                var copy = (byte[])rar.Clone();
                copy[start + i] ^= 0x01;
                Assert.Equal(ArchiveError.Corrupt, OpenError(copy));
            }

        var endStart = rar.Length - 8;                       // end block: crc(4) size(1) type(1) flags(1) endflags(1)
        var tail = (byte[])rar.Clone();
        tail[endStart] ^= 0x01;
        Assert.Equal(ArchiveError.Corrupt, OpenError(tail));
    }

    [Fact]
    public void Rejects_headers_larger_than_the_spec_maximum_even_when_they_fit_in_the_file()
    {
        var rar = new Rar5Builder().AddBlock(99, 0x04, new byte[2_100_000]).AddFile("a", [1]).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(rar));
        var ok = new Rar5Builder().AddBlock(99, 0x04, new byte[2_000_000]).AddFile("a", [1]).Build();
        Assert.Single(Open(ok).Entries);
    }

    [Fact]
    public void Rejects_extra_area_size_larger_than_the_header()
    {
        var rar = new Rar5Builder().AddFile("a", [1], new FileOpts { Extras = [Rar5Builder.UnixTimeExtra(1)], ExtraSizeField = 1000 }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(rar));
    }

    [Fact]
    public void Rejects_data_size_that_would_wrap_negative_when_narrowed()
    {
        foreach (var size in new[] { 1UL << 63, (1UL << 63) + 5, ulong.MaxValue })
            Assert.Equal(ArchiveError.Corrupt, OpenError(new Rar5Builder().AddFile("a", [1], new FileOpts { DataSizeField = size }).Build()));
    }

    [Fact]
    public void Rejects_name_lengths_that_wrap_or_overlap_the_extra_area()
    {
        var wraps = new Rar5Builder().AddFile("abc", [1], new FileOpts { NameLenField = (1UL << 32) + 3 }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(wraps));

        var overlaps = new Rar5Builder().AddFile("abc", [1], new FileOpts { Extras = [Rar5Builder.UnixTimeExtra(1)], NameLenField = 6 }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(overlaps));
    }

    [Fact]
    public void Rejects_a_data_size_that_wraps_the_parser_back_onto_the_same_block()
    {
        // size = 2^64 - blockLength: narrowed to long it is -blockLength, so "next block" == this block, forever.
        static byte[] Build(ulong dataSize) => new Rar5Builder().AddFile("a", [], new FileOpts { DataSizeField = dataSize }).Build();
        var probe = Build(1UL << 63);
        var main = Rar5Builder.Signature.Length + MainBlockLength(probe);
        var blockLength = probe.Length - main - 8;                           // minus the 8-byte end block
        var evil = Build(0UL - (ulong)blockLength);

        // The low entry limit makes a loop fail fast (LimitExceeded) instead of hanging the suite.
        Assert.Equal(ArchiveError.Corrupt, OpenError(evil, new ArchiveLimits { MaxEntries = 50 }));
    }

    [Fact]
    public void Rejects_an_extra_area_size_that_wraps_to_the_real_length_when_narrowed()
    {
        var extra = Rar5Builder.UnixTimeExtra(1_600_000_000);
        var rar = new Rar5Builder().AddFile("a", [1], new FileOpts { Extras = [extra], ExtraSizeField = (1UL << 32) + (ulong)extra.Length }).Build();
        Assert.Equal(ArchiveError.Corrupt, OpenError(rar));
    }

    [Fact]
    public void Directory_flag_wins_over_host_and_attributes()
    {
        var rar = new Rar5Builder()
            .AddDirectory("win", new FileOpts { Host = 0, Attr = 0x10 })
            .AddDirectory("unixfile", new FileOpts { Host = 1, Attr = 0x81A4 })
            .Build();
        Assert.All(Open(rar).Entries, e => Assert.Equal(EntryKind.Directory, e.Kind));
    }

    // ---- integrity ----
    [Fact]
    public void Detects_data_corruption_via_crc()
    {
        var rar = new Rar5Builder().AddFile("a", Rnd(200)).Build();
        var a = Open(rar);
        var (start, end) = a.GetSpan(a.Entries[0]);
        rar[(int)end - 100] ^= 0x01;                                   // inside the data
        Assert.Equal(ArchiveError.Corrupt, Assert.Throws<ArchiveException>(() => ExtractAll(Open(rar), 0)).Error);
        Assert.True(start < end);
    }

    [Fact]
    public void Detects_wrong_crc_field_and_size_mismatch()
    {
        var wrongCrc = Open(new Rar5Builder().AddFile("a", [1, 2, 3], new FileOpts { CrcOverride = 0xDEADBEEF }).Build());
        Assert.Throws<ArchiveException>(() => ExtractAll(wrongCrc, 0));

        var sizeLie = Open(new Rar5Builder().AddFile("a", [1, 2, 3], new FileOpts { UnpackedOverride = 4 }).Build());
        Assert.Throws<ArchiveException>(() => ExtractAll(sizeLie, 0));
    }

    // ---- through the whole pipeline ----
    private static readonly string Dest = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));

    [Fact]
    public void Pipeline_extracts_rar_tree_in_memory()
    {
        var rar = Sample(out var d1, out var d2);
        var fs = new InMemoryFileSystem(Dest);
        var r = ArchiveExtractor.Extract(rar, Dest, null, fs);
        Assert.Equal(d1, fs.Files[Path.Combine(Dest, "a.bin")]);
        Assert.Equal(d2, fs.Files[Path.Combine(Dest, "dir", "日本 b.bin")]);
        Assert.Equal(3, r.Files);
        Assert.Equal(d1.Length + d2.Length, fs.BytesWritten);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).LocalDateTime, fs.LastWrite[Path.Combine(Dest, "a.bin")]);
        Assert.Empty(fs.LeftoverTemps());
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\evil.txt")]
    [InlineData("CON")]
    [InlineData("a.txt:stream")]
    public void Pipeline_refuses_hostile_rar_names_without_touching_the_file_system(string evil)
    {
        var rar = new Rar5Builder().AddFile("good", [1]).AddFile(evil, [2]).Build();
        var fs = new InMemoryFileSystem(Dest);
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(rar, Dest, null, fs)).Error);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.CreatedDirs);
    }

    [Fact]
    public void Pipeline_skips_rar_links_and_special_files_and_never_creates_them()
    {
        var rar = new Rar5Builder()
            .AddFile("link", [], new FileOpts { Extras = [Rar5Builder.RedirectionExtra(1, "/etc/shadow")] })
            .AddFile("sym", [], new FileOpts { Attr = 0xA1FF })
            .AddFile("fifo", [], new FileOpts { Attr = 0x11A4 })
            .AddFile("real", [5]).Build();
        var fs = new InMemoryFileSystem(Dest);
        var r = ArchiveExtractor.Extract(rar, Dest, null, fs);
        Assert.Equal(3, r.Skipped.Count);
        Assert.Equal([Path.Combine(Dest, "real")], fs.Files.Keys.Where(k => !k.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pipeline_rejects_duplicate_and_case_colliding_rar_names()
    {
        var rar = new Rar5Builder().AddFile("Readme", [1]).AddFile("README", [2]).Build();
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            ArchiveExtractor.Extract(rar, Dest, null, new InMemoryFileSystem(Dest))).Error);
    }

    [Fact]
    public void Pipeline_rejects_unsupported_entry_before_writing_anything()
    {
        var rar = new Rar5Builder().AddFile("ok", [1]).AddFile("exotic", [1, 2], new FileOpts { Method = 6 }).Build();
        var fs = new InMemoryFileSystem(Dest);
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(rar, Dest, null, fs)).Error);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public void Directory_entry_that_claims_data_is_rejected_by_the_planner()
    {
        var bad = new Rar5Builder().AddDirectory("d", new FileOpts { Attr = 0x41ED, UnpackedOverride = 5 }).Build();
        Assert.Equal(ArchiveError.Corrupt, Assert.Throws<ArchiveException>(() =>
            ArchiveExtractor.Extract(bad, Dest, null, new InMemoryFileSystem(Dest))).Error);
    }

    // ---- robustness sweeps ----
    private static void TryAll(byte[] bytes)
    {
        try
        {
            var a = ArchiveReader.Open(bytes);
            foreach (var e in a.Entries.Where(e => e.Kind == EntryKind.File))
            {
                a.CheckSupported(e);
                a.Extract(e, new MemorySink(), new ArchiveLimits { MaxEntrySize = 1 << 20 });
            }
        }
        catch (ArchiveException) { }
    }

    private static byte[] Small() => new Rar5Builder().AddFile("a", Rnd(40)).AddDirectory("d").AddFile("d/b", Rnd(60, 3), new FileOpts { Extras = [Rar5Builder.UnixTimeExtra(1_650_000_000)] }).Build();

    [Fact]
    public void Every_truncation_is_handled()
    {
        var rar = Small();
        for (var len = 0; len < rar.Length; len++) TryAll(rar.AsSpan(0, len).ToArray());
    }

    [Fact]
    public void Every_byte_corruption_is_handled()
    {
        var original = Small();
        var rar = (byte[])original.Clone();
        for (var i = 0; i < rar.Length; i++)
            foreach (var mask in new byte[] { 0x01, 0x40, 0x80, 0xFF })
            {
                rar[i] ^= mask;
                TryAll(rar);
                rar[i] = original[i];
            }
    }

    [Fact]
    public void Corrupted_headers_with_valid_crcs_are_handled()
    {
        // The CRC only protects against accidents. An attacker re-signs, so fuzz the fields with fixed-up CRCs.
        var original = Small();
        var rar = (byte[])original.Clone();
        var starts = new List<int>();
        var p = Rar5Builder.Signature.Length;
        while (p < rar.Length)
        {
            if (!VInt.TryRead(rar.AsSpan(p + 4), out var size, out var n)) break;
            starts.Add(p);
            var hdrEnd = p + 4 + n + (int)size;
            // find data size for stepping: parse type/flags/extra/data quickly
            var q = p + 4 + n;
            VInt.TryRead(rar.AsSpan(q), out _, out var tn); q += tn;
            VInt.TryRead(rar.AsSpan(q), out var flags, out var fn); q += fn;
            ulong extra = 0, data = 0;
            if ((flags & 1) != 0) { VInt.TryRead(rar.AsSpan(q), out extra, out var en); q += en; }
            if ((flags & 2) != 0) { VInt.TryRead(rar.AsSpan(q), out data, out _); }
            p = hdrEnd + (int)data;
        }

        foreach (var start in starts)
        {
            VInt.TryRead(rar.AsSpan(start + 4), out var size, out var n);
            for (var off = 4 + n; off < 4 + n + (int)size; off++)
                foreach (var mask in new byte[] { 0x01, 0x80, 0xFF, 0x7F })
                {
                    rar[start + off] ^= mask;
                    Rar5Builder.FixHeaderCrc(rar, start);
                    TryAll(rar);
                    rar[start + off] = original[start + off];
                }

            Rar5Builder.FixHeaderCrc(rar, start);
        }
    }
}
