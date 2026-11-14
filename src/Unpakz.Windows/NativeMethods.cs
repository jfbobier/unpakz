using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Unpakz.Windows;

internal static class NativeMethods
{
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    /// <summary>Copies bytes into a movable global block, the memory kind OLE clipboard/drag formats are exchanged in.</summary>
    internal static IntPtr AllocGlobal(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)Math.Max(1, bytes.Length));
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
        var p = GlobalLock(handle);
        try { Marshal.Copy(bytes, 0, p, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }
}
