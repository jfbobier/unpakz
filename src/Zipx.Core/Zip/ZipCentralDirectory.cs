using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zipx.Core.Zip;

/// <summary>
/// Parses and validates the ZIP central directory (including ZIP64). Every field is treated as hostile:
/// all offsets and lengths are range-checked before use and only <see cref="ArchiveException"/> escapes.
/// Not yet supported: data prepended to the archive (SFX stubs), multi-disk archives.
/// </summary>
public static class ZipCentralDirectory
{
    private const uint EocdSig = 0x06054b50, Eocd64Sig = 0x06064b50, Loc64Sig = 0x07064b50, CentralSig = 0x02014b50;
    private const int EocdSize = 22, Eocd64Size = 56, Loc64Size = 20, CentralFixed = 46, LocalFixed = 30;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<ZipEntryInfo> Read(ReadOnlySpan<byte> zip) => Read(zip, new ZipLimits());

    public static IReadOnlyList<ZipEntryInfo> Read(ReadOnlySpan<byte> zip, ZipLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var (count, cdOffset, cdSize) = LocateDirectory(zip);

        if (count > (ulong)limits.MaxEntries)
            throw new ArchiveException("Too many entries.", ArchiveError.LimitExceeded);
        if (count > cdSize / CentralFixed)
            throw new ArchiveException("Entry count impossible for central directory size.");

        var cd = zip.Slice((int)cdOffset, (int)cdSize);
        var entries = new List<ZipEntryInfo>((int)count);
        var pos = 0;
        for (ulong i = 0; i < count; i++)
        {
            entries.Add(ReadEntry(cd, ref pos, cdOffset));
        }

        return entries;
    }

    private static (ulong Count, ulong CdOffset, ulong CdSize) LocateDirectory(ReadOnlySpan<byte> zip)
    {
        if (zip.Length < EocdSize) throw new ArchiveException("Too small to be a ZIP archive.");

        var eocd = FindEocd(zip);
        var e = zip[eocd..];
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
        var cdDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
        ulong entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        ulong total = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        ulong cdSize = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        ulong cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);

        if (disk != 0 || cdDisk != 0 || entriesOnDisk != total)
            throw new ArchiveException("Multi-disk archives are not supported.", ArchiveError.Unsupported);

        var endBound = (ulong)eocd;
        var saturated = cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF || total == 0xFFFF;
        if (saturated)
        {
            var locPos = eocd - Loc64Size;
            var hasLocator = locPos >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(zip[locPos..]) == Loc64Sig;
            if (hasLocator)
            {
                (total, cdSize, cdOffset, endBound) = ReadZip64(zip, locPos);
            }
            else if (cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
            {
                throw new ArchiveException("ZIP64 fields present but no ZIP64 locator.");
            }
            // else: exactly 65535 entries without ZIP64 is legal
        }

        if (cdOffset > endBound || cdSize > endBound - cdOffset)
            throw new ArchiveException("Central directory lies outside the archive.");

        return (total, cdOffset, cdSize);
    }

    private static int FindEocd(ReadOnlySpan<byte> zip)
    {
        var lowest = Math.Max(0, zip.Length - EocdSize - ushort.MaxValue);
        for (var pos = zip.Length - EocdSize; pos >= lowest; pos--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip[pos..]) != EocdSig) continue;
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(zip[(pos + 20)..]);
            if (pos + EocdSize + commentLen == zip.Length) return pos;
        }

        throw new ArchiveException("End of central directory record not found.");
    }

    private static (ulong Total, ulong CdSize, ulong CdOffset, ulong EndBound) ReadZip64(ReadOnlySpan<byte> zip, int locPos)
    {
        var loc = zip[locPos..];
        var diskWithE64 = BinaryPrimitives.ReadUInt32LittleEndian(loc[4..]);
        var e64Offset = BinaryPrimitives.ReadUInt64LittleEndian(loc[8..]);
        var totalDisks = BinaryPrimitives.ReadUInt32LittleEndian(loc[16..]);
        if (diskWithE64 != 0 || totalDisks > 1)
            throw new ArchiveException("Multi-disk archives are not supported.", ArchiveError.Unsupported);

        if (e64Offset > (ulong)locPos || (ulong)locPos - e64Offset < Eocd64Size)
            throw new ArchiveException("ZIP64 end record offset out of range.");

        var r = zip[(int)e64Offset..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(r) != Eocd64Sig)
            throw new ArchiveException("Bad ZIP64 end record signature.");

        var recSize = BinaryPrimitives.ReadUInt64LittleEndian(r[4..]);
        if (recSize < Eocd64Size - 12) throw new ArchiveException("ZIP64 end record too small.");

        var disk = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
        var cdDisk = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
        var onDisk = BinaryPrimitives.ReadUInt64LittleEndian(r[24..]);
        var total = BinaryPrimitives.ReadUInt64LittleEndian(r[32..]);
        var cdSize = BinaryPrimitives.ReadUInt64LittleEndian(r[40..]);
        var cdOffset = BinaryPrimitives.ReadUInt64LittleEndian(r[48..]);
        if (disk != 0 || cdDisk != 0 || onDisk != total)
            throw new ArchiveException("Multi-disk archives are not supported.", ArchiveError.Unsupported);

        return (total, cdSize, cdOffset, e64Offset);
    }

    private static ZipEntryInfo ReadEntry(ReadOnlySpan<byte> cd, ref int pos, ulong cdStart)
    {
        if (cd.Length - pos < CentralFixed) throw new ArchiveException("Truncated central directory header.");
        var h = cd[pos..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != CentralSig)
            throw new ArchiveException("Bad central directory header signature.");

        var versionMade = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
        var method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
        var time = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        var date = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        ulong csize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        ulong usize = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
        int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
        uint diskStart = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
        var extAttr = BinaryPrimitives.ReadUInt32LittleEndian(h[38..]);
        ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);

        var total = CentralFixed + nameLen + extraLen + commentLen;
        if (cd.Length - pos < total) throw new ArchiveException("Central directory header overruns directory.");

        var name = DecodeName(h.Slice(CentralFixed, nameLen), (flags & 0x0800) != 0);
        ApplyZip64Extra(h.Slice(CentralFixed + nameLen, extraLen), ref usize, ref csize, ref localOffset, ref diskStart);

        if (diskStart != 0) throw new ArchiveException("Multi-disk archives are not supported.", ArchiveError.Unsupported);
        if (localOffset > cdStart || cdStart - localOffset < LocalFixed)
            throw new ArchiveException("Local header offset outside archive body.");
        if (method == 0 && (flags & 1) == 0 && csize != usize)
            throw new ArchiveException("Stored entry has mismatched sizes.");

        pos += total;
        return new ZipEntryInfo(name, versionMade, flags, method, ((uint)date << 16) | time, crc, csize, usize, localOffset, extAttr);
    }

    private static string DecodeName(ReadOnlySpan<byte> raw, bool utf8)
    {
        if (utf8)
        {
            try { return StrictUtf8.GetString(raw); }
            catch (ArgumentException ex) { throw new ArchiveException("Entry name is not valid UTF-8.", ex); }
        }

        return string.Create(raw.Length, raw.ToArray(), static (dst, src) =>
        {
            for (var i = 0; i < dst.Length; i++) dst[i] = Cp437.Decode(src[i]);
        });
    }

    private static void ApplyZip64Extra(ReadOnlySpan<byte> extra, ref ulong usize, ref ulong csize, ref ulong offset, ref uint disk)
    {
        while (extra.Length > 0)
        {
            if (extra.Length < 4) throw new ArchiveException("Truncated extra field header.");
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (extra.Length - 4 < size) throw new ArchiveException("Extra field overruns its container.");

            if (id == 0x0001)
            {
                var d = extra.Slice(4, size);
                if (usize == 0xFFFFFFFF) usize = TakeU64(ref d);
                if (csize == 0xFFFFFFFF) csize = TakeU64(ref d);
                if (offset == 0xFFFFFFFF) offset = TakeU64(ref d);
                if (disk == 0xFFFF)
                {
                    if (d.Length < 4) throw new ArchiveException("ZIP64 extra field too short.");
                    disk = BinaryPrimitives.ReadUInt32LittleEndian(d);
                }
            }

            extra = extra[(4 + size)..];
        }
    }

    private static ulong TakeU64(ref ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) throw new ArchiveException("ZIP64 extra field too short.");
        var v = BinaryPrimitives.ReadUInt64LittleEndian(d);
        d = d[8..];
        return v;
    }
}
