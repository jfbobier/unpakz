using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using Unpakz.Engine;
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
        var start = Environment.TickCount64;
        while (!until())
        {
            Application.DoEvents();
            Thread.Sleep(5);
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("The UI did not reach the expected state in time.");
        }
    }

    public static void Settle(int ms = 60)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(5); }
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

/// <summary>Archives of every supported flavour, each with the tree it must extract to.</summary>
internal static class Archives
{
    public sealed record Case(string Name, Func<string, string> Create, Dictionary<string, byte[]> Tree);

    private static Dictionary<string, byte[]> Tree() => new()
    {
        ["readme.txt"] = TestData.Text(3000, 1),
        ["docs/guide.txt"] = TestData.Text(9000, 2),
        ["docs/deep/notes.txt"] = TestData.Text(2000, 3),
        ["data/blob.bin"] = TestData.Random(20_000, 4),
        ["empty.txt"] = [],
    };

    private static Dictionary<string, byte[]> FixtureTree() => new()
    {
        ["text.txt"] = TestData.Text(8000), ["table.bin"] = TestData.Table(4000), ["tiny.txt"] = "hello"u8.ToArray(),
        ["empty.txt"] = [], ["sub/z.txt"] = TestData.Text(3000, 9),
    };

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static readonly Case[] All =
    [
        new("zip", d => { var b = new ZipBuilder(); foreach (var (k, v) in Tree()) b.Add(k, v, deflate: v.Length > 100); var p = Path.Combine(d, "sample.zip"); File.WriteAllBytes(p, b.Build()); return p; }, Tree()),
        new("rar5-stored", d => { var b = new Rar5Builder(); foreach (var (k, v) in Tree()) b.AddFile(k, v); var p = Path.Combine(d, "sample.rar"); File.WriteAllBytes(p, b.Build()); return p; }, Tree()),
        new("rar5-multivolume", d => { var b = new Rar5Builder(); foreach (var (k, v) in Tree()) b.AddFile(k, v); var vols = b.BuildVolumes(5000); for (var i = 0; i < vols.Count; i++) File.WriteAllBytes(Path.Combine(d, $"sample.part{i + 1:D2}.rar"), vols[i]); return Path.Combine(d, "sample.part01.rar"); }, Tree()),
        new("rar5-compressed", d => { var p = Path.Combine(d, "mix.rar"); File.Copy(Fixture("rar5-m3-mix.rar"), p); return p; }, FixtureTree()),
        new("rar5-solid", d => { var p = Path.Combine(d, "solid.rar"); File.Copy(Fixture("rar5-m5-solid.rar"), p); return p; }, FixtureTree()),
        new("rar5-real-volumes", d => { foreach (var f in Directory.GetFiles(Fixture("mv-m3"), "*.rar")) File.Copy(f, Path.Combine(d, Path.GetFileName(f))); return Directory.GetFiles(d, "*part01.rar").Single(); },
            new() { ["a.txt"] = TestData.Text(9000, 3), ["t.bin"] = TestData.Table(3000), ["c.txt"] = TestData.Text(700, 4) }),
    ];

    public static TheoryData<string> Names() => [.. All.Select(c => c.Name)];

    public static Case Get(string name) => All.Single(c => c.Name == name);

    public static Dictionary<string, string> Snapshot(string root) =>
        !Directory.Exists(root) ? [] : Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    public static Dictionary<string, string> Expected(Dictionary<string, byte[]> tree, Func<string, bool>? keep = null) =>
        tree.Where(kv => keep?.Invoke(kv.Key) ?? true).ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)), StringComparer.Ordinal);
}

internal sealed class FixedPicker(string? answer) : IFolderPicker
{
    public int Calls { get; private set; }
    public string? Pick(string title, string? startFolder) { Calls++; return answer; }
}

internal sealed class FixedPrompt(bool answer) : IPrompt
{
    public bool ConfirmReplace(string message) => answer;
}

/// <summary>Lets the first write through, then holds every later write until released: freezes an extraction mid-way.</summary>
internal sealed class GatedFileSystem(IFileSystem inner) : IFileSystem
{
    private int _writes;

    public ManualResetEventSlim Gate { get; } = new(false);

    public PathKind GetKind(string path) => inner.GetKind(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public IFileWriter CreateFile(string finalPath, long preallocate) => new W(inner.CreateFile(finalPath, preallocate), this);

    private sealed class W(IFileWriter w, GatedFileSystem owner) : IFileWriter
    {
        // the very first write of the whole extraction goes through (so progress becomes non-zero); every later one waits for the gate
        public void Write(ReadOnlySpan<byte> data)
        {
            if (Interlocked.Increment(ref owner._writes) > 1) owner.Gate.Wait();
            w.Write(data);
        }

        public void Commit(DateTime? t, bool o) => w.Commit(t, o);

        public void Dispose() => w.Dispose();
    }
}

internal static class FormReflection
{
    public static void Raise(Control c, string method, EventArgs args) =>
        typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, [args]);
}
