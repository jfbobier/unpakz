// Decoder semantics and hostile bitstreams, driven by a test-side encoder (no dependency on the rar binary).
// Ported from the C# Rar5DecoderStreamTests and RarFilterTests.
#include <deque>

#include "../src/rar5_testing.h"
#include "rar5_stream_builder.h"
#include "support.h"

using namespace tsupport;
namespace rt = unpakz::rar5::testing;

constexpr std::uint64_t Dict4M = 1ull << 22;

// A model that outlives the expression (builders keep a reference to it); owned by a pool so nothing leaks.
static RarModel& fresh() { static std::deque<RarModel> pool; pool.emplace_back(); return pool.back(); }

static Bytes rb(std::size_t n, unsigned seed = 1) { auto s = rnd(n, seed); return Bytes(s.begin(), s.end()); }
static Bytes B(const std::string& s) { return Bytes(s.begin(), s.end()); }

static Bytes decode(rt::TestDecoder& d, const Bytes& stream, std::uint64_t size, bool solid = false) {
    Bytes out;
    d.decode({{stream.data(), stream.size()}}, size, solid, [&](const std::uint8_t* p, std::size_t n) { out.insert(out.end(), p, p + n); });
    return out;
}
static Bytes decode_new(const Bytes& stream, std::uint64_t size, std::uint64_t dict = Dict4M, std::uint64_t window = Dict4M) {
    rt::TestDecoder d(dict, window);
    return decode(d, stream, size);
}
// Status of decoding (Ok if it succeeded).
static Status fails(const Bytes& stream, std::uint64_t size, rt::TestDecoder* d = nullptr, bool solid = false, std::uint64_t dict = Dict4M, std::uint64_t window = Dict4M) {
    try {
        if (d) decode(*d, stream, size, solid);
        else { rt::TestDecoder fresh(dict, window); decode(fresh, stream, size, solid); }
        return Status::Ok;
    } catch (const Error& e) { return e.status(); }
}
static bool rejected(const Bytes& stream, std::uint64_t size, rt::TestDecoder* d = nullptr, bool solid = false, std::uint64_t dict = Dict4M, std::uint64_t window = Dict4M) {
    return fails(stream, size, d, solid, dict, window) != Status::Ok;
}

using Cfg = RarBlockBuilder::Config;
static Cfg no_tables() { Cfg c; c.write_tables = false; return c; }
static Cfg with_ld(std::vector<int> v) { Cfg c; c.ld = std::move(v); return c; }
static Cfg raw(std::function<void(RarBitWriter&)> f) { Cfg c; c.raw_tables = std::move(f); return c; }
static RarBlockBuilder::BuildOpts not_last() { RarBlockBuilder::BuildOpts o; o.last = false; return o; }

// ---------------------------------------------------------------- semantics

TEST(literals_only) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals("hello").build();
    CHECK(decode_new(block, 5) == B("hello"));
}

TEST(empty_stream_decodes_to_an_empty_file_only) {
    CHECK(decode_new({}, 0).empty());
    CHECK(rejected({}, 1));
}

TEST(match_copies_from_history_including_overlapping_runs) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals("abc").match(9, 3).literal('x').match(100, 1).build();
    auto expected = m.file_output();
    CHECK(decode_new(block, m.history.size()) == expected);
    CHECK(std::string(expected.begin(), expected.begin() + 12) == "abcabcabcabc");
}

TEST(every_representable_match_length_round_trips) {
    RarModel m;
    RarBlockBuilder b(m);
    b.literals(rb(5000, 3));
    for (int len = 2; len <= 4097; len += len < 300 ? 1 : 37) b.match(len, 1 + (len % 4000));
    b.match(4097, 4999);
    CHECK(decode_new(b.build(), m.history.size()) == m.file_output());
}

TEST(every_distance_slot_boundary_round_trips) {
    RarModel m;
    RarBlockBuilder b(m);
    b.literals(rb(70'000, 4));
    std::vector<std::int64_t> dists;
    for (int shift = 0; shift < 16; ++shift)
        for (std::int64_t delta : {-1, 0, 1, 2, 3}) { auto d = (1ll << shift) + delta; if (d >= 1 && d <= 69'000) dists.push_back(d); }
    std::sort(dists.begin(), dists.end());
    dists.erase(std::unique(dists.begin(), dists.end()), dists.end());
    for (auto d : dists) b.match(7, d);
    CHECK(decode_new(b.build(), m.history.size()) == m.file_output());
}

TEST(very_long_distances_and_their_length_bonus_round_trip) {
    RarModel m;
    RarBlockBuilder b(m);
    b.literals(rb(1'300'000, 5));
    for (std::int64_t d : {0x100, 0x101, 0x2000, 0x2001, 0x40000, 0x40001, 1'000'000, 1'299'999}) b.match(20, d);
    CHECK(decode_new(b.build(), m.history.size()) == m.file_output());
}

TEST(repeat_codes_follow_the_distance_cache_rules) {
    RarModel m;
    RarBlockBuilder b(m);
    b.literals(rb(500, 6));
    b.match(5, 10).match(6, 20).match(7, 30).match(8, 40);        // cache: 40 30 20 10
    b.repeat_last();                                                // 8 bytes at distance 40
    b.repeat_distance(2, 9);                                        // distance 20 moves to the front: 20 40 30 10
    b.repeat_distance(3, 11);                                       // distance 10 -> 10 20 40 30
    b.repeat_distance(0, 4);
    b.repeat_last();
    b.repeat_distance(1, 3);
    CHECK(decode_new(b.build(), m.history.size()) == m.file_output());
}

TEST(repeat_last_with_no_previous_match_does_nothing) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals("ab").repeat_last().literal('c').build();
    CHECK(decode_new(block, 3) == B("abc"));
}

TEST(random_token_streams_match_the_model) {
    for (int seed = 0; seed < 150; ++seed) {
        std::mt19937 rng(static_cast<unsigned>(seed));
        auto next = [&](int lo, int hi) { return lo + static_cast<int>(rng() % static_cast<unsigned>(hi - lo)); };
        RarModel m;
        Bytes stream;
        const int block_count = next(1, 4);
        for (int bi = 0; bi < block_count; ++bi) {
            Cfg c; c.write_tables = bi == 0;
            RarBlockBuilder b(m, c);
            const int tokens = next(1, 400);
            for (int t = 0; t < tokens; ++t) {
                const int r = next(0, 100);
                const int have = static_cast<int>(m.history.size());
                if (r < 45 || have < 4) b.literal(static_cast<std::uint8_t>(next(0, 256)));
                else if (r < 75) b.match(next(5, 300), next(1, std::min(have, 70'000) + 1));
                else if (r < 82) b.repeat_last();
                else {
                    const int idx = next(0, 4);
                    if (m.dist[idx] >= 1 && m.dist[idx] <= have) b.repeat_distance(idx, next(2, 60));
                    else b.literal(static_cast<std::uint8_t>(next(0, 256)));
                }
            }
            RarBlockBuilder::BuildOpts o; o.last = bi == block_count - 1;
            auto blk = b.build(o);
            stream.insert(stream.end(), blk.begin(), blk.end());
        }
        CHECK(decode_new(stream, m.history.size()) == m.file_output());
    }
}

TEST(second_block_without_tables_reuses_the_previous_tables_and_the_last_flag_is_optional) {
    RarModel m;
    auto b1 = RarBlockBuilder(m).literals("one ").build(not_last());
    auto b2 = RarBlockBuilder(m, no_tables()).literals("two").build(not_last());
    Bytes both = b1; both.insert(both.end(), b2.begin(), b2.end());
    CHECK(decode_new(both, 7) == B("one two"));
}

TEST(window_wraps_correctly_when_data_far_exceeds_it) {
    RarModel m;
    std::mt19937 rng(9);
    RarBlockBuilder b(m);
    b.literals(rb(1000, 1));
    while (m.history.size() < 900'000) {
        if (rng() % 3 == 0) b.literals(rb(1 + rng() % 199, static_cast<unsigned>(rng())));
        else b.match(5 + static_cast<int>(rng() % 495), 1 + static_cast<std::int64_t>(rng() % std::min<std::size_t>(m.history.size(), 100'000)));
    }
    CHECK(decode_new(b.build(), m.history.size(), 1 << 17, 1 << 17) == m.file_output());
}

TEST(window_grows_from_its_initial_size_without_losing_history) {
    RarModel m;
    RarBlockBuilder b(m);
    b.literals(rb(2'600'000, 2));
    b.match(1000, 2'500'000).match(3000, 1'048'577).match(500, 2'599'999);
    CHECK(decode_new(b.build(), m.history.size(), 1ull << 24, 1ull << 23) == m.file_output());
}

TEST(solid_files_reference_earlier_files_and_non_solid_files_cannot) {
    RarModel m;
    auto first = RarBlockBuilder(m).literals(rb(2000, 7)).build();
    auto first_out = m.file_output();
    m.start_new_file(true);
    auto second = RarBlockBuilder(m).match(500, 1500).literals("end").build();
    auto second_out = m.file_output();
    rt::TestDecoder d(Dict4M, Dict4M);
    CHECK(decode(d, first, first_out.size()) == first_out);
    CHECK(decode(d, second, second_out.size(), true) == second_out);
    rt::TestDecoder fresh(Dict4M, Dict4M);
    decode(fresh, first, first_out.size());
    CHECK(rejected(second, second_out.size(), &fresh, false));            // history was dropped: distance outside written data
}

// ---------------------------------------------------------------- filters (expected values computed by hand)

TEST(delta_filter_over_literal_data) {
    RarModel m;
    auto block = RarBlockBuilder(m).filter(0, 0, 6, 2).literals(Bytes{1, 2, 3, 4, 5, 6}).build();
    CHECK((decode_new(block, 6) == Bytes{255, 252, 253, 247, 250, 241}));
}

TEST(e8_filter_with_raw_bytes_before_and_after_uses_file_relative_position) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals(Bytes{1, 2, 3}).filter(1, 2, 7).literals(Bytes{9, 9, 0xE8, 100, 0, 0, 0, 0x90, 0x90, 0, 0, 0, 7, 7}).build();
    auto out = decode_new(block, 17);
    // E8 at filtered index 0 -> i becomes 1; offset = 1 + 5 = 6; 100 - 6 = 94
    CHECK((Bytes(out.begin(), out.begin() + 5) == Bytes{1, 2, 3, 9, 9}));
    CHECK((Bytes(out.begin() + 5, out.begin() + 12) == Bytes{0xE8, 94, 0, 0, 0, 0x90, 0x90}));
}

TEST(arm_filter_subtracts_the_instruction_index) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals(rb(8, 1)).filter(3, 0, 4).literals(Bytes{0x10, 0, 0, 0xEB}).build();
    auto out = decode_new(block, 12);
    CHECK((Bytes(out.begin() + 8, out.end()) == Bytes{0x0E, 0, 0, 0xEB}));        // 0x10 - (8 / 4)
}

TEST(back_to_back_filters_apply_in_order) {
    RarModel m;
    auto block = RarBlockBuilder(m).filter(0, 0, 4, 1).filter(0, 4, 4, 1).literals(Bytes{1, 1, 1, 1, 2, 2, 2, 2}).build();
    CHECK((decode_new(block, 8) == Bytes{255, 254, 253, 252, 254, 252, 250, 248}));
}

TEST(filters_work_while_the_window_is_flushing_and_wrapping) {
    RarModel m;
    auto head = rb(100'000, 11), middle = rb(50'000, 12), tail = rb(100'000, 13);
    auto block = RarBlockBuilder(m).literals(head).filter(0, 0, 50'000, 1).literals(middle).literals(tail).build();
    Bytes expected(250'000);
    std::copy(head.begin(), head.end(), expected.begin());
    std::uint8_t prev = 0;
    for (std::size_t i = 0; i < middle.size(); ++i) { prev = static_cast<std::uint8_t>(prev - middle[i]); expected[100'000 + i] = prev; }
    std::copy(tail.begin(), tail.end(), expected.begin() + 150'000);
    CHECK(decode_new(block, 250'000, 1 << 17, 1 << 17) == expected);
}

TEST(e8e9_type_converts_jumps_but_e8_type_does_not) {
    Bytes jmp{0xE9, 100, 0, 0, 0, 0x90, 0x90};
    CHECK(decode_new(RarBlockBuilder(fresh()).filter(1, 0, 7).literals(jmp).build(), 7) == jmp);
    CHECK((decode_new(RarBlockBuilder(fresh()).filter(2, 0, 7).literals(jmp).build(), 7) == Bytes{0xE9, 99, 0, 0, 0, 0x90, 0x90}));
}

TEST(arm_filter_ignores_a_trailing_partial_word) {
    RarModel m;
    auto out = decode_new(RarBlockBuilder(m).filter(3, 0, 11).literals(Bytes{1, 2, 3, 0xEA, 0x10, 0, 0, 0xEB, 9, 9, 9}).build(), 11);
    CHECK((Bytes(out.begin() + 8, out.end()) == Bytes{9, 9, 9}));
    CHECK_EQ(out[7], 0xEB);
}

TEST(filter_positions_are_relative_to_the_start_of_the_current_file_in_solid_mode) {
    RarModel m;
    auto first = RarBlockBuilder(m).literals(rb(1000, 4)).build();
    m.start_new_file(true);
    auto second = RarBlockBuilder(m).filter(1, 0, 7).literals(Bytes{0xE8, 100, 0, 0, 0, 0x90, 0x90}).build();
    rt::TestDecoder d(Dict4M, Dict4M);
    decode(d, first, 1000);
    CHECK((decode(d, second, 7, true) == Bytes{0xE8, 99, 0, 0, 0, 0x90, 0x90}));       // an absolute window position of 1000 would give 99 - 1000
}

// ---------------------------------------------------------------- tables

TEST(table_nibble_escapes_and_odd_nibble_alignment_are_handled) {
    std::vector<int> bc(20, 0);
    for (int i = 4; i <= 18; ++i) bc[static_cast<std::size_t>(i)] = 4;
    bc[19] = 15;
    RarHuffmanEncoder enc(bc);
    RarModel m;
    RarBlockBuilder b(m, raw([&](RarBitWriter& w) {
        w.write(15, 4); w.write(2, 4);                                  // 4 zeros
        for (int i = 4; i <= 18; ++i) w.write(4, 4);
        w.write(15, 4); w.write(0, 4);                                  // a literal 15
        for (int i = 0; i < 306; ++i) enc.write(w, 9);
        for (int i = 0; i < 64; ++i) enc.write(w, 6);
        for (int i = 0; i < 16; ++i) enc.write(w, 4);
        for (int i = 0; i < 44; ++i) enc.write(w, 6);
    }));
    b.literals("ok!");
    CHECK(decode_new(b.build(), 3) == B("ok!"));
}

TEST(table_repeat_and_zero_run_codes_expand_correctly) {
    std::vector<int> bc(20, 0);
    bc[9] = 2; bc[6] = 2; bc[4] = 3; bc[17] = 3; bc[0] = 3;
    RarHuffmanEncoder enc(bc);
    RarModel m;
    RarBlockBuilder b(m, raw([&](RarBitWriter& w) {
        for (int i = 0; i < 20; ++i) w.write(bc[static_cast<std::size_t>(i)], 4);
        enc.write(w, 9);
        int left = 305;
        while (left > 0) {
            int run = std::min(left, 18);
            if (run < 11) { for (int k = 0; k < run; ++k) enc.write(w, 9); left -= run; continue; }
            enc.write(w, 17); w.write(run - 11, 7); left -= run;
        }
        for (int i = 0; i < 64; ++i) enc.write(w, 6);
        for (int i = 0; i < 16; ++i) enc.write(w, 4);
        for (int i = 0; i < 44; ++i) enc.write(w, 6);
    }));
    b.literals("rep");
    CHECK(decode_new(b.build(), 3) == B("rep"));
}

TEST(table_runs_that_overshoot_the_end_are_clamped_not_overflowed) {
    RarModel m;
    RarBlockBuilder b(m, raw([](RarBitWriter& w) {
        for (int i = 0; i < 20; ++i) w.write(5, 4);
        for (int i = 0; i < 306; ++i) w.write(9, 5);
        for (int i = 0; i < 64; ++i) w.write(6, 5);
        for (int i = 0; i < 16; ++i) w.write(4, 5);
        w.write(6, 5);                                                  // RD: one explicit length ...
        w.write(17, 5); w.write(100, 7);                                // ... then "repeat 111 times": only 43 are left
    }));
    b.literals("clamped");
    CHECK(decode_new(b.build(), 7) == B("clamped"));
    RarModel m2;
    RarBlockBuilder z(m2, raw([](RarBitWriter& w) {
        for (int i = 0; i < 20; ++i) w.write(5, 4);
        for (int i = 0; i < 306; ++i) w.write(9, 5);
        for (int i = 0; i < 64; ++i) w.write(6, 5);
        for (int i = 0; i < 16; ++i) w.write(4, 5);
        w.write(19, 5); w.write(127, 7);                                // zero run of 138 over 44 remaining
    }));
    z.literals("zeroed");
    CHECK(decode_new(z.build(), 6) == B("zeroed"));
}

TEST(incomplete_code_with_unassigned_bit_patterns_fails_typed_never_with_an_index_error) {
    std::vector<int> ld(306, 0); ld[0] = 1;
    RarModel m;
    RarBlockBuilder b(m, with_ld(ld));
    b.literals(Bytes(8, 0));
    b.raw().write(0xFF, 8);
    auto stream = b.build();
    CHECK(decode_new(stream, 9) == Bytes(9, 0));                        // the unassigned pattern became one more literal 0
    CHECK(rejected(stream, 20));
}

// ---------------------------------------------------------------- hostile: structure

TEST(wrong_block_checksum_size_beyond_data_and_four_byte_size_field_are_rejected) {
    RarBlockBuilder::BuildOpts o;
    o.checksum_xor = 1;
    CHECK(rejected(RarBlockBuilder(fresh()).literals("x").build(o), 1));
    RarBlockBuilder::BuildOpts s; s.size_override = 5000;
    CHECK(rejected(RarBlockBuilder(fresh()).literals("x").build(s), 1));
    RarBlockBuilder::BuildOpts f; f.size_field_bytes = 4;
    CHECK(rejected(RarBlockBuilder(fresh()).literals("x").build(f), 1));
}

TEST(truncated_block_header_is_rejected) {
    CHECK(rejected(Bytes{0x80}, 1));
    CHECK(rejected(Bytes{0x80, 0x00}, 1));
}

TEST(first_block_without_tables_and_missing_tables_after_a_reset_are_rejected) {
    CHECK(rejected(RarBlockBuilder(fresh(), no_tables()).literals("x").build(), 1));
    rt::TestDecoder d(Dict4M, Dict4M);
    decode(d, RarBlockBuilder(fresh()).literals("x").build(), 1);
    CHECK(rejected(RarBlockBuilder(fresh(), no_tables()).literals("y").build(), 1, &d, false));     // a non-solid file resets the tables
}

TEST(zero_length_block_with_tables_flag_is_truncated_tables) {
    Bytes block{0x80 | 0x40, static_cast<std::uint8_t>(0x5A ^ (0x80 | 0x40)), 0};
    CHECK(rejected(block, 1));
}

TEST(oversubscribed_tables_are_rejected) {
    { Cfg c; c.ld = std::vector<int>(306, 1); CHECK(rejected(RarBlockBuilder(fresh(), c).literals("x").build(), 1)); }
    { Cfg c; c.dd = std::vector<int>(64, 2); CHECK(rejected(RarBlockBuilder(fresh(), c).literals("x").build(), 1)); }
    { Cfg c; c.ldd = std::vector<int>(16, 1); CHECK(rejected(RarBlockBuilder(fresh(), c).literals("x").build(), 1)); }
    { Cfg c; c.rd = std::vector<int>(44, 3); CHECK(rejected(RarBlockBuilder(fresh(), c).literals("x").build(), 1)); }
    CHECK(rejected(RarBlockBuilder(fresh(), raw([](RarBitWriter& w) { for (int i = 0; i < 20; ++i) w.write(1, 4); w.write(0, 16); })).build(), 1));
}

TEST(repeat_previous_length_at_the_start_of_the_table_is_rejected) {
    RarBlockBuilder b(fresh(), raw([](RarBitWriter& w) { for (int i = 0; i < 20; ++i) w.write(5, 4); w.write(16, 5); w.write(0, 3); w.write(0, 64); }));
    CHECK(rejected(b.build(), 1));
}

TEST(truncated_tables_are_rejected_even_when_no_output_is_expected) {
    CHECK(rejected(RarBlockBuilder(fresh(), raw([](RarBitWriter& w) { for (int i = 0; i < 5; ++i) w.write(5, 4); })).build(), 1));
    auto two = RarBlockBuilder(fresh(), raw([](RarBitWriter& w) { for (int i = 0; i < 20; ++i) w.write(5, 4); for (int i = 0; i < 10; ++i) w.write(9, 5); })).build();
    CHECK(rejected(two, 1));
    CHECK(rejected(two, 0));
}

TEST(last_byte_valid_bit_count_is_honoured_in_both_directions) {
    // Literal 0 costs one bit. The tables take 2230 bits, so twelve literals end 2 bits into a byte: 2 valid bits in the last byte.
    std::vector<int> ld(306, 0); ld[0] = 1;
    for (int i = 1; i < 256; ++i) ld[static_cast<std::size_t>(i)] = 9;
    auto stream = [&](std::optional<int> valid) {
        RarBlockBuilder::BuildOpts o; o.valid_bits_override = valid;
        return RarBlockBuilder(fresh(), with_ld(ld)).literals(Bytes(12, 0)).build(o);
    };
    CHECK(decode_new(stream(std::nullopt), 12) == Bytes(12, 0));
    CHECK(rejected(stream(8), 12));          // padding bits are decoded as six more literals: too long
    CHECK(rejected(stream(1), 12));          // the twelfth literal begins at the claimed end: too short
}

TEST(symbol_cut_off_mid_way_is_rejected) {
    RarModel m;
    RarBlockBuilder::BuildOpts o; o.cut_data_bytes = 2;
    CHECK(rejected(RarBlockBuilder(m).literals("abcd").match(10, 3).build(o), 14));
}

// ---------------------------------------------------------------- hostile: values

TEST(distance_before_the_start_beyond_the_dictionary_and_beyond_the_window_are_rejected) {
    CHECK(rejected(RarBlockBuilder(fresh()).literal('a').match(5, 2, false).build(), 6));
    CHECK(rejected(RarBlockBuilder(fresh()).literals(rb(300'000, 3)).match(5, 150'000, false).build(), 300'005, nullptr, false, 1 << 17, 1 << 17));
    CHECK(rejected(RarBlockBuilder(fresh()).literals(rb(300'000, 3)).match(10, 200'000, false).build(), 300'010, nullptr, false, 1 << 22, 1 << 17));
}

TEST(output_longer_or_shorter_than_declared_is_rejected_for_literals_and_matches) {
    CHECK(rejected(RarBlockBuilder(fresh()).literals("abcde").build(), 3));
    CHECK(rejected(RarBlockBuilder(fresh()).literals("abc").match(100, 1, false).build(), 20));
    CHECK(rejected(RarBlockBuilder(fresh()).literals("abc").build(), 10));
    CHECK(rejected(RarBlockBuilder(fresh()).literals("abc").build(not_last()), 10));
    RarModel m;
    auto exact = RarBlockBuilder(m).literals("abc").match(17, 3).build();
    CHECK(decode_new(exact, 20) == m.file_output());          // ends exactly at the declared size
}

TEST(distance_exactly_at_the_limits_is_allowed_one_more_is_not) {
    RarModel m;
    auto ok = RarBlockBuilder(m).literals(rb(100, 2)).match(10, 100).build();
    CHECK(decode_new(ok, 110) == m.file_output());
    CHECK(rejected(RarBlockBuilder(fresh()).literals(rb(100, 2)).match(10, 101, false).build(), 110));
    RarModel m2;
    auto ok_dict = RarBlockBuilder(m2).literals(rb(140'000, 3)).match(10, 131'072).build();
    CHECK(decode_new(ok_dict, 140'010, 1 << 17, 1 << 17) == m2.file_output());
    CHECK(rejected(RarBlockBuilder(fresh()).literals(rb(140'000, 3)).match(10, 131'073, false).build(), 140'010, nullptr, false, 1 << 17, 1 << 17));
}

TEST(repeating_a_cache_entry_that_was_never_set_is_rejected) {
    auto block = RarBlockBuilder(fresh()).literals(rb(50, 3)).match(5, 10).match(5, 20).repeat_distance(3, 6, false).build();
    CHECK(rejected(block, 66));
}

// ---------------------------------------------------------------- hostile: filters

TEST(invalid_filter_lengths_are_rejected) {
    for (std::int64_t len : {3, 0x400001}) {
        auto block = RarBlockBuilder(fresh()).filter(0, 0, len).literals(Bytes{1, 2, 3, 4}).build();
        CHECK(rejected(block, 4, nullptr, false, 1ull << 30, Dict4M));
    }
}

TEST(filter_longer_than_half_the_dictionary_is_rejected) {
    CHECK(rejected(RarBlockBuilder(fresh()).filter(1, 0, 70'000).literals(Bytes{1, 2, 3, 4}).build(), 4, nullptr, false, 1 << 17, 1 << 17));
    auto ok = RarBlockBuilder(fresh()).filter(1, 0, 65'536).literals(Bytes(65'536, 0)).build();
    CHECK(!rejected(ok, 65'536, nullptr, false, 1 << 17, 1 << 17));
}

TEST(unknown_filter_types_are_unsupported) {
    for (int type : {4, 5, 7}) {
        auto block = RarBlockBuilder(fresh()).filter(type, 0, 8).literals(Bytes(8, 0)).build();
        CHECK(fails(block, 8) == Status::Unsupported);
    }
}

TEST(overlapping_filters_are_rejected) {
    CHECK(rejected(RarBlockBuilder(fresh()).filter(1, 100, 50).filter(1, 120, 50).literals(Bytes(300, 0)).build(), 300));
    CHECK(!rejected(RarBlockBuilder(fresh()).filter(1, 100, 50).filter(1, 150, 50).literals(Bytes(300, 0)).build(), 300));
}

TEST(pending_filter_limit_and_filters_beyond_the_data) {
    auto stream = [](int count) {
        RarBlockBuilder b(fresh());
        for (int i = 0; i < count; ++i) b.filter(1, 1000 + 4 * i, 4);
        return b.literals(Bytes(static_cast<std::size_t>(1000 + 4 * count), 0)).build();
    };
    CHECK(!rejected(stream(1024), 1000 + 4 * 1024));
    CHECK(rejected(stream(1025), 1000 + 4 * 1025));
    CHECK(rejected(RarBlockBuilder(fresh()).filter(1, 0, 100).literals(Bytes(10, 0)).build(), 10));
    CHECK(!rejected(RarBlockBuilder(fresh()).filter(1, 0, 10).literals(Bytes(10, 0)).build(), 10));
}

TEST(filter_length_cap_is_exactly_four_mebibytes) {
    auto stream = [](std::int64_t length) { return RarBlockBuilder(fresh()).filter(1, 0, length).literals(Bytes(static_cast<std::size_t>(length), 0)).build(); };
    CHECK(!rejected(stream(0x400000), 0x400000, nullptr, false, 1ull << 24, 1ull << 23));
    CHECK(rejected(stream(0x400001), 0x400001, nullptr, false, 1ull << 24, 1ull << 23));
}

// ---------------------------------------------------------------- solid state, boundaries, misc

TEST(solid_continuation_keeps_the_distance_cache_and_non_solid_resets_it) {
    RarModel m;
    auto first = RarBlockBuilder(m).literals(rb(100, 1)).match(8, 50).build();
    auto first_out = m.file_output();
    m.start_new_file(true);
    auto second = RarBlockBuilder(m).repeat_last().build();               // repeats length 8 at distance 50 across files
    auto second_out = m.file_output();
    rt::TestDecoder d(Dict4M, Dict4M);
    decode(d, first, first_out.size());
    CHECK(decode(d, second, second_out.size(), true) == second_out);
    rt::TestDecoder d2(Dict4M, Dict4M);
    decode(d2, first, first_out.size());
    CHECK(rejected(second, 8, &d2, false));                                // cache and last length were reset: nothing to repeat, size mismatch
}

TEST(trailing_bytes_after_the_last_block_are_ignored) {
    RarModel m;
    auto block = RarBlockBuilder(m).literals("data").build();
    block.insert(block.end(), {1, 2, 3, 4, 5});
    CHECK(decode_new(block, 4) == B("data"));
}

TEST(streams_split_across_arbitrary_segments_decode_identically) {
    std::mt19937 rng(21);
    RarModel m;
    Bytes stream;
    for (int bi = 0; bi < 3; ++bi) {
        Cfg c; c.write_tables = bi == 0;
        RarBlockBuilder b(m, c);
        for (int t = 0; t < 600; ++t) {
            if (rng() % 3 == 0 && m.history.size() > 10) b.match(5 + static_cast<int>(rng() % 75), 1 + static_cast<std::int64_t>(rng() % std::min<std::size_t>(m.history.size(), 5000)));
            else b.literal(static_cast<std::uint8_t>(rng()));
        }
        RarBlockBuilder::BuildOpts o; o.last = bi == 2;
        auto blk = b.build(o);
        stream.insert(stream.end(), blk.begin(), blk.end());
    }
    for (int trial = 0; trial < 60; ++trial) {
        std::vector<std::size_t> cuts = {0, stream.size()};
        const int n = 1 + static_cast<int>(rng() % 7);
        for (int i = 0; i < n; ++i) cuts.push_back(rng() % (stream.size() + 1));
        std::sort(cuts.begin(), cuts.end());
        cuts.erase(std::unique(cuts.begin(), cuts.end()), cuts.end());
        std::vector<std::pair<const std::uint8_t*, std::size_t>> segs;
        for (std::size_t i = 0; i + 1 < cuts.size(); ++i) segs.push_back({stream.data() + cuts[i], cuts[i + 1] - cuts[i]});
        Bytes out;
        rt::TestDecoder d(Dict4M, Dict4M);
        d.decode(segs, m.history.size(), false, [&](const std::uint8_t* p, std::size_t k) { out.insert(out.end(), p, p + k); });
        CHECK(out == m.file_output());
    }
}

TEST(the_sink_never_receives_more_than_the_declared_size_even_when_the_stream_keeps_going) {
    auto received = [](const Bytes& stream, std::uint64_t declared) {
        std::uint64_t got = 0;
        rt::TestDecoder d(1 << 17, 1 << 17);
        try { d.decode({{stream.data(), stream.size()}}, declared, false, [&](const std::uint8_t*, std::size_t n) { got += n; }); CHECK(false); }
        catch (const Error&) {}
        return got;
    };
    CHECK(received(RarBlockBuilder(fresh()).literals(Bytes(200'000, 0)).build(), 10) <= 10);              // 64 KiB flushes happen long before the size check
    CHECK(received(RarBlockBuilder(fresh()).literals(rb(100, 1)).match(4000, 50, false).build(), 120) <= 120);
}

TEST(constructor_rejects_non_positive_sizes_and_the_dictionary_formula) {
    CHECK(throws(Status::InvalidArgument, [] { rt::TestDecoder d(0, 1 << 20); }));
    CHECK(throws(Status::InvalidArgument, [] { rt::TestDecoder d(1 << 20, 0); }));
    CHECK_EQ(rt::dictionary_size(0), 0x20000ull);
    CHECK_EQ(rt::dictionary_size(1), 0x40000ull);
    CHECK_EQ(rt::dictionary_size(5), 0x400000ull);
    CHECK_EQ(rt::dictionary_size(15), 0x100000000ull);
}

// ---------------------------------------------------------------- the filter functions on their own

static Bytes run3(const std::function<void(const std::uint8_t*, std::size_t, std::uint8_t*)>& f, const Bytes& src) {
    Bytes dst(src.size());
    f(src.data(), src.size(), dst.data());
    return dst;
}
static Bytes delta_f(const Bytes& s, int ch) { return run3([&](auto* a, auto n, auto* d) { rt::delta(a, n, ch, d); }, s); }
static Bytes e8_f(const Bytes& s, std::int64_t start, bool ext) { return run3([&](auto* a, auto n, auto* d) { rt::e8(a, n, start, ext, d); }, s); }
static Bytes arm_f(const Bytes& s, std::int64_t start) { return run3([&](auto* a, auto n, auto* d) { rt::arm(a, n, start, d); }, s); }

TEST(delta_hand_vectors_and_round_trip) {
    CHECK((delta_f(Bytes{1, 2, 3, 4, 5, 6}, 2) == Bytes{255, 252, 253, 247, 250, 241}));
    CHECK((delta_f(Bytes{1, 2, 3}, 1) == Bytes{255, 253, 250}));
    for (int channels : {1, 2, 3, 7, 31}) {
        std::mt19937 rng(static_cast<unsigned>(channels));
        for (std::size_t length : {4u, 5u, 31u, 32u, 33u, 1000u, 4096u}) {
            Bytes original(length);
            for (auto& b : original) b = static_cast<std::uint8_t>(rng());
            Bytes encoded;
            for (int c = 0; c < channels; ++c) {
                std::uint8_t previous = 0;
                for (std::size_t i = static_cast<std::size_t>(c); i < length; i += static_cast<std::size_t>(channels)) { encoded.push_back(static_cast<std::uint8_t>(previous - original[i])); previous = original[i]; }
            }
            CHECK(delta_f(encoded, channels) == original);
        }
    }
}

TEST(e8_vectors) {
    CHECK((e8_f(Bytes{0xE8, 100, 0, 0, 0, 0x90, 0x90}, 0, false) == Bytes{0xE8, 99, 0, 0, 0, 0x90, 0x90}));
    Bytes big{0xE8, 0, 0, 0, 1, 0x90, 0x90};
    CHECK(e8_f(big, 0, false) == big);
    Bytes neg{0xE8, 0, 0xFF, 0xFF, 0xFF, 0x90, 0x90};
    CHECK(e8_f(neg, 0, false) == neg);
    CHECK((e8_f(Bytes{0xE8, 0xFF, 0xFF, 0xFF, 0xFF, 0x90, 0x90}, 0, false) == Bytes{0xE8, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x90}));
    Bytes jmp{0xE9, 100, 0, 0, 0, 0x90, 0x90};
    CHECK(e8_f(jmp, 0, false) == jmp);
    CHECK((e8_f(jmp, 0, true) == Bytes{0xE9, 99, 0, 0, 0, 0x90, 0x90}));
    Bytes pos{0x90, 0xE8, 200, 0, 0, 0, 0x90, 0x90};
    CHECK((e8_f(pos, 0, false) == Bytes{0x90, 0xE8, 198, 0, 0, 0, 0x90, 0x90}));
    CHECK((e8_f(pos, 1000, false) == Bytes{0x90, 0xE8, 0xDE, 0xFC, 0xFF, 0xFF, 0x90, 0x90}));
    CHECK((e8_f(pos, 0x1000000, false) == Bytes{0x90, 0xE8, 198, 0, 0, 0, 0x90, 0x90}));          // modulo 2^24
    Bytes nested{0xE8, 0xE8, 10, 0, 0, 0x90, 0x90};
    auto expected = nested; expected[1] = 0xE7; expected[2] = 0x0A;
    CHECK(e8_f(nested, 0, false) == expected);
    Bytes tail{0x90, 0x90, 0x90, 0xE8, 1, 0, 0};
    CHECK(e8_f(tail, 0, false) == tail);
}

TEST(arm_vectors) {
    Bytes src{0x10, 0x00, 0x00, 0xEB};
    const std::uint32_t v = ((0x10u - 100u) & 0x00FFFFFFu) | 0xEB000000u;
    CHECK((arm_f(src, 400) == Bytes{static_cast<std::uint8_t>(v), static_cast<std::uint8_t>(v >> 8), static_cast<std::uint8_t>(v >> 16), static_cast<std::uint8_t>(v >> 24)}));
    CHECK(arm_f(src, 0) == src);
    Bytes mixed{1, 2, 3, 0xEA, 0x10, 0, 0, 0xEB, 9, 9};
    auto r = arm_f(mixed, 8);
    CHECK((Bytes(r.begin(), r.begin() + 4) == Bytes(mixed.begin(), mixed.begin() + 4)));
    CHECK((Bytes(r.begin() + 8, r.end()) == Bytes(mixed.begin() + 8, mixed.end())));
    const std::uint32_t w = ((0x10u - 3u) & 0x00FFFFFFu) | 0xEB000000u;
    CHECK((Bytes(r.begin() + 4, r.begin() + 8) == Bytes{static_cast<std::uint8_t>(w), static_cast<std::uint8_t>(w >> 8), static_cast<std::uint8_t>(w >> 16), static_cast<std::uint8_t>(w >> 24)}));
}

TEST(filters_never_change_length_on_random_blocks) {
    std::mt19937_64 rng(77);
    for (int i = 0; i < 2000; ++i) {
        Bytes src(4 + rng() % 296);
        for (auto& b : src) b = static_cast<std::uint8_t>(rng());
        const std::int64_t start = static_cast<std::int64_t>(rng() % (1ull << 40));
        CHECK_EQ(e8_f(src, start, i % 2 == 0).size(), src.size());
        CHECK_EQ(arm_f(src, start).size(), src.size());
        CHECK_EQ(delta_f(src, 1 + i % 31).size(), src.size());
    }
}

UT_MAIN()
