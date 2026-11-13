using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Unpakz.Tests.Support;

/// <summary>Independent, byte-exact ZIP writer used only to build fixtures. Shares no code with the parser under test.</summary>
internal sealed class ZipBuilder
{
    internal sealed record Item(string Name, byte[] Data, ushort Method, bool Utf8, bool Zip64Extra, uint? Attr);

    private readonly List<Item> _items = [];

    public bool ForceZip64Eocd { get; set; }
    public byte[] Comment { get; set; } = [];

    public ZipBuilder Add(string name, byte[] data, bool deflate = false, bool utf8 = true, bool zip64Extra = false, uint? externalAttrs = null)
    {
        _items.Add(new Item(name, data, deflate ? (ushort)8 : (ushort)0, utf8, zip64Extra, externalAttrs));
        return this;
    }

    public ZipBuilder AddDirectory(string name) => Add(name.EndsWith('/') ? name : name + "/", []);

    public const uint SymlinkAttr = 0xA1FF0000, FifoAttr = 0x11A40000, DirModeAttr = 0x41ED0000;

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        var central = new List<(Item Item, uint Crc, byte[] Comp, long Offset, byte[] NameBytes)>();

        foreach (var it in _items)
        {
            var nameBytes = it.Utf8 ? Encoding.UTF8.GetBytes(it.Name) : Cp437(it.Name);
            var comp = it.Method == 8 ? Deflate(it.Data) : it.Data;
            var crc = Crc32(it.Data);
            var offset = ms.Position;

            var h = new byte[30];
            W32(h, 0, 0x04034b50);
            W16(h, 4, 20);
            W16(h, 6, it.Utf8 ? (ushort)0x0800 : (ushort)0);
            W16(h, 8, it.Method);
            W16(h, 10, 0x6000); W16(h, 12, 0x5821);   // arbitrary valid DOS time/date
            W32(h, 14, crc);
            W32(h, 18, (uint)comp.Length);
            W32(h, 22, (uint)it.Data.Length);
            W16(h, 26, (ushort)nameBytes.Length);
            W16(h, 28, 0);
            ms.Write(h);
            ms.Write(nameBytes);
            ms.Write(comp);
            central.Add((it, crc, comp, offset, nameBytes));
        }

        var cdStart = ms.Position;
        foreach (var (it, crc, comp, offset, nameBytes) in central)
        {
            var extra = Array.Empty<byte>();
            uint cs = (uint)comp.Length, us = (uint)it.Data.Length, lo = (uint)offset;
            if (it.Zip64Extra)
            {
                extra = new byte[4 + 24];
                W16(extra, 0, 1); W16(extra, 2, 24);
                W64(extra, 4, (ulong)it.Data.Length);
                W64(extra, 12, (ulong)comp.Length);
                W64(extra, 20, (ulong)offset);
                cs = us = lo = 0xFFFFFFFF;
            }

            var h = new byte[46];
            W32(h, 0, 0x02014b50);
            W16(h, 4, 0x031E);              // made by Unix, v3.0
            W16(h, 6, it.Zip64Extra ? (ushort)45 : (ushort)20);
            W16(h, 8, it.Utf8 ? (ushort)0x0800 : (ushort)0);
            W16(h, 10, it.Method);
            W16(h, 12, 0x6000); W16(h, 14, 0x5821);
            W32(h, 16, crc);
            W32(h, 20, cs); W32(h, 24, us);
            W16(h, 28, (ushort)nameBytes.Length);
            W16(h, 30, (ushort)extra.Length);
            W16(h, 32, 0); W16(h, 34, 0); W16(h, 36, 0);
            W32(h, 38, it.Attr ?? (it.Name.EndsWith('/') ? 0x41ED0010u : 0x81A40000u));
            W32(h, 42, lo);
            ms.Write(h);
            ms.Write(nameBytes);
            ms.Write(extra);
        }
        var cdSize = ms.Position - cdStart;

        var count = _items.Count;
        var zip64 = ForceZip64Eocd;
        if (zip64)
        {
            var e64Pos = ms.Position;
            var r = new byte[56];
            W32(r, 0, 0x06064b50); W64(r, 4, 44);
            W16(r, 12, 45); W16(r, 14, 45);
            W32(r, 16, 0); W32(r, 20, 0);
            W64(r, 24, (ulong)count); W64(r, 32, (ulong)count);
            W64(r, 40, (ulong)cdSize); W64(r, 48, (ulong)cdStart);
            ms.Write(r);

            var loc = new byte[20];
            W32(loc, 0, 0x07064b50); W32(loc, 4, 0); W64(loc, 8, (ulong)e64Pos); W32(loc, 16, 1);
            ms.Write(loc);
        }

        var eocd = new byte[22];
        W32(eocd, 0, 0x06054b50);
        W16(eocd, 4, 0); W16(eocd, 6, 0);
        W16(eocd, 8, zip64 ? (ushort)0xFFFF : (ushort)count);
        W16(eocd, 10, zip64 ? (ushort)0xFFFF : (ushort)count);
        W32(eocd, 12, zip64 ? 0xFFFFFFFF : (uint)cdSize);
        W32(eocd, 16, zip64 ? 0xFFFFFFFF : (uint)cdStart);
        W16(eocd, 20, (ushort)Comment.Length);
        ms.Write(eocd);
        ms.Write(Comment);
        return ms.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var o = new MemoryStream();
        using (var d = new DeflateStream(o, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        return o.ToArray();
    }

    // CP437 for the tiny set of characters tests use.
    private static byte[] Cp437(string s) =>
        [.. s.Select(c => c switch { 'é' => (byte)0x82, 'ü' => (byte)0x81, 'ñ' => (byte)0xA4, _ => (byte)c })];

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
}
