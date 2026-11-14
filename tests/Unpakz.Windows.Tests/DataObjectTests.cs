using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Unpakz.Interop;
using Unpakz.Windows;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Unpakz.Windows.Tests;

/// <summary>Talks to the drag source exactly like Explorer does: through IDataObject and IStream, never through our own types.</summary>
public sealed class DataObjectTests
{
    private static readonly byte[] Small = Unpakz.Tests.Support.TestData.Text(5000, 1);
    private static readonly byte[] Big = Unpakz.Tests.Support.TestData.Random(3_000_000, 2);

    private static readonly VirtualFile[] Items =
    {
        new("a.txt", false, Small.Length, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local), 0),
        new("docs", true, 0, null, 1),
        new("docs\\big.bin", false, Big.Length, null, 2),
        new("empty.txt", false, 0, null, 3),
    };

    private static Stream Open(VirtualFile f) => new MemoryStream(f.EntryIndex == 0 ? Small : f.EntryIndex == 2 ? Big : Array.Empty<byte>());

    private static VirtualFileDataObject Make(Func<VirtualFile, Stream>? open = null) => new(Items, open ?? Open);

    private static FORMATETC Format(string name, TYMED tymed, int index = -1) => new()
    {
        cfFormat = (short)VirtualFileDataObject.FormatId(name),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        tymed = tymed,
    };

    private static byte[] ReadHGlobal(STGMEDIUM m)
    {
        Assert.Equal(TYMED.TYMED_HGLOBAL, m.tymed);
        var size = (int)NativeMethods.GlobalSize(m.unionmember);
        var p = NativeMethods.GlobalLock(m.unionmember);
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(m.unionmember);
            NativeMethods.ReleaseStgMedium(ref m);
        }
    }

    private static byte[] ReadAll(IStream s, int chunk)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[chunk];
        var countPtr = Marshal.AllocCoTaskMem(4);
        try
        {
            while (true)
            {
                s.Read(buffer, buffer.Length, countPtr);
                var n = Marshal.ReadInt32(countPtr);
                if (n == 0) break;
                ms.Write(buffer, 0, n);
            }
        }
        finally { Marshal.FreeCoTaskMem(countPtr); }
        return ms.ToArray();
    }

    private static IStream GetContents(ComIDataObject obj, int index)
    {
        var f = Format("FileContents", TYMED.TYMED_ISTREAM, index);
        obj.GetData(ref f, out var medium);
        Assert.Equal(TYMED.TYMED_ISTREAM, medium.tymed);
        return (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
    }

    [Fact]
    public void The_file_group_descriptor_matches_the_drag_list()
    {
        using var obj = Make();
        var f = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
        ((ComIDataObject)obj).GetData(ref f, out var m);
        Assert.Equal(FileGroupDescriptor.Serialize(Items), ReadHGlobal(m));
    }

    [Fact]
    public void Every_file_streams_exactly_its_bytes_and_reports_its_size()
    {
        using var obj = Make();
        foreach (var chunk in new[] { 1, 7, 4096, 1 << 20 })
        {
            Assert.Equal(Small, ReadAll(GetContents(obj, 0), Math.Min(chunk, 4096)));
            Assert.Equal(Big, ReadAll(GetContents(obj, 2), chunk));
            Assert.Empty(ReadAll(GetContents(obj, 3), chunk));
        }

        var s = GetContents(obj, 0);
        s.Stat(out var stat, 0);
        Assert.Equal(Small.Length, stat.cbSize);
    }

    [Fact]
    public void Contents_requests_for_folders_or_unknown_items_fail_with_the_right_hresult()
    {
        using var obj = Make();
        foreach (var index in new[] { 1, -1, Items.Length, 9999 })
        {
            var f = Format("FileContents", TYMED.TYMED_ISTREAM, index);
            var ex = Assert.Throws<COMException>(() => ((ComIDataObject)obj).GetData(ref f, out _));
            Assert.Equal(unchecked((int)0x80040068), ex.HResult);                      // DV_E_LINDEX
        }

        var lonely = new VirtualFileDataObject(new[] { new VirtualFile("docs", true, 0, null, 0), new VirtualFile("docs\\a.txt", false, 3, null, 1) }, _ => new MemoryStream(new byte[] { 1, 2, 3 }));
        var g = Format("FileContents", TYMED.TYMED_ISTREAM, 0);
        Assert.Equal(unchecked((int)0x80040068), Assert.Throws<COMException>(() => ((ComIDataObject)lonely).GetData(ref g, out _)).HResult);   // a folder never yields contents, even if an opener would
        lonely.Dispose();
    }

    [Fact]
    public void Query_and_enumeration_advertise_the_virtual_file_formats_only()
    {
        using var obj = Make();
        ComIDataObject com = obj;
        var good = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
        Assert.Equal(0, com.QueryGetData(ref good));
        var drop = Format("Preferred DropEffect", TYMED.TYMED_HGLOBAL);
        Assert.Equal(0, com.QueryGetData(ref drop));
        var wrongTymed = Format("FileGroupDescriptorW", TYMED.TYMED_ISTREAM);
        Assert.Equal(unchecked((int)0x80040069), com.QueryGetData(ref wrongTymed));
        var other = new FORMATETC { cfFormat = 13, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        Assert.Equal(unchecked((int)0x80040064), com.QueryGetData(ref other));
        var thumb = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL); thumb.dwAspect = DVASPECT.DVASPECT_THUMBNAIL;
        Assert.Equal(unchecked((int)0x80040065), com.QueryGetData(ref thumb));

        var e = com.EnumFormatEtc(DATADIR.DATADIR_GET);
        var all = new FORMATETC[8];
        e.Next(8, all, null!);
        Assert.Equal(3, all.Count(f => f.cfFormat != 0));
        Assert.Throws<COMException>(() => com.EnumFormatEtc(DATADIR.DATADIR_SET));
    }

    [Fact]
    public void Preferred_drop_effect_is_copy_and_set_data_from_the_shell_is_accepted()
    {
        using var obj = Make();
        ComIDataObject com = obj;
        var f = Format("Preferred DropEffect", TYMED.TYMED_HGLOBAL);
        com.GetData(ref f, out var m);
        Assert.Equal(1, BitConverter.ToInt32(ReadHGlobal(m), 0));
        var performed = Format("Performed DropEffect", TYMED.TYMED_HGLOBAL);
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = NativeMethods.AllocGlobal(new byte[] { 1, 0, 0, 0 }) };
        com.SetData(ref performed, ref medium, true);
    }

    [Fact]
    public void Streams_are_forward_only_but_allow_the_seeks_a_copy_engine_makes()
    {
        using var obj = Make();
        var s = GetContents(obj, 0);
        var pos = Marshal.AllocCoTaskMem(8);
        try
        {
            s.Seek(0, 1, pos);
            Assert.Equal(0, Marshal.ReadInt64(pos));
            s.Read(new byte[100], 100, IntPtr.Zero);
            s.Seek(50, 1, pos);
            Assert.Equal(150, Marshal.ReadInt64(pos));
            Assert.Equal(unchecked((int)0x80030001), Assert.Throws<COMException>(() => s.Seek(10, 0, pos)).HResult);   // STG_E_INVALIDFUNCTION
            Assert.Equal(Small.Skip(150).ToArray(), ReadAll(s, 1000));
            var fresh = GetContents(obj, 0);
            fresh.Seek(-10, 2, pos);                                                                                      // from the end
            Assert.Equal(Small.Length - 10, Marshal.ReadInt64(pos));
            Assert.Equal(Small.Skip(Small.Length - 10).ToArray(), ReadAll(fresh, 100));
        }
        finally { Marshal.FreeCoTaskMem(pos); }
    }

    [Fact]
    public void Writing_and_other_mutations_are_refused_and_commit_is_harmless()
    {
        using var obj = Make();
        var s = GetContents(obj, 0);
        Assert.Equal(unchecked((int)0x80030005), Assert.Throws<COMException>(() => s.Write(new byte[1], 1, IntPtr.Zero)).HResult);   // STG_E_ACCESSDENIED
        Assert.Throws<COMException>(() => s.SetSize(10));
        s.Commit(0);
        s.Revert();
    }

    [Fact]
    public void A_damaged_file_surfaces_as_a_read_fault_after_its_good_bytes()
    {
        using var obj = Make(f => new FaultyStream(Small, 1000));
        var s = GetContents(obj, 0);
        var buffer = new byte[4096];
        var got = 0;
        var ex = Assert.Throws<COMException>(() => { while (true) { s.Read(buffer, buffer.Length, IntPtr.Zero); got++; if (got > 100) break; } });
        Assert.Equal(unchecked((int)0x8003001E), ex.HResult);                         // STG_E_READFAULT
    }

    private sealed class FaultyStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _failAt;
        private int _pos;
        public FaultyStream(byte[] data, int failAt) { _data = data; _failAt = failAt; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _failAt) throw new UnpakzException(UnpakzStatus.Corrupt, "bad checksum");
            var n = Math.Min(Math.Min(count, _failAt - _pos), _data.Length - _pos);
            Buffer.BlockCopy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Tracked : MemoryStream
    {
        public static int Disposed;
        public Tracked(byte[] b) : base(b) { }
        protected override void Dispose(bool disposing) { if (disposing) Disposed++; base.Dispose(disposing); }
    }

    [Fact]
    public void Disposing_the_data_object_releases_every_stream_even_if_the_shell_never_finished_reading()
    {
        Tracked.Disposed = 0;
        var obj = Make(_ => new Tracked(Small));
        GetContents(obj, 0);
        GetContents(obj, 0);
        Assert.Equal(2, obj.OpenStreams);
        obj.Dispose();
        Assert.Equal(2, Tracked.Disposed);
        var f = Format("FileContents", TYMED.TYMED_ISTREAM, 0);
        Assert.Throws<ObjectDisposedException>(() => ((ComIDataObject)obj).GetData(ref f, out _));
    }
}
