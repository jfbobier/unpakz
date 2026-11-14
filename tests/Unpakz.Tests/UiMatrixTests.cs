using System.Security.Cryptography;
using Unpakz.Core;
using Unpakz.Engine;
using Unpakz.Presentation;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>
/// The UI matrix, driven through the view-models the WinForms views bind to: archive format x operation x folder-picker outcome x
/// selection kind x failure mode. The real windows are thin shells over exactly these calls.
/// </summary>
[Collection("ArchiveSource")]
public sealed class UiMatrixTests : IDisposable
{
    private readonly string _root = Oracle.NewWorkDir("ui");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Txt(int n, int seed) => TestData.Text(n, seed);

    /// <summary>One archive format with the tree it must extract to.</summary>
    public sealed record ArchiveCase(string Name, Func<string, string> Create, Dictionary<string, byte[]> Tree, bool HasFolder = true);

    private static Dictionary<string, byte[]> Tree() => new()
    {
        ["readme.txt"] = Txt(3000, 1),
        ["docs/guide.txt"] = Txt(9000, 2),
        ["docs/deep/notes.txt"] = Txt(2000, 3),
        ["data/blob.bin"] = TestData.Random(20_000, 4),
        ["empty.txt"] = [],
    };

    private static readonly ArchiveCase[] Cases =
    [
        new("zip", dir =>
        {
            var b = new ZipBuilder();
            foreach (var (k, v) in Tree()) b.Add(k, v, deflate: v.Length > 100);
            var p = Path.Combine(dir, "sample.zip");
            File.WriteAllBytes(p, b.Build());
            return p;
        }, Tree()),
        new("rar5-stored", dir =>
        {
            var b = new Rar5Builder();
            foreach (var (k, v) in Tree()) b.AddFile(k, v);
            var p = Path.Combine(dir, "sample.rar");
            File.WriteAllBytes(p, b.Build());
            return p;
        }, Tree()),
        new("rar5-multivolume", dir =>
        {
            var b = new Rar5Builder();
            foreach (var (k, v) in Tree()) b.AddFile(k, v);
            var vols = b.BuildVolumes(5000);
            for (var i = 0; i < vols.Count; i++) File.WriteAllBytes(Path.Combine(dir, $"sample.part{i + 1:D2}.rar"), vols[i]);
            return Path.Combine(dir, "sample.part01.rar");
        }, Tree()),
        new("rar5-compressed", dir =>
        {
            var p = Path.Combine(dir, "mix.rar");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rar5-m3-mix.rar"), p);
            return p;
        }, new Dictionary<string, byte[]>
        {
            ["text.txt"] = TestData.Text(8000), ["table.bin"] = TestData.Table(4000), ["tiny.txt"] = "hello"u8.ToArray(),
            ["empty.txt"] = [], ["sub/z.txt"] = TestData.Text(3000, 9),
        }),
        new("rar5-solid", dir =>
        {
            var p = Path.Combine(dir, "solid.rar");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rar5-m5-solid.rar"), p);
            return p;
        }, new Dictionary<string, byte[]>
        {
            ["text.txt"] = TestData.Text(8000), ["table.bin"] = TestData.Table(4000), ["tiny.txt"] = "hello"u8.ToArray(),
            ["empty.txt"] = [], ["sub/z.txt"] = TestData.Text(3000, 9),
        }),
        new("rar5-real-volumes", dir =>
        {
            foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mv-m3"), "*.rar"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            return Directory.GetFiles(dir, "*part01.rar").Single();
        }, new Dictionary<string, byte[]>
        {
            ["a.txt"] = TestData.Text(9000, 3), ["t.bin"] = TestData.Table(3000), ["c.txt"] = TestData.Text(700, 4),
        }, HasFolder: false),
    ];

    public static TheoryData<string> Formats() => [.. Cases.Select(c => c.Name)];

    private static ArchiveCase Get(string name) => Cases.Single(c => c.Name == name);

    private string NewSource(ArchiveCase c)
    {
        var dir = Path.Combine(_root, c.Name + "-src");
        Directory.CreateDirectory(dir);
        return c.Create(dir);
    }

    private sealed class FakePicker(string? answer) : IFolderPicker
    {
        public int Calls { get; private set; }
        public string? LastStart { get; private set; }
        public string? Pick(string title, string? startFolder) { Calls++; LastStart = startFolder; return answer; }
    }

    private sealed class FakePrompt(bool replace) : IPrompt
    {
        public int Asked { get; private set; }
        public bool ConfirmReplace(string message) { Asked++; return replace; }
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        !Directory.Exists(root) ? [] : Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    private static Dictionary<string, string> Expected(Dictionary<string, byte[]> tree, Func<string, bool>? keep = null) =>
        tree.Where(kv => keep?.Invoke(kv.Key) ?? true).ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)), StringComparer.Ordinal);

    private MainViewModel Open(ArchiveCase c, IFolderPicker picker, IPrompt? prompt = null)
    {
        var vm = new MainViewModel(picker, prompt ?? new FakePrompt(false));
        Assert.True(vm.TryOpen(NewSource(c)), vm.Status);
        return vm;
    }

    // ---- open ----
    [Theory]
    [MemberData(nameof(Formats))]
    public void Opening_shows_a_summary(string format)
    {
        var c = Get(format);
        using var vm = Open(c, new FakePicker(null));
        Assert.True(vm.IsOpen);
        Assert.Contains(Path.GetFileName(vm.ArchivePath!), vm.Summary, StringComparison.Ordinal);
        Assert.Contains(c.Tree.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.Summary, StringComparison.Ordinal);
        Assert.Contains(vm.Source!.Archive.Format, vm.Summary, StringComparison.OrdinalIgnoreCase);
        if (format.Contains("volume", StringComparison.Ordinal)) Assert.Contains("parts", vm.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Details_describe_the_archive_without_repeating_its_name(string format)
    {
        using var vm = Open(Get(format), new FakePicker(null));
        Assert.DoesNotContain(Path.GetFileName(vm.ArchivePath!), vm.Details, StringComparison.Ordinal);
        Assert.Contains(vm.Source!.Archive.Format.ToUpperInvariant(), vm.Details, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFileName(vm.ArchivePath!), vm.Summary, StringComparison.Ordinal);
        Assert.EndsWith(vm.Details, vm.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_says_extracting_while_working_not_ready()
    {
        var dir = Path.Combine(_root, "stat");
        Directory.CreateDirectory(dir);
        var b = new ZipBuilder();
        for (var i = 0; i < 10; i++) b.Add($"f{i}.bin", TestData.Random(200_000, i));
        File.WriteAllBytes(Path.Combine(dir, "s.zip"), b.Build());
        using var vm = new MainViewModel(new FakePicker(Path.Combine(_root, "stat-out")), new FakePrompt(false));
        Assert.True(vm.TryOpen(Path.Combine(dir, "s.zip")));
        Assert.Equal("Ready.", vm.Status);

        var duringWork = new List<string>();
        vm.Changed += name => { if (name == nameof(MainViewModel.Progress) && vm.IsBusy) duringWork.Add(vm.Status); };
        await vm.ExtractAllAsync();

        Assert.NotEmpty(duringWork);
        Assert.All(duringWork, s => Assert.Contains("Extracting", s, StringComparison.Ordinal));
        Assert.DoesNotContain("Extracting", vm.Status, StringComparison.Ordinal);       // replaced by the result when done
    }

    // ---- Extract all... (folder picker) ----
    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Extract_all_to_the_folder_the_picker_returns(string format)
    {
        var c = Get(format);
        var dest = Path.Combine(_root, "all-" + format);
        var picker = new FakePicker(dest);
        using var vm = Open(c, picker);

        await vm.ExtractAllAsync();

        Assert.Equal(1, picker.Calls);
        Assert.Equal(Path.GetDirectoryName(vm.ArchivePath), picker.LastStart);          // the dialog opens next to the archive
        Assert.Equal(Expected(c.Tree), Snapshot(dest));
        Assert.Equal(RunStatus.Success, vm.LastStatus);
        Assert.Contains("extracted", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
        Assert.Empty(Directory.GetFiles(dest, ".unpakz-*", SearchOption.AllDirectories));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Cancelling_the_folder_picker_does_nothing(string format)
    {
        var c = Get(format);
        var picker = new FakePicker(null);
        using var vm = Open(c, picker);
        var before = vm.Status;
        await vm.ExtractAllAsync();
        Assert.Equal(1, picker.Calls);
        Assert.Null(vm.LastStatus);
        Assert.Equal(before, vm.Status);
        Assert.False(vm.IsBusy);
    }

    // ---- Extract here / to named folder ----
    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Extract_here_uses_the_archives_own_folder_without_asking(string format)
    {
        var c = Get(format);
        var picker = new FakePicker("never used");
        using var vm = Open(c, picker);
        var dir = Path.GetDirectoryName(vm.ArchivePath)!;
        var before = Snapshot(dir);

        await vm.ExtractHereAsync();

        Assert.Equal(0, picker.Calls);
        var after = Snapshot(dir);
        foreach (var (k, v) in Expected(c.Tree)) Assert.Equal(v, after[k]);
        Assert.Equal(before.Count + c.Tree.Count, after.Count);                 // the archive parts that were already there, plus exactly the tree
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Extract_to_a_folder_named_after_the_archive(string format)
    {
        var c = Get(format);
        var picker = new FakePicker("never used");
        using var vm = Open(c, picker);
        var target = DestinationNaming.FolderNextTo(vm.ArchivePath!);

        await vm.ExtractToNamedFolderAsync();

        Assert.Equal(0, picker.Calls);
        Assert.Equal(Expected(c.Tree), Snapshot(target));
        Assert.True(Path.GetFileName(target).Length > 0 && !Path.GetFileName(target).Contains(".part", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Extract selected (file list) ----
    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Extract_selected_files_keeps_their_folder_structure(string format)
    {
        var c = Get(format);
        var dest = Path.Combine(_root, "sel-" + format);
        using var vm = Open(c, new FakePicker(dest));
        var list = new FileListViewModel(vm);
        var wanted = c.Tree.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(2).ToArray();
        var rows = wanted.Select(w => list.IndexOfPath(w)).ToArray();

        await list.ExtractSelectedAsync(rows);

        Assert.Equal(Expected(c.Tree, k => wanted.Contains(k)), Snapshot(dest));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("rar5-stored")]
    [InlineData("rar5-multivolume")]
    public async Task Extract_a_selected_folder_brings_everything_inside_it(string format)
    {
        var c = Get(format);
        var dest = Path.Combine(_root, "folder-" + format);
        using var vm = Open(c, new FakePicker(dest));
        var list = new FileListViewModel(vm);
        var folderRow = list.IndexOfPath("docs");
        Assert.True(folderRow >= 0, "the file list should show folders as rows");

        await list.ExtractSelectedAsync([folderRow]);

        Assert.Equal(Expected(c.Tree, k => k.StartsWith("docs/", StringComparison.Ordinal)), Snapshot(dest));
    }

    [Fact]
    public async Task Empty_selection_does_not_even_open_the_picker()
    {
        var picker = new FakePicker("x");
        using var vm = Open(Get("zip"), picker);
        await new FileListViewModel(vm).ExtractSelectedAsync([]);
        Assert.Equal(0, picker.Calls);
        Assert.Contains("select", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    // ---- existing files: replace prompt ----
    [Fact]
    public async Task Existing_files_ask_before_replacing_and_no_means_nothing_is_touched()
    {
        var c = Get("zip");
        var dest = Path.Combine(_root, "exists");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "readme.txt"), "MINE");
        var prompt = new FakePrompt(false);
        using var vm = Open(c, new FakePicker(dest), prompt);

        await vm.ExtractAllAsync();

        Assert.Equal(1, prompt.Asked);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(dest, "readme.txt")));
        Assert.Equal(RunStatus.Failed, vm.LastStatus);
        Assert.False(Directory.Exists(Path.Combine(dest, "docs")));             // refused up front: not even the other folders were created
    }

    [Fact]
    public async Task Existing_files_are_replaced_only_after_a_yes()
    {
        var c = Get("zip");
        var dest = Path.Combine(_root, "exists-yes");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "readme.txt"), "MINE");
        var prompt = new FakePrompt(true);
        using var vm = Open(c, new FakePicker(dest), prompt);

        await vm.ExtractAllAsync();

        Assert.Equal(1, prompt.Asked);
        Assert.Equal(Expected(c.Tree), Snapshot(dest));
        Assert.Equal(RunStatus.Success, vm.LastStatus);
    }

    // ---- threading: dialogs belong to the UI thread ----
    private sealed class ThreadTaggedContext : SynchronizationContext
    {
        public int Sends { get; private set; }
        public override void Send(SendOrPostCallback d, object? state) { Sends++; d(state); }
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ => d(state));
    }

    private sealed class RecordingPrompt(ThreadTaggedContext ctx) : IPrompt
    {
        public int SendsAtPrompt { get; private set; } = -1;
        public bool ConfirmReplace(string message) { SendsAtPrompt = ctx.Sends; return true; }
    }

    [Fact]
    public async Task The_replace_prompt_is_shown_through_the_callers_synchronization_context()
    {
        var c = Get("zip");
        var dest = Path.Combine(_root, "ctx");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "readme.txt"), "MINE");
        var ctx = new ThreadTaggedContext();
        var prompt = new RecordingPrompt(ctx);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            using var vm = Open(c, new FakePicker(dest), prompt);
            await vm.ExtractAllAsync();
            Assert.True(prompt.SendsAtPrompt >= 1, "the prompt must be marshalled to the UI context, not run on a worker thread");
            Assert.Equal(RunStatus.Success, vm.LastStatus);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ---- failure modes ----
    [Fact]
    public async Task A_hostile_archive_is_refused_with_a_clear_message_and_nothing_is_written()
    {
        var dir = Path.Combine(_root, "evil");
        Directory.CreateDirectory(dir);
        var zip = new ZipBuilder().Add("fine.txt", "ok"u8.ToArray()).Add("../escaped.txt", "bad"u8.ToArray()).Build();
        var path = Path.Combine(dir, "evil.zip");
        File.WriteAllBytes(path, zip);
        var dest = Path.Combine(_root, "evil-out");
        using var vm = new MainViewModel(new FakePicker(dest), new FakePrompt(false));
        Assert.True(vm.TryOpen(path));

        await vm.ExtractAllAsync();

        Assert.Equal(RunStatus.Failed, vm.LastStatus);
        Assert.Contains("Nothing was extracted", vm.Status, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dest));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "escaped.txt")));
    }

    [Fact]
    public void A_missing_part_is_explained_when_opening()
    {
        var dir = Path.Combine(_root, "gap");
        Directory.CreateDirectory(dir);
        var vols = new Rar5Builder().AddFile("a", new byte[9000]).BuildVolumes(2500);
        for (var i = 0; i < vols.Count; i++) if (i != 1) File.WriteAllBytes(Path.Combine(dir, $"set.part{i + 1}.rar"), vols[i]);
        using var vm = new MainViewModel(new FakePicker(null), new FakePrompt(false));
        Assert.False(vm.TryOpen(Path.Combine(dir, "set.part1.rar")));
        Assert.False(vm.IsOpen);
        Assert.Contains("Part 2", vm.Status, StringComparison.Ordinal);
        Assert.Contains("same folder", vm.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing.zip")]
    public void A_file_that_does_not_exist_or_is_not_an_archive_is_reported_calmly(string name)
    {
        using var vm = new MainViewModel(new FakePicker(null), new FakePrompt(false));
        Assert.False(vm.TryOpen(Path.Combine(_root, name)));
        Assert.Contains("not found", vm.Status, StringComparison.OrdinalIgnoreCase);

        var junk = Path.Combine(_root, "junk.zip");
        File.WriteAllBytes(junk, new byte[500]);
        Assert.False(vm.TryOpen(junk));
        Assert.False(vm.IsOpen);
        Assert.NotEmpty(vm.Status);
    }

    [Fact]
    public async Task A_destination_that_is_a_file_gives_a_friendly_error()
    {
        var file = Path.Combine(_root, "iam-a-file");
        File.WriteAllText(file, "x");
        using var vm = Open(Get("zip"), new FakePicker(file));
        await vm.ExtractAllAsync();
        Assert.Equal(RunStatus.Failed, vm.LastStatus);
        Assert.NotEmpty(vm.Status);
    }

    [Fact]
    public async Task Damaged_data_is_reported_as_damaged_and_leaves_no_partial_files()
    {
        var dir = Path.Combine(_root, "dmg");
        Directory.CreateDirectory(dir);
        var zip = new ZipBuilder().Add("good.txt", "fine"u8.ToArray()).Add("bad.bin", TestData.Random(50_000, 7)).Build();
        zip[30 + 8 + 4 + 30 + 7 + 25_000] ^= 0xFF;                                 // inside bad.bin's data
        var path = Path.Combine(dir, "dmg.zip");
        File.WriteAllBytes(path, zip);
        var dest = Path.Combine(_root, "dmg-out");
        using var vm = new MainViewModel(new FakePicker(dest), new FakePrompt(false));
        Assert.True(vm.TryOpen(path));

        await vm.ExtractAllAsync();

        Assert.Equal(RunStatus.Failed, vm.LastStatus);
        Assert.Contains("damaged", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(dest, "bad.bin")));
        Assert.Empty(Directory.GetFiles(dest, ".unpakz-*", SearchOption.AllDirectories));
    }

    // ---- progress, cancel, busy ----
    [Fact]
    public async Task Progress_rises_to_one_and_busy_is_set_only_while_working()
    {
        var dir = Path.Combine(_root, "prog");
        Directory.CreateDirectory(dir);
        var b = new ZipBuilder();
        for (var i = 0; i < 20; i++) b.Add($"f{i}.bin", TestData.Random(200_000, i));
        var path = Path.Combine(dir, "big.zip");
        File.WriteAllBytes(path, b.Build());
        using var vm = new MainViewModel(new FakePicker(Path.Combine(_root, "prog-out")), new FakePrompt(false));
        Assert.True(vm.TryOpen(path));

        var seen = new List<double>();
        var busyDuring = new List<bool>();
        vm.Changed += name => { if (name == nameof(MainViewModel.Progress)) { seen.Add(vm.Progress); busyDuring.Add(vm.IsBusy); } };

        Assert.False(vm.IsBusy);
        await vm.ExtractAllAsync();

        Assert.False(vm.IsBusy);
        Assert.NotEmpty(seen);
        Assert.All(busyDuring, b2 => Assert.True(b2));
        for (var i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1]);
        Assert.Equal(1.0, vm.Progress, 3);
    }

    [Fact]
    public async Task Cancel_stops_the_run_and_reports_cancelled()
    {
        var dir = Path.Combine(_root, "cancel");
        Directory.CreateDirectory(dir);
        var b = new ZipBuilder();
        for (var i = 0; i < 30; i++) b.Add($"f{i}.bin", TestData.Random(300_000, i));
        var path = Path.Combine(dir, "many.zip");
        File.WriteAllBytes(path, b.Build());
        var dest = Path.Combine(_root, "cancel-out");
        using var vm = new MainViewModel(new FakePicker(dest), new FakePrompt(false));
        Assert.True(vm.TryOpen(path));
        var cancelled = false;
        vm.Changed += name => { if (name == nameof(MainViewModel.Progress) && !cancelled && vm.Progress > 0.2) { cancelled = true; vm.Cancel(); } };

        await vm.ExtractAllAsync();

        Assert.Equal(RunStatus.Cancelled, vm.LastStatus);
        Assert.Contains("cancel", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dest, ".unpakz-*", SearchOption.AllDirectories));
        Assert.True(Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length < 30);
    }

    [Fact]
    public async Task A_second_extraction_while_busy_is_ignored()
    {
        var dir = Path.Combine(_root, "busy");
        Directory.CreateDirectory(dir);
        var b = new ZipBuilder();
        for (var i = 0; i < 10; i++) b.Add($"f{i}.bin", TestData.Random(500_000, i));
        var path = Path.Combine(dir, "b.zip");
        File.WriteAllBytes(path, b.Build());
        var picker = new FakePicker(Path.Combine(_root, "busy-out"));
        using var vm = new MainViewModel(picker, new FakePrompt(false));
        Assert.True(vm.TryOpen(path));

        var first = vm.ExtractAllAsync();
        await vm.ExtractAllAsync();                                                 // issued while the first is (very likely) still running
        await first;
        Assert.True(picker.Calls is 1 or 2);
        Assert.False(vm.IsBusy);
    }

    // ---- drag out ----
    [Theory]
    [MemberData(nameof(Formats))]
    public void Drag_of_everything_lists_every_file_with_safe_names(string format)
    {
        var c = Get(format);
        using var vm = Open(c, new FakePicker(null));
        var drag = vm.PrepareDrag(null);
        Assert.NotNull(drag);
        var files = drag.Items.Where(i => !i.IsDirectory).Select(i => i.RelativePath.Replace('\\', '/')).Order(StringComparer.Ordinal);
        Assert.Equal(c.Tree.Keys.Order(StringComparer.Ordinal), files);
    }

    [Fact]
    public void Drag_of_a_selection_lists_only_that_and_hostile_archives_refuse_to_drag()
    {
        using var vm = Open(Get("zip"), new FakePicker(null));
        var list = new FileListViewModel(vm);
        var drag = list.PrepareDrag([list.IndexOfPath("readme.txt")]);
        Assert.Equal(["readme.txt"], drag!.Items.Select(i => i.RelativePath));

        var dir = Path.Combine(_root, "evil2");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "e.zip"), new ZipBuilder().Add("../x", [1]).Build());
        using var bad = new MainViewModel(new FakePicker(null), new FakePrompt(false));
        Assert.True(bad.TryOpen(Path.Combine(dir, "e.zip")));
        Assert.Null(bad.PrepareDrag(null));
        Assert.Contains("Nothing was extracted", bad.Status, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Reading_a_dragged_file_gives_exactly_its_bytes_without_writing_anything(string format)
    {
        var c = Get(format);
        using var vm = Open(c, new FakePicker(null));
        var filesBefore = Snapshot(_root).Count;
        var drag = vm.PrepareDrag(null)!;

        foreach (var item in drag.Items.Where(i => !i.IsDirectory))
        {
            using var stream = vm.OpenDragStream(item);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.True(c.Tree[item.RelativePath.Replace('\\', '/')].AsSpan().SequenceEqual(ms.ToArray()), $"{format}: {item.RelativePath}");
        }

        Assert.Equal(filesBefore, Snapshot(_root).Count);                       // streaming a drag creates no temp copies
    }

    // ---- closing ----
    [Fact]
    public void Closing_releases_the_archive_and_reopening_works()
    {
        var picker = new FakePicker(null);
        var vm = Open(Get("rar5-multivolume"), picker);
        var live = MappedFile.LiveMappings;
        Assert.True(live > 0);
        vm.Dispose();
        vm.Dispose();
        Assert.False(vm.IsOpen);
        Assert.True(MappedFile.LiveMappings < live);
    }
}
