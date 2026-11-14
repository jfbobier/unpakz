// locate_volumes: port of the C# VolumeLocator tests.
#include <filesystem>
#include <fstream>

#include "support.h"

using namespace tsupport;
namespace fsys = std::filesystem;

struct VDir {
    fsys::path path;
    VDir() { path = fsys::temp_directory_path() / ("unpakz-vol-" + std::to_string(std::random_device{}())); fsys::create_directories(path); }
    ~VDir() { std::error_code ec; fsys::remove_all(path, ec); }
    void touch(const char* n) const { std::ofstream(path / n) << "x"; }
    std::u16string at(const char* n) const { return (path / n).u16string(); }
    static std::vector<std::string> names(const std::vector<std::u16string>& v) { std::vector<std::string> r; for (auto& p : v) r.push_back(fsys::path(p).filename().string()); return r; }
};

TEST(finds_every_volume_from_any_volume_in_numeric_order) {
    VDir d;
    for (auto n : {"b.part1.rar", "b.part2.rar", "b.part10.rar", "b.part3.rar", "b.part4.rar", "b.part5.rar", "b.part6.rar", "b.part7.rar", "b.part8.rar", "b.part9.rar"}) d.touch(n);
    std::vector<std::string> expected;
    for (int i = 1; i <= 10; ++i) expected.push_back("b.part" + std::to_string(i) + ".rar");
    for (auto start : {"b.part1.rar", "b.part7.rar", "b.part10.rar"}) CHECK(VDir::names(locate_volumes(d.at(start))) == expected);
}

TEST(zero_padding_does_not_matter_and_other_files_are_ignored) {
    VDir d;
    for (auto n : {"a.part01.rar", "a.part02.rar", "a.part3.rar", "other.part1.rar", "a.rar", "a.part1.zip", "a.part1.rar.bak", "a.partx.rar"}) d.touch(n);
    CHECK((VDir::names(locate_volumes(d.at("a.part02.rar"))) == std::vector<std::string>{"a.part01.rar", "a.part02.rar", "a.part3.rar"}));
}

TEST(matching_ignores_letter_case) {
    VDir d;
    d.touch("Doc.PART1.RAR"); d.touch("doc.part2.rar");
    CHECK((VDir::names(locate_volumes(d.at("doc.part2.rar"))) == std::vector<std::string>{"Doc.PART1.RAR", "doc.part2.rar"}));
}

TEST(a_neighbouring_set_never_extends_this_one) {
    VDir d;
    d.touch("a.part1.rar"); d.touch("a.part2.rar"); d.touch("b.part3.rar");
    CHECK((VDir::names(locate_volumes(d.at("a.part1.rar"))) == std::vector<std::string>{"a.part1.rar", "a.part2.rar"}));
}

TEST(a_gap_is_reported_as_the_first_missing_volume_number) {
    VDir d;
    d.touch("g.part1.rar"); d.touch("g.part2.rar"); d.touch("g.part4.rar");
    try { locate_volumes(d.at("g.part1.rar")); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::MissingVolume); CHECK_EQ(e.volume(), 3); }
}

TEST(starting_from_a_later_part_without_part_one_reports_volume_one) {
    VDir d;
    d.touch("h.part2.rar"); d.touch("h.part3.rar");
    try { locate_volumes(d.at("h.part2.rar")); CHECK(false); }
    catch (const Error& e) { CHECK(e.status() == Status::MissingVolume); CHECK_EQ(e.volume(), 1); }
}

TEST(a_plain_name_is_a_set_of_one_and_a_missing_file_is_an_io_error) {
    VDir d;
    d.touch("plain.rar"); d.touch("x.zip");
    CHECK_EQ(locate_volumes(d.at("plain.rar")).size(), 1u);
    CHECK_EQ(locate_volumes(d.at("x.zip")).size(), 1u);
    try { locate_volumes(d.at("nope.part1.rar")); CHECK(false); } catch (const Error& e) { CHECK(e.status() == Status::Io); }
}

TEST(too_many_volumes_is_a_limit_error) {
    VDir d;
    for (int i = 1; i <= 5; ++i) d.touch(("m.part" + std::to_string(i) + ".rar").c_str());
    try { locate_volumes(d.at("m.part1.rar"), 4); CHECK(false); } catch (const Error& e) { CHECK(e.status() == Status::LimitExceeded); }
    CHECK_EQ(locate_volumes(d.at("m.part1.rar"), 5).size(), 5u);
}

UT_MAIN()
