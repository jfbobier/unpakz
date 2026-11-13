using System;

namespace Unpakz.Core.Rar;

/// <summary>The RAR5 post-processing filters as pure functions over one contiguous block. Positions are relative to the file start.</summary>
internal static class RarFilters
{
    private const uint FileSize = 0x1000000;

    /// <summary>Undoes delta coding: the block holds <paramref name="channels"/> interleaved planes stored one after another.</summary>
    internal static void Delta(ReadOnlySpan<byte> src, int channels, Span<byte> dst)
    {
        var s = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            byte prev = 0;
            for (var d = channel; d < src.Length; d += channels)
            {
                prev -= src[s++];
                dst[d] = prev;
            }
        }
    }

    /// <summary>Converts absolute x86 CALL (E8) and optionally JMP (E9) targets back to relative ones.</summary>
    internal static void E8(ReadOnlySpan<byte> src, long startRel, bool extended, Span<byte> dst)
    {
        src.CopyTo(dst);

        for (var i = 0; i < src.Length - 4;)
        {
            var b = src[i++];
            if (b != 0xE8 && !(extended && b == 0xE9)) continue;

            var offset = (uint)((i + startRel) % FileSize);
            var addr = ReadLe32(src, i);

            if ((addr & 0x80000000) != 0)
            {
                if (((addr + offset) & 0x80000000) == 0) WriteLe32(dst, i, addr + FileSize);
            }
            else if (((addr - FileSize) & 0x80000000) != 0)
            {
                WriteLe32(dst, i, addr - offset);
            }

            i += 4;
        }
    }

    /// <summary>Converts absolute ARM BL (0xEB) targets back to relative ones.</summary>
    internal static void Arm(ReadOnlySpan<byte> src, long startRel, Span<byte> dst)
    {
        src.CopyTo(dst);

        for (var i = 0; i < src.Length - 3; i += 4)
        {
            if (src[i + 3] != 0xEB) continue;

            var offset = ReadLe32(src, i) & 0x00FFFFFF;
            offset -= (uint)((i + startRel) / 4);
            offset = (offset & 0x00FFFFFF) | 0xEB000000;
            WriteLe32(dst, i, offset);
        }
    }

    private static uint ReadLe32(ReadOnlySpan<byte> s, int i) =>
        s[i] | ((uint)s[i + 1] << 8) | ((uint)s[i + 2] << 16) | ((uint)s[i + 3] << 24);

    private static void WriteLe32(Span<byte> d, int i, uint v)
    {
        d[i] = (byte)v;
        d[i + 1] = (byte)(v >> 8);
        d[i + 2] = (byte)(v >> 16);
        d[i + 3] = (byte)(v >> 24);
    }
}
