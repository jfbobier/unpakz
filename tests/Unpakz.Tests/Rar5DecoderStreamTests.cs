using Unpakz.Core;
using Unpakz.Core.Rar;
using Unpakz.Tests.Support;

namespace Unpakz.Tests;

/// <summary>Decoder semantics and hostile bitstreams, driven by a test-side encoder (no dependency on the rar binary).</summary>
public class Rar5DecoderStreamTests
{
    private const long Dict4M = 1L << 22;

    private static Rar5Decoder NewDecoder(long dict = Dict4M, long window = Dict4M) => new(dict, window);

    private static byte[] Decode(Rar5Decoder d, byte[] stream, long size, bool solid = false)
    {
        var sink = new MemorySink();
        d.Decode(new PackedStream(stream), (ulong)size, solid, sink);
        return sink.ToArray();
    }

    private static byte[] Rnd(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    private static ArchiveException Fails(byte[] stream, long size, Rar5Decoder? d = null, bool solid = false) =>
        Assert.Throws<ArchiveException>(() => Decode(d ?? NewDecoder(), stream, size, solid));

    // ---- positive: semantics ----
    [Fact]
    public void Literals_only()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("hello"u8).Build();
        Assert.Equal("hello"u8.ToArray(), Decode(NewDecoder(), block, 5));
    }

    [Fact]
    public void Empty_stream_decodes_to_an_empty_file_only()
    {
        Assert.Empty(Decode(NewDecoder(), [], 0));
        Fails([], 1);
    }

    [Fact]
    public void Match_copies_from_history_including_overlapping_runs()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("abc"u8).Match(9, 3).Literal((byte)'x').Match(100, 1).Build();
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), block, m.History.Count));
        Assert.Equal("abcabcabcabc", System.Text.Encoding.ASCII.GetString(m.FileOutput(), 0, 12));
    }

    [Fact]
    public void Every_representable_match_length_round_trips()
    {
        var m = new RarModel();
        var b = new RarBlockBuilder(m).Literals(Rnd(5000, 3));
        for (var len = 2; len <= 4097; len += len < 300 ? 1 : 37) b.Match(len, 1 + (len % 4000));
        b.Match(4097, 4999);
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), b.Build(), m.History.Count));
    }

    [Fact]
    public void Every_distance_slot_boundary_round_trips()
    {
        var m = new RarModel();
        var b = new RarBlockBuilder(m).Literals(Rnd(70_000, 4));
        var dists = new List<long>();
        for (var shift = 0; shift < 16; shift++)
            foreach (var delta in new[] { -1L, 0, 1, 2, 3 })
            {
                var d = (1L << shift) + delta;
                if (d >= 1 && d <= 69_000) dists.Add(d);
            }

        foreach (var d in dists.Distinct()) b.Match(7, d);
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), b.Build(), m.History.Count));
    }

    [Fact]
    public void Very_long_distances_and_their_length_bonus_round_trip()
    {
        var m = new RarModel();
        var b = new RarBlockBuilder(m).Literals(Rnd(1_300_000, 5));
        foreach (var d in new long[] { 0x100, 0x101, 0x2000, 0x2001, 0x40000, 0x40001, 1_000_000, 1_299_999 }) b.Match(20, d);
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), b.Build(), m.History.Count));
    }

    [Fact]
    public void Repeat_codes_follow_the_distance_cache_rules()
    {
        var m = new RarModel();
        var b = new RarBlockBuilder(m).Literals(Rnd(500, 6));
        b.Match(5, 10).Match(6, 20).Match(7, 30).Match(8, 40);          // cache: 40 30 20 10
        b.RepeatLast();                                                   // 8 bytes at distance 40
        b.RepeatDistance(2, 9);                                           // distance 20 moves to the front: 20 40 30 10
        b.RepeatDistance(3, 11);                                          // distance 10 -> 10 20 40 30
        b.RepeatDistance(0, 4);
        b.RepeatLast();
        b.RepeatDistance(1, 3);
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), b.Build(), m.History.Count));
    }

    [Fact]
    public void Repeat_last_with_no_previous_match_does_nothing()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("ab"u8).RepeatLast().Literal((byte)'c').Build();
        Assert.Equal("abc"u8.ToArray(), Decode(NewDecoder(), block, 3));
    }

    [Fact]
    public void Random_token_streams_match_the_model()
    {
        for (var seed = 0; seed < 150; seed++)
        {
            var rng = new Random(seed);
            var m = new RarModel();
            var blocks = new List<byte[]>();
            var blockCount = rng.Next(1, 4);
            for (var bi = 0; bi < blockCount; bi++)
            {
                var b = new RarBlockBuilder(m, writeTables: bi == 0);
                var tokens = rng.Next(1, 400);
                for (var t = 0; t < tokens; t++)
                {
                    var r = rng.Next(100);
                    var have = m.History.Count;
                    if (r < 45 || have < 4) b.Literal((byte)rng.Next(256));
                    else if (r < 75) b.Match(rng.Next(5, 300), rng.Next(1, Math.Min(have, 70_000) + 1));
                    else if (r < 82) b.RepeatLast();
                    else
                    {
                        var idx = rng.Next(4);
                        if (m.Dist[idx] >= 1 && m.Dist[idx] <= have) b.RepeatDistance(idx, rng.Next(2, 60));
                        else b.Literal((byte)rng.Next(256));
                    }
                }

                blocks.Add(b.Build(last: bi == blockCount - 1));
            }

            var stream = blocks.SelectMany(x => x).ToArray();
            Assert.True(m.FileOutput().AsSpan().SequenceEqual(Decode(NewDecoder(), stream, m.History.Count)), $"seed {seed}");
        }
    }

    [Fact]
    public void Second_block_without_tables_reuses_the_previous_tables_and_last_flag_is_optional_when_data_is_used_up()
    {
        var m = new RarModel();
        var b1 = new RarBlockBuilder(m).Literals("one "u8).Build(last: false);
        var b2 = new RarBlockBuilder(m, writeTables: false).Literals("two"u8).Build(last: false);   // no last flag anywhere
        Assert.Equal("one two"u8.ToArray(), Decode(NewDecoder(), [.. b1, .. b2], 7));
    }

    [Fact]
    public void Window_wraps_correctly_when_data_far_exceeds_it()
    {
        var m = new RarModel();
        var rng = new Random(9);
        var b = new RarBlockBuilder(m).Literals(Rnd(1000, 1));
        while (m.History.Count < 900_000)
        {
            if (rng.Next(3) == 0) b.Literals(Rnd(rng.Next(1, 200), rng.Next()));
            else b.Match(rng.Next(5, 500), rng.Next(1, Math.Min(m.History.Count, 100_000) + 1));
        }

        var d = NewDecoder(dict: 1 << 17, window: 1 << 17);
        Assert.True(m.FileOutput().AsSpan().SequenceEqual(Decode(d, b.Build(), m.History.Count)));
    }

    [Fact]
    public void Window_grows_from_its_initial_size_without_losing_history()
    {
        var m = new RarModel();
        var b = new RarBlockBuilder(m).Literals(Rnd(2_600_000, 2));
        b.Match(1000, 2_500_000).Match(3000, 1_048_577).Match(500, 2_599_999);
        Assert.True(m.FileOutput().AsSpan().SequenceEqual(Decode(NewDecoder(1L << 24, 1L << 23), b.Build(), m.History.Count)));
    }

    [Fact]
    public void Solid_files_reference_earlier_files_and_non_solid_files_cannot()
    {
        var m = new RarModel();
        var first = new RarBlockBuilder(m).Literals(Rnd(2000, 7)).Build();
        var firstOut = m.FileOutput();

        m.StartNewFile(solid: true);
        var second = new RarBlockBuilder(m, writeTables: true).Match(500, 1500).Literals("end"u8).Build();
        var secondOut = m.FileOutput();

        var d = NewDecoder();
        Assert.Equal(firstOut, Decode(d, first, firstOut.Length));
        Assert.Equal(secondOut, Decode(d, second, secondOut.Length, solid: true));

        var fresh = NewDecoder();
        Decode(fresh, first, firstOut.Length);
        Fails(second, secondOut.Length, fresh, solid: false);              // history was dropped: distance outside written data
    }

    // ---- positive: filters (expected values computed by hand) ----
    [Fact]
    public void Delta_filter_over_literal_data()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Filter(0, 0, 6, channels: 2).Literals([1, 2, 3, 4, 5, 6]).Build();
        Assert.Equal([255, 252, 253, 247, 250, 241], Decode(NewDecoder(), block, 6));
    }

    [Fact]
    public void E8_filter_with_raw_bytes_before_and_after_uses_file_relative_position()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m)
            .Literals([1, 2, 3])                                    // raw prefix, write position 3
            .Filter(1, 2, 7)                                        // block starts at 3 + 2 = 5
            .Literals([9, 9, 0xE8, 100, 0, 0, 0, 0x90, 0x90, 0, 0, 0, 7, 7])   // raw 2, filtered 7, raw tail
            .Build();
        // E8 at filtered index 0 -> i becomes 1; offset = 1 + 5 = 6; 100 - 6 = 94
        byte[] expected = [1, 2, 3, 9, 9, 0xE8, 94, 0, 0, 0, 0x90, 0x90, 0, 0, 0, 7, 7];
        _ = expected;
        var output = Decode(NewDecoder(), block, 17 - 0);
        Assert.Equal(expected[..5], output[..5]);
        Assert.Equal(expected[5..12], output[5..12]);
    }

    [Fact]
    public void Arm_filter_subtracts_the_instruction_index()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals(Rnd(8, 1)).Filter(3, 0, 4).Literals([0x10, 0, 0, 0xEB]).Build();
        var output = Decode(NewDecoder(), block, 12);
        Assert.Equal([0x0E, 0, 0, 0xEB], output[8..]);                 // 0x10 - (8 / 4)
    }

    [Fact]
    public void Back_to_back_filters_apply_in_order()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Filter(0, 0, 4, 1).Filter(0, 4, 4, 1).Literals([1, 1, 1, 1, 2, 2, 2, 2]).Build();
        Assert.Equal([255, 254, 253, 252, 254, 252, 250, 248], Decode(NewDecoder(), block, 8));
    }

    [Fact]
    public void Filters_work_while_the_window_is_flushing_and_wrapping()
    {
        var m = new RarModel();
        var head = Rnd(100_000, 11);
        var middle = Rnd(50_000, 12);
        var tail = Rnd(100_000, 13);
        var block = new RarBlockBuilder(m).Literals(head).Filter(0, 0, 50_000, 1).Literals(middle).Literals(tail).Build();

        var expected = new byte[250_000];
        head.CopyTo(expected, 0);
        byte prev = 0;
        for (var i = 0; i < middle.Length; i++) { prev -= middle[i]; expected[100_000 + i] = prev; }
        tail.CopyTo(expected, 150_000);

        Assert.Equal(expected, Decode(NewDecoder(1 << 17, 1 << 17), block, 250_000));
    }

    [Fact]
    public void Table_nibble_escapes_and_odd_nibble_alignment_are_handled()
    {
        // BC lengths: symbols 0..3 absent (zero-run escape), 4..18 length 4, symbol 19 length 15 (literal-15 escape).
        // 19 nibbles = odd, so the table symbols start in the middle of a byte.
        var bc = new int[20];
        for (var i = 4; i <= 18; i++) bc[i] = 4;
        bc[19] = 15;
        var enc = new RarHuffmanEncoder(bc);
        var m = new RarModel();
        var builder = new RarBlockBuilder(m, rawTables: w =>
        {
            w.Write(15, 4); w.Write(2, 4);                                  // 4 zeros
            for (var i = 4; i <= 18; i++) w.Write(4, 4);
            w.Write(15, 4); w.Write(0, 4);                                  // a literal 15
            foreach (var l in Enumerable.Repeat(9, 306).Concat(Enumerable.Repeat(6, 64)).Concat(Enumerable.Repeat(4, 16)).Concat(Enumerable.Repeat(6, 44)))
                enc.Write(w, l);
        });
        builder.Literals("ok!"u8);
        Assert.Equal("ok!"u8.ToArray(), Decode(NewDecoder(), builder.Build(), 3));
    }

    [Fact]
    public void Table_repeat_and_zero_run_codes_expand_correctly()
    {
        // Use BC symbols 17 (repeat previous 11+n) and 18/19 (zero runs) to express the 430 lengths compactly.
        var bc = Enumerable.Repeat(0, 20).ToArray();
        bc[9] = 2; bc[6] = 2; bc[4] = 3; bc[17] = 3; bc[0] = 3;
        var enc = new RarHuffmanEncoder(bc);
        var m = new RarModel();
        var builder = new RarBlockBuilder(m, rawTables: w =>
        {
            for (var i = 0; i < 20; i++) w.Write(bc[i], 4);
            // LD: 306 x length 9 = one explicit 9, then 295 repeats via symbol 17 (11 + n, n <= 7 => up to 18 each)
            enc.Write(w, 9);
            var left = 305;
            while (left > 0)
            {
                var run = Math.Min(left, 11 + 127 / 128 * 0 + 7 + 0);
                run = Math.Min(left, 18);
                if (run < 11) { for (var k = 0; k < run; k++) enc.Write(w, 9); left -= run; continue; }
                enc.Write(w, 17); w.Write(run - 11, 7); left -= run;
            }

            foreach (var _ in Enumerable.Range(0, 64)) enc.Write(w, 6);
            foreach (var _ in Enumerable.Range(0, 16)) enc.Write(w, 4);
            foreach (var _ in Enumerable.Range(0, 44)) enc.Write(w, 6);
        });
        builder.Literals("rep"u8);
        Assert.Equal("rep"u8.ToArray(), Decode(NewDecoder(), builder.Build(), 3));
    }

    // ---- hostile: structure ----
    [Fact]
    public void Wrong_block_checksum_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Literals("x"u8).Build(checksumXor: 1);
        Fails(block, 1);
    }

    [Fact]
    public void Block_size_beyond_available_data_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Literals("x"u8).Build(sizeOverride: 5000);
        Fails(block, 1);
    }

    [Fact]
    public void Four_byte_block_size_field_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Literals("x"u8).Build(sizeFieldBytes: 4);
        Fails(block, 1);
    }

    [Fact]
    public void Truncated_block_header_is_rejected()
    {
        Fails([0x80], 1);
        Fails([0x80, 0x00], 1);
    }

    [Fact]
    public void First_block_without_tables_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel(), writeTables: false).Literals("x"u8).Build();
        Fails(block, 1);
    }

    [Fact]
    public void Block_without_tables_after_a_failed_file_in_the_same_decoder_is_rejected()
    {
        var d = NewDecoder();
        var ok = new RarBlockBuilder(new RarModel()).Literals("x"u8).Build();
        Decode(d, ok, 1);
        var noTables = new RarBlockBuilder(new RarModel(), writeTables: false).Literals("y"u8).Build();
        Fails(noTables, 1, d, solid: false);          // a non-solid file resets the tables
    }

    [Fact]
    public void Zero_length_block_with_tables_flag_is_truncated_tables()
    {
        byte[] block = [0x80 | 0x40, (byte)(0x5A ^ (0x80 | 0x40)), 0];
        Fails(block, 1);
    }

    [Fact]
    public void Oversubscribed_tables_are_rejected()
    {
        Fails(new RarBlockBuilder(new RarModel(), ld: Enumerable.Repeat(1, 306).ToArray()).Literals("x"u8).Build(), 1);
        Fails(new RarBlockBuilder(new RarModel(), dd: Enumerable.Repeat(2, 64).ToArray()).Literals("x"u8).Build(), 1);
        Fails(new RarBlockBuilder(new RarModel(), ldd: Enumerable.Repeat(1, 16).ToArray()).Literals("x"u8).Build(), 1);
        Fails(new RarBlockBuilder(new RarModel(), rd: Enumerable.Repeat(3, 44).ToArray()).Literals("x"u8).Build(), 1);

        var badBc = new RarBlockBuilder(new RarModel(), rawTables: w => { for (var i = 0; i < 20; i++) w.Write(1, 4); w.Write(0, 16); });
        Fails(badBc.Build(), 1);
    }

    [Fact]
    public void Repeat_previous_length_at_the_start_of_the_table_is_rejected()
    {
        var b = new RarBlockBuilder(new RarModel(), rawTables: w =>
        {
            for (var i = 0; i < 20; i++) w.Write(5, 4);
            w.Write(16, 5); w.Write(0, 3);
            w.Write(0, 64);
        });
        Fails(b.Build(), 1);
    }

    [Fact]
    public void Truncated_tables_are_rejected()
    {
        Fails(new RarBlockBuilder(new RarModel(), rawTables: w => { for (var i = 0; i < 5; i++) w.Write(5, 4); }).Build(), 1);
        Fails(new RarBlockBuilder(new RarModel(), rawTables: w => { for (var i = 0; i < 20; i++) w.Write(5, 4); for (var i = 0; i < 10; i++) w.Write(9, 5); }).Build(), 1);
    }

    [Fact]
    public void Last_byte_valid_bit_count_is_honoured_in_both_directions()
    {
        // Literal 0 costs one bit. The tables take 2230 bits, so twelve literals end 2 bits into a byte: 2 valid bits in the last byte.
        int[] ld = [1, .. Enumerable.Repeat(9, 255), .. new int[50]];
        byte[] Stream(int? valid) => new RarBlockBuilder(new RarModel(), ld: ld).Literals(new byte[12]).Build(validBitsOverride: valid);

        Assert.Equal(new byte[12], Decode(NewDecoder(), Stream(null), 12));
        Fails(Stream(8), 12);          // padding bits are decoded as six more literals: too long
        Fails(Stream(1), 12);          // the twelfth literal begins at the claimed end: too short
    }

    [Fact]
    public void Symbol_cut_off_mid_way_is_rejected()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("abcd"u8).Match(10, 3).Build(cutDataBytes: 2);
        Fails(block, 14);
    }

    // ---- hostile: values ----
    [Fact]
    public void Distance_before_the_start_of_data_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Literal((byte)'a').Match(5, 2, trackModel: false).Build();
        Fails(block, 6);
    }

    [Fact]
    public void Distance_beyond_the_declared_dictionary_is_rejected()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals(Rnd(300_000, 3)).Match(5, 150_000, trackModel: false).Build();
        Fails(block, 300_005, NewDecoder(1 << 17, 1 << 17));
    }

    [Fact]
    public void Output_longer_than_declared_is_rejected_for_literals_and_matches()
    {
        Fails(new RarBlockBuilder(new RarModel()).Literals("abcde"u8).Build(), 3);
        Fails(new RarBlockBuilder(new RarModel()).Literals("abc"u8).Match(100, 1, trackModel: false).Build(), 20);
    }

    [Fact]
    public void A_match_that_ends_exactly_at_the_declared_size_is_accepted()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("abc"u8).Match(17, 3).Build();
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), block, 20));
    }

    [Fact]
    public void Output_shorter_than_declared_is_rejected()
    {
        Fails(new RarBlockBuilder(new RarModel()).Literals("abc"u8).Build(), 10);
    }

    [Fact]
    public void Last_flag_missing_and_data_used_up_still_checks_the_size()
    {
        Fails(new RarBlockBuilder(new RarModel()).Literals("abc"u8).Build(last: false), 10);
    }

    // ---- hostile: filters ----
    [Theory]
    [InlineData(0, 4, 3)]       // length below 4
    [InlineData(0, 4, 0x400001)] // length above the 4 MiB cap
    public void Invalid_filter_lengths_are_rejected(int type, int _, int length)
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(type, 0, length).Literals([1, 2, 3, 4]).Build();
        Fails(block, 4, NewDecoder(1L << 30, Dict4M));
    }

    [Fact]
    public void Filter_longer_than_half_the_dictionary_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(1, 0, 70_000).Literals([1, 2, 3, 4]).Build();
        Fails(block, 4, NewDecoder(1 << 17, 1 << 17));
        var ok = new RarBlockBuilder(new RarModel()).Filter(1, 0, 65_536).Literals(new byte[65_536]).Build();
        Decode(NewDecoder(1 << 17, 1 << 17), ok, 65_536);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void Unknown_filter_types_are_unsupported(int type)
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(type, 0, 8).Literals(new byte[8]).Build();
        var ex = Fails(block, 8);
        Assert.Equal(ArchiveError.Unsupported, ex.Error);
    }

    [Fact]
    public void Overlapping_filters_are_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(1, 100, 50).Filter(1, 120, 50).Literals(new byte[300]).Build();
        Fails(block, 300);
        var ok = new RarBlockBuilder(new RarModel()).Filter(1, 100, 50).Filter(1, 150, 50).Literals(new byte[300]).Build();
        Decode(NewDecoder(), ok, 300);
    }

    [Fact]
    public void Too_many_pending_filters_are_rejected()
    {
        var b = new RarBlockBuilder(new RarModel());
        for (var i = 0; i < 1030; i++) b.Filter(1, 1000 + (4 * i), 4);
        Fails(b.Literals(new byte[10]).Build(), 10);
    }

    [Fact]
    public void Filter_that_extends_beyond_the_data_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(1, 0, 100).Literals(new byte[10]).Build();
        Fails(block, 10);
        var ok = new RarBlockBuilder(new RarModel()).Filter(1, 0, 10).Literals(new byte[10]).Build();
        Decode(NewDecoder(), ok, 10);
    }

    // ---- solid state handling ----
    [Fact]
    public void Solid_continuation_keeps_the_distance_cache_and_non_solid_resets_it()
    {
        var m = new RarModel();
        var first = new RarBlockBuilder(m).Literals(Rnd(100, 1)).Match(8, 50).Build();
        var firstOut = m.FileOutput();
        m.StartNewFile(solid: true);
        var second = new RarBlockBuilder(m, writeTables: true).RepeatLast().Build();       // repeats length 8 at distance 50 across files
        var secondOut = m.FileOutput();

        var d = NewDecoder();
        Decode(d, first, firstOut.Length);
        Assert.Equal(secondOut, Decode(d, second, secondOut.Length, solid: true));

        var d2 = NewDecoder();
        Decode(d2, first, firstOut.Length);
        Fails(second, 8, d2, solid: false);                // cache and last length were reset: nothing to repeat, size mismatch
    }

    // ---- boundaries ----
    [Fact]
    public void Distance_exactly_at_the_dictionary_or_written_size_is_allowed_one_more_is_not()
    {
        // history == 100 bytes: distance 100 is the very first byte, 101 is before the start
        var m = new RarModel();
        var ok = new RarBlockBuilder(m).Literals(Rnd(100, 2)).Match(10, 100).Build();
        Assert.Equal(m.FileOutput(), Decode(NewDecoder(), ok, 110));
        Fails(new RarBlockBuilder(new RarModel()).Literals(Rnd(100, 2)).Match(10, 101, trackModel: false).Build(), 110);

        // dictionary of 128 KiB: distance 131072 allowed, 131073 not
        var m2 = new RarModel();
        var okDict = new RarBlockBuilder(m2).Literals(Rnd(140_000, 3)).Match(10, 131_072).Build();
        Assert.True(m2.FileOutput().AsSpan().SequenceEqual(Decode(NewDecoder(1 << 17, 1 << 17), okDict, 140_010)));
        Fails(new RarBlockBuilder(new RarModel()).Literals(Rnd(140_000, 3)).Match(10, 131_073, trackModel: false).Build(), 140_010, NewDecoder(1 << 17, 1 << 17));
    }

    [Fact]
    public void Distance_beyond_the_window_but_inside_the_dictionary_is_rejected()
    {
        var block = new RarBlockBuilder(new RarModel()).Literals(Rnd(300_000, 3)).Match(10, 200_000, trackModel: false).Build();
        Fails(block, 300_010, NewDecoder(dict: 1 << 22, window: 1 << 17));
    }

    [Fact]
    public void Table_runs_that_overshoot_the_end_are_clamped_not_overflowed()
    {
        // All BC symbols use 5 bits. The last table entries come from one repeat (17) and one zero-run (19) code that are longer than what is left.
        var builder = new RarBlockBuilder(new RarModel(), rawTables: w =>
        {
            for (var i = 0; i < 20; i++) w.Write(5, 4);
            void Run(int symbol, int extra, int bits) { w.Write(symbol, 5); w.Write(extra, bits); }

            foreach (var _ in Enumerable.Range(0, 306)) w.Write(9, 5);      // LD
            foreach (var _ in Enumerable.Range(0, 64)) w.Write(6, 5);       // DD
            foreach (var _ in Enumerable.Range(0, 16)) w.Write(4, 5);       // LDD
            w.Write(6, 5);                                                  // RD: one explicit length ...
            Run(17, 100, 7);                                                // ... then "repeat 111 times": only 43 are left
        });
        builder.Literals("clamped"u8);
        Assert.Equal("clamped"u8.ToArray(), Decode(NewDecoder(), builder.Build(), 7));

        var zeros = new RarBlockBuilder(new RarModel(), rawTables: w =>
        {
            for (var i = 0; i < 20; i++) w.Write(5, 4);
            foreach (var _ in Enumerable.Range(0, 306)) w.Write(9, 5);
            foreach (var _ in Enumerable.Range(0, 64)) w.Write(6, 5);
            foreach (var _ in Enumerable.Range(0, 16)) w.Write(4, 5);
            w.Write(19, 5); w.Write(127, 7);                                 // zero run of 138 over 44 remaining
        });
        zeros.Literals("zeroed"u8);
        Assert.Equal("zeroed"u8.ToArray(), Decode(NewDecoder(), zeros.Build(), 6));
    }

    [Fact]
    public void Incomplete_code_with_unassigned_bit_patterns_fails_typed_never_with_an_index_error()
    {
        // Only literal 0 has a code (1 bit, "0"); every "1" bit is an unassigned pattern, which decodes as symbol 0 (as in the reference).
        int[] ld = [1, .. new int[305]];
        var b = new RarBlockBuilder(new RarModel(), ld: ld);
        b.Literals(new byte[8]);                                            // 8 zero bits
        b.Raw.Write(0xFF, 8);                                               // then bits that match no code
        var stream = b.Build();

        Assert.Equal(new byte[9], Decode(NewDecoder(), stream, 9));        // the unassigned pattern became one more literal 0
        Fails(stream, 20);                                                  // the stream cannot fill 20 bytes: typed size error, no index error
    }

    [Fact]
    public void Exactly_1024_pending_filters_are_accepted_and_1025_are_rejected()
    {
        byte[] Stream(int count)
        {
            var b = new RarBlockBuilder(new RarModel());
            for (var i = 0; i < count; i++) b.Filter(1, 1000 + (4 * i), 4);
            return b.Literals(new byte[1000 + (4 * count)]).Build();
        }

        Decode(NewDecoder(), Stream(1024), 1000 + (4 * 1024));
        Fails(Stream(1025), 1000 + (4 * 1025));
    }

    [Fact]
    public void E8E9_type_converts_jumps_but_E8_type_does_not()
    {
        byte[] jmp = [0xE9, 100, 0, 0, 0, 0x90, 0x90];
        var plain = new RarBlockBuilder(new RarModel()).Filter(1, 0, 7).Literals(jmp).Build();
        Assert.Equal(jmp, Decode(NewDecoder(), plain, 7));
        var extended = new RarBlockBuilder(new RarModel()).Filter(2, 0, 7).Literals(jmp).Build();
        Assert.Equal([0xE9, 99, 0, 0, 0, 0x90, 0x90], Decode(NewDecoder(), extended, 7));
    }

    [Fact]
    public void Arm_filter_ignores_a_trailing_partial_word()
    {
        var block = new RarBlockBuilder(new RarModel()).Filter(3, 0, 11).Literals([1, 2, 3, 0xEA, 0x10, 0, 0, 0xEB, 9, 9, 9]).Build();
        var output = Decode(NewDecoder(), block, 11);
        Assert.Equal([9, 9, 9], output[8..]);
        Assert.Equal(0xEB, output[7]);
    }

    [Fact]
    public void Filter_positions_are_relative_to_the_start_of_the_current_file_in_solid_mode()
    {
        var m = new RarModel();
        var first = new RarBlockBuilder(m).Literals(Rnd(1000, 4)).Build();
        m.StartNewFile(solid: true);
        var second = new RarBlockBuilder(m).Filter(1, 0, 7).Literals([0xE8, 100, 0, 0, 0, 0x90, 0x90]).Build();

        var d = NewDecoder();
        Decode(d, first, 1000);
        // file-relative start 0: offset = 1, relative target 99 (an absolute window position of 1000 would give 99 - 1000)
        Assert.Equal([0xE8, 99, 0, 0, 0, 0x90, 0x90], Decode(d, second, 7, solid: true));
    }

    [Fact]
    public void Trailing_bytes_after_the_last_block_are_ignored()
    {
        var m = new RarModel();
        var block = new RarBlockBuilder(m).Literals("data"u8).Build(last: true);
        Assert.Equal("data"u8.ToArray(), Decode(NewDecoder(), [.. block, 1, 2, 3, 4, 5], 4));
    }

    [Fact]
    public void Streams_split_across_arbitrary_segments_decode_identically()
    {
        var rng = new Random(21);
        var m = new RarModel();
        var blocks = new List<byte[]>();
        for (var bi = 0; bi < 3; bi++)
        {
            var b = new RarBlockBuilder(m, writeTables: bi == 0);
            for (var t = 0; t < 600; t++)
            {
                if (rng.Next(3) == 0 && m.History.Count > 10) b.Match(rng.Next(5, 80), rng.Next(1, Math.Min(m.History.Count, 5000) + 1));
                else b.Literal((byte)rng.Next(256));
            }

            blocks.Add(b.Build(last: bi == 2));
        }

        var stream = blocks.SelectMany(x => x).ToArray();
        for (var trial = 0; trial < 60; trial++)
        {
            var cuts = Enumerable.Range(0, rng.Next(1, 8)).Select(_ => rng.Next(0, stream.Length + 1)).Append(0).Append(stream.Length).Distinct().Order().ToArray();
            var segments = new List<ReadOnlyMemory<byte>>();
            for (var i = 0; i + 1 < cuts.Length; i++) segments.Add(stream.AsMemory(cuts[i], cuts[i + 1] - cuts[i]));

            var sink = new MemorySink();
            NewDecoder().Decode(new PackedStream(segments), (ulong)m.History.Count, solid: false, sink);
            Assert.True(m.FileOutput().AsSpan().SequenceEqual(sink.ToArray()), $"trial {trial}");
        }
    }

    [Fact]
    public void The_sink_never_receives_more_than_the_declared_size_even_when_the_stream_keeps_going()
    {
        long Received(byte[] stream, long declared)
        {
            var sink = new CountingSink();
            Assert.Throws<ArchiveException>(() => NewDecoder(1 << 17, 1 << 17).Decode(new PackedStream(stream), (ulong)declared, false, sink));
            return sink.Bytes;
        }

        // 200,000 literals against a declared size of 10: the decoder would flush 64 KiB chunks long before the final size check
        var literals = new RarBlockBuilder(new RarModel()).Literals(new byte[200_000]).Build();
        Assert.True(Received(literals, 10) <= 10);

        // one overshooting match as the very last symbol: the final flush would emit its extra bytes before the size check
        var match = new RarBlockBuilder(new RarModel()).Literals(Rnd(100, 1)).Match(4000, 50, trackModel: false);
        Assert.True(Received(match.Build(), 120) <= 120);
    }

    private sealed class CountingSink : IOutputSink
    {
        public long Bytes { get; private set; }
        public void Write(ReadOnlySpan<byte> data) => Bytes += data.Length;
    }

    [Fact]
    public void Repeating_a_cache_entry_that_was_never_set_is_rejected()
    {
        // two real matches fill cache slots 0 and 1; slot 3 is still the initial zero distance
        var block = new RarBlockBuilder(new RarModel()).Literals(Rnd(50, 3)).Match(5, 10).Match(5, 20).RepeatDistance(3, 6, trackModel: false).Build();
        Fails(block, 66);
    }

    [Fact]
    public void Filter_length_cap_is_exactly_four_mebibytes()
    {
        byte[] Stream(int length) => new RarBlockBuilder(new RarModel()).Filter(1, 0, length).Literals(new byte[length]).Build();
        Decode(NewDecoder(1L << 24, 1L << 23), Stream(0x400000), 0x400000);
        Fails(Stream(0x400001), 0x400001, NewDecoder(1L << 24, 1L << 23));
    }

    [Fact]
    public void Truncated_tables_are_rejected_even_when_no_output_is_expected()
    {
        var block = new RarBlockBuilder(new RarModel(), rawTables: w => { for (var i = 0; i < 20; i++) w.Write(5, 4); for (var i = 0; i < 10; i++) w.Write(9, 5); }).Build();
        Fails(block, 0);                                    // an empty file must not make a broken block acceptable
    }

    // ---- limits of construction ----
    [Fact]
    public void Constructor_rejects_non_positive_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rar5Decoder(0, 1 << 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rar5Decoder(1 << 20, 0));
    }

    [Theory]
    [InlineData(0, 0x20000L)]
    [InlineData(1, 0x40000L)]
    [InlineData(5, 0x400000L)]
    [InlineData(15, 0x100000000L)]
    public void Dictionary_size_formula(int log, long expected) => Assert.Equal(expected, Rar5Decoder.DictionarySize(log));
}
