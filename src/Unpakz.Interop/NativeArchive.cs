using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Unpakz.Interop;

public enum UnpakzStatus
{
    Ok = 0,
    Corrupt = 1,
    Unsupported = 2,
    LimitExceeded = 3,
    UnsafePath = 4,
    Conflict = 5,
    MissingVolume = 6,
    Io = 7,
    Cancelled = 8,
    InvalidArgument = 9,
    Internal = 10,
}

public enum EntryKind { File = 0, Directory = 1, Symlink = 2, Hardlink = 3, Special = 4 }

public enum OverwritePolicy { Fail = 0, Skip = 1, Replace = 2 }

/// <summary>Any failure reported by unpakz.dll, with its typed status.</summary>
public sealed class UnpakzException : Exception
{
    public UnpakzException(UnpakzStatus status, string message, int volume = 0)
        : base(message)
    {
        Status = status;
        Volume = volume;
    }

    public UnpakzStatus Status { get; }

    /// <summary>With <see cref="UnpakzStatus.MissingVolume"/>: the 1-based number of the missing volume.</summary>
    public int Volume { get; }
}

public sealed class EntryInfo
{
    public long Index { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>After the native guard ('/' separated); empty if the name was refused.</summary>
    public string RelativePath { get; set; } = string.Empty;
    public EntryKind Kind { get; set; }
    public ulong Size { get; set; }
    public bool SizeKnown { get; set; }
    public DateTime? LastWriteUtc { get; set; }
}

public sealed class ExtractOptions
{
    public OverwritePolicy Overwrite { get; set; }
    public bool RestoreTimestamps { get; set; } = true;
    public bool PreflightPass { get; set; } = true;
    public Func<EntryInfo, bool>? Filter { get; set; }
    public Action<ulong, ulong, string>? Progress { get; set; }
    public CancellationToken Cancellation { get; set; }
}

public sealed class ExtractResult
{
    public ulong Files { get; set; }
    public ulong Directories { get; set; }
    public ulong Bytes { get; set; }
    public ulong Skipped { get; set; }
}

/// <summary>Managed face of unpakz.dll. All archive logic, limits and the path guard live in the native library.</summary>
public static class NativeArchive
{
    public static uint AbiVersion()
    {
        var v = NativeMethods.unpakz_abi_version();
        if (v != NativeMethods.ExpectedAbiVersion)
            throw new UnpakzException(UnpakzStatus.Internal, $"unpakz.dll has ABI version {v}, this application expects {NativeMethods.ExpectedAbiVersion}.");
        return v;
    }

    public static IReadOnlyList<EntryInfo> List(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        AbiVersion();

        var limits = new NativeMethods.NativeLimits { StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeLimits>() };
        NativeMethods.unpakz_default_limits(ref limits);

        var entries = new List<EntryInfo>();
        NativeMethods.EntryCallback callback = (ref NativeMethods.NativeEntry e, IntPtr user) =>
        {
            entries.Add(Convert(ref e));
            return 1;
        };

        var error = NewError();
        var status = NativeMethods.unpakz_list(path, ref limits, callback, IntPtr.Zero, ref error);
        GC.KeepAlive(callback);
        Throw(status, error);
        return entries;
    }

    public static ExtractResult Extract(string path, string destination, ExtractOptions? options = null)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        options ??= new ExtractOptions();
        AbiVersion();

        var native = new NativeMethods.NativeExtractOptions { StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeExtractOptions>() };
        NativeMethods.unpakz_default_extract_options(ref native);
        native.Overwrite = (int)options.Overwrite;
        native.RestoreTimestamps = options.RestoreTimestamps ? 1 : 0;
        native.PreflightPass = options.PreflightPass ? 1 : 0;

        Exception? callbackFailure = null;
        var token = options.Cancellation;
        if (options.Filter is { } filter)
        {
            native.Filter = (ref NativeMethods.NativeEntry e, IntPtr user) =>
            {
                try { return filter(Convert(ref e)) ? 1 : 0; }
                catch (Exception ex) { callbackFailure = ex; return 0; }
            };
        }

        if (options.Progress is { } progress)
        {
            native.Progress = (ref NativeMethods.NativeProgress p, IntPtr user) =>
            {
                try { progress(p.BytesDone, p.BytesTotal, System.Runtime.InteropServices.Marshal.PtrToStringUni(p.CurrentName) ?? string.Empty); }
                catch (Exception ex) { callbackFailure = ex; }
            };
        }

        native.Cancelled = _ => (token.IsCancellationRequested || callbackFailure is not null) ? 1 : 0;

        var result = new NativeMethods.NativeResult { StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeResult>() };
        var error = NewError();
        var status = NativeMethods.unpakz_extract(path, destination, ref native, ref result, ref error);
        GC.KeepAlive(native.Filter);
        GC.KeepAlive(native.Progress);
        GC.KeepAlive(native.Cancelled);

        if (callbackFailure is not null) throw new InvalidOperationException("A callback failed while extracting.", callbackFailure);
        if (status == (int)UnpakzStatus.Cancelled && token.IsCancellationRequested) throw new OperationCanceledException(token);
        Throw(status, error);
        return new ExtractResult { Files = result.Files, Directories = result.Directories, Bytes = result.Bytes, Skipped = result.Skipped };
    }

    /// <summary>The native path guard, for front ends that show or forward names. Returns false (and the reason) for a refused name.</summary>
    public static bool TryCheckName(string name, out string relativePath, out string reason)
    {
        var sb = new StringBuilder(Math.Max(64, name.Length + 8));
        var error = NewError();
        var status = NativeMethods.unpakz_check_name(name, sb, (uint)sb.Capacity, ref error);
        if (status == 0)
        {
            relativePath = sb.ToString();
            reason = string.Empty;
            return true;
        }

        relativePath = string.Empty;
        reason = error.Message ?? string.Empty;
        return false;
    }

    private static NativeMethods.NativeError NewError() =>
        new() { StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeError>(), Message = string.Empty };

    private static void Throw(int status, NativeMethods.NativeError error)
    {
        if (status == 0) return;
        throw new UnpakzException((UnpakzStatus)status, error.Message ?? "unknown error", error.Volume);
    }

    private static EntryInfo Convert(ref NativeMethods.NativeEntry e) => new()
    {
        Index = (long)e.Index,
        Name = System.Runtime.InteropServices.Marshal.PtrToStringUni(e.Name) ?? string.Empty,
        RelativePath = System.Runtime.InteropServices.Marshal.PtrToStringUni(e.RelativePath) ?? string.Empty,
        Kind = (EntryKind)e.Kind,
        Size = e.Size,
        SizeKnown = e.SizeKnown != 0,
        LastWriteUtc = e.HasMtime != 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(e.MtimeUnixSeconds) : null,
    };
}
