using System;
using System.Collections.Generic;
using System.Text;
using Unpakz.Core;
using Unpakz.Core.Zip;

namespace Unpakz.Engine;

public sealed record PlannedEntry(int Index, ZipEntryInfo Entry, string RelativePath, bool IsDirectory);

public sealed record ExtractionPlan(IReadOnlyList<PlannedEntry> Items, IReadOnlyList<SkippedEntry> Skipped, long TotalBytes);

/// <summary>
/// Pure, I/O-free validation of an archive against everything we refuse to do. Any failure here happens before
/// the first byte is written, so a hostile archive leaves the file system untouched.
/// </summary>
public static class ExtractionPlanner
{
    private const uint SymlinkType = 0xA000, RegularType = 0x8000, DirectoryType = 0x4000;

    public static ExtractionPlan Plan(ReadOnlyMemory<byte> zip, IReadOnlyList<ZipEntryInfo> entries, string destinationRoot, ExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(destinationRoot);
        ArgumentNullException.ThrowIfNull(options);

        var items = new List<PlannedEntry>(entries.Count);
        var skipped = new List<SkippedEntry>();
        var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // value: true = directory
        long total = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var type = e.UnixFileType;

            if (type == SymlinkType) { skipped.Add(new SkippedEntry(e.Name, SkipReason.Symlink)); continue; }
            if (type is not (0 or RegularType or DirectoryType)) { skipped.Add(new SkippedEntry(e.Name, SkipReason.SpecialFile)); continue; }

            var check = PathGuard.Validate(e.Name, options.PathGuard);
            if (!check.IsSafe)
            {
                if (options.SkipUnsafePaths) { skipped.Add(new SkippedEntry(e.Name, SkipReason.UnsafePath)); continue; }
                throw new ArchiveException($"Unsafe entry name '{Printable(e.Name)}': {check.Reason}.", ArchiveError.UnsafePath);
            }

            var isDir = check.IsDirectory || type == DirectoryType;
            var rel = check.RelativePath;

            if ((long)destinationRoot.Length + 1 + rel.Length > options.MaxFullPathLength)
                throw new ArchiveException($"Path too long: '{Printable(e.Name)}'.", ArchiveError.LimitExceeded);

            if (isDir)
            {
                if (e.UncompressedSize != 0 || e.CompressedSize != 0) throw new ArchiveException("Directory entry has data.");
            }
            else
            {
                if (e.IsEncrypted) throw new ArchiveException("Encrypted entries are not supported yet.", ArchiveError.Unsupported);
                if (e.Method is not (0 or 8))
                    throw new ArchiveException($"Compression method {e.Method} is not supported.", ArchiveError.Unsupported);

                total += (long)Math.Min(e.UncompressedSize, long.MaxValue / 2);
                if (total > options.MaxTotalSize) throw new ArchiveException("Total extracted size exceeds the limit.", ArchiveError.LimitExceeded);
            }

            if (!RegisterAndCheckCollisions(kinds, rel, isDir, out var duplicateDirectory))
                throw new ArchiveException($"Entry '{Printable(e.Name)}' collides with another entry.", ArchiveError.Conflict);
            if (duplicateDirectory) continue;

            items.Add(new PlannedEntry(i, e, rel, isDir));
        }

        CheckOverlap(zip, items);
        return new ExtractionPlan(items, skipped, total);
    }

    private static bool RegisterAndCheckCollisions(Dictionary<string, bool> kinds, string rel, bool isDir, out bool duplicateDirectory)
    {
        duplicateDirectory = false;

        var slash = -1;
        while ((slash = rel.IndexOf('/', slash + 1)) >= 0)
        {
            var prefix = rel[..slash];
            if (kinds.TryGetValue(prefix, out var prefixIsDir)) { if (!prefixIsDir) return false; }
            else kinds[prefix] = true;
        }

        if (kinds.TryGetValue(rel, out var existingIsDir))
        {
            if (isDir && existingIsDir) { duplicateDirectory = true; return true; }
            return false;
        }

        kinds[rel] = isDir;
        return true;
    }

    private static void CheckOverlap(ReadOnlyMemory<byte> zip, List<PlannedEntry> items)
    {
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var it in items)
        {
            // Header + data: contiguous entries touch exactly; anything that shares bytes with another entry is hostile.
            var (start, length) = ZipEntryExtractor.LocateRange(zip, it.Entry);
            ranges.Add((it.Entry.LocalHeaderOffset, start + length));
        }

        ranges.Sort();
        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End) throw new ArchiveException("Entries overlap in the archive.");
        }
    }

    internal static string Printable(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, 200));
        foreach (var c in name.AsSpan(0, Math.Min(name.Length, 200))) sb.Append(char.IsControl(c) ? '?' : c);
        return sb.ToString();
    }
}
