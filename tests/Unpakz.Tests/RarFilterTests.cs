using Unpakz.Core.Rar;

namespace Unpakz.Tests;

public class RarFilterTests
{
    private static byte[] Run(Action<byte[], byte[]> filter, byte[] src)
    {
        var dst = new byte[src.Length];
        filter(src, dst);
        return dst;
    }

    // ---- delta ----
    [Fact]
    public void Delta_hand_computed_vector() =>
        Assert.Equal([255, 252, 253, 247, 250, 241], Run((s, d) => RarFilters.Delta(s, 2, d), [1, 2, 3, 4, 5, 6]));

    [Fact]
    public void Delta_single_channel_is_a_running_negative_sum() =>
        Assert.Equal([255, 253, 250], Run((s, d) => RarFilters.Delta(s, 1, d), [1, 2, 3]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(31)]
    public void Delta_round_trips_an_independent_encoder(int channels)
    {
        // Encoder: planes are emitted channel by channel, each as differences from the previous sample of that channel.
        var rng = new Random(channels);
        foreach (var length in new[] { 4, 5, 31, 32, 33, 1000, 4096 })
        {
            var original = new byte[length];
            rng.NextBytes(original);
            var encoded = new List<byte>();
            for (var c = 0; c < channels; c++)
            {
                byte previous = 0;
                for (var i = c; i < length; i += channels)
                {
                    encoded.Add((byte)(previous - original[i]));
                    previous = original[i];
                }
            }

            Assert.Equal(original, Run((s, d) => RarFilters.Delta(s, channels, d), [.. encoded]));
        }
    }

    // ---- x86 ----
    [Fact]
    public void E8_converts_absolute_call_target_to_relative()
    {
        // block: E8 + abs 100 at stream position 0 -> offset = 1 (position after the opcode); relative = 100 - 1 = 99
        var src = new byte[] { 0xE8, 100, 0, 0, 0, 0x90, 0x90 };
        Assert.Equal([0xE8, 99, 0, 0, 0, 0x90, 0x90], Run((s, d) => RarFilters.E8(s, 0, false, d), src));
    }

    [Fact]
    public void E8_leaves_targets_outside_the_converted_ranges_alone()
    {
        // addr >= 2^24 (positive, out of range) and a negative addr whose sum stays negative: unchanged
        var big = new byte[] { 0xE8, 0x00, 0x00, 0x00, 0x01, 0x90, 0x90 };
        Assert.Equal(big, Run((s, d) => RarFilters.E8(s, 0, false, d), big));
        var neg = new byte[] { 0xE8, 0x00, 0xFF, 0xFF, 0xFF, 0x90, 0x90 };
        Assert.Equal(neg, Run((s, d) => RarFilters.E8(s, 0, false, d), neg));
    }

    [Fact]
    public void E8_wraps_negative_addresses_that_become_non_negative()
    {
        // addr = 0xFFFFFFFF, offset = 1 -> addr + offset wraps to 0 (non-negative) -> written as addr + 2^24 = 0x00FFFFFF
        var src = new byte[] { 0xE8, 0xFF, 0xFF, 0xFF, 0xFF, 0x90, 0x90 };
        Assert.Equal([0xE8, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x90], Run((s, d) => RarFilters.E8(s, 0, false, d), src));
    }

    [Fact]
    public void E9_is_only_converted_in_extended_mode()
    {
        var src = new byte[] { 0xE9, 100, 0, 0, 0, 0x90, 0x90 };
        Assert.Equal(src, Run((s, d) => RarFilters.E8(s, 0, false, d), src));
        Assert.Equal([0xE9, 99, 0, 0, 0, 0x90, 0x90], Run((s, d) => RarFilters.E8(s, 0, true, d), src));
    }

    [Fact]
    public void E8_offset_uses_the_file_relative_position_modulo_16_mib()
    {
        var src = new byte[] { 0x90, 0xE8, 200, 0, 0, 0, 0x90, 0x90 };        // opcode at 1 -> i becomes 2
        Assert.Equal([0x90, 0xE8, 198, 0, 0, 0, 0x90, 0x90], Run((s, d) => RarFilters.E8(s, 0, false, d), src));
        Assert.Equal([0x90, 0xE8, 0xDE, 0xFC, 0xFF, 0xFF, 0x90, 0x90],
            Run((s, d) => RarFilters.E8(s, 1000, false, d), src));            // offset = 2 + 1000: 200 - 1002 = -802 = 0xFFFFFCDE
        Assert.Equal([0x90, 0xE8, 198, 0, 0, 0, 0x90, 0x90], Run((s, d) => RarFilters.E8(s, 0x1000000, false, d), src));   // modulo 2^24
    }

    [Fact]
    public void E8_stops_four_bytes_before_the_end_and_skips_operands()
    {
        // An E8 inside the operand of a previous E8 is data, not an opcode.
        var src = new byte[] { 0xE8, 0xE8, 10, 0, 0, 0x90, 0x90 };
        var expected = (byte[])src.Clone();
        var result = Run((s, d) => RarFilters.E8(s, 0, false, d), src);
        // operand = E8 0A 00 00 = 0x00000AE8 (positive, < 2^24): converted to 0xAE8 - 1
        expected[1] = 0xE7; expected[2] = 0x0A;
        Assert.Equal(expected, result);

        var tail = new byte[] { 0x90, 0x90, 0x90, 0xE8, 1, 0, 0 };           // opcode within the last 4 bytes: ignored
        Assert.Equal(tail, Run((s, d) => RarFilters.E8(s, 0, false, d), tail));
    }

    // ---- ARM ----
    [Fact]
    public void Arm_converts_absolute_branch_targets_to_relative()
    {
        // BL with absolute word target 0x10 at relative position 400 (instruction index 100): 0x10 - 100
        var src = new byte[] { 0x10, 0x00, 0x00, 0xEB };
        var expected = BitConverter.GetBytes((unchecked(0x10u - 100u) & 0x00FFFFFFu) | 0xEB000000u);
        Assert.Equal(expected, Run((s, d) => RarFilters.Arm(s, 400, d), src));
        Assert.Equal(src, Run((s, d) => RarFilters.Arm(s, 0, d), src));      // position 0: nothing to subtract
    }

    [Fact]
    public void Arm_ignores_other_instructions_and_unaligned_tail()
    {
        var src = new byte[] { 1, 2, 3, 0xEA, 0x10, 0, 0, 0xEB, 9, 9 };      // 0xEA is B, not BL; the last two bytes are a partial word
        var result = Run((s, d) => RarFilters.Arm(s, 8, d), src);
        Assert.Equal(src[..4], result[..4]);
        Assert.Equal(src[8..], result[8..]);
        Assert.Equal(BitConverter.GetBytes(((0x10u - 3u) & 0x00FFFFFFu) | 0xEB000000u), result[4..8]);          // (4 + 8) / 4 = 3
    }

    // ---- shared properties ----
    [Fact]
    public void Filters_never_change_length_or_throw_on_random_blocks()
    {
        var rng = new Random(77);
        for (var i = 0; i < 2000; i++)
        {
            var src = new byte[rng.Next(4, 300)];
            rng.NextBytes(src);
            var start = rng.NextInt64(0, 1L << 40);
            Assert.Equal(src.Length, Run((s, d) => RarFilters.E8(s, start, i % 2 == 0, d), src).Length);
            Assert.Equal(src.Length, Run((s, d) => RarFilters.Arm(s, start, d), src).Length);
            Assert.Equal(src.Length, Run((s, d) => RarFilters.Delta(s, 1 + i % 31, d), src).Length);
        }
    }
}
