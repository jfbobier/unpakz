using Unpakz.Core.Zip;

namespace Unpakz.Tests;

public class DosTimeTests
{
    private static uint Pack(int y, int mo, int d, int h, int mi, int s) =>
        (uint)((((y - 1980) << 9) | (mo << 5) | d) << 16 | (h << 11) | (mi << 5) | (s / 2));

    [Fact]
    public void Converts_valid_values() =>
        Assert.True(DosTime.TryConvert(Pack(2024, 1, 1, 12, 0, 0), out var t) && t == new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local));

    [Fact]
    public void Handles_extremes_and_leap_day()
    {
        Assert.True(DosTime.TryConvert(Pack(1980, 1, 1, 0, 0, 0), out var a) && a.Year == 1980);
        Assert.True(DosTime.TryConvert(Pack(2107, 12, 31, 23, 59, 58), out var b) && b.Year == 2107 && b.Second == 58);
        Assert.True(DosTime.TryConvert(Pack(1980, 2, 29, 0, 0, 0), out _));
        Assert.True(DosTime.TryConvert(Pack(2000, 2, 29, 0, 0, 0), out _));
    }

    [Theory]
    [InlineData(0u)]                       // date 0 means "unset"
    public void Rejects_unset(uint v) => Assert.False(DosTime.TryConvert(v, out _));

    [Theory]
    [InlineData(2024, 0, 1, 0, 0, 0)]
    [InlineData(2024, 13, 1, 0, 0, 0)]
    [InlineData(2024, 1, 0, 0, 0, 0)]
    [InlineData(2024, 1, 32, 0, 0, 0)]
    [InlineData(2024, 2, 30, 0, 0, 0)]
    [InlineData(2023, 2, 29, 0, 0, 0)]
    [InlineData(2024, 4, 31, 0, 0, 0)]
    [InlineData(2024, 1, 1, 24, 0, 0)]
    [InlineData(2024, 1, 1, 0, 60, 0)]
    public void Rejects_invalid_fields(int y, int mo, int d, int h, int mi, int s) =>
        Assert.False(DosTime.TryConvert(Pack(y, mo, d, h, mi, s), out _));

    [Fact]
    public void Rejects_seconds_field_above_29()
    {
        var v = (uint)(((((2024 - 1980) << 9) | (1 << 5) | 1) << 16) | 30);   // seconds*2 field = 30 -> 60 s
        Assert.False(DosTime.TryConvert(v, out _));
    }

    [Fact]
    public void Never_throws_for_any_32_bit_input_sample()
    {
        var rng = new Random(5);
        for (var i = 0; i < 300_000; i++) _ = DosTime.TryConvert((uint)rng.NextInt64(0, 1L << 32), out _);
    }
}
