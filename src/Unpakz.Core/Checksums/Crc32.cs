using System;
using System.Buffers.Binary;

namespace Unpakz.Core.Checksums;

/// <summary>CRC-32 (IEEE 802.3, as used by ZIP). Software slicing-by-8 baseline; faster paths must beat it in a benchmark.</summary>
public static class Crc32
{
    private static readonly uint[] Tables = BuildTables();

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
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
