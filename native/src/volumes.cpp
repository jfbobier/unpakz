// Finds the files of a RAR multi-volume set ("name.partN.rar") from any one of them. Numbers are compared numerically, so zero padding is
// irrelevant; the volumes must be 1..N without gaps. The reader verifies order and numbering again from the numbers stored inside each
// volume, so the file names are only a way to find candidates.
#include <filesystem>
#include <map>

#include "engine.h"

namespace unpakz {
namespace {

namespace fsys = std::filesystem;

char16_t lower(char16_t c) { return (c >= u'A' && c <= u'Z') ? static_cast<char16_t>(c + 32) : c; }

std::u16string lowered(std::u16string s) {
    for (auto& c : s) c = lower(c);
    return s;
}

// name = <base>.part<digits>.rar (case-insensitive); returns the base (original case) and the number.
bool parse_part_name(const std::u16string& name, std::u16string& base, std::uint32_t& number) {
    auto l = lowered(name);
    if (l.size() < 4 || l.compare(l.size() - 4, 4, u".rar") != 0) return false;
    std::size_t end = l.size() - 4;
    std::size_t digits = 0;
    while (digits < end && l[end - digits - 1] >= u'0' && l[end - digits - 1] <= u'9') ++digits;
    if (digits < 1 || digits > 9) return false;
    std::size_t part = end - digits;
    if (part < 5 + 1 || l.compare(part - 5, 5, u".part") != 0) return false;   // needs a non-empty base before ".part"
    base = name.substr(0, part - 5);
    number = 0;
    for (std::size_t i = part; i < end; ++i) number = number * 10 + static_cast<std::uint32_t>(l[i] - u'0');
    return true;
}

}  // namespace

std::vector<std::u16string> locate_volumes(const std::u16string& path, std::uint32_t max_volumes) {
    std::error_code ec;
    auto full = fsys::absolute(fsys::path(path), ec);
    if (ec || !fsys::is_regular_file(full, ec)) throw Error(Status::Io, "archive not found");

    std::u16string base;
    std::uint32_t number = 0;
    if (!parse_part_name(full.filename().u16string(), base, number)) return {full.u16string()};

    const auto lbase = lowered(base);
    std::map<std::uint32_t, std::u16string> by_number;
    for (fsys::directory_iterator it(full.parent_path(), ec), end; !ec && it != end; it.increment(ec)) {
        std::u16string b;
        std::uint32_t n = 0;
        if (!parse_part_name(it->path().filename().u16string(), b, n) || lowered(b) != lbase || n < 1) continue;
        by_number[n] = it->path().u16string();     // two files with the same number (different padding): the last one wins
        if (by_number.size() > max_volumes) throw Error(Status::LimitExceeded, "too many volumes");
    }

    std::uint32_t expected = 1;
    for (auto& [n, p] : by_number) {
        if (n != expected) throw Error(Status::MissingVolume, "the set is incomplete: volume " + std::to_string(expected) + " is missing", static_cast<int>(expected));
        ++expected;
    }
    std::vector<std::u16string> r;
    for (auto& [n, p] : by_number) r.push_back(p);
    return r;
}

}  // namespace unpakz
