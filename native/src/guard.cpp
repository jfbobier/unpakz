#include "guard.h"

namespace unpakz {
namespace {

bool is_sep(char16_t c) { return c == u'/' || c == u'\\'; }
bool is_ascii_letter(char16_t c) { return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z'); }
char16_t upper(char16_t c) { return (c >= u'a' && c <= u'z') ? static_cast<char16_t>(c - 32) : c; }

bool equals_ci(std::u16string_view s, std::u16string_view ascii_upper) {
    if (s.size() != ascii_upper.size()) return false;
    for (std::size_t i = 0; i < s.size(); ++i)
        if (upper(s[i]) != ascii_upper[i]) return false;
    return true;
}

PathCheck fail(PathRejection r) {
    PathCheck c;
    c.is_safe = false;
    c.reason = r;
    return c;
}

bool is_reserved_device_name(std::u16string_view seg) {
    // Windows ignores the extension and trailing spaces: "NUL.txt" and "nul " both open the NUL device.
    auto dot = seg.find(u'.');
    auto stem = dot == std::u16string_view::npos ? seg : seg.substr(0, dot);
    while (!stem.empty() && stem.back() == u' ') stem.remove_suffix(1);

    for (auto name : {std::u16string_view(u"CON"), std::u16string_view(u"PRN"), std::u16string_view(u"AUX"), std::u16string_view(u"NUL"),
                      std::u16string_view(u"CONIN$"), std::u16string_view(u"CONOUT$")})
        if (equals_ci(stem, name)) return true;

    if (stem.size() == 4 && (equals_ci(stem.substr(0, 3), u"COM") || equals_ci(stem.substr(0, 3), u"LPT"))) {
        auto d = stem[3];
        return (d >= u'1' && d <= u'9') || d == 0x00B9 || d == 0x00B2 || d == 0x00B3;
    }
    return false;
}

PathRejection check_segment(std::u16string_view seg, const GuardOptions& o) {
    for (auto c : seg)
        if (c < u' ' || c == 0x7F || c == u'<' || c == u'>' || c == u':' || c == u'"' || c == u'|' || c == u'?' || c == u'*')
            return PathRejection::InvalidCharacter;
    if (is_reserved_device_name(seg)) return PathRejection::ReservedName;
    if (seg.back() == u'.' || seg.back() == u' ') return PathRejection::TrailingDotOrSpace;
    if (seg.size() > o.max_segment_length) return PathRejection::TooLong;
    return PathRejection::None;
}

}  // namespace

PathCheck validate_entry_name(std::u16string_view name, const GuardOptions& options) {
    if (name.empty()) return fail(PathRejection::Empty);

    // Rooted: leading separator (covers UNC, \\?\ and \\.\) or a drive prefix such as "C:" / "c:foo".
    if (is_sep(name[0])) return fail(PathRejection::Rooted);
    if (name.size() >= 2 && is_ascii_letter(name[0]) && name[1] == u':') return fail(PathRejection::Rooted);

    const bool is_directory = is_sep(name.back());
    std::u16string result;
    result.reserve(name.size());
    int depth = 0;
    std::size_t start = 0;

    for (std::size_t i = 0; i <= name.size(); ++i) {
        if (i < name.size() && !is_sep(name[i])) continue;

        auto seg = name.substr(start, i - start);
        start = i + 1;

        if (seg.empty() || seg == u".") continue;
        if (seg == u"..") return fail(PathRejection::Traversal);

        if (auto reason = check_segment(seg, options); reason != PathRejection::None) return fail(reason);

        if (++depth > options.max_depth) return fail(PathRejection::TooDeep);

        if (!result.empty()) result.push_back(u'/');
        result.append(seg);
        if (result.size() > options.max_path_length) return fail(PathRejection::TooLong);
    }

    if (depth == 0) return fail(PathRejection::Empty);
    PathCheck ok;
    ok.is_safe = true;
    ok.reason = PathRejection::None;
    ok.relative_path = std::move(result);
    ok.is_directory = is_directory;
    return ok;
}

bool utf8_to_utf16(std::string_view in, std::u16string& out) {
    std::u16string r;
    r.reserve(in.size());
    std::size_t i = 0;
    while (i < in.size()) {
        const auto b0 = static_cast<unsigned char>(in[i]);
        char32_t cp;
        std::size_t len;
        if (b0 < 0x80) { cp = b0; len = 1; }
        else if (b0 >= 0xC2 && b0 <= 0xDF) { cp = b0 & 0x1Fu; len = 2; }
        else if (b0 >= 0xE0 && b0 <= 0xEF) { cp = b0 & 0x0Fu; len = 3; }
        else if (b0 >= 0xF0 && b0 <= 0xF4) { cp = b0 & 0x07u; len = 4; }
        else return false;                                   // stray continuation, C0/C1 (overlong), F5..FF
        if (i + len > in.size()) return false;
        for (std::size_t k = 1; k < len; ++k) {
            const auto b = static_cast<unsigned char>(in[i + k]);
            if ((b & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (b & 0x3Fu);
        }
        if (len == 3 && cp < 0x800) return false;            // overlong
        if (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) return false;
        if (cp >= 0xD800 && cp <= 0xDFFF) return false;      // encoded surrogate
        if (cp >= 0x10000) {
            cp -= 0x10000;
            r.push_back(static_cast<char16_t>(0xD800 + (cp >> 10)));
            r.push_back(static_cast<char16_t>(0xDC00 + (cp & 0x3FF)));
        } else {
            r.push_back(static_cast<char16_t>(cp));
        }
        i += len;
    }
    out = std::move(r);
    return true;
}

std::string utf16_to_utf8(std::u16string_view in) {
    std::string r;
    for (std::size_t i = 0; i < in.size(); ++i) {
        char32_t cp = in[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < in.size() && in[i + 1] >= 0xDC00 && in[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (in[i + 1] - 0xDC00u);
            ++i;
        } else if (cp >= 0xD800 && cp <= 0xDFFF) {
            cp = 0xFFFD;                                     // lone surrogate
        }
        if (cp < 0x80) r.push_back(static_cast<char>(cp));
        else if (cp < 0x800) { r.push_back(static_cast<char>(0xC0 | (cp >> 6))); r.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
        else if (cp < 0x10000) { r.push_back(static_cast<char>(0xE0 | (cp >> 12))); r.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); r.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
        else { r.push_back(static_cast<char>(0xF0 | (cp >> 18))); r.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F))); r.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); r.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
    }
    return r;
}

}  // namespace unpakz
