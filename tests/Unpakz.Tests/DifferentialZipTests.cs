using Unpakz.Core.Zip;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Archives made by 7-Zip must extract byte-identically with our code.</summary>
public class DifferentialZipTests
{
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
            TestTree.Build(src);
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
