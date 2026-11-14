using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Unpakz.Interop;

/// <summary>An opened archive as the windows see it: where it is, which files make it up, and its entries (already checked by the native guard).</summary>
public sealed class ArchiveSession
{
    private ArchiveSession(string path, IReadOnlyList<string> volumes, IReadOnlyList<EntryInfo> entries, string format)
    {
        Path = path;
        VolumePaths = volumes;
        Entries = entries;
        Format = format;
    }

    public string Path { get; }

    /// <summary>Every file of the set, in order (one for an ordinary archive).</summary>
    public IReadOnlyList<string> VolumePaths { get; }

    public IReadOnlyList<EntryInfo> Entries { get; }

    /// <summary>"ZIP", "RAR" or "TAR.GZ", from the file's first bytes.</summary>
    public string Format { get; }

    public long TotalBytes => VolumePaths.Sum(p => new FileInfo(p).Length);

    public static ArchiveSession Open(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("File not found.", full);

        var entries = NativeArchive.List(full);
        var format = DetectFormat(full);
        // A plain tar has no signature, and a file of zero bytes reads as a valid empty tar: do not present either as "an archive with 0 files".
        if (entries.Count == 0 && format == "ARCHIVE") throw new UnpakzException(UnpakzStatus.Unsupported, "This file is not a supported archive.");
        return new ArchiveSession(full, VolumeFiles.Find(full), entries, format);
    }

    private static string DetectFormat(string path)
    {
        var head = new byte[8];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
        if (n >= 4 && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r' && head[3] == (byte)'!') return "RAR";
        if (n >= 2 && head[0] == 0x1F && head[1] == 0x8B) return "TAR.GZ";
        return n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K' ? "ZIP" : "ARCHIVE";
    }
}

/// <summary>Finds the files of a "name.partN.rar" set next to a given part (for display; the native library verifies the set itself).</summary>
internal static class VolumeFiles
{
    private static readonly Regex PartName = new(@"^(?<base>.+)\.part(?<n>\d{1,9})\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Find(string fullPath)
    {
        var name = System.IO.Path.GetFileName(fullPath);
        var match = PartName.Match(name);
        if (!match.Success) return new[] { fullPath };

        var dir = System.IO.Path.GetDirectoryName(fullPath)!;
        var set = new SortedDictionary<int, string>();
        foreach (var candidate in Directory.EnumerateFiles(dir))
        {
            var m = PartName.Match(System.IO.Path.GetFileName(candidate));
            if (m.Success && string.Equals(m.Groups["base"].Value, match.Groups["base"].Value, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
                set[n] = candidate;
        }

        return set.Count == 0 ? new[] { fullPath } : set.Values.ToList();
    }
}
