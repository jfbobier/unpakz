using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using Unpakz.Interop;
using Unpakz.Presentation;
using Unpakz.Tests.Support;

namespace Unpakz.Windows.Tests;

internal static class Sta
{
    /// <summary>Runs on a fresh single-threaded-apartment thread, as WinForms requires.</summary>
    public static T Run<T>(Func<T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    public static void Run(Action body) => Run(() => { body(); return 0; });

    public static void Pump(Func<bool> until, int timeoutMs = 30_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!until())
        {
            Application.DoEvents();
            Thread.Sleep(5);
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The UI did not reach the expected state in time.");
        }
    }

    public static void Settle(int ms = 60)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }
}

internal static class Ui
{
    public static T Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, true).OfType<T>().SingleOrDefault() ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'.");

    public static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    public static void Place(Form form, double scale, Size clientSize)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.ShowInTaskbar = false;
        form.Font = new Font(form.Font.FontFamily, (float)(form.Font.Size * scale));
        form.ClientSize = new Size((int)(clientSize.Width * scale), (int)(clientSize.Height * scale));
        form.Show();
        form.PerformLayout();
        Sta.Settle();
    }

    public static string Screenshot(Form form, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "ui-shots");
        Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        var path = Path.Combine(dir, name + ".png");
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    /// <summary>Layout problems found in a rendered window; empty means it looks sane.</summary>
    public static List<string> LayoutProblems(Form form)
    {
        var problems = new List<string>();
        var client = form.ClientRectangle;
        foreach (var c in All(form).Where(c => c.Visible))
        {
            var r = form.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            if (c is not ListView && (r.Left < client.Left - 1 || r.Top < client.Top - 1 || r.Right > client.Right + 1 || r.Bottom > client.Bottom + 1))
                if (!(c.Parent is ScrollableControl { AutoScroll: true }))
                    problems.Add($"{Describe(c)} sticks out of the window: {r} vs {client}");

            if (c is Button b && !string.IsNullOrEmpty(b.Text))
            {
                var need = TextRenderer.MeasureText(b.Text, b.Font).Width + b.Padding.Horizontal;
                if (need > b.Width + 2) problems.Add($"{Describe(c)} text is clipped: needs {need}px, has {b.Width}px");
            }

            if (c is Label { AutoSize: true } l && l.Text.Length > 0 && l.Visible)
            {
                var wrapWidth = l.MaximumSize.Width > 0 ? l.MaximumSize.Width : int.MaxValue;
                var measured = TextRenderer.MeasureText(l.Text, l.Font, new Size(wrapWidth, int.MaxValue), TextFormatFlags.WordBreak);
                if (l.Height + 3 < measured.Height) problems.Add($"{Describe(c)} text is cut off vertically");
            }
        }

        foreach (var panel in All(form).Where(c => c is TableLayoutPanel or FlowLayoutPanel))
        {
            var kids = panel.Controls.Cast<Control>().Where(k => k.Visible && k is Button or ProgressBar or Panel).ToList();
            for (var i = 0; i < kids.Count; i++)
                for (var j = i + 1; j < kids.Count; j++)
                    if (kids[i].Bounds.IntersectsWith(kids[j].Bounds)) problems.Add($"{Describe(kids[i])} overlaps {Describe(kids[j])}");
        }

        return problems;
    }

    private static string Describe(Control c) => string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;
}

/// <summary>Archives of every supported flavour, built by independent writers, each with the tree it must extract to.</summary>
internal static class Samples
{
    public static readonly Dictionary<string, byte[]> Tree = new()
    {
        ["readme.txt"] = Unpakz.Tests.Support.TestData.Text(3000, 1),
        ["docs/guide.txt"] = Unpakz.Tests.Support.TestData.Text(9000, 2),
        ["docs/deep/notes.txt"] = Unpakz.Tests.Support.TestData.Text(2000, 3),
        ["data/blob.bin"] = Unpakz.Tests.Support.TestData.Random(20_000, 4),
        ["empty.txt"] = [],
    };

    public static string Dir() { var d = Path.Combine(Path.GetTempPath(), "unpakz-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

    public static string Zip(string dir, Dictionary<string, byte[]>? tree = null)
    {
        var b = new Unpakz.Tests.Support.ZipBuilder();
        foreach (var (k, v) in tree ?? Tree) b.Add(k, v, deflate: v.Length > 100);
        var p = Path.Combine(dir, "sample.zip");
        File.WriteAllBytes(p, b.Build());
        return p;
    }

    public static string Rar(string dir)
    {
        var b = new Unpakz.Tests.Support.Rar5Builder();
        foreach (var (k, v) in Tree) b.AddFile(k, v);
        var p = Path.Combine(dir, "sample.rar");
        File.WriteAllBytes(p, b.Build());
        return p;
    }

    public static string RealRar(string dir)
    {
        var p = Path.Combine(dir, "real.rar");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rar5-m3-mix.rar"), p);
        return p;
    }

    public static string MultiVolume(string dir)
    {
        var b = new Unpakz.Tests.Support.Rar5Builder();
        foreach (var (k, v) in Tree) b.AddFile(k, v);
        var vols = b.BuildVolumes(5000);
        for (var i = 0; i < vols.Count; i++) File.WriteAllBytes(Path.Combine(dir, $"sample.part{i + 1:D2}.rar"), vols[i]);
        return Path.Combine(dir, "sample.part01.rar");
    }

    public static string Broken(string dir)
    {
        var p = Path.Combine(dir, "broken.zip");
        File.WriteAllBytes(p, Unpakz.Tests.Support.TestData.Random(3000, 9));
        return p;
    }

    public static string Hostile(string dir)
    {
        var p = Path.Combine(dir, "evil.zip");
        File.WriteAllBytes(p, new Unpakz.Tests.Support.ZipBuilder().Add("ok", [1]).Add("../escape", [2]).Build());
        return p;
    }
}

internal sealed class FakePicker : IFolderPicker
{
    private readonly string? _answer;
    public FakePicker(string? answer) => _answer = answer;
    public string? Pick(string title, string? startFolder) => _answer;
}

internal sealed class FakePrompt : IPrompt
{
    private readonly bool _answer;
    public int Asked;
    public FakePrompt(bool answer) => _answer = answer;
    public bool ConfirmReplace(string message) { Asked++; return _answer; }
}
