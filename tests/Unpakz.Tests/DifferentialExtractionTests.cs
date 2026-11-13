using System.Security.Cryptography;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>End to end: the tree we extract must equal the tree 7-Zip extracts from the same archive.</summary>
public class DifferentialExtractionTests
{
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).ToDictionary(
            e => Path.GetRelativePath(root, e).Replace('\\', '/'),
            e => Directory.Exists(e) ? "<dir>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e))),
            StringComparer.Ordinal);

    [Theory]
    [InlineData("Copy", 0)]
    [InlineData("Deflate", 5)]
    public void Extracted_tree_equals_sevenzip_extracted_tree(string method, int level)
    {
        if (!Oracle.TryGetSevenZip(out var sz)) return;
        var work = Oracle.NewWorkDir("diffx");
        try
        {
            var src = Path.Combine(work, "src");
            TestTree.Build(src);
            var zipPath = Path.Combine(work, "a.zip");
            Assert.Equal(0, Oracle.Run(sz, ["a", "-tzip", $"-mm={method}", $"-mx={level}", zipPath, "."], src, out var log1));

            var theirs = Path.Combine(work, "theirs");
            Directory.CreateDirectory(theirs);
            Assert.True(Oracle.Run(sz, ["x", "-y", $"-o{theirs}", zipPath], work, out var log2) == 0, log2);

            var ours = Path.Combine(work, "ours");
            var result = ArchiveExtractor.Extract(File.ReadAllBytes(zipPath), ours);

            var a = Snapshot(ours);
            var b = Snapshot(theirs);
            Assert.Equal(b.Keys.Order(StringComparer.Ordinal), a.Keys.Order(StringComparer.Ordinal));
            foreach (var (k, v) in b) Assert.True(a[k] == v, $"differs: {k}");
            Assert.True(result.Files > 2000);

            // timestamps: ours come from the 2-second DOS field, so allow 2 s (+ rounding) against the originals
            foreach (var f in Directory.GetFiles(ours, "*", SearchOption.AllDirectories).Take(200))
            {
                var original = File.GetLastWriteTime(Path.Combine(src, Path.GetRelativePath(ours, f)));
                Assert.InRange(Math.Abs((File.GetLastWriteTime(f) - original).TotalSeconds), 0, 2.5);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
