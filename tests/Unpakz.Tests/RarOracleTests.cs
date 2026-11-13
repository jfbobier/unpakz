using System.Security.Cryptography;
using Unpakz.Core;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Archives made by the real RAR 7.x must be read like the real unrar reads them.</summary>
public sealed class RarOracleTests : IDisposable
{
    private readonly string _work = Oracle.NewWorkDir("rar");

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).ToDictionary(
            e => Path.GetRelativePath(root, e).Replace('\\', '/'),
            e => Directory.Exists(e) ? "<dir>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e))),
            StringComparer.Ordinal);

    private string MakeArchive(string rar, string src, params string[] switches)
    {
        var archive = Path.Combine(_work, "a.rar");
        var args = new List<string> { "a", "-ma5", "-r", "-idq", "-y" };
        args.AddRange(switches);
        args.Add(archive);
        args.Add("*");
        Assert.True(Oracle.Run(rar, args, src, out var log) == 0, log);
        return archive;
    }

    private string NewTree()
    {
        var src = Path.Combine(_work, "src");
        TestTree.Build(src);
        return src;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stored_tree_matches_unrar(bool solid)
    {
        if (!Oracle.TryGetRar(out var rar, out var unrar)) return;
        var src = NewTree();
        var archive = solid ? MakeArchive(rar, src, "-m0", "-s") : MakeArchive(rar, src, "-m0");

        var theirs = Path.Combine(_work, "theirs");
        Directory.CreateDirectory(theirs);
        Assert.True(Oracle.Run(unrar, ["x", "-y", "-idq", archive, theirs + Path.DirectorySeparatorChar], _work, out var log) == 0, log);

        var ours = Path.Combine(_work, "ours");
        var result = ArchiveExtractor.Extract(File.ReadAllBytes(archive), ours);

        var a = Snapshot(ours);
        var b = Snapshot(theirs);
        Assert.Equal(b.Keys.Order(StringComparer.Ordinal), a.Keys.Order(StringComparer.Ordinal));
        foreach (var (k, v) in b) Assert.True(a[k] == v, $"differs: {k}");
        Assert.True(result.Files > 2000);
        Assert.Empty(result.Skipped);

        foreach (var f in Directory.GetFiles(ours, "*", SearchOption.AllDirectories).Take(200))
        {
            var original = File.GetLastWriteTime(Path.Combine(src, Path.GetRelativePath(ours, f)));
            Assert.InRange(Math.Abs((File.GetLastWriteTime(f) - original).TotalSeconds), 0, 2.5);
        }
    }

    [Fact]
    public void Symlinks_stored_by_rar_are_recognised_and_skipped()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        if (OperatingSystem.IsWindows()) return;
        var src = Path.Combine(_work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "real.txt"), "hello");
        File.CreateSymbolicLink(Path.Combine(src, "link.txt"), "/etc/passwd");
        File.CreateSymbolicLink(Path.Combine(src, "dirlink"), "/etc");
        var archive = MakeArchive(rar, src, "-m0", "-ol");

        var a = ArchiveReader.Open(File.ReadAllBytes(archive));
        Assert.Equal(EntryKind.Symlink, a.Entries.Single(e => e.Name == "link.txt").Kind);
        Assert.Equal(EntryKind.Symlink, a.Entries.Single(e => e.Name == "dirlink").Kind);
        Assert.Equal(EntryKind.File, a.Entries.Single(e => e.Name == "real.txt").Kind);

        var ours = Path.Combine(_work, "ours");
        var r = ArchiveExtractor.Extract(File.ReadAllBytes(archive), ours);
        Assert.Equal(2, r.Skipped.Count);
        Assert.Equal(["real.txt"], Directory.GetFileSystemEntries(ours).Select(Path.GetFileName));
    }

    [Fact]
    public void Blake2_only_integrity_is_reported_unsupported()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var src = Path.Combine(_work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var archive = MakeArchive(rar, src, "-m0", "-htb");
        var ex = Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(File.ReadAllBytes(archive), Path.Combine(_work, "ours")));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
        Assert.False(Directory.Exists(Path.Combine(_work, "ours")));
    }

    [Fact]
    public void Compressed_archives_parse_fully_but_extraction_is_unsupported_for_now()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var src = Path.Combine(_work, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), new string('x', 5000));
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "hello hello hello hello");
        var archive = MakeArchive(rar, src, "-m3");

        var a = ArchiveReader.Open(File.ReadAllBytes(archive));
        Assert.Equal(["a.txt", "sub", "sub/b.txt"], a.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(5000UL, a.Entries.Single(e => e.Name == "a.txt").UncompressedSize);
        Assert.True(a.Entries.Single(e => e.Name == "a.txt").CompressedSize < 5000);

        var ex = Assert.Throws<ArchiveException>(() => ArchiveExtractor.Extract(File.ReadAllBytes(archive), Path.Combine(_work, "ours")));
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
        Assert.False(Directory.Exists(Path.Combine(_work, "ours")));      // refused before touching the disk
    }

    [Fact]
    public void Encrypted_archives_are_reported_unsupported_at_the_right_level()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var src = Path.Combine(_work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "secret data");

        var dataOnly = MakeArchive(rar, src, "-m0", "-psecret");       // names visible, contents encrypted
        var a = ArchiveReader.Open(File.ReadAllBytes(dataOnly));
        Assert.Equal("a.txt", Assert.Single(a.Entries).Name);
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => a.CheckSupported(a.Entries[0])).Error);
        File.Delete(dataOnly);

        var headers = MakeArchive(rar, src, "-m0", "-hpsecret");       // names encrypted too
        Assert.Equal(ArchiveError.Unsupported, Assert.Throws<ArchiveException>(() => ArchiveReader.Open(File.ReadAllBytes(headers))).Error);
    }

    [Fact]
    public void Every_truncation_of_a_real_archive_is_handled()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var src = Path.Combine(_work, "src");
        Directory.CreateDirectory(Path.Combine(src, "d"));
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[300]);
        File.WriteAllText(Path.Combine(src, "d", "b.txt"), "hi");
        var bytes = File.ReadAllBytes(MakeArchive(rar, src, "-m0"));

        for (var len = 0; len < bytes.Length; len++)
        {
            try
            {
                var a = ArchiveReader.Open(bytes.AsMemory(0, len));
                foreach (var e in a.Entries.Where(e => e.Kind == EntryKind.File)) a.Extract(e, new MemorySink(), new ArchiveLimits());
            }
            catch (ArchiveException) { }
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var copy = (byte[])bytes.Clone();
            copy[i] ^= 0xFF;
            try
            {
                var a = ArchiveReader.Open(copy);
                foreach (var e in a.Entries.Where(e => e.Kind == EntryKind.File)) a.Extract(e, new MemorySink(), new ArchiveLimits());
            }
            catch (ArchiveException) { }
        }
    }
}
