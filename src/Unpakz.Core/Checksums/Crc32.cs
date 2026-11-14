using System;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Unpakz.Core.Checksums;

/// <summary>
/// CRC-32 (IEEE 802.3, as used by ZIP). Software slicing-by-8 baseline plus a PCLMULQDQ folding path, which is used only because
/// <c>bench/results/crc-linux</c> shows it ~8x faster (1.5 GB/s -> 12.7 GB/s on 256 MiB). Both are tested against each other.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Tables = BuildTables();

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (!HardwareSupported || data.Length < HardwareMinimum) return UpdateSoftware(crc, data);

        var simdLength = data.Length & ~15;
        var state = Fold(~crc, data[..simdLength]);
        return UpdateSoftware(~state, data[simdLength..]);
    }

    internal static bool HardwareSupported => Pclmulqdq.IsSupported && Sse41.IsSupported;

    internal const int HardwareMinimum = 64;

    internal static uint UpdateSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        var t = Tables;
        var c = ~crc;
        var i = 0;

        for (; data.Length - i >= 8; i += 8)
        {
            var v = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
            var lo = (uint)v ^ c;
            var hi = (uint)(v >> 32);
            c = t[(7 << 8) + (int)(lo & 0xFF)] ^ t[(6 << 8) + (int)((lo >> 8) & 0xFF)]
              ^ t[(5 << 8) + (int)((lo >> 16) & 0xFF)] ^ t[(4 << 8) + (int)(lo >> 24)]
              ^ t[(3 << 8) + (int)(hi & 0xFF)] ^ t[(2 << 8) + (int)((hi >> 8) & 0xFF)]
              ^ t[(1 << 8) + (int)((hi >> 16) & 0xFF)] ^ t[(int)(hi >> 24)];
        }

        for (; i < data.Length; i++) c = t[(int)((c ^ data[i]) & 0xFF)] ^ (c >> 8);
        return ~c;
    }

    // Folding constants for the reflected polynomial 0xEDB88320 (Intel, "Fast CRC Computation for Generic Polynomials Using PCLMULQDQ").
    private static readonly Vector128<ulong> K1K2 = Vector128.Create(0x0154442bd4UL, 0x01c6e41596UL);
    private static readonly Vector128<ulong> K3K4 = Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL);
    private static readonly Vector128<ulong> K5 = Vector128.Create(0x0163cd6124UL, 0UL);
    private static readonly Vector128<ulong> Poly = Vector128.Create(0x01db710641UL, 0x01f7011641UL);

    /// <summary>Raw CRC state in, raw state out (no inversion). <paramref name="data"/> must be a multiple of 16 bytes, at least 16.</summary>
    private static uint Fold(uint state, ReadOnlySpan<byte> data)
    {
        var x1 = Vector128.LoadUnsafe(in data[0]).AsUInt64() ^ Vector128.CreateScalar(state).AsUInt64();
        var pos = 16;

        if (data.Length >= 64)
        {
            var x2 = Vector128.LoadUnsafe(in data[pos]).AsUInt64();
            var x3 = Vector128.LoadUnsafe(in data[pos + 16]).AsUInt64();
            var x4 = Vector128.LoadUnsafe(in data[pos + 32]).AsUInt64();
            pos += 48;

            while (data.Length - pos >= 64)
            {
                x1 = FoldStep(x1, K1K2, Vector128.LoadUnsafe(in data[pos]).AsUInt64());
                x2 = FoldStep(x2, K1K2, Vector128.LoadUnsafe(in data[pos + 16]).AsUInt64());
                x3 = FoldStep(x3, K1K2, Vector128.LoadUnsafe(in data[pos + 32]).AsUInt64());
                x4 = FoldStep(x4, K1K2, Vector128.LoadUnsafe(in data[pos + 48]).AsUInt64());
                pos += 64;
            }

            x1 = FoldStep(x1, K3K4, x2);
            x1 = FoldStep(x1, K3K4, x3);
            x1 = FoldStep(x1, K3K4, x4);
        }

        while (data.Length - pos >= 16)
        {
            x1 = FoldStep(x1, K3K4, Vector128.LoadUnsafe(in data[pos]).AsUInt64());
            pos += 16;
        }

        // 128 -> 64 bits.
        var mask32 = Vector128.Create(~0u, 0u, ~0u, 0u);
        var t = Pclmulqdq.CarrylessMultiply(x1, K3K4, 0x10);
        x1 = Sse2.ShiftRightLogical128BitLane(x1, 8) ^ t;
        t = Sse2.ShiftRightLogical128BitLane(x1, 4);
        x1 = (x1.AsUInt32() & mask32).AsUInt64();
        x1 = Pclmulqdq.CarrylessMultiply(x1, K5, 0x00) ^ t;

        // Barrett reduction to 32 bits.
        t = (x1.AsUInt32() & mask32).AsUInt64();
        t = Pclmulqdq.CarrylessMultiply(t, Poly, 0x10);
        t = (t.AsUInt32() & mask32).AsUInt64();
        t = Pclmulqdq.CarrylessMultiply(t, Poly, 0x00);
        x1 ^= t;
        return x1.AsUInt32().GetElement(1);
    }

    private static Vector128<ulong> FoldStep(Vector128<ulong> x, Vector128<ulong> k, Vector128<ulong> next) =>
        Pclmulqdq.CarrylessMultiply(x, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(x, k, 0x11) ^ next;

    private static uint[] BuildTables()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }

        for (var k = 1; k < 8; k++)
            for (var n = 0; n < 256; n++)
            {
                var prev = t[((k - 1) << 8) + n];
                t[(k << 8) + n] = (prev >> 8) ^ t[(int)(prev & 0xFF)];
            }

        return t;
    }
}
