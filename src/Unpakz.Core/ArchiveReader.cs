using System;
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
}
