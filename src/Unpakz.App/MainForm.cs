using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Unpakz.Engine;
using Unpakz.Presentation;
using Unpakz.Windows;

namespace Unpakz.App;

/// <summary>
/// The main window: what you do most often, in one place. Open (or drop) an archive, then extract everything, or look at the files.
/// A tile lets you drag the whole archive's contents straight onto an Explorer folder.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MainViewModel _model;
    private readonly Label _title = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 13F), Margin = new Padding(0, 0, 0, 2) };
    private readonly ToolTip _tips = new();
    private readonly Label _subtitle = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 0) };
    private readonly Button _extractTo = new() { Text = "Extract to…", AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 10F), Padding = new Padding(12, 8, 12, 8) };
    private readonly Button _extractNamed = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 6) };
    private readonly Button _extractHere = new() { Text = "Extract here", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 6) };
    private readonly Button _showFiles = new() { Text = "Show files…", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 6) };
    private readonly Panel _dragTile = new() { Dock = DockStyle.Fill, MinimumSize = new Size(0, 64), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand, Margin = new Padding(0, 10, 0, 10) };
    private readonly Label _dragLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Drag from here onto a folder\nto extract everything", ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Height = 14 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Visible = false };
    private readonly Button _open = new() { Text = "Open another archive…", AutoSize = true };
    private readonly LinkLabel _integration = new() { Text = "Show in Open with / Default apps", AutoSize = true };
    private Point _dragStart;
    private FileListForm? _list;

    public MainForm(MainViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Text = "Unpakz";
        Name = "MainForm";
        _title.Name = "Title"; _subtitle.Name = "Subtitle"; _extractTo.Name = "ExtractTo"; _extractNamed.Name = "ExtractNamed";
        _extractHere.Name = "ExtractHere"; _showFiles.Name = "ShowFiles"; _dragTile.Name = "DragTile"; _dragLabel.Name = "DragLabel";
        _progress.Name = "Progress"; _status.Name = "Status"; _cancel.Name = "Cancel"; _open.Name = "Open"; _integration.Name = "Integration";
        MinimumSize = new Size(520, 440);
        ClientSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16), AutoSize = false, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // main action
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // secondary actions
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // drag tile takes the slack
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // progress
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // status + cancel
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // footer

        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(_subtitle, 0, 1);
        header.SizeChanged += (_, _) => _subtitle.MaximumSize = new Size(Math.Max(120, header.ClientSize.Width), 0);
        FontChanged += (_, _) => { ApplyFonts(); FitTitleHeight(); };

        // a wrapping flow: buttons keep their natural width and move to a second line when the window is narrow, never clipped
        var secondary = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        foreach (var b in new[] { _extractNamed, _extractHere, _showFiles })
        {
            b.Dock = DockStyle.None;
            b.Margin = new Padding(0, 0, 8, 8);
            secondary.Controls.Add(b);
        }

        _dragTile.Controls.Add(_dragLabel);
        var statusRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        statusRow.Controls.AddRange([_status, _cancel]);
        statusRow.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(120, statusRow.ClientSize.Width - _cancel.Width - 24), 0);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        footer.Controls.AddRange([_open, _integration]);
        _integration.Margin = new Padding(14, 7, 0, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_extractTo, 0, 1);
        root.Controls.Add(secondary, 0, 2);
        root.Controls.Add(_dragTile, 0, 3);
        root.Controls.Add(_progress, 0, 4);
        root.Controls.Add(statusRow, 0, 5);
        root.Controls.Add(footer, 0, 6);
        Controls.Add(root);

        _extractTo.Click += async (_, _) => await _model.ExtractAllAsync();
        _extractNamed.Click += async (_, _) => await _model.ExtractToNamedFolderAsync();
        _extractHere.Click += async (_, _) => await _model.ExtractHereAsync();
        _showFiles.Click += (_, _) => ShowFiles();
        _cancel.Click += (_, _) => _model.Cancel();
        _open.Click += (_, _) => PickArchive();
        _integration.LinkClicked += (_, _) => RegisterShell();

        foreach (var c in new Control[] { _dragTile, _dragLabel })
        {
            c.MouseDown += (_, e) => _dragStart = e.Location;
            c.MouseMove += OnTileMouseMove;
        }

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) OpenArchive(files[0]);
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _model.IsBusy) _model.Cancel();
            else if (e.Control && e.KeyCode == Keys.O) PickArchive();
        };

        _model.Changed += _ => { if (IsHandleCreated && !IsDisposed) BeginInvoke(Render); };
        ApplyFonts();
        FitTitleHeight();
        Render();
    }

    /// <summary>Explicit fonts are derived from the window font, so they follow system text scaling like everything else.</summary>
    private void ApplyFonts()
    {
        _title.Font = new Font("Segoe UI Semibold", Font.Size * 13F / 9F);
        _extractTo.Font = new Font("Segoe UI Semibold", Font.Size * 10F / 9F);
    }

    private void FitTitleHeight() => _title.Height = TextRenderer.MeasureText("Ag", _title.Font).Height + 6;

    /// <summary>Opens an archive (from the command line, a drop, or the Open dialog).</summary>
    public void OpenArchive(string path)
    {
        if (!_model.TryOpen(path)) { Render(); return; }
        _list?.Close();
        _list = null;
        Render();
    }

    private void PickArchive()
    {
        using var dialog = new OpenFileDialog { Title = "Open archive", Filter = "Archives (*.zip;*.rar)|*.zip;*.rar|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenArchive(dialog.FileName);
    }

    internal FileListForm? FileList => _list;

    internal void ShowFiles()
    {
        if (!_model.IsOpen) return;
        if (_list is { IsDisposed: false }) { _list.Activate(); return; }
        _list = new FileListForm(_model);
        _list.FormClosed += (_, _) => _list = null;
        _list.Show(this);
    }

    private static void RegisterShell()
    {
        try
        {
            ShellIntegration.Register(Environment.ProcessPath!);
            MessageBox.Show("Done. Open Settings > Default apps and choose Unpakz for .zip and/or .rar if you want it to open them by double-click.", "Unpakz", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(ex.Message, "Unpakz", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnTileMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_model.IsOpen || _model.IsBusy) return;
        var moved = Math.Abs(e.X - _dragStart.X) > SystemInformation.DragSize.Width / 2 || Math.Abs(e.Y - _dragStart.Y) > SystemInformation.DragSize.Height / 2;
        if (!moved) return;

        var drag = _model.PrepareDrag(null);
        if (drag is null) { Render(); return; }
        using var data = new VirtualFileDataObject(drag.Items, _model.OpenDragStream);
        DoDragDrop(data, DragDropEffects.Copy);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void Render()
    {
        var open = _model.IsOpen;
        var idle = open && !_model.IsBusy;
        _title.Text = open ? Path.GetFileName(_model.ArchivePath) : "No archive open";
        _subtitle.Text = open ? _model.Details : "Drop a .zip or .rar file here, or use Open.";
        _extractTo.Enabled = idle;
        _extractHere.Enabled = idle;
        _showFiles.Enabled = open;
        _extractNamed.Enabled = idle;
        var folder = open ? Path.GetFileName(DestinationNaming.FolderNextTo(_model.ArchivePath!)) : string.Empty;
        _extractNamed.Text = open ? $"Extract to “{Shorten(folder, 18)}\\”" : "Extract to folder";
        _tips.SetToolTip(_extractNamed, open ? DestinationNaming.FolderNextTo(_model.ArchivePath!) : string.Empty);
        _tips.SetToolTip(_title, open ? _model.ArchivePath ?? string.Empty : string.Empty);
        _dragTile.Enabled = idle;
        _dragLabel.ForeColor = idle ? SystemColors.ControlText : SystemColors.GrayText;
        _progress.Value = (int)Math.Clamp(_model.Progress * 1000, 0, 1000);
        _progress.Visible = _model.IsBusy || _model.Progress > 0;
        _status.Text = _model.Status;
        _status.ForeColor = _model.LastStatus == RunStatus.Failed ? Color.Firebrick : SystemColors.ControlText;
        _cancel.Visible = _model.IsBusy;
        _open.Enabled = !_model.IsBusy;
    }
}
