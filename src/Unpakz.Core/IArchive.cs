using System.Collections.Generic;

namespace Unpakz.Core;

/// <summary>An opened, structurally validated archive. Implementations never touch files; they decode into an <see cref="IOutputSink"/>.</summary>
public interface IArchive
{
    string Format { get; }

    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Throws <see cref="ArchiveException"/> (Unsupported) if this entry cannot be extracted by this build (method, encryption, integrity data).</summary>
    void CheckSupported(ArchiveEntry entry);

    /// <summary>Byte range [Start, End) the entry occupies in the archive (header + data). Used to reject entries that share bytes.</summary>
    (ulong Start, ulong End) GetSpan(ArchiveEntry entry);

    /// <summary>Decodes the entry, verifying size and integrity. On failure the caller must discard what was written.</summary>
    void Extract(ArchiveEntry entry, IOutputSink sink, ArchiveLimits limits);
}
