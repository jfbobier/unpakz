using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Unpakz.Core.Checksums;

namespace Unpakz.Core.Rar;

/// <summary>
/// RAR 5.x container reader (layout from RARLAB's public technical note). Every length is range-checked, every header CRC verified.
/// Implemented: all structure, stored entries, link/special detection, timestamps.
/// Reported as Unsupported (never guessed): compressed methods, encryption, BLAKE2-only integrity, multi-volume, encrypted headers.
/// </summary>
public sealed class Rar5Archive : IArchive
{
    private static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly byte[] Signature4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaxHeaderSize = 2 * 1024 * 1024;
    private const ulong MaxFileTime = 2_650_467_743_999_999_999UL;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<RarFile> _files;

    public string Format => "rar5";

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    private sealed record RarFile(
        string Name, EntryKind Kind, ulong PackedSize, ulong UnpackedSize, uint? Crc32, bool UnknownSize, bool Encrypted,
        int Method, long BlockStart, long DataStart, DateTime? LastWrite);

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static bool HasRar4Signature(ReadOnlySpan<byte> data) => data.StartsWith(Signature4);

    private Rar5Archive(ReadOnlyMemory<byte> data, List<RarFile> files)
    {
        _data = data;
        _files = files;
        var list = new List<ArchiveEntry>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            list.Add(new ArchiveEntry(i, f.Name, f.Kind, f.PackedSize, f.UnpackedSize, f.LastWrite));
        }

        Entries = list;
    }

    public static Rar5Archive Open(ReadOnlyMemory<byte> data, ArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var span = data.Span;
        if (!HasSignature(span)) throw new ArchiveException("Not a RAR 5 archive.");

        var files = new List<RarFile>();
        long pos = Signature.Length, len = span.Length;
        var first = true;
        var sawEnd = false;

        while (pos < len && !sawEnd)
        {
            if (len - pos < 5) throw new ArchiveException("Truncated RAR block header.");
            var crcStored = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)pos..]);
            if (!VInt.TryRead(span[(int)(pos + 4)..], out var headerSize, out var sizeLen))
                throw new ArchiveException("Invalid RAR header size.");
            if (headerSize == 0 || headerSize > MaxHeaderSize) throw new ArchiveException("RAR header size out of range.");

            var headerStart = pos + 4 + sizeLen;
            var headerEnd = headerStart + (long)headerSize;
            if (headerEnd > len) throw new ArchiveException("RAR header extends past the archive.");
            if (Crc32.Compute(span[(int)(pos + 4)..(int)headerEnd]) != crcStored)
                throw new ArchiveException("RAR header CRC mismatch.");

            var header = span[(int)headerStart..(int)headerEnd];
            var c = new Cursor(header);
            var type = c.ReadVInt();
            var flags = c.ReadVInt();
            var extraSize = (flags & 1) != 0 ? c.ReadVInt() : 0;
            var dataSize = (flags & 2) != 0 ? c.ReadVInt() : 0;
            if (extraSize > (ulong)c.Remaining) throw new ArchiveException("RAR extra area larger than its header.");
            if (dataSize > (ulong)(len - headerEnd)) throw new ArchiveException("RAR data area extends past the archive.");
            if ((flags & 0x18) != 0) throw new ArchiveException("Multi-volume RAR archives are not supported.", ArchiveError.Unsupported);

            if (first)
            {
                if (type == 4) throw new ArchiveException("Encrypted RAR headers are not supported.", ArchiveError.Unsupported);
                if (type != 1) throw new ArchiveException("RAR main header missing.");
            }

            var dataStart = headerEnd;
            switch (type)
            {
                case 1:
                    if (!first) throw new ArchiveException("Duplicate RAR main header.");
                    ReadMainHeader(ref c);
                    break;
                case 2:
                    if (files.Count >= limits.MaxEntries) throw new ArchiveException("Too many entries.", ArchiveError.LimitExceeded);
                    files.Add(ReadFileHeader(header, c, (int)extraSize, dataSize, pos, dataStart));
                    break;
                case 3:   // service header (comment, recovery record, quick open, NTFS streams): not extracted
                    break;
                case 4:
                    throw new ArchiveException("Encrypted RAR headers are not supported.", ArchiveError.Unsupported);
                case 5:
                    sawEnd = true;
                    break;
                default:
                    if ((flags & 4) == 0)
                        throw new ArchiveException($"Unknown mandatory RAR block type {type}.", ArchiveError.Unsupported);
                    break;
            }

            first = false;
            pos = dataStart + (long)dataSize;
        }

        if (!sawEnd) throw new ArchiveException("RAR end-of-archive block missing.");
        return new Rar5Archive(data, files);
    }

    private static void ReadMainHeader(ref Cursor c)
    {
        var archiveFlags = c.ReadVInt();
        if ((archiveFlags & 0x01) != 0) throw new ArchiveException("Multi-volume RAR archives are not supported.", ArchiveError.Unsupported);
        if ((archiveFlags & 0x02) != 0) c.ReadVInt();   // volume number
    }

    private static RarFile ReadFileHeader(ReadOnlySpan<byte> header, Cursor c, int extraSize, ulong packedSize, long blockStart, long dataStart)
    {
        var fileFlags = c.ReadVInt();
        var unpacked = c.ReadVInt();
        var attributes = c.ReadVInt();
        DateTime? mtime = null;
        if ((fileFlags & 0x02) != 0) mtime = FromUnix(c.ReadU32());
        uint? crc = null;
        if ((fileFlags & 0x04) != 0) crc = c.ReadU32();
        var compInfo = c.ReadVInt();
        var host = c.ReadVInt();
        var nameLen = c.ReadVInt();
        if (nameLen > (ulong)c.Remaining) throw new ArchiveException("RAR file name overruns its header.");

        var nameBytes = c.Bytes((int)nameLen);
        var extraStart = header.Length - extraSize;
        if (c.Pos > extraStart) throw new ArchiveException("RAR file name overlaps the extra area.");

        string name;
        try { name = StrictUtf8.GetString(nameBytes); }
        catch (ArgumentException ex) { throw new ArchiveException("RAR file name is not valid UTF-8.", ex); }

        bool encrypted = false, redirected = false;
        var extra = new Cursor(header[extraStart..]);
        while (extra.Remaining > 0)
        {
            var size = extra.ReadVInt();
            if (size == 0 || size > (ulong)extra.Remaining) throw new ArchiveException("Malformed RAR extra record.");

            var rc = new Cursor(extra.Bytes((int)size));
            switch (rc.ReadVInt())
            {
                case 1: encrypted = true; break;
                case 5: redirected = true; break;
                case 3:
                    var tf = rc.ReadVInt();
                    if ((tf & 0x02) != 0)
                    {
                        var t = (tf & 0x01) != 0 ? FromUnix(rc.ReadU32()) : FromFileTime(rc.ReadU64());
                        mtime = t ?? mtime;
                    }

                    break;
            }
        }

        var directory = (fileFlags & 0x01) != 0;
        var unknownSize = (fileFlags & 0x08) != 0;
        return new RarFile(name, KindOf(redirected, directory, host, attributes), packedSize, unknownSize ? packedSize : unpacked, crc,
            unknownSize, encrypted, (int)((compInfo >> 7) & 7), blockStart, dataStart, mtime);
    }

    private static EntryKind KindOf(bool redirected, bool directory, ulong host, ulong attributes)
    {
        if (redirected) return EntryKind.Symlink;
        if (directory) return EntryKind.Directory;
        if (host != 1) return EntryKind.File;

        return (attributes & 0xF000) switch
        {
            0 or 0x8000 => EntryKind.File,
            0x4000 => EntryKind.Directory,
            0xA000 => EntryKind.Symlink,
            _ => EntryKind.Special,
        };
    }

    private static DateTime? FromUnix(uint seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    private static DateTime? FromFileTime(ulong fileTime) =>
        fileTime <= MaxFileTime ? DateTime.FromFileTimeUtc((long)fileTime).ToLocalTime() : null;

    public void CheckSupported(ArchiveEntry entry)
    {
        var f = _files[entry.Index];
        if (f.Kind != EntryKind.File) return;
        if (f.Encrypted) throw new ArchiveException("Encrypted RAR entries are not supported yet.", ArchiveError.Unsupported);
        if (f.UnknownSize) throw new ArchiveException("RAR entries of unknown size are not supported.", ArchiveError.Unsupported);
        if (f.Method != 0) throw new ArchiveException($"RAR compression method {f.Method} is not implemented yet.", ArchiveError.Unsupported);
        if (f.Crc32 is null) throw new ArchiveException("RAR entry has no CRC-32 (BLAKE2 integrity is not implemented yet).", ArchiveError.Unsupported);
    }

    public (ulong Start, ulong End) GetSpan(ArchiveEntry entry)
    {
        var f = _files[entry.Index];
        return ((ulong)f.BlockStart, (ulong)f.DataStart + f.PackedSize);
    }

    public void Extract(ArchiveEntry entry, IOutputSink sink, ArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);
        var f = _files[entry.Index];

        if (f.Kind == EntryKind.Directory)
        {
            if (f.UnpackedSize != 0) throw new ArchiveException("Directory entry has data.");
            return;
        }

        CheckSupported(entry);
        if (f.UnpackedSize > (ulong)limits.MaxEntrySize) throw new ArchiveException("Entry exceeds the configured size limit.", ArchiveError.LimitExceeded);
        if (f.PackedSize != f.UnpackedSize) throw new ArchiveException("Stored entry has mismatched sizes.");

        var crc = StoredData.Copy(_data.Slice((int)f.DataStart, (int)f.PackedSize), sink);
        if (crc != f.Crc32) throw new ArchiveException("CRC-32 mismatch.");
    }

    /// <summary>Bounds-checked forward reader over a header; any overrun is a Corrupt archive.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _s;

        public Cursor(ReadOnlySpan<byte> s)
        {
            _s = s;
            Pos = 0;
        }

        public int Pos { get; private set; }

        public readonly int Remaining => _s.Length - Pos;

        public ulong ReadVInt()
        {
            if (!VInt.TryRead(_s[Pos..], out var v, out var n)) throw Truncated();
            Pos += n;
            return v;
        }

        public uint ReadU32()
        {
            if (Remaining < 4) throw Truncated();
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_s[Pos..]);
            Pos += 4;
            return v;
        }

        public ulong ReadU64()
        {
            if (Remaining < 8) throw Truncated();
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_s[Pos..]);
            Pos += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > Remaining) throw Truncated();
            var r = _s.Slice(Pos, count);
            Pos += count;
            return r;
        }

        private static ArchiveException Truncated() => new("Truncated RAR header field.");
    }
}
