using System;
using System.Collections.Generic;

namespace Unpakz.Core;

/// <summary>Copies uncompressed archive bytes straight to the sink (no intermediate buffer), computing the CRC-32 on the way.</summary>
internal static class StoredData
{
    private const int Chunk = 1 << 20;

    /// <summary>The same, for data that is spread over several buffers (volumes); the CRC runs across the joins.</summary>
    internal static uint Copy(IReadOnlyList<ReadOnlyMemory<byte>> segments, IOutputSink sink)
    {
        uint crc = 0;
        foreach (var segment in segments)
        {
            for (var pos = 0; pos < segment.Length; pos += Chunk)
            {
                var chunk = segment.Span.Slice(pos, Math.Min(Chunk, segment.Length - pos));
                crc = Checksums.Crc32.Update(crc, chunk);
                sink.Write(chunk);
            }
        }

        return crc;
    }

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
