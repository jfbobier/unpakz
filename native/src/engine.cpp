#include "engine.h"

#include <algorithm>
#include <set>
#include <unordered_set>

#include "reader.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

namespace unpakz {
namespace {

constexpr std::uint64_t kPreallocateLimit = 256ull << 20;   // never trust a declared size beyond this for pre-sizing a file

// Case-insensitive key, matching how the target file system compares names.
std::u16string fold(const std::u16string& s) {
#ifdef _WIN32
    if (s.empty()) return s;
    std::u16string r(s.size(), u'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, reinterpret_cast<LPCWSTR>(s.data()), static_cast<int>(s.size()),
                  reinterpret_cast<LPWSTR>(r.data()), static_cast<int>(r.size()), nullptr, nullptr, 0);
    return r;
#else
    std::u16string r = s;
    for (auto& c : r) {
        if (c >= u'A' && c <= u'Z') c = static_cast<char16_t>(c + 32);
        else if (c >= 0xC0 && c <= 0xDE && c != 0xD7) c = static_cast<char16_t>(c + 32);
    }
    return r;
#endif
}

std::u16string join_native(const std::u16string& root, const std::u16string& relative) {
    std::u16string p = root;
    while (p.size() > 1 && (p.back() == u'/' || p.back() == u'\\')) p.pop_back();
    if (p.empty() || p.back() != kNativeSeparator) p.push_back(kNativeSeparator);
    for (auto c : relative) p.push_back(c == u'/' ? kNativeSeparator : c);
    return p;
}

// Second line of defence behind the guard: the relative path handed to the file system can never climb out of the root.
void require_confined(const std::u16string& relative) {
    if (relative.empty() || relative.front() == u'/' || relative.front() == u'\\' || relative.find(u':') != std::u16string::npos ||
        relative.find(u'\\') != std::u16string::npos)
        throw Error(Status::UnsafePath, "internal: unconfined relative path");
    std::size_t start = 0;
    while (start <= relative.size()) {
        auto end = relative.find(u'/', start);
        if (end == std::u16string::npos) end = relative.size();
        auto seg = std::u16string_view(relative).substr(start, end - start);
        if (seg.empty() || seg == u"." || seg == u"..") throw Error(Status::UnsafePath, "internal: unconfined relative path");
        start = end + 1;
    }
}

std::u16string display(const std::u16string& s) {
    // Names in error messages: control characters never reach a message box or log as-is.
    std::u16string r;
    for (auto c : s) r.push_back(c < u' ' || c == 0x7F ? u'?' : c);
    return r;
}

std::string narrow(const std::u16string& s) { return utf16_to_utf8(display(s)); }

struct Entry {
    EntryInfo info;
    bool name_valid = true;
    PathRejection rejection = PathRejection::None;
    bool is_directory_path = false;
};

Entry analyze(struct archive_entry* e, std::uint64_t index, const Limits& limits) {
    Entry r;
    r.info.index = index;

    const char* utf8 = archive_entry_pathname_utf8(e);
    const char* raw = utf8 ? utf8 : archive_entry_pathname(e);
    std::string bytes = raw ? raw : "";
    if (!utf8_to_utf16(bytes, r.info.name)) {
        r.name_valid = false;
        r.info.name = u"<name is not valid text>";
    }

    const bool hardlink = archive_entry_hardlink(e) != nullptr || archive_entry_hardlink_utf8(e) != nullptr;
    switch (archive_entry_filetype(e)) {
        case AE_IFREG: r.info.kind = hardlink ? EntryKind::Hardlink : EntryKind::File; break;
        case AE_IFDIR: r.info.kind = EntryKind::Directory; break;
        case AE_IFLNK: r.info.kind = EntryKind::Symlink; break;
        default: r.info.kind = hardlink ? EntryKind::Hardlink : EntryKind::Special; break;
    }

    r.info.size_known = archive_entry_size_is_set(e) != 0;
    if (r.info.size_known) {
        auto sz = archive_entry_size(e);
        if (sz < 0) throw Error(Status::Corrupt, "entry has a negative size");
        r.info.size = static_cast<std::uint64_t>(sz);
    }
    if (archive_entry_mtime_is_set(e)) r.info.mtime = Timestamp{static_cast<std::int64_t>(archive_entry_mtime(e)), static_cast<std::int32_t>(archive_entry_mtime_nsec(e))};

    if (!r.name_valid) {
        r.rejection = PathRejection::InvalidCharacter;
    } else {
        auto check = validate_entry_name(r.info.name, limits.guard);
        if (check.is_safe) {
            r.info.relative_path = std::move(check.relative_path);
            r.is_directory_path = check.is_directory;
        } else {
            r.rejection = check.reason;
        }
    }
    return r;
}

const char* rejection_text(PathRejection r) {
    switch (r) {
        case PathRejection::Empty: return "is empty";
        case PathRejection::Rooted: return "is an absolute path";
        case PathRejection::Traversal: return "climbs out of the destination folder";
        case PathRejection::InvalidCharacter: return "contains a character that is not allowed";
        case PathRejection::ReservedName: return "is a reserved device name";
        case PathRejection::TrailingDotOrSpace: return "ends with a dot or a space";
        case PathRejection::TooLong: return "is too long";
        case PathRejection::TooDeep: return "is nested too deeply";
        default: return "is not allowed";
    }
}

// Bookkeeping that decides, entry by entry, what happens to each one. Used for the pre-flight pass and again while writing.
class Planner {
public:
    enum class Action { Skip, Directory, File };
    struct Decision { Action action; std::optional<SkipReason> reason; };

    Planner(const Limits& limits, std::function<bool(const EntryInfo&)> filter) : limits_(limits), filter_(std::move(filter)) {}

    Decision add(const Entry& e) {
        if (++seen_ > limits_.max_entries) throw Error(Status::LimitExceeded, "the archive has too many entries");
        if (filter_ && !filter_(e.info)) return {Action::Skip, std::nullopt};

        if (e.info.relative_path.empty())
            throw Error(Status::UnsafePath, "the entry '" + narrow(e.info.name) + "' " + rejection_text(e.rejection));

        if (e.info.kind == EntryKind::Symlink || e.info.kind == EntryKind::Hardlink) return {Action::Skip, SkipReason::Link};
        if (e.info.kind == EntryKind::Special) return {Action::Skip, SkipReason::Special};

        const auto key = fold(e.info.relative_path);
        // every parent directory must not be a file
        for (std::size_t slash = key.find(u'/'); slash != std::u16string::npos; slash = key.find(u'/', slash + 1)) {
            auto parent = key.substr(0, slash);
            if (files_.count(parent)) throw Error(Status::Conflict, "'" + narrow(e.info.relative_path) + "' needs a folder where another entry is a file");
            dirs_.insert(parent);
        }

        if (e.info.kind == EntryKind::Directory) {
            if (files_.count(key)) throw Error(Status::Conflict, "'" + narrow(e.info.relative_path) + "' is both a file and a folder in the archive");
            dirs_.insert(key);
            return {Action::Directory, std::nullopt};
        }

        if (dirs_.count(key) || !files_.insert(key).second)
            throw Error(Status::Conflict, "'" + narrow(e.info.relative_path) + "' appears more than once in the archive");

        if (e.info.size_known) {
            if (e.info.size > limits_.max_entry_size) throw Error(Status::LimitExceeded, "'" + narrow(e.info.relative_path) + "' is larger than the allowed entry size");
            declared_total_ += e.info.size;
            if (declared_total_ < e.info.size || declared_total_ > limits_.max_total_size) throw Error(Status::LimitExceeded, "the archive is larger than the allowed total size");
        }
        return {Action::File, std::nullopt};
    }

    std::uint64_t declared_total() const { return declared_total_; }

private:
    const Limits& limits_;
    std::function<bool(const EntryInfo&)> filter_;
    std::uint64_t seen_ = 0;
    std::uint64_t declared_total_ = 0;
    std::set<std::u16string> files_, dirs_;
};

struct PlannedItem {
    std::uint64_t index;
    std::u16string name;
    std::u16string relative;
    bool directory;
};

// Reads the next header; returns false at end of archive. Throws on errors.
bool next_header(struct archive* a, struct archive_entry* e) {
    archive_entry_clear(e);
    int r = archive_read_next_header2(a, e);
    if (r == ARCHIVE_EOF) return false;
    if (r == ARCHIVE_OK || r == ARCHIVE_WARN) return true;     // WARN on a header is advisory (e.g. a name needs charset conversion)
    throw_reader_error(a, "cannot read entry");
}

class Extractor {
public:
    Extractor(const ArchiveSource& source, const std::u16string& dest, const Options& options)
        : source_(source), dest_(dest), o_(options), fs_(options.file_system ? *options.file_system : native_file_system()) {}

    Result run() {
        if (dest_.empty()) throw Error(Status::InvalidArgument, "no destination folder");

        std::vector<PlannedItem> plan;
        std::uint64_t declared_total = 0;
        if (o_.preflight_pass) {
            plan = plan_pass(declared_total);
            preflight(plan);
        }
        ensure_root();
        write_pass(plan, declared_total);
        return result_;
    }

private:
    const ArchiveSource& source_;
    std::u16string dest_;
    const Options& o_;
    IFileSystem& fs_;
    Result result_;
    std::set<std::u16string> verified_dirs_;

    void check_cancel() const {
        if (o_.cancelled && o_.cancelled()) throw Error(Status::Cancelled, "cancelled");
    }

    std::vector<PlannedItem> plan_pass(std::uint64_t& declared_total) {
        auto reader = open_reader(source_, o_.limits);
        std::unique_ptr<archive_entry, decltype(&archive_entry_free)> e(archive_entry_new(), archive_entry_free);
        Planner planner(o_.limits, o_.filter);
        std::vector<PlannedItem> plan;
        std::uint64_t index = 0;
        while (next_header(reader.get(), e.get())) {
            check_cancel();
            auto entry = analyze(e.get(), index, o_.limits);
            auto d = planner.add(entry);
            if (d.action != Planner::Action::Skip) plan.push_back({index, entry.info.name, entry.info.relative_path, d.action == Planner::Action::Directory});
            ++index;
        }
        declared_total = planner.declared_total();
        return plan;
    }

    // Everything that can be decided against the file system is decided before the first byte is written.
    void preflight(const std::vector<PlannedItem>& plan) {
        std::set<std::u16string> checked;
        for (auto& item : plan) {
            // parents (and directory entries): nothing in the way, nothing redirected
            const auto last_slash = item.relative.rfind(u'/');
            const std::u16string parents = item.directory ? item.relative : (last_slash == std::u16string::npos ? std::u16string() : item.relative.substr(0, last_slash));
            std::u16string prefix;
            for (std::size_t start = 0; !parents.empty() && start <= parents.size();) {
                auto end = parents.find(u'/', start);
                if (end == std::u16string::npos) end = parents.size();
                if (!prefix.empty()) prefix.push_back(u'/');
                prefix.append(parents, start, end - start);
                if (checked.insert(prefix).second) {
                    auto kind = fs_.get_kind(join_native(dest_, prefix));
                    if (kind == PathKind::File) throw Error(Status::Conflict, "'" + narrow(prefix) + "' exists as a file where a folder is needed");
                    if (kind == PathKind::ReparsePoint) throw Error(Status::Conflict, "'" + narrow(prefix) + "' is a link; refusing to extract through it");
                }
                start = end + 1;
            }
            if (item.directory) continue;
            auto kind = fs_.get_kind(join_native(dest_, item.relative));
            if (kind == PathKind::None) continue;
            if (kind == PathKind::Directory || o_.overwrite == OverwritePolicy::Fail)
                throw Error(Status::Conflict, "'" + narrow(item.relative) + "' already exists");
        }
    }

    void ensure_root() {
        std::vector<std::u16string> chain;
        std::u16string cur = dest_;
        while (!cur.empty()) {
            auto kind = fs_.get_kind(cur);
            if (kind == PathKind::Directory || kind == PathKind::ReparsePoint) break;      // a user-chosen destination may itself be a junction
            if (kind == PathKind::File) throw Error(Status::Conflict, "the destination is a file, not a folder");
            chain.push_back(cur);
            while (cur.size() > 1 && (cur.back() == u'/' || cur.back() == u'\\')) cur.pop_back();
            auto sep = cur.find_last_of(u"/\\");
            if (sep == std::u16string::npos || sep == 0 || (sep == 2 && cur[1] == u':' && cur.size() == 2)) break;
            cur = cur.substr(0, sep);
            if (cur.size() == 2 && cur[1] == u':') break;
        }
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) fs_.create_directory(*it);
    }

    void ensure_directory(const std::u16string& relative_dir) {
        require_confined(relative_dir);
        std::u16string prefix;
        for (std::size_t start = 0; start <= relative_dir.size();) {
            auto end = relative_dir.find(u'/', start);
            if (end == std::u16string::npos) end = relative_dir.size();
            if (!prefix.empty()) prefix.push_back(u'/');
            prefix.append(relative_dir, start, end - start);
            start = end + 1;
            if (verified_dirs_.count(prefix)) continue;
            auto full = join_native(dest_, prefix);
            switch (fs_.get_kind(full)) {
                case PathKind::None: fs_.create_directory(full); break;
                case PathKind::Directory: break;
                case PathKind::File: throw Error(Status::Conflict, "'" + narrow(prefix) + "' is a file where a folder is needed");
                case PathKind::ReparsePoint: throw Error(Status::Conflict, "'" + narrow(prefix) + "' is a link; refusing to extract through it");
            }
            verified_dirs_.insert(prefix);
        }
    }

    void write_pass(const std::vector<PlannedItem>& plan, std::uint64_t declared_total) {
        auto reader = open_reader(source_, o_.limits);
        std::unique_ptr<archive_entry, decltype(&archive_entry_free)> e(archive_entry_new(), archive_entry_free);
        Planner planner(o_.limits, o_.filter);
        std::uint64_t index = 0;
        std::size_t plan_pos = 0;
        std::uint64_t total_written = 0;
        std::uint64_t progress_total = declared_total;

        while (next_header(reader.get(), e.get())) {
            check_cancel();
            auto entry = analyze(e.get(), index, o_.limits);
            auto d = planner.add(entry);
            ++index;

            if (d.action == Planner::Action::Skip) {
                if (d.reason) result_.skipped.push_back({entry.info.name, *d.reason});
                continue;
            }
            if (o_.preflight_pass) {
                // The archive must be the one we planned (it could have been replaced between the two reads).
                if (plan_pos >= plan.size() || plan[plan_pos].index != entry.info.index || plan[plan_pos].relative != entry.info.relative_path)
                    throw Error(Status::Corrupt, "the archive changed while it was being extracted");
                ++plan_pos;
            }

            if (d.action == Planner::Action::Directory) {
                ensure_directory(entry.info.relative_path);
                ++result_.directories;
                continue;
            }
            extract_file(reader.get(), entry, total_written, progress_total);
        }
        if (o_.preflight_pass && plan_pos != plan.size()) throw Error(Status::Corrupt, "the archive changed while it was being extracted");
    }

    void extract_file(struct archive* a, const Entry& entry, std::uint64_t& total_written, std::uint64_t& progress_total) {
        const auto& rel = entry.info.relative_path;
        require_confined(rel);
        auto slash = rel.rfind(u'/');
        if (slash != std::u16string::npos) ensure_directory(rel.substr(0, slash));

        const auto full = join_native(dest_, rel);
        switch (fs_.get_kind(full)) {      // re-checked: it may have changed since the pre-flight
            case PathKind::Directory: throw Error(Status::Conflict, "a folder exists where '" + narrow(rel) + "' should be written");
            case PathKind::File:
            case PathKind::ReparsePoint:
                if (o_.overwrite == OverwritePolicy::Fail) throw Error(Status::Conflict, "'" + narrow(rel) + "' already exists");
                if (o_.overwrite == OverwritePolicy::Skip) { result_.skipped.push_back({entry.info.name, SkipReason::AlreadyExists}); return; }
                break;
            case PathKind::None: break;
        }

        const std::uint64_t declared = entry.info.size_known ? entry.info.size : 0;
        auto writer = fs_.create_file(full, declared <= kPreallocateLimit ? declared : 0);

        std::uint64_t written = 0;
        for (;;) {
            check_cancel();
            const void* buf = nullptr;
            std::size_t size = 0;
            la_int64_t offset = 0;
            int r = archive_read_data_block(a, &buf, &size, &offset);
            if (r == ARCHIVE_EOF) break;
            if (r != ARCHIVE_OK) throw_reader_error(a, "cannot read data");     // WARN too: a CRC mismatch is reported as a warning

            if (offset < 0 || static_cast<std::uint64_t>(offset) < written) throw Error(Status::Corrupt, "data blocks out of order");
            // Sparse gaps are materialised as zeros, and count like any other byte.
            std::uint64_t gap = static_cast<std::uint64_t>(offset) - written;
            if (gap > 0) {
                charge(gap, written, total_written, rel);
                static const std::vector<std::uint8_t> zeros(1 << 16, 0);
                for (std::uint64_t left = gap; left > 0;) {
                    auto n = static_cast<std::size_t>(std::min<std::uint64_t>(left, zeros.size()));
                    writer->write(zeros.data(), n);
                    left -= n;
                }
            }
            charge(size, written, total_written, rel);
            if (entry.info.size_known && written + size > entry.info.size) throw Error(Status::Corrupt, "'" + narrow(rel) + "' has more data than its header declares");
            writer->write(static_cast<const std::uint8_t*>(buf), size);
            written += size;
            total_written += size;
            if (o_.progress) {
                progress_total = std::max(progress_total, total_written);
                Progress p{total_written, progress_total, &entry.info.name};
                o_.progress(p);
            }
        }

        if (entry.info.size_known && written != entry.info.size) throw Error(Status::Corrupt, "'" + narrow(rel) + "' is shorter than its header declares");
        writer->commit(o_.restore_timestamps ? entry.info.mtime : std::nullopt, o_.overwrite == OverwritePolicy::Overwrite);
        ++result_.files;
        result_.bytes += written;
    }

    // Declared sizes are hostile; the limits apply to what is actually produced.
    void charge(std::uint64_t n, std::uint64_t written, std::uint64_t total_written, const std::u16string& rel) const {
        if (written + n < written || written + n > o_.limits.max_entry_size) throw Error(Status::LimitExceeded, "'" + narrow(rel) + "' exceeds the allowed entry size while unpacking");
        if (total_written + n < total_written || total_written + n > o_.limits.max_total_size) throw Error(Status::LimitExceeded, "the archive exceeds the allowed total size while unpacking");
    }
};

}  // namespace

Result extract_archive(const ArchiveSource& source, const std::u16string& destination, const Options& options) {
    Extractor x(source, destination, options);
    return x.run();
}

std::vector<EntryInfo> list_archive(const ArchiveSource& source, const Limits& limits) {
    auto reader = open_reader(source, limits);
    std::unique_ptr<archive_entry, decltype(&archive_entry_free)> e(archive_entry_new(), archive_entry_free);
    std::vector<EntryInfo> out;
    std::uint64_t index = 0;
    while (next_header(reader.get(), e.get())) {
        if (index >= limits.max_entries) throw Error(Status::LimitExceeded, "the archive has too many entries");
        out.push_back(analyze(e.get(), index++, limits).info);
    }
    return out;
}

}  // namespace unpakz
