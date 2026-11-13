using System;

namespace Unpakz.Core.Rar;

// TEMPORARY stub so the IArchive refactor can be verified in isolation; replaced by the real reader next.
public sealed class Rar5Archive
{
    public static bool HasSignature(ReadOnlySpan<byte> data) => false;
    public static bool HasRar4Signature(ReadOnlySpan<byte> data) => false;
    public static IArchive Open(ReadOnlyMemory<byte> data, ArchiveLimits limits) => throw new NotSupportedException();
}
