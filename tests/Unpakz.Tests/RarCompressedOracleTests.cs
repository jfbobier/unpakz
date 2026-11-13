using System.Security.Cryptography;
using Unpakz.Core;
using Unpakz.Core.Rar;
using Unpakz.Engine;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Compressed archives made by the real RAR 7.x must decode byte-identically (checked against the source files and unrar).</summary>
public sealed class RarCompressedOracleTests : IDisposable
{
    private readonly string _work = Oracle.NewWorkDir("rarc");

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string Src => Path.Combine(_work, "src");

    private void Put(string name, byte[] data)
    {
        var path = Path.Combine(Src, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private string Pack(string rar, params string[] switches)
    {
        var archive = Path.Combine(_work, "a.rar");
        if (File.Exists(archive)) File.Delete(archive);
        List<string> args = ["a", "-ma5", "-r", "-idq", "-y", "-ep1", .. switches, archive, "*"];
        Assert.True(Oracle.Run(rar, args, Src, out var log) == 0, log);
        return archive;
    }

    /// <summary>Extracts with our engine and compares every file with the original; returns decoder stats.</summary>
    private DecoderStats ExtractAndCompare(string archive, string label = "")
    {
        var bytes = File.ReadAllBytes(archive);
        var opened = ArchiveReader.Open(bytes, new ArchiveLimits { MaxDictionarySize = 1L << 31 });
        var ours = Path.Combine(_work, "ours-" + Guid.NewGuid().ToString("N")[..6]);
        ArchiveExtractor.Extract(opened, ours, new ExtractOptions { Limits = new ArchiveLimits { MaxDictionarySize = 1L << 31 } });

        var expected = Directory.GetFiles(Src, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Src, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(ours, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(ours, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
        foreach (var rel in expected)
        {
            var a = SHA256.HashData(File.ReadAllBytes(Path.Combine(Src, rel)));
            var b = SHA256.HashData(File.ReadAllBytes(Path.Combine(ours, rel)));
            Assert.True(a.AsSpan().SequenceEqual(b), $"{label}: content differs: {rel}");
        }

        return ((Rar5Archive)opened).Stats;
    }

    private void StandardMix()
    {
        Put("text.txt", TestData.Text(300_000));
        Put("table.bin", TestData.Table(200_000));
        Put("zeros.bin", TestData.Zeros(500_000));
        Put("random.bin", TestData.Random(50_000));
        Put("tiny.txt", "hello"u8.ToArray());
        Put("empty.txt", []);
        Put("sub/dir/more.txt", TestData.Text(40_000, 9));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Methods_1_to_5_decode_identically(int method)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        StandardMix();
        ExtractAndCompare(Pack(rar, $"-m{method}"), $"m{method}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Solid_archives_decode_identically(int method)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        StandardMix();
        ExtractAndCompare(Pack(rar, $"-m{method}", "-s"), $"solid m{method}");
    }

    // ---- filters: forced on real data, and proven to have run ----
    [Theory]
    [InlineData("-mcD+", 1)]
    [InlineData("-mc2D+", 2)]
    [InlineData("-mc4D+", 4)]
    public void Delta_filter_decodes_identically(string flag, int channels)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("audio.raw", channels == 2 ? TestData.Audio(800_000) : TestData.Table(800_000));
        var stats = ExtractAndCompare(Pack(rar, "-m3", flag), flag);
        Assert.True(stats.Delta > 0, "the delta filter never ran: this test would prove nothing");
    }

    [Fact]
    public void X86_filter_decodes_identically()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("code.bin", TestData.X86(1_500_000));
        var stats = ExtractAndCompare(Pack(rar, "-m3", "-mcE+"));
        Assert.True(stats.E8 + stats.E8E9 > 0, "no x86 filter ran");
    }

    [Fact]
    public void Filters_work_when_blocks_cross_the_window_wrap_point()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("code.bin", TestData.X86(6_000_000));
        Put("audio.raw", TestData.Audio(3_000_000));
        var stats = ExtractAndCompare(Pack(rar, "-m3", "-md128k", "-mcE+", "-mc2D+"));
        Assert.True(stats.Filters > 50, $"expected many filter blocks, saw {stats.Filters}");
    }

    // ---- dictionary sizes, window wrap, distances ----
    [Theory]
    [InlineData("-md128k")]
    [InlineData("-md1m")]
    [InlineData("-md4m")]
    public void Small_dictionaries_wrap_the_window_many_times(string dict)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("big.txt", TestData.Text(24_000_000));
        Put("mixed.bin", TestData.Table(6_000_000));
        ExtractAndCompare(Pack(rar, "-m2", dict), dict);
    }

    [Theory]
    [InlineData("-md4m", 3_000_000)]
    [InlineData("-md64m", 20_000_000)]
    public void Long_range_matches_decode_identically(string dict, int blockSize)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("repeat.bin", TestData.LongRange(blockSize * 4 + 12345, blockSize));
        ExtractAndCompare(Pack(rar, "-m5", dict, "-mcL+"), dict);
    }

    [Theory]
    [InlineData("-md256m")]
    [InlineData("-md1g")]
    public void Huge_declared_dictionaries_cost_nothing_for_small_data(string dict)
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        StandardMix();
        var archive = Pack(rar, "-m3", dict);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        ExtractAndCompare(archive, dict);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(after - before < 64L * 1024 * 1024, $"decoder retained {(after - before) / 1_000_000} MB for a tiny archive");
    }

    [Fact]
    public void Dictionary_beyond_the_configured_limit_is_refused()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        Put("data.txt", TestData.Text(2_000_000));
        var archive = ArchiveReader.Open(File.ReadAllBytes(Pack(rar, "-m3", "-md1m")));
        var entry = archive.Entries.Single();
        var ex = Assert.Throws<ArchiveException>(() => archive.Extract(entry, new MemorySink(), new ArchiveLimits { MaxDictionarySize = 512 * 1024 }));
        Assert.Equal(ArchiveError.LimitExceeded, ex.Error);
    }

    // ---- solid archives: selective and out-of-order extraction ----
    [Fact]
    public void Solid_entries_can_be_extracted_selectively_and_out_of_order()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var data = new Dictionary<string, byte[]>();
        for (var i = 0; i < 6; i++) { data[$"f{i}.txt"] = TestData.Text(60_000 + (i * 7000), i + 1); Put($"f{i}.txt", data[$"f{i}.txt"]); }
        var archive = ArchiveReader.Open(File.ReadAllBytes(Pack(rar, "-m3", "-s")));

        byte[] Get(string name)
        {
            var e = archive.Entries.Single(x => x.Name == name);
            var sink = new MemorySink();
            archive.Extract(e, sink, new ArchiveLimits());
            return sink.ToArray();
        }

        Assert.Equal(data["f4.txt"], Get("f4.txt"));      // jumps over f0..f3 (replayed internally)
        Assert.Equal(data["f1.txt"], Get("f1.txt"));      // goes backwards: decoder rebuilt
        Assert.Equal(data["f5.txt"], Get("f5.txt"));
        Assert.Equal(data["f5.txt"], Get("f5.txt"));      // same entry twice
        Assert.Equal(data["f0.txt"], Get("f0.txt"));
        Assert.Equal(data["f2.txt"], Get("f2.txt"));
        Assert.Equal(data["f3.txt"], Get("f3.txt"));
    }

    [Fact]
    public void A_failed_solid_extraction_does_not_poison_later_ones()
    {
        if (!Oracle.TryGetRar(out var rar, out _)) return;
        var a = TestData.Text(80_000, 1); var b = TestData.Text(80_000, 2);
        Put("a.txt", a); Put("b.txt", b);
        var bytes = File.ReadAllBytes(Pack(rar, "-m3", "-s"));
        var archive = ArchiveReader.Open(bytes);
        var entryA = archive.Entries.Single(x => x.Name == "a.txt");
        var entryB = archive.Entries.Single(x => x.Name == "b.txt");

        Assert.Throws<ArchiveException>(() => archive.Extract(entryB, new ThrowingSink(100), new ArchiveLimits()));   // sink fails mid-way
        var sink = new MemorySink();
        archive.Extract(entryB, sink, new ArchiveLimits());
        Assert.Equal(b, sink.ToArray());
        var sinkA = new MemorySink();
        archive.Extract(entryA, sinkA, new ArchiveLimits());
        Assert.Equal(a, sinkA.ToArray());
    }

    private sealed class ThrowingSink(int after) : IOutputSink
    {
        private int _written;
        public void Write(ReadOnlySpan<byte> data)
        {
            _written += data.Length;
            if (_written > after) throw new ArchiveException("sink failed");
        }
    }
}
