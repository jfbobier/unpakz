using Unpakz.Core.Checksums;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class Crc32Tests
{
    [Fact]
    public void Known_check_value() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Empty_input_is_zero() => Assert.Equal(0u, Crc32.Compute([]));

    [Fact]
    public void Matches_independent_reference_for_all_small_lengths()
    {
        var rng = new Random(1);
        for (var len = 0; len <= 600; len++)
        {
            var data = new byte[len];
            rng.NextBytes(data);
            Assert.Equal(ZipBuilder.Crc32(data), Crc32.Compute(data));
        }
    }

    [Fact]
    public void Matches_reference_on_large_unaligned_buffer()
    {
        var data = new byte[3_000_017];
        new Random(2).NextBytes(data);
        var slice = data.AsSpan(3, data.Length - 5);   // unaligned start and tail
        Assert.Equal(ZipBuilder.Crc32(slice), Crc32.Compute(slice));
    }

    [Fact]
    public void Incremental_updates_equal_one_shot_for_any_split()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        var whole = Crc32.Compute(data);
        for (var split = 0; split <= data.Length; split += 7)
        {
            var c = Crc32.Update(0, data.AsSpan(0, split));
            c = Crc32.Update(c, data.AsSpan(split));
            Assert.Equal(whole, c);
        }
    }

    [Fact]
    public void Single_bit_flip_always_changes_crc()
    {
        var data = new byte[64];
        new Random(4).NextBytes(data);
        var baseline = Crc32.Compute(data);
        for (var i = 0; i < data.Length * 8; i++)
        {
            data[i / 8] ^= (byte)(1 << (i % 8));
            Assert.NotEqual(baseline, Crc32.Compute(data));
            data[i / 8] ^= (byte)(1 << (i % 8));
        }
    }
}
