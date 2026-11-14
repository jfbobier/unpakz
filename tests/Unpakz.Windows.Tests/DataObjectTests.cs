using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Unpakz.Core;
using Unpakz.Core.Zip;
using Unpakz.Engine;
using Unpakz.Presentation;
using Unpakz.Tests.Support;
using Unpakz.Windows;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Unpakz.Windows.Tests;

/// <summary>Talks to the drag source exactly like Explorer does: through IDataObject and IStream, never through our own types.</summary>
public sealed class DataObjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "work-" + Guid.NewGuid().ToString("N")[..8]);

    public DataObjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly Dictionary<string, byte[]> Tree = new()
    {
        ["a.txt"] = TestData.Text(5000, 1),
        ["docs\\big.bin"] = TestData.Random(3_000_000, 2),
        ["docs\\sub\\c.txt"] = TestData.Text(100, 3),
        ["empty.txt"] = [],
    };

    private (MainViewModel Vm, VirtualFileList Drag, VirtualFileDataObject Obj) Setup()
    {
        var b = new ZipBuilder();
        foreach (var (k, v) in Tree) b.Add(k.Replace('\\', '/'), v, deflate: v.Length < 1_000_000);
        b.AddDirectory("docs");
        var path = Path.Combine(_dir, "t.zip");
        File.WriteAllBytes(path, b.Build());
        var vm = new MainViewModel(new NoPicker(), new NoPrompt());
        Assert.True(vm.TryOpen(path), vm.Status);
        var drag = vm.PrepareDrag(null)!;
        return (vm, drag, new VirtualFileDataObject(drag.Items, vm.OpenDragStream));
    }

    private sealed class NoPicker : IFolderPicker { public string? Pick(string title, string? startFolder) => null; }
    private sealed class NoPrompt : IPrompt { public bool ConfirmReplace(string message) => false; }

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
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var f = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
            ((ComIDataObject)obj).GetData(ref f, out var m);
            Assert.Equal(FileGroupDescriptor.Serialize(drag.Items), ReadHGlobal(m));
        }
    }

    [Fact]
    public void Every_file_streams_exactly_its_bytes_and_reports_its_size()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            for (var i = 0; i < drag.Items.Count; i++)
            {
                var item = drag.Items[i];
                if (item.IsDirectory) continue;
                var s = GetContents(obj, i);
                s.Stat(out var stat, 0);
                Assert.Equal(item.Size, stat.cbSize);
                Assert.Equal(2, stat.type);                                          // STGTY_STREAM
                Assert.True(Tree[item.RelativePath].AsSpan().SequenceEqual(ReadAll(s, i % 2 == 0 ? 4096 : 65_537)), item.RelativePath);
            }
        }
    }

    [Fact]
    public void Contents_requests_for_folders_or_unknown_items_fail_with_the_right_hresult()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var folder = drag.Items.ToList().FindIndex(i => i.IsDirectory);
            foreach (var index in new[] { folder, -1, drag.Items.Count, 9999 })
            {
                var f = Format("FileContents", TYMED.TYMED_ISTREAM, index);
                var ex = Assert.Throws<COMException>(() => ((ComIDataObject)obj).GetData(ref f, out _));
                Assert.Equal(unchecked((int)0x80040068), ex.HResult);                // DV_E_LINDEX
            }
        }
    }

    [Fact]
    public void Query_and_enumeration_advertise_the_virtual_file_formats_only()
    {
        var (vm, _, obj) = Setup();
        using (vm) using (obj)
        {
            ComIDataObject com = obj;
            var ok1 = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
            var ok2 = Format("FileContents", TYMED.TYMED_ISTREAM, 0);
            var wrongTymed = Format("FileContents", TYMED.TYMED_HGLOBAL, 0);
            var unknown = new FORMATETC { cfFormat = 1, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };   // CF_TEXT
            Assert.Equal(0, com.QueryGetData(ref ok1));
            Assert.Equal(0, com.QueryGetData(ref ok2));
            Assert.Equal(unchecked((int)0x80040069), com.QueryGetData(ref wrongTymed));       // DV_E_TYMED
            Assert.Equal(unchecked((int)0x80040064), com.QueryGetData(ref unknown));          // DV_E_FORMATETC

            var ids = new List<short>();
            var en = com.EnumFormatEtc(DATADIR.DATADIR_GET);
            var one = new FORMATETC[1];
            while (en.Next(1, one, null!) == 0) ids.Add(one[0].cfFormat);
            Assert.Contains((short)VirtualFileDataObject.FormatId("FileGroupDescriptorW"), ids);
            Assert.Contains((short)VirtualFileDataObject.FormatId("FileContents"), ids);
            Assert.DoesNotContain((short)1, ids);                                             // no CF_TEXT / CF_HDROP: nothing on disk to point at
            Assert.DoesNotContain((short)15, ids);
        }
    }

    [Fact]
    public void Only_the_content_aspect_is_offered()
    {
        var (vm, _, obj) = Setup();
        using (vm) using (obj)
        {
            ComIDataObject com = obj;
            var thumb = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
            thumb.dwAspect = DVASPECT.DVASPECT_THUMBNAIL;
            Assert.Equal(unchecked((int)0x80040065), com.QueryGetData(ref thumb));            // DV_E_DVASPECT
            Assert.Equal(unchecked((int)0x80040065), Assert.Throws<COMException>(() => com.GetData(ref thumb, out _)).HResult);
        }
    }

    [Fact]
    public void Enumerator_supports_reset_skip_and_clone()
    {
        var (vm, _, obj) = Setup();
        using (vm) using (obj)
        {
            var en = ((ComIDataObject)obj).EnumFormatEtc(DATADIR.DATADIR_GET);
            var all = new List<short>();
            var one = new FORMATETC[1];
            while (en.Next(1, one, null!) == 0) all.Add(one[0].cfFormat);
            Assert.True(all.Count >= 2);

            en.Reset();
            Assert.Equal(0, en.Skip(1));
            en.Next(1, one, null!);
            Assert.Equal(all[1], one[0].cfFormat);

            en.Reset();
            en.Clone(out var copy);
            copy.Next(1, one, null!);
            Assert.Equal(all[0], one[0].cfFormat);
        }
    }

    [Fact]
    public void A_folder_item_never_yields_contents_even_if_the_opener_would_happily_produce_a_stream()
    {
        VirtualFile[] items = [new("docs", true, 0, null, 0), new("docs\\a.txt", false, 3, null, 1)];
        using var obj = new VirtualFileDataObject(items, _ => new MemoryStream([1, 2, 3]));
        var f = Format("FileContents", TYMED.TYMED_ISTREAM, 0);
        var ex = Assert.Throws<COMException>(() => ((ComIDataObject)obj).GetData(ref f, out _));
        Assert.Equal(unchecked((int)0x80040068), ex.HResult);                                 // DV_E_LINDEX
        Assert.NotNull(GetContents(obj, 1));                                                  // the file next to it is fine
    }

    [Fact]
    public void Preferred_drop_effect_is_copy_and_set_data_from_the_shell_is_accepted()
    {
        var (vm, _, obj) = Setup();
        using (vm) using (obj)
        {
            ComIDataObject com = obj;
            var f = Format("Preferred DropEffect", TYMED.TYMED_HGLOBAL);
            com.GetData(ref f, out var m);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(ReadHGlobal(m)));          // DROPEFFECT_COPY
            Assert.Equal(0, com.QueryGetData(ref f));                                          // and it is advertised as available

            var performed = Format("Performed DropEffect", TYMED.TYMED_HGLOBAL);
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = NativeMethods.AllocGlobal([1, 0, 0, 0]) };
            com.SetData(ref performed, ref medium, true);                                     // Explorer reports the result this way
        }
    }

    [Fact]
    public void Unsupported_operations_return_proper_com_errors_instead_of_crashing()
    {
        var (vm, _, obj) = Setup();
        using (vm) using (obj)
        {
            ComIDataObject com = obj;
            var f = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL);
            var medium = new STGMEDIUM();
            Assert.Throws<NotImplementedException>(() => com.GetDataHere(ref f, ref medium));
            Assert.Throws<COMException>(() => com.EnumFormatEtc(DATADIR.DATADIR_SET));
            Assert.Throws<COMException>(() => com.DAdvise(ref f, 0, null!, out _));
            Assert.Throws<COMException>(() => com.EnumDAdvise(out _));
            Assert.Throws<COMException>(() => com.DUnadvise(1));
        }
    }

    // ---- the IStream bridge ----
    [Fact]
    public void Streams_are_forward_only_but_allow_the_seeks_a_copy_engine_makes()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var index = drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt");
            var s = GetContents(obj, index);
            var posPtr = Marshal.AllocCoTaskMem(8);
            try
            {
                s.Seek(0, 1, posPtr);                                                          // STREAM_SEEK_CUR 0: "where am I"
                Assert.Equal(0, Marshal.ReadInt64(posPtr));
                s.Seek(0, 0, posPtr);                                                          // rewind to the start while at the start
                var buf = new byte[100];
                s.Read(buf, 100, IntPtr.Zero);
                s.Seek(50, 1, posPtr);                                                         // skip forward
                Assert.Equal(150, Marshal.ReadInt64(posPtr));
                s.Seek(0, 1, posPtr);
                Assert.Equal(150, Marshal.ReadInt64(posPtr));
                var back = Assert.Throws<COMException>(() => s.Seek(10, 0, posPtr));           // backwards cannot be honoured
                Assert.Equal(unchecked((int)0x80030001), back.HResult);                        // STG_E_INVALIDFUNCTION
                var rest = ReadAll(s, 1000);
                Assert.True(Tree["a.txt"].AsSpan(150).SequenceEqual(rest));

                var fresh = GetContents(obj, index);                                           // a second stream, to seek from the end
                fresh.Seek(-10, 2, posPtr);                                                    // STREAM_SEEK_END is relative to the length
                Assert.Equal(Tree["a.txt"].Length - 10, Marshal.ReadInt64(posPtr));
                Assert.True(Tree["a.txt"].AsSpan(Tree["a.txt"].Length - 10).SequenceEqual(ReadAll(fresh, 100)));
            }
            finally { Marshal.FreeCoTaskMem(posPtr); }
        }
    }

    [Fact]
    public void Writing_and_other_mutations_are_refused_and_commit_is_harmless()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var s = GetContents(obj, drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt"));
            Assert.Equal(unchecked((int)0x80030005), Assert.Throws<COMException>(() => s.Write(new byte[1], 1, IntPtr.Zero)).HResult);   // STG_E_ACCESSDENIED
            Assert.Throws<COMException>(() => s.SetSize(0));
            Assert.Throws<COMException>(() => s.Clone(out _));
            s.Commit(0);
            s.Revert();
        }
    }

    [Fact]
    public void CopyTo_streams_into_another_stream_which_is_how_some_shells_copy()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var s = GetContents(obj, drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt"));
            var sink = new MemoryIStream();
            var rd = Marshal.AllocCoTaskMem(8);
            var wr = Marshal.AllocCoTaskMem(8);
            try
            {
                s.CopyTo(sink, long.MaxValue, rd, wr);
                Assert.Equal(Tree["a.txt"].Length, Marshal.ReadInt64(rd));
                Assert.Equal(Tree["a.txt"].Length, Marshal.ReadInt64(wr));
                Assert.Equal(Tree["a.txt"], sink.ToArray());
            }
            finally { Marshal.FreeCoTaskMem(rd); Marshal.FreeCoTaskMem(wr); }
        }
    }

    [Fact]
    public void A_damaged_file_surfaces_as_a_read_fault_after_its_good_bytes()
    {
        var zip = new ZipBuilder().Add("bad.bin", TestData.Random(300_000, 9)).Build();
        zip[30 + 7 + 200_000] ^= 0x01;
        var path = Path.Combine(_dir, "bad.zip");
        File.WriteAllBytes(path, zip);
        using var vm = new MainViewModel(new NoPicker(), new NoPrompt());
        Assert.True(vm.TryOpen(path));
        var drag = vm.PrepareDrag(null)!;
        using var obj = new VirtualFileDataObject(drag.Items, vm.OpenDragStream);
        var s = GetContents(obj, 0);
        var ex = Assert.Throws<COMException>(() => ReadAll(s, 4096));
        Assert.Equal(unchecked((int)0x8003001E), ex.HResult);                                  // STG_E_READFAULT
    }

    private sealed class TrackedStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; inner.Dispose(); } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Dispose_really_disposes_the_underlying_decoders_streams_and_the_count_tracks_it()
    {
        var (vm, drag, _) = Setup();
        using (vm)
        {
            var tracked = new List<TrackedStream>();
            using var obj = new VirtualFileDataObject(drag.Items, item => { var t = new TrackedStream(vm.OpenDragStream(item)); tracked.Add(t); return t; });
            var a = drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt");
            var big = drag.Items.ToList().FindIndex(i => i.RelativePath == "docs\\big.bin");
            _ = GetContents(obj, a);
            _ = GetContents(obj, big);
            Assert.Equal(2, obj.OpenStreams);
            Assert.All(tracked, t => Assert.False(t.Disposed));

            obj.Dispose();
            Assert.Equal(0, obj.OpenStreams);
            Assert.All(tracked, t => Assert.True(t.Disposed, "a stream handed to the shell was not disposed"));
        }
    }

    [Fact]
    public void Reading_to_the_end_does_not_dispose_early_but_disposing_the_adapter_removes_it_from_the_count()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var a = drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt");
            var s = GetContents(obj, a);
            Assert.Equal(1, obj.OpenStreams);
            ReadAll(s, 1000);
            Assert.Equal(1, obj.OpenStreams);                                                   // finished reading, shell may still Stat
            s.Stat(out var stat, 0);
            Assert.Equal(Tree["a.txt"].Length, stat.cbSize);
        }
    }

    [Fact]
    public void Dispose_releases_every_stream_that_was_handed_out_even_if_the_shell_never_finished_reading()
    {
        var (vm, drag, obj) = Setup();
        using (vm)
        {
            var big = drag.Items.ToList().FindIndex(i => i.RelativePath == "docs\\big.bin");
            var s = GetContents(obj, big);
            var buf = new byte[10];
            s.Read(buf, 10, IntPtr.Zero);                                                       // started, then the user cancels the copy
            obj.Dispose();
            Assert.Equal(0, obj.OpenStreams);
            obj.Dispose();                                                                      // idempotent
            Assert.Throws<ObjectDisposedException>(() => { var f = Format("FileGroupDescriptorW", TYMED.TYMED_HGLOBAL); ((ComIDataObject)obj).GetData(ref f, out _); });
        }
    }

    [Fact]
    public async Task Two_files_can_be_read_at_once()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var i1 = drag.Items.ToList().FindIndex(i => i.RelativePath == "a.txt");
            var i2 = drag.Items.ToList().FindIndex(i => i.RelativePath == "docs\\big.bin");
            var s1 = GetContents(obj, i1);
            var s2 = GetContents(obj, i2);
            var t1 = Task.Run(() => ReadAll(s1, 777));
            var t2 = Task.Run(() => ReadAll(s2, 65_536));
            Assert.Equal(Tree["a.txt"], await t1);
            Assert.Equal(Tree["docs\\big.bin"], await t2);
        }
    }

    [Fact]
    public void Nothing_is_written_to_disk_while_files_are_streamed()
    {
        var (vm, drag, obj) = Setup();
        using (vm) using (obj)
        {
            var before = Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Length;
            for (var i = 0; i < drag.Items.Count; i++)
                if (!drag.Items[i].IsDirectory) ReadAll(GetContents(obj, i), 8192);
            Assert.Equal(before, Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Length);
        }
    }
}

/// <summary>A minimal writable IStream standing in for the destination in the CopyTo test.</summary>
internal sealed class MemoryIStream : IStream, IDisposable
{
    public void Dispose() => _ms.Dispose();
    private readonly MemoryStream _ms = new();
    public byte[] ToArray() => _ms.ToArray();
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) { _ms.Write(pv, 0, cb); if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb); }
    public void Read(byte[] pv, int cb, IntPtr pcbRead) => throw new NotImplementedException();
    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) => throw new NotImplementedException();
    public void SetSize(long libNewSize) => throw new NotImplementedException();
    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotImplementedException();
    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void Stat(out STATSTG pstatstg, int grfStatFlag) => throw new NotImplementedException();
    public void Clone(out IStream ppstm) => throw new NotImplementedException();
}

