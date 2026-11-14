using Unpakz.Interop;
using Unpakz.Tests.Support;

namespace Unpakz.Interop.Tests;

public class SupportTests
{
    private static EntryInfo E(long i, string name, EntryKind k = EntryKind.File) => new() { Index = i, Name = name, RelativePath = name, Kind = k };

    [Fact]
    public void Selection_normalizes_names_and_a_picked_folder_brings_its_contents()
    {
        Assert.Equal("a/b/c", EntrySelection.Normalize("a\\b//c/"));
        var entries = new[] { E(0, "top.txt"), E(1, "d/", EntryKind.Directory), E(2, "d/x.txt"), E(3, "d/e/y.txt"), E(4, "other/z.txt"), E(5, "dd/q.txt") };
        Assert.Equal(new[] { 1, 2, 3 }, EntrySelection.Expand(entries, new[] { 1 }).OrderBy(x => x));
        Assert.Equal(new[] { 0 }, EntrySelection.Expand(entries, new[] { 0 }).OrderBy(x => x));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntrySelection.Expand(entries, new[] { 9 }));
    }

    [Theory]
    [InlineData("C:\\dl\\backup.zip", "backup")]
    [InlineData("C:\\dl\\set.part01.rar", "set")]
    [InlineData("C:\\dl\\src.tar.gz", "src")]
    [InlineData("C:\\dl\\src.tgz", "src")]
    [InlineData("C:\\dl\\weird.rar", "weird")]
    public void The_default_folder_is_named_after_the_archive(string archive, string expected)
    {
        if (!OperatingSystem.IsWindows()) archive = archive.Replace("C:\\dl\\", "/dl/");
        Assert.Equal(expected, Path.GetFileName(DestinationNaming.FolderNextTo(archive)));
    }

    [Fact]
    public void An_unsafe_archive_name_gets_a_safe_folder()
    {
        Assert.Equal("extracted", Path.GetFileName(DestinationNaming.FolderNextTo("/x/CON.zip")));
        Assert.Equal("extracted", Path.GetFileName(DestinationNaming.FolderNextTo("/x/.zip")));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("dir/a b.txt", true)]
    [InlineData("héllo/日本.txt", true)]
    [InlineData("../x", false)]
    [InlineData("/abs", false)]
    [InlineData("a:b", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("trail.", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("a//b", false)]
    public void The_client_side_name_check_only_ever_says_no(string path, bool expected) => Assert.Equal(expected, SafeNames.IsSafeRelativePath(path));

    [Fact]
    public void Errors_are_described_in_plain_language_without_control_characters()
    {
        Assert.Contains("Part 3", FriendlyErrors.Describe(new UnpakzException(UnpakzStatus.MissingVolume, "x", 3)), StringComparison.Ordinal);
        Assert.Contains("damaged", FriendlyErrors.Describe(new UnpakzException(UnpakzStatus.Corrupt, "bad\u0001data")), StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', FriendlyErrors.Describe(new UnpakzException(UnpakzStatus.Corrupt, "bad\u0001data")));
        Assert.Contains("outside the chosen folder", FriendlyErrors.Describe(new UnpakzException(UnpakzStatus.UnsafePath, "x")), StringComparison.Ordinal);
        Assert.Contains("cancelled", FriendlyErrors.Describe(new OperationCanceledException()), StringComparison.Ordinal);
        Assert.Contains("disk is full", FriendlyErrors.Describe(new IOException("There is not enough space on the disk")), StringComparison.Ordinal);
        Assert.True(FriendlyErrors.Describe(new UnpakzException(UnpakzStatus.Corrupt, new string('x', 5000))).Length < 450);
    }

    private sealed class FakeRegistry : IRegistryWriter
    {
        public List<(string Key, string? Name, string Value)> Sets { get; } = [];
        public List<string> Deleted { get; } = [];
        public void SetValue(string key, string? name, string value) => Sets.Add((key, name, value));
        public void DeleteValue(string key, string name) => Deleted.Add(key + "|" + name);
        public void DeleteKeyTree(string key) => Deleted.Add(key);
    }

    [Fact]
    public void Registration_writes_only_per_user_keys_and_never_the_protected_choice()
    {
        var reg = new FakeRegistry();
        FileAssociationPlan.Build(@"C:\Apps\Unpakz\Unpakz.exe").Apply(reg);
        Assert.All(reg.Sets, s => Assert.StartsWith("Software\\", s.Key, StringComparison.Ordinal));
        Assert.DoesNotContain(reg.Sets, s => s.Key.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reg.Sets, s => s.Key.EndsWith(@"shell\open\command", StringComparison.Ordinal) && s.Value == "\"C:\\Apps\\Unpakz\\Unpakz.exe\" \"%1\"");
        Assert.Contains(reg.Sets, s => s.Key == @"Software\Classes\.tgz\OpenWithProgids");
        FileAssociationPlan.Remove(reg);
        Assert.Contains(@"Software\Classes\Unpakz.Archive", reg.Deleted);
        Assert.Throws<ArgumentException>(() => FileAssociationPlan.Build("relative.exe"));
        Assert.Throws<ArgumentException>(() => FileAssociationPlan.Build("C:\\a\"b.exe"));
    }

    [Fact]
    public void The_file_group_descriptor_has_the_documented_layout()
    {
        var items = new[] { new VirtualFile("dir\\a.txt", false, 0x1_0000_0005, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local), 0), new VirtualFile("dir", true, 0, null, 1) };
        var b = FileGroupDescriptor.Serialize(items);
        Assert.Equal(4 + (2 * 592), b.Length);
        Assert.Equal(2u, BitConverter.ToUInt32(b, 0));
        Assert.Equal(0x04u | 0x20u | 0x40u | 0x4000u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(0x80u, BitConverter.ToUInt32(b, 4 + 36));
        Assert.Equal(1u, BitConverter.ToUInt32(b, 4 + 64));
        Assert.Equal(5u, BitConverter.ToUInt32(b, 4 + 68));
        Assert.Equal("dir\\a.txt", System.Text.Encoding.Unicode.GetString(b, 4 + 72, 18));
        Assert.Equal(0x10u, BitConverter.ToUInt32(b, 4 + 592 + 36));
        Assert.Equal(0x04u | 0x4000u, BitConverter.ToUInt32(b, 4 + 592));
        Assert.Throws<UnpakzException>(() => FileGroupDescriptor.Serialize(new[] { new VirtualFile(new string('a', 260), false, 1, null, 0) }));
    }

    [Fact]
    public void A_drag_list_carries_only_safe_files_and_reports_skipped_links()
    {
        using var d = new TempDir();
        var path = Archives.Zip(d);
        var session = ArchiveSession.Open(path);
        var all = VirtualFileList.Build(session);
        Assert.Equal(Archives.Tree.Count, all.Items.Count(i => !i.IsDirectory));
        Assert.All(all.Items, i => Assert.DoesNotContain('/', i.RelativePath));
        var one = VirtualFileList.Build(session, new[] { session.Entries.Single(e => e.RelativePath == "readme.txt").Index is var ix ? (int)ix : 0 });
        Assert.Equal("readme.txt", Assert.Single(one.Items).RelativePath);

        var evil = ArchiveSession.Open(d.Write("evil.zip", new ZipBuilder().Add("ok.txt", [1]).Add("../x", [2]).Build()));
        Assert.Equal(UnpakzStatus.UnsafePath, Assert.Throws<UnpakzException>(() => VirtualFileList.Build(evil)).Status);
    }

    [Fact]
    public void An_entry_stream_delivers_the_exact_bytes_lazily_and_can_be_abandoned()
    {
        using var d = new TempDir();
        var big = new Dictionary<string, byte[]> { ["big.bin"] = TestData.Random(12_000_000, 7), ["small.txt"] = [1, 2, 3] };
        var session = ArchiveSession.Open(Archives.Zip(d, tree: big));
        using (var s = EntryStream.Open(session, session.Entries.Single(e => e.RelativePath == "big.bin")))
        {
            Assert.Equal(12_000_000, s.ExpectedLength);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(big["big.bin"], ms.ToArray());
        }

        var abandoned = EntryStream.Open(session, session.Entries.Single(e => e.RelativePath == "big.bin"));
        Assert.True(abandoned.Read(new byte[10], 0, 10) > 0);
        abandoned.Dispose();                                     // must stop the decoder instead of hanging
        Assert.Throws<ObjectDisposedException>(() => abandoned.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => _ = abandoned.Length);
    }

    [Fact]
    public void A_checksum_failure_is_raised_after_the_bytes_that_precede_it()
    {
        using var d = new TempDir();
        var data = TestData.Random(4000, 5);
        var zip = new ZipBuilder().Add("bad.bin", data).Build();            // stored: the payload is visible in the archive
        var at = IndexOf(zip, data.AsSpan(1000, 50).ToArray());
        zip[at + 10] ^= 0x55;
        var session = ArchiveSession.Open(d.Write("bad.zip", zip));
        using var s = EntryStream.Open(session, session.Entries[0]);
        var got = 0;
        var buf = new byte[1024];
        var ex = Assert.Throws<UnpakzException>(() => { int n; while ((n = s.Read(buf, 0, buf.Length)) > 0) got += n; });
        Assert.Equal(UnpakzStatus.Corrupt, ex.Status);
        Assert.True(got > 0);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void The_session_describes_format_parts_and_size()
    {
        using var d = new TempDir();
        Assert.Equal("ZIP", ArchiveSession.Open(Archives.Zip(d)).Format);
        Assert.Equal("TAR.GZ", ArchiveSession.Open(Archives.TarGz(d)).Format);
        Assert.Equal("RAR", ArchiveSession.Open(Archives.RarStored(d)).Format);
        foreach (var f in Directory.GetFiles(Env.Fixture("mv-stored"))) File.Copy(f, d.File(Path.GetFileName(f)));
        var set = ArchiveSession.Open(d.File("a.part2.rar"));
        Assert.True(set.VolumePaths.Count >= 3);
        Assert.Equal(set.VolumePaths.Sum(p => new FileInfo(p).Length), set.TotalBytes);
        Assert.Throws<FileNotFoundException>(() => ArchiveSession.Open(d.File("nope.zip")));
    }
}
