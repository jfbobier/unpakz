using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unpakz.Core;
using Unpakz.Core.Rar;

namespace Unpakz.Tests;

public class PackedStreamTests
{
    private static byte[] Seq(int n, int start = 0) => [.. Enumerable.Range(start, n).Select(i => (byte)i)];

    [Fact]
    public void Reads_and_takes_within_one_segment_without_copying()
    {
        var data = Seq(100);
        var s = new PackedStream(data);
        Assert.Equal(100, s.Remaining);

        var buf = new byte[10];
        s.ReadExactly(buf);
        Assert.Equal(Seq(10), buf);
        Assert.Equal(90, s.Remaining);

        var taken = s.Take(20);
        Assert.Equal(Seq(20, 10), taken.ToArray());
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(taken.Span), ref data[10]), "Take must return a slice of the archive, not a copy");
        Assert.Equal(70, s.Remaining);
    }

    [Fact]
    public void Take_and_read_across_segment_boundaries_see_one_continuous_stream()
    {
        var all = Seq(90);
        var s = new PackedStream([all.AsMemory(0, 30), all.AsMemory(30, 0), all.AsMemory(30, 25), all.AsMemory(55, 35)]);
        Assert.Equal(90, s.Remaining);

        Assert.Equal(all[..20], s.Take(20).ToArray());             // inside the first segment
        var straddle = s.Take(30);                                  // 10 from the first, 20 from the third
        Assert.Equal(all[20..50], straddle.ToArray());
        var buf = new byte[25];
        s.ReadExactly(buf);                                         // 5 from the third, 20 from the fourth
        Assert.Equal(all[50..75], buf);
        Assert.Equal(15, s.Remaining);
        Assert.Equal(all[75..], s.Take(15).ToArray());
        Assert.Equal(0, s.Remaining);
    }

    [Fact]
    public void A_straddling_take_is_a_merged_copy_and_a_contained_take_is_not()
    {
        var all = Seq(40);
        var s = new PackedStream([all.AsMemory(0, 20), all.AsMemory(20, 20)]);
        var inside = s.Take(5);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(inside.Span), ref all[0]));
        var straddle = s.Take(30);
        Assert.False(Unsafe.AreSame(ref MemoryMarshal.GetReference(straddle.Span), ref all[5]));
        Assert.Equal(all[5..35], straddle.ToArray());
    }

    [Fact]
    public void A_take_that_exactly_fills_the_rest_of_a_segment_is_still_a_slice()
    {
        var all = Seq(40);
        var s = new PackedStream([all.AsMemory(0, 20), all.AsMemory(20, 20)]);
        var exact = s.Take(20);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(exact.Span), ref all[0]));
        var next = s.Take(20);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(next.Span), ref all[20]));
    }

    [Fact]
    public void Zero_length_reads_and_takes_are_harmless()
    {
        var s = new PackedStream(Seq(5));
        Assert.True(s.Take(0).IsEmpty);
        s.ReadExactly([]);
        Assert.Equal(5, s.Remaining);
        var empty = new PackedStream([]);
        Assert.Equal(0, empty.Remaining);
        Assert.True(empty.Take(0).IsEmpty);
    }

    [Fact]
    public void Over_reading_is_an_archive_error_and_consumes_nothing()
    {
        var s = new PackedStream(Seq(10));
        Assert.Throws<ArchiveException>(() => s.Take(11));
        Assert.Throws<ArchiveException>(() => s.Take(-1));
        Assert.Throws<ArchiveException>(() => s.ReadExactly(new byte[11]));
        Assert.Equal(10, s.Remaining);
        Assert.Equal(Seq(10), s.Take(10).ToArray());
        Assert.Throws<ArchiveException>(() => s.Take(1));
    }

    [Fact]
    public void Segments_made_only_of_empty_slices_are_skipped_cleanly()
    {
        var s = new PackedStream([ReadOnlyMemory<byte>.Empty, new byte[] { 1, 2, 3 }, ReadOnlyMemory<byte>.Empty, new byte[] { 4 }]);
        Assert.Equal(4, s.Remaining);
        Assert.Equal([1, 2, 3, 4], s.Take(4).ToArray());
    }
}
