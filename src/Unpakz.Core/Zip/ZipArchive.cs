using System;
using System.Collections.Generic;

namespace Unpakz.Core.Zip;

/// <summary>ZIP/ZIP64 behind the format-neutral <see cref="IArchive"/>.</summary>
public sealed class ZipArchive : IArchive
{
    private const uint SymlinkType = 0xA000, RegularType = 0x8000, DirectoryType = 0x4000;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly IReadOnlyList<ZipEntryInfo> _zipEntries;

    public string Format => "zip";

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    private ZipArchive(ReadOnlyMemory<byte> data, IReadOnlyList<ZipEntryInfo> zipEntries)
    {
        _data = data;
        _zipEntries = zipEntries;
        var list = new List<ArchiveEntry>(zipEntries.Count);
        for (var i = 0; i < zipEntries.Count; i++)
        {
            var z = zipEntries[i];
            list.Add(new ArchiveEntry(i, z.Name, KindOf(z), z.CompressedSize, z.UncompressedSize,
                DosTime.TryConvert(z.DosDateTime, out var t) ? t : null));
        }

        Entries = list;
    }

    public static ZipArchive Open(ReadOnlyMemory<byte> data, ArchiveLimits? limits = null) =>
        new(data, ZipCentralDirectory.Read(data.Span, limits ?? new ArchiveLimits()));

    private static EntryKind KindOf(ZipEntryInfo z)
    {
        var type = z.UnixFileType;
        if (type == SymlinkType) return EntryKind.Symlink;
        if (type is not (0 or RegularType or DirectoryType)) return EntryKind.Special;
        return z.IsDirectory || type == DirectoryType ? EntryKind.Directory : EntryKind.File;
    }

    public void CheckSupported(ArchiveEntry entry)
    {
        var z = _zipEntries[entry.Index];
        if (z.IsEncrypted) throw new ArchiveException("Encrypted entries are not supported yet.", ArchiveError.Unsupported);
        if (z.Method is not (0 or 8))
            throw new ArchiveException($"Compression method {z.Method} is not supported.", ArchiveError.Unsupported);
    }

    public (ulong Start, ulong End) GetSpan(ArchiveEntry entry)
    {
        var z = _zipEntries[entry.Index];
        var (start, length) = ZipEntryExtractor.LocateRange(_data, z);
        return (z.LocalHeaderOffset, start + length);   // header + data: contiguous entries touch exactly
    }

    public void Extract(ArchiveEntry entry, IOutputSink sink, ArchiveLimits limits) =>
        ZipEntryExtractor.Extract(_data, _zipEntries[entry.Index], sink, limits);
}
