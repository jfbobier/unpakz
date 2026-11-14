using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unpakz.Interop;

namespace Unpakz.Interop.Tests;

/// <summary>Points the P/Invoke "unpakz_native.dll" at the native library built from native/ (libunpakz_native.so on Linux, unpakz_native.dll on Windows).</summary>
internal static class Env
{
    [ModuleInitializer]
    internal static void Init() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeArchive).Assembly, (name, _, _) =>
        {
            if (name != "unpakz_native.dll") return IntPtr.Zero;
            var path = Environment.GetEnvironmentVariable("UNPAKZ_NATIVE_LIB") ?? FindDefault();
            return NativeLibrary.Load(path);
        });

    private static string FindDefault()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var candidate in new[] { "native/build-release/libunpakz_native.so", "native/build-msvc/unpakz_native.dll", "native/build-linux/libunpakz_native.so" })
            {
                var p = Path.Combine(dir, candidate);
                if (File.Exists(p)) return p;
            }
        }

        throw new FileNotFoundException("The native library was not found; build it with scripts/native-test.sh (UNPAKZ_NATIVE_BUILD=release) or set UNPAKZ_NATIVE_LIB.");
    }

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unpakz-it-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] bytes)
    {
        var p = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal static class Archives
{
    public static readonly Dictionary<string, byte[]> Tree = new()
    {
        ["readme.txt"] = Unpakz.Tests.Support.TestData.Text(3000, 1),
        ["docs/guide.txt"] = Unpakz.Tests.Support.TestData.Text(9000, 2),
        ["docs/deep/notes.txt"] = Unpakz.Tests.Support.TestData.Text(2000, 3),
        ["data/blob.bin"] = Unpakz.Tests.Support.TestData.Random(20_000, 4),
        ["empty.txt"] = [],
    };

    public static string Zip(TempDir d, string name = "sample.zip", Dictionary<string, byte[]>? tree = null)
    {
        var b = new Unpakz.Tests.Support.ZipBuilder();
        foreach (var (k, v) in tree ?? Tree) b.Add(k, v, deflate: v.Length > 100);
        return d.Write(name, b.Build());
    }

    public static string TarGz(TempDir d, string name = "sample.tar.gz", Dictionary<string, byte[]>? tree = null)
    {
        var path = d.File(name);
        using (var file = File.Create(path))
        using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
        using (var tar = new System.Formats.Tar.TarWriter(gz, System.Formats.Tar.TarEntryFormat.Pax))
        {
            foreach (var (k, v) in tree ?? Tree)
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, k) { DataStream = new MemoryStream(v) });
        }

        return path;
    }

    public static string RarStored(TempDir d, string name = "sample.rar", Dictionary<string, byte[]>? tree = null)
    {
        var b = new Unpakz.Tests.Support.Rar5Builder();
        foreach (var (k, v) in tree ?? Tree) b.AddFile(k, v);
        return d.Write(name, b.Build());
    }

    public static IEnumerable<(string Format, Func<TempDir, string> Make)> All()
    {
        yield return ("zip", d => Zip(d));
        yield return ("tar.gz", d => TarGz(d));
        yield return ("rar", d => RarStored(d));
    }

    public static void AssertTree(string dir, Dictionary<string, byte[]> expected)
    {
        foreach (var (k, v) in expected)
            Assert.Equal(v, File.ReadAllBytes(Path.Combine(dir, k.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.EnumerateFiles(dir, ".unpakz-*", SearchOption.AllDirectories));
    }
}
