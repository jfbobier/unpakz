namespace Unpakz.Tests.Support;

internal static class TestTree
{
    /// <summary>Builds a deterministic mixed tree (empty, tiny, random, compressible, large, Unicode, nested, 2000 small files).</summary>
    public static void Build(string root)
    {
        var rng = new Random(99);
        void Put(string rel, int size, bool compressible)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var b = new byte[size];
            if (compressible) for (var i = 0; i < size; i++) b[i] = (byte)"lorem ipsum dolor "[i % 18];
            else rng.NextBytes(b);
            File.WriteAllBytes(path, b);
        }

        Put("empty.bin", 0, false);
        Put("one.bin", 1, false);
        Put("small.txt", 1500, true);
        Put("random_1m.bin", 1 << 20, false);
        Put("text_8m.txt", 8 << 20, true);
        Put("zeros_20m.bin", 20 << 20, true);
        Put("nested/a/b/c/deep.dat", 70_000, false);
        Put("unicode/日本語/é ü ñ.txt", 4000, true);
        Put("spaces in name/file with spaces.txt", 100, true);
        Directory.CreateDirectory(Path.Combine(root, "empty_dir"));
        for (var i = 0; i < 2000; i++) Put($"many/f{i / 100:D2}/file{i:D4}.txt", 10 + i % 300, i % 2 == 0);
    }
}
