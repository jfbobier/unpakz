using Unpakz.Core.Rar;

namespace Unpakz.Tests;

public class VIntTests
{
    [Theory]
    [InlineData(new byte[] { 0x00 }, 0UL, 1)]
    [InlineData(new byte[] { 0x7F }, 127UL, 1)]
    [InlineData(new byte[] { 0x80, 0x01 }, 128UL, 2)]
    [InlineData(new byte[] { 0xFF, 0x7F }, 16383UL, 2)]
    [InlineData(new byte[] { 0x80, 0x80, 0x01 }, 16384UL, 3)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, ulong.MaxValue, 10)]
    [InlineData(new byte[] { 0x05, 0xAA, 0xBB }, 5UL, 1)]   // trailing bytes are not consumed
    public void Decodes(byte[] input, ulong expected, int length)
    {
        Assert.True(VInt.TryRead(input, out var v, out var n));
        Assert.Equal(expected, v);
        Assert.Equal(length, n);
    }

    [Theory]
    [InlineData(new byte[] { })]                                                              // empty
    [InlineData(new byte[] { 0x80 })]                                                         // continuation, then nothing
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF })]                                             // truncated
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 })]   // 65 bits: overflow
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })]   // 11 bytes
    public void Rejects(byte[] input) => Assert.False(VInt.TryRead(input, out _, out _));

    [Fact]
    public void Round_trips_with_independent_encoder()
    {
        var rng = new Random(11);
        for (var i = 0; i < 20_000; i++)
        {
            var v = (ulong)rng.NextInt64() >> rng.Next(0, 64);
            var enc = Support.Rar5Builder.VInt(v);
            Assert.True(VInt.TryRead(enc, out var back, out var n));
            Assert.Equal(v, back);
            Assert.Equal(enc.Length, n);
        }
    }
}
