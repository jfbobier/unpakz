using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unpakz.Core;

namespace Unpakz.Engine;

/// <summary>
/// One archive entry as a pull-based, forward-only <see cref="Stream"/>: a background thread decodes into a small bounded queue and
/// <see cref="Read(byte[], int, int)"/> drains it. This is what lets a shell (drag and drop) take file bytes straight from the decoder
/// without an intermediate copy on disk. Memory use is bounded by queueCapacity x chunk size, whatever the entry size.
/// An error in the data (bad CRC, corrupt stream) is delivered after the bytes that precede it and stays raised.
/// The archive must stay open while the stream is in use.
/// </summary>
public sealed class EntryStream : Stream
{
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _producer;
    private readonly long _expectedLength;
    private (byte[] Buffer, int Length)? _current;
    private int _offset;
    private Exception? _failure;
    private readonly Counters _counters = new();
    private bool _disposed;

    private EntryStream(IArchive archive, ArchiveEntry entry, ArchiveLimits limits, int queueCapacity, CancellationToken external)
    {
        _expectedLength = (long)Math.Min(entry.UncompressedSize, long.MaxValue);
        _queue = new BlockingCollection<(byte[], int)>(queueCapacity);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var token = _cts.Token;
        _producer = Task.Factory.StartNew(() => Produce(archive, entry, limits, token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static EntryStream Open(IArchive archive, ArchiveEntry entry, ArchiveLimits? limits = null, int queueCapacity = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        return new EntryStream(archive, entry, limits ?? new ArchiveLimits(), queueCapacity, cancellationToken);
    }

    public long ExpectedLength => _expectedLength;

    internal long PeakBufferedBytes => Interlocked.Read(ref _counters.Peak);

    internal bool ProducerRunning => !_producer.IsCompleted;

    private void Produce(IArchive archive, ArchiveEntry entry, ArchiveLimits limits, CancellationToken token)
    {
        try
        {
            if (entry.Kind == EntryKind.Directory) return;
            archive.Extract(entry, new QueueSink(this, token), limits);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _failure, new OperationCanceledException(token));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ex);
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    private sealed class Counters
    {
        public long Buffered;
        public long Peak;
    }

    private sealed class QueueSink(EntryStream owner, CancellationToken token) : IOutputSink
    {
        public void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                var n = Math.Min(data.Length, 1 << 20);
                var buffer = ArrayPool<byte>.Shared.Rent(n);
                data[..n].CopyTo(buffer);
                var now = Interlocked.Add(ref owner._counters.Buffered, n);
                long peak;
                while (now > (peak = Interlocked.Read(ref owner._counters.Peak)) && Interlocked.CompareExchange(ref owner._counters.Peak, now, peak) != peak) { }

                try { owner._queue.Add((buffer, n), token); }
                catch
                {
                    Interlocked.Add(ref owner._counters.Buffered, -n);
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                data = data[n..];
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;

        while (true)
        {
            if (_current is { } c && _offset < c.Length)
            {
                var n = Math.Min(buffer.Length, c.Length - _offset);
                c.Buffer.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                if (_offset == c.Length) Release();
                return n;
            }

            if (_queue.TryTake(out var next, Timeout.Infinite))
            {
                _current = next;
                _offset = 0;
                continue;
            }

            // queue completed and drained: end of data, or the producer failed
            _producer.Wait();
            if (Volatile.Read(ref _failure) is { } failure)
            {
                throw Rethrow(failure);
            }

            return 0;
        }
    }

    private static Exception Rethrow(Exception failure) => failure is ArchiveException ae
        ? new ArchiveException(ae.Message, ae, ae.Error) { Volume = ae.Volume }
        : failure is OperationCanceledException ? new OperationCanceledException(failure.Message, failure) : failure;

    private void Release()
    {
        if (_current is not { } c) return;
        Interlocked.Add(ref _counters.Buffered, -c.Length);
        ArrayPool<byte>.Shared.Return(c.Buffer);
        _current = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _cts.Cancel();
            while (_queue.TryTake(out var item)) ArrayPool<byte>.Shared.Return(item.Buffer);
            try { _producer.Wait(); } catch (AggregateException) { }
            while (_queue.TryTake(out var item)) ArrayPool<byte>.Shared.Return(item.Buffer);
            Release();
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
