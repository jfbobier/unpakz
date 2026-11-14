using System;
using System.Collections.Generic;
using Unpakz.Core.Rar;
using Unpakz.Core.Zip;

namespace Unpakz.Core;

/// <summary>Format detection. Signature first (RAR), otherwise treat as ZIP (which locates its directory from the end).</summary>
public static class ArchiveReader
{
    public static IArchive Open(ReadOnlyMemory<byte> data, ArchiveLimits? limits = null)
    {
        limits ??= new ArchiveLimits();
        var span = data.Span;

        if (Rar5Archive.HasSignature(span)) return Rar5Archive.Open(data, limits);
        if (Rar5Archive.HasRar4Signature(span))
            throw new ArchiveException("RAR 4.x archives are not supported yet.", ArchiveError.Unsupported);

        return ZipArchive.Open(data, limits);
    }

    /// <summary>Opens a set of volumes (first volume first). Only RAR 5 supports more than one volume.</summary>
    public static IArchive Open(IReadOnlyList<ReadOnlyMemory<byte>> volumes, ArchiveLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (volumes.Count == 0) throw new ArgumentException("At least one volume is required.", nameof(volumes));
        limits ??= new ArchiveLimits();

        if (Rar5Archive.HasSignature(volumes[0].Span)) return Rar5Archive.Open(volumes, limits);
        if (volumes.Count > 1) throw new ArchiveException("Multi-volume archives are only supported for RAR 5.", ArchiveError.Unsupported);
        return Open(volumes[0], limits);
    }
}
