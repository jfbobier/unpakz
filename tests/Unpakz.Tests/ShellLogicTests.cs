using System.Buffers.Binary;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

public class FriendlyErrorsTests
{
    [Fact]
    public void Missing_volume_names_the_part_and_tells_what_to_do()
    {
        var text = FriendlyErrors.Describe(new ArchiveException("x", ArchiveError.MissingVolume) { Volume = 3 });
        Assert.Contains("part 3", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same folder", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ArchiveError.Corrupt, "damaged")]
    [InlineData(ArchiveError.Unsupported, "not supported")]
    [InlineData(ArchiveError.LimitExceeded, "limit")]
    [InlineData(ArchiveError.UnsafePath, "Nothing was extracted")]
    [InlineData(ArchiveError.Conflict, "already exists")]
    public void Each_archive_error_gets_a_plain_sentence(ArchiveError kind, string expected) =>
        Assert.Contains(expected, FriendlyErrors.Describe(new ArchiveException("detail here", kind)), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_technical_detail_is_kept_but_never_a_stack_trace()
    {
        var text = FriendlyErrors.Describe(new ArchiveException("CRC-32 mismatch.", ArchiveError.Corrupt));
        Assert.Contains("CRC-32 mismatch", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_system_errors_are_translated()
    {
        Assert.Contains("not found", FriendlyErrors.Describe(new FileNotFoundException("x", "C:\\a.zip")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access was denied", FriendlyErrors.Describe(new UnauthorizedAccessException("xyz")), StringComparison.Ordinal);
        Assert.Contains("cancel", FriendlyErrors.Describe(new OperationCanceledException()), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The disk is full", FriendlyErrors.Describe(new IOException("There is not enough space on the disk.")), StringComparison.Ordinal);
        Assert.Contains("A file operation failed", FriendlyErrors.Describe(new IOException("sharing violation")), StringComparison.Ordinal);
        Assert.Contains("InvalidOperation", FriendlyErrors.Describe(new InvalidOperationException("boom")), StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_messages_cannot_reach_the_screen_and_long_ones_are_cut()
    {
        var text = FriendlyErrors.Describe(new ArchiveException("evil\u0007\u001b[31m name\r\nnext", ArchiveError.Corrupt));
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain('\u001b', text);
        Assert.True(FriendlyErrors.Describe(new ArchiveException(new string('x', 5000), ArchiveError.Corrupt)).Length < 800);
    }
}

public class DestinationNamingTests
{
    private static string Name(string archive) => Path.GetFileName(DestinationNaming.FolderNextTo(Path.Combine(Path.GetTempPath(), archive)));

    [Theory]
    [InlineData("photos.zip", "photos")]
    [InlineData("PHOTOS.ZIP", "PHOTOS")]
    [InlineData("backup.rar", "backup")]
    [InlineData("set.part01.rar", "set")]
    [InlineData("set.PART3.RAR", "set")]
    [InlineData("a.b.c.zip", "a.b.c")]
    [InlineData("my docs 2024.zip", "my docs 2024")]
    public void Strips_archive_extensions_and_part_numbers(string archive, string folder) => Assert.Equal(folder, Name(archive));

    [Theory]
    [InlineData(".zip")]
    [InlineData("   .rar")]
    [InlineData("CON.zip")]
    [InlineData("aux.rar")]
    [InlineData("trailing..zip")]
    public void Unusable_results_fall_back_to_a_safe_name(string archive)
    {
        var n = Name(archive);
        Assert.True(PathGuard.Validate(n).IsSafe, $"'{n}' is not a safe folder name");
    }

    [Fact]
    public void A_name_equal_to_the_archive_itself_gets_a_suffix_so_it_cannot_collide_with_the_file()
    {
        var folder = Name("README");
        Assert.NotEqual("README", folder);
        Assert.StartsWith("README", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_sits_next_to_the_archive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "somewhere");
        Assert.Equal(Path.Combine(dir, "x"), DestinationNaming.FolderNextTo(Path.Combine(dir, "x.zip")));
        Assert.Equal(dir, DestinationNaming.DirectoryOf(Path.Combine(dir, "x.zip")));
    }
}

public class VirtualFileListTests
{
    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static ZipArchive Sample() => ZipArchive.Open(new ZipBuilder()
        .AddDirectory("d").Add("d/a.txt", B("AAA")).AddDirectory("d/e").Add("d/e/b.bin", new byte[10]).Add("top.txt", B("T")).Build());

    [Fact]
    public void Lists_everything_by_default_with_windows_separators()
    {
        var list = VirtualFileList.Build(Sample());
        Assert.Equal(["d", "d\\a.txt", "d\\e", "d\\e\\b.bin", "top.txt"], list.Items.Select(i => i.RelativePath));
        Assert.Equal([true, false, true, false, false], list.Items.Select(i => i.IsDirectory));
        Assert.Equal([0L, 3, 0, 10, 1], list.Items.Select(i => i.Size));
        Assert.Empty(list.Skipped);
    }

    [Fact]
    public void A_selection_limits_the_list_and_a_folder_brings_its_contents()
    {
        var a = Sample();
        var d = a.Entries.Single(e => e.Name == "d/").Index;
        var top = a.Entries.Single(e => e.Name == "top.txt").Index;
        Assert.Equal(["d", "d\\a.txt", "d\\e", "d\\e\\b.bin"], VirtualFileList.Build(a, [d]).Items.Select(i => i.RelativePath));
        Assert.Equal(["top.txt"], VirtualFileList.Build(a, [top]).Items.Select(i => i.RelativePath));
    }

    [Fact]
    public void Hostile_names_refuse_the_whole_drag()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("ok", B("1")).Add("../evil", B("2")).Build());
        Assert.Equal(ArchiveError.UnsafePath, Assert.Throws<ArchiveException>(() => VirtualFileList.Build(a)).Error);
    }

    [Fact]
    public void Links_are_skipped_and_reported_unsupported_entries_refuse_the_drag()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("l", B("x"), externalAttrs: ZipBuilder.SymlinkAttr).Add("f", B("1")).Build());
        var list = VirtualFileList.Build(a);
        Assert.Equal(["f"], list.Items.Select(i => i.RelativePath));
        Assert.Single(list.Skipped);

        var zip = new ZipBuilder().Add("enc", B("1")).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(zip.Length - 22 + 16 - 0), BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(zip.Length - 22 + 16)));
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(cd + 10), 99);
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => VirtualFileList.Build(ZipArchive.Open(zip))).Error);
    }

    [Fact]
    public void A_259_character_path_can_be_dragged_and_a_260_character_path_cannot()
    {
        static ZipArchive WithPath(int length) => ZipArchive.Open(new ZipBuilder().Add(new string('a', 100) + "/" + new string('b', 100) + "/" + new string('c', length - 202), B("1")).Build());
        Assert.Equal(259, Assert.Single(VirtualFileList.Build(WithPath(259)).Items).RelativePath.Length);
        Assert.Equal(ArchiveError.LimitExceeded, Assert.Throws<ArchiveException>(() => VirtualFileList.Build(WithPath(260))).Error);
    }

    [Fact]
    public void Names_too_long_for_a_shell_descriptor_refuse_the_drag_with_advice()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add(string.Join("/", Enumerable.Repeat(new string('a', 100), 3)) + ".txt", B("1")).Build());
        var ex = Assert.Throws<ArchiveException>(() => VirtualFileList.Build(a));
        Assert.Equal(ArchiveError.LimitExceeded, ex.Error);
        Assert.Contains("Extract to", ex.Message, StringComparison.Ordinal);
    }

    // ---- FILEGROUPDESCRIPTORW bytes ----
    [Fact]
    public void Descriptor_bytes_follow_the_windows_layout()
    {
        var list = VirtualFileList.Build(Sample());
        var bytes = FileGroupDescriptor.Serialize(list.Items);
        Assert.Equal(4 + (592 * list.Items.Count), bytes.Length);
        Assert.Equal((uint)list.Items.Count, BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        for (var i = 0; i < list.Items.Count; i++)
        {
            var d = bytes.AsSpan(4 + (592 * i), 592);
            var item = list.Items[i];
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
            var attrs = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
            var high = BinaryPrimitives.ReadUInt32LittleEndian(d[64..]);
            var low = BinaryPrimitives.ReadUInt32LittleEndian(d[68..]);
            var name = System.Text.Encoding.Unicode.GetString(d.Slice(72, 520)).TrimEnd('\0');

            Assert.Equal(item.RelativePath, name);
            Assert.NotEqual(0u, flags & 0x04);                                   // FD_ATTRIBUTES
            Assert.NotEqual(0u, flags & 0x4000);                                 // FD_PROGRESSUI
            Assert.Equal(item.IsDirectory, (attrs & 0x10) != 0);                 // FILE_ATTRIBUTE_DIRECTORY
            if (item.IsDirectory) { Assert.Equal(0u, flags & 0x40); }
            else
            {
                Assert.NotEqual(0u, flags & 0x40);                               // FD_FILESIZE
                Assert.Equal((ulong)item.Size, ((ulong)high << 32) | low);
                Assert.NotEqual(0u, flags & 0x20);                               // FD_WRITESTIME (the sample archive has times)
                var ft = BinaryPrimitives.ReadInt64LittleEndian(d[56..]);
                Assert.Equal(item.LastWriteLocal!.Value.ToFileTimeUtc(), ft);
            }
        }
    }

    [Fact]
    public void Missing_times_clear_the_write_time_flag_and_big_sizes_split_into_two_words()
    {
        var item = new VirtualFile("big.bin", false, (5L << 32) + 7, null, 0);
        var d = FileGroupDescriptor.Serialize([item]).AsSpan(4);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(d) & 0x20);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(d[64..]));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(d[68..]));
    }

    [Fact]
    public void A_259_character_name_fits_and_a_260_character_name_is_refused()
    {
        var ok = new VirtualFile(new string('n', 259), false, 1, null, 0);
        var bytes = FileGroupDescriptor.Serialize([ok]);
        Assert.Equal(new string('n', 259), System.Text.Encoding.Unicode.GetString(bytes.AsSpan(4 + 72, 520)).TrimEnd('\0'));
        Assert.Throws<ArchiveException>(() => FileGroupDescriptor.Serialize([new VirtualFile(new string('n', 260), false, 1, null, 0)]));
    }

    [Fact]
    public void An_empty_list_serialises_to_a_zero_count()
    {
        Assert.Equal([0, 0, 0, 0], FileGroupDescriptor.Serialize([]));
    }
}

public class FileAssociationPlanTests
{
    private const string Exe = "C:\\Program Files\\Unpakz\\Unpakz.exe";

    [Fact]
    public void The_plan_registers_the_app_for_zip_and_rar_without_touching_defaults()
    {
        var plan = FileAssociationPlan.Build(Exe);
        string V(string key, string? name = null) => plan.Writes.Single(w => w.Key == key && w.Name == name).Value;

        Assert.Equal($"\"{Exe}\" \"%1\"", V(@"Software\Classes\Unpakz.Archive\shell\open\command"));
        Assert.Equal($"\"{Exe}\",0", V(@"Software\Classes\Unpakz.Archive\DefaultIcon"));
        Assert.Equal($"\"{Exe}\" \"%1\"", V(@"Software\Classes\Applications\Unpakz.exe\shell\open\command"));
        Assert.Equal("Unpakz.Archive", V(@"Software\Unpakz\Capabilities\FileAssociations", ".zip"));
        Assert.Equal("Unpakz.Archive", V(@"Software\Unpakz\Capabilities\FileAssociations", ".rar"));
        Assert.Equal(@"Software\Unpakz\Capabilities", V(@"Software\RegisteredApplications", "Unpakz"));
        Assert.Equal("", V(@"Software\Classes\.zip\OpenWithProgids", "Unpakz.Archive"));
        Assert.Equal("", V(@"Software\Classes\.rar\OpenWithProgids", "Unpakz.Archive"));
        Assert.Equal("", V(@"Software\Classes\Applications\Unpakz.exe\SupportedTypes", ".rar"));

        // never writes the keys Windows protects for the user's choice of default
        Assert.DoesNotContain(plan.Writes, w => w.Key.Contains("FileExts", StringComparison.OrdinalIgnoreCase) || w.Key.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Writes, w => w.Key is @"Software\Classes\.zip" or @"Software\Classes\.rar" && w.Name is null);
    }

    [Fact]
    public void Every_key_is_under_hkcu_so_no_administrator_rights_are_needed()
    {
        foreach (var w in FileAssociationPlan.Build(Exe).Writes) Assert.StartsWith("Software\\", w.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_and_removing_round_trip_on_a_registry()
    {
        var reg = new InMemoryRegistry();
        reg.SetValue(@"Software\Unrelated", "keep", "me");
        var plan = FileAssociationPlan.Build(Exe);

        plan.Apply(reg);
        plan.Apply(reg);                                                         // idempotent
        Assert.Equal("Unpakz.Archive", reg.Get(@"Software\Unpakz\Capabilities\FileAssociations", ".rar"));

        FileAssociationPlan.Remove(reg);
        Assert.Equal("me", reg.Get(@"Software\Unrelated", "keep"));              // untouched
        Assert.Null(reg.Get(@"Software\Unpakz\Capabilities\FileAssociations", ".rar"));
        Assert.Null(reg.Get(@"Software\Classes\.zip\OpenWithProgids", "Unpakz.Archive"));
        Assert.Null(reg.Get(@"Software\RegisteredApplications", "Unpakz"));
        Assert.False(reg.AnyKeyStartsWith(@"Software\Classes\Unpakz.Archive"));
        Assert.False(reg.AnyKeyStartsWith(@"Software\Classes\Applications\Unpakz.exe"));
    }

    [Fact]
    public void Odd_exe_paths_are_quoted_and_rejected_when_dangerous()
    {
        Assert.Contains("\"C:\\a b\\x.exe\" \"%1\"", FileAssociationPlan.Build("C:\\a b\\x.exe").Writes.Select(w => w.Value));
        Assert.Throws<ArgumentException>(() => FileAssociationPlan.Build("C:\\a\"b\\x.exe"));
        Assert.Throws<ArgumentException>(() => FileAssociationPlan.Build(""));
        Assert.Throws<ArgumentException>(() => FileAssociationPlan.Build("relative\\x.exe"));
    }
}

internal sealed class InMemoryRegistry : IRegistryWriter
{
    private readonly Dictionary<(string Key, string Name), string> _values = [];
    private readonly HashSet<string> _keys = [];

    public void SetValue(string key, string? name, string value)
    {
        _keys.Add(key);
        _values[(key, name ?? string.Empty)] = value;
    }

    public string? Get(string key, string? name) => _values.TryGetValue((key, name ?? string.Empty), out var v) ? v : null;

    public void DeleteValue(string key, string name) => _values.Remove((key, name));

    public void DeleteKeyTree(string key)
    {
        foreach (var k in _keys.Where(k => k == key || k.StartsWith(key + "\\", StringComparison.Ordinal)).ToArray())
        {
            _keys.Remove(k);
            foreach (var v in _values.Keys.Where(v => v.Key == k).ToArray()) _values.Remove(v);
        }
    }

    public bool AnyKeyStartsWith(string prefix) => _keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
}

public class ExtractionRunnerTests
{
    private static readonly string Dest = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "dest"));
    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Success_reports_counts_in_plain_words()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("a", B("1")).Add("b/c", B("22")).AddDirectory("e").Build());
        var fs = new InMemoryFileSystem(Dest);
        var outcome = await ExtractionRunner.RunAsync(a, Dest, fileSystem: fs);
        Assert.Equal(RunStatus.Success, outcome.Status);
        Assert.Contains("2 files", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(3, outcome.Result!.Files + outcome.Result.Directories);
    }

    [Fact]
    public async Task One_file_is_singular_and_skipped_links_are_mentioned()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("a", B("1")).Add("l", B("x"), externalAttrs: ZipBuilder.SymlinkAttr).Build());
        var outcome = await ExtractionRunner.RunAsync(a, Dest, fileSystem: new InMemoryFileSystem(Dest));
        Assert.Contains("1 file ", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("skipped", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_selection_extracts_only_those_entries()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("a", B("1")).Add("b", B("2")).Add("c", B("3")).Build());
        var fs = new InMemoryFileSystem(Dest);
        var outcome = await ExtractionRunner.RunAsync(a, Dest, selection: [a.Entries[1].Index], fileSystem: fs);
        Assert.Equal(1, outcome.Result!.Files);
        Assert.Equal([Path.Combine(Dest, "b")], fs.Files.Keys.Where(k => !k.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Failures_become_friendly_messages_not_exceptions()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("ok", B("1")).Add("../evil", B("2")).Build());
        var fs = new InMemoryFileSystem(Dest);
        var outcome = await ExtractionRunner.RunAsync(a, Dest, fileSystem: fs);
        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Contains("Nothing was extracted", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(ArchiveError.UnsafePath, outcome.Error);
        Assert.Null(outcome.Result);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task Cancelling_reports_cancelled_and_cleans_up()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("a", new byte[2_000_000]).Add("b", new byte[2_000_000]).Build());
        var fs = new InMemoryFileSystem(Dest);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.FilesCompleted >= 1) cts.Cancel(); });
        var outcome = await ExtractionRunner.RunAsync(a, Dest, progress: progress, fileSystem: fs, cancellationToken: cts.Token);
        Assert.Equal(RunStatus.Cancelled, outcome.Status);
        Assert.Empty(fs.LeftoverTemps());
    }

    [Fact]
    public async Task Progress_is_forwarded()
    {
        var a = ZipArchive.Open(new ZipBuilder().Add("a", new byte[1000]).Build());
        var seen = new List<ExtractionProgress>();
        await ExtractionRunner.RunAsync(a, Dest, progress: new SyncProgress(seen.Add), fileSystem: new InMemoryFileSystem(Dest));
        Assert.NotEmpty(seen);
        Assert.Equal(1000, seen[^1].BytesWritten);
    }

    private sealed class SyncProgress(Action<ExtractionProgress> a) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => a(value);
    }
}
