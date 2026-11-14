// The C ABI end to end on the real file system (what the .NET front ends will call).
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>

#include "../include/unpakz.h"
#include "support.h"

using namespace tsupport;
namespace fsys = std::filesystem;

struct Dir {
    fsys::path path;
    Dir() {
        path = fsys::temp_directory_path() / ("unpakz-api-" + std::to_string(std::random_device{}()));
        fsys::create_directories(path);
    }
    ~Dir() { std::error_code ec; fsys::remove_all(path, ec); }
    std::u16string u(const char* rel = "") const { return (path / rel).u16string(); }
    std::string read(const char* rel) const { std::ifstream f(path / rel, std::ios::binary); return std::string(std::istreambuf_iterator<char>(f), {}); }
    bool exists(const char* rel) const { return fsys::exists(path / rel); }
    void write(const char* rel, const std::vector<std::uint8_t>& b) const { fsys::create_directories((path / rel).parent_path()); std::ofstream f(path / rel, std::ios::binary); f.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size())); }
    void write(const char* rel, const std::string& b) const { write(rel, std::vector<std::uint8_t>(b.begin(), b.end())); }
};

static unpakz_error make_error() { unpakz_error e{}; e.struct_size = sizeof e; return e; }
static std::string msg(const unpakz_error& e) { return utf16_to_utf8(std::u16string(e.message)); }

TEST(version_and_defaults) {
    CHECK_EQ(unpakz_abi_version(), UNPAKZ_ABI_VERSION);
    CHECK(unpakz_library_version() != nullptr);
    unpakz_limits l{}; l.struct_size = sizeof l;
    unpakz_default_limits(&l);
    CHECK(l.max_entries > 0 && l.max_entry_size > 0 && l.max_total_size > 0 && l.max_volumes > 0 && l.max_path_length > 0 && l.max_path_depth > 0 && l.max_segment_length > 0);
    unpakz_extract_options o{}; o.struct_size = sizeof o;
    unpakz_default_extract_options(&o);
    CHECK_EQ(o.struct_size, sizeof o);
    CHECK_EQ(o.overwrite, UNPAKZ_OVERWRITE_FAIL);
    CHECK(o.restore_timestamps != 0 && o.preflight_pass != 0);
}

TEST(extracts_a_zip_and_a_tar_gz_from_disk_through_the_abi) {
    Dir d;
    auto tree = std::vector<Ent>{file("a.txt", txt(3000, 1)), file("sub/b.bin", rnd(9000, 2)), dir("empty")};
    d.write("t.zip", make_zip(tree));
    d.write("t.tar.gz", make_targz(tree));
    for (auto name : {"t.zip", "t.tar.gz"}) {
        auto out = std::string("out-") + name;
        unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
        unpakz_result r{}; r.struct_size = sizeof r;
        auto err = make_error();
        auto dest = (d.path / out).u16string();
        auto st = unpakz_extract(d.u(name).c_str(), dest.c_str(), &o, &r, &err);
        CHECK_EQ(st, UNPAKZ_OK);
        CHECK_EQ(r.files, 2u); CHECK_EQ(r.directories, 1u); CHECK_EQ(r.bytes, 12000u); CHECK_EQ(r.skipped, 0u);
        CHECK_EQ(d.read((out + "/a.txt").c_str()), tree[0].data);
        CHECK_EQ(d.read((out + "/sub/b.bin").c_str()), tree[1].data);
        CHECK(fsys::is_directory(d.path / out / "empty"));
        for (auto& e : fsys::recursive_directory_iterator(d.path / out)) CHECK(e.path().filename().string().rfind(".unpakz-", 0) != 0);   // no temp files left
    }
}

TEST(null_and_malformed_arguments_are_invalid_argument_not_a_crash) {
    Dir d;
    unpakz_result r{}; r.struct_size = sizeof r;
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    auto err = make_error();
    CHECK_EQ(unpakz_extract(nullptr, d.u().c_str(), &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);
    CHECK_EQ(err.status, UNPAKZ_INVALID_ARGUMENT);
    CHECK_EQ(unpakz_extract(d.u("x.zip").c_str(), nullptr, &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);
    CHECK_EQ(unpakz_extract(d.u("x.zip").c_str(), d.u("o").c_str(), nullptr, &r, &err), UNPAKZ_INVALID_ARGUMENT);
    o.struct_size = 4;
    CHECK_EQ(unpakz_extract(d.u("x.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);   // too small to be ours
    o.struct_size = 0;
    CHECK_EQ(unpakz_extract(d.u("x.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);
    CHECK_EQ(unpakz_extract(d.u("x.zip").c_str(), d.u("o").c_str(), &o, nullptr, nullptr), UNPAKZ_INVALID_ARGUMENT);   // no result, no error buffer: still fine
    CHECK_EQ(unpakz_list(nullptr, nullptr, nullptr, nullptr, &err), UNPAKZ_INVALID_ARGUMENT);
}

TEST(a_newer_larger_options_struct_is_accepted) {
    Dir d;
    d.write("t.zip", make_zip({file("a", "1")}));
    struct Bigger { unpakz_extract_options base; std::uint64_t future_field; } o{};
    o.base.struct_size = sizeof o;
    unpakz_default_extract_options(&o.base);
    o.base.struct_size = sizeof o;
    unpakz_result r{}; r.struct_size = sizeof r; auto err = make_error();
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("out").c_str(), &o.base, &r, &err), UNPAKZ_OK);
}

TEST(errors_are_typed_and_carry_a_message) {
    Dir d;
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    unpakz_result r{}; r.struct_size = sizeof r;
    auto err = make_error();
    CHECK_EQ(unpakz_extract(d.u("missing.zip").c_str(), d.u("o1").c_str(), &o, &r, &err), UNPAKZ_IO);
    CHECK(!msg(err).empty());

    d.write("junk.zip", rnd(4000, 5));
    err = make_error();
    auto st = unpakz_extract(d.u("junk.zip").c_str(), d.u("o2").c_str(), &o, &r, &err);
    CHECK(st == UNPAKZ_UNSUPPORTED || st == UNPAKZ_CORRUPT);
    CHECK_EQ(err.status, st);

    d.write("evil.zip", make_zip({file("ok.txt", "fine"), file("../evil.txt", "x")}));
    err = make_error();
    CHECK_EQ(unpakz_extract(d.u("evil.zip").c_str(), d.u("o3").c_str(), &o, &r, &err), UNPAKZ_UNSAFE_PATH);
    CHECK(msg(err).find("evil.txt") != std::string::npos);
    CHECK(!d.exists("o3/ok.txt"));                                  // refused before anything was written
    CHECK(!d.exists("evil.txt"));
}

TEST(limits_flow_through_and_zero_means_default) {
    Dir d;
    d.write("t.zip", make_zip({file("a", "1"), file("b", "2"), file("c", "3")}));
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    o.limits.max_entries = 2;
    unpakz_result r{}; r.struct_size = sizeof r; auto err = make_error();
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_LIMIT_EXCEEDED);
    o.limits.max_entries = 0;                                       // default
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_OK);
    o.limits.struct_size = 0;                                       // a limits struct without a size is refused
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o2").c_str(), &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);
}

TEST(overwrite_policies_through_the_abi) {
    Dir d;
    d.write("t.zip", make_zip({file("a.txt", "NEW")}));
    d.write("out/a.txt", std::string("OLD"));
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    unpakz_result r{}; r.struct_size = sizeof r; auto err = make_error();
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("out").c_str(), &o, &r, &err), UNPAKZ_CONFLICT);
    CHECK_EQ(d.read("out/a.txt"), std::string("OLD"));
    o.overwrite = UNPAKZ_OVERWRITE_SKIP;
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("out").c_str(), &o, &r, &err), UNPAKZ_OK);
    CHECK_EQ(d.read("out/a.txt"), std::string("OLD")); CHECK_EQ(r.skipped, 1u); CHECK_EQ(r.files, 0u);
    o.overwrite = UNPAKZ_OVERWRITE_REPLACE;
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("out").c_str(), &o, &r, &err), UNPAKZ_OK);
    CHECK_EQ(d.read("out/a.txt"), std::string("NEW"));
    o.overwrite = 77;
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("out").c_str(), &o, &r, &err), UNPAKZ_INVALID_ARGUMENT);
}

TEST(callbacks_filter_progress_and_cancel) {
    Dir d;
    d.write("t.zip", make_zip({file("a.txt", rnd(2000, 1)), file("b.txt", rnd(3000, 2)), file("c.txt", rnd(1500000, 3))}));
    struct Ctx { int progress_calls = 0; std::uint64_t last = 0; int cancel_after = -1; int cancel_polls = 0; } ctx;
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    o.user = &ctx;
    o.filter = [](const unpakz_entry* e, void*) -> int32_t { return std::u16string(e->name) != u"b.txt"; };
    o.progress = [](const unpakz_progress* p, void* u) { auto* c = static_cast<Ctx*>(u); ++c->progress_calls; c->last = p->bytes_done; };
    unpakz_result r{}; r.struct_size = sizeof r; auto err = make_error();
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_OK);
    CHECK(d.exists("o/a.txt") && !d.exists("o/b.txt") && d.exists("o/c.txt"));
    CHECK(ctx.progress_calls >= 2); CHECK_EQ(ctx.last, 2000u + 1500000u);

    o.filter = nullptr; o.progress = nullptr;
    ctx.cancel_after = 3;
    o.cancelled = [](void* u) -> int32_t { auto* c = static_cast<Ctx*>(u); return ++c->cancel_polls > c->cancel_after; };
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o2").c_str(), &o, &r, &err), UNPAKZ_CANCELLED);
    if (fsys::exists(d.path / "o2")) for (auto& e : fsys::recursive_directory_iterator(d.path / "o2")) CHECK(e.path().filename().string().rfind(".unpakz-", 0) != 0);
}

TEST(list_streams_entries_and_a_zero_return_stops) {
    Dir d;
    d.write("t.zip", make_zip({file("a.txt", "12345"), file("../x", "y"), dir("d")}));
    struct Seen { std::vector<std::u16string> names, rel; std::vector<int> kinds; int stop_at = -1; } seen;
    auto cb = [](const unpakz_entry* e, void* u) -> int32_t {
        auto* s = static_cast<Seen*>(u);
        s->names.emplace_back(e->name); s->rel.emplace_back(e->relative_path); s->kinds.push_back(e->kind);
        return static_cast<int>(s->names.size()) != s->stop_at;
    };
    auto err = make_error();
    CHECK_EQ(unpakz_list(d.u("t.zip").c_str(), nullptr, cb, &seen, &err), UNPAKZ_OK);
    CHECK_EQ(seen.names.size(), 3u);
    CHECK(seen.rel[0] == u"a.txt" && seen.rel[1].empty() && seen.kinds[2] == UNPAKZ_ENTRY_DIRECTORY);
    Seen stopped; stopped.stop_at = 2;
    CHECK_EQ(unpakz_list(d.u("t.zip").c_str(), nullptr, cb, &stopped, &err), UNPAKZ_CANCELLED);
    CHECK_EQ(stopped.names.size(), 2u);
}

TEST(check_name_exposes_the_guard) {
    char16_t out[64];
    auto err = make_error();
    CHECK_EQ(unpakz_check_name(u"dir\\sub/a.txt", out, 64, &err), UNPAKZ_OK);
    CHECK(std::u16string(out) == u"dir/sub/a.txt");
    CHECK_EQ(unpakz_check_name(u"..\\x", out, 64, &err), UNPAKZ_UNSAFE_PATH);
    CHECK_EQ(unpakz_check_name(u"dir/a.txt", out, 3, &err), UNPAKZ_INVALID_ARGUMENT);     // buffer too small
    CHECK_EQ(unpakz_check_name(nullptr, out, 64, &err), UNPAKZ_INVALID_ARGUMENT);
    CHECK_EQ(unpakz_check_name(u"a", nullptr, 64, &err), UNPAKZ_INVALID_ARGUMENT);
}

TEST(error_messages_are_always_terminated_and_truncated_safely) {
    Dir d;
    auto long_name = std::string(300, 'a') + "/" + std::string(300, 'b') + "<";
    d.write("t.zip", make_zip({file(long_name, "x")}));
    unpakz_extract_options o{}; o.struct_size = sizeof o; unpakz_default_extract_options(&o);
    unpakz_result r{}; r.struct_size = sizeof r;
    auto err = make_error();
    std::fill(std::begin(err.message), std::end(err.message), u'Z');
    CHECK_EQ(unpakz_extract(d.u("t.zip").c_str(), d.u("o").c_str(), &o, &r, &err), UNPAKZ_UNSAFE_PATH);
    bool terminated = false;
    for (auto c : err.message) if (c == u'\0') terminated = true;
    CHECK(terminated);
}

TEST(read_entry_streams_one_entry_exactly_for_every_format) {
    Dir d;
    auto tree = std::vector<Ent>{file("a.txt", txt(3000, 1)), dir("sub"), file("sub/b.bin", rnd(900000, 2)), file("e", "")};
    d.write("t.zip", make_zip(tree));
    d.write("t.tar.gz", make_targz(tree));
    struct Got { std::string bytes; int stop_after = -1; int calls = 0; } got;
    auto cb = [](const std::uint8_t* p, std::uint64_t n, void* u) -> int32_t { auto* g = static_cast<Got*>(u); g->bytes.append(reinterpret_cast<const char*>(p), n); return ++g->calls != g->stop_after; };
    for (auto name : {"t.zip", "t.tar.gz"}) {
        auto err = make_error();
        for (std::uint64_t idx = 0; idx < 4; ++idx) {
            Got g;
            auto st = unpakz_read_entry(d.u(name).c_str(), idx, nullptr, cb, &g, &err);
            CHECK_EQ(st, UNPAKZ_OK);
            const Ent& e = tree[idx];
            CHECK_EQ(g.bytes, e.type == AE_IFREG ? e.data : std::string());     // a directory yields nothing
        }
        Got stopped; stopped.stop_after = 1;
        CHECK_EQ(unpakz_read_entry(d.u(name).c_str(), 2, nullptr, cb, &stopped, &err), UNPAKZ_CANCELLED);
        CHECK_EQ(stopped.calls, 1);
        Got none;
        CHECK_EQ(unpakz_read_entry(d.u(name).c_str(), 99, nullptr, cb, &none, &err), UNPAKZ_INVALID_ARGUMENT);
        CHECK_EQ(unpakz_read_entry(d.u(name).c_str(), 0, nullptr, nullptr, &none, &err), UNPAKZ_INVALID_ARGUMENT);
        unpakz_limits small{}; small.struct_size = sizeof small; unpakz_default_limits(&small); small.max_entry_size = 100;
        CHECK_EQ(unpakz_read_entry(d.u(name).c_str(), 0, &small, cb, &none, &err), UNPAKZ_LIMIT_EXCEEDED);
    }
}

TEST(read_entry_delivers_the_bytes_before_a_checksum_failure_and_then_reports_it) {
    Dir d;
    auto zip = make_zip({file("bad.bin", rnd(4000, 5))}, false);          // stored: the payload is visible
    auto pos = std::string(zip.begin(), zip.end()).find(rnd(4000, 5).substr(1000, 50));
    zip[pos + 10] ^= 0x55;
    d.write("bad.zip", zip);
    std::size_t total = 0;
    auto cb = [](const std::uint8_t*, std::uint64_t n, void* u) -> int32_t { *static_cast<std::size_t*>(u) += n; return 1; };
    auto err = make_error();
    CHECK_EQ(unpakz_read_entry(d.u("bad.zip").c_str(), 0, nullptr, cb, &total, &err), UNPAKZ_CORRUPT);
    CHECK(total > 0);
}

UT_MAIN()
