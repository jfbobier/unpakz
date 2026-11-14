using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>
/// Finds the files of a RAR 5 multi-volume set (name.part1.rar, name.part02.rar, ...) from any one of them. Numbers are compared
/// numerically, so zero padding is irrelevant; the volumes must be 1..N without gaps. Order and numbering are verified again
/// by the reader from the numbers stored inside each volume, so the file names are only a way to find candidates.
/// </summary>
public static partial class VolumeLocator
{
    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,9})\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    public static IReadOnlyList<string> Find(string path, int maxVolumes = 65_536)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Archive not found.", full);

        var match = PartName().Match(Path.GetFileName(full));
        if (!match.Success) return [full];

        var directory = Path.GetDirectoryName(full)!;
        var baseName = match.Groups["base"].Value;
        var byNumber = new SortedDictionary<int, string>();

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var m = PartName().Match(Path.GetFileName(candidate));
            if (!m.Success || !string.Equals(m.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(m.Groups["n"].Value, out var number) || number < 1) continue;

            byNumber[number] = candidate;      // two files with the same number (different padding): the last one wins
            if (byNumber.Count > maxVolumes) throw new ArchiveException("Too many volumes.", ArchiveError.LimitExceeded);
        }

        var expected = 1;
        foreach (var number in byNumber.Keys)
        {
            if (number != expected)
                throw new ArchiveException($"The set is incomplete: volume {expected} is missing.", ArchiveError.MissingVolume) { Volume = expected };
            expected++;
        }

        return [.. byNumber.Values];
    }
}
