// Executable spec for the extraction engine (security, single-write, limits, formats). Mirrors the C# ArchiveExtractorTests.
#include <cstdlib>
#include <filesystem>
#include <fstream>

#include "support.h"

using namespace tsupport;

static std::vector<Ent> sample_tree() {
    return {file("a.txt", txt(5000, 1)), dir("dir"), file("dir/b.bin", rnd(70000, 2)), file("dir/sub/c.txt", txt(100, 3)), dir("emptydir"), file("empty", "")};
}

static void check_sample(MemoryFs& fs, const std::vector<Ent>& tree) {
    for (auto& e : tree) {
        if (e.type != AE_IFREG) continue;
        CHECK(fs.has_file(P(e.name.c_str())));
        if (fs.has_file(P(e.name.c_str()))) CHECK_EQ(fs.text(P(e.name.c_str())), e.data);
    }
}

TEST(extracts_zip_tree_exactly) {
    auto tree = sample_tree();
    for (bool deflate : {true, false}) {
        MemoryFs fs;
        auto r = run(make_zip(tree, deflate), fs);
        check_sample(fs, tree);
        CHECK(fs.dirs.count(fold(P("emptydir"))) == 1);
        CHECK_EQ(r.files, 4u);
        CHECK(fs.leftover_temps().empty());
    }
}

TEST(extracts_tar_gz_tree_exactly) {
    auto tree = sample_tree();
    MemoryFs fs;
    run(make_targz(tree), fs);
    check_sample(fs, tree);
    CHECK(fs.dirs.count(fold(P("emptydir"))) == 1);
}

TEST(unicode_names_survive) {
    std::vector<Ent> tree = {file("h\xC3\xA9llo/\xE6\x97\xA5\xE6\x9C\xAC.txt", "unicode")};
    for (bool zip : {true, false}) {
        MemoryFs fs;
        run(zip ? make_zip(tree) : make_targz(tree), fs);
        CHECK(fs.has_file(P("h\xC3\xA9llo/\xE6\x97\xA5\xE6\x9C\xAC.txt")));
    }
}

TEST(one_write_per_byte_and_temp_is_in_the_final_directory) {
    auto tree = sample_tree();
    std::uint64_t total = 0;
    for (auto& e : tree) total += e.data.size();
    for (bool zip : {true, false}) {
        MemoryFs fs;
        run(zip ? make_zip(tree) : make_targz(tree), fs);
        CHECK_EQ(fs.bytes_written, total);                                  // each byte written exactly once
        CHECK_EQ(fs.commits, 4);
        for (auto& [temp, final_path] : fs.temp_files) {
            auto parent = [](const std::u16string& p) { return p.substr(0, p.find_last_of(u"/\\")); };
            CHECK(parent(temp) == parent(final_path));                      // same directory: rename, never copy
        }
    }
}

TEST(only_the_expected_directories_are_created) {
    MemoryFs fs;
    run(make_zip({file("a/b/c.txt", "x")}), fs);
    CHECK_EQ(fs.created_dirs.size(), 3u);                                   // dest, a, a/b: no scratch directories
}

TEST(path_traversal_and_rooted_names_are_refused_before_any_write) {
    const char* bad[] = {"../evil.txt", "a/../../evil.txt", "a/..", "/etc/passwd", "C:\\x.dll", "c:evil", "\\\\server\\share\\x", "//server/x",
                         "f.txt:stream", "dir/CON", "NUL.txt", "trail.", "sp ace /x", "a<b", "q?"};
    for (auto name : bad) {
        for (bool zip : {true, false}) {
            MemoryFs fs;
            auto bytes = zip ? make_zip({file("ok.txt", "fine"), file(name, "evil")}) : make_targz({file("ok.txt", "fine"), file(name, "evil")});
            CHECK(throws(Status::UnsafePath, [&] { run(bytes, fs); }));
            CHECK_EQ(fs.bytes_written, 0u);                                 // the good file before it was not written either
            CHECK_EQ(fs.final_file_count(), 0u);
            CHECK(fs.temp_files.empty());
        }
    }
}

TEST(duplicate_names_and_case_collisions_are_conflicts) {
    for (bool zip : {true, false}) {
        for (auto second : {"a.txt", "A.TXT"}) {
            MemoryFs fs;
            auto bytes = zip ? make_zip({file("a.txt", "1"), file(second, "2")}) : make_targz({file("a.txt", "1"), file(second, "2")});
            CHECK(throws(Status::Conflict, [&] { run(bytes, fs); }));
            CHECK_EQ(fs.bytes_written, 0u);
        }
    }
}

TEST(file_and_directory_collisions_are_conflicts) {
    MemoryFs a;
    CHECK(throws(Status::Conflict, [&] { run(make_targz({file("x", "1"), file("x/y", "2")}), a); }));
    MemoryFs b;
    CHECK(throws(Status::Conflict, [&] { run(make_targz({dir("x"), file("x", "2")}), b); }));
    CHECK_EQ(a.bytes_written + b.bytes_written, 0u);
}

TEST(symlinks_and_hardlinks_are_skipped_and_never_created) {
    Ent hard = file("hard", ""); hard.link = "a.txt";
    MemoryFs fs;
    auto r = run(make_targz({file("a.txt", "data"), symlink_to("link", "/etc/passwd"), hard}), fs);
    CHECK(fs.has_file(P("a.txt")));
    CHECK(!fs.has_file(P("link")) && !fs.has_file(P("hard")));
    CHECK(fs.links.empty());
    CHECK_EQ(r.skipped.size(), 2u);
    CHECK(r.skipped[0].reason == SkipReason::Link);
    CHECK(r.skipped[1].reason == SkipReason::Link);       // a hard link is a link, not a special file
}

TEST(existing_files_follow_the_overwrite_policy_and_preflight_refuses_up_front) {
    auto zip = make_zip({file("a.txt", "NEW"), file("b.txt", "NEW")});
    {
        MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("b.txt"), {'O', 'L', 'D'});
        CHECK(throws(Status::Conflict, [&] { run(zip, fs); }));
        CHECK(!fs.has_file(P("a.txt")));                                    // the conflict on the LATER file was found before a.txt was written
        CHECK_EQ(fs.text(P("b.txt")), std::string("OLD"));
        CHECK_EQ(fs.bytes_written, 0u);
    }
    {
        MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("b.txt"), {'O', 'L', 'D'});
        Options o; o.overwrite = OverwritePolicy::Skip;
        auto r = run(zip, fs, o);
        CHECK_EQ(fs.text(P("b.txt")), std::string("OLD"));
        CHECK_EQ(fs.text(P("a.txt")), std::string("NEW"));
        CHECK_EQ(r.skipped.size(), 1u);
        CHECK(r.skipped[0].reason == SkipReason::AlreadyExists);
    }
    {
        MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("b.txt"), {'O', 'L', 'D'});
        Options o; o.overwrite = OverwritePolicy::Overwrite;
        run(zip, fs, o);
        CHECK_EQ(fs.text(P("b.txt")), std::string("NEW"));
    }
}

TEST(directory_where_a_file_should_go_is_a_conflict_even_with_overwrite) {
    MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.dirs.insert(fold(P("a.txt")));
    Options o; o.overwrite = OverwritePolicy::Overwrite;
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a.txt", "x")}), fs, o); }));
    CHECK(fs.temp_files.empty());
}

TEST(file_that_appears_after_the_check_is_never_overwritten) {
    MemoryFs fs;
    fs.on_create_file = [&](const std::u16string& f) { if (f == P("a.txt")) fs.put_file(P("a.txt"), {'R', 'A', 'C', 'E'}); };
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a.txt", "NEW")}), fs); }));
    CHECK_EQ(fs.text(P("a.txt")), std::string("RACE"));
    CHECK(fs.leftover_temps().empty());
}

TEST(late_appearing_file_for_a_later_entry_is_refused_before_decoding_it) {
    MemoryFs fs;
    fs.on_create_file = [&](const std::u16string& f) { if (f == P("a.txt")) fs.put_file(P("b.txt"), {'R'}); };
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a.txt", "1"), file("b.txt", "2")}), fs); }));
    for (auto& t : fs.temp_files) CHECK(t.second != P("b.txt"));
    CHECK_EQ(fs.text(P("b.txt")), std::string("R"));
}

TEST(reparse_point_in_the_destination_path_is_refused) {
    MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.dirs.insert(fold(P("sub"))); fs.links.insert(fold(P("sub")));
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("sub/a.txt", "x")}), fs); }));
    CHECK(!fs.has_file(P("sub/a.txt")));
}

TEST(a_link_at_the_final_path_is_replaced_not_followed) {
    MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.links.insert(fold(P("a.txt")));
    Options o; o.overwrite = OverwritePolicy::Overwrite;
    run(make_zip({file("a.txt", "data")}), fs, o);
    CHECK(fs.links.empty());
    CHECK_EQ(fs.text(P("a.txt")), std::string("data"));
}

TEST(limits_are_enforced_before_writing) {
    auto zip = make_zip({file("a", "12345"), file("b", "12345"), file("c", "12345")});
    { MemoryFs fs; Options o; o.limits.max_entries = 2; CHECK(throws(Status::LimitExceeded, [&] { run(zip, fs, o); })); CHECK_EQ(fs.bytes_written, 0u); }
    { MemoryFs fs; Options o; o.limits.max_entries = 3; run(zip, fs, o); CHECK_EQ(fs.final_file_count(), 3u); }
    { MemoryFs fs; Options o; o.limits.max_entry_size = 4; CHECK(throws(Status::LimitExceeded, [&] { run(zip, fs, o); })); CHECK_EQ(fs.bytes_written, 0u); CHECK(fs.temp_files.empty()); }
    { MemoryFs fs; Options o; o.limits.max_entry_size = 5; run(zip, fs, o); }
    { MemoryFs fs; Options o; o.limits.max_total_size = 14; CHECK(throws(Status::LimitExceeded, [&] { run(zip, fs, o); })); CHECK_EQ(fs.bytes_written, 0u); CHECK(fs.temp_files.empty()); }
    { MemoryFs fs; Options o; o.limits.max_total_size = 15; run(zip, fs, o); }
}

TEST(guard_limits_apply_to_names) {
    MemoryFs fs;
    Options o; o.limits.guard.max_depth = 2;
    CHECK(throws(Status::UnsafePath, [&] { run(make_zip({file("a/b/c", "x")}), fs, o); }));
}

TEST(corrupt_data_never_leaves_a_final_file_or_a_temp) {
    auto zip = make_zip({file("good.txt", txt(2000, 4)), file("bad.bin", rnd(4000, 5))}, false);   // stored: data is visible in the bytes
    auto pos = std::string(zip.begin(), zip.end()).find(rnd(4000, 5).substr(1000, 50));
    CHECK(pos != std::string::npos);
    zip[pos + 10] ^= 0x55;                                                  // flip a payload byte: the CRC no longer matches
    MemoryFs fs;
    CHECK(throws(Status::Corrupt, [&] { run(zip, fs); }));
    CHECK(!fs.has_file(P("bad.bin")));
    CHECK(fs.leftover_temps().empty());
}

TEST(truncation_is_a_typed_error_everywhere) {
    for (bool zip : {true, false}) {
        auto bytes = zip ? make_zip(sample_tree()) : make_targz(sample_tree());
        for (std::size_t len = 0; len < bytes.size(); len += (bytes.size() / 97) + 1) {
            std::vector<std::uint8_t> cut(bytes.begin(), bytes.begin() + static_cast<std::ptrdiff_t>(len));
            MemoryFs fs;
            try { run(cut, fs); }
            catch (const Error&) {}                                          // a typed error is fine; any other exception or a crash is not
            CHECK(fs.leftover_temps().empty());
        }
    }
}

TEST(cancellation_removes_the_partial_file) {
    MemoryFs fs;
    int calls = 0;
    Options o;
    o.cancelled = [&] { return ++calls > 3; };
    CHECK(throws(Status::Cancelled, [&] { run(make_zip({file("big", rnd(3'000'000, 9))}), fs, o); }));
    CHECK(fs.leftover_temps().empty());
    CHECK(!fs.has_file(P("big")));
}

TEST(selection_filter_extracts_only_selected_entries) {
    MemoryFs fs;
    Options o;
    o.filter = [](const EntryInfo& e) { return e.name == u"a.txt" || e.name.rfind(u"dir/", 0) == 0; };
    run(make_targz(sample_tree()), fs, o);
    CHECK(fs.has_file(P("a.txt")) && fs.has_file(P("dir/b.bin")) && !fs.has_file(P("empty")));
}

TEST(timestamps_are_restored_by_the_sink_unless_disabled) {
    Ent a = file("a.txt", "x"); a.mtime = 1600000000;
    for (bool zip : {true, false}) {
        MemoryFs fs;
        run(zip ? make_zip({a}) : make_targz({a}), fs);
        CHECK(fs.mtimes.at(fold(P("a.txt"))).has_value());
        CHECK_EQ(fs.mtimes.at(fold(P("a.txt")))->unix_seconds, 1600000000);
        MemoryFs off; Options o; o.restore_timestamps = false;
        run(zip ? make_zip({a}) : make_targz({a}), off, o);
        CHECK(!off.mtimes.at(fold(P("a.txt"))).has_value());
    }
}

TEST(commit_replaces_only_when_the_policy_says_so) {
    for (auto policy : {OverwritePolicy::Fail, OverwritePolicy::Skip, OverwritePolicy::Overwrite}) {
        MemoryFs fs; Options o; o.overwrite = policy;
        run(make_zip({file("a.txt", "x")}), fs, o);
        CHECK_EQ(fs.overwrite_flags.at(fold(P("a.txt"))), policy == OverwritePolicy::Overwrite);
    }
}

TEST(actual_size_limits_apply_when_the_header_declares_no_size) {
    Ent big = file("big", std::string(5, 'x')); big.unknown_size = true;
    Ent small = file("small", std::string(5, 'y')); small.unknown_size = true;
    { MemoryFs fs; Options o; o.limits.max_entry_size = 4; CHECK(throws(Status::LimitExceeded, [&] { run(make_zip({big}), fs, o); })); CHECK(!fs.has_file(P("big"))); CHECK(fs.leftover_temps().empty()); }
    { MemoryFs fs; Options o; o.limits.max_entry_size = 5; run(make_zip({big}), fs, o); CHECK(fs.has_file(P("big"))); }
    { MemoryFs fs; Options o; o.limits.max_total_size = 9; CHECK(throws(Status::LimitExceeded, [&] { run(make_zip({big, small}), fs, o); })); }
    { MemoryFs fs; Options o; o.limits.max_total_size = 10; run(make_zip({big, small}), fs, o); CHECK_EQ(fs.final_file_count(), 2u); }
}

TEST(without_the_preflight_pass_the_same_problems_are_still_refused_while_writing) {
    Options o; o.preflight_pass = false;
    {   MemoryFs fs; CHECK(throws(Status::UnsafePath, [&] { run(make_zip({file("../x", "y")}), fs, o); })); CHECK_EQ(fs.bytes_written, 0u); }
    {   MemoryFs fs; CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a", "1"), file("A", "2")}), fs, o); })); CHECK(!fs.has_file(P("A")) || fs.text(P("a")) == "1"); }
    {   MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.dirs.insert(fold(P("sub"))); fs.links.insert(fold(P("sub")));
        CHECK(throws(Status::Conflict, [&] { run(make_zip({file("sub/a.txt", "x")}), fs, o); })); CHECK(!fs.has_file(P("sub/a.txt"))); }
    {   MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("sub"), {'f'});
        CHECK(throws(Status::Conflict, [&] { run(make_zip({file("sub/a.txt", "x")}), fs, o); })); }
    {   MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("a.txt"), {'O'});
        CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a.txt", "N")}), fs, o); })); CHECK_EQ(fs.text(P("a.txt")), std::string("O")); }
    {   MemoryFs fs; auto r = run(make_targz(sample_tree()), fs, o); CHECK_EQ(r.files, 4u); }
}

TEST(the_destination_may_not_be_a_file_and_missing_parents_are_created) {
    MemoryFs a; a.put_file(kDest, {'x'});
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a", "1")}), a); }));
    MemoryFs b;
    Options o; o.file_system = &b;
    auto bytes = make_zip({file("a", "1")});
    auto deep = kDest + kNativeSeparator + u"x" + kNativeSeparator + u"y";
    extract_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()), deep, o);
    CHECK(b.dirs.count(fold(kDest + kNativeSeparator + u"x")) == 1);
}

TEST(progress_reports_monotonic_totals_and_names) {
    MemoryFs fs; Options o;
    std::uint64_t last = 0; int calls = 0; bool names_ok = true;
    o.progress = [&](const Progress& p) { if (p.bytes_done < last) names_ok = false; last = p.bytes_done; ++calls; if (!p.current || p.current->empty()) names_ok = false; if (p.bytes_total < p.bytes_done) names_ok = false; };
    run(make_zip({file("a", rnd(3000)), file("b", rnd(2000))}), fs, o);
    CHECK(calls >= 2 && names_ok);
    CHECK_EQ(last, 5000u);
}

TEST(the_archive_is_re_validated_between_the_two_passes) {
    // A different archive appears at the same path after the first pass: the second pass must notice.
    namespace fsys = std::filesystem;
    auto dir = fsys::temp_directory_path() / "unpakz-toctou";
    fsys::create_directories(dir);
    auto path = dir / "a.zip";
    auto write = [&](const std::vector<std::uint8_t>& b) { auto tmp = path; tmp += ".new"; { std::ofstream f(tmp, std::ios::binary); f.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size())); } fsys::rename(tmp, path); };
    write(make_zip({file("a.txt", "one"), file("b.txt", "two")}));
    MemoryFs fs; Options o; o.file_system = &fs;
    int calls = 0;
    o.filter = [&](const EntryInfo&) { if (++calls == 2) write(make_zip({file("a.txt", "one"), file("evil.txt", "two")})); return true; };
    bool refused = false;
    try { extract_archive(ArchiveSource::from_file(path.u16string()), kDest, o); } catch (const Error& e) { refused = e.status() == Status::Corrupt; }
    CHECK(refused);
    CHECK(!fs.has_file(P("evil.txt")));
    fsys::remove_all(dir);
}

TEST(extraction_from_a_real_file_works_and_a_missing_file_is_an_io_error) {
    namespace fsys = std::filesystem;
    auto dir = fsys::temp_directory_path() / "unpakz-file-source";
    fsys::create_directories(dir);
    auto bytes = make_zip(sample_tree());
    auto path = dir / "s.zip";
    { std::ofstream f(path, std::ios::binary); f.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size())); }
    MemoryFs fs; Options o; o.file_system = &fs;
    auto r = extract_archive(ArchiveSource::from_file(path.u16string()), kDest, o);
    CHECK_EQ(r.files, 4u);
    CHECK(throws(Status::Io, [&] { extract_archive(ArchiveSource::from_file((dir / "nope.zip").u16string()), kDest, o); }));
    fsys::remove_all(dir);
}

TEST(list_reports_names_sizes_and_refused_names) {
    auto bytes = make_zip({file("a.txt", "12345"), file("../x", "y"), dir("d")});
    auto entries = list_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()));
    CHECK_EQ(entries.size(), 3u);
    CHECK(entries[0].relative_path == u"a.txt" && entries[0].size == 5);
    CHECK(entries[1].relative_path.empty());
    CHECK(entries[2].kind == EntryKind::Directory);
}

TEST(garbage_is_unsupported_or_corrupt_not_a_crash) {
    MemoryFs fs;
    std::vector<std::uint8_t> junk(5000);
    std::mt19937 g(3);
    for (auto& b : junk) b = static_cast<std::uint8_t>(g());
    try { run(junk, fs); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::Unsupported || e.status() == Status::Corrupt); }
    std::vector<std::uint8_t> empty;
    try { run(empty, fs); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::Unsupported || e.status() == Status::Corrupt); }
}

TEST(special_files_are_skipped_and_never_created) {
    Ent fifo; fifo.name = "pipe"; fifo.type = AE_IFIFO;
    MemoryFs fs;
    auto r = run(make_targz({file("a.txt", "x"), fifo}), fs);
    CHECK(!fs.has_file(P("pipe")) && fs.has_file(P("a.txt")));
    CHECK_EQ(r.skipped.size(), 1u);
    CHECK(r.skipped[0].reason == SkipReason::Special);
}

TEST(a_directory_after_a_file_of_the_same_name_is_a_conflict) {
    MemoryFs fs;
    CHECK(throws(Status::Conflict, [&] { run(make_targz({file("x", "1"), dir("x")}), fs); }));
    CHECK_EQ(fs.bytes_written, 0u);
}

TEST(preflight_finds_a_file_or_link_in_the_way_before_writing_anything) {
    auto zip = make_zip({file("first.txt", "1"), file("sub/b.txt", "2")});
    {   MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.put_file(P("sub"), {'f'});
        CHECK(throws(Status::Conflict, [&] { run(zip, fs); })); CHECK_EQ(fs.bytes_written, 0u); CHECK(fs.temp_files.empty()); }
    {   MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.dirs.insert(fold(P("sub"))); fs.links.insert(fold(P("sub")));
        CHECK(throws(Status::Conflict, [&] { run(zip, fs); })); CHECK_EQ(fs.bytes_written, 0u); CHECK(fs.temp_files.empty()); }
}

TEST(a_directory_that_appears_after_the_preflight_is_refused_at_write_time) {
    MemoryFs fs;
    fs.on_create_file = [&](const std::u16string& f) { if (f == P("a.txt")) fs.dirs.insert(fold(P("b.txt"))); };
    Options o; o.overwrite = OverwritePolicy::Overwrite;
    CHECK(throws(Status::Conflict, [&] { run(make_zip({file("a.txt", "1"), file("b.txt", "2")}), fs, o); }));
    for (auto& t : fs.temp_files) CHECK(t.second != P("b.txt"));
}

TEST(an_archive_that_loses_entries_between_the_passes_is_refused) {
    namespace fsys = std::filesystem;
    auto dir = fsys::temp_directory_path() / "unpakz-toctou2";
    fsys::create_directories(dir);
    auto path = dir / "a.zip";
    auto write = [&](const std::vector<std::uint8_t>& b) { auto tmp = path; tmp += ".new"; { std::ofstream f(tmp, std::ios::binary); f.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size())); } fsys::rename(tmp, path); };
    write(make_zip({file("a.txt", "one"), file("b.txt", "two")}));
    MemoryFs fs; Options o; o.file_system = &fs;
    int calls = 0;
    o.filter = [&](const EntryInfo&) { if (++calls == 2) write(make_zip({file("a.txt", "one")})); return true; };
    bool refused = false;
    try { extract_archive(ArchiveSource::from_file(path.u16string()), kDest, o); } catch (const Error& e) { refused = e.status() == Status::Corrupt; }
    CHECK(refused);
    fsys::remove_all(dir);
}

TEST(a_destination_that_is_itself_a_link_is_allowed_and_not_recreated) {
    MemoryFs fs; fs.dirs.insert(fold(kDest)); fs.links.insert(fold(kDest));
    run(make_zip({file("a.txt", "x")}), fs);
    CHECK(fs.has_file(P("a.txt")));
    CHECK(fs.create_dir_calls.empty());
}

TEST(a_destination_that_is_a_file_is_reported_as_such) {
    MemoryFs fs; fs.put_file(kDest, {'x'});
    try { run(make_zip({file("a", "1")}), fs); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::Conflict); CHECK(std::string(e.what()).find("destination is a file") != std::string::npos); }
}

TEST(an_empty_destination_is_refused_and_nothing_is_written) {
    MemoryFs fs;
    auto bytes = make_zip({file("a", "1")});
    Options o; o.file_system = &fs;
    CHECK(throws(Status::InvalidArgument, [&] { extract_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()), u"", o); }));
    CHECK_EQ(fs.bytes_written, 0u);
}

TEST(files_are_presized_only_when_the_declared_size_is_modest) {
    MemoryFs fs; fs.discard_data = true;
    run(make_zip({file("small", txt(1234))}), fs);
    CHECK_EQ(fs.preallocations.size(), 1u);
    CHECK_EQ(fs.preallocations[0], 1234u);
    MemoryFs big; big.discard_data = true;
    Ent huge = file("huge", std::string(300u << 20, '\0'));      // 300 MiB of zeros: tiny once deflated, over the pre-size limit
    run(make_zip({huge}), big);
    CHECK_EQ(big.preallocations[0], 0u);
    CHECK_EQ(big.bytes_written, 300ull << 20);
}

TEST(list_enforces_the_entry_limit) {
    auto bytes = make_zip({file("a", "1"), file("b", "2"), file("c", "3")});
    Limits l; l.max_entries = 2;
    CHECK(throws(Status::LimitExceeded, [&] { list_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()), l); }));
    l.max_entries = 3;
    CHECK_EQ(list_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()), l).size(), 3u);
}

TEST(lying_central_directory_sizes_never_produce_wrong_data) {
    // Whatever libarchive does with sizes that disagree with the data, the result must be a typed error or the exact original bytes.
    std::string data = txt(5000, 7);
    auto zip = make_zip({file("a.txt", data)}, false);
    std::size_t cd = 0;
    for (std::size_t i = 0; i + 4 < zip.size(); ++i) if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 1 && zip[i + 3] == 2) cd = i;
    CHECK(cd != 0);
    for (auto [csz, usz] : std::vector<std::pair<std::uint32_t, std::uint32_t>>{{100, 100}, {5000, 100}, {100, 5000}, {9000, 9000}, {5000, 9000}, {5000, 0}, {0, 5000}, {0xFFFFFFFFu, 0xFFFFFFFFu}}) {
        auto z = zip;
        auto put = [&](std::size_t off, std::uint32_t v) { for (int k = 0; k < 4; k++) z[off + k] = static_cast<std::uint8_t>((v >> (8 * k)) & 0xff); };
        put(cd + 20, csz); put(cd + 24, usz);
        MemoryFs fs;
        try {
            run(z, fs);
            CHECK(false);                                              // every one of these lies about the data
        } catch (const Error& e) {
            CHECK(e.status() == Status::Corrupt);
            if (csz == 0 && usz == 5000) CHECK(std::string(e.what()).find("shorter than its header declares") != std::string::npos);   // our own check, not libarchive's
        }
        CHECK(!fs.has_file(P("a.txt")));
        CHECK(fs.leftover_temps().empty());
    }
}

TEST(zip64_entries_larger_than_4_gib_extract_completely) {
    // Opt-in (several seconds, builds a 5 GiB entry of zeros): UNPAKZ_BIG=1. Proves there is no 2 GiB / 4 GiB limit anywhere.
    if (!std::getenv("UNPAKZ_BIG")) return;
    const std::uint64_t size = 5ull << 30;
    auto zip = make_zip_huge("huge.bin", size);
    MemoryFs fs; fs.discard_data = true;
    Options o; o.limits.max_entry_size = 1ull << 40;
    auto r = run(zip, fs, o);
    CHECK_EQ(fs.bytes_written, size);
    CHECK_EQ(r.bytes, size);
    CHECK_EQ(r.files, 1u);
}

UT_MAIN()
