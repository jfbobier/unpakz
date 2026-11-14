using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Unpakz.Interop;

/// <summary>
/// One archive entry as a pull-based, forward-only <see cref="Stream"/>: a background thread asks the native library to decode the entry into a
/// small bounded queue and <see cref="Read(byte[], int, int)"/> drains it. This is what lets a shell (drag and drop) take file bytes straight from the
/// decoder without an intermediate copy on disk. Memory is bounded by queueCapacity x 1 MiB whatever the entry size. An error in the data (bad checksum,
/// corrupt stream) is delivered after the bytes that precede it and stays raised.
/// </summary>
public sealed class EntryStream : Stream
{
    private readonly BlockingCollection<byte[]> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _producer;
    private byte[]? _current;
    private int _offset;
    private Exception? _failure;
    private bool _disposed;

    private EntryStream(string archivePath, long entryIndex, long expectedLength, int queueCapacity)
    {
        ExpectedLength = expectedLength;
        _queue = new BlockingCollection<byte[]>(queueCapacity);
        var token = _cts.Token;
        _producer = Task.Factory.StartNew(() => Produce(archivePath, entryIndex, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public long ExpectedLength { get; }

    public static EntryStream Open(ArchiveSession session, EntryInfo entry, int queueCapacity = 8)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        return new EntryStream(session.Path, entry.Index, (long)Math.Min(entry.Size, long.MaxValue), queueCapacity);
    }

    private void Produce(string path, long index, CancellationToken token)
    {
        try
        {
            NativeArchive.ReadEntry(path, index, (buffer, count) =>
            {
                if (token.IsCancellationRequested) return false;
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, 0, copy, 0, count);
                try { _queue.Add(copy, token); }
                catch (OperationCanceledException) { return false; }
                return true;
            });
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Volatile.Write(ref _failure, ex);
        }
        catch (Exception)
        {
            // cancelled by Dispose: nothing to report
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EntryStream));
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (count == 0) return 0;

        while (true)
        {
            if (_current is { } c && _offset < c.Length)
            {
                var n = Math.Min(count, c.Length - _offset);
                Buffer.BlockCopy(c, _offset, buffer, offset, n);
                _offset += n;
                if (_offset == c.Length) _current = null;
                return n;
            }

            if (_queue.TryTake(out var next, Timeout.Infinite))
            {
                _current = next;
                _offset = 0;
                continue;
            }

            _producer.Wait();          // queue completed and drained: end of data, or the producer failed
            if (Volatile.Read(ref _failure) is { } failure)
                throw failure is UnpakzException u ? new UnpakzException(u.Status, u.Message, u.Volume) : failure;
            return 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _cts.Cancel();
            while (_queue.TryTake(out _)) { }
            try { _producer.Wait(); } catch (AggregateException) { }
            _queue.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }

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
