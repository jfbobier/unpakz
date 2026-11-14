using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>
/// A read-only memory-mapped view of a whole file as <see cref="ReadOnlyMemory{T}"/>: the operating system pages it in on demand,
/// nothing is copied into the managed heap. Limited to files under 2 GiB (the size of a .NET memory); larger archives need
/// to be split into volumes until windowed access exists.
/// </summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly Manager? _manager;
    private static int _live;
    private bool _disposed;

    /// <summary>Mappings currently open in this process; lets tests prove that every failure path releases its files.</summary>
    internal static int LiveMappings => Volatile.Read(ref _live);

    public ReadOnlyMemory<byte> Memory { get; }

    private MappedFile(MemoryMappedFile? file, MemoryMappedViewAccessor? view, Manager? manager)
    {
        _file = file;
        _view = view;
        _manager = manager;
        Memory = manager is null ? ReadOnlyMemory<byte>.Empty : manager.Memory;
        if (view is not null) Interlocked.Increment(ref _live);
    }

    public static MappedFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var length = new FileInfo(path).Length;                 // throws FileNotFoundException for a missing file
        if (length > int.MaxValue)
            throw new ArchiveException("The archive is larger than 2 GiB; a single file of that size is not supported yet (split it into volumes).", ArchiveError.Unsupported);
        if (length == 0) return new MappedFile(null, null, null);

        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return new MappedFile(file, view, new Manager(view, pointer + view.PointerOffset, (int)length));
        }
        catch
        {
            view?.Dispose();
            file?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_view is not null) Interlocked.Decrement(ref _live);
        _manager?.Release();                 // from here on a stale ReadOnlyMemory throws instead of touching unmapped memory
        _view?.SafeMemoryMappedViewHandle.ReleasePointer();
        _view?.Dispose();
        _file?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Manager(MemoryMappedViewAccessor owner, byte* pointer, int length) : MemoryManager<byte>
    {
        private volatile bool _released;

        public void Release() => _released = true;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_released, nameof(MappedFile));
            return new Span<byte>(pointer, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_released, nameof(MappedFile));
            return new MemoryHandle(pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing) => _ = owner;
    }
}
