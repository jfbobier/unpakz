using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Unpakz.Core.Checksums;

namespace Unpakz.Core.Zip;

/// <summary>
/// Decodes one entry into an <see cref="IOutputSink"/>. Pure: no files, no OS. The output length must equal the declared
/// size exactly and the CRC-32 must match; on any failure the caller must discard what was already written.
/// </summary>
public static class ZipEntryExtractor
{
    private const uint LocalSig = 0x04034b50;
    private const int LocalFixed = 30;
    private const int StoredChunk = 1 << 20;
    private const int InflateBuffer = 128 * 1024;
    private const ulong MaxDeflateRatio = 1032;   // theoretical maximum for DEFLATE (length-258 match in 2 bits)

    public static void Extract(ReadOnlyMemory<byte> zip, ZipEntryInfo entry, IOutputSink sink) =>
        Extract(zip, entry, sink, new ArchiveLimits());

    public static void Extract(ReadOnlyMemory<byte> zip, ZipEntryInfo entry, IOutputSink sink, ArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);

        if (entry.IsEncrypted) throw new ArchiveException("Encrypted entries are not supported yet.", ArchiveError.Unsupported);
        if (entry.Method is not (0 or 8))
            throw new ArchiveException($"Compression method {entry.Method} is not supported.", ArchiveError.Unsupported);
        if (entry.UncompressedSize > (ulong)limits.MaxEntrySize)
            throw new ArchiveException("Entry exceeds the configured size limit.", ArchiveError.LimitExceeded);

        if (entry.IsDirectory)
        {
            if (entry.UncompressedSize != 0) throw new ArchiveException("Directory entry has data.");
            return;
        }

        var data = LocateData(zip, entry);

        uint crc;
        if (entry.Method == 0)
        {
            if (entry.CompressedSize != entry.UncompressedSize) throw new ArchiveException("Stored entry has mismatched sizes.");
            crc = CopyStored(data, sink);
        }
        else
        {
            if (entry.UncompressedSize > entry.CompressedSize * MaxDeflateRatio + 64)
                throw new ArchiveException("Declared size exceeds the maximum possible DEFLATE expansion.");
            crc = Inflate(data, entry.UncompressedSize, sink);
        }

        if (crc != entry.Crc32) throw new ArchiveException("CRC-32 mismatch.");
    }

    private static ReadOnlyMemory<byte> LocateData(ReadOnlyMemory<byte> zip, ZipEntryInfo entry)
    {
        var (start, length) = LocateRange(zip, entry);
        return zip.Slice((int)start, (int)length);
    }

    /// <summary>Validated byte range of the entry's data inside the archive.</summary>
    internal static (ulong Start, ulong Length) LocateRange(ReadOnlyMemory<byte> zip, ZipEntryInfo entry)
    {
        var len = (ulong)zip.Length;
        var lho = entry.LocalHeaderOffset;
        if (lho > len || len - lho < LocalFixed) throw new ArchiveException("Local header outside archive.");

        var h = zip.Span[(int)lho..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != LocalSig) throw new ArchiveException("Bad local header signature.");

        var start = lho + LocalFixed + BinaryPrimitives.ReadUInt16LittleEndian(h[26..]) + (ulong)BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        if (start > len || entry.CompressedSize > len - start) throw new ArchiveException("Entry data extends past the archive.");

        return (start, entry.CompressedSize);
    }

    private static uint CopyStored(ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        uint crc = 0;
        for (var pos = 0; pos < data.Length; pos += StoredChunk)
        {
            var chunk = data.Span.Slice(pos, Math.Min(StoredChunk, data.Length - pos));
            crc = Crc32.Update(crc, chunk);
            sink.Write(chunk);   // straight from the archive bytes: no intermediate copy
        }

        return crc;
    }

    private static uint Inflate(ReadOnlyMemory<byte> data, ulong expected, IOutputSink sink)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InflateBuffer);
        try
        {
            using var input = new MemoryReadStream(data);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            uint crc = 0;
            ulong total = 0;
            int n;
            while ((n = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                if ((ulong)n > expected - total) throw new ArchiveException("DEFLATE output exceeds the declared size.");
                crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                sink.Write(buffer.AsSpan(0, n));
                total += (ulong)n;
            }

            if (total != expected) throw new ArchiveException("DEFLATE output is shorter than the declared size.");
            return crc;
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveException("Corrupt DEFLATE stream.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
