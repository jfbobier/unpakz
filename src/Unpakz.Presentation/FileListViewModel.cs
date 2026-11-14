using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unpakz.Core;
using Unpakz.Engine;

namespace Unpakz.Presentation;

public enum ListColumn { Name, Size, Packed, Modified, Type }

/// <summary>One row of the file list. Folders that exist only implicitly (no entry of their own) get a row too, with EntryIndex -1.</summary>
public sealed record ListRow(string Path, string Name, string Type, bool IsFolder, int EntryIndex, long Size, long Packed, DateTime? Modified);

/// <summary>Sorted, filtered rows over an archive's entries; maps picked rows back to entries (a picked folder brings its contents).</summary>
public sealed class FileListViewModel
{
    private readonly MainViewModel _main;
    private List<ListRow> _all = [];
    private List<ListRow> _view = [];
    private ListColumn _column = ListColumn.Name;
    private bool _ascending = true;
    private string _filter = string.Empty;

    public FileListViewModel(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        Reload();
    }

    public int RowCount => _view.Count;

    public ListRow GetRow(int row) => _view[row];

    public string Filter
    {
        get => _filter;
        set { _filter = value ?? string.Empty; Rebuild(); }
    }

    public void SortBy(ListColumn column, bool ascending)
    {
        _column = column;
        _ascending = ascending;
        Rebuild();
    }

    /// <summary>Rebuilds from the archive currently open in the main view-model.</summary>
    public void Reload()
    {
        _all = _main.Source is null ? [] : BuildRows(_main.Source.Archive.Entries);
        Rebuild();
    }

    public int IndexOfPath(string path)
    {
        var wanted = EntrySelection.Normalize(path);
        for (var i = 0; i < _view.Count; i++) if (_view[i].Path == wanted) return i;
        return -1;
    }

    /// <summary>Entry indexes behind the picked rows.</summary>
    public HashSet<int> ResolveSelection(IEnumerable<int> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var entries = _main.Source?.Archive.Entries ?? [];
        var picked = new List<int>();
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if ((uint)r >= (uint)_view.Count) throw new ArgumentOutOfRangeException(nameof(rows), $"Row {r} does not exist.");
            var row = _view[r];
            if (row.EntryIndex >= 0) picked.Add(row.EntryIndex);
            if (row.IsFolder) folders.Add(row.Path);
        }

        var result = EntrySelection.Expand(entries, picked);
        if (folders.Count == 0) return result;

        for (var i = 0; i < entries.Count; i++)
        {
            var path = EntrySelection.Normalize(entries[i].Name);
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (!folders.Contains(path[..slash])) continue;
                result.Add(i);
                break;
            }
        }

        return result;
    }

    public Task ExtractSelectedAsync(IReadOnlyCollection<int> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return _main.ExtractSelectedAsync(rows.Count == 0 ? [] : [.. ResolveSelection(rows)]);
    }

    public VirtualFileList? PrepareDrag(IReadOnlyCollection<int> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return _main.PrepareDrag(rows.Count == 0 ? null : [.. ResolveSelection(rows)]);
    }

    private static List<ListRow> BuildRows(IReadOnlyList<ArchiveEntry> entries)
    {
        var rows = new List<ListRow>(entries.Count);
        var explicitFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Kind == EntryKind.Directory) explicitFolders.Add(EntrySelection.Normalize(e.Name));

        var implied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var path = EntrySelection.Normalize(e.Name);
            if (path.Length == 0) continue;
            var isFolder = e.Kind == EntryKind.Directory;
            rows.Add(new ListRow(path, path[(path.LastIndexOf('/') + 1)..], TypeOf(e.Kind), isFolder, e.Index, isFolder ? 0 : (long)Math.Min(e.UncompressedSize, long.MaxValue), (long)Math.Min(e.CompressedSize, long.MaxValue), e.LastWriteLocal));

            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                var folder = path[..slash];
                if (!explicitFolders.Contains(folder) && implied.Add(folder))
                    rows.Add(new ListRow(folder, folder[(folder.LastIndexOf('/') + 1)..], "Folder", true, -1, 0, 0, null));
            }
        }

        return rows;
    }

    private static string TypeOf(EntryKind kind) => kind switch
    {
        EntryKind.Directory => "Folder",
        EntryKind.Symlink => "Link",
        EntryKind.Special => "Special",
        _ => "File",
    };

    private void Rebuild()
    {
        IEnumerable<ListRow> rows = _all;
        if (_filter.Length > 0) rows = rows.Where(r => r.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        Comparison<ListRow> byPath = (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        Comparison<ListRow> compare = _column switch
        {
            ListColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            ListColumn.Packed => (a, b) => a.Packed.CompareTo(b.Packed),
            ListColumn.Modified => (a, b) => Nullable.Compare(a.Modified, b.Modified),
            ListColumn.Type => (a, b) => string.Compare(a.Type, b.Type, StringComparison.Ordinal),
            _ => byPath,
        };

        var sorted = rows.ToList();
        sorted.Sort((a, b) =>
        {
            var c = compare(a, b);
            if (c == 0) c = byPath(a, b);
            return _ascending ? c : -c;
        });
        _view = sorted;
    }
}
