namespace Unpakz.Core;

public sealed class ArchiveLimits
{
    /// <summary>Maximum number of central directory entries accepted.</summary>
    public long MaxEntries { get; init; } = 10_000_000;

    /// <summary>Maximum declared uncompressed size of a single entry (default 16 TiB).</summary>
    public long MaxEntrySize { get; init; } = 1L << 44;

    /// <summary>Largest decompression window (dictionary) a decoder may allocate; bigger needs an explicit opt-in.</summary>
    public long MaxDictionarySize { get; init; } = 1L << 30;
}
