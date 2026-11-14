namespace Unpakz.Tests.Support;

/// <summary>MSB-first bit writer (the order RAR5 uses).</summary>
internal sealed class RarBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bits;

    public int TotalBits => _bits;

    public void Write(long value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            var bit = (int)((value >> i) & 1);
            if (_bits % 8 == 0) _bytes.Add(0);
            _bytes[^1] |= (byte)(bit << (7 - (_bits % 8)));
            _bits++;
        }
    }

    public byte[] ToBytes() => [.. _bytes];
}

/// <summary>Canonical prefix codes from code lengths, assigned the way RAR's decode tables expect (by length, then symbol index).</summary>
internal sealed class RarHuffmanEncoder
{
    private readonly int[] _code;
    private readonly int[] _len;

    public RarHuffmanEncoder(int[] lengths)
    {
        _len = lengths;
        _code = new int[lengths.Length];
        var count = new int[17];
        foreach (var l in lengths) count[l]++;
        count[0] = 0;
        var next = new int[17];
        var code = 0;
        for (var l = 1; l <= 16; l++)
        {
            code = (code + count[l - 1]) << 1;
            next[l] = code;
        }

        for (var s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] > 0) _code[s] = next[lengths[s]]++;
        }
    }

    public void Write(RarBitWriter w, int symbol)
    {
        if (_len[symbol] == 0) throw new InvalidOperationException($"symbol {symbol} has no code");
        w.Write(_code[symbol], _len[symbol]);
    }
}

/// <summary>Expected-output model shared across blocks and files (the LZ77 window and RAR's repeat-distance cache).</summary>
internal sealed class RarModel
{
    public List<byte> History { get; } = [];
    public long[] Dist { get; } = new long[4];
    public int LastLen { get; set; }
    public int FileStart { get; set; }

    public byte[] FileOutput() => [.. History.Skip(FileStart)];

    public void Copy(int len, long dist)
    {
        if (dist < 1 || dist > History.Count) throw new InvalidOperationException("model: distance outside history");
        for (var i = 0; i < len; i++) History.Add(History[^(int)dist]);
    }

    public void StartNewFile(bool solid)
    {
        if (!solid)
        {
            History.Clear();
            Array.Clear(Dist);
            LastLen = 0;
        }

        FileStart = History.Count;
    }
}

/// <summary>Builds one RAR5 compressed block with fixed or custom Huffman code lengths. Test-only: shares nothing with the decoder.</summary>
internal sealed class RarBlockBuilder
{
    public const int Nc = 306, Dc = 64, Ldc = 16, Rc = 44;

    private readonly RarBitWriter _w = new();
    private readonly RarModel _model;
    private readonly RarHuffmanEncoder _ld, _dd, _ldd, _rd;

    public RarBlockBuilder(RarModel model, bool writeTables = true, int[]? ld = null, int[]? dd = null, int[]? ldd = null, int[]? rd = null,
        Action<RarBitWriter>? rawTables = null)
    {
        _model = model;
        ld ??= Enumerable.Repeat(9, Nc).ToArray();
        dd ??= Enumerable.Repeat(6, Dc).ToArray();
        ldd ??= Enumerable.Repeat(4, Ldc).ToArray();
        rd ??= Enumerable.Repeat(6, Rc).ToArray();
        _ld = new RarHuffmanEncoder(ld);
        _dd = new RarHuffmanEncoder(dd);
        _ldd = new RarHuffmanEncoder(ldd);
        _rd = new RarHuffmanEncoder(rd);
        HasTables = writeTables || rawTables is not null;
        if (rawTables is not null) rawTables(_w);
        else if (writeTables) WriteTables(ld, dd, ldd, rd);
    }

    public bool HasTables { get; }

    public RarBitWriter Raw => _w;

    /// <summary>The bit-length table is "all 20 symbols, 5 bits each", so every value 0..19 is just its own 5-bit number.</summary>
    private void WriteTables(int[] ld, int[] dd, int[] ldd, int[] rd)
    {
        for (var i = 0; i < 20; i++) _w.Write(5, 4);                       // BC lengths: nibble 5 for each symbol
        foreach (var l in ld.Concat(dd).Concat(ldd).Concat(rd)) _w.Write(l, 5);
    }

    // ---- tokens (each also advances the expected-output model) ----
    public RarBlockBuilder Literal(byte b)
    {
        _ld.Write(_w, b);
        _model.History.Add(b);
        return this;
    }

    public RarBlockBuilder Literals(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) Literal(b);
        return this;
    }

    public RarBlockBuilder Match(int len, long dist, bool trackModel = true)
    {
        var adj = (dist > 0x100 ? 1 : 0) + (dist > 0x2000 ? 1 : 0) + (dist > 0x40000 ? 1 : 0);
        var (code, extra, bits) = LenCode(len - adj);
        _ld.Write(_w, 262 + code);
        if (bits > 0) _w.Write(extra, bits);

        var d = dist - 1;
        int slot;
        long dExtra = 0;
        if (d < 4) slot = (int)d;
        else
        {
            slot = 4;
            while (true)
            {
                var dbits = (slot / 2) - 1;
                var baseValue = (long)(2 | (slot & 1)) << dbits;
                if (d >= baseValue && d < baseValue + (1L << dbits)) { dExtra = d - baseValue; break; }
                slot++;
            }
        }

        _dd.Write(_w, slot);
        if (slot >= 4)
        {
            var dbits = (slot / 2) - 1;
            if (dbits < 4) _w.Write(dExtra, dbits);
            else
            {
                if (dbits > 4) _w.Write(dExtra >> 4, dbits - 4);
                _ldd.Write(_w, (int)(dExtra & 15));
            }
        }

        if (!trackModel) return this;

        _model.Copy(len, dist);
        _model.Dist[3] = _model.Dist[2]; _model.Dist[2] = _model.Dist[1]; _model.Dist[1] = _model.Dist[0]; _model.Dist[0] = dist;
        _model.LastLen = len;
        return this;
    }

    public RarBlockBuilder RepeatLast()
    {
        _ld.Write(_w, 257);
        if (_model.LastLen != 0) _model.Copy(_model.LastLen, _model.Dist[0]);
        return this;
    }

    public RarBlockBuilder RepeatDistance(int index, int len, bool trackModel = true)
    {
        _ld.Write(_w, 258 + index);
        if (!trackModel)
        {
            var (c, e, b) = LenCode(len);
            _rd.Write(_w, c);
            if (b > 0) _w.Write(e, b);
            return this;
        }

        var dist = _model.Dist[index];
        for (var i = index; i > 0; i--) _model.Dist[i] = _model.Dist[i - 1];
        _model.Dist[0] = dist;

        var (code, extra, bits) = LenCode(len);
        _rd.Write(_w, code);
        if (bits > 0) _w.Write(extra, bits);
        _model.Copy(len, dist);
        _model.LastLen = len;
        return this;
    }

    /// <summary>Declares a filter. Does not change the model: filtered output is computed by the test.</summary>
    public RarBlockBuilder Filter(int type, long start, long length, int channels = 1)
    {
        _ld.Write(_w, 256);
        WriteFilterValue(start);
        WriteFilterValue(length);
        _w.Write(type, 3);
        if (type == 0) _w.Write(channels - 1, 5);
        return this;
    }

    private void WriteFilterValue(long v)
    {
        var bytes = v <= 0xFF ? 1 : v <= 0xFFFF ? 2 : v <= 0xFFFFFF ? 3 : 4;
        _w.Write(bytes - 1, 2);
        for (var i = 0; i < bytes; i++) _w.Write((v >> (8 * i)) & 0xFF, 8);
    }

    public static (int Code, int Extra, int Bits) LenCode(int len)
    {
        var x = len - 2;
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(len));
        for (var code = 0; code < 44; code++)
        {
            if (code < 8) { if (x == code) return (code, 0, 0); continue; }
            var bits = (code / 4) - 1;
            var baseValue = (4 | (code & 3)) << bits;
            if (x >= baseValue && x < baseValue + (1 << bits)) return (code, x - baseValue, bits);
        }

        throw new ArgumentOutOfRangeException(nameof(len), "length not representable");
    }

    /// <summary>Finishes the block: header (flags, checksum, size) + data. <paramref name="validBitsOverride"/> lets tests lie about the last byte.</summary>
    public byte[] Build(bool last = true, int? validBitsOverride = null, int? sizeFieldBytes = null, byte? checksumXor = null, long? sizeOverride = null,
        int cutDataBytes = 0)
    {
        var data = _w.ToBytes();
        if (cutDataBytes > 0) data = data[..^cutDataBytes];
        var valid = validBitsOverride ?? (cutDataBytes > 0 ? 8 : _w.TotalBits % 8 == 0 ? 8 : _w.TotalBits % 8);
        var size = sizeOverride ?? data.Length;
        var sizeBytes = sizeFieldBytes ?? (size < 256 ? 1 : size < 65536 ? 2 : 3);

        var flags = (byte)((HasTables ? 0x80 : 0) | (last ? 0x40 : 0) | ((sizeBytes - 1) << 3) | (valid - 1));
        var check = (byte)(0x5A ^ flags);
        var header = new List<byte> { flags, 0 };
        for (var i = 0; i < sizeBytes; i++)
        {
            var b = (byte)((size >> (8 * i)) & 0xFF);
            header.Add(b);
            check ^= b;
        }

        header[1] = (byte)(check ^ (checksumXor ?? 0));
        return [.. header, .. data];
    }
}
