using System;
using System.IO;

namespace Unpakz.Core.Zip;

/// <summary>Forward-only read stream over memory, so BCL decompressors can consume a slice of the archive without copying it.</summary>
internal sealed class MemoryReadStream(ReadOnlyMemory<byte> data) : Stream
{
    private ReadOnlyMemory<byte> _rest = data;
    private readonly long _length = data.Length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _rest.Length; set => throw new NotSupportedException(); }

    public override int Read(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _rest.Length);
        _rest.Span[..n].CopyTo(buffer);
        _rest = _rest[n..];
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
