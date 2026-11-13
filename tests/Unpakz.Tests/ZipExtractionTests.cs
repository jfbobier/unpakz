using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

internal sealed class MemorySink : IOutputSink
{
    private readonly System.Buffers.ArrayBufferWriter<byte> _buf = new();
    public int Writes { get; private set; }
    public long Length => _buf.WrittenCount;
    public byte[] ToArray() => _buf.WrittenSpan.ToArray();
    public void Write(ReadOnlySpan<byte> data) { System.Buffers.BuffersExtensions.Write(_buf, data); Writes++; }
}

public class ZipExtractionTests
{
    private static byte[] Random(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Compressible(int n) { var b = new byte[n]; for (var i = 0; i < n; i++) b[i] = (byte)(i % 13 < 9 ? 'a' : 'b' + i % 7); return b; }

    private static byte[] Extract(byte[] zip, int index = 0)
    {
        var entry = ZipCentralDirectory.Read(zip)[index];
        var sink = new MemorySink();
        ZipEntryExtractor.Extract(zip, entry, sink);
        return sink.ToArray();
    }

    private static int CdOf(byte[] zip) => (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));

    public static TheoryData<int, bool, string> Cases() => new()
    {
        { 0, false, "empty stored" }, { 0, true, "empty deflate" },
        { 1, false, "1B stored" }, { 1, true, "1B deflate" },
        { 100_000, false, "100KB random stored" }, { 100_000, true, "100KB random deflate" },
        { 5_000_000, false, "5MB stored" }, { 5_000_000, true, "5MB compressible deflate" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Round_trips(int size, bool deflate, string label)
    {
        var data = deflate && size > 1000 && label.Contains("compressible", StringComparison.Ordinal) ? Compressible(size) : Random(size);
        var zip = new ZipBuilder().Add("f", data, deflate).Build();
        Assert.Equal(data, Extract(zip));
    }

    [Fact]
    public void Extracts_each_entry_independently()
    {
        var a = Random(500, 1); var b = Compressible(70_000); var c = Random(3000, 3);
        var zip = new ZipBuilder().Add("a", a).Add("b", b, deflate: true).Add("c", c, deflate: true).Build();
        Assert.Equal(a, Extract(zip, 0));
        Assert.Equal(b, Extract(zip, 1));
        Assert.Equal(c, Extract(zip, 2));
    }

    [Fact]
    public void Directory_entry_extracts_nothing()
    {
        var zip = new ZipBuilder().AddDirectory("d").Build();
        Assert.Empty(Extract(zip));
    }

    [Fact]
    public void Large_stored_entry_is_written_in_bounded_chunks()
    {
        var zip = new ZipBuilder().Add("f", Random(5_000_000)).Build();
        var entry = ZipCentralDirectory.Read(zip)[0];
        var max = 0;
        ZipEntryExtractor.Extract(zip, entry, new CallbackSink(n => max = Math.Max(max, n)));
        Assert.InRange(max, 1, 4 * 1024 * 1024);
    }

    private sealed class CallbackSink(Action<int> onWrite) : IOutputSink
    {
        public void Write(ReadOnlySpan<byte> data) => onWrite(data.Length);
    }

    // ---- integrity ----
    [Fact]
    public void Detects_crc_mismatch_in_stored_data()
    {
        var zip = new ZipBuilder().Add("f", Random(200)).Build();
        zip[30 + 1 + 50] ^= 0x01;   // flip a data bit (local header 30 + name 1)
        var ex = Assert.Throws<ArchiveException>(() => Extract(zip));
        Assert.Equal(ArchiveError.Corrupt, ex.Error);
    }

    [Fact]
    public void Detects_corrupt_deflate_stream()
    {
        var zip = new ZipBuilder().Add("f", Compressible(50_000), deflate: true).Build();
        for (var i = 0; i < 40; i++) zip[30 + 1 + i] = 0xFF;
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Fact]
    public void Rejects_deflate_output_longer_than_declared()
    {
        var zip = new ZipBuilder().Add("f", new byte[1000], deflate: true).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CdOf(zip) + 24), 999);
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Fact]
    public void Rejects_deflate_output_shorter_than_declared()
    {
        var zip = new ZipBuilder().Add("f", new byte[1000], deflate: true).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CdOf(zip) + 24), 1001);
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Fact]
    public void Rejects_declared_size_beyond_deflate_maximum_ratio_before_decoding()
    {
        var zip = new ZipBuilder().Add("f", new byte[1000], deflate: true).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CdOf(zip) + 24), 0x7FFFFFFF);
        var entry = ZipCentralDirectory.Read(zip)[0];
        var sink = new MemorySink();
        Assert.Throws<ArchiveException>(() => ZipEntryExtractor.Extract(zip, entry, sink));
        Assert.Equal(0, sink.Writes);
    }

    [Fact]
    public void Honours_configured_entry_size_limit()
    {
        var zip = new ZipBuilder().Add("f", new byte[10_000], deflate: true).Build();
        var entry = ZipCentralDirectory.Read(zip)[0];
        var ex = Assert.Throws<ArchiveException>(() =>
            ZipEntryExtractor.Extract(zip, entry, new MemorySink(), new ArchiveLimits { MaxEntrySize = 9_999 }));
        Assert.Equal(ArchiveError.LimitExceeded, ex.Error);
    }

    [Fact]
    public void Bomb_with_small_declared_size_never_writes_more_than_declared()
    {
        // 20 MB of zeros deflates to ~20 KB; declare only 20_000 bytes (within the 1032:1 ratio bound).
        var zip = new ZipBuilder().Add("f", new byte[20_000_000], deflate: true).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CdOf(zip) + 24), 20_000);
        var entry = ZipCentralDirectory.Read(zip)[0];
        var sink = new MemorySink();
        Assert.Throws<ArchiveException>(() => ZipEntryExtractor.Extract(zip, entry, sink));
        Assert.True(sink.Length <= 20_000, $"sink received {sink.Length} bytes");
    }

    [Fact]
    public void Directory_entry_declaring_data_is_rejected()
    {
        var zip = new ZipBuilder().AddDirectory("d").Build();
        var cd = CdOf(zip);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 20), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 24), 5);
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(29)]
    public void Entry_from_full_archive_applied_to_truncated_bytes_is_rejected(int keep)
    {
        var zip = new ZipBuilder().Add("f", Random(100)).Add("g", Random(100)).Build();
        var entry = ZipCentralDirectory.Read(zip)[1];   // local header is well past the truncation point
        Assert.Throws<ArchiveException>(() => ZipEntryExtractor.Extract(zip.AsMemory(0, keep), entry, new MemorySink()));
    }

    // ---- structure ----
    [Fact]
    public void Rejects_bad_local_header_signature()
    {
        var zip = new ZipBuilder().Add("f", Random(10)).Build();
        zip[0] ^= 0xFF;
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Fact]
    public void Rejects_data_extending_past_archive()
    {
        var zip = new ZipBuilder().Add("f", Random(10)).Build();
        var cd = CdOf(zip);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 20), 0x7FFFFF00);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 24), 0x7FFFFF00);
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Fact]
    public void Rejects_local_name_length_that_pushes_data_past_archive()
    {
        var zip = new ZipBuilder().Add("f", Random(10)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(26), 65535);
        Assert.Throws<ArchiveException>(() => Extract(zip));
    }

    [Theory]
    [InlineData(99)]   // AES
    [InlineData(1)]    // shrink
    [InlineData(12)]   // bzip2
    [InlineData(14)]   // LZMA
    public void Unsupported_methods_report_unsupported(int method)
    {
        var zip = new ZipBuilder().Add("f", Random(10)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(CdOf(zip) + 10), (ushort)method);
        var ex = Assert.Throws<ArchiveException>(() => Extract(zip));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
    }

    [Fact]
    public void Encrypted_entries_report_unsupported()
    {
        var zip = new ZipBuilder().Add("f", Random(10)).Build();
        var cd = CdOf(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 8), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cd + 8)) | 1));
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => Extract(zip)).Error);
    }

    // ---- robustness sweeps ----
    private static void TryReadAndExtractAll(ReadOnlySpan<byte> span)
    {
        var copy = span.ToArray();
        try
        {
            foreach (var e in ZipCentralDirectory.Read(copy))
                ZipEntryExtractor.Extract(copy, e, new MemorySink(), new ArchiveLimits { MaxEntrySize = 1 << 20 });
        }
        catch (ArchiveException) { /* the only allowed failure */ }
    }

    private static byte[] Mixed() => new ZipBuilder()
        .Add("a", Random(300)).Add("d/", []).Add("b", Compressible(2000), deflate: true).Build();

    [Fact]
    public void Every_truncation_is_handled_through_extraction()
    {
        var zip = Mixed();
        for (var len = 0; len < zip.Length; len++) TryReadAndExtractAll(zip.AsSpan(0, len));
    }

    [Fact]
    public void Every_byte_corruption_is_handled_through_extraction()
    {
        var original = Mixed();
        var zip = (byte[])original.Clone();
        for (var i = 0; i < zip.Length; i++)
            foreach (var mask in new byte[] { 0x01, 0x10, 0xFF })
            {
                zip[i] ^= mask;
                TryReadAndExtractAll(zip);
                zip[i] = original[i];
            }
    }
}
