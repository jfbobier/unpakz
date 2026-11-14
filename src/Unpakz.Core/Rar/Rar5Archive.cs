using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Unpakz.Core.Checksums;

namespace Unpakz.Core.Rar;

/// <summary>
/// RAR 5.x container reader (layout from RARLAB's public technical note). Every length is range-checked, every header CRC verified.
/// Implemented: all structure, stored and compressed entries, solid archives, multi-volume sets, link/special detection, timestamps.
/// Reported as Unsupported (never guessed): encryption, BLAKE2-only integrity, encrypted headers. Multi-volume sets are supported.
/// </summary>
public sealed class Rar5Archive : IArchive
{
    private static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly byte[] Signature4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaxHeaderSize = 2 * 1024 * 1024;
    private const ulong MaxFileTime = 2_650_467_743_999_999_999UL;

    private readonly List<RarFile> _files;
    private readonly List<List<int>> _groups;
    private readonly object _decoderLock = new();

    internal DecoderStats Stats { get; } = new();

    private Rar5Decoder? _decoder;
    private int _decoderGroup = -1;
    private int _decoderNext;

    public string Format => "rar5";

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Number of volumes this archive was assembled from (1 for an ordinary archive).</summary>
    public int VolumeCount { get; }

    private sealed record RarFile(
        string Name, EntryKind Kind, ulong PackedSize, ulong UnpackedSize, uint? Crc32, bool UnknownSize, bool Encrypted,
        int Method, ulong SpanStart, ulong SpanEnd, ReadOnlyMemory<byte>[] Segments, DateTime? LastWrite,
        bool Solid, int DictLog, int Version, int Group, int GroupIndex);

    /// <summary>Everything one file-header block says; several of these (one per volume) can make up one file.</summary>
    private sealed class FilePart
    {
        public required string Name { get; init; }
        public required EntryKind Kind { get; init; }
        public required ulong Unpacked { get; init; }
        public required bool UnknownSize { get; init; }
        public required uint? Crc { get; init; }
        public required ulong CompInfo { get; init; }
        public required ulong Host { get; init; }
        public required bool Encrypted { get; init; }
        public required DateTime? LastWrite { get; init; }
    }

    /// <summary>A file whose data started in one volume and continues in the next.</summary>
    private sealed class PendingFile(FilePart first, ulong spanStart)
    {
        public FilePart First { get; } = first;
        public ulong SpanStart { get; } = spanStart;
        public List<ReadOnlyMemory<byte>> Segments { get; } = [];
        public ulong Packed { get; set; }
    }

    /// <summary>Chains compressed files into solid groups (a non-solid file starts a new group); stored files and non-files take no part.</summary>
    private static List<List<int>> AssignGroups(List<RarFile> files)
    {
        var groups = new List<List<int>>();
        List<int>? current = null;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (f.Kind != EntryKind.File || f.Method == 0) continue;

            if (current is null || !f.Solid)
            {
                current = [];
                groups.Add(current);
            }

            files[i] = f with { Group = groups.Count - 1, GroupIndex = current.Count };
            current.Add(i);
        }

        return groups;
    }

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static bool HasRar4Signature(ReadOnlySpan<byte> data) => data.StartsWith(Signature4);

    private Rar5Archive(List<RarFile> files, int volumeCount)
    {
        _files = files;
        VolumeCount = volumeCount;
        _groups = AssignGroups(files);
        var list = new List<ArchiveEntry>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            list.Add(new ArchiveEntry(i, f.Name, f.Kind, f.PackedSize, f.UnpackedSize, f.LastWrite));
        }

        Entries = list;
    }

    public static Rar5Archive Open(ReadOnlyMemory<byte> data, ArchiveLimits limits) => Open([data], limits);

    /// <summary>Opens a multi-volume set: the volumes in order, first volume first. A set with a volume missing is refused.</summary>
    public static Rar5Archive Open(IReadOnlyList<ReadOnlyMemory<byte>> volumes, ArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(limits);
        if (volumes.Count == 0) throw new ArgumentException("At least one volume is required.", nameof(volumes));
        if (volumes.Count > limits.MaxVolumes) throw new ArchiveException("Too many volumes.", ArchiveError.LimitExceeded);

        var parser = new VolumeParser(volumes, limits);
        return new Rar5Archive(parser.Run(), volumes.Count);
    }

    /// <summary>Parses all volumes in sequence, joining the parts of split files.</summary>
    private sealed class VolumeParser(IReadOnlyList<ReadOnlyMemory<byte>> volumes, ArchiveLimits limits)
    {
        private readonly List<RarFile> _files = [];
        private PendingFile? _pending;
        private ulong _base;      // virtual offset of the current volume: volumes laid end to end

        public List<RarFile> Run()
        {
            for (var v = 0; v < volumes.Count; v++)
            {
                var more = ParseVolume(v);
                var last = v == volumes.Count - 1;
                if (!last && !more)
                    throw new ArchiveException($"Volume {v + 1} says it is the last volume, but more volumes were supplied.");
                if (last && more)
                    throw new ArchiveException($"The set is incomplete: volume {v + 2} is missing.", ArchiveError.MissingVolume) { Volume = v + 2 };

                _base += (ulong)volumes[v].Length;
            }

            if (_pending is not null) throw new ArchiveException($"File '{_pending.First.Name}' starts in one volume but never continues.");
            return _files;
        }

        /// <summary>Parses one volume; returns true if its end block says another volume follows.</summary>
        private bool ParseVolume(int volume)
        {
            var data = volumes[volume];
            var span = data.Span;
            if (!HasSignature(span)) throw new ArchiveException($"Volume {volume + 1} is not a RAR 5 archive.");

            long pos = Signature.Length, len = span.Length;
            var first = true;
            var more = false;
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
                        ReadMainHeader(ref c, volume);
                        break;
                    case 2:
                        AddPart(ReadFilePart(header, c, (int)extraSize), flags, volume, pos, dataStart, dataSize, data);
                        break;
                    case 3:   // service header (comment, recovery record, quick open, NTFS streams): not extracted
                        break;
                    case 4:
                        throw new ArchiveException("Encrypted RAR headers are not supported.", ArchiveError.Unsupported);
                    case 5:
                        sawEnd = true;
                        more = c.Remaining > 0 && (c.ReadVInt() & 1) != 0;
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
            return more;
        }

        private void ReadMainHeader(ref Cursor c, int volume)
        {
            var archiveFlags = c.ReadVInt();
            var isVolume = (archiveFlags & 0x01) != 0;
            var number = (archiveFlags & 0x02) != 0 ? c.ReadVInt() : 0;

            if (volumes.Count > 1 && !isVolume) throw new ArchiveException($"Volume {volume + 1} is not part of a multi-volume set.");
            if (number > (ulong)volume)
                throw new ArchiveException($"The set is incomplete: volume {volume + 1} is missing.", ArchiveError.MissingVolume) { Volume = volume + 1 };
            if (number < (ulong)volume) throw new ArchiveException($"Volume {volume + 1} is out of order or duplicated.");
        }

        private void AddPart(FilePart part, ulong flags, int volume, long blockStart, long dataStart, ulong dataSize, ReadOnlyMemory<byte> data)
        {
            var splitBefore = (flags & 0x08) != 0;
            var splitAfter = (flags & 0x10) != 0;
            var segment = data.Slice((int)dataStart, (int)dataSize);
            var spanStart = _base + (ulong)blockStart;
            var spanEnd = _base + (ulong)dataStart + dataSize;

            if (_pending is { } pending)
            {
                if (!splitBefore)
                    throw new ArchiveException($"File '{pending.First.Name}' should continue in this volume, but a different entry follows.");
                var f = pending.First;
                if (part.Name != f.Name || part.Unpacked != f.Unpacked || part.CompInfo != f.CompInfo || part.Host != f.Host)
                    throw new ArchiveException($"The parts of '{f.Name}' disagree about the file.");

                pending.Segments.Add(segment);
                pending.Packed += dataSize;
                if (splitAfter) return;

                _pending = null;
                _files.Add(Complete(pending, part.Crc, spanEnd));
                return;
            }

            if (splitBefore) throw new ArchiveException($"'{part.Name}' is a continuation, but nothing precedes it.");
            if (_files.Count >= limits.MaxEntries) throw new ArchiveException("Too many entries.", ArchiveError.LimitExceeded);

            var started = new PendingFile(part, spanStart);
            started.Segments.Add(segment);
            started.Packed = dataSize;
            if (splitAfter)
            {
                _pending = started;
                return;
            }

            _files.Add(Complete(started, part.Crc, spanEnd));
        }

        private static RarFile Complete(PendingFile p, uint? crc, ulong spanEnd)
        {
            var f = p.First;
            return new RarFile(f.Name, f.Kind, p.Packed, f.UnknownSize ? p.Packed : f.Unpacked, crc, f.UnknownSize, f.Encrypted,
                (int)((f.CompInfo >> 7) & 7), p.SpanStart, spanEnd, [.. p.Segments], f.LastWrite,
                (f.CompInfo & 0x40) != 0, (int)((f.CompInfo >> 10) & 0xF), (int)(f.CompInfo & 0x3F), -1, 0);
        }
    }

    private static FilePart ReadFilePart(ReadOnlySpan<byte> header, Cursor c, int extraSize)
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
        return new FilePart
        {
            Name = name,
            Kind = KindOf(redirected, directory, host, attributes),
            Unpacked = unpacked,
            UnknownSize = (fileFlags & 0x08) != 0,
            Crc = crc,
            CompInfo = compInfo,
            Host = host,
            Encrypted = encrypted,
            LastWrite = mtime,
        };
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
        if (f.Method > 5) throw new ArchiveException($"RAR compression method {f.Method} is not supported.", ArchiveError.Unsupported);
        if (f.Method != 0 && f.Version != 0)
            throw new ArchiveException($"RAR compression version {f.Version} is not supported.", ArchiveError.Unsupported);
        if (f.Crc32 is null) throw new ArchiveException("RAR entry has no CRC-32 (BLAKE2 integrity is not implemented yet).", ArchiveError.Unsupported);
    }

    public (ulong Start, ulong End) GetSpan(ArchiveEntry entry)
    {
        var f = _files[entry.Index];
        return (f.SpanStart, f.SpanEnd);
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

        if (f.Method == 0)
        {
            if (f.PackedSize != f.UnpackedSize) throw new ArchiveException("Stored entry has mismatched sizes.");

            var crc = StoredData.Copy(f.Segments, sink);
            if (crc != f.Crc32) throw new ArchiveException("CRC-32 mismatch.");
            return;
        }

        ExtractCompressed(f, sink, limits);
    }

    private void ExtractCompressed(RarFile f, IOutputSink sink, ArchiveLimits limits)
    {
        var members = _groups[f.Group];
        var dictLog = 0;
        ulong total = 0;
        foreach (var idx in members)
        {
            dictLog = Math.Max(dictLog, _files[idx].DictLog);
            total += _files[idx].UnpackedSize;
        }

        var dictionary = Rar5Decoder.DictionarySize(dictLog);
        var windowLimit = Math.Min(dictionary, NextPowerOfTwo(total));
        if (windowLimit > limits.MaxDictionarySize)
            throw new ArchiveException("The RAR dictionary exceeds the configured memory limit.", ArchiveError.LimitExceeded);

        var verify = new CrcSink(sink);
        lock (_decoderLock)
        {
            try
            {
                if (_decoder is null || _decoderGroup != f.Group || _decoderNext > f.GroupIndex)
                {
                    _decoder = new Rar5Decoder(dictionary, windowLimit, Stats);
                    _decoderGroup = f.Group;
                    _decoderNext = 0;
                }

                while (_decoderNext < f.GroupIndex)
                {
                    var earlier = _files[members[_decoderNext]];
                    CheckSupported(Entries[members[_decoderNext]]);
                    _decoder.Decode(PackedFor(earlier), earlier.UnpackedSize, solid: _decoderNext > 0, NullSink.Instance);
                    Stats.FilesDecoded++;
                    _decoderNext++;
                }

                _decoder.Decode(PackedFor(f), f.UnpackedSize, solid: f.GroupIndex > 0, verify);
                Stats.FilesDecoded++;
                _decoderNext = f.GroupIndex + 1;
            }
            catch
            {
                _decoder = null;      // a failed decode leaves the shared window in an unknown state
                _decoderGroup = -1;
                throw;
            }
        }

        if (verify.Crc != f.Crc32) throw new ArchiveException("CRC-32 mismatch.");
    }

    private static PackedStream PackedFor(RarFile f) => new(f.Segments);

    private static long NextPowerOfTwo(ulong value)
    {
        var size = 1L << 16;
        while ((ulong)size < value && size < (1L << 40)) size <<= 1;
        return size;
    }

    private sealed class CrcSink(IOutputSink inner) : IOutputSink
    {
        public uint Crc { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            Crc = Crc32.Update(Crc, data);
            inner.Write(data);
        }
    }

    private sealed class NullSink : IOutputSink
    {
        public static readonly NullSink Instance = new();

        public void Write(ReadOnlySpan<byte> data)
        {
        }
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
