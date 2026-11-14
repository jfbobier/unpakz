using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>One file or folder offered to the shell for a drag: a relative path with Windows separators, never validated by the shell itself.</summary>
public sealed record VirtualFile(string RelativePath, bool IsDirectory, long Size, DateTime? LastWriteLocal, int EntryIndex);

/// <summary>
/// What a drag-and-drop out of the app carries. Built through the same planner as a normal extraction, so every name has passed
/// <see cref="PathGuard"/> (traversal, reserved names, collisions, links) before the shell ever sees it.
/// </summary>
public sealed class VirtualFileList
{
    /// <summary>Longest relative path a shell file descriptor can hold (MAX_PATH - 1).</summary>
    public const int MaxDescriptorName = 259;

    public IReadOnlyList<VirtualFile> Items { get; }

    public IReadOnlyList<SkippedEntry> Skipped { get; }

    private VirtualFileList(IReadOnlyList<VirtualFile> items, IReadOnlyList<SkippedEntry> skipped)
    {
        Items = items;
        Skipped = skipped;
    }

    public static VirtualFileList Build(IArchive archive, IEnumerable<int>? selection = null, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        options ??= new ExtractOptions();
        if (selection is not null)
        {
            var keep = EntrySelection.Expand(archive.Entries, selection);
            options = options.WithFilter(e => keep.Contains(e.Index));
        }

        var plan = ExtractionPlanner.Plan(archive, "x", options);
        var items = new List<VirtualFile>(plan.Items.Count);
        foreach (var it in plan.Items)
        {
            var path = it.RelativePath.Replace('/', '\\');
            if (path.Length > MaxDescriptorName)
                throw new ArchiveException($"'{ExtractionPlanner.Printable(path)}' has a path that is too long to drag out. Use \"Extract to…\" instead.", ArchiveError.LimitExceeded);

            items.Add(new VirtualFile(path, it.IsDirectory, it.IsDirectory ? 0 : (long)Math.Min(it.Entry.UncompressedSize, long.MaxValue), it.Entry.LastWriteLocal, it.Entry.Index));
        }

        return new VirtualFileList(items, plan.Skipped);
    }
}

/// <summary>Serialises a FILEGROUPDESCRIPTORW: the clipboard format that tells Explorer which virtual files a drag contains.</summary>
public static class FileGroupDescriptor
{
    private const int DescriptorSize = 592;       // DWORD flags, CLSID, SIZEL, POINTL, DWORD attrs, 3 x FILETIME, 2 x DWORD size, WCHAR[260]
    private const int NameOffset = 72;
    private const int NameChars = 260;
    private const uint FdAttributes = 0x04, FdWritesTime = 0x20, FdFileSize = 0x40, FdProgressUi = 0x4000;
    private const uint AttrDirectory = 0x10, AttrNormal = 0x80;

    public static byte[] Serialize(IReadOnlyList<VirtualFile> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var bytes = new byte[4 + (DescriptorSize * items.Count)];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.RelativePath.Length >= NameChars)
                throw new ArchiveException("A path is too long for a shell file descriptor.", ArchiveError.LimitExceeded);

            var d = bytes.AsSpan(4 + (DescriptorSize * i), DescriptorSize);
            var flags = FdAttributes | FdProgressUi;
            if (!item.IsDirectory) flags |= FdFileSize;
            if (item.LastWriteLocal is not null) flags |= FdWritesTime;

            BitConverter.TryWriteBytes(d[..4], flags);
            BitConverter.TryWriteBytes(d.Slice(36, 4), item.IsDirectory ? AttrDirectory : AttrNormal);
            if (item.LastWriteLocal is { } t) BitConverter.TryWriteBytes(d.Slice(56, 8), t.ToFileTimeUtc());
            if (!item.IsDirectory)
            {
                BitConverter.TryWriteBytes(d.Slice(64, 4), (uint)((ulong)item.Size >> 32));
                BitConverter.TryWriteBytes(d.Slice(68, 4), (uint)((ulong)item.Size & 0xFFFFFFFF));
            }

            Encoding.Unicode.GetBytes(item.RelativePath, d[NameOffset..]);
        }

        return bytes;
    }
}
