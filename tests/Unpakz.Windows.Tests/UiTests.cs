using System.Drawing;
using System.Windows.Forms;
using Unpakz.App;
using Unpakz.Engine;
using Unpakz.Presentation;
using Unpakz.Tests.Support;

namespace Unpakz.Windows.Tests;

/// <summary>The real windows, built and driven on an STA thread: layout matrix with screenshots, and genuine button clicks.</summary>
public sealed class UiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ui-" + Guid.NewGuid().ToString("N")[..8]);

    public UiTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string NewSource(Archives.Case c)
    {
        var dir = Path.Combine(_root, c.Name + "-src-" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(dir);
        return c.Create(dir);
    }

    // ---------------------------------------------------------------- layout matrix
    public static TheoryData<string, double, int> MainMatrix()
    {
        var data = new TheoryData<string, double, int>();
        foreach (var state in new[] { "empty", "open", "volumes", "busy", "failed", "longname" })
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                foreach (var size in new[] { 0, 1, 2 })
                    data.Add(state, scale, size);
        return data;
    }

    private static readonly string[] ExtractButtons = ["ExtractTo", "ExtractNamed", "ExtractHere", "ShowFiles"];

    private static readonly Size[] Sizes = [new(520, 440), new(560, 460), new(900, 620)];

    [Theory]
    [MemberData(nameof(MainMatrix))]
    public void Main_window_lays_out_cleanly(string state, double scale, int sizeIndex)
    {
        var problems = Sta.Run(() =>
        {
            var gated = new GatedFileSystem(new LocalFileSystem());
            var picker = new FixedPicker(Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N")[..6]));
            using var vm = new MainViewModel(picker, new FixedPrompt(false), null, state == "busy" ? gated : null);
            using var form = new MainForm(vm);
            Ui.Place(form, scale, Sizes[sizeIndex]);

            switch (state)
            {
                case "open":
                    form.OpenArchive(NewSource(Archives.Get("zip")));
                    break;
                case "volumes":
                    form.OpenArchive(NewSource(Archives.Get("rar5-multivolume")));
                    break;
                case "longname":
                {
                    var dir = Path.Combine(_root, "ln-" + Guid.NewGuid().ToString("N")[..4]);
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, new string('x', 90) + " a very long archive name with spaces.zip");
                    File.WriteAllBytes(p, new ZipBuilder().Add("a", [1]).Build());
                    form.OpenArchive(p);
                    break;
                }
                case "failed":
                {
                    var dir = Path.Combine(_root, "bad-" + Guid.NewGuid().ToString("N")[..4]);
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, "evil.zip");
                    File.WriteAllBytes(p, new ZipBuilder().Add("ok", [1]).Add("../escape", [2]).Build());
                    form.OpenArchive(p);
                    Ui.Find<Button>(form, "ExtractTo").PerformClick();
                    Sta.Pump(() => vm.LastStatus == RunStatus.Failed);
                    break;
                }
                case "busy":
                {
                    var b = new ZipBuilder();
                    for (var i = 0; i < 6; i++) b.Add($"f{i}.bin", TestData.Random(300_000, i));
                    var dir = Path.Combine(_root, "busy-" + Guid.NewGuid().ToString("N")[..4]);
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, "busy.zip");
                    File.WriteAllBytes(p, b.Build());
                    form.OpenArchive(p);
                    Ui.Find<Button>(form, "ExtractTo").PerformClick();
                    Sta.Pump(() => vm.IsBusy && vm.Progress > 0);
                    Sta.Settle();
                    break;
                }
            }

            Sta.Settle();
            var found = Ui.LayoutProblems(form);
            Ui.Screenshot(form, $"main-{state}-{scale:0.00}-{sizeIndex}");

            if (state == "busy")
            {
                Assert.False(Ui.Find<Button>(form, "ExtractTo").Enabled);
                Assert.True(Ui.Find<Button>(form, "Cancel").Visible);
                Assert.True(Ui.Find<ProgressBar>(form, "Progress").Visible);
                gated.Gate.Set();
                Sta.Pump(() => !vm.IsBusy);
            }

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_empty_window_invites_a_drop_and_disables_the_extract_buttons()
    {
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            Assert.Equal("No archive open", Ui.Find<Label>(form, "Title").Text);
            Assert.Contains("Drop", Ui.Find<Label>(form, "Subtitle").Text, StringComparison.Ordinal);
            foreach (var n in ExtractButtons) Assert.False(Ui.Find<Button>(form, n).Enabled, n);
            Assert.True(Ui.Find<Button>(form, "Open").Enabled);
            Assert.True(form.AllowDrop);
            Assert.False(Ui.Find<ProgressBar>(form, "Progress").Visible);           // nothing is running
            Assert.False(Ui.Find<Button>(form, "Cancel").Visible);
        });
    }

    // ---------------------------------------------------------------- real clicks, every format
    [Theory]
    [MemberData(nameof(ArchivesNames))]
    public void Extract_to_button_extracts_everything_and_reports_it(string format)
    {
        var c = Archives.Get(format);
        var dest = Path.Combine(_root, "to-" + format);
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(dest), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            form.OpenArchive(NewSource(c));
            Assert.Contains(Path.GetFileName(vm.ArchivePath!), Ui.Find<Label>(form, "Title").Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFileName(vm.ArchivePath!), Ui.Find<Label>(form, "Subtitle").Text, StringComparison.Ordinal);   // the name is shown once
            Assert.Contains(vm.Source!.Archive.Format.ToUpperInvariant(), Ui.Find<Label>(form, "Subtitle").Text, StringComparison.Ordinal);

            Ui.Find<Button>(form, "ExtractTo").PerformClick();
            Sta.Pump(() => vm.LastStatus is not null && !vm.IsBusy);
            Sta.Settle();

            Assert.Equal(RunStatus.Success, vm.LastStatus);
            Assert.Contains("extracted", Ui.Find<Label>(form, "Status").Text, StringComparison.Ordinal);
            Assert.True(Ui.Find<Button>(form, "ExtractTo").Enabled);
            Assert.False(Ui.Find<Button>(form, "Cancel").Visible);
        });
        Assert.Equal(Archives.Expected(c.Tree), Archives.Snapshot(dest));
    }

    public static TheoryData<string> ArchivesNames() => Archives.Names();

    [Theory]
    [MemberData(nameof(ArchivesNames))]
    public void Extract_to_named_folder_and_here_buttons_work(string format)
    {
        var c = Archives.Get(format);
        string named = "", archiveDir = "";
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker("unused"), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            form.OpenArchive(NewSource(c));
            named = DestinationNaming.FolderNextTo(vm.ArchivePath!);
            archiveDir = Path.GetDirectoryName(vm.ArchivePath)!;
            Assert.Contains(Path.GetFileName(named), Ui.Find<Button>(form, "ExtractNamed").Text, StringComparison.Ordinal);

            Ui.Find<Button>(form, "ExtractNamed").PerformClick();
            Sta.Pump(() => vm.LastStatus is not null && !vm.IsBusy);
            Assert.Equal(RunStatus.Success, vm.LastStatus);
        });
        Assert.Equal(Archives.Expected(c.Tree), Archives.Snapshot(named));

        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker("unused"), new FixedPrompt(true));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            form.OpenArchive(Directory.GetFiles(archiveDir).OrderBy(f => f, StringComparer.Ordinal).First());
            Ui.Find<Button>(form, "ExtractHere").PerformClick();
            Sta.Pump(() => vm.LastStatus is not null && !vm.IsBusy);
            Assert.Equal(RunStatus.Success, vm.LastStatus);
        });
        foreach (var (k, v) in Archives.Expected(c.Tree)) Assert.Equal(v, Archives.Snapshot(archiveDir)[k]);
    }

    [Fact]
    public void Cancel_button_and_escape_stop_a_running_extraction()
    {
        var dest = Path.Combine(_root, "cancel-out");
        Sta.Run(() =>
        {
            var gated = new GatedFileSystem(new LocalFileSystem());
            using var vm = new MainViewModel(new FixedPicker(dest), new FixedPrompt(false), null, gated);
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            var b = new ZipBuilder();
            for (var i = 0; i < 8; i++) b.Add($"f{i}.bin", TestData.Random(300_000, i));
            var dir = Path.Combine(_root, "cancel-src");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "c.zip"), b.Build());
            form.OpenArchive(Path.Combine(dir, "c.zip"));

            Ui.Find<Button>(form, "ExtractTo").PerformClick();
            Sta.Pump(() => vm.IsBusy && vm.Progress > 0);
            Ui.Find<Button>(form, "Cancel").PerformClick();
            gated.Gate.Set();
            Sta.Pump(() => !vm.IsBusy);
            Sta.Settle();                                    // the label is refreshed by a queued message
            Assert.Equal(RunStatus.Cancelled, vm.LastStatus);
            Assert.Contains("cancel", Ui.Find<Label>(form, "Status").Text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(Directory.GetFiles(dest, ".unpakz-*", SearchOption.AllDirectories));

        Sta.Run(() =>
        {
            var gated = new GatedFileSystem(new LocalFileSystem());
            using var vm = new MainViewModel(new FixedPicker(Path.Combine(_root, "esc-out")), new FixedPrompt(false), null, gated);
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            var b = new ZipBuilder();
            for (var i = 0; i < 8; i++) b.Add($"f{i}.bin", TestData.Random(300_000, i));
            var dir = Path.Combine(_root, "esc-src");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "e.zip"), b.Build());
            form.OpenArchive(Path.Combine(dir, "e.zip"));
            Ui.Find<Button>(form, "ExtractTo").PerformClick();
            Sta.Pump(() => vm.IsBusy && vm.Progress > 0);
            FormReflection.Raise(form, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            gated.Gate.Set();
            Sta.Pump(() => !vm.IsBusy);
            Assert.Equal(RunStatus.Cancelled, vm.LastStatus);
        });
    }

    [Fact]
    public void Dropping_an_archive_file_on_the_window_opens_it()
    {
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            var path = NewSource(Archives.Get("zip"));
            var data = new DataObject(DataFormats.FileDrop, new[] { path });

            var enter = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            FormReflection.Raise(form, "OnDragEnter", enter);
            Assert.Equal(DragDropEffects.Copy, enter.Effect);

            FormReflection.Raise(form, "OnDragDrop", new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy));
            Assert.True(vm.IsOpen);
            Assert.Equal(Path.GetFileName(path), Ui.Find<Label>(form, "Title").Text);

            var other = new DataObject(DataFormats.Text, "hello");
            var no = new DragEventArgs(other, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            FormReflection.Raise(form, "OnDragEnter", no);
            Assert.Equal(DragDropEffects.None, no.Effect);
        });
    }

    [Fact]
    public void Opening_a_broken_file_shows_a_calm_message_and_keeps_the_window_usable()
    {
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            var junk = Path.Combine(_root, "junk.zip");
            File.WriteAllBytes(junk, new byte[300]);
            form.OpenArchive(junk);
            Assert.False(vm.IsOpen);
            Assert.NotEmpty(Ui.Find<Label>(form, "Status").Text);
            Assert.True(Ui.Find<Button>(form, "Open").Enabled);
            form.OpenArchive(NewSource(Archives.Get("zip")));                         // and recovers on the next open
            Assert.True(vm.IsOpen);
            Assert.True(Ui.Find<Button>(form, "ExtractTo").Enabled);
        });
    }

    // ---------------------------------------------------------------- the file list
    public static TheoryData<string, double, int> ListMatrix()
    {
        var data = new TheoryData<string, double, int>();
        foreach (var state in new[] { "few", "many", "filtered", "selected", "sorted", "longpaths" })
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                foreach (var size in new[] { 0, 2 })
                    data.Add(state, scale, size);
        return data;
    }

    private static readonly Size[] ListSizes = [new(560, 360), new(760, 520), new(1100, 700)];

    [Theory]
    [MemberData(nameof(ListMatrix))]
    public void File_list_lays_out_cleanly(string state, double scale, int sizeIndex)
    {
        var problems = Sta.Run(() =>
        {
            var b = new ZipBuilder();
            var count = state == "many" ? 50_000 : 40;
            for (var i = 0; i < count; i++)
                b.Add(state == "longpaths" ? $"{new string('d', 60)}/{new string('e', 60)}/file number {i} with a long name {new string('n', 40)}.txt" : $"folder{i % 7}/item{i:D5}.txt", new byte[i % 50]);
            var dir = Path.Combine(_root, "lst-" + Guid.NewGuid().ToString("N")[..4]);
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "list.zip");
            File.WriteAllBytes(p, b.Build());

            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            Assert.True(vm.TryOpen(p), vm.Status);
            using var main = new MainForm(vm);
            using var form = new FileListForm(vm);
            Ui.Place(form, scale, ListSizes[sizeIndex]);

            var list = Ui.Find<ListView>(form, "List");
            Assert.True(list.VirtualListSize >= count);
            if (state == "filtered") Ui.Find<TextBox>(form, "Filter").Text = "item0001";
            if (state == "selected") for (var i = 0; i < Math.Min(8, list.VirtualListSize); i++) list.SelectedIndices.Add(i);
            if (state == "sorted") { form.SortByColumn(1); form.SortByColumn(1); }
            Sta.Settle();

            var found = Ui.LayoutProblems(form);
            Ui.Screenshot(form, $"list-{state}-{scale:0.00}-{sizeIndex}");
            if (state == "filtered") Assert.True(list.VirtualListSize is > 0 and < 100, $"filter left {list.VirtualListSize} rows");
            if (state == "selected") Assert.Contains("selected", Ui.Find<Label>(form, "Count").Text, StringComparison.Ordinal);
            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(ArchivesNames))]
    public void Selecting_files_in_the_list_and_extracting_them_keeps_folder_structure(string format)
    {
        var c = Archives.Get(format);
        var dest = Path.Combine(_root, "sel-" + format);
        var wanted = c.Tree.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(2).ToArray();
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(dest), new FixedPrompt(false));
            Assert.True(vm.TryOpen(NewSource(c)), vm.Status);
            using var main = new MainForm(vm);
            using var form = new FileListForm(vm);
            Ui.Place(form, 1.0, ListSizes[1]);
            var list = Ui.Find<ListView>(form, "List");
            var button = Ui.Find<Button>(form, "ExtractSelected");
            Assert.False(button.Enabled);                                              // nothing picked yet

            for (var i = 0; i < list.VirtualListSize; i++)
                if (wanted.Contains(list.RetrieveText(i))) list.SelectedIndices.Add(i);
            Sta.Settle();
            Assert.True(button.Enabled);
            Assert.Contains("2", button.Text, StringComparison.Ordinal);

            button.PerformClick();
            Sta.Pump(() => vm.LastStatus is not null && !vm.IsBusy);
            Assert.Equal(RunStatus.Success, vm.LastStatus);
        });
        Assert.Equal(Archives.Expected(c.Tree, wanted.Contains), Archives.Snapshot(dest));
    }

    [Fact]
    public void Picking_a_folder_row_extracts_everything_inside_it_and_sorting_filtering_behave()
    {
        var c = Archives.Get("zip");
        var dest = Path.Combine(_root, "folder-pick");
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(dest), new FixedPrompt(false));
            Assert.True(vm.TryOpen(NewSource(c)), vm.Status);
            using var main = new MainForm(vm);
            using var form = new FileListForm(vm);
            Ui.Place(form, 1.0, ListSizes[1]);
            var list = Ui.Find<ListView>(form, "List");

            var rows = Enumerable.Range(0, list.VirtualListSize).Select(list.RetrieveText).ToList();
            Assert.Contains("docs", rows);                                             // an implied folder is listed
            list.SelectedIndices.Add(rows.IndexOf("docs"));
            Ui.Find<Button>(form, "ExtractSelected").PerformClick();
            Sta.Pump(() => vm.LastStatus is not null && !vm.IsBusy);

            form.SortByColumn(1);                                                      // by size, ascending
            var sizes = Enumerable.Range(0, list.VirtualListSize).Select(i => list.RetrieveItem(i).SubItems[1].Text).ToList();
            Assert.Equal("", sizes[0]);                                                // folders (no size) first
            form.SortByColumn(1);                                                      // descending
            Assert.NotEqual(rows[0], list.RetrieveText(0));

            Ui.Find<TextBox>(form, "Filter").Text = "deep";
            Assert.Equal(["docs/deep", "docs/deep/notes.txt"], Enumerable.Range(0, list.VirtualListSize).Select(list.RetrieveText).Order(StringComparer.Ordinal));
            Ui.Find<TextBox>(form, "Filter").Text = "no such thing";
            Assert.Equal(0, list.VirtualListSize);
            Assert.Contains("0 items", Ui.Find<Label>(form, "Count").Text, StringComparison.Ordinal);
        });
        Assert.Equal(Archives.Expected(c.Tree, k => k.StartsWith("docs/", StringComparison.Ordinal)), Archives.Snapshot(dest));
    }

    [Fact]
    public void Show_files_opens_one_list_window_and_closing_the_archive_window_content_is_safe()
    {
        Sta.Run(() =>
        {
            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            using var form = new MainForm(vm);
            Ui.Place(form, 1.0, Sizes[1]);
            form.OpenArchive(NewSource(Archives.Get("zip")));
            Assert.Null(form.FileList);
            form.ShowFiles();
            var first = form.FileList;
            Assert.NotNull(first);
            form.ShowFiles();
            Assert.Same(first, form.FileList);                                          // not a second window

            form.OpenArchive(NewSource(Archives.Get("rar5-stored")));                   // opening another archive closes the old list
            Sta.Settle();
            Assert.True(first!.IsDisposed || !first.Visible);
            Assert.Null(form.FileList);
        });
    }
}

internal static class ListViewExtensions
{
    public static string RetrieveText(this ListView list, int index) => list.RetrieveItem(index).Text;

    public static ListViewItem RetrieveItem(this ListView list, int index)
    {
        var e = new RetrieveVirtualItemEventArgs(index);
        typeof(ListView).GetMethod("OnRetrieveVirtualItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(list, [e]);
        return e.Item!;
    }
}
