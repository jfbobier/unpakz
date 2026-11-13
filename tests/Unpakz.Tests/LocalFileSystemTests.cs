using System.Buffers.Binary;
using System.Security.Cryptography;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Same guarantees as the in-memory tests, proven against the real file system.</summary>
public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _work = Oracle.NewWorkDir("local");
    private string Dest => Path.Combine(_work, "dest");
    private string Outside => Path.Combine(_work, "outside");

    public LocalFileSystemTests() => Directory.CreateDirectory(Outside);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static byte[] Txt(int n) { var b = new byte[n]; for (var i = 0; i < n; i++) b[i] = (byte)"hello "[i % 6]; return b; }

    private string[] Leftovers() =>
        Directory.Exists(_work) ? Directory.GetFiles(_work, ".unpakz-*", SearchOption.AllDirectories) : [];

    private static bool CanSymlink()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var d = Path.Combine(Path.GetTempPath(), "unpakz-symlink-probe-" + Guid.NewGuid().ToString("N")[..6]);
        try { Directory.CreateDirectory(d); Directory.CreateSymbolicLink(Path.Combine(d, "l"), d); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { Directory.Delete(d, true); } catch (IOException) { } }
    }

    [Fact]
    public void Extracts_tree_with_content_directories_and_timestamps()
    {
        var a = Rnd(1000); var b = Txt(200_000); var c = Txt(33);
        var zip = new ZipBuilder().Add("a.bin", a).AddDirectory("d").Add("d/b.txt", b, deflate: true).AddDirectory("empty")
            .Add("d/e/f/c.txt", c, deflate: true).Add("日本/é ü.txt", c).Build();
        var r = ZipExtractor.Extract(zip, Path.Combine(Dest, "x", "y"));

        var root = Path.Combine(Dest, "x", "y");
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(root, "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(root, "d", "b.txt")));
        Assert.Equal(c, File.ReadAllBytes(Path.Combine(root, "d", "e", "f", "c.txt")));
        Assert.Equal(c, File.ReadAllBytes(Path.Combine(root, "日本", "é ü.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, "empty")));
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0), File.GetLastWriteTime(Path.Combine(root, "a.bin")));
        Assert.Equal(4, r.Files);
        Assert.Equal(2, r.Directories);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Single_write_proof_on_real_file_system()
    {
        var data = new (string, byte[])[] { ("a", Rnd(5000)), ("d/b", Txt(300_000)), ("d/c", []) };
        var zip = new ZipBuilder().Add("a", data[0].Item2).Add("d/b", data[1].Item2, deflate: true).Add("d/c", []).Build();
        var rec = new RecordingFileSystem(new LocalFileSystem());
        ZipExtractor.Extract(zip, Dest, null, rec);

        Assert.Equal(data.Sum(x => (long)x.Item2.Length), rec.BytesWritten);
        Assert.Equal(3, rec.Created.Count);
        Assert.All(rec.Created, f => Assert.StartsWith(Dest, f, StringComparison.Ordinal));
        Assert.Equal([Dest, Path.Combine(Dest, "d")], rec.Dirs.Order(StringComparer.Ordinal));
        Assert.Equal(["a", "d/b", "d/c"], Directory.GetFiles(Dest, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Dest, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Symlinked_directory_in_destination_is_not_followed()
    {
        if (!CanSymlink()) return;
        Directory.CreateDirectory(Dest);
        Directory.CreateSymbolicLink(Path.Combine(Dest, "link"), Outside);

        var zip = new ZipBuilder().Add("ok.txt", Rnd(5)).Add("link/pwned.txt", Rnd(5)).Build();
        var ex = Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest));
        Assert.Equal(ArchiveError.UnsafePath, ex.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Symlink_at_final_path_is_never_written_through()
    {
        if (!CanSymlink()) return;
        var target = Path.Combine(Outside, "precious.txt");
        File.WriteAllText(target, "PRECIOUS");
        Directory.CreateDirectory(Dest);
        File.CreateSymbolicLink(Path.Combine(Dest, "a.txt"), target);
        var zip = new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Build();

        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest)).Error);
        Assert.Equal("PRECIOUS", File.ReadAllText(target));

        ZipExtractor.Extract(zip, Dest, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite });
        Assert.Equal("PRECIOUS", File.ReadAllText(target));                       // target untouched
        Assert.Null(new FileInfo(Path.Combine(Dest, "a.txt")).LinkTarget);        // link replaced by a regular file
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(Dest, "a.txt")));
    }

    [Fact]
    public void Dangling_symlink_at_final_path_conflicts()
    {
        if (!CanSymlink()) return;
        Directory.CreateDirectory(Dest);
        File.CreateSymbolicLink(Path.Combine(Dest, "a.txt"), Path.Combine(Outside, "does-not-exist"));
        var zip = new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Build();
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest)).Error);
        Assert.False(File.Exists(Path.Combine(Outside, "does-not-exist")));
    }

    [Fact]
    public void Overwrite_policies_behave_on_real_files()
    {
        Directory.CreateDirectory(Dest);
        var path = Path.Combine(Dest, "a.txt");
        var zip = new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Add("b.txt", "B"u8.ToArray()).Build();

        File.WriteAllText(path, "OLD");
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest)).Error);
        Assert.Equal("OLD", File.ReadAllText(path));

        var r = ZipExtractor.Extract(zip, Dest, new ExtractOptions { Overwrite = OverwritePolicy.Skip });
        Assert.Equal("OLD", File.ReadAllText(path));
        Assert.Equal("B", File.ReadAllText(Path.Combine(Dest, "b.txt")));
        Assert.Single(r.Skipped);

        ZipExtractor.Extract(zip, Dest, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite });
        Assert.Equal("NEW", File.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Directory_in_the_way_conflicts_even_with_overwrite()
    {
        Directory.CreateDirectory(Path.Combine(Dest, "a.txt"));
        var zip = new ZipBuilder().Add("a.txt", "NEW"u8.ToArray()).Build();
        Assert.Equal(ArchiveError.Conflict, Assert.Throws<ArchiveException>(() =>
            ZipExtractor.Extract(zip, Dest, new ExtractOptions { Overwrite = OverwritePolicy.Overwrite })).Error);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Zip_slip_creates_nothing_anywhere()
    {
        var zip = new ZipBuilder().Add("ok.txt", Rnd(5)).Add("../escaped.txt", Rnd(5)).Add("..\\escaped2.txt", Rnd(5)).Build();
        Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest));
        Assert.False(Directory.Exists(Dest));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_work), e => Path.GetFileName(e).StartsWith("escaped", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_entry_leaves_no_partial_or_temp_file()
    {
        var zip = new ZipBuilder().Add("one", Rnd(50)).Add("two", Txt(100_000), deflate: true).Build();
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));
        var lho = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 46 + 3 + 42));
        zip[lho + 30 + 3 + 40] ^= 0xFF;
        Assert.Throws<ArchiveException>(() => ZipExtractor.Extract(zip, Dest));
        Assert.True(File.Exists(Path.Combine(Dest, "one")));
        Assert.False(File.Exists(Path.Combine(Dest, "two")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Large_entries_round_trip_by_hash()
    {
        var big = Rnd(48 << 20, 7);
        var txt = Txt(64 << 20);
        var zip = new ZipBuilder().Add("big.bin", big).Add("big.txt", txt, deflate: true).Build();
        ZipExtractor.Extract(zip, Dest);
        Assert.Equal(SHA256.HashData(big), SHA256.HashData(File.ReadAllBytes(Path.Combine(Dest, "big.bin"))));
        Assert.Equal(SHA256.HashData(txt), SHA256.HashData(File.ReadAllBytes(Path.Combine(Dest, "big.txt"))));
    }

    [Fact]
    public void GetKind_reports_each_kind_without_following_links()
    {
        var fs = new LocalFileSystem();
        Directory.CreateDirectory(Dest);
        var file = Path.Combine(Dest, "f"); File.WriteAllText(file, "x");
        Assert.Equal(PathKind.None, fs.GetKind(Path.Combine(Dest, "nope")));
        Assert.Equal(PathKind.None, fs.GetKind(Path.Combine(file, "below-a-file")));
        Assert.Equal(PathKind.File, fs.GetKind(file));
        Assert.Equal(PathKind.Directory, fs.GetKind(Dest));
        if (!CanSymlink()) return;
        File.CreateSymbolicLink(Path.Combine(Dest, "lf"), file);
        Directory.CreateSymbolicLink(Path.Combine(Dest, "ld"), Outside);
        File.CreateSymbolicLink(Path.Combine(Dest, "dangling"), Path.Combine(Outside, "gone"));
        Assert.Equal(PathKind.ReparsePoint, fs.GetKind(Path.Combine(Dest, "lf")));
        Assert.Equal(PathKind.ReparsePoint, fs.GetKind(Path.Combine(Dest, "ld")));
        Assert.Equal(PathKind.ReparsePoint, fs.GetKind(Path.Combine(Dest, "dangling")));
    }

    private sealed class RecordingFileSystem(IFileSystem inner) : IFileSystem
    {
        public long BytesWritten { get; private set; }
        public List<string> Created { get; } = [];
        public HashSet<string> Dirs { get; } = [];
        public PathKind GetKind(string path) => inner.GetKind(path);
        public void CreateDirectory(string path) { Dirs.Add(path); inner.CreateDirectory(path); }

        public IFileWriter CreateFile(string finalPath, long preallocate)
        {
            Created.Add(finalPath);
            return new W(inner.CreateFile(finalPath, preallocate), this);
        }

        private sealed class W(IFileWriter w, RecordingFileSystem fs) : IFileWriter
        {
            public void Write(ReadOnlySpan<byte> data) { fs.BytesWritten += data.Length; w.Write(data); }
            public void Commit(DateTime? t, bool o) => w.Commit(t, o);
            public void Dispose() => w.Dispose();
        }
    }
}
