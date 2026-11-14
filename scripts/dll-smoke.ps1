# Calls unpakz.dll from Windows PowerShell (which runs on .NET Framework, like the planned front ends) through P/Invoke.
param([string]$Dll = "native\build-msvc\unpakz_native.dll", [string]$Archive, [string]$Dest)
$src = @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class U {
  [DllImport(@"$((Resolve-Path $Dll).Path.Replace('\','\\'))", CallingConvention=CallingConvention.Cdecl)] public static extern uint unpakz_abi_version();
  [DllImport(@"$((Resolve-Path $Dll).Path.Replace('\','\\'))", CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr unpakz_library_version();
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct Err { public uint size; public int status; public int volume; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=512)] public string message; }
  [StructLayout(LayoutKind.Sequential)] public struct Limits { public uint size; public uint maxVolumes; public ulong maxEntries, maxEntrySize, maxTotal; public uint pathLen, depth, segLen, reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct Opts { public uint size; public int overwrite, restoreTimes, preflight; public Limits limits; public IntPtr filter, progress, cancel, user; }
  [StructLayout(LayoutKind.Sequential)] public struct Res { public uint size, reserved; public ulong files, dirs, bytes, skipped; }
  [DllImport(@"$((Resolve-Path $Dll).Path.Replace('\','\\'))", CallingConvention=CallingConvention.Cdecl)] public static extern void unpakz_default_extract_options(ref Opts o);
  [DllImport(@"$((Resolve-Path $Dll).Path.Replace('\','\\'))", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] public static extern int unpakz_extract(string path, string dest, ref Opts o, ref Res r, ref Err e);
}
"@
Add-Type -TypeDefinition $src
"CLR: " + [Environment]::Version + "  PowerShell " + $PSVersionTable.PSVersion
"ABI: " + [U]::unpakz_abi_version() + "  " + [Runtime.InteropServices.Marshal]::PtrToStringAnsi([U]::unpakz_library_version())
$o = New-Object U+Opts; $o.size = [Runtime.InteropServices.Marshal]::SizeOf([type][U+Opts]); [U]::unpakz_default_extract_options([ref]$o)
$r = New-Object U+Res; $r.size = [Runtime.InteropServices.Marshal]::SizeOf([type][U+Res])
$e = New-Object U+Err; $e.size = [Runtime.InteropServices.Marshal]::SizeOf([type][U+Err]); $e.message = ""
$st = [U]::unpakz_extract($Archive, $Dest, [ref]$o, [ref]$r, [ref]$e)
"status=$st files=$($r.files) dirs=$($r.dirs) bytes=$($r.bytes) message='$($e.message)'"
