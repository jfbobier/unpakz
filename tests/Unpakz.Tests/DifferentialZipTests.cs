using Unpakz.Core.Zip;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Archives made by 7-Zip must extract byte-identically with our code.</summary>
public class DifferentialZipTests
{
    private static void BuildTree(string root)
    {
        var rng = new Random(99);
        void File(string rel, int size, bool compressible)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var b = new byte[size];
            if (compressible) for (var i = 0; i < size; i++) b[i] = (byte)("lorem ipsum dolor "[i % 18]);
            else rng.NextBytes(b);
            System.IO.File.WriteAllBytes(path, b);
        }

        File("empty.bin", 0, false);
        File("one.bin", 1, false);
        File("small.txt", 1500, true);
        File("random_1m.bin", 1 << 20, false);
        File("text_8m.txt", 8 << 20, true);
        File("zeros_20m.bin", 20 << 20, true);
        File("nested/a/b/c/deep.dat", 70_000, false);
        File("unicode/日本語/é ü ñ.txt", 4000, true);
        File("spaces in name/file with spaces.txt", 100, true);
        Directory.CreateDirectory(Path.Combine(root, "empty_dir"));
        for (var i = 0; i < 2000; i++) File($"many/f{i / 100:D2}/file{i:D4}.txt", 10 + i % 300, i % 2 == 0);
    }

    [Theory]
    [InlineData("Copy", 0)]
    [InlineData("Deflate", 1)]
    [InlineData("Deflate", 5)]
    [InlineData("Deflate", 9)]
    public void Extracts_sevenzip_archives_identically(string method, int level)
    {
        if (!Oracle.TryGetSevenZip(out var sz)) return;

        var work = Oracle.NewWorkDir($"diff-{method}-{level}");
        try
        {
            var src = Path.Combine(work, "src");
            BuildTree(src);
            var zipPath = Path.Combine(work, "out.zip");
            var rc = Oracle.Run(sz, ["a", "-tzip", $"-mm={method}", $"-mx={level}", "-mmt=on", zipPath, "."], src, out var log);
            Assert.True(rc == 0, log);

            var zip = File.ReadAllBytes(zipPath);
            var entries = ZipCentralDirectory.Read(zip);

            var expectedFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var actualFiles = entries.Where(e => !e.IsDirectory).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedFiles, actualFiles);

            foreach (var e in entries.Where(e => !e.IsDirectory))
            {
                var sink = new MemorySink();
                ZipEntryExtractor.Extract(zip, e, sink);
                var expected = File.ReadAllBytes(Path.Combine(src, e.Name));
                Assert.True(expected.AsSpan().SequenceEqual(sink.ToArray()), $"content differs: {e.Name}");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
