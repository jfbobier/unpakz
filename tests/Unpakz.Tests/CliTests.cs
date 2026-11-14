using Unpakz.Cli;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

[Collection("ArchiveSource")]
public sealed class CliTests : IDisposable
{
    private readonly string _dir = Oracle.NewWorkDir("cli");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private string Zip(string name = "pack.zip")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new ZipBuilder().Add("a.txt", "AAA"u8.ToArray()).Add("d/b.txt", "BBB"u8.ToArray()).Add("d/e/c.txt", "CCC"u8.ToArray()).Add("z.txt", "ZZZ"u8.ToArray()).Build());
        return p;
    }

    [Fact]
    public void Help_and_version_and_usage_errors()
    {
        Assert.Equal(CliApp.Ok, Run("--help").Code);
        Assert.Contains("unpakz l", Run("--help").Out, StringComparison.Ordinal);
        Assert.Contains("experimental", Run("--version").Out, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CliApp.Usage, Run().Code);
        Assert.Equal(CliApp.Usage, Run("x").Code);
        Assert.Equal(CliApp.Usage, Run("frobnicate", "a.zip").Code);
        Assert.Equal(CliApp.Usage, Run("x", Zip(), "--nope").Code);
        Assert.Equal(CliApp.Usage, Run("x", Zip(), "-o").Code);
    }

    [Fact]
    public void List_shows_every_entry()
    {
        var r = Run("l", Zip());
        Assert.Equal(CliApp.Ok, r.Code);
        foreach (var n in new[] { "a.txt", "d/b.txt", "d/e/c.txt", "z.txt" }) Assert.Contains(n, r.Out, StringComparison.Ordinal);
        Assert.Contains("ZIP", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_defaults_to_a_folder_named_after_the_archive()
    {
        var r = Run("x", Zip());
        Assert.Equal(CliApp.Ok, r.Code);
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(_dir, "pack", "d", "b.txt")));
        Assert.Contains("4 file(s)", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_to_a_chosen_folder_and_here()
    {
        var zip = Zip();
        var out1 = Path.Combine(_dir, "chosen");
        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", out1).Code);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(out1, "a.txt")));

        Assert.Equal(CliApp.Ok, Run("x", zip, "--here").Code);
        Assert.Equal("ZZZ", File.ReadAllText(Path.Combine(_dir, "z.txt")));
    }

    [Fact]
    public void Extract_only_the_named_files_and_folders()
    {
        var o = Path.Combine(_dir, "part");
        Assert.Equal(CliApp.Ok, Run("x", Zip(), "z.txt", "d/e", "-o", o).Code);
        Assert.Equal(["d/e/c.txt", "z.txt"], Directory.GetFiles(o, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(o, f).Replace('\\', '/')).Order(StringComparer.Ordinal));

        var missing = Run("x", Zip(), "nope.txt", "-o", Path.Combine(_dir, "none"));
        Assert.Equal(CliApp.Failed, missing.Code);
        Assert.Contains("Not in the archive", missing.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "none")));
    }

    [Fact]
    public void Existing_files_stop_the_run_unless_told_otherwise()
    {
        var zip = Zip();
        var o = Path.Combine(_dir, "ex");
        Directory.CreateDirectory(o);
        File.WriteAllText(Path.Combine(o, "z.txt"), "MINE");

        var stop = Run("x", zip, "-o", o);
        Assert.Equal(CliApp.Failed, stop.Code);
        Assert.Contains("already exists", stop.Err, StringComparison.Ordinal);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(o, "z.txt")));
        Assert.False(File.Exists(Path.Combine(o, "a.txt")));                    // nothing else was written either

        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", o, "--skip").Code);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(o, "z.txt")));
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(o, "a.txt")));

        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", o, "--overwrite").Code);
        Assert.Equal("ZZZ", File.ReadAllText(Path.Combine(o, "z.txt")));
    }

    [Fact]
    public void Test_command_checks_everything_and_writes_nothing()
    {
        var good = Run("t", Zip());
        Assert.Equal(CliApp.Ok, good.Code);
        Assert.Contains("4 file(s) OK", good.Out, StringComparison.Ordinal);
        Assert.Equal(["pack.zip"], Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName));

        var zip = new ZipBuilder().Add("x.bin", new byte[1000]).Build();
        zip[30 + 5 + 500] ^= 1;
        var bad = Path.Combine(_dir, "bad.zip");
        File.WriteAllBytes(bad, zip);
        var r = Run("t", bad);
        Assert.Equal(CliApp.Failed, r.Code);
        Assert.Contains("x.bin", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_archives_are_refused_without_writing_anything()
    {
        var evil = Path.Combine(_dir, "evil.zip");
        File.WriteAllBytes(evil, new ZipBuilder().Add("ok.txt", "1"u8.ToArray()).Add("../../escape.txt", "2"u8.ToArray()).Build());
        var r = Run("x", evil, "-o", Path.Combine(_dir, "out"));
        Assert.Equal(CliApp.Failed, r.Code);
        Assert.Contains("Nothing was extracted", r.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "..", "escape.txt"))));
    }

    [Fact]
    public void Missing_files_missing_parts_and_junk_give_messages_and_exit_one()
    {
        Assert.Equal(CliApp.Failed, Run("l", Path.Combine(_dir, "nope.zip")).Code);
        File.WriteAllBytes(Path.Combine(_dir, "junk.zip"), new byte[100]);
        Assert.Equal(CliApp.Failed, Run("x", Path.Combine(_dir, "junk.zip")).Code);

        var vols = new Rar5Builder().AddFile("a", new byte[9000]).BuildVolumes(2500);
        for (var i = 0; i < vols.Count; i++) if (i != 2) File.WriteAllBytes(Path.Combine(_dir, $"s.part{i + 1}.rar"), vols[i]);
        var r = Run("x", Path.Combine(_dir, "s.part1.rar"));
        Assert.Equal(CliApp.Failed, r.Code);
        Assert.Contains("Part 3", r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_part_rar_set_extracts_from_any_part()
    {
        var data = TestData.Random(30_000, 3);
        var vols = new Rar5Builder().AddFile("big.bin", data).AddFile("note.txt", "hi"u8.ToArray()).BuildVolumes(7000);
        for (var i = 0; i < vols.Count; i++) File.WriteAllBytes(Path.Combine(_dir, $"set.part{i + 1:D2}.rar"), vols[i]);
        var o = Path.Combine(_dir, "set-out");
        var r = Run("x", Path.Combine(_dir, "set.part03.rar"), "-o", o);
        Assert.Equal(CliApp.Ok, r.Code);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(o, "big.bin")));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(o, "note.txt")));
    }

    [Fact]
    public void Links_in_an_archive_are_reported_as_skipped_and_never_created()
    {
        var p = Path.Combine(_dir, "l.zip");
        File.WriteAllBytes(p, new ZipBuilder().Add("link", "/etc/passwd"u8.ToArray(), externalAttrs: ZipBuilder.SymlinkAttr).Add("f", "1"u8.ToArray()).Build());
        var r = Run("x", p, "-o", Path.Combine(_dir, "lo"));
        Assert.Equal(CliApp.Ok, r.Code);
        Assert.Contains("skipped", r.Out, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "lo", "link")));
    }

    [Fact]
    public void Control_characters_in_names_cannot_reach_the_terminal()
    {
        var p = Path.Combine(_dir, "ctl.zip");
        File.WriteAllBytes(p, new ZipBuilder().Add("bad\u001b[31mname.txt", "1"u8.ToArray()).Build());
        var listing = Run("l", p);
        Assert.DoesNotContain('\u001b', listing.Out);
    }

    [Fact]
    public void Cancellation_returns_the_cancelled_code()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var o = new StringWriter();
        using var e = new StringWriter();
        Assert.Equal(CliApp.Cancelled, CliApp.Run(["t", Zip()], o, e, cts.Token));
    }
}
