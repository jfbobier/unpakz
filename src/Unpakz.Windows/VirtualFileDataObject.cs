using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Unpakz.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Unpakz.Windows;

/// <summary>
/// The drag-and-drop payload for dragging archive contents out of the app. It carries a list of virtual files (FileGroupDescriptorW) and
/// hands Explorer each file as an IStream when the user drops: bytes go from the decoder to the destination folder with no temporary
/// copy on disk. Names offered here have already passed PathGuard (see <see cref="VirtualFileList"/>).
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComIDataObject, IDisposable
{
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int DvELindex = unchecked((int)0x80040068);
    private const int DvETymed = unchecked((int)0x80040069);
    private const int DvEDvAspect = unchecked((int)0x80040065);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int DataSSameFormatEtc = 0x00040130;
    private const int DropEffectCopy = 1;

    private static readonly short CfDescriptor = (short)FormatId("FileGroupDescriptorW");
    private static readonly short CfContents = (short)FormatId("FileContents");
    private static readonly short CfDropEffect = (short)FormatId("Preferred DropEffect");

    private readonly IReadOnlyList<VirtualFile> _items;
    private readonly Func<VirtualFile, Stream> _open;
    private readonly byte[] _descriptor;
    private readonly List<StreamAdapter> _streams = [];
    private readonly object _gate = new();
    private bool _disposed;

    public VirtualFileDataObject(IReadOnlyList<VirtualFile> items, Func<VirtualFile, Stream> openContent)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _open = openContent ?? throw new ArgumentNullException(nameof(openContent));
        _descriptor = FileGroupDescriptor.Serialize(items);
    }

    /// <summary>Streams handed to the shell that have not been disposed yet.</summary>
    public int OpenStreams
    {
        get { lock (_gate) return _streams.Count; }
    }

    public static uint FormatId(string name) => NativeMethods.RegisterClipboardFormat(name);

    // ---- IDataObject ----
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
        medium = default;
        CheckAspect(format);

        if (format.cfFormat == CfDescriptor)
        {
            RequireTymed(format, TYMED.TYMED_HGLOBAL);
            medium = HGlobal(_descriptor);
        }
        else if (format.cfFormat == CfDropEffect)
        {
            RequireTymed(format, TYMED.TYMED_HGLOBAL);
            medium = HGlobal(BitConverter.GetBytes(DropEffectCopy));
        }
        else if (format.cfFormat == CfContents)
        {
            RequireTymed(format, TYMED.TYMED_ISTREAM);
            medium = Contents(format.lindex);
        }
        else
        {
            throw new COMException("Format not supported.", DvEFormatEtc);
        }
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (_disposed) return DvEFormatEtc;
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return DvEDvAspect;

        TYMED wanted;
        if (format.cfFormat == CfDescriptor || format.cfFormat == CfDropEffect) wanted = TYMED.TYMED_HGLOBAL;
        else if (format.cfFormat == CfContents) wanted = TYMED.TYMED_ISTREAM;
        else return DvEFormatEtc;

        return (format.tymed & wanted) != 0 ? 0 : DvETymed;
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Only reading formats is supported.", ENotImpl);
        return new FormatEnumerator(
        [
            Offer(CfDescriptor, TYMED.TYMED_HGLOBAL),
            Offer(CfContents, TYMED.TYMED_ISTREAM),
            Offer(CfDropEffect, TYMED.TYMED_HGLOBAL),
        ]);
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // Explorer reports "Performed DropEffect", "Paste Succeeded" etc. back this way. Nothing to do with them: accept and free.
        if (release) NativeMethods.ReleaseStgMedium(ref medium);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DataSSameFormatEtc;
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        => throw new COMException("Advise is not supported.", OleEAdviseNotSupported);

    public void DUnadvise(int connection) => throw new COMException("Advise is not supported.", OleEAdviseNotSupported);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) => throw new COMException("Advise is not supported.", OleEAdviseNotSupported);

    // ---- internals ----
    private static FORMATETC Offer(short format, TYMED tymed) => new()
    {
        cfFormat = format,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        ptd = IntPtr.Zero,
        tymed = tymed,
    };

    private static void CheckAspect(FORMATETC f)
    {
        if (f.dwAspect != DVASPECT.DVASPECT_CONTENT) throw new COMException("Aspect not supported.", DvEDvAspect);
    }

    private static void RequireTymed(FORMATETC f, TYMED wanted)
    {
        if ((f.tymed & wanted) == 0) throw new COMException("Medium not supported.", DvETymed);
    }

    private static STGMEDIUM HGlobal(byte[] bytes) => new()
    {
        tymed = TYMED.TYMED_HGLOBAL,
        unionmember = NativeMethods.AllocGlobal(bytes),
        pUnkForRelease = null,
    };

    private STGMEDIUM Contents(int index)
    {
        if ((uint)index >= (uint)_items.Count || _items[index].IsDirectory)
            throw new COMException("No file content at that index.", DvELindex);

        var item = _items[index];
        var adapter = new StreamAdapter(_open(item), item.Size, Forget);
        lock (_gate) _streams.Add(adapter);

        return new STGMEDIUM
        {
            tymed = TYMED.TYMED_ISTREAM,
            unionmember = Marshal.GetComInterfaceForObject(adapter, typeof(IStream)),
            pUnkForRelease = null,
        };
    }

    private void Forget(StreamAdapter adapter)
    {
        lock (_gate) _streams.Remove(adapter);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StreamAdapter[] open;
        lock (_gate) open = [.. _streams];
        foreach (var s in open) s.Dispose();         // unblocks any decoder whose consumer walked away
    }

    private sealed class FormatEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _next;

        public FormatEnumerator(FORMATETC[] formats, int next = 0)
        {
            _formats = formats;
            _next = next;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _next < _formats.Length) rgelt[fetched++] = _formats[_next++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _next = Math.Min(_formats.Length, _next + celt);
            return _next < _formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _next = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(_formats, _next);
    }
}
