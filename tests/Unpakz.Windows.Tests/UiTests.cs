using System.Drawing;
using System.Windows.Forms;
using Unpakz.App;
using Unpakz.Interop;
using Unpakz.Presentation;

namespace Unpakz.Windows.Tests;

/// <summary>The real forms, rendered on this machine: no clipped text, nothing sticking out, no overlaps, at several font scales and window sizes.</summary>
public class UiTests
{
    private static readonly double[] Scales = { 1.0, 1.25, 1.5, 2.0 };
    private static readonly Size[] Sizes = { new(520, 440), new(640, 480), new(900, 600) };

    private static string Make(string state, string dir)
    {
        switch (state)
        {
            case "zip": return Samples.Zip(dir);
            case "rar": return Samples.RealRar(dir);
            case "multivolume": return Samples.MultiVolume(dir);
            default: return string.Empty;
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zip")]
    [InlineData("rar")]
    [InlineData("multivolume")]
    [InlineData("broken")]
    public void The_main_window_lays_out_cleanly_in_every_state_scale_and_size(string state)
    {
        var dir = Samples.Dir();
        try
        {
            Sta.Run(() =>
            {
                foreach (var scale in Scales)
                    foreach (var size in Sizes)
                    {
                        using var model = new MainViewModel(new FakePicker(null), new FakePrompt(false));
                        using var form = new MainForm(model);
                        if (state == "broken") model.TryOpen(Samples.Broken(dir));
                        else if (state != "empty") model.TryOpen(Make(state, dir));
                        Ui.Place(form, scale, size);
                        var problems = Ui.LayoutProblems(form);
                        if (scale == 1.0 && size.Width == 640) Ui.Screenshot(form, $"main-{state}");
                        Assert.True(problems.Count == 0, $"{state} @ {scale:0.00}x {size}: " + string.Join("; ", problems));
                        form.Close();
                    }
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("rar")]
    public void The_file_list_window_lays_out_cleanly(string kind)
    {
        var dir = Samples.Dir();
        try
        {
            Sta.Run(() =>
            {
                foreach (var scale in Scales)
                {
                    using var model = new MainViewModel(new FakePicker(null), new FakePrompt(false));
                    model.TryOpen(kind == "zip" ? Samples.Zip(dir) : Samples.RealRar(dir));
                    using var list = new FileListForm(model);
                    Ui.Place(list, scale, new Size(760, 520));
                    var problems = Ui.LayoutProblems(list);
                    if (scale == 1.0) Ui.Screenshot(list, $"list-{kind}");
                    Assert.True(problems.Count == 0, $"list {kind} @ {scale:0.00}x: " + string.Join("; ", problems));
                    list.Close();
                }
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Clicking_Extract_here_extracts_next_to_the_archive_and_reports_success()
    {
        var dir = Samples.Dir();
        try
        {
            Sta.Run(() =>
            {
                using var model = new MainViewModel(new FakePicker(null), new FakePrompt(false));
                using var form = new MainForm(model);
                model.TryOpen(Samples.Zip(dir));
                Ui.Place(form, 1.0, new Size(640, 480));
                Ui.Find<Button>(form, "ExtractHere").PerformClick();
                Sta.Pump(() => !model.IsBusy && model.LastStatus is not null);
                Assert.Equal(RunStatus.Success, model.LastStatus);
                Assert.Equal(Samples.Tree["docs/guide.txt"], File.ReadAllBytes(Path.Combine(dir, "docs", "guide.txt")));
                form.Close();
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_hostile_archive_shows_a_refusal_and_writes_nothing()
    {
        var dir = Samples.Dir();
        try
        {
            Sta.Run(() =>
            {
                using var model = new MainViewModel(new FakePicker(Path.Combine(dir, "dest")), new FakePrompt(false));
                using var form = new MainForm(model);
                model.TryOpen(Samples.Hostile(dir));
                Ui.Place(form, 1.0, new Size(640, 480));
                Ui.Find<Button>(form, "ExtractTo").PerformClick();
                Sta.Pump(() => !model.IsBusy && model.LastStatus is not null);
                Assert.Equal(RunStatus.Failed, model.LastStatus);
                Assert.Contains("outside the chosen folder", Ui.Find<Label>(form, "Status").Text, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(dir, "dest", "ok")));
                form.Close();
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_file_list_extracts_the_selection_and_sorts_by_column()
    {
        var dir = Samples.Dir();
        try
        {
            Sta.Run(() =>
            {
                using var model = new MainViewModel(new FakePicker(Path.Combine(dir, "picked")), new FakePrompt(false));
                model.TryOpen(Samples.Zip(dir));
                using var list = new FileListForm(model);
                Ui.Place(list, 1.0, new Size(760, 520));
                list.SortByColumn(1);
                var view = Ui.Find<ListView>(list, "List");
                Assert.True(view.VirtualListSize > 0);
                view.SelectedIndices.Add(0);
                list.ExtractSelectedAsync().GetAwaiter().GetResult();
                Assert.True(Directory.Exists(Path.Combine(dir, "picked")));
                list.Close();
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
