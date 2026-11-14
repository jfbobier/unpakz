/* unpakz.h - flat C API of unpakz.dll (extraction only).
 *
 * Conventions
 *  - Paths and names are UTF-16 (char16_t), NUL terminated: binary compatible with .NET LPWStr and Win32 wide strings.
 *  - Every struct that crosses the boundary starts with `struct_size`; set it to sizeof(the struct). Newer fields are appended,
 *    never reordered, so an old caller keeps working.
 *  - No exception, no allocation ownership crosses the boundary. Errors come back as a status plus a message in a caller-owned buffer.
 *  - Strings handed to callbacks are valid only during the callback.
 *  - Callbacks run on the calling thread; they must not call back into the library for the same extraction.
 */
#ifndef UNPAKZ_H
#define UNPAKZ_H

#include <stdint.h>
#include <uchar.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(UNPAKZ_BUILD_DLL)
#define UNPAKZ_API __declspec(dllexport)
#elif defined(__GNUC__)
#define UNPAKZ_API __attribute__((visibility("default")))
#else
#define UNPAKZ_API
#endif

#define UNPAKZ_ABI_VERSION 1u

typedef enum unpakz_status {
    UNPAKZ_OK = 0,
    UNPAKZ_CORRUPT = 1,        /* damaged, truncated or inconsistent archive */
    UNPAKZ_UNSUPPORTED = 2,    /* a format or feature we do not handle (e.g. encryption) */
    UNPAKZ_LIMIT_EXCEEDED = 3, /* a configured safety limit was hit */
    UNPAKZ_UNSAFE_PATH = 4,    /* an entry name was refused by the path guard */
    UNPAKZ_CONFLICT = 5,       /* something already exists / would be overwritten / is in the way */
    UNPAKZ_MISSING_VOLUME = 6, /* a volume of a multi-volume set is missing (error.volume says which, 1-based) */
    UNPAKZ_IO = 7,             /* the operating system refused (disk full, access denied, ...) */
    UNPAKZ_CANCELLED = 8,
    UNPAKZ_INVALID_ARGUMENT = 9,
    UNPAKZ_INTERNAL = 10       /* a bug: an unexpected exception was caught at the boundary */
} unpakz_status;

typedef enum unpakz_entry_kind {
    UNPAKZ_ENTRY_FILE = 0,
    UNPAKZ_ENTRY_DIRECTORY = 1,
    UNPAKZ_ENTRY_SYMLINK = 2,
    UNPAKZ_ENTRY_HARDLINK = 3,
    UNPAKZ_ENTRY_SPECIAL = 4
} unpakz_entry_kind;

typedef enum unpakz_overwrite {
    UNPAKZ_OVERWRITE_FAIL = 0,
    UNPAKZ_OVERWRITE_SKIP = 1,
    UNPAKZ_OVERWRITE_REPLACE = 2
} unpakz_overwrite;

#define UNPAKZ_ERROR_MESSAGE_CHARS 512

typedef struct unpakz_error {
    uint32_t struct_size;
    int32_t status;
    int32_t volume; /* with UNPAKZ_MISSING_VOLUME: the 1-based number of the missing volume */
    char16_t message[UNPAKZ_ERROR_MESSAGE_CHARS];
} unpakz_error;

/* Hard limits. Zero in a field means "use the safe default". */
typedef struct unpakz_limits {
    uint32_t struct_size;
    uint32_t max_volumes;
    uint64_t max_entries;
    uint64_t max_entry_size;
    uint64_t max_total_size;
    uint32_t max_path_length;
    uint32_t max_path_depth;
    uint32_t max_segment_length;
    uint32_t reserved;
} unpakz_limits;

typedef struct unpakz_entry {
    uint32_t struct_size;
    int32_t kind; /* unpakz_entry_kind */
    uint64_t index;
    const char16_t* name;          /* as stored */
    const char16_t* relative_path; /* after the guard ('/' separated); empty string if the name was refused */
    uint64_t size;
    int32_t size_known;
    int32_t has_mtime;
    int64_t mtime_unix_seconds;
} unpakz_entry;

typedef struct unpakz_progress {
    uint32_t struct_size;
    uint32_t reserved;
    uint64_t bytes_done;
    uint64_t bytes_total; /* declared, may be an underestimate */
    const char16_t* current_name;
} unpakz_progress;

/* Return non-zero to continue (list) / to extract the entry (filter). */
typedef int32_t (*unpakz_entry_fn)(const unpakz_entry* entry, void* user);
typedef void (*unpakz_progress_fn)(const unpakz_progress* progress, void* user);
typedef int32_t (*unpakz_cancel_fn)(void* user); /* return non-zero to cancel */

typedef struct unpakz_extract_options {
    uint32_t struct_size;
    int32_t overwrite;           /* unpakz_overwrite */
    int32_t restore_timestamps;  /* non-zero: set modification times */
    int32_t preflight_pass;      /* non-zero: read the archive twice so every problem is found before any byte is written */
    unpakz_limits limits;
    unpakz_entry_fn filter;      /* optional: return non-zero to extract the entry */
    unpakz_progress_fn progress; /* optional */
    unpakz_cancel_fn cancelled;  /* optional */
    void* user;
} unpakz_extract_options;

typedef struct unpakz_result {
    uint32_t struct_size;
    uint32_t reserved;
    uint64_t files;
    uint64_t directories;
    uint64_t bytes;
    uint64_t skipped; /* links, special files, existing files that were skipped */
} unpakz_result;

UNPAKZ_API uint32_t unpakz_abi_version(void);
UNPAKZ_API const char* unpakz_library_version(void); /* UTF-8, static storage */

/* Fills `limits` with the safe defaults. */
UNPAKZ_API void unpakz_default_limits(unpakz_limits* limits);
UNPAKZ_API void unpakz_default_extract_options(unpakz_extract_options* options);

/* Lists the entries of the archive at `path` (for "name.partN.rar" the whole set is found). `callback` is called once per entry. */
UNPAKZ_API unpakz_status unpakz_list(const char16_t* path, const unpakz_limits* limits, unpakz_entry_fn callback, void* user, unpakz_error* error);

/* Extracts into `destination` (created if missing). Nothing is written when the plan is refused. */
UNPAKZ_API unpakz_status unpakz_extract(const char16_t* path, const char16_t* destination, const unpakz_extract_options* options,
                                        unpakz_result* result, unpakz_error* error);

/* Runs the path guard on one name (for front ends that show or forward names). Returns UNPAKZ_OK and writes the relative path,
 * or UNPAKZ_UNSAFE_PATH with the reason in `error`. `out` must have room for out_chars UTF-16 units (including the NUL). */
UNPAKZ_API unpakz_status unpakz_check_name(const char16_t* name, char16_t* out, uint32_t out_chars, unpakz_error* error);

#ifdef __cplusplus
}
#endif
#endif /* UNPAKZ_H */
