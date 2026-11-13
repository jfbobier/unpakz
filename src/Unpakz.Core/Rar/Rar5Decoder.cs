// The RAR5 compression algorithm (Huffman table layout, symbol/length/distance coding, filters) is not documented in
// RARLAB's public technical note. This implementation follows the behaviour of libarchive's RAR5 reader
// (archive_read_support_format_rar5.c, Copyright (c) 2018 Grzegorz Antoniak, BSD-2-Clause), which was studied as a
// reference and re-expressed here with our own structure, bounds checking and streaming window. See docs/THIRD-PARTY.md.

using System;
using System.Collections.Generic;

namespace Unpakz.Core.Rar;

/// <summary>
/// Streaming RAR5 LZ+Huffman decoder with the delta / x86 (E8, E8E9) / ARM filters. Pure: bytes in, bytes out to a sink.
/// Holds the sliding window across files so solid archives work. Every length and distance from the stream is validated;
/// only <see cref="ArchiveException"/> escapes for bad input.
/// </summary>
/// <summary>Counters so tests can prove which code paths a real archive exercised.</summary>
internal sealed class DecoderStats
{
    public int Filters;
    public int Delta;
    public int E8;
    public int E8E9;
    public int Arm;
    public int Wraps;
}

internal sealed class Rar5Decoder
{
    private const int HuffBc = 20, HuffNc = 306, HuffDc = 64, HuffLdc = 16, HuffRc = 44;
    private const int HuffTableSize = HuffNc + HuffDc + HuffRc + HuffLdc;
    private const int MaxFilterLength = 0x400000;
    private const int MaxPendingFilters = 1024;
    private const int InitialWindow = 1 << 20;

    private readonly long _dictionary;       // declared dictionary size (format-level validity limit)
    private readonly long _windowLimit;      // largest window we will ever allocate (power of two)
    private byte[] _window;
    private int _mask;

    private long _write;                     // bytes produced since init, across solid files
    private long _lastWrite;                 // bytes already sent to the sink
    private long _fileBase;
    private ulong _fileSize;

    private readonly long[] _dist = new long[4];
    private long _lastLen;

    private readonly HuffTable _bd = new(HuffBc), _ld = new(HuffNc), _dd = new(HuffDc), _ldd = new(HuffLdc), _rd = new(HuffRc);
    private bool _haveTables;

    private readonly Queue<Filter> _filters = new();
    private long _lastFilterStart, _lastFilterLength;
    private byte[] _filterBuffer = [];
    private byte[] _filterSource = [];

    private readonly DecoderStats? _stats;

    private sealed record Filter(int Type, long StartAbs, long StartRel, int Length, int Channels);

    public Rar5Decoder(long dictionarySize, long windowLimit, DecoderStats? stats = null)
    {
        _stats = stats;
        if (dictionarySize <= 0 || windowLimit <= 0) throw new ArgumentOutOfRangeException(nameof(dictionarySize));
        _dictionary = dictionarySize;
        _windowLimit = windowLimit;
        var initial = (int)Math.Min(InitialWindow, windowLimit);
        _window = new byte[initial];
        _mask = initial - 1;
    }

    public static long DictionarySize(int dictionaryLog) => 0x20000L << dictionaryLog;

    /// <summary>Decodes one file's compressed data. <paramref name="solid"/> continues from the previous file's window.</summary>
    public void Decode(PackedStream input, ulong unpackedSize, bool solid, IOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);

        if (!solid) ResetStream();
        _fileBase = _write;
        _fileSize = unpackedSize;
        _filters.Clear();
        _lastFilterStart = _lastFilterLength = 0;

        if (input.Remaining == 0)
        {
            // No packed data at all: only a zero-length file can look like that.
            if (unpackedSize != 0) throw new ArchiveException("Compressed data ended unexpectedly.");
            return;
        }

        // A file ends at its flagged last block, or when its packed data is used up (solid archives do not always flag it);
        // either way the exact size check below is what proves the output is complete.
        while (true)
        {
            var last = DecodeBlock(input, sink);
            if (last || input.Remaining == 0) break;
        }

        Flush(sink, final: true);
        if ((ulong)(_write - _fileBase) != unpackedSize)
            throw new ArchiveException("Decompressed size does not match the declared size.");
    }

    private void ResetStream()
    {
        _write = _lastWrite = 0;
        Array.Clear(_dist);
        _lastLen = 0;
        _haveTables = false;
    }

    // ---------------------------------------------------------------- blocks

    /// <summary>Decodes one block; returns true if it was flagged as the last block of the file.</summary>
    private bool DecodeBlock(PackedStream input, IOutputSink sink)
    {
        Span<byte> head = stackalloc byte[5];
        input.ReadExactly(head[..2]);
        var flags = head[0];
        var checksum = head[1];
        var sizeBytes = ((flags >> 3) & 7) + 1;
        if (sizeBytes > 3) throw new ArchiveException("Unsupported RAR block size field.");
        input.ReadExactly(head.Slice(2, sizeBytes));

        var blockSize = 0;
        for (var i = 0; i < sizeBytes; i++) blockSize |= head[2 + i] << (8 * i);

        var expected = (byte)(0x5A ^ flags ^ (blockSize & 0xFF) ^ ((blockSize >> 8) & 0xFF) ^ ((blockSize >> 16) & 0xFF));
        if (expected != checksum) throw new ArchiveException("RAR block header checksum mismatch.");
        if ((long)blockSize > input.Remaining) throw new ArchiveException("RAR block extends past the compressed data.");

        var tablePresent = (flags & 0x80) != 0;
        var isLast = (flags & 0x40) != 0;
        var bitSize = (flags & 7) + 1;

        var data = input.Take(blockSize);
        var bits = new Bits(data.Span);
        if (tablePresent) ParseTables(ref bits);
        else if (!_haveTables) throw new ArchiveException("RAR block uses Huffman tables that were never defined.");

        DecodeSymbols(ref bits, blockSize, bitSize, sink);
        return isLast;
    }

    private void ParseTables(ref Bits bits)
    {
        Span<byte> bitLength = stackalloc byte[HuffBc];
        var nibble = 0;                                   // index of the next 4-bit unit
        var data = bits.Data;

        for (var w = 0; w < HuffBc;)
        {
            var value = ReadNibble(data, ref nibble);
            if (value != 15)
            {
                bitLength[w++] = (byte)value;
                continue;
            }

            value = ReadNibble(data, ref nibble);
            if (value == 0)
            {
                bitLength[w++] = 15;
            }
            else
            {
                for (var k = 0; k < value + 2 && w < HuffBc; k++) bitLength[w++] = 0;
            }
        }

        bits.In = nibble >> 1;
        bits.Bit = (nibble & 1) * 4;

        if (!_bd.Build(bitLength)) throw new ArchiveException("Invalid RAR Huffman table.");

        var table = new byte[HuffTableSize];
        for (var i = 0; i < HuffTableSize;)
        {
            var num = _bd.Decode(ref bits);
            if (num < 16)
            {
                table[i++] = (byte)num;
            }
            else if (num < 18)
            {
                int n;
                if (num == 16) { n = (bits.Peek16() >> 13) + 3; bits.Skip(3); }
                else { n = (bits.Peek16() >> 9) + 11; bits.Skip(7); }

                if (i == 0) throw new ArchiveException("Invalid RAR Huffman table repeat.");
                while (n-- > 0 && i < HuffTableSize)
                {
                    table[i] = table[i - 1];
                    i++;
                }
            }
            else
            {
                int n;
                if (num == 18) { n = (bits.Peek16() >> 13) + 3; bits.Skip(3); }
                else { n = (bits.Peek16() >> 9) + 11; bits.Skip(7); }

                while (n-- > 0 && i < HuffTableSize) table[i++] = 0;
            }
        }

        var span = table.AsSpan();
        if (!_ld.Build(span[..HuffNc])
            || !_dd.Build(span.Slice(HuffNc, HuffDc))
            || !_ldd.Build(span.Slice(HuffNc + HuffDc, HuffLdc))
            || !_rd.Build(span.Slice(HuffNc + HuffDc + HuffLdc, HuffRc)))
        {
            throw new ArchiveException("Invalid RAR Huffman table.");
        }

        _haveTables = true;
    }

    private static int ReadNibble(ReadOnlySpan<byte> data, ref int nibble)
    {
        var byteIndex = nibble >> 1;
        if (byteIndex >= data.Length) throw new ArchiveException("Truncated Huffman table data.");
        var v = (nibble & 1) == 0 ? data[byteIndex] >> 4 : data[byteIndex] & 0x0F;
        nibble++;
        return v;
    }

    private void DecodeSymbols(ref Bits bits, int blockSize, int bitSize, IOutputSink sink)
    {
        var half = _windowLimit >> 1;

        while (true)
        {
            if (_write - _lastWrite >= half) Flush(sink, final: false);

            if (bits.In > blockSize - 1 || (bits.In == blockSize - 1 && bits.Bit >= bitSize)) return;

            var num = _ld.Decode(ref bits);

            if (num < 256)
            {
                if ((ulong)(_write - _fileBase) >= _fileSize) throw new ArchiveException("Decompressed data exceeds the declared size.");
                EnsureWindow(_write + 1);
                _window[(int)(_write & _mask)] = (byte)num;
                _write++;
            }
            else if (num >= 262)
            {
                var len = DecodeLength(ref bits, num - 262);
                var slot = _dd.Decode(ref bits);
                var dist = DecodeDistance(ref bits, slot);

                if (dist > 0x100)
                {
                    len++;
                    if (dist > 0x2000)
                    {
                        len++;
                        if (dist > 0x40000) len++;
                    }
                }

                PushDistance(dist);
                _lastLen = len;
                CopyString(len, dist);
            }
            else if (num == 256)
            {
                ParseFilter(ref bits);
            }
            else if (num == 257)
            {
                if (_lastLen != 0) CopyString(_lastLen, _dist[0]);
            }
            else
            {
                var idx = num - 258;
                var dist = _dist[idx];
                for (var i = idx; i > 0; i--) _dist[i] = _dist[i - 1];
                _dist[0] = dist;

                var len = DecodeLength(ref bits, _rd.Decode(ref bits));
                _lastLen = len;
                CopyString(len, dist);
            }
        }
    }

    private static int DecodeLength(ref Bits bits, int code)
    {
        var length = 2;
        int lbits;
        if (code < 8)
        {
            lbits = 0;
            length += code;
        }
        else
        {
            lbits = (code / 4) - 1;
            length += (4 | (code & 3)) << lbits;
        }

        if (lbits > 0) length += bits.ReadBits(lbits);
        return length;
    }

    private long DecodeDistance(ref Bits bits, int slot)
    {
        long dist = 1;
        int dbits;
        if (slot < 4)
        {
            dbits = 0;
            dist += slot;
        }
        else
        {
            dbits = (slot / 2) - 1;
            dist += (long)(2 | (slot & 1)) << dbits;
        }

        if (dbits > 0)
        {
            if (dbits >= 4)
            {
                if (dbits > 4)
                {
                    var add = bits.Peek32();
                    bits.Skip(dbits - 4);
                    dist += (long)((add >> (36 - dbits)) << 4);
                }

                dist += _ldd.Decode(ref bits);      // the low four bits come from their own Huffman table
            }
            else
            {
                dist += bits.ReadBits(dbits);
            }
        }

        return dist;
    }

    private void PushDistance(long dist)
    {
        _dist[3] = _dist[2];
        _dist[2] = _dist[1];
        _dist[1] = _dist[0];
        _dist[0] = dist;
    }

    // ---------------------------------------------------------------- window

    private void EnsureWindow(long needTotal)
    {
        if (needTotal <= _window.Length) return;
        if (_window.Length >= _windowLimit) return;       // full size: the window wraps from here on

        var size = (long)_window.Length;
        while (size < needTotal && size < _windowLimit) size <<= 1;
        size = Math.Min(size, _windowLimit);

        var bigger = new byte[size];
        _window.AsSpan().CopyTo(bigger);                   // nothing has wrapped yet, so positions are preserved
        _window = bigger;
        _mask = (int)(size - 1);
    }

    private void CopyString(long len, long dist)
    {
        if ((ulong)(_write - _fileBase) > _fileSize || (ulong)len > _fileSize - (ulong)(_write - _fileBase))
            throw new ArchiveException("Decompressed data exceeds the declared size.");
        if (dist <= 0 || dist > _dictionary || dist > _write || dist > _windowLimit)
            throw new ArchiveException("Invalid match distance.");

        EnsureWindow(_write + len);
        var window = _window;
        var mask = _mask;
        var w = _write;
        for (long i = 0; i < len; i++)
        {
            window[(int)((w + i) & mask)] = window[(int)((w + i - dist) & mask)];
        }

        _write += len;
    }

    // ---------------------------------------------------------------- output + filters

    private void Flush(IOutputSink sink, bool final)
    {
        while (true)
        {
            if (_filters.Count == 0)
            {
                Emit(sink, _lastWrite, _write);
                _lastWrite = _write;
                return;
            }

            var f = _filters.Peek();
            if (_write >= f.StartAbs + f.Length)
            {
                if (_lastWrite < f.StartAbs)
                {
                    Emit(sink, _lastWrite, f.StartAbs);
                    _lastWrite = f.StartAbs;
                }

                RunFilter(f, sink);
                _filters.Dequeue();
                _lastWrite = f.StartAbs + f.Length;
                continue;
            }

            // Filter range not fully decoded yet: everything before it can go out now.
            var upTo = Math.Min(f.StartAbs, _write);
            if (upTo > _lastWrite)
            {
                Emit(sink, _lastWrite, upTo);
                _lastWrite = upTo;
            }

            if (final) throw new ArchiveException("A RAR filter extends beyond the end of the data.");
            return;
        }
    }

    private void Emit(IOutputSink sink, long from, long to)
    {
        if (to <= from) return;
        var start = (int)(from & _mask);
        var length = to - from;
        if (start + length <= _window.Length)
        {
            sink.Write(_window.AsSpan(start, (int)length));
            return;
        }

        var first = _window.Length - start;
        sink.Write(_window.AsSpan(start, first));
        sink.Write(_window.AsSpan(0, (int)(length - first)));
    }

    private void RunFilter(Filter f, IOutputSink sink)
    {
        if (_filterBuffer.Length < f.Length)
        {
            _filterBuffer = new byte[f.Length];
            _filterSource = new byte[f.Length];
        }

        CopyFromWindow(f.StartAbs, f.Length, _filterSource);
        var src = _filterSource.AsSpan(0, f.Length);
        var dst = _filterBuffer.AsSpan(0, f.Length);
        if (_stats is not null) _stats.Filters++;

        switch (f.Type)
        {
            case 0:
                if (_stats is not null) _stats.Delta++;
                RarFilters.Delta(src, f.Channels, dst);
                break;
            case 1:
            case 2:
                if (_stats is not null) { if (f.Type == 1) _stats.E8++; else _stats.E8E9++; }
                RarFilters.E8(src, f.StartRel, extended: f.Type == 2, dst);
                break;
            default:
                if (_stats is not null) _stats.Arm++;
                RarFilters.Arm(src, f.StartRel, dst);
                break;
        }

        sink.Write(dst);
    }

    private void CopyFromWindow(long startAbs, int length, byte[] dst)
    {
        var start = (int)(startAbs & _mask);
        if (start + length <= _window.Length)
        {
            _window.AsSpan(start, length).CopyTo(dst);
            return;
        }

        var first = _window.Length - start;
        _window.AsSpan(start, first).CopyTo(dst);
        _window.AsSpan(0, length - first).CopyTo(dst.AsSpan(first));
    }

    private void ParseFilter(ref Bits bits)
    {
        var start = ReadFilterValue(ref bits);
        var length = ReadFilterValue(ref bits);
        var type = bits.Peek16() >> 13;
        bits.Skip(3);
        if (type > 3) throw new ArchiveException($"Unsupported RAR filter type {type}.", ArchiveError.Unsupported);

        var startRel = (_write - _fileBase) + start;
        var validStart = _lastFilterStart == 0 || _lastFilterLength == 0 || startRel >= _lastFilterStart + _lastFilterLength;
        if (length < 4 || length > MaxFilterLength || !validStart || length > (_dictionary >> 1))
            throw new ArchiveException("Invalid RAR filter.");
        if (_filters.Count >= MaxPendingFilters) throw new ArchiveException("Too many pending RAR filters.");

        var channels = 0;
        if (type == 0) channels = bits.ReadBits(5) + 1;

        _filters.Enqueue(new Filter(type, _write + start, startRel, (int)length, channels));
        _lastFilterStart = startRel;
        _lastFilterLength = length;
    }

    private static long ReadFilterValue(ref Bits bits)
    {
        var bytes = bits.ReadBits(2) + 1;
        long value = 0;
        for (var i = 0; i < bytes; i++)
        {
            value += (long)(bits.Peek16() >> 8) << (i * 8);
            bits.Skip(8);
        }

        return value;
    }

    // ---------------------------------------------------------------- bit reader and Huffman tables

    /// <summary>MSB-first bit reader over one block. Reading at or beyond the end is a corrupt stream.</summary>
    internal ref struct Bits
    {
        internal readonly ReadOnlySpan<byte> Data;
        internal int In;
        internal int Bit;

        internal Bits(ReadOnlySpan<byte> data)
        {
            Data = data;
            In = 0;
            Bit = 0;
        }

        private readonly int At(int i) => (uint)i < (uint)Data.Length ? Data[i] : 0;

        internal readonly int Peek16()
        {
            if (In >= Data.Length) throw new ArchiveException("Premature end of RAR block data.");
            var v = (uint)((At(In) << 16) | (At(In + 1) << 8) | At(In + 2));
            return (int)((v >> (8 - Bit)) & 0xFFFF);
        }

        internal readonly uint Peek32()
        {
            if (In >= Data.Length) throw new ArchiveException("Premature end of RAR block data.");
            var v = ((uint)At(In) << 24) | ((uint)At(In + 1) << 16) | ((uint)At(In + 2) << 8) | (uint)At(In + 3);
            return (v << Bit) | (uint)(At(In + 4) >> (8 - Bit));
        }

        internal void Skip(int n)
        {
            var nb = Bit + n;
            In += nb >> 3;
            Bit = nb & 7;
        }

        internal int ReadBits(int n)
        {
            var v = Peek16() >> (16 - n);
            Skip(n);
            return v;
        }
    }

    internal sealed class HuffTable
    {
        private readonly int _size;
        private readonly int _quickBits;
        private readonly int[] _decodeLen = new int[16];
        private readonly int[] _decodePos = new int[16];
        private readonly ushort[] _decodeNum;
        private readonly byte[] _quickLen = new byte[1 << 10];
        private readonly ushort[] _quickNum = new ushort[1 << 10];

        internal HuffTable(int size)
        {
            _size = size;
            _quickBits = size == HuffNc ? 10 : 7;
            _decodeNum = new ushort[size];
        }

        /// <summary>Builds the decode tables from 4-bit code lengths; false if the lengths over-subscribe the code space.</summary>
        internal bool Build(ReadOnlySpan<byte> lengths)
        {
            Span<int> lc = stackalloc int[16];
            lc.Clear();
            Array.Clear(_decodeNum);

            for (var i = 0; i < _size; i++) lc[lengths[i] & 15]++;

            lc[0] = 0;
            _decodePos[0] = 0;
            _decodeLen[0] = 0;
            var upper = 0;
            for (var i = 1; i < 16; i++)
            {
                upper += lc[i];
                _decodeLen[i] = upper << (16 - i);
                _decodePos[i] = _decodePos[i - 1] + lc[i - 1];
                upper <<= 1;
            }

            if (upper > 65536) return false;

            Span<int> clone = stackalloc int[16];
            _decodePos.AsSpan().CopyTo(clone);
            for (var i = 0; i < _size; i++)
            {
                var clen = lengths[i] & 15;
                if (clen > 0) _decodeNum[clone[clen]++] = (ushort)i;
            }

            var quickSize = 1 << _quickBits;
            var curLen = 1;
            for (var code = 0; code < quickSize; code++)
            {
                var bitField = code << (16 - _quickBits);
                while (curLen < 16 && bitField >= _decodeLen[curLen]) curLen++;

                _quickLen[code] = (byte)curLen;
                var dist = (bitField - _decodeLen[curLen - 1]) >> (16 - curLen);
                var pos = _decodePos[curLen & 15] + dist;
                _quickNum[code] = curLen < 16 && pos < _size ? _decodeNum[pos] : (ushort)0;
            }

            return true;
        }

        internal int Decode(ref Bits bits)
        {
            var bitField = bits.Peek16() & 0xFFFE;

            if (bitField < _decodeLen[_quickBits])
            {
                var code = bitField >> (16 - _quickBits);
                bits.Skip(_quickLen[code]);
                return _quickNum[code];
            }

            var n = 15;
            for (var i = _quickBits + 1; i < 15; i++)
            {
                if (bitField < _decodeLen[i])
                {
                    n = i;
                    break;
                }
            }

            bits.Skip(n);
            var dist = (bitField - _decodeLen[n - 1]) >> (16 - n);
            var pos = _decodePos[n] + dist;
            if (pos >= _size) pos = 0;
            return _decodeNum[pos];
        }
    }
}
