// Extraction engine: plan -> pre-flight -> write. Format readers come from libarchive (read side only); every name goes
// through the guard, every limit is enforced here, and every output file goes through IFileSystem.
#pragma once
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "errors.h"
#include "fs.h"
#include "guard.h"

namespace unpakz {

struct Limits {
    std::uint64_t max_entries = 1'000'000;
    std::uint64_t max_entry_size = 256ull << 30;   // 256 GiB, declared and actual
    std::uint64_t max_total_size = 1ull << 40;     // 1 TiB, declared and actual
    std::uint32_t max_volumes = 65536;
    GuardOptions guard;
};

enum class OverwritePolicy { Fail, Skip, Overwrite };
enum class EntryKind { File, Directory, Symlink, Hardlink, Special };
enum class SkipReason { AlreadyExists, Link, Special };

struct EntryInfo {
    std::uint64_t index = 0;               // position in archive order
    std::u16string name;                   // as stored (decoded to UTF-16)
    std::u16string relative_path;          // after the guard; '/' separated; empty if the name was refused
    EntryKind kind = EntryKind::File;
    std::uint64_t size = 0;
    bool size_known = false;
    std::optional<Timestamp> mtime;
};

struct Skipped {
    std::u16string name;
    SkipReason reason;
};

struct Progress {
    std::uint64_t bytes_done = 0;
    std::uint64_t bytes_total = 0;         // declared, may be an underestimate
    const std::u16string* current = nullptr;
};

// Where the archive comes from: one or more files (a volume set, in order) or a memory buffer (tests).
struct ArchiveSource {
    std::vector<std::u16string> files;
    const std::uint8_t* data = nullptr;
    std::size_t size = 0;
    static ArchiveSource from_file(std::u16string path) { ArchiveSource s; s.files.push_back(std::move(path)); return s; }
    static ArchiveSource from_memory(const std::uint8_t* d, std::size_t n) { ArchiveSource s; s.data = d; s.size = n; return s; }
};

struct Options {
    OverwritePolicy overwrite = OverwritePolicy::Fail;
    bool restore_timestamps = true;
    // Read the archive twice so that every problem is found before the first byte is written. Costs a second decompression for
    // stream formats (tar.gz); seekable formats (zip, rar) only re-read headers' worth of data in the first pass.
    bool preflight_pass = true;
    Limits limits;
    std::function<bool(const EntryInfo&)> filter;           // return true to extract the entry
    std::function<void(const Progress&)> progress;
    std::function<bool()> cancelled;                        // return true to stop
    IFileSystem* file_system = nullptr;                     // default: native_file_system()
};

struct Result {
    std::uint64_t files = 0;
    std::uint64_t directories = 0;
    std::uint64_t bytes = 0;
    std::vector<Skipped> skipped;
};

// Throws unpakz::Error. Nothing is written when the plan is refused.
Result extract_archive(const ArchiveSource& source, const std::u16string& destination, const Options& options = {});

// Lists entries (names are guarded: refused names have empty relative_path).
std::vector<EntryInfo> list_archive(const ArchiveSource& source, const Limits& limits = {});

}  // namespace unpakz
