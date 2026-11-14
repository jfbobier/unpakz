#include <cctype>
#include <cerrno>
#include <cstring>
#include <string>
#include <vector>

#include "platform.h"
#include "reader.h"

namespace unpakz {
namespace {

constexpr std::size_t kBlock = 1 << 20;   // 1 MiB reads: few syscalls, still small

struct Volume;

struct FileSet {
    std::vector<std::u16string> paths;
    std::vector<std::unique_ptr<Volume>> volumes;
    platform::RawFile file;
    std::vector<char> buffer = std::vector<char>(kBlock);
    std::string last_error;
};

struct Volume {
    FileSet* set;
    std::size_t index;
};

int on_open(struct archive* a, void* cd) {
    auto* v = static_cast<Volume*>(cd);
    try { v->set->file.open(v->set->paths[v->index]); }
    catch (const Error& e) { archive_set_error(a, EIO, "%s", e.what()); return ARCHIVE_FATAL; }
    return ARCHIVE_OK;
}

la_ssize_t on_read(struct archive* a, void* cd, const void** out) {
    auto* v = static_cast<Volume*>(cd);
    try {
        auto n = v->set->file.read(v->set->buffer.data(), v->set->buffer.size());
        *out = v->set->buffer.data();
        return static_cast<la_ssize_t>(n);
    } catch (const Error& e) {
        archive_set_error(a, EIO, "%s", e.what());
        return -1;
    }
}

la_int64_t on_skip(struct archive*, void* cd, la_int64_t request) {
    auto* v = static_cast<Volume*>(cd);
    try {
        auto before = v->set->file.seek(0, 1);
        auto after = v->set->file.seek(request, 1);
        return after - before;
    } catch (const Error&) {
        return 0;                                        // libarchive falls back to reading
    }
}

la_int64_t on_seek(struct archive* a, void* cd, la_int64_t offset, int whence) {
    auto* v = static_cast<Volume*>(cd);
    try { return v->set->file.seek(offset, whence); }
    catch (const Error& e) { archive_set_error(a, EIO, "%s", e.what()); return ARCHIVE_FATAL; }
}

int on_close(struct archive*, void* cd) {
    static_cast<Volume*>(cd)->set->file.close();      // called once per volume; closing twice is harmless
    return ARCHIVE_OK;
}

int on_switch(struct archive* a, void*, void* next) {
    auto* v = static_cast<Volume*>(next);
    v->set->file.close();
    try { v->set->file.open(v->set->paths[v->index]); }
    catch (const Error& e) { archive_set_error(a, ENOENT, "%s", e.what()); return ARCHIVE_FATAL; }
    return ARCHIVE_OK;
}

}  // namespace

void throw_reader_error(struct archive* a, const char* context) {
    const char* msg = archive_error_string(a);
    std::string text = std::string(context) + ": " + (msg ? msg : "unknown error");
    std::string lower;
    for (char c : text) lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));
    auto has = [&](const char* s) { return lower.find(s) != std::string::npos; };
    const int err = archive_errno(a);

    if (err == ENOMEM) throw Error(Status::LimitExceeded, text);
    if (has("ncrypt") || has("assword") || has("nsupported") || has("not supported") || has("unrecognized archive format") ||
        has("unknown compression") || has("unknown format") || has("no suitable"))
        throw Error(Status::Unsupported, text);
    if (has("cannot open") || err == EIO || err == ENOENT) throw Error(Status::Io, text);
    throw Error(Status::Corrupt, text);
}

Reader open_reader(const ArchiveSource& source, const Limits& limits) {
    Reader r;
    r.a.reset(archive_read_new());
    if (!r.a) throw Error(Status::Io, "out of memory");
    struct archive* a = r.a.get();

    archive_read_support_format_zip(a);
    archive_read_support_format_tar(a);
    archive_read_support_format_rar(a);
    archive_read_support_format_rar5(a);
    archive_read_support_filter_gzip(a);                  // tar.gz; no program-spawning filters, ever

    if (source.data != nullptr || source.files.empty()) {
        if (archive_read_open_memory(a, source.data, source.size) != ARCHIVE_OK) throw_reader_error(a, "cannot read archive");
        return r;
    }

    if (source.files.size() > limits.max_volumes) throw Error(Status::LimitExceeded, "too many volumes");

    auto set = std::make_shared<FileSet>();
    set->paths = source.files;
    for (std::size_t i = 0; i < set->paths.size(); ++i) set->volumes.push_back(std::make_unique<Volume>(Volume{set.get(), i}));
    r.keep = set;

    archive_read_set_open_callback(a, on_open);
    archive_read_set_read_callback(a, on_read);
    archive_read_set_skip_callback(a, on_skip);
    if (set->paths.size() == 1) archive_read_set_seek_callback(a, on_seek);    // zip's central directory needs seeking
    archive_read_set_close_callback(a, on_close);
    archive_read_set_switch_callback(a, on_switch);
    archive_read_set_callback_data(a, set->volumes[0].get());
    for (std::size_t i = 1; i < set->volumes.size(); ++i) archive_read_append_callback_data(a, set->volumes[i].get());

    if (archive_read_open1(a) != ARCHIVE_OK) throw_reader_error(a, "cannot open archive");
    return r;
}

}  // namespace unpakz
