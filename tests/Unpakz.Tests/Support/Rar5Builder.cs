using System.Buffers.Binary;
using System.Text;

namespace Unpakz.Tests.Support;

internal sealed class FileOpts
{
    public int Method { get; init; }
    public int CompressionVersion { get; init; }
    public int DictionaryLog { get; init; }
    public bool OmitCrc { get; init; }
    public uint? CrcOverride { get; init; }
    public ulong Host { get; init; } = 1;                 // 0 = Windows, 1 = Unix
    public ulong Attr { get; init; } = 0x81A4;            // regular, 0644
    public uint? Mtime { get; init; } = 1_700_000_000;
    public byte[][] Extras { get; init; } = [];
    public bool Solid { get; init; }
    public bool UnknownSize { get; init; }
    public ulong? UnpackedOverride { get; init; }
    public ulong? DataSizeField { get; init; }
    public byte[]? NameBytes { get; init; }
    public ulong? NameLenField { get; init; }
    public ulong? ExtraSizeField { get; init; }
}

/// <summary>Independent RAR5 writer for fixtures (stored data only). Shares no code with the reader under test.</summary>
internal sealed class Rar5Builder
{
    public static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly List<byte[]> _blocks = [];

    public ulong ArchiveFlags { get; set; }
    public bool OmitMain { get; set; }
    public bool OmitEnd { get; set; }
    public byte[] Trailing { get; set; } = [];

    public Rar5Builder AddFile(string name, byte[] data, FileOpts? o = null)
    {
        o ??= new FileOpts();
        _blocks.Add(FileBlock(o.NameBytes ?? Encoding.UTF8.GetBytes(name), data, o, isDir: false));
        return this;
    }

    public Rar5Builder AddDirectory(string name, FileOpts? o = null)
    {
        o ??= new FileOpts { Attr = 0x41ED };
        _blocks.Add(FileBlock(Encoding.UTF8.GetBytes(name), [], o, isDir: true));
        return this;
    }

    public Rar5Builder AddBlock(ulong type, ulong flags, byte[] specific, byte[]? data = null, byte[]? extra = null, ulong? dataSizeField = null)
    {
        _blocks.Add(Block(type, flags, specific, extra ?? [], data ?? [], dataSizeField, null));
        return this;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        if (!OmitMain)
        {
            var spec = new List<byte>(VInt(ArchiveFlags));
            if ((ArchiveFlags & 2) != 0) spec.AddRange(VInt(1));
            ms.Write(Block(1, 0, [.. spec], [], [], null, null));
        }

        foreach (var b in _blocks) ms.Write(b);
        if (!OmitEnd) ms.Write(Block(5, 0, VInt(0), [], [], null, null));
        ms.Write(Trailing);
        return ms.ToArray();
    }

    // ---- extra records ----
    public static byte[] Extra(ulong type, byte[] data) => [.. VInt((ulong)VInt(type).Length + (ulong)data.Length), .. VInt(type), .. data];
    public static byte[] EncryptionExtra() => Extra(1, [.. VInt(0), .. VInt(0), 15, .. new byte[16 + 12]]);
    public static byte[] HashExtra() => Extra(2, [.. VInt(0), .. new byte[32]]);
    public static byte[] RedirectionExtra(ulong linkType, string target) =>
        Extra(5, [.. VInt(linkType), .. VInt(0), .. VInt((ulong)Encoding.UTF8.GetByteCount(target)), .. Encoding.UTF8.GetBytes(target)]);
    public static byte[] UnixTimeExtra(uint sec) => Extra(3, [.. VInt(0x01 | 0x02), .. U32(sec)]);
    public static byte[] WindowsTimeExtra(ulong filetime) => Extra(3, [.. VInt(0x02), .. BitConverter.GetBytes(filetime)]);

    // ---- encoding ----
    private static byte[] FileBlock(byte[] name, byte[] data, FileOpts o, bool isDir)
    {
        ulong fflags = (isDir ? 1UL : 0) | (o.Mtime is not null ? 2UL : 0) | (o.OmitCrc ? 0UL : 4UL) | (o.UnknownSize ? 8UL : 0);
        var crc = o.CrcOverride ?? ZipBuilder.Crc32(data);
        var comp = ((ulong)o.Method << 7) | (o.Solid ? 1UL << 6 : 0) | (uint)o.CompressionVersion | ((ulong)(uint)o.DictionaryLog << 10);

        var spec = new List<byte>();
        spec.AddRange(VInt(fflags));
        spec.AddRange(VInt(o.UnpackedOverride ?? (ulong)data.Length));
        spec.AddRange(VInt(o.Attr));
        if (o.Mtime is { } m) spec.AddRange(U32(m));
        if (!o.OmitCrc) spec.AddRange(U32(crc));
        spec.AddRange(VInt(comp));
        spec.AddRange(VInt(o.Host));
        spec.AddRange(VInt(o.NameLenField ?? (ulong)name.Length));
        spec.AddRange(name);

        var extra = o.Extras.SelectMany(e => e).ToArray();
        return Block(2, 0, [.. spec], extra, data, o.DataSizeField, o.ExtraSizeField);
    }

    private static byte[] Block(ulong type, ulong flags, byte[] specific, byte[] extra, byte[] data, ulong? dataSizeField, ulong? extraSizeField)
    {
        var hasData = data.Length > 0 || dataSizeField is not null;
        var hasExtra = extra.Length > 0 || extraSizeField is not null;
        var f = flags | (hasExtra ? 1UL : 0) | (hasData ? 2UL : 0);

        var h = new List<byte>();
        h.AddRange(VInt(type));
        h.AddRange(VInt(f));
        if (hasExtra) h.AddRange(VInt(extraSizeField ?? (ulong)extra.Length));
        if (hasData) h.AddRange(VInt(dataSizeField ?? (ulong)data.Length));
        h.AddRange(specific);
        h.AddRange(extra);

        var sizeBytes = VInt((ulong)h.Count);
        var crcInput = sizeBytes.Concat(h).ToArray();
        return [.. U32(ZipBuilder.Crc32(crcInput)), .. crcInput, .. data];
    }

    public static byte[] VInt(ulong v)
    {
        var o = new List<byte>();
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            o.Add(b);
        } while (v != 0);
        return [.. o];
    }

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    /// <summary>Recomputes and patches the header CRC of the block starting at <paramref name="blockStart"/> after a test edits its bytes.</summary>
    public static void FixHeaderCrc(byte[] archive, int blockStart)
    {
        var p = blockStart + 4;
        var size = 0; var shift = 0; var n = 0;
        while (true) { var b = archive[p + n]; size |= (b & 0x7F) << shift; shift += 7; n++; if ((b & 0x80) == 0) break; }
        var crc = ZipBuilder.Crc32(archive.AsSpan(p, n + size));
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(blockStart), crc);
    }
}
