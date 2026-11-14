// PathGuard: the single chokepoint that turns an untrusted archive entry name into a safe relative path.
// Pure (no file or OS access) and platform independent: the strictest (Windows) rules apply everywhere.
#pragma once
#include <cstddef>
#include <string>
#include <string_view>

namespace unpakz {

enum class PathRejection { None = 0, Empty, Rooted, Traversal, InvalidCharacter, ReservedName, TrailingDotOrSpace, TooLong, TooDeep };

struct GuardOptions {
    std::size_t max_path_length = 32000;
    int max_depth = 256;
    std::size_t max_segment_length = 255;
};

struct PathCheck {
    bool is_safe = false;
    PathRejection reason = PathRejection::Empty;
    std::u16string relative_path;  // '/' separated, empty when unsafe
    bool is_directory = false;
};

PathCheck validate_entry_name(std::u16string_view name, const GuardOptions& options = {});

// Strict UTF-8 -> UTF-16. Returns false for malformed input (overlong forms, surrogates, > U+10FFFF, truncation). NUL is passed through.
bool utf8_to_utf16(std::string_view in, std::u16string& out);
std::string utf16_to_utf8(std::u16string_view in);

}  // namespace unpakz
