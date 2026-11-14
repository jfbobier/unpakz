using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Unpakz.Interop;

/// <summary>One file or folder offered to the shell for a drag: a relative path with Windows separators.</summary>
public sealed class VirtualFile
{
    public VirtualFile(string relativePath, bool isDirectory, long size, DateTime? lastWriteLocal, int entryIndex)
    {
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteLocal = lastWriteLocal;
        EntryIndex = entryIndex;
    }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public DateTime? LastWriteLocal { get; }

    public int EntryIndex { get; }
}

/// <summary>
/// What a drag-and-drop out of the app carries. Every name was already checked by the native guard; it is checked once more here
/// (client-side double check) before the shell sees it: nothing unsafe, no duplicates, nothing a file descriptor cannot hold.
/// </summary>
public sealed class VirtualFileList
{
    /// <summary>Longest relative path a shell file descriptor can hold (MAX_PATH - 1).</summary>
    public const int MaxDescriptorName = 259;

    private VirtualFileList(IReadOnlyList<VirtualFile> items, int skipped)
    {
        Items = items;
        Skipped = skipped;
    }

    public IReadOnlyList<VirtualFile> Items { get; }

    /// <summary>Links and special files in the selection, which are never offered.</summary>
    public int Skipped { get; }

    public static VirtualFileList Build(ArchiveSession session, IEnumerable<int>? selection = null)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        var keep = selection is null ? null : EntrySelection.Expand(session.Entries, selection);

        var items = new List<VirtualFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var e in session.Entries)
        {
            if (keep is not null && !keep.Contains((int)e.Index)) continue;
            if (e.Kind != EntryKind.File && e.Kind != EntryKind.Directory) { skipped++; continue; }

            if (e.RelativePath.Length == 0)
            {
                if (e.Kind == EntryKind.Directory) continue;          // the archive's root entry ("./")
                throw new UnpakzException(UnpakzStatus.UnsafePath, "The archive contains a name that was refused.");
            }

            if (!SafeNames.IsSafeRelativePath(e.RelativePath)) throw new UnpakzException(UnpakzStatus.UnsafePath, "An entry name failed the client-side check.");
            var path = e.RelativePath.Replace('/', '\\');
            if (path.Length > MaxDescriptorName)
                throw new UnpakzException(UnpakzStatus.LimitExceeded, "A path is too long to drag out. Use \"Extract to…\" instead.");
            if (!seen.Add(path) && e.Kind == EntryKind.File) throw new UnpakzException(UnpakzStatus.Conflict, "Two entries have the same name.");

            items.Add(new VirtualFile(path, e.Kind == EntryKind.Directory, e.Kind == EntryKind.Directory ? 0 : (long)Math.Min(e.Size, long.MaxValue),
                e.LastWriteUtc?.ToLocalTime(), (int)e.Index));
        }

        return new VirtualFileList(items, skipped);
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
        if (items is null) throw new ArgumentNullException(nameof(items));
        var bytes = new byte[4 + (DescriptorSize * items.Count)];
        Put(bytes, 0, (uint)items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.RelativePath.Length >= NameChars)
                throw new UnpakzException(UnpakzStatus.LimitExceeded, "A path is too long for a shell file descriptor.");

            var d = 4 + (DescriptorSize * i);
            var flags = FdAttributes | FdProgressUi;
            if (!item.IsDirectory) flags |= FdFileSize;
            if (item.LastWriteLocal is not null) flags |= FdWritesTime;

            Put(bytes, d, flags);
            Put(bytes, d + 36, item.IsDirectory ? AttrDirectory : AttrNormal);
            if (item.LastWriteLocal is { } t) Put(bytes, d + 56, (ulong)t.ToFileTimeUtc());
            if (!item.IsDirectory)
            {
                Put(bytes, d + 64, (uint)((ulong)item.Size >> 32));
                Put(bytes, d + 68, (uint)((ulong)item.Size & 0xFFFFFFFF));
            }

            Encoding.Unicode.GetBytes(item.RelativePath, 0, item.RelativePath.Length, bytes, d + NameOffset);
        }

        return bytes;
    }

    private static void Put(byte[] b, int at, uint v) { for (var i = 0; i < 4; i++) b[at + i] = (byte)(v >> (8 * i)); }

    private static void Put(byte[] b, int at, ulong v) { for (var i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i)); }
}
