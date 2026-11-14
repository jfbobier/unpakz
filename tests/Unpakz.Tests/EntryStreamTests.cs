using Unpakz.Core;
using Unpakz.Core.Rar;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class EntryStreamTests
{
    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Txt(int n) { var b = new byte[n]; for (var i = 0; i < n; i++) b[i] = (byte)"hello "[i % 6]; return b; }

    private static byte[] ReadAll(Stream s, int chunk = 4096)
    {
        using var ms = new MemoryStream();
        var buf = new byte[chunk];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    private static ArchiveEntry Entry(IArchive a, string name) => a.Entries.Single(e => e.Name == name);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    [InlineData(1 << 20)]
    public void Delivers_the_exact_bytes_whatever_the_read_size(int chunk)
    {
        var data = Txt(3_000_000);
        var a = ZipArchive.Open(new ZipBuilder().Add("f", data, deflate: true).Build());
        using var s = EntryStream.Open(a, Entry(a, "f"));
        if (chunk == 1) { Assert.Equal(data[..2000], ReadAtMost(s, 2000)); return; }
        Assert.True(data.AsSpan().SequenceEqual(ReadAll(s, chunk)));
    }

    private static byte[] ReadAtMost(Stream s, int count)
    {
        var b = new byte[count];
        for (var i = 0; i < count; i++) Assert.Equal(1, s.Read(b, i, 1));
        return b;
    }

    [Fact]
    public void Works_for_stored_deflated_and_rar_entries()
    {
        var stored = Rnd(100_000, 2);
        var zip = ZipArchive.Open(new ZipBuilder().Add("s", stored).Add("d", Txt(50_000), deflate: true).Build());
        using (var s = EntryStream.Open(zip, Entry(zip, "s"))) Assert.Equal(stored, ReadAll(s));
        using (var s = EntryStream.Open(zip, Entry(zip, "d"))) Assert.Equal(Txt(50_000), ReadAll(s));

        var rar = Rar5Archive.Open(new Rar5Builder().AddFile("r", stored).Build(), new ArchiveLimits());
        using (var s = EntryStream.Open(rar, Entry(rar, "r"))) Assert.Equal(stored, ReadAll(s));

        var compressed = ArchiveReader.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rar5-m3-mix.rar")));
        using (var s = EntryStream.Open(compressed, Entry(compressed, "text.txt"))) Assert.Equal(TestData.Text(8000), ReadAll(s));
    }

    [Fact]
    public void Empty_entries_and_directories_read_as_empty()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("e", []).AddDirectory("d").Build());
        using var empty = EntryStream.Open(a, Entry(a, "e"));
        Assert.Equal(0, empty.Read(new byte[10], 0, 10));
        using var dir = EntryStream.Open(a, Entry(a, "d/"));
        Assert.Equal(0, dir.Read(new byte[10], 0, 10));
    }

    [Fact]
    public void Memory_use_is_bounded_for_a_huge_entry_read_slowly()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("big", Rnd(40_000_000, 3)).Build());
        using var s = EntryStream.Open(a, Entry(a, "big"), queueCapacity: 4);
        var buf = new byte[65_536];
        long total = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total == 5_000_000) Thread.Sleep(150);                   // let the producer run ahead as far as it is allowed
        }

        Assert.Equal(40_000_000, total);
        Assert.True(s.PeakBufferedBytes <= 6L * 1024 * 1024, $"buffered {s.PeakBufferedBytes} bytes, expected a small bounded amount");
    }

    [Fact]
    public void Disposing_early_stops_the_producer_and_leaves_the_archive_usable()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("big", Rnd(30_000_000, 4)).Add("small", [1, 2, 3]).Build());
        var s = EntryStream.Open(a, Entry(a, "big"), queueCapacity: 2);
        Assert.Equal(10, s.Read(new byte[10], 0, 10));
        s.Dispose();
        Assert.False(s.ProducerRunning);

        using var again = EntryStream.Open(a, Entry(a, "small"));
        Assert.Equal([1, 2, 3], ReadAll(again));
    }

    [Fact]
    public void Disposing_without_reading_and_disposing_twice_are_harmless()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("f", Rnd(5_000_000, 5)).Build());
        var s = EntryStream.Open(a, Entry(a, "f"));
        s.Dispose();
        s.Dispose();
        Assert.False(s.ProducerRunning);
        Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Damaged_data_is_delivered_up_to_the_failure_then_reported_as_an_archive_error()
    {
        var zip = new ZipBuilder().Add("f", Rnd(300_000, 6)).Build();
        zip[30 + 1 + 200_000] ^= 0x01;                                    // flip a bit deep inside the data: only the CRC can notice
        var a = ZipArchive.Open(zip);
        using var s = EntryStream.Open(a, Entry(a, "f"));
        var ex = Assert.Throws<ArchiveException>(() => ReadAll(s));
        Assert.Equal(ArchiveError.Corrupt, ex.Error);
        Assert.Throws<ArchiveException>(() => s.Read(new byte[1], 0, 1));   // the error is sticky, not a silent end of stream
    }

    [Fact]
    public void Cancellation_surfaces_as_operation_canceled()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("big", Rnd(20_000_000, 7)).Build());
        using var cts = new CancellationTokenSource();
        using var s = EntryStream.Open(a, Entry(a, "big"), cancellationToken: cts.Token);
        Assert.Equal(100, s.Read(new byte[100], 0, 100));
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ReadAll(s));
    }

    [Fact]
    public void It_is_a_forward_only_readable_stream_that_knows_the_expected_length()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("f", Rnd(1234, 8)).Build());
        using var s = EntryStream.Open(a, Entry(a, "f"));
        Assert.True(s.CanRead);
        Assert.False(s.CanSeek);
        Assert.False(s.CanWrite);
        Assert.Equal(1234L, s.ExpectedLength);
        Assert.Throws<NotSupportedException>(() => _ = s.Length);
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
    }

    [Fact]
    public async Task Async_reads_work()
    {
        var data = Rnd(200_000, 9);
        var a = ZipArchive.Open(new ZipBuilder().Add("f", data).Build());
        await using var s = EntryStream.Open(a, Entry(a, "f"));
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task Two_streams_over_one_archive_can_be_read_in_parallel()
    {
        var x = Rnd(2_000_000, 10); var y = Txt(1_500_000);
        var a = ZipArchive.Open(new ZipBuilder().Add("x", x).Add("y", y, deflate: true).Build());
        using var sx = EntryStream.Open(a, Entry(a, "x"));
        using var sy = EntryStream.Open(a, Entry(a, "y"));
        var tx = Task.Run(() => ReadAll(sx));
        var ty = Task.Run(() => ReadAll(sy));
        Assert.Equal(x, await tx);
        Assert.Equal(y, await ty);
    }

    [Fact]
    public void Small_solid_entries_can_be_opened_together_and_read_in_either_order()
    {
        var archive = ArchiveReader.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rar5-m5-solid.rar")));
        var files = archive.Entries.Where(e => e.Kind == EntryKind.File && e.UncompressedSize > 0).ToArray();
        var streams = files.Select(e => EntryStream.Open(archive, e)).ToArray();
        try
        {
            foreach (var i in Enumerable.Range(0, streams.Length).Reverse())
            {
                Assert.Equal((int)files[i].UncompressedSize, ReadAll(streams[i]).Length);
            }
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    [Fact]
    public void Opening_validates_arguments()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("f", [1]).Build());
        Assert.Throws<ArgumentNullException>(() => EntryStream.Open(null!, a.Entries[0]));
        Assert.Throws<ArgumentNullException>(() => EntryStream.Open(a, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntryStream.Open(a, a.Entries[0], queueCapacity: 0));
    }
}
