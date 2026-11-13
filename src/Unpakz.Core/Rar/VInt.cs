using System;

namespace Unpakz.Core.Rar;

/// <summary>RAR5 variable-length integer: 7 bits per byte, least significant group first, high bit = "more follows", at most 64 bits.</summary>
internal static class VInt
{
    internal static bool TryRead(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        for (var i = 0; i < data.Length && i < 10; i++)
        {
            var b = data[i];
            var payload = (ulong)(b & 0x7F);
            if (i == 9 && payload > 1) return false;   // would not fit in 64 bits

            value |= payload << (7 * i);
            if ((b & 0x80) == 0)
            {
                length = i + 1;
                return true;
            }
        }

        value = 0;
        return false;   // truncated, or longer than 10 bytes
    }
}
