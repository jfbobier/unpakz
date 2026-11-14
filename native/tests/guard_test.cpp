// Ported from tests/Unpakz.Tests/PathGuardTests.cs (the C# suite is the executable spec).
#include <random>
#include "../src/guard.h"
#include "test.h"

using namespace unpakz;

static std::u16string U(const char* utf8) {
    std::u16string out;
    CHECK(utf8_to_utf16(utf8, out));
    return out;
}
static std::u16string U(std::string_view utf8) { std::u16string o; CHECK(utf8_to_utf16(utf8, o)); return o; }
static PathCheck V(const char* s, const GuardOptions& o = {}) { return validate_entry_name(U(s), o); }

TEST(accepts_safe_names) {
    struct { const char* in; const char* out; } cases[] = {
        {"a.txt", "a.txt"}, {"dir/a.txt", "dir/a.txt"}, {"dir\\sub\\a.txt", "dir/sub/a.txt"}, {"./a.txt", "a.txt"},
        {"dir//a.txt", "dir/a.txt"}, {"dir/.hidden", "dir/.hidden"}, {"a..b/c", "a..b/c"}, {"console.txt", "console.txt"},
        {"CONSOLE", "CONSOLE"}, {"h\xC3\xA9llo/\xE6\x97\xA5\xE6\x9C\xAC\xE8\xAA\x9E.txt", "h\xC3\xA9llo/\xE6\x97\xA5\xE6\x9C\xAC\xE8\xAA\x9E.txt"},
    };
    for (auto& c : cases) {
        auto r = V(c.in);
        CHECK(r.is_safe);
        CHECK_EQ(r.relative_path, U(c.out));
    }
}

TEST(directory_entry_with_trailing_slash_is_accepted_and_flagged) {
    auto r = V("dir/sub/");
    CHECK(r.is_safe);
    CHECK(r.is_directory);
    CHECK_EQ(r.relative_path, U("dir/sub"));
}

TEST(rejects_parent_traversal) {
    for (auto s : {"../evil.txt", "..\\evil.txt", "a/../../evil.txt", "a/b/../../../evil.txt", "..", "a/.."}) {
        CHECK(V(s).reason == PathRejection::Traversal);
        CHECK(!V(s).is_safe);
    }
}

TEST(rejects_absolute_and_rooted) {
    for (auto s : {"/etc/passwd", "\\windows\\system32\\x.dll", "C:\\windows\\x.dll", "C:/windows/x.dll", "c:evil.txt",
                   "\\\\server\\share\\x", "//server/share/x", "\\\\?\\C:\\x", "\\\\.\\PhysicalDrive0"})
        CHECK(V(s).reason == PathRejection::Rooted);
}

TEST(rejects_reserved_device_names) {
    for (auto s : {"CON", "con", "NUL.txt", "dir/PRN", "aux.tar.gz", "COM1", "com9.log", "LPT3", "COM\xC2\xB9", "CONIN$", "CONOUT$",
                   "nul ", "NUL  .txt"})
        CHECK(V(s).reason == PathRejection::ReservedName);
    CHECK(V("COM0").is_safe);       // COM0 and LPT0 are not reserved
    CHECK(V("COM10").is_safe);
}

TEST(rejects_colon_in_names) {
    for (auto s : {"file.txt:stream", "file.txt::$DATA", "dir:evil/a.txt"}) CHECK(V(s).reason == PathRejection::InvalidCharacter);
}

TEST(rejects_invalid_filename_characters) {
    for (auto s : {"a<b", "a>b", "a\"b", "a|b", "a?b", "a*b", "a\x01" "b", "a\x1F" "b", "a\x7F" "b"}) CHECK(V(s).reason == PathRejection::InvalidCharacter);
    CHECK(validate_entry_name(std::u16string(u"a\0b", 3)).reason == PathRejection::InvalidCharacter);
}

TEST(rejects_trailing_dot_or_space) {
    for (auto s : {"a.", "a ", "dir./a.txt", "dir /a.txt", "a/b..."}) CHECK(V(s).reason == PathRejection::TrailingDotOrSpace);
}

TEST(rejects_empty_results) {
    for (auto s : {"", "/", ".", "./"}) {
        auto r = V(s);
        CHECK(!r.is_safe);
        CHECK(r.reason == PathRejection::Empty || r.reason == PathRejection::Rooted);
    }
}

TEST(rejects_segment_longer_than_255) {
    CHECK(validate_entry_name(std::u16string(255, u'a')).is_safe);
    CHECK(validate_entry_name(std::u16string(256, u'a')).reason == PathRejection::TooLong);
}

TEST(rejects_total_length_over_limit) {
    GuardOptions o; o.max_path_length = 20;
    CHECK(V("aaaaaaaaaa/bbbbbbbb", o).is_safe);                 // 19
    CHECK(V("aaaaaaaaaa/bbbbbbbbbbbb", o).reason == PathRejection::TooLong);
    CHECK(V("aaaaaaaaaa/bbbbbbbbb", o).is_safe);                // exactly 20
    CHECK(V("aaaaaaaaaa/bbbbbbbbbb", o).reason == PathRejection::TooLong);   // 21
}

TEST(rejects_excessive_depth) {
    GuardOptions o; o.max_depth = 3;
    CHECK(V("a/b/c", o).is_safe);
    CHECK(V("a/b/c/d", o).reason == PathRejection::TooDeep);
}

TEST(safe_results_never_contain_dangerous_content) {
    std::mt19937 rng(12345);
    const std::u16string alphabet = std::u16string(u"ab./\\:*?<>| ") + std::u16string(1, u'\0') + u"CONcom1~\u00B9\u00E9";
    for (int i = 0; i < 200000; i++) {
        std::u16string in(rng() % 24, u'x');
        for (auto& c : in) c = alphabet[rng() % alphabet.size()];
        auto r = validate_entry_name(in);
        if (!r.is_safe) continue;
        CHECK(r.relative_path.find(u'\\') == std::u16string::npos);
        CHECK(r.relative_path.find(u':') == std::u16string::npos);
        CHECK(!r.relative_path.empty() && r.relative_path.front() != u'/');
        std::size_t start = 0;
        while (start <= r.relative_path.size()) {
            auto end = r.relative_path.find(u'/', start);
            if (end == std::u16string::npos) end = r.relative_path.size();
            auto seg = r.relative_path.substr(start, end - start);
            CHECK(!seg.empty() && seg != u"." && seg != u"..");
            CHECK(seg.back() != u'.' && seg.back() != u' ');
            start = end + 1;
        }
    }
}

TEST(validation_is_idempotent_for_safe_names) {
    for (auto s : {"a/b.txt", "dir\\x\\y", "./q/./r"}) {
        auto once = V(s);
        auto twice = validate_entry_name(once.relative_path);
        CHECK_EQ(once.relative_path, twice.relative_path);
    }
}

TEST(utf8_decoder_is_strict) {
    std::u16string o;
    CHECK(utf8_to_utf16("plain", o)); CHECK_EQ(o, std::u16string(u"plain"));
    CHECK(utf8_to_utf16("\xF0\x9F\x98\x80", o)); CHECK_EQ(o.size(), 2u);                  // surrogate pair
    CHECK(!utf8_to_utf16("\xC0\xAF", o));                                                 // overlong '/'
    CHECK(!utf8_to_utf16("\xE0\x80\xAF", o));                                             // overlong 3-byte
    CHECK(!utf8_to_utf16("\xED\xA0\x80", o));                                             // encoded surrogate
    CHECK(!utf8_to_utf16("\xF4\x90\x80\x80", o));                                         // > U+10FFFF
    CHECK(!utf8_to_utf16("\xF8\x88\x80\x80\x80", o));                                     // 5-byte form
    CHECK(!utf8_to_utf16("\xE2\x82", o));                                                 // truncated
    CHECK(!utf8_to_utf16("\x80", o));                                                     // stray continuation
    CHECK(!utf8_to_utf16("\xC3\x28", o));                                                // lead byte followed by a non-continuation
    CHECK(!utf8_to_utf16("\xE2\x28\xA1", o));
    CHECK(!utf8_to_utf16("\xF0\x28\x8C\xBC", o));
    CHECK(!utf8_to_utf16("\xF5\x80\x80\x80", o));                                       // lead byte beyond F4
    CHECK(utf8_to_utf16(std::string_view("a\0b", 3), o)); CHECK_EQ(o.size(), 3u);         // NUL passes through to the guard
    CHECK_EQ(utf16_to_utf8(U("h\xC3\xA9llo\xF0\x9F\x98\x80")), std::string("h\xC3\xA9llo\xF0\x9F\x98\x80"));
}

UT_MAIN()
