using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Unpakz.Windows;

/// <summary>
/// Presents a forward-only decoded stream as a COM IStream, the way a shell expects the bytes of a virtual file. Reading works; the
/// only seeks honoured are forward ones (done by reading and discarding); everything that would write or duplicate is refused.
/// </summary>
internal sealed class StreamAdapter : IStream, IDisposable
{
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const int StgEReadFault = unchecked((int)0x8003001E);
    private const int EFail = unchecked((int)0x80004005);
    private const int ENotImpl = unchecked((int)0x80004001);

    private readonly Stream _inner;
    private readonly long _length;
    private readonly Action<StreamAdapter>? _onDispose;
    private long _position;
    private bool _disposed;

    internal StreamAdapter(Stream inner, long length, Action<StreamAdapter>? onDispose = null)
    {
        _inner = inner;
        _length = length;
        _onDispose = onDispose;
    }

    ~StreamAdapter() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        _onDispose?.Invoke(this);
        GC.SuppressFinalize(this);
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        if (pv is null) throw new ArgumentNullException(nameof(pv));
        var total = 0;
        try
        {
            if (!_disposed && cb > 0)
            {
                // a short read is legal for IStream, but returning what the decoder has ready keeps the shell's copy loop simple
                total = _inner.Read(pv, 0, Math.Min(cb, pv.Length));
                _position += total;
            }
        }
        catch (Exception ex)
        {
            throw new COMException($"The file could not be read: {ex.Message}", StgEReadFault);
        }

        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var target = dwOrigin switch
        {
            0 => dlibMove,
            1 => _position + dlibMove,
            2 => _length + dlibMove,
            _ => throw new COMException("Invalid seek origin.", StgEInvalidFunction),
        };

        if (target < _position) throw new COMException("This stream cannot move backwards.", StgEInvalidFunction);

        var skip = new byte[8192];
        while (_position < target)
        {
            var n = _inner.Read(skip, 0, (int)Math.Min(skip.Length, target - _position));
            if (n == 0) break;
            _position += n;
        }

        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG { type = 2, cbSize = _length, grfMode = 0 };    // STGTY_STREAM, read-only
    }

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        if (pstm is null) throw new ArgumentNullException(nameof(pstm));
        var buffer = new byte[65_536];
        long copied = 0;
        while (copied < cb)
        {
            int n;
            try { n = _inner.Read(buffer, 0, (int)Math.Min(buffer.Length, cb - copied)); }
            catch (Exception ex) { throw new COMException($"The file could not be read: {ex.Message}", StgEReadFault); }
            if (n == 0) break;
            pstm.Write(buffer, n, IntPtr.Zero);
            copied += n;
            _position += n;
        }

        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, copied);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, copied);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("The stream is read-only.", StgEAccessDenied);

    public void SetSize(long libNewSize) => throw new COMException("The stream is read-only.", StgEAccessDenied);

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Locking is not supported.", StgEInvalidFunction);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Locking is not supported.", StgEInvalidFunction);

    public void Clone(out IStream ppstm) => throw new COMException("Cloning is not supported.", ENotImpl);

    internal static int Failure => EFail;
}
