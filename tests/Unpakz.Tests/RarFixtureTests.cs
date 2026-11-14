using System.Diagnostics;
using Unpakz.Core;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>
/// Small archives produced by the real RAR 7.23 and committed under Fixtures/, so the robustness sweeps run everywhere.
/// To regenerate: UNPAKZ_REGEN_FIXTURES=1 dotnet test --filter Regenerate (needs tools/rar).
/// </summary>
public class RarFixtureTests
{
    private sealed record Fixture(string File, string[] Switches, Func<Dictionary<string, byte[]>> Content);

    private static Dictionary<string, byte[]> Mix() => new()
    {
        ["text.txt"] = TestData.Text(8000),
        ["table.bin"] = TestData.Table(4000),
        ["tiny.txt"] = "hello"u8.ToArray(),
        ["empty.txt"] = [],
        ["sub/z.txt"] = TestData.Text(3000, 9),
    };

    private static readonly Fixture[] Fixtures =
    [
        new("rar5-m3-mix.rar", ["-m3"], Mix),
        new("rar5-m5-solid.rar", ["-m5", "-s"], Mix),
        new("rar5-delta.rar", ["-m3", "-mc2D+"], () => new() { ["audio.raw"] = TestData.Audio(6_000) }),
        new("rar5-x86.rar", ["-m3", "-mcE+"], () => new() { ["code.bin"] = TestData.X86(6_000) }),
        new("rar5-md256m-tiny.rar", ["-m3", "-md256m"], () => new() { ["note.txt"] = TestData.Text(5000, 8) }),
        new("rar5-m1-stored-and-packed.rar", ["-m1"], () => new() { ["a.txt"] = TestData.Text(6000, 3), ["noise.bin"] = TestData.Random(1500), ["b.txt"] = TestData.Text(2500, 4) }),
    ];

    public static TheoryData<string> Names() => [.. Fixtures.Select(f => f.File)];

    private static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Fixture Get(string name) => Fixtures.Single(f => f.File == name);

    private static Dictionary<string, byte[]> ExtractAll(byte[] archive)
    {
        var a = ArchiveReader.Open(archive);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in a.Entries.Where(e => e.Kind == EntryKind.File))
        {
            a.CheckSupported(e);
            var sink = new MemorySink();
            a.Extract(e, sink, new ArchiveLimits { MaxEntrySize = 1 << 22 });
            result[e.Name] = sink.ToArray();
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_extracts_to_the_expected_content(string name)
    {
        var expected = Get(name).Content();
        var actual = ExtractAll(Load(name));
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (k, v) in expected) Assert.True(v.AsSpan().SequenceEqual(actual[k]), $"content differs: {k}");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_truncation_of_a_real_archive_is_an_archive_error_or_correct(string name)
    {
        var expected = Get(name).Content();
        var bytes = Load(name);
        for (var len = 0; len < bytes.Length; len++)
        {
            try
            {
                var got = ExtractAll(bytes.AsSpan(0, len).ToArray());
                foreach (var (k, v) in got) Assert.True(v.AsSpan().SequenceEqual(expected[k]), $"wrong data accepted at length {len}: {k}");
            }
            catch (ArchiveException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_byte_corruption_never_yields_silently_wrong_data(string name)
    {
        var expected = Get(name).Content();
        var original = Load(name);
        var bytes = (byte[])original.Clone();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < bytes.Length; i++)
        {
            foreach (var mask in new byte[] { 0x01, 0x10, 0x80, 0xFF })
            {
                bytes[i] ^= mask;
                try
                {
                    var got = ExtractAll(bytes);
                    foreach (var (k, v) in got)
                        Assert.True(expected.TryGetValue(k, out var e) && v.AsSpan().SequenceEqual(e), $"wrong data accepted: byte {i} ^ {mask:X2} -> {k}");
                }
                catch (ArchiveException) { }

                bytes[i] = original[i];
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), $"sweep took {clock.Elapsed}: something decodes far too slowly on corrupt input");
    }

    [Fact]
    public void A_huge_declared_dictionary_is_fine_for_small_data_even_under_a_tight_memory_limit()
    {
        var archive = ArchiveReader.Open(Load("rar5-md256m-tiny.rar"));
        var entry = archive.Entries.Single();
        var sink = new MemorySink();
        archive.Extract(entry, sink, new ArchiveLimits { MaxDictionarySize = 64 * 1024 });      // 256 MiB declared, 64 KiB needed
        Assert.Equal(Get("rar5-md256m-tiny.rar").Content()["note.txt"], sink.ToArray());
    }

    [Fact]
    public void Sequential_solid_extraction_decodes_each_file_exactly_once()
    {
        static (Unpakz.Core.Rar.Rar5Archive Archive, List<ArchiveEntry> Files) Fresh()
        {
            var a = (Unpakz.Core.Rar.Rar5Archive)ArchiveReader.Open(Load("rar5-m5-solid.rar"));
            return (a, [.. a.Entries.Where(e => e.Kind == EntryKind.File && e.UncompressedSize > 0)]);
        }

        static void Pull(Unpakz.Core.Rar.Rar5Archive a, ArchiveEntry e) => a.Extract(e, new MemorySink(), new ArchiveLimits());

        var (seq, files) = Fresh();
        foreach (var f in files) Pull(seq, f);
        var members = seq.Stats.FilesDecoded;
        Assert.True(members >= 2, "the fixture must have a real solid chain");

        var (last, lastFiles) = Fresh();
        Pull(last, lastFiles[^1]);
        Assert.Equal(members, last.Stats.FilesDecoded);                       // jumping to the end replays the chain once

        var (mixed, mixedFiles) = Fresh();
        Pull(mixed, mixedFiles[^1]);
        Pull(mixed, mixedFiles[0]);
        Assert.Equal(members + 1, mixed.Stats.FilesDecoded);                  // going back to the head costs just that file
    }

    [Fact]
    public void Regenerate_fixtures_when_asked()
    {
        if (Environment.GetEnvironmentVariable("UNPAKZ_REGEN_FIXTURES") != "1") return;
        Assert.True(Oracle.TryGetRar(out var rar, out _));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Unpakz.Tests"))) dir = dir.Parent;
        var target = Path.Combine(dir!.FullName, "tests", "Unpakz.Tests", "Fixtures");
        Directory.CreateDirectory(target);

        foreach (var f in Fixtures)
        {
            var work = Oracle.NewWorkDir("regen");
            try
            {
                foreach (var (rel, data) in f.Content())
                {
                    var path = Path.Combine(work, "src", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, data);
                }

                var archive = Path.Combine(target, f.File);
                if (File.Exists(archive)) File.Delete(archive);
                List<string> args = ["a", "-ma5", "-r", "-idq", "-y", "-ep1", "-tsm-", "-tsc-", "-tsa-", .. f.Switches, archive, "*"];
                Assert.True(Oracle.Run(rar, args, Path.Combine(work, "src"), out var log) == 0, log);
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
