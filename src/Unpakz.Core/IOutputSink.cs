using System;

namespace Unpakz.Core;

/// <summary>Destination for decoded bytes. Decoders write sequentially and never touch files or the OS directly.</summary>
public interface IOutputSink
{
    void Write(ReadOnlySpan<byte> data);
}
