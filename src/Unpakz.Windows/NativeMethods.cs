using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Unpakz.Windows;

internal static unsafe partial class NativeMethods
{
    private const uint GmemMoveable = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(IntPtr handle);

#pragma warning disable SYSLIB1054 // STGMEDIUM carries a COM object reference: classic marshalling is the supported route
    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
#pragma warning restore SYSLIB1054

    /// <summary>Copies bytes into a movable global block, the memory kind OLE clipboard/drag formats are exchanged in.</summary>
    internal static IntPtr AllocGlobal(ReadOnlySpan<byte> bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)Math.Max(1, bytes.Length));
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
        var p = GlobalLock(handle);
        try { bytes.CopyTo(new Span<byte>((void*)p, bytes.Length)); }
        finally { GlobalUnlock(handle); }
        return handle;
    }
}
