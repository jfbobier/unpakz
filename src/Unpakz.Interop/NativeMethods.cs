using System;
using System.Runtime.InteropServices;

namespace Unpakz.Interop;

/// <summary>The flat C API of unpakz.dll (native/include/unpakz.h), declared exactly once. Strings are UTF-16.</summary>
internal static class NativeMethods
{
    private const string Dll = "unpakz_native.dll";
    internal const uint ExpectedAbiVersion = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int EntryCallback(ref NativeEntry entry, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ProgressCallback(ref NativeProgress progress, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CancelCallback(IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DataCallback(IntPtr data, ulong size, IntPtr user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeError
    {
        public uint StructSize;
        public int Status;
        public int Volume;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string Message;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeLimits
    {
        public uint StructSize;
        public uint MaxVolumes;
        public ulong MaxEntries;
        public ulong MaxEntrySize;
        public ulong MaxTotalSize;
        public uint MaxPathLength;
        public uint MaxPathDepth;
        public uint MaxSegmentLength;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEntry
    {
        public uint StructSize;
        public int Kind;
        public ulong Index;
        public IntPtr Name;
        public IntPtr RelativePath;
        public ulong Size;
        public int SizeKnown;
        public int HasMtime;
        public long MtimeUnixSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeProgress
    {
        public uint StructSize;
        public uint Reserved;
        public ulong BytesDone;
        public ulong BytesTotal;
        public IntPtr CurrentName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeExtractOptions
    {
        public uint StructSize;
        public int Overwrite;
        public int RestoreTimestamps;
        public int PreflightPass;
        public NativeLimits Limits;
        public EntryCallback? Filter;
        public ProgressCallback? Progress;
        public CancelCallback? Cancelled;
        public IntPtr User;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeResult
    {
        public uint StructSize;
        public uint Reserved;
        public ulong Files;
        public ulong Directories;
        public ulong Bytes;
        public ulong Skipped;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint unpakz_abi_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void unpakz_default_limits(ref NativeLimits limits);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void unpakz_default_extract_options(ref NativeExtractOptions options);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int unpakz_list(string path, ref NativeLimits limits, EntryCallback callback, IntPtr user, ref NativeError error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int unpakz_read_entry(string path, ulong entryIndex, IntPtr limits, DataCallback callback, IntPtr user, ref NativeError error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int unpakz_extract(string path, string destination, ref NativeExtractOptions options, ref NativeResult result, ref NativeError error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int unpakz_check_name([MarshalAs(UnmanagedType.LPWStr)] string name, [Out] char[] output, uint outputChars, ref NativeError error);
}
