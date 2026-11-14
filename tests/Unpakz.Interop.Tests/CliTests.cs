using Unpakz.Cli;

namespace Unpakz.Interop.Tests;

public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Help_version_and_usage_errors()
    {
        Assert.Equal(CliApp.Usage, Run().Code);
        Assert.Contains("Usage:", Run("--help").Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.Ok, Run("--version").Code);
        Assert.Equal(CliApp.Usage, Run("l").Code);
        Assert.Equal(CliApp.Usage, Run("q", "x.zip").Code);
        Assert.Equal(CliApp.Usage, Run("x", "x.zip", "--bogus").Code);
        Assert.Equal(CliApp.Usage, Run("x", "x.zip", "-o").Code);
    }

    [Fact]
    public void List_prints_every_entry_for_each_format()
    {
        foreach (var (format, make) in Archives.All())
        {
            using var d = new TempDir();
            var r = Run("l", make(d));
            Assert.Equal(CliApp.Ok, r.Code);
            Assert.Contains(format == "tar.gz" ? "TAR.GZ" : format.ToUpperInvariant(), r.Out, StringComparison.Ordinal);
            Assert.Contains("docs/guide.txt", r.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Extract_uses_the_named_folder_by_default_and_honours_o_here_skip_and_overwrite()
    {
        using var d = new TempDir();
        var zip = Archives.Zip(d, tree: new() { ["a.txt"] = [1, 2, 3], ["sub/b.txt"] = [4] });
        Assert.Equal(CliApp.Ok, Run("x", zip).Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.File("sample/a.txt")));
        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", d.File("custom")).Code);
        Assert.True(File.Exists(d.File("custom/sub/b.txt")));
        Assert.Equal(CliApp.Ok, Run("x", zip, "--here").Code);
        Assert.True(File.Exists(d.File("a.txt")));

        File.WriteAllBytes(d.File("custom/a.txt"), [9]);
        Assert.Equal(CliApp.Failed, Run("x", zip, "-o", d.File("custom")).Code);
        Assert.Equal([9], File.ReadAllBytes(d.File("custom/a.txt")));
        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", d.File("custom"), "--skip").Code);
        Assert.Equal([9], File.ReadAllBytes(d.File("custom/a.txt")));
        Assert.Equal(CliApp.Ok, Run("x", zip, "-o", d.File("custom"), "--overwrite").Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.File("custom/a.txt")));
    }

    [Fact]
    public void Extract_of_paths_takes_only_those_and_reports_unknown_paths()
    {
        using var d = new TempDir();
        var zip = Archives.Zip(d);
        Assert.Equal(CliApp.Ok, Run("x", zip, "docs", "readme.txt", "-o", d.File("o")).Code);
        Assert.True(File.Exists(d.File("o/docs/deep/notes.txt")) && File.Exists(d.File("o/readme.txt")));
        Assert.False(File.Exists(d.File("o/data/blob.bin")));
        var missing = Run("x", zip, "nope.txt", "-o", d.File("o2"));
        Assert.Equal(CliApp.Failed, missing.Code);
        Assert.Contains("Not in the archive", missing.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_decodes_everything_and_writes_nothing_and_flags_damage()
    {
        using var d = new TempDir();
        var good = Run("t", Archives.TarGz(d));
        Assert.Equal(CliApp.Ok, good.Code);
        Assert.Contains("5 file(s) OK, 0 failed", good.Out, StringComparison.Ordinal);
        Assert.Equal(new[] { d.File("sample.tar.gz") }, Directory.GetFileSystemEntries(d.Path));      // testing wrote nothing

        var data = Unpakz.Tests.Support.TestData.Random(3000, 5);
        var zip = new Unpakz.Tests.Support.ZipBuilder().Add("bad.bin", data).Build();
        var at = zip.AsSpan().IndexOf(data.AsSpan(500, 40));
        zip[at + 5] ^= 1;
        var bad = Run("t", d.Write("bad.zip", zip));
        Assert.Equal(CliApp.Failed, bad.Code);
        Assert.Contains("bad.bin", bad.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_are_one_line_with_exit_code_one()
    {
        using var d = new TempDir();
        var r = Run("x", d.File("missing.zip"));
        Assert.Equal(CliApp.Failed, r.Code);
        Assert.DoesNotContain("   at ", r.Err, StringComparison.Ordinal);
        var evil = d.Write("evil.zip", new Unpakz.Tests.Support.ZipBuilder().Add("../escape", [1]).Build());
        var h = Run("x", evil, "-o", d.File("o"));
        Assert.Equal(CliApp.Failed, h.Code);
        Assert.False(File.Exists(d.File("escape")));
    }
}
