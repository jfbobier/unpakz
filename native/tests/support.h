// Test helpers: an in-memory IFileSystem with an operation log, and archive builders that use libarchive's *writer*
// (the shipped DLL never writes archives, and never uses libarchive's write_disk).
#pragma once
#include <archive.h>
#include <archive_entry.h>

#include <clocale>
#include <cstdint>
#include <cstring>
#include <map>
#include <random>
#include <set>
#include <string>
#include <vector>

#include "../src/engine.h"
#include "../src/guard.h"
#include "../src/scoped_locale.h"
#include "test.h"

namespace tsupport {

using namespace unpakz;

inline std::u16string W(const char* utf8) {
    std::u16string o;
    if (!utf8_to_utf16(utf8, o)) throw std::runtime_error("bad utf8 in test");
    return o;
}

// Parent directory of a path, keeping the separator of a drive root ("c:\\" not "c:").
inline std::u16string parent_dir(const std::u16string& p) {
    auto sep = p.find_last_of(u"/\\");
    if (sep == std::u16string::npos) return {};
    if (sep == 0) return p.substr(0, 1);
    if (sep == 2 && p[1] == u':') return p.substr(0, 3);
    return p.substr(0, sep);
}

inline std::u16string fold(const std::u16string& s) {
    std::u16string r = s;
    for (auto& c : r)
        if (c >= u'A' && c <= u'Z') c = static_cast<char16_t>(c + 32);
    return r;
}

#ifdef _WIN32
inline const std::u16string kRoot = u"C:\\";
inline const std::u16string kDest = u"C:\\dest";
#else
inline const std::u16string kRoot = u"/";
inline const std::u16string kDest = u"/dest";
#endif

inline std::u16string P(const char* rel) {
    std::u16string p = kDest;
    p.push_back(kNativeSeparator);
    for (auto c : W(rel)) p.push_back(c == u'/' ? kNativeSeparator : c);
    return p;
}

// Case-insensitive, like the Windows file system the guard targets.
class MemoryFs : public IFileSystem {
public:
    std::map<std::u16string, std::vector<std::uint8_t>> files;   // keyed by folded path
    std::map<std::u16string, std::u16string> names;              // folded -> as created
    std::set<std::u16string> dirs;
    std::set<std::u16string> links;
    std::vector<std::u16string> created_dirs;
    std::vector<std::pair<std::u16string, std::u16string>> temp_files;   // temp, final
    std::map<std::u16string, std::optional<Timestamp>> mtimes;   // folded path -> mtime given at commit
    std::map<std::u16string, bool> overwrite_flags;
    std::vector<std::uint64_t> preallocations;
    std::vector<std::u16string> create_dir_calls;
    bool discard_data = false;   // count bytes but do not keep them (huge-entry tests)
    std::uint64_t bytes_written = 0;
    int commits = 0;
    std::function<void(const std::u16string&)> on_create_file;   // fired when a writer is created (race injection)
    int temp_counter = 0;

    MemoryFs() { dirs.insert(fold(kRoot)); }

    void put_file(const std::u16string& path, std::vector<std::uint8_t> data) { files[fold(path)] = std::move(data); names[fold(path)] = path; }
    bool has_file(const std::u16string& path) const { return files.count(fold(path)) != 0; }
    const std::vector<std::uint8_t>& file(const std::u16string& path) const { return files.at(fold(path)); }
    std::string text(const std::u16string& path) const { auto& v = file(path); return std::string(v.begin(), v.end()); }
    std::vector<std::u16string> leftover_temps() const {
        std::vector<std::u16string> r;
        for (auto& [k, v] : files)
            if (k.find(u".unpakz-") != std::u16string::npos) r.push_back(k);
        return r;
    }
    std::size_t final_file_count() const { return files.size() - leftover_temps().size(); }

    PathKind get_kind(const std::u16string& path) override {
        auto k = fold(path);
        if (links.count(k)) return PathKind::ReparsePoint;
        if (dirs.count(k)) return PathKind::Directory;
        if (files.count(k)) return PathKind::File;
        return PathKind::None;
    }

    void create_directory(const std::u16string& path) override {
        create_dir_calls.push_back(path);
        auto k = fold(path);
        if (!dirs.count(parent_dir(k))) throw Error(Status::Io, "parent directory missing");
        if (files.count(k)) throw Error(Status::Conflict, "a file exists at the directory path");
        if (dirs.insert(k).second) created_dirs.push_back(path);
    }

    std::unique_ptr<IFileWriter> create_file(const std::u16string& final_path, std::uint64_t) override;
};

class MemWriter : public IFileWriter {
public:
    MemWriter(MemoryFs& fs, std::u16string temp, std::u16string final_path) : fs_(fs), temp_(std::move(temp)), final_(std::move(final_path)) {}
    ~MemWriter() override { if (!committed_) fs_.files.erase(fold(temp_)); }
    void write(const std::uint8_t* d, std::size_t n) override {
        if (!fs_.discard_data) {
            auto& v = fs_.files[fold(temp_)];
            v.insert(v.end(), d, d + n);
        }
        fs_.bytes_written += n;
    }
    void commit(std::optional<Timestamp> mtime, bool overwrite) override {
        auto fk = fold(final_);
        fs_.mtimes[fk] = mtime;
        fs_.overwrite_flags[fk] = overwrite;
        if (fs_.dirs.count(fk)) throw Error(Status::Conflict, "directory at final path");
        if (!overwrite && (fs_.files.count(fk) || fs_.links.count(fk))) throw Error(Status::Conflict, "already exists");
        fs_.links.erase(fk);
        fs_.files[fk] = std::move(fs_.files[fold(temp_)]);
        fs_.files.erase(fold(temp_));
        fs_.names[fk] = final_;
        committed_ = true;
        ++fs_.commits;
    }

private:
    MemoryFs& fs_;
    std::u16string temp_, final_;
    bool committed_ = false;
};

inline std::unique_ptr<IFileWriter> MemoryFs::create_file(const std::u16string& final_path, std::uint64_t preallocate) {
    preallocations.push_back(preallocate);
    auto dir = parent_dir(final_path);
    if (!dirs.count(fold(dir))) throw Error(Status::Io, "directory missing");
    std::u16string temp = dir + (dir.back() == kNativeSeparator ? std::u16string() : std::u16string(1, kNativeSeparator)) + u".unpakz-" + std::u16string(1, static_cast<char16_t>(u'a' + (temp_counter++ % 26))) + u".tmp";
    temp += std::u16string(1, static_cast<char16_t>(u'0' + temp_counter % 10));
    files[fold(temp)] = {};
    temp_files.emplace_back(temp, final_path);
    if (on_create_file) on_create_file(final_path);
    return std::make_unique<MemWriter>(*this, temp, final_path);
}

// ---- archive builders ----
struct Ent {
    std::string name;
    std::string data;
    unsigned type = AE_IFREG;
    std::string link;          // symlink / hardlink target
    std::int64_t mtime = 1700000000;
    bool unknown_size = false;   // zip only: stream the entry without declaring its size (data descriptor)
};

inline std::string dir_name(std::string n) { return n; }

struct WriteCtx { std::vector<std::uint8_t> out; };
inline la_ssize_t write_cb(struct archive*, void* ctx, const void* b, size_t n) {
    auto* c = static_cast<WriteCtx*>(ctx);
    auto* p = static_cast<const std::uint8_t*>(b);
    c->out.insert(c->out.end(), p, p + n);
    return static_cast<la_ssize_t>(n);
}

inline std::vector<std::uint8_t> build(const std::vector<Ent>& ents, bool zip, bool deflate = true) {
    unpakz::ScopedUtf8Locale utf8_names;
    WriteCtx ctx;
    struct archive* a = archive_write_new();
    if (zip) {
        archive_write_set_format_zip(a);
        archive_write_set_options(a, deflate ? "zip:compression=deflate,hdrcharset=UTF-8" : "zip:compression=store,hdrcharset=UTF-8");
    } else {
        archive_write_set_format_pax_restricted(a);
        archive_write_add_filter_gzip(a);
        archive_write_set_options(a, "hdrcharset=UTF-8");
    }
    archive_write_open2(a, &ctx, nullptr, write_cb, nullptr, nullptr);
    for (auto& e : ents) {
        struct archive_entry* en = archive_entry_new();
        archive_entry_set_pathname(en, e.name.c_str());
        archive_entry_set_filetype(en, e.type);
        archive_entry_set_perm(en, e.type == AE_IFDIR ? 0755 : 0644);
        archive_entry_set_mtime(en, e.mtime, 0);
        if (e.type == AE_IFLNK) archive_entry_set_symlink(en, e.link.c_str());
        if (e.type == AE_IFREG && !e.link.empty()) { archive_entry_set_hardlink(en, e.link.c_str()); archive_entry_set_size(en, 0); }
        else if (e.type == AE_IFREG && !e.unknown_size) archive_entry_set_size(en, static_cast<la_int64_t>(e.data.size()));
        if (archive_write_header(a, en) != ARCHIVE_OK) throw std::runtime_error(std::string("test builder: ") + archive_error_string(a));
        if (e.type == AE_IFREG && e.link.empty() && !e.data.empty()) archive_write_data(a, e.data.data(), e.data.size());
        archive_entry_free(en);
    }
    archive_write_close(a);
    archive_write_free(a);
    return ctx.out;
}
inline std::vector<std::uint8_t> make_zip(const std::vector<Ent>& e, bool deflate = true) { return build(e, true, deflate); }
inline std::vector<std::uint8_t> make_targz(const std::vector<Ent>& e) { return build(e, false); }

// One huge entry of zeros streamed through the zip writer (zip64 is switched on by libarchive when the size needs it).
inline std::vector<std::uint8_t> make_zip_huge(const std::string& name, std::uint64_t size) {
    WriteCtx ctx;
    struct archive* a = archive_write_new();
    archive_write_set_format_zip(a);
    archive_write_set_options(a, "zip:compression=deflate");
    archive_write_open2(a, &ctx, nullptr, write_cb, nullptr, nullptr);
    struct archive_entry* en = archive_entry_new();
    archive_entry_set_pathname(en, name.c_str());
    archive_entry_set_filetype(en, AE_IFREG);
    archive_entry_set_perm(en, 0644);
    archive_entry_set_size(en, static_cast<la_int64_t>(size));
    if (archive_write_header(a, en) != ARCHIVE_OK) throw std::runtime_error(archive_error_string(a));
    std::vector<char> zeros(1 << 20, 0);
    for (std::uint64_t left = size; left > 0;) {
        auto n = static_cast<std::size_t>(std::min<std::uint64_t>(left, zeros.size()));
        archive_write_data(a, zeros.data(), n);
        left -= n;
    }
    archive_entry_free(en);
    archive_write_close(a);
    archive_write_free(a);
    return ctx.out;
}

inline std::string rnd(std::size_t n, unsigned seed = 1) {
    std::mt19937 g(seed);
    std::string s(n, '\0');
    for (auto& c : s) c = static_cast<char>(g());
    return s;
}
inline std::string txt(std::size_t n, unsigned seed = 1) {
    std::mt19937 g(seed);
    std::string s(n, 'a');
    for (auto& c : s) c = static_cast<char>('a' + g() % 6);
    return s;
}
inline Ent dir(const std::string& name) { Ent e; e.name = name; e.type = AE_IFDIR; return e; }
inline Ent file(const std::string& name, const std::string& data) { Ent e; e.name = name; e.data = data; return e; }
inline Ent symlink_to(const std::string& name, const std::string& target) { Ent e; e.name = name; e.type = AE_IFLNK; e.link = target; return e; }

inline Result run(const std::vector<std::uint8_t>& bytes, MemoryFs& fs, Options o = {}) {
    o.file_system = &fs;
    return extract_archive(ArchiveSource::from_memory(bytes.data(), bytes.size()), kDest, o);
}

template <typename F>
bool throws(Status expected, F&& f) {
    try { f(); }
    catch (const Error& e) { return e.status() == expected; }
    return false;
}

}  // namespace tsupport
