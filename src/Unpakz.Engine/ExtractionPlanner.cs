using System;
using System.Collections.Generic;
using System.Text;
using Unpakz.Core;

namespace Unpakz.Engine;

public sealed record PlannedEntry(ArchiveEntry Entry, string RelativePath, bool IsDirectory);

public sealed record ExtractionPlan(IReadOnlyList<PlannedEntry> Items, IReadOnlyList<SkippedEntry> Skipped, long TotalBytes);

/// <summary>
/// Pure, I/O-free validation of an archive against everything we refuse to do. Any failure here happens before
/// the first byte is written, so a hostile archive leaves the file system untouched.
/// </summary>
public static class ExtractionPlanner
{
    public static ExtractionPlan Plan(IArchive archive, string destinationRoot, ExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var entries = archive.Entries;
        ArgumentNullException.ThrowIfNull(destinationRoot);
        ArgumentNullException.ThrowIfNull(options);

        var items = new List<PlannedEntry>(entries.Count);
        var skipped = new List<SkippedEntry>();
        var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // value: true = directory
        long total = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];

            if (e.Kind == EntryKind.Symlink) { skipped.Add(new SkippedEntry(e.Name, SkipReason.Symlink)); continue; }
            if (e.Kind == EntryKind.Special) { skipped.Add(new SkippedEntry(e.Name, SkipReason.SpecialFile)); continue; }

            var check = PathGuard.Validate(e.Name, options.PathGuard);
            if (!check.IsSafe)
            {
                if (options.SkipUnsafePaths) { skipped.Add(new SkippedEntry(e.Name, SkipReason.UnsafePath)); continue; }
                throw new ArchiveException($"Unsafe entry name '{Printable(e.Name)}': {check.Reason}.", ArchiveError.UnsafePath);
            }

            var isDir = check.IsDirectory || e.Kind == EntryKind.Directory;
            var rel = check.RelativePath;

            if ((long)destinationRoot.Length + 1 + rel.Length > options.MaxFullPathLength)
                throw new ArchiveException($"Path too long: '{Printable(e.Name)}'.", ArchiveError.LimitExceeded);

            if (isDir)
            {
                if (e.UncompressedSize != 0 || e.CompressedSize != 0) throw new ArchiveException("Directory entry has data.");
            }
            else
            {
                archive.CheckSupported(e);

                total += (long)Math.Min(e.UncompressedSize, long.MaxValue / 2);
                if (total > options.MaxTotalSize) throw new ArchiveException("Total extracted size exceeds the limit.", ArchiveError.LimitExceeded);
            }

            if (!RegisterAndCheckCollisions(kinds, rel, isDir, out var duplicateDirectory))
                throw new ArchiveException($"Entry '{Printable(e.Name)}' collides with another entry.", ArchiveError.Conflict);
            if (duplicateDirectory) continue;

            items.Add(new PlannedEntry(e, rel, isDir));
        }

        CheckOverlap(archive, items);
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

    private static void CheckOverlap(IArchive archive, List<PlannedEntry> items)
    {
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var it in items)
        {
            // Header + data: contiguous entries touch exactly; anything that shares bytes with another entry is hostile.
            ranges.Add(archive.GetSpan(it.Entry));
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
