using System;

namespace Unpakz.Core;

public enum EntryKind { File, Directory, Symlink, Special }

/// <summary>Format-neutral view of one archive entry, as the planner needs it. <see cref="Index"/> maps back into the format reader.</summary>
public sealed record ArchiveEntry(
    int Index,
    string Name,
    EntryKind Kind,
    ulong CompressedSize,
    ulong UncompressedSize,
    DateTime? LastWriteLocal);
