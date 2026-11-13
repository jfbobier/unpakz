using System;

namespace Unpakz.Core;

/// <summary>Copies uncompressed archive bytes straight to the sink (no intermediate buffer), computing the CRC-32 on the way.</summary>
internal static class StoredData
{
    private const int Chunk = 1 << 20;

    internal static uint Copy(ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        uint crc = 0;
        for (var pos = 0; pos < data.Length; pos += Chunk)
        {
            var chunk = data.Span.Slice(pos, Math.Min(Chunk, data.Length - pos));
            crc = Checksums.Crc32.Update(crc, chunk);
            sink.Write(chunk);   // straight from the archive bytes: no intermediate copy
        }

        return crc;
    }
}
