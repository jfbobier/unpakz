// The C ABI: validation of arguments, translation of exceptions into statuses, and nothing else. All logic lives in engine.cpp.
#include "../include/unpakz.h"

#include <cstddef>
#include <cstring>
#include <new>
#include <string>

#include "engine.h"

using namespace unpakz;

namespace {

constexpr const char* kVersion = "unpakz 0.2.0-native (libarchive 3.8.11, zlib-ng 2.2.5)";

void set_error(unpakz_error* e, Status s, const std::string& message, int volume = 0) {
    if (!e || e->struct_size < sizeof(unpakz_error)) return;
    e->status = static_cast<int32_t>(s);
    e->volume = volume;
    std::u16string w;
    if (!utf8_to_utf16(message, w)) w = u"(message could not be decoded)";
    const std::size_t n = std::min<std::size_t>(w.size(), UNPAKZ_ERROR_MESSAGE_CHARS - 1);
    std::memcpy(e->message, w.data(), n * sizeof(char16_t));
    e->message[n] = u'\0';
}

unpakz_status fail(unpakz_error* e, Status s, const std::string& m, int volume = 0) {
    set_error(e, s, m, volume);
    return static_cast<unpakz_status>(s);
}

unpakz_status ok(unpakz_error* e) {
    if (e && e->struct_size >= sizeof(unpakz_error)) { e->status = 0; e->volume = 0; e->message[0] = u'\0'; }
    return UNPAKZ_OK;
}

template <typename F>
unpakz_status guarded(unpakz_error* e, F&& f) noexcept {
    try {
        return f();
    } catch (const Error& ex) {
        return fail(e, ex.status(), ex.what(), ex.volume());
    } catch (const std::bad_alloc&) {
        return fail(e, Status::LimitExceeded, "out of memory");
    } catch (const std::exception& ex) {
        set_error(e, Status::Ok, ex.what());
        if (e && e->struct_size >= sizeof(unpakz_error)) e->status = UNPAKZ_INTERNAL;
        return UNPAKZ_INTERNAL;
    } catch (...) {
        set_error(e, Status::Ok, "unexpected failure");
        if (e && e->struct_size >= sizeof(unpakz_error)) e->status = UNPAKZ_INTERNAL;
        return UNPAKZ_INTERNAL;
    }
}

// Returns false if the limits struct is malformed.
bool to_limits(const unpakz_limits* in, Limits& out) {
    if (!in) return true;
    if (in->struct_size < sizeof(unpakz_limits)) return false;
    if (in->max_volumes) out.max_volumes = in->max_volumes;
    if (in->max_entries) out.max_entries = in->max_entries;
    if (in->max_entry_size) out.max_entry_size = in->max_entry_size;
    if (in->max_total_size) out.max_total_size = in->max_total_size;
    if (in->max_path_length) out.guard.max_path_length = in->max_path_length;
    if (in->max_path_depth) out.guard.max_depth = static_cast<int>(std::min<std::uint32_t>(in->max_path_depth, 1u << 20));
    if (in->max_segment_length) out.guard.max_segment_length = in->max_segment_length;
    return true;
}

void to_c_entry(const EntryInfo& e, unpakz_entry& c) {
    c.struct_size = sizeof c;
    c.kind = static_cast<int32_t>(e.kind);
    c.index = e.index;
    c.name = e.name.c_str();
    c.relative_path = e.relative_path.c_str();
    c.size = e.size;
    c.size_known = e.size_known ? 1 : 0;
    c.has_mtime = e.mtime ? 1 : 0;
    c.mtime_unix_seconds = e.mtime ? e.mtime->unix_seconds : 0;
}

}  // namespace

extern "C" {

UNPAKZ_API uint32_t unpakz_abi_version(void) { return UNPAKZ_ABI_VERSION; }
UNPAKZ_API const char* unpakz_library_version(void) { return kVersion; }

UNPAKZ_API void unpakz_default_limits(unpakz_limits* l) {
    if (!l) return;
    Limits d;
    std::memset(l, 0, sizeof *l);
    l->struct_size = sizeof *l;
    l->max_volumes = d.max_volumes;
    l->max_entries = d.max_entries;
    l->max_entry_size = d.max_entry_size;
    l->max_total_size = d.max_total_size;
    l->max_path_length = static_cast<uint32_t>(d.guard.max_path_length);
    l->max_path_depth = static_cast<uint32_t>(d.guard.max_depth);
    l->max_segment_length = static_cast<uint32_t>(d.guard.max_segment_length);
}

UNPAKZ_API void unpakz_default_extract_options(unpakz_extract_options* o) {
    if (!o) return;
    std::memset(o, 0, sizeof *o);
    o->struct_size = sizeof *o;
    o->overwrite = UNPAKZ_OVERWRITE_FAIL;
    o->restore_timestamps = 1;
    o->preflight_pass = 1;
    unpakz_default_limits(&o->limits);
}

UNPAKZ_API unpakz_status unpakz_list(const char16_t* path, const unpakz_limits* limits, unpakz_entry_fn callback, void* user, unpakz_error* error) {
    return guarded(error, [&]() -> unpakz_status {
        if (!path || !*path || !callback) return fail(error, Status::InvalidArgument, "path and callback are required");
        Limits lim;
        if (!to_limits(limits, lim)) return fail(error, Status::InvalidArgument, "limits struct has the wrong size");
        auto files = locate_volumes(path, lim.max_volumes);
        ArchiveSource src;
        src.files = std::move(files);
        auto entries = list_archive(src, lim);
        for (auto& e : entries) {
            unpakz_entry c{};
            to_c_entry(e, c);
            if (!callback(&c, user)) return fail(error, Status::Cancelled, "listing stopped by the caller");
        }
        return ok(error);
    });
}

UNPAKZ_API unpakz_status unpakz_extract(const char16_t* path, const char16_t* destination, const unpakz_extract_options* o, unpakz_result* result, unpakz_error* error) {
    return guarded(error, [&]() -> unpakz_status {
        if (!path || !*path || !destination || !*destination || !o) return fail(error, Status::InvalidArgument, "path, destination and options are required");
        if (o->struct_size < sizeof(unpakz_extract_options)) return fail(error, Status::InvalidArgument, "options struct has the wrong size");
        if (o->overwrite < UNPAKZ_OVERWRITE_FAIL || o->overwrite > UNPAKZ_OVERWRITE_REPLACE) return fail(error, Status::InvalidArgument, "unknown overwrite policy");

        Options opt;
        if (o->limits.struct_size < sizeof(unpakz_limits)) return fail(error, Status::InvalidArgument, "limits struct has the wrong size");
        to_limits(&o->limits, opt.limits);
        opt.overwrite = static_cast<OverwritePolicy>(o->overwrite);
        opt.restore_timestamps = o->restore_timestamps != 0;
        opt.preflight_pass = o->preflight_pass != 0;
        void* user = o->user;
        if (auto f = o->filter) opt.filter = [f, user](const EntryInfo& e) { unpakz_entry c{}; to_c_entry(e, c); return f(&c, user) != 0; };
        if (auto f = o->progress) opt.progress = [f, user](const Progress& p) {
            unpakz_progress c{};
            c.struct_size = sizeof c;
            c.bytes_done = p.bytes_done;
            c.bytes_total = p.bytes_total;
            c.current_name = p.current ? p.current->c_str() : u"";
            f(&c, user);
        };
        if (auto f = o->cancelled) opt.cancelled = [f, user]() { return f(user) != 0; };

        ArchiveSource src;
        src.files = locate_volumes(path, opt.limits.max_volumes);
        Result r = extract_archive(src, destination, opt);
        if (result && result->struct_size >= sizeof(unpakz_result)) {
            result->files = r.files;
            result->directories = r.directories;
            result->bytes = r.bytes;
            result->skipped = r.skipped.size();
        }
        return ok(error);
    });
}

UNPAKZ_API unpakz_status unpakz_check_name(const char16_t* name, char16_t* out, uint32_t out_chars, unpakz_error* error) {
    return guarded(error, [&]() -> unpakz_status {
        if (!name || !out || out_chars == 0) return fail(error, Status::InvalidArgument, "name and output buffer are required");
        auto check = validate_entry_name(name);
        if (!check.is_safe) return fail(error, Status::UnsafePath, "the name is not allowed");
        if (check.relative_path.size() + 1 > out_chars) return fail(error, Status::InvalidArgument, "output buffer too small");
        std::memcpy(out, check.relative_path.data(), check.relative_path.size() * sizeof(char16_t));
        out[check.relative_path.size()] = u'\0';
        return ok(error);
    });
}

}  // extern "C"
