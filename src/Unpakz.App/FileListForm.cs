using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Unpakz.Presentation;
using Unpakz.Windows;

namespace Unpakz.App;

/// <summary>
/// The file list: every file and folder in the archive. Pick any of them (picking a folder takes everything inside) and extract them to a
/// folder, or drag them onto an Explorer folder.
/// </summary>
internal sealed class FileListForm : Form
{
    private readonly MainViewModel _main;
    private readonly FileListViewModel _model;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, MultiSelect = true, HideSelection = false, AllowColumnReorder = false };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill };
    private readonly Button _extract = new() { Text = "Extract selected to…", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private ListColumn _sortColumn = ListColumn.Name;
    private bool _ascending = true;

    public FileListForm(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _model = new FileListViewModel(main);
        Text = $"Files – {System.IO.Path.GetFileName(main.ArchivePath)}";
        Name = "FileListForm"; _list.Name = "List"; _filter.Name = "Filter"; _extract.Name = "ExtractSelected"; _count.Name = "Count";
        MinimumSize = new Size(560, 360);
        ClientSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? Font;      // Segoe UI 9 on Windows 11 (net48 would otherwise default to MS Sans Serif)
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        _list.Columns.Add("Name", 320);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Modified", 130);
        _list.Columns.Add("Type", 70);
        _list.RetrieveVirtualItem += OnRetrieve;
        _list.ColumnClick += OnColumnClick;
        _list.ItemDrag += OnItemDrag;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.VirtualListSize = _model.RowCount;
        _list.Resize += (_, _) => FitNameColumn();
        Shown += (_, _) => FitNameColumn();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10, 10, 10, 6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_filter, 0, 0);
        top.Controls.Add(_extract, 1, 0);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 6, 10, 8) };
        bottom.Controls.Add(_count);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);

        _filter.TextChanged += (_, _) => { _model.Filter = _filter.Text; Refill(); };
        _extract.Click += async (_, _) => await ExtractSelectedAsync();
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _list.Focused) { e.Handled = true; await ExtractSelectedAsync(); }
            else if (e.KeyCode == Keys.Escape) Close();
        };
        _main.Changed += name => { if (name == nameof(MainViewModel.IsBusy) && IsHandleCreated && !IsDisposed) BeginInvoke(UpdateButtons); };

        UpdateButtons();
    }

    /// <summary>The Name column takes whatever width the other columns leave, so nothing needs a horizontal scrollbar.</summary>
    private void FitNameColumn()
    {
        var others = 0;
        for (var i = 1; i < _list.Columns.Count; i++) others += _list.Columns[i].Width;
        var available = _list.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth;
        _list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(160), available);
    }

    private void OnRetrieve(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var inv = CultureInfo.CurrentCulture;
        var row = _model.GetRow(e.ItemIndex);
        var item = new ListViewItem(row.Path);
        item.SubItems.Add(row.IsFolder ? string.Empty : MainViewModel.FormatSize(row.Size));
        item.SubItems.Add(row.Modified?.ToString("g", inv) ?? string.Empty);
        item.SubItems.Add(row.Type);
        e.Item = item;
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e) => SortByColumn(e.Column);

    internal void SortByColumn(int index)
    {
        var column = (ListColumn)index;
        _ascending = column == _sortColumn ? !_ascending : true;
        _sortColumn = column;
        _model.SortBy(column, _ascending);
        Refill();
    }

    private void Refill()
    {
        _list.VirtualListSize = _model.RowCount;
        _list.SelectedIndices.Clear();
        _list.Invalidate();
        UpdateButtons();
    }

    private void SelectAll()
    {
        _list.BeginUpdate();
        for (var i = 0; i < _list.VirtualListSize; i++) _list.SelectedIndices.Add(i);
        _list.EndUpdate();
    }

    private int[] Selected() => [.. _list.SelectedIndices.Cast<int>()];

    internal async System.Threading.Tasks.Task ExtractSelectedAsync() => await _model.ExtractSelectedAsync(Selected());

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        var rows = Selected();
        if (rows.Length == 0 || _main.IsBusy) return;
        var drag = _model.PrepareDrag(rows);
        if (drag is null) { MessageBox.Show(this, _main.Status, "Unpakz", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        using var data = new VirtualFileDataObject(drag.Items, _main.OpenDragStream);
        DoDragDrop(data, DragDropEffects.Copy);
    }

    private void UpdateButtons()
    {
        var n = _list.SelectedIndices.Count;
        _extract.Enabled = n > 0 && !_main.IsBusy;
        _extract.Text = n > 1 ? $"Extract {n} selected to…" : "Extract selected to…";
        _count.Text = n > 0 ? $"{n} of {_model.RowCount} selected" : $"{_model.RowCount} items";
    }
}
