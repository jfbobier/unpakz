using Unpakz.Interop;
using Unpakz.Presentation;

namespace Unpakz.Interop.Tests;

public class ViewModelTests
{
    private sealed class Picker(string? answer) : IFolderPicker
    {
        public List<string> Titles { get; } = [];
        public string? Pick(string title, string? startFolder) { Titles.Add(title); return answer; }
    }

    private sealed class Prompt(bool answer) : IPrompt
    {
        public int Asked { get; private set; }
        public bool ConfirmReplace(string message) { Asked++; return answer; }
    }

    private static MainViewModel Model(string? pick, bool replace = false) => new(new Picker(pick), new Prompt(replace));

    [Fact]
    public void Opening_shows_name_format_count_size_and_resets_on_failure()
    {
        using var d = new TempDir();
        using var m = Model(null);
        var changes = new List<string>();
        m.Changed += changes.Add;
        Assert.True(m.TryOpen(Archives.Zip(d)));
        Assert.True(m.IsOpen);
        Assert.StartsWith("sample.zip · ZIP · 5 files · ", m.Summary, StringComparison.Ordinal);
        Assert.Equal("Ready.", m.Status);
        Assert.Contains(nameof(MainViewModel.IsOpen), changes);

        Assert.False(m.TryOpen(d.File("missing.zip")));
        Assert.False(m.IsOpen);
        Assert.Contains("not found", m.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(m.TryOpen(d.Write("junk.zip", new byte[3000])));
        Assert.Contains("not supported", m.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5_000_000_000L, "4.7 GB")]
    public void Sizes_are_formatted_for_people(long bytes, string expected) => Assert.Equal(expected, MainViewModel.FormatSize(bytes));

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    [InlineData("rar")]
    public async Task Extract_all_goes_to_the_folder_the_picker_returns(string format)
    {
        using var d = new TempDir();
        var path = Archives.All().Single(a => a.Format == format).Make(d);
        using var m = Model(d.File("dest"));
        Assert.True(m.TryOpen(path));
        await m.ExtractAllAsync();
        Assert.Equal(RunStatus.Success, m.LastStatus);
        Assert.Equal(1.0, m.Progress);
        Assert.False(m.IsBusy);
        Assert.Contains("5 files extracted", m.Status, StringComparison.Ordinal);
        Archives.AssertTree(d.File("dest"), Archives.Tree);
    }

    [Fact]
    public async Task A_cancelled_picker_does_nothing()
    {
        using var d = new TempDir();
        using var m = Model(null);
        m.TryOpen(Archives.Zip(d));
        await m.ExtractAllAsync();
        Assert.Null(m.LastStatus);
        Assert.False(Directory.Exists(d.File("dest")));
    }

    [Fact]
    public async Task Extract_here_and_to_the_named_folder_use_the_archives_location()
    {
        using var d = new TempDir();
        using var m = Model(null);
        m.TryOpen(Archives.Zip(d));
        await m.ExtractToNamedFolderAsync();
        Assert.True(File.Exists(d.File("sample/readme.txt")));
        await m.ExtractHereAsync();
        Assert.True(File.Exists(d.File("readme.txt")));
    }

    [Fact]
    public async Task Existing_files_ask_first_and_a_no_leaves_everything_alone()
    {
        using var d = new TempDir();
        var path = Archives.Zip(d, tree: new() { ["a.txt"] = [1, 2, 3] });
        Directory.CreateDirectory(d.File("dest"));
        File.WriteAllBytes(d.File("dest/a.txt"), [9]);

        var no = new Prompt(false);
        using (var m = new MainViewModel(new Picker(d.File("dest")), no))
        {
            m.TryOpen(path);
            await m.ExtractAllAsync();
            Assert.Equal(1, no.Asked);
            Assert.Equal(RunStatus.Failed, m.LastStatus);
            Assert.Equal([9], File.ReadAllBytes(d.File("dest/a.txt")));
        }

        var yes = new Prompt(true);
        using (var m = new MainViewModel(new Picker(d.File("dest")), yes))
        {
            m.TryOpen(path);
            await m.ExtractAllAsync();
            Assert.Equal(RunStatus.Success, m.LastStatus);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(d.File("dest/a.txt")));
        }
    }

    [Fact]
    public async Task A_hostile_archive_fails_with_a_clear_message_and_writes_nothing()
    {
        using var d = new TempDir();
        using var m = Model(d.File("dest"));
        m.TryOpen(d.Write("evil.zip", new Unpakz.Tests.Support.ZipBuilder().Add("ok.txt", [1]).Add("../escape.txt", [2]).Build()));
        await m.ExtractAllAsync();
        Assert.Equal(RunStatus.Failed, m.LastStatus);
        Assert.Contains("outside the chosen folder", m.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(d.File("dest/ok.txt")));
    }

    [Fact]
    public async Task Selected_files_and_a_picked_folder_bring_only_what_was_chosen()
    {
        using var d = new TempDir();
        using var m = Model(d.File("dest"));
        m.TryOpen(Archives.Zip(d));
        var list = new FileListViewModel(m);
        var docs = list.IndexOfPath("docs");
        Assert.True(docs >= 0);
        await list.ExtractSelectedAsync(new[] { docs });
        Assert.True(File.Exists(d.File("dest/docs/guide.txt")) && File.Exists(d.File("dest/docs/deep/notes.txt")));
        Assert.False(File.Exists(d.File("dest/readme.txt")));
        await m.ExtractSelectedAsync(Array.Empty<int>());
        Assert.Contains("Select one or more", m.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_stops_a_running_extraction_and_reports_it()
    {
        using var d = new TempDir();
        var tree = new Dictionary<string, byte[]>();
        for (var i = 0; i < 30; i++) tree[$"f{i}.bin"] = Unpakz.Tests.Support.TestData.Random(1_000_000, i);
        using var m = Model(d.File("dest"));
        m.TryOpen(Archives.Zip(d, tree: tree));
        var started = new TaskCompletionSource();
        m.Changed += n => { if (n == nameof(MainViewModel.Progress) && m.Progress > 0) started.TrySetResult(); };
        var run = m.ExtractAllAsync();
        await Task.WhenAny(started.Task, Task.Delay(5000));
        m.Cancel();
        await run;
        Assert.True(m.LastStatus is RunStatus.Cancelled or RunStatus.Success);
        Assert.False(m.IsBusy);
        Assert.Empty(Directory.Exists(d.File("dest")) ? Directory.EnumerateFiles(d.File("dest"), ".unpakz-*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Drag_out_streams_decoded_bytes_without_touching_the_disk()
    {
        using var d = new TempDir();
        using var m = Model(null);
        m.TryOpen(Archives.Zip(d));
        var before = Directory.GetFileSystemEntries(d.Path).Length;
        var drag = m.PrepareDrag(null)!;
        Assert.NotNull(drag);
        foreach (var item in drag.Items.Where(i => !i.IsDirectory))
        {
            using var s = m.OpenDragStream(item);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal(Archives.Tree[item.RelativePath.Replace('\\', '/')], ms.ToArray());
        }

        Assert.Equal(before, Directory.GetFileSystemEntries(d.Path).Length);
    }

    [Fact]
    public void Drag_of_a_hostile_archive_is_refused_with_a_status()
    {
        using var d = new TempDir();
        using var m = Model(null);
        m.TryOpen(d.Write("evil.zip", new Unpakz.Tests.Support.ZipBuilder().Add("../x", [2]).Build()));
        Assert.Null(m.PrepareDrag(null));
        Assert.Contains("refused", m.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_list_shows_folders_sorts_filters_and_maps_selection()
    {
        using var d = new TempDir();
        using var m = Model(null);
        m.TryOpen(Archives.Zip(d));
        var list = new FileListViewModel(m);
        var paths = Enumerable.Range(0, list.RowCount).Select(i => list.GetRow(i).Path).ToList();
        Assert.Contains("docs", paths);                      // folders that exist only implicitly still get a row
        Assert.Contains("docs/deep", paths);
        Assert.Equal(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), paths);

        list.SortBy(ListColumn.Size, ascending: false);
        Assert.Equal("data/blob.bin", list.GetRow(0).Path);
        list.Filter = "NOTES";
        Assert.Equal("docs/deep/notes.txt", Assert.Single(Enumerable.Range(0, list.RowCount).Select(i => list.GetRow(i).Path)));
        list.Filter = "";
        var folderRow = list.IndexOfPath("docs/deep");
        var picked = list.ResolveSelection(new[] { folderRow });
        Assert.Equal(["docs/deep/notes.txt"], picked.Select(i => m.Source!.Entries[i].RelativePath).Where(p => p.EndsWith(".txt", StringComparison.Ordinal)));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.ResolveSelection(new[] { 999 }));
    }
}
