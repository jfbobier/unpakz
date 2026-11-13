namespace Zipx.Core.Zip;

public sealed class ZipLimits
{
    /// <summary>Maximum number of central directory entries accepted.</summary>
    public long MaxEntries { get; init; } = 10_000_000;
}
