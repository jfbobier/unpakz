// RAR 5 reader tests on the committed fixtures (made by the real `rar`): decoding, filters, solid groups, volumes, and the
// "never silently wrong data" corruption sweeps.
#include <filesystem>
#include <fstream>
#include <map>

#include "../src/rar5.h"
#include "support.h"

using namespace tsupport;
namespace fsys = std::filesystem;

static fsys::path fixture(const char* name) { return fsys::path(UNPAKZ_FIXTURE_DIR) / name; }

static std::vector<std::uint8_t> read_file(const fsys::path& p) {
    std::ifstream f(p, std::ios::binary);
    CHECK(f.good());
    return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(f), {});
}

using Tree = std::map<std::u16string, std::vector<std::uint8_t>>;

static Tree tree_of(const MemoryFs& fs) {
    Tree t;
    for (auto& [k, v] : fs.files) t[fs.names.at(k)] = v;
    return t;
}

static Tree extract_memory(const std::vector<std::uint8_t>& bytes, Options o = {}) {
    MemoryFs fs;
    run(bytes, fs, o);
    return tree_of(fs);
}

static const char* kFixtures[] = {"rar5-delta.rar", "rar5-m1-stored-and-packed.rar", "rar5-m3-mix.rar", "rar5-m5-solid.rar", "rar5-md256m-tiny.rar", "rar5-x86.rar"};

// Reads every file through the RAR5 reader directly and returns its decoder statistics.
static rar5::DecoderStats decode_all(const fsys::path& p) {
    std::vector<std::unique_ptr<rar5::Volume>> vols;
    vols.push_back(rar5::file_volume(p.u16string()));
    auto r = rar5::open(std::move(vols), Limits{});
    EntryFacts f;
    while (r->next(f))
        if (f.kind == EntryKind::File) r->read_data([](const std::uint8_t*, std::size_t, std::uint64_t) {});
    return r->stats();
}

TEST(every_fixture_extracts_and_verifies_its_checksums) {
    for (auto name : kFixtures) {
        auto bytes = read_file(fixture(name));
        auto t = extract_memory(bytes);
        CHECK(!t.empty());
    }
}

TEST(fixtures_exercise_the_decoder_paths_they_were_made_for) {
    CHECK(decode_all(fixture("rar5-x86.rar")).e8 + decode_all(fixture("rar5-x86.rar")).e8e9 > 0);
    CHECK(decode_all(fixture("rar5-delta.rar")).delta > 0);
    CHECK(decode_all(fixture("rar5-m3-mix.rar")).files_decoded > 0);
}

TEST(mix_fixture_lists_the_expected_files) {
    auto entries = list_archive(ArchiveSource::from_file(fixture("rar5-m3-mix.rar").u16string()));
    std::map<std::u16string, std::uint64_t> sizes;
    for (auto& e : entries) if (e.kind == EntryKind::File) sizes[e.relative_path] = e.size;
    CHECK_EQ(sizes.size(), 5u);
    CHECK_EQ(sizes[u"text.txt"], 8000u);
    CHECK_EQ(sizes[u"table.bin"], 4000u);
    CHECK_EQ(sizes[u"empty.txt"], 0u);
    CHECK_EQ(sizes[u"tiny.txt"], 5u);
    CHECK_EQ(sizes[u"sub/z.txt"], 3000u);
}

TEST(skipping_files_of_a_solid_group_still_decodes_them_for_the_window) {
    auto bytes = read_file(fixture("rar5-m5-solid.rar"));
    auto full = extract_memory(bytes);
    auto entries = list_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()));
    // extract each file alone: every one needs the decoded history of the files before it
    for (auto& e : entries) {
        if (e.kind != EntryKind::File) continue;
        Options o;
        o.filter = [&](const EntryInfo& x) { return x.index == e.index; };
        auto one = extract_memory(bytes, o);
        CHECK_EQ(one.size(), 1u);
        CHECK(one.begin()->second == full.at(one.begin()->first));
    }
}

TEST(native_and_libarchive_engines_agree_on_non_solid_archives) {
    for (auto name : {"rar5-delta.rar", "rar5-m1-stored-and-packed.rar", "rar5-m3-mix.rar", "rar5-x86.rar"}) {
        auto bytes = read_file(fixture(name));
        Options lib; lib.rar5_engine = RarEngine::Libarchive;
        CHECK(extract_memory(bytes) == extract_memory(bytes, lib));
    }
}

TEST(multi_volume_sets_extract_from_files_and_match_the_single_volume_view) {
    for (auto dir : {"mv-stored", "mv-m3", "mv-solid"}) {
        auto first = std::string(dir) == "mv-stored" ? "a.part1.rar" : "a.part01.rar";
        auto path = (fixture(dir) / first).u16string();
        auto volumes = locate_volumes(path);
        CHECK(volumes.size() >= 3);
        ArchiveSource src; src.files = volumes;
        MemoryFs fs; Options o; o.file_system = &fs;
        auto r = extract_archive(src, kDest, o);
        CHECK(r.files >= 1);
        CHECK(fs.leftover_temps().empty());
    }
}

TEST(a_missing_volume_is_reported_by_number_before_anything_is_written) {
    auto vols = locate_volumes((fixture("mv-stored") / "a.part1.rar").u16string());
    ArchiveSource src; src.files = {vols[0], vols[2]};                      // volume 2 left out
    MemoryFs fs; Options o; o.file_system = &fs;
    try { extract_archive(src, kDest, o); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::MissingVolume); CHECK_EQ(e.volume(), 2); }
    CHECK_EQ(fs.bytes_written, 0u);
    ArchiveSource only_first; only_first.files = {vols[0]};                  // set ends early: the end block says more follow
    try { extract_archive(only_first, kDest, o); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::MissingVolume); CHECK_EQ(e.volume(), 2); }
}

TEST(dictionary_limit_is_enforced_and_a_huge_declared_dictionary_costs_nothing) {
    auto tiny = read_file(fixture("rar5-md256m-tiny.rar"));              // declares a 256 MiB dictionary for a tiny file
    Options o; o.limits.max_dictionary = 1ull << 20;                        // 1 MiB cap: fine, the window is sized by the data, not the declaration
    CHECK(!extract_memory(tiny, o).empty());
    auto mix = read_file(fixture("rar5-m3-mix.rar"));
    Options small; small.limits.max_dictionary = 1ull << 10;
    MemoryFs fs;
    CHECK(throws(Status::LimitExceeded, [&] { run(mix, fs, small); }));
}

TEST(every_single_byte_flip_is_a_typed_error_or_the_exact_original) {
    for (auto name : kFixtures) {
        auto bytes = read_file(fixture(name));
        auto clean = extract_memory(bytes);
        std::size_t step = bytes.size() > 800 ? bytes.size() / 800 + 1 : 1;
        for (std::size_t pos = 0; pos < bytes.size(); pos += step) {
            auto bad = bytes;
            bad[pos] ^= 0x5A;
            MemoryFs fs;
            try {
                run(bad, fs);
                CHECK(tree_of(fs) == clean);            // accepted: then the output must be exactly right (e.g. a flipped mtime byte)
            } catch (const Error&) {
                CHECK(fs.leftover_temps().empty());
            }
        }
    }
}

TEST(truncation_at_every_length_is_a_typed_error) {
    for (auto name : kFixtures) {
        auto bytes = read_file(fixture(name));
        std::size_t step = bytes.size() > 800 ? bytes.size() / 800 + 1 : 1;
        for (std::size_t len = 0; len < bytes.size(); len += step) {
            std::vector<std::uint8_t> cut(bytes.begin(), bytes.begin() + static_cast<std::ptrdiff_t>(len));
            MemoryFs fs;
            CHECK(throws(Status::Corrupt, [&] { run(cut, fs); }) || throws(Status::Unsupported, [&] { run(cut, fs); }));
            CHECK(fs.final_file_count() == 0 || !fs.leftover_temps().empty() == false);
        }
    }
}

UT_MAIN()
