using System;
using System.Collections.Generic;

namespace Unpakz.Interop;

/// <summary>Turns what a user picked in a file list into the set of entries to extract: a picked folder brings everything inside it.</summary>
public static class EntrySelection
{
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>Entry name with '/' separators, no empty segments and no trailing separator.</summary>
    public static string Normalize(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return string.Join("/", name.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The picked entries plus, for each picked directory, every entry below it. Indexes refer to <paramref name="entries"/>.</summary>
    public static HashSet<int> Expand(IReadOnlyList<EntryInfo> entries, IEnumerable<int> selected)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (selected is null) throw new ArgumentNullException(nameof(selected));

        var result = new HashSet<int>();
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in selected)
        {
            if ((uint)index >= (uint)entries.Count) throw new ArgumentOutOfRangeException(nameof(selected), $"Entry {index} does not exist.");
            result.Add(index);
            if (entries[index].Kind == EntryKind.Directory) folders.Add(Normalize(entries[index].Name));
        }

        if (folders.Count == 0) return result;

        for (var i = 0; i < entries.Count; i++)
        {
            if (result.Contains(i)) continue;
            var path = Normalize(entries[i].Name);
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (!folders.Contains(path.Substring(0, slash))) continue;
                result.Add(i);
                break;
            }
        }

        return result;
    }
}
