using System;
using System.Collections.Generic;

namespace Unpakz.Core.Rar;

/// <summary>
/// A file's compressed data as one forward-only stream, even when it is split across volumes. Blocks that fit inside one
/// segment are returned as slices of the archive (no copy); blocks that straddle a volume boundary are merged.
/// </summary>
internal sealed class PackedStream
{
    private readonly IReadOnlyList<ReadOnlyMemory<byte>> _segments;
    private int _segment;
    private int _offset;

    public PackedStream(IReadOnlyList<ReadOnlyMemory<byte>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments;
        foreach (var s in segments) Remaining += s.Length;
    }

    public PackedStream(ReadOnlyMemory<byte> single)
        : this([single])
    {
    }

    public long Remaining { get; private set; }

    public void ReadExactly(Span<byte> destination)
    {
        if (destination.Length > Remaining) throw new ArchiveException("Compressed data ended unexpectedly.");

        var written = 0;
        while (written < destination.Length)
        {
            SkipExhaustedSegments();
            var current = _segments[_segment].Span[_offset..];
            var n = Math.Min(current.Length, destination.Length - written);
            current[..n].CopyTo(destination[written..]);
            written += n;
            _offset += n;
            Remaining -= n;
        }
    }

    /// <summary>Returns the next <paramref name="count"/> bytes contiguously; a slice of the archive when possible.</summary>
    public ReadOnlyMemory<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new ArchiveException("Compressed data ended unexpectedly.");
        if (count == 0) return ReadOnlyMemory<byte>.Empty;

        SkipExhaustedSegments();
        var current = _segments[_segment];
        if (current.Length - _offset >= count)
        {
            var slice = current.Slice(_offset, count);
            _offset += count;
            Remaining -= count;
            return slice;
        }

        var merged = new byte[count];
        ReadExactly(merged);
        return merged;
    }

    private void SkipExhaustedSegments()
    {
        while (_offset >= _segments[_segment].Length)
        {
            _segment++;
            _offset = 0;
        }
    }
}
