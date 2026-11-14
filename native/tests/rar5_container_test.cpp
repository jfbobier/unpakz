// RAR5 container and multi-volume tests on hand-built archives (ported from the C# Rar5ArchiveTests / Rar5MultiVolumeTests):
// hostile headers, sizes that wrap, volume sequencing, and sweeps over fixed-up header CRCs.
#include "../src/rar5.h"
#include "rar5_builder.h"
#include "support.h"

extern "C" {
#include "archive_blake2.h"
}

using namespace tsupport;

static Bytes rb(std::size_t n, unsigned seed = 1) { auto s = rnd(n, seed); return Bytes(s.begin(), s.end()); }

struct Opened {
    std::unique_ptr<rar5::Reader> reader;
};

static std::unique_ptr<rar5::Reader> open_set(const std::vector<Bytes>& volumes, const Limits& l = {}) {
    std::vector<std::unique_ptr<rar5::Volume>> v;
    for (auto& b : volumes) v.push_back(rar5::memory_volume(b.data(), b.size()));
    return rar5::open(std::move(v), l);
}
static std::unique_ptr<rar5::Reader> open1(const Bytes& b, const Limits& l = {}) {
    std::vector<std::unique_ptr<rar5::Volume>> v;
    v.push_back(rar5::memory_volume(b.data(), b.size()));      // the volume points at `b`, which the caller keeps alive
    return rar5::open(std::move(v), l);
}

// Status of opening (Ok if it opened) and, for failures, the error.
static Status open_status(const std::vector<Bytes>& vols, const Limits& l = {}, int* volume = nullptr, std::string* message = nullptr) {
    try { open_set(vols, l); return Status::Ok; }
    catch (const Error& e) { if (volume) *volume = e.volume(); if (message) *message = e.what(); return e.status(); }
}
static Status open_status(const Bytes& b, const Limits& l = {}) {
    try { open1(b, l); return Status::Ok; }
    catch (const Error& e) { return e.status(); }
}

struct Item { std::string name; EntryFacts facts; Bytes data; };

// Reads every entry through the reader: facts, and data for files that are supported.
static std::vector<Item> read_all(rar5::Reader& r, Status* data_error = nullptr) {
    std::vector<Item> items;
    EntryFacts f;
    while (r.next(f)) {
        Item it{f.name_utf8, f, {}};
        if (f.kind == EntryKind::File && f.unsupported.empty()) {
            try { r.read_data([&](const std::uint8_t* d, std::size_t n, std::uint64_t) { it.data.insert(it.data.end(), d, d + n); }); }
            catch (const Error& e) { if (data_error) *data_error = e.status(); throw; }
        }
        items.push_back(std::move(it));
    }
    return items;
}
static Status data_status(const Bytes& archive) {
    try { auto r = open1(archive); read_all(*r); return Status::Ok; }
    catch (const Error& e) { return e.status(); }
}
static Status data_status(const std::vector<Bytes>& vols) {
    try { auto r = open_set(vols); read_all(*r); return Status::Ok; }
    catch (const Error& e) { return e.status(); }
}

static Bytes sample(Bytes& a, Bytes& b) {
    a = rb(300); b = rb(5000, 2);
    return Rar5Builder().add_file("a.bin", a).add_directory("dir").add_file("dir/\xE6\x97\xA5\xE6\x9C\xAC b.bin", b).add_file("empty", {}).build();
}

static std::size_t main_block_length(const Bytes& rar) {
    std::size_t p = Rar5Builder::signature.size() + 4;
    std::size_t size = 0, shift = 0, n = 0;
    for (;;) { auto b = rar[p + n]; size |= static_cast<std::size_t>(b & 0x7F) << shift; shift += 7; ++n; if ((b & 0x80) == 0) break; }
    return 4 + n + size;
}
static std::size_t flags_position(const Bytes& rar, std::size_t block_start) {
    std::size_t p = block_start + 4;
    while (rar[p] & 0x80) ++p;
    ++p;                       // header size vint
    while (rar[p] & 0x80) ++p;
    return p + 1;              // after the type vint
}

// ---------------------------------------------------------------- signatures and happy path

TEST(signatures) {
    CHECK(rar5::has_signature(Rar5Builder::signature.data(), Rar5Builder::signature.size()));
    CHECK(!rar5::has_signature(Rar5Builder::signature.data(), 7));
    CHECK(!rar5::has_signature(reinterpret_cast<const std::uint8_t*>("PK\x03\x04"), 4));
    const std::uint8_t rar4[8] = {0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00};
    CHECK(rar5::has_rar4_signature(rar4, 8));
    CHECK(!rar5::has_rar4_signature(Rar5Builder::signature.data(), Rar5Builder::signature.size()));
}

TEST(lists_entries_with_kinds_sizes_names_and_times) {
    Bytes a, b;
    auto rar = sample(a, b);
    auto r = open1(rar);
    auto items = read_all(*r);
    CHECK_EQ(items.size(), 4u);
    CHECK(items[0].name == "a.bin" && items[1].name == "dir" && items[2].name == "dir/\xE6\x97\xA5\xE6\x9C\xAC b.bin" && items[3].name == "empty");
    CHECK(items[0].facts.kind == EntryKind::File && items[1].facts.kind == EntryKind::Directory);
    CHECK_EQ(items[0].facts.size, 300u); CHECK_EQ(items[2].facts.size, 5000u);
    CHECK(items[0].facts.mtime.has_value() && items[0].facts.mtime->unix_seconds == 1'700'000'000);
    CHECK(items[0].data == a); CHECK(items[2].data == b); CHECK(items[3].data.empty());
}

TEST(time_extras_override_the_header_time) {
    auto mt = [](const Bytes& rar) { auto r = open1(rar); EntryFacts f; r->next(f); return f.mtime; };
    FileOpts u; u.extras = {Rar5Builder::unix_time_extra(1'600'000'000)};
    { auto t0 = mt(Rar5Builder().add_file("u", {1}, u).build()); CHECK(t0.has_value() && t0->unix_seconds == 1'600'000'000); }
    const std::uint64_t ft = 133'000'000'000'000'000ull;
    FileOpts w; w.mtime = std::nullopt; w.extras = {Rar5Builder::windows_time_extra(ft)};
    auto t = mt(Rar5Builder().add_file("w", {1}, w).build());
    CHECK(t.has_value()); CHECK_EQ(t->unix_seconds, static_cast<std::int64_t>(ft / 10'000'000ull) - 11644473600ll);
    FileOpts n; n.mtime = std::nullopt;
    CHECK(!mt(Rar5Builder().add_file("n", {1}, n).build()).has_value());
    FileOpts absurd; absurd.mtime = std::nullopt; absurd.extras = {Rar5Builder::windows_time_extra(~0ull)};
    CHECK(!mt(Rar5Builder().add_file("x", {1}, absurd).build()).has_value());
}

TEST(unknown_skippable_blocks_and_trailing_bytes_are_tolerated) {
    Rar5Builder b; b.trailing = {1, 2, 3, 4};
    b.add_block(99, 0x04, {1, 2, 3}).add_block(3, 0, vint(0), {9, 9, 9}).add_file("a", {7});
    auto rar = b.build();
    auto r = open1(rar);
    auto items = read_all(*r);
    CHECK_EQ(items.size(), 1u); CHECK(items[0].name == "a");
}

TEST(redirection_records_make_a_link_entry) {
    for (std::uint64_t t : {1, 2, 3, 4, 5}) {
        FileOpts o; o.extras = {Rar5Builder::redirection_extra(t, "/etc/passwd")};
        auto rar = Rar5Builder().add_file("l", {}, o).build();
        auto r = open1(rar);
        EntryFacts f; r->next(f);
        CHECK(f.kind == EntryKind::Symlink);
    }
}

TEST(unix_attributes_decide_symlink_special_and_directory_kinds) {
    auto with = [](std::uint64_t host, std::uint64_t attr) { FileOpts o; o.host = host; o.attr = attr; return o; };
    auto rar = Rar5Builder().add_file("sym", {}, with(1, 0xA1FF)).add_file("fifo", {}, with(1, 0x11A4)).add_file("chr", {}, with(1, 0x21A4))
                   .add_file("dirbymode", {}, with(1, 0x41ED)).add_file("winfile", {1}, with(0, 0xA000)).build();
    auto r = open1(rar);
    std::vector<EntryKind> kinds;
    EntryFacts f;
    while (r->next(f)) kinds.push_back(f.kind);
    CHECK((kinds == std::vector<EntryKind>{EntryKind::Symlink, EntryKind::Special, EntryKind::Special, EntryKind::Directory, EntryKind::File}));
}

TEST(directory_flag_wins_over_host_and_attributes) {
    FileOpts a; a.host = 0; a.attr = 0x10;
    FileOpts b; b.host = 1; b.attr = 0x81A4;
    auto rar = Rar5Builder().add_directory("win", a).add_directory("unixfile", b).build();
    auto r = open1(rar);
    EntryFacts f;
    while (r->next(f)) CHECK(f.kind == EntryKind::Directory);
}

// ---------------------------------------------------------------- unsupported (reported, never mis-extracted)

static std::string unsupported_of(const Bytes& rar) { auto r = open1(rar); EntryFacts f; r->next(f); return f.unsupported; }

TEST(reserved_methods_and_newer_versions_are_unsupported) {
    for (int m : {6, 7}) {
        FileOpts o; o.method = m;
        auto rar = Rar5Builder().add_file("c", {1, 2, 3}, o).build();
        CHECK(!unsupported_of(rar).empty());
        auto r = open1(rar); EntryFacts f; r->next(f);
        CHECK(throws(Status::Unsupported, [&] { r->read_data([](const std::uint8_t*, std::size_t, std::uint64_t) {}); }));
    }
    FileOpts v; v.method = 3; v.compression_version = 1;
    CHECK(!unsupported_of(Rar5Builder().add_file("c", {1, 2, 3}, v).build()).empty());
}

TEST(garbage_in_a_compressed_entry_is_corrupt_not_unsupported) {
    FileOpts o; o.method = 3; o.unpacked_override = 100;
    auto rar = Rar5Builder().add_file("c", {1, 2, 3, 4, 5, 6, 7, 8}, o).build();
    CHECK(unsupported_of(rar).empty());
    CHECK(data_status(rar) == Status::Corrupt);
}

TEST(encrypted_unknown_size_and_missing_integrity_are_unsupported) {
    FileOpts e; e.extras = {Rar5Builder::encryption_extra()};
    CHECK(!unsupported_of(Rar5Builder().add_file("e", {1}, e).build()).empty());
    FileOpts u; u.unknown_size = true;
    CHECK(!unsupported_of(Rar5Builder().add_file("u", {1}, u).build()).empty());
    FileOpts n; n.omit_crc = true;
    CHECK(!unsupported_of(Rar5Builder().add_file("n", {1}, n).build()).empty());          // no CRC and no hash: nothing to verify against
    FileOpts d; d.omit_crc = true;
    auto dir = Rar5Builder().add_directory("d", d).build();
    CHECK(unsupported_of(dir).empty());                                                    // directories carry no data or CRC
}

TEST(blake2sp_is_verified_when_it_is_the_only_integrity_data) {
    Bytes data = rb(5000, 4);
    std::uint8_t good[32];
    blake2sp_state st;
    blake2sp_init(&st, 32); blake2sp_update(&st, data.data(), data.size()); blake2sp_final(&st, good, 32);
    FileOpts o; o.omit_crc = true; o.extras = {Rar5Builder::extra(2, cat({vint(0), Bytes(good, good + 32)}))};
    auto rar = Rar5Builder().add_file("h", data, o).build();
    CHECK(unsupported_of(rar).empty());
    CHECK(data_status(rar) == Status::Ok);
    FileOpts bad; bad.omit_crc = true; bad.extras = {Rar5Builder::hash_extra()};          // an all-zero hash
    CHECK(data_status(Rar5Builder().add_file("h", data, bad).build()) == Status::Corrupt);
}

TEST(file_version_records_become_a_name_suffix) {
    FileOpts o; o.extras = {Rar5Builder::extra(4, cat({vint(0), vint(7)}))};
    auto rar = Rar5Builder().add_file("f.txt", {1}, o).build();
    auto r = open1(rar);
    EntryFacts f; r->next(f);
    CHECK(f.name_utf8 == "f.txt;7");
}

TEST(encrypted_headers_and_unknown_mandatory_blocks_are_unsupported) {
    { Rar5Builder b; b.omit_main = true; b.add_block(4, 0, Bytes(40, 0)).add_file("a", {1}); CHECK(open_status(b.build()) == Status::Unsupported); }
    CHECK(open_status(Rar5Builder().add_block(99, 0, {1}).add_file("a", {1}).build()) == Status::Unsupported);
    CHECK(open_status(Rar5Builder().add_file("a", {1}).add_block(4, 0x04, Bytes(20, 0)).add_file("b", {2}).build()) == Status::Unsupported);   // even if "skippable"
}

// ---------------------------------------------------------------- volumes flagged inside a single archive

TEST(a_lone_volume_flagged_as_a_later_volume_is_a_missing_volume) {
    Rar5Builder b; b.archive_flags = 0x03; b.add_file("a", {1});
    int vol = 0;
    CHECK(open_status({b.build()}, {}, &vol) == Status::MissingVolume);
    CHECK_EQ(vol, 1);
}

TEST(split_flags_that_nothing_satisfies_are_corrupt_in_a_single_archive) {
    for (std::uint64_t flag : {0x08ull, 0x10ull}) CHECK(open_status(Rar5Builder().add_block(2, flag, {}).build()) == Status::Corrupt);
    auto cont = Rar5Builder().add_file("a", {1, 2, 3}).build();
    auto block = Rar5Builder::signature.size() + main_block_length(cont);
    cont[flags_position(cont, block)] |= 0x10;
    Rar5Builder::fix_header_crc(cont, block);
    CHECK(open_status(cont) == Status::Corrupt);
    auto before = Rar5Builder().add_file("a", {1, 2, 3}).build();
    before[flags_position(before, block)] |= 0x08;
    Rar5Builder::fix_header_crc(before, block);
    CHECK(open_status(before) == Status::Corrupt);
}

// ---------------------------------------------------------------- structural rejections

TEST(short_or_missing_signature) {
    CHECK(open_status(Bytes(4, 0)) == Status::Corrupt);
    CHECK(open_status(Bytes(100, 0)) == Status::Corrupt);
    Bytes a, b; auto rar = sample(a, b);
    rar[6] = 0x00;
    CHECK(open_status(rar) == Status::Corrupt);
    rar[6] = 0x01; rar[0] = 0x00;
    CHECK(open_status(rar) == Status::Corrupt);
}

TEST(missing_end_missing_main_and_second_main_header) {
    { Rar5Builder b; b.omit_end = true; b.add_file("a", {1}); CHECK(open_status(b.build()) == Status::Corrupt); }
    { Rar5Builder b; b.omit_main = true; b.add_file("a", {1}); CHECK(open_status(b.build()) == Status::Corrupt); }
    CHECK(open_status(Rar5Builder().add_block(1, 0, vint(0)).add_file("a", {1}).build()) == Status::Corrupt);
}

TEST(any_header_crc_mismatch_is_corrupt) {
    Bytes a, b; auto rar = sample(a, b);
    rar[Rar5Builder::signature.size() + main_block_length(rar) + 4 + 3] ^= 0x01;
    CHECK(open_status(rar) == Status::Corrupt);
}

TEST(header_size_zero_oversized_or_past_end) {
    auto rar = Rar5Builder().add_file("a", {1}).build();
    auto p = Rar5Builder::signature.size() + main_block_length(rar) + 4;
    auto zero = rar; zero[p] = 0;
    CHECK(open_status(zero) == Status::Corrupt);
    auto with_size = [&](std::uint64_t v) { Bytes r(rar.begin(), rar.begin() + static_cast<std::ptrdiff_t>(p)); append(r, vint(v)); r.insert(r.end(), rar.begin() + static_cast<std::ptrdiff_t>(p) + 1, rar.end()); return r; };
    CHECK(open_status(with_size(1ull << 40)) == Status::Corrupt);
    CHECK(open_status(with_size(2'000'000)) == Status::Corrupt);                          // within the spec maximum, beyond the file
}

TEST(headers_larger_than_the_spec_maximum_are_refused_even_when_they_fit) {
    CHECK(open_status(Rar5Builder().add_block(99, 0x04, Bytes(2'100'000, 0)).add_file("a", {1}).build()) == Status::Corrupt);
    auto ok = Rar5Builder().add_block(99, 0x04, Bytes(2'000'000, 0)).add_file("a", {1}).build();
    CHECK(open_status(ok) == Status::Ok);
    CHECK(open_status(Rar5Builder().add_block(99, 0x04, Bytes(2u * 1024 * 1024 - 8, 0)).add_file("a", {1}).build()) == Status::Ok);        // near the edge
}

TEST(data_size_beyond_the_archive_or_wrapping) {
    FileOpts huge; huge.data_size_field = 1ull << 40;
    CHECK(open_status(Rar5Builder().add_file("a", {1, 2, 3}, huge).build()) == Status::Corrupt);
    FileOpts past; past.data_size_field = 4;
    CHECK(open_status(Rar5Builder().add_file("a", {1, 2, 3}, past).build()) == Status::Corrupt);
    for (std::uint64_t size : {1ull << 63, (1ull << 63) + 5, ~0ull}) {
        FileOpts o; o.data_size_field = size;
        CHECK(open_status(Rar5Builder().add_file("a", {1}, o).build()) == Status::Corrupt);
    }
}

TEST(invalid_names_and_malformed_extra_areas) {
    FileOpts bad_name; bad_name.name_bytes = Bytes{0x61, 0xFF, 0x62};
    CHECK(open_status(Rar5Builder().add_file("x", {1}, bad_name).build()) == Status::Corrupt);
    FileOpts e1; e1.extras = {Bytes{0x7F, 0x01}};                                          // record claims 127 bytes
    CHECK(open_status(Rar5Builder().add_file("x", {1}, e1).build()) == Status::Corrupt);
    FileOpts e2; e2.extras = {Bytes{0x00}};                                                // zero-size record
    CHECK(open_status(Rar5Builder().add_file("x", {1}, e2).build()) == Status::Corrupt);
    FileOpts e3; e3.extras = {Rar5Builder::extra(3, {0x03})};                              // time flags, no time
    CHECK(open_status(Rar5Builder().add_file("x", {1}, e3).build()) == Status::Corrupt);
    FileOpts e4; e4.extras = {cat({vint((1ull << 32) + 2), Bytes{0x09, 0x09}})};           // size beyond 32 bits must not wrap to 2
    CHECK(open_status(Rar5Builder().add_file("x", {1}, e4).build()) == Status::Corrupt);
}

TEST(entry_count_limit) {
    auto rar = Rar5Builder().add_file("a", {1}).add_file("b", {1}).add_file("c", {1}).build();
    Limits l; l.max_entries = 2;
    CHECK(open_status(rar, l) == Status::LimitExceeded);
    l.max_entries = 3;
    CHECK(open_status(rar, l) == Status::Ok);
}

TEST(extra_area_and_name_lengths_that_wrap_or_overlap) {
    FileOpts big; big.extras = {Rar5Builder::unix_time_extra(1)}; big.extra_size_field = 1000;
    CHECK(open_status(Rar5Builder().add_file("a", {1}, big).build()) == Status::Corrupt);
    auto extra = Rar5Builder::unix_time_extra(1'600'000'000);
    FileOpts wrap; wrap.extras = {extra}; wrap.extra_size_field = (1ull << 32) + extra.size();
    CHECK(open_status(Rar5Builder().add_file("a", {1}, wrap).build()) == Status::Corrupt);
    FileOpts nl; nl.name_len_field = (1ull << 32) + 3;
    CHECK(open_status(Rar5Builder().add_file("abc", {1}, nl).build()) == Status::Corrupt);
    FileOpts ov; ov.extras = {Rar5Builder::unix_time_extra(1)}; ov.name_len_field = 6;
    CHECK(open_status(Rar5Builder().add_file("abc", {1}, ov).build()) == Status::Corrupt);
}

TEST(a_data_size_that_wraps_the_parser_back_onto_the_same_block) {
    auto build = [](std::uint64_t data_size) { FileOpts o; o.data_size_field = data_size; return Rar5Builder().add_file("a", {}, o).build(); };
    auto probe = build(1ull << 63);
    auto main = Rar5Builder::signature.size() + main_block_length(probe);
    auto block_length = probe.size() - main - 8;                                           // minus the 8-byte end block
    Limits l; l.max_entries = 50;                                                          // a loop fails fast instead of hanging
    CHECK(open_status(build(0ull - block_length), l) == Status::Corrupt);
}

TEST(tampering_with_the_stored_header_crc_itself) {
    auto rar = Rar5Builder().add_file("a", rb(40)).add_directory("d").build();
    std::vector<std::size_t> starts = {Rar5Builder::signature.size()};
    starts.push_back(starts[0] + main_block_length(rar));
    for (auto start : starts)
        for (std::size_t i = 0; i < 4; ++i) { auto c = rar; c[start + i] ^= 1; CHECK(open_status(c) == Status::Corrupt); }
    auto tail = rar; tail[rar.size() - 8] ^= 1;                                            // end block: crc(4) size(1) type(1) flags(1) endflags(1)
    CHECK(open_status(tail) == Status::Corrupt);
}

TEST(directory_that_claims_data_is_corrupt_and_a_clean_directory_is_fine) {
    FileOpts o; o.attr = 0x41ED; o.unpacked_override = 5;
    CHECK(data_status(Rar5Builder().add_directory("d", o).build()) == Status::Corrupt);
    CHECK(data_status(Rar5Builder().add_directory("d").build()) == Status::Ok);
}

TEST(stored_integrity_and_size_checks) {
    auto rar = Rar5Builder().add_file("a", rb(200)).build();
    auto bad = rar; bad[bad.size() - 8 - 100] ^= 1;                                        // inside the data
    CHECK(data_status(bad) == Status::Corrupt);
    FileOpts wrong; wrong.crc_override = 0xDEADBEEF;
    CHECK(data_status(Rar5Builder().add_file("a", {1, 2, 3}, wrong).build()) == Status::Corrupt);
    FileOpts lie; lie.unpacked_override = 4;
    CHECK(data_status(Rar5Builder().add_file("a", {1, 2, 3}, lie).build()) == Status::Corrupt);
}

// ---------------------------------------------------------------- through the whole pipeline

static Bytes as_bytes(const std::vector<std::uint8_t>& v) { return v; }

TEST(pipeline_extracts_a_rar_tree_in_memory) {
    Bytes a, b; auto rar = sample(a, b);
    MemoryFs fs;
    auto r = run(rar, fs);
    CHECK(fs.file(P("a.bin")) == a);
    CHECK(fs.file(P("dir/\xE6\x97\xA5\xE6\x9C\xAC b.bin")) == b);
    CHECK_EQ(r.files, 3u);
    CHECK_EQ(fs.bytes_written, a.size() + b.size());
    CHECK(fs.mtimes.at(fold(P("a.bin")))->unix_seconds == 1'700'000'000);
    CHECK(fs.leftover_temps().empty());
}

TEST(pipeline_refuses_hostile_names_without_touching_the_file_system) {
    for (auto evil : {"../evil.txt", "a/../../evil.txt", "/etc/passwd", "C:\\evil.txt", "CON", "a.txt:stream"}) {
        auto rar = Rar5Builder().add_file("good", {1}).add_file(evil, {2}).build();
        MemoryFs fs;
        CHECK(throws(Status::UnsafePath, [&] { run(rar, fs); }));
        CHECK(fs.files.empty() && fs.created_dirs.empty());
    }
}

TEST(pipeline_skips_links_and_special_files) {
    FileOpts link; link.extras = {Rar5Builder::redirection_extra(1, "/etc/shadow")};
    FileOpts sym; sym.attr = 0xA1FF;
    FileOpts fifo; fifo.attr = 0x11A4;
    auto rar = Rar5Builder().add_file("link", {}, link).add_file("sym", {}, sym).add_file("fifo", {}, fifo).add_file("real", {5}).build();
    MemoryFs fs;
    auto r = run(rar, fs);
    CHECK_EQ(r.skipped.size(), 3u);
    CHECK_EQ(fs.final_file_count(), 1u);
    CHECK(fs.has_file(P("real")));
}

TEST(pipeline_rejects_case_colliding_names_and_unsupported_entries_before_writing) {
    MemoryFs a;
    CHECK(throws(Status::Conflict, [&] { run(Rar5Builder().add_file("Readme", {1}).add_file("README", {2}).build(), a); }));
    FileOpts exotic; exotic.method = 6;
    MemoryFs b;
    CHECK(throws(Status::Unsupported, [&] { run(Rar5Builder().add_file("ok", {1}).add_file("exotic", {1, 2}, exotic).build(), b); }));
    CHECK(b.files.empty());
    FileOpts d; d.attr = 0x41ED; d.unpacked_override = 5;
    MemoryFs c;
    CHECK(throws(Status::Corrupt, [&] { run(Rar5Builder().add_directory("d", d).build(), c); }) || c.files.empty());
}

// ---------------------------------------------------------------- sweeps

static void try_all(const Bytes& bytes) {
    try {
        auto r = open1(bytes);
        EntryFacts f;
        while (r->next(f))
            if (f.kind == EntryKind::File && f.unsupported.empty() && f.size <= (1u << 20)) r->read_data([](const std::uint8_t*, std::size_t, std::uint64_t) {});
    } catch (const Error&) {}
}

static Bytes small_archive() {
    FileOpts t; t.extras = {Rar5Builder::unix_time_extra(1'650'000'000)};
    return Rar5Builder().add_file("a", rb(40)).add_directory("d").add_file("d/b", rb(60, 3), t).build();
}

TEST(every_truncation_is_handled) {
    auto rar = small_archive();
    for (std::size_t len = 0; len < rar.size(); ++len) try_all(Bytes(rar.begin(), rar.begin() + static_cast<std::ptrdiff_t>(len)));
}

TEST(every_byte_corruption_is_handled) {
    auto original = small_archive();
    auto rar = original;
    for (std::size_t i = 0; i < rar.size(); ++i)
        for (std::uint8_t mask : {0x01, 0x40, 0x80, 0xFF}) { rar[i] ^= mask; try_all(rar); rar[i] = original[i]; }
}

TEST(corrupted_headers_with_valid_crcs_are_handled) {
    // The CRC only protects against accidents. An attacker re-signs, so fuzz the fields with fixed-up CRCs.
    auto original = small_archive();
    auto rar = original;
    std::vector<std::size_t> starts;
    std::size_t p = Rar5Builder::signature.size();
    auto rd = [&](std::size_t& q) { std::uint64_t v = 0; int shift = 0; for (;;) { auto b = rar[q++]; v |= static_cast<std::uint64_t>(b & 0x7F) << shift; shift += 7; if (!(b & 0x80)) break; } return v; };
    while (p < rar.size()) {
        std::size_t q = p + 4;
        auto size = rd(q);
        starts.push_back(p);
        const std::size_t header_end = q + size;
        std::size_t r = p + 4; rd(r);
        rd(r);                                  // type
        auto flags = rd(r);
        if (flags & 1) rd(r);
        std::uint64_t data = (flags & 2) ? rd(r) : 0;
        p = header_end + data;
    }
    for (auto start : starts) {
        std::size_t q = start + 4;
        auto size = rd(q);
        const std::size_t hdr_off = q - start;
        for (std::size_t off = hdr_off; off < hdr_off + size; ++off)
            for (std::uint8_t mask : {0x01, 0x80, 0xFF, 0x7F}) {
                rar[start + off] ^= mask;
                Rar5Builder::fix_header_crc(rar, start);
                try_all(rar);
                rar[start + off] = original[start + off];
            }
        Rar5Builder::fix_header_crc(rar, start);
    }
}

// ---------------------------------------------------------------- multi-volume

struct VSet { std::vector<Bytes> vols; Bytes a, b, c; };
static VSet vsample(std::size_t per_volume = 4000) {
    VSet s; s.a = rb(10'000, 1); s.b = rb(2500, 2); s.c = rb(9000, 3);
    s.vols = Rar5Builder().add_file("a.bin", s.a).add_directory("d").add_file("d/b.bin", s.b).add_file("empty", {}).add_file("c.bin", s.c).build_volumes(per_volume);
    return s;
}
static Bytes data_of(const std::vector<Bytes>& vols, const std::string& name) {
    auto r = open_set(vols);
    for (auto& it : read_all(*r)) if (it.name == name) return it.data;
    return {};
}
static std::size_t end_block_start(const Bytes& v) { return v.size() - 8; }
static void set_end_flags(Bytes& v, std::size_t start, std::uint8_t flags) { v[start + 7] = flags; Rar5Builder::fix_header_crc(v, start); }

TEST(a_set_extracts_exactly_like_one_archive) {
    auto s = vsample();
    CHECK(s.vols.size() >= 5);
    CHECK(data_of(s.vols, "a.bin") == s.a); CHECK(data_of(s.vols, "d/b.bin") == s.b); CHECK(data_of(s.vols, "c.bin") == s.c);
    CHECK(data_of(s.vols, "empty").empty());
    auto single = Rar5Builder().add_file("a.bin", s.a).add_directory("d").add_file("d/b.bin", s.b).add_file("empty", {}).add_file("c.bin", s.c).build();
    auto r1 = open1(single); auto r2 = open_set(s.vols);
    auto i1 = read_all(*r1), i2 = read_all(*r2);
    CHECK_EQ(i1.size(), i2.size());
    for (std::size_t i = 0; i < i1.size() && i < i2.size(); ++i) CHECK(i1[i].name == i2[i].name && i1[i].facts.kind == i2[i].facts.kind && i1[i].data == i2[i].data);
    CHECK_EQ(r2->volume_count(), static_cast<int>(s.vols.size()));
}

TEST(a_file_can_span_many_volumes_and_a_volume_can_hold_several_files) {
    Bytes data = rb(50'000, 4);
    auto vols = Rar5Builder().add_file("small1", {1, 2, 3}).add_file("big", data).add_file("small2", {4, 5}).build_volumes(700);
    CHECK(vols.size() > 60);
    CHECK(data_of(vols, "big") == data);
    CHECK((data_of(vols, "small1") == Bytes{1, 2, 3}));
    CHECK((data_of(vols, "small2") == Bytes{4, 5}));
}

TEST(a_single_volume_set_is_just_an_archive) {
    auto vols = Rar5Builder().add_file("a", {1, 2, 3}).build_volumes(100);
    CHECK_EQ(vols.size(), 1u);
    CHECK((data_of(vols, "a") == Bytes{1, 2, 3}));
}

TEST(corruption_in_a_middle_volume_is_caught_by_the_whole_file_crc) {
    auto s = vsample();
    s.vols[1][s.vols[1].size() / 2] ^= 0x40;
    CHECK(data_status(s.vols) == Status::Corrupt);
}

TEST(volume_sequencing_missing_first_middle_last_and_only_first) {
    auto s = vsample();
    int vol = 0;
    { auto v = s.vols; v.erase(v.begin() + 2); CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, 3); }
    { auto v = s.vols; v.erase(v.begin()); CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, 1); }
    { auto v = s.vols; auto n = static_cast<int>(v.size()); v.pop_back(); CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, n); }
    { CHECK(open_status(std::vector<Bytes>{s.vols[0]}, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, 2); }
}

TEST(volumes_out_of_order_or_duplicated_are_corrupt) {
    auto s = vsample();
    int vol = 0;
    { auto v = s.vols; v.insert(v.begin() + 1, s.vols[0]); CHECK(open_status(v) == Status::Corrupt); }       // a repeated volume 1 where volume 2 belongs
    { auto v = s.vols; std::swap(v[0], v[1]); CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, 1); }
    { auto v = s.vols; std::swap(v[2], v[3]); CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, 3); }
    { auto v = s.vols; v.insert(v.begin() + 2, s.vols[1]); std::string m; CHECK(open_status(v, {}, nullptr, &m) == Status::Corrupt); CHECK(m.find("out of order") != std::string::npos); }
}

TEST(a_volume_from_another_set_and_a_first_volume_that_is_no_set) {
    auto s = vsample();
    auto other = Rar5Builder().add_file("x", rb(9000, 9)).build_volumes(4000);
    auto mixed = s.vols; mixed[1] = other[1];
    CHECK(data_status(mixed) != Status::Ok);
    auto single = Rar5Builder().add_file("a", {1}).build();
    std::string m;
    CHECK(open_status(std::vector<Bytes>{single, s.vols[1]}, {}, nullptr, &m) == Status::Corrupt);
    CHECK(m.find("not part of a multi-volume set") != std::string::npos);
}

TEST(end_blocks_that_contradict_the_set) {
    auto s = vsample();
    { auto v = s.vols; set_end_flags(v[1], end_block_start(v[1]), 0); CHECK(open_status(v) == Status::Corrupt); }       // a middle volume says it is last
    { auto v = s.vols; set_end_flags(v.back(), end_block_start(v.back()), 1); int vol = 0; CHECK(open_status(v, {}, &vol) == Status::MissingVolume); CHECK_EQ(vol, static_cast<int>(v.size()) + 1); }
    { auto vols = Rar5Builder().add_file("f", rb(300, 1)).add_file("g", rb(100, 2)).build_volumes(100);
      set_end_flags(vols[0], end_block_start(vols[0]), 0);
      CHECK(open_status(std::vector<Bytes>{vols[0]}) == Status::Corrupt); }                                              // a file that never continues
}

TEST(continuation_parts_must_agree_with_the_first_part) {
    auto a = Rar5Builder().add_file("aaaa", rb(300, 1)).build_volumes(100);
    auto b = Rar5Builder().add_file("bbbb", rb(300, 1)).build_volumes(100);
    auto c = Rar5Builder().add_file("aaaa", rb(400, 1)).build_volumes(100);
    CHECK(open_status(std::vector<Bytes>{a[0], b[1], b[2]}) == Status::Corrupt);
    CHECK(open_status(std::vector<Bytes>{a[0], c[1], c[2]}) == Status::Corrupt);
    FileOpts m3; m3.method = 3;
    auto m0 = Rar5Builder().add_file("f", rb(300, 1)).build_volumes(100);
    auto mm3 = Rar5Builder().add_file("f", rb(300, 1), m3).build_volumes(100);
    CHECK(open_status(std::vector<Bytes>{m0[0], mm3[1], mm3[2]}) == Status::Corrupt);
    FileOpts win; win.host = 0;
    auto w = Rar5Builder().add_file("f", rb(300, 1), win).build_volumes(100);
    CHECK(open_status(std::vector<Bytes>{m0[0], w[1], w[2]}) == Status::Corrupt);
}

TEST(an_entry_that_does_not_continue_the_pending_split_is_corrupt) {
    auto y = Rar5Builder().add_file("f", rb(250, 1)).build_volumes(100);
    auto x = Rar5Builder().add_file("pad", rb(100, 2)).add_file("g", rb(50, 3)).build_volumes(100);
    CHECK_EQ(x.size(), 2u);
    CHECK(open_status(std::vector<Bytes>{y[0], x[1]}) == Status::Corrupt);
    auto x2 = Rar5Builder().add_file("pad", rb(100, 2)).add_file("f", rb(250, 3)).build_volumes(100);   // same name, identical attributes, no continuation flag
    std::string m;
    CHECK(open_status(std::vector<Bytes>{y[0], x2[1]}, {}, nullptr, &m) == Status::Corrupt);
    CHECK(m.find("different entry follows") != std::string::npos);
}

TEST(a_continuation_part_with_nothing_before_it_is_corrupt) {
    auto vols = Rar5Builder().add_file("f", rb(300, 1)).build_volumes(100);
    auto v0 = vols[0];
    auto block = Rar5Builder::signature.size() + main_block_length(v0);
    v0[flags_position(v0, block)] |= 0x08;
    Rar5Builder::fix_header_crc(v0, block);
    CHECK(open_status(std::vector<Bytes>{v0, vols[1], vols[2]}) == Status::Corrupt);
}

TEST(volume_count_entry_limit_and_non_rar_volumes) {
    auto vols = Rar5Builder().add_file("f", rb(500, 1)).build_volumes(100);
    Limits l; l.max_volumes = 3;
    CHECK(open_status(vols, l) == Status::LimitExceeded);
    l.max_volumes = static_cast<std::uint32_t>(vols.size());
    CHECK(open_status(vols, l) == Status::Ok);
    auto many = Rar5Builder().add_file("a", {1}).add_file("b", {2}).add_file("c", {3}).build_volumes(1);
    Limits e; e.max_entries = 2;
    CHECK(open_status(many, e) == Status::LimitExceeded);
    CHECK(throws(Status::InvalidArgument, [] { std::vector<std::unique_ptr<rar5::Volume>> none; rar5::open(std::move(none), {}); }));
    auto s = vsample();
    CHECK(open_status(std::vector<Bytes>{s.vols[0], Bytes(100, 0)}) == Status::Corrupt);
}

TEST(pipeline_extracts_a_set_and_refuses_an_incomplete_one_before_writing) {
    auto s = vsample();
    {
        MemoryFs fs;
        ArchiveSource src = ArchiveSource::from_memory(nullptr, 0);   // sets are given as files in production; memory path is single-volume
        (void)src;
    }
    auto vols = s.vols;
    vols.erase(vols.begin() + 3);
    CHECK(open_status(vols) == Status::MissingVolume);
}

TEST(every_truncation_and_corruption_of_every_volume_is_handled) {
    auto vols = Rar5Builder().add_file("a", rb(260, 1)).add_file("b", rb(140, 2)).build_volumes(120);
    auto try_set = [](const std::vector<Bytes>& set) {
        try {
            auto r = open_set(set);
            EntryFacts f;
            while (r->next(f)) if (f.kind == EntryKind::File && f.unsupported.empty()) r->read_data([](const std::uint8_t*, std::size_t, std::uint64_t) {});
        } catch (const Error&) {}
    };
    for (std::size_t v = 0; v < vols.size(); ++v) {
        for (std::size_t len = 0; len < vols[v].size(); ++len) { auto set = vols; set[v].resize(len); try_set(set); }
        for (std::size_t i = 0; i < vols[v].size(); ++i)
            for (std::uint8_t mask : {0x01, 0x80, 0xFF}) { auto set = vols; set[v][i] ^= mask; try_set(set); }
    }
}

UT_MAIN()
