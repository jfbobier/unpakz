using System;
using System.Collections.Generic;
using Unpakz.Core;

namespace Unpakz.Engine;

public enum OverwritePolicy
{
    /// <summary>Abort with a Conflict error if a file already exists (default; never destroys data).</summary>
    Fail,
    /// <summary>Keep the existing file and report it as skipped.</summary>
    Skip,
    /// <summary>Replace the existing file (a symlink at that path is replaced, never followed).</summary>
    Overwrite,
}

public enum SkipReason
{
    Symlink,
    SpecialFile,
    UnsafePath,
    AlreadyExists,
}

public sealed record SkippedEntry(string Name, SkipReason Reason);

public sealed class ExtractOptions
{
    /// <summary>A copy of these options with a different entry filter.</summary>
    public ExtractOptions WithFilter(Func<ArchiveEntry, bool>? filter) => new()
    {
        PathGuard = PathGuard,
        Limits = Limits,
        Overwrite = Overwrite,
        SkipUnsafePaths = SkipUnsafePaths,
        EntryFilter = filter,
        RestoreTimestamps = RestoreTimestamps,
        MaxTotalSize = MaxTotalSize,
        MaxFullPathLength = MaxFullPathLength,
        PreallocateLimit = PreallocateLimit,
    };

    public PathGuardOptions PathGuard { get; init; } = new();
    public ArchiveLimits Limits { get; init; } = new();
    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Fail;

    /// <summary>If false (default) any unsafe entry name aborts the whole extraction before anything is written.</summary>
    public bool SkipUnsafePaths { get; init; }

    /// <summary>When set, only entries for which this returns true are planned and extracted (unselected entries are not even validated).</summary>
    public Func<ArchiveEntry, bool>? EntryFilter { get; init; }

    public bool RestoreTimestamps { get; init; } = true;

    /// <summary>Maximum total declared size of all extracted files.</summary>
    public long MaxTotalSize { get; init; } = 1L << 50;

    /// <summary>Maximum length of destination root + relative path.</summary>
    public int MaxFullPathLength { get; init; } = 32_000;

    /// <summary>Files up to this declared size are pre-sized on disk; larger declared sizes are not trusted for preallocation.</summary>
    public long PreallocateLimit { get; init; } = 1L << 30;
}

public readonly record struct ExtractionProgress(long BytesWritten, long TotalBytes, int FilesCompleted, int TotalFiles);

public sealed record ExtractionResult(int Files, int Directories, long Bytes, IReadOnlyList<SkippedEntry> Skipped);
