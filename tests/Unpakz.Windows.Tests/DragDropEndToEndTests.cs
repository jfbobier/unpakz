using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Unpakz.Presentation;
using Unpakz.Tests.Support;
using Unpakz.Windows;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Unpakz.Windows.Tests;

internal enum DragOutcome { Dropped, Cancelled, Failed }

[StructLayout(LayoutKind.Sequential)]
internal struct PointL { public int X; public int Y; }

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComIDataObject data, uint keyState, PointL pt, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, PointL pt, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComIDataObject data, uint keyState, PointL pt, ref uint effect);
}

/// <summary>What Explorer does on a drop: read the descriptor list, then pull each file's IStream and write it to the destination folder.</summary>
[ComVisible(true)]
internal sealed class ExplorerLikeTarget(string destination) : IDropTarget
{
    public bool Entered { get; private set; }
    public bool Dropped { get; private set; }
    public Exception? Error { get; private set; }

    public int DragEnter(ComIDataObject data, uint keyState, PointL pt, ref uint effect)
    {
        Entered = true;
        effect = 1;
        return 0;
    }

    public int DragOver(uint keyState, PointL pt, ref uint effect)
    {
        effect = 1;
        return 0;
    }

    public int DragLeave() => 0;

    public int Drop(ComIDataObject data, uint keyState, PointL pt, ref uint effect)
    {
        try
        {
            var f = new FORMATETC { cfFormat = (short)VirtualFileDataObject.FormatId("FileGroupDescriptorW"), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            data.GetData(ref f, out var m);
            var size = (int)NativeMethods.GlobalSize(m.unionmember);
            var p = NativeMethods.GlobalLock(m.unionmember);
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            NativeMethods.GlobalUnlock(m.unionmember);
            NativeMethods.ReleaseStgMedium(ref m);

            var count = BitConverter.ToInt32(bytes, 0);
            for (var i = 0; i < count; i++)
            {
                var d = bytes.AsSpan(4 + (592 * i), 592);
                var attrs = BitConverter.ToUInt32(d.Slice(36, 4));
                var name = System.Text.Encoding.Unicode.GetString(d.Slice(72, 520)).TrimEnd('\0');
                var target = Path.Combine(destination, name);
                if ((attrs & 0x10) != 0) { Directory.CreateDirectory(target); continue; }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var cf = new FORMATETC { cfFormat = (short)VirtualFileDataObject.FormatId("FileContents"), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = i, tymed = TYMED.TYMED_ISTREAM };
                data.GetData(ref cf, out var sm);
                var stream = (IStream)Marshal.GetObjectForIUnknown(sm.unionmember);
                using var output = File.Create(target);
                var buffer = new byte[65_536];
                var countPtr = Marshal.AllocCoTaskMem(4);
                try
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, countPtr);
                        var n = Marshal.ReadInt32(countPtr);
                        if (n == 0) break;
                        output.Write(buffer, 0, n);
                    }
                }
                finally { Marshal.FreeCoTaskMem(countPtr); }
            }

            Dropped = true;
            effect = 1;
            return 0;
        }
        catch (Exception ex)
        {
            Error = ex;
            effect = 0;
            return unchecked((int)0x80004005);
        }
    }
}

#pragma warning disable SYSLIB1054, CA5392
internal static class Win32
{
    [DllImport("ole32.dll")] internal static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);
    [DllImport("ole32.dll")] internal static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    internal const uint LeftDown = 0x0002, LeftUp = 0x0004, MoveAbsolute = 0x8001;

    /// <summary>A real mouse-move input event to a screen position (SetCursorPos alone does not generate the messages OLE tracks).</summary>
    internal static void MoveMouse(int x, int y)
    {
        var w = GetSystemMetrics(0);
        var h = GetSystemMetrics(1);
        mouse_event(MoveAbsolute, (int)(x * 65535L / (w - 1)), (int)(y * 65535L / (h - 1)), 0, UIntPtr.Zero);
    }

    internal static bool DesktopIsInteractive()
    {
        var d = OpenInputDesktop(0, false, 0x0100);       // DESKTOP_SWITCHDESKTOP: fails on a locked workstation
        if (d == IntPtr.Zero) return false;
        CloseDesktop(d);
        return true;
    }
}
#pragma warning restore SYSLIB1054, CA5392

/// <summary>
/// A real OLE drag, performed with the real mouse, into an Explorer-like target. Opt-in because it moves the cursor:
/// set UNPAKZ_INTERACTIVE_TESTS=1 and keep the desktop unlocked and hands off the mouse while it runs.
/// </summary>
public sealed class DragDropEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "dd-" + Guid.NewGuid().ToString("N")[..8]);

    public DragDropEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static bool Enabled => Environment.GetEnvironmentVariable("UNPAKZ_INTERACTIVE_TESTS") == "1";

    [Fact]
    public void Dragging_archive_contents_into_a_drop_target_delivers_every_file_with_no_temp_copy()
    {
        const string loop = "winforms";
        if (!Enabled || !Win32.DesktopIsInteractive()) return;     // opt-in; documented in HARNESS.md
        if (!InjectedDragWorks()) { Console.Error.WriteLine("SKIPPED: injected mouse input cannot start even a standard WinForms drag on this machine, so this test cannot discriminate."); return; }

        var tree = new Dictionary<string, byte[]>
        {
            ["a.txt"] = TestData.Text(4000, 1),
            ["docs/big.bin"] = TestData.Random(2_500_000, 2),
            ["docs/sub/c.txt"] = TestData.Text(300, 3),
            ["empty.txt"] = [],
        };
        var b = new ZipBuilder();
        foreach (var (k, v) in tree) b.Add(k, v, deflate: v.Length < 1_000_000);
        b.AddDirectory("docs/sub");
        var zip = Path.Combine(_dir, "src.zip");
        File.WriteAllBytes(zip, b.Build());
        var dest = Path.Combine(_dir, "dropped-" + loop);
        Directory.CreateDirectory(dest);

        var result = Sta.Run(() =>
        {
            // Absolute mouse input is in physical pixels: without this, window coordinates are DPI-virtualised and the cursor lands elsewhere.
            Win32.SetProcessDpiAwarenessContext(new IntPtr(-4));          // PER_MONITOR_AWARE_V2 (fails harmlessly if windows already exist)
            using var vm = new MainViewModel(new FixedPicker(null), new FixedPrompt(false));
            Assert.True(vm.TryOpen(zip), vm.Status);
            var drag = vm.PrepareDrag(null)!;

            using var target = new Form { Text = "drop here", StartPosition = FormStartPosition.Manual, Location = new Point(300, 250), Size = new Size(320, 220), TopMost = true };
            using var source = new Form { Text = "drag from here", StartPosition = FormStartPosition.Manual, Location = new Point(760, 250), Size = new Size(220, 220), TopMost = true };
            target.Show();
            source.Show();
            Sta.Settle(300);

            var dropTarget = new ExplorerLikeTarget(dest);
            Assert.Equal(0, Win32.RegisterDragDrop(target.Handle, dropTarget));
            try
            {
                var from = source.PointToScreen(new Point(source.ClientSize.Width / 2, source.ClientSize.Height / 2));
                var to = target.PointToScreen(new Point(target.ClientSize.Width / 2, target.ClientSize.Height / 2));
                Win32.MoveMouse(from.X, from.Y);
                Sta.Settle(200);
                Win32.mouse_event(Win32.LeftDown, 0, 0, 0, UIntPtr.Zero);
                Sta.Settle(150);
                Console.Error.WriteLine($"BUTTON after injected press: async={(Win32.GetAsyncKeyState(1) & 0x8000) != 0} winforms={Control.MouseButtons} cursor={Cursor.Position} at-source={from}");

                var mover = new Thread(() =>
                {
                    Thread.Sleep(400);
                    for (var i = 1; i <= 30; i++)
                    {
                        Win32.MoveMouse(from.X + ((to.X - from.X) * i / 30), from.Y + ((to.Y - from.Y) * i / 30));
                        Thread.Sleep(30);
                    }

                    Thread.Sleep(400);
                    Win32.mouse_event(Win32.LeftUp, 0, 0, 0, UIntPtr.Zero);
                });
                mover.Start();

                // Safety net: an OLE drag loop that never sees the button release would block this thread forever.
                var watchdog = new Thread(() =>
                {
                    Thread.Sleep(12_000);
                    for (var i = 0; i < 3; i++) { Win32.keybd_event(0x1B, 0, 0, UIntPtr.Zero); Win32.keybd_event(0x1B, 0, 2, UIntPtr.Zero); Thread.Sleep(300); }
                    Win32.mouse_event(Win32.LeftUp, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(8_000);
                    Console.Error.WriteLine("DRAG WATCHDOG: the OLE drag loop did not end; aborting the test process.");
                    Environment.Exit(99);
                }) { IsBackground = true };
                watchdog.Start();

                using var data = new VirtualFileDataObject(drag.Items, vm.OpenDragStream);
                EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> log = (_, e) =>
                    Console.Error.WriteLine("FIRST-CHANCE: " + e.Exception.GetType().Name + ": " + e.Exception.Message + " @ " + (e.Exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
                AppDomain.CurrentDomain.FirstChanceException += log;
                DragOutcome outcome;
                var effect = source.DoDragDrop(data, DragDropEffects.Copy);
                outcome = effect == DragDropEffects.Copy ? DragOutcome.Dropped : DragOutcome.Cancelled;

                                mover.Join();
                Console.Error.WriteLine($"CURSOR from={from} to={to} now={Cursor.Position} targetBounds={target.Bounds} dpi={target.DeviceDpi}");
                AppDomain.CurrentDomain.FirstChanceException -= log;
                Sta.Settle(200);
                return (outcome, dropTarget.Entered, dropTarget.Dropped, dropTarget.Error);
            }
            finally
            {
                _ = Win32.RevokeDragDrop(target.Handle);
                Win32.mouse_event(Win32.LeftUp, 0, 0, 0, UIntPtr.Zero);
            }
        });

        Assert.Null(result.Error);
        Assert.True(result.Entered, $"the drop target never saw the drag (outcome: {result.outcome})");
        Assert.True(result.Dropped, "the drop never happened");
        Assert.Equal(DragOutcome.Dropped, result.outcome);
        foreach (var (k, v) in tree) Assert.True(v.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dest, k))), k);
        Assert.True(Directory.Exists(Path.Combine(dest, "docs", "sub")));
        Assert.Empty(Directory.GetFiles(_dir, ".unpakz-*", SearchOption.AllDirectories));
    }

    /// <summary>The control: does injected mouse input start a drag that a completely standard WinForms target sees?</summary>
    private static bool InjectedDragWorks() => Sta.Run(() =>
    {
        Win32.SetProcessDpiAwarenessContext(new IntPtr(-4));
        using var target = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(300, 250), Size = new Size(320, 220), TopMost = true, AllowDrop = true };
        using var source = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(760, 250), Size = new Size(220, 220), TopMost = true };
        var sawEnter = false;
        target.DragEnter += (_, e) => { sawEnter = true; e.Effect = DragDropEffects.Copy; };
        target.Show();
        source.Show();
        Sta.Settle(400);

        var from = source.PointToScreen(new Point(110, 110));
        var to = target.PointToScreen(new Point(160, 110));
        Win32.MoveMouse(from.X, from.Y);
        Sta.Settle(200);
        Win32.mouse_event(Win32.LeftDown, 0, 0, 0, UIntPtr.Zero);
        Sta.Settle(100);
        var mover = new Thread(() =>
        {
            Thread.Sleep(300);
            for (var i = 1; i <= 30; i++) { Win32.MoveMouse(from.X + ((to.X - from.X) * i / 30), from.Y + ((to.Y - from.Y) * i / 30)); Thread.Sleep(30); }
            Thread.Sleep(300);
            Win32.mouse_event(Win32.LeftUp, 0, 0, 0, UIntPtr.Zero);
        });
        mover.Start();
        source.DoDragDrop(new DataObject(DataFormats.Text, "hello"), DragDropEffects.Copy);
        mover.Join();
        return sawEnter;
    });
}
