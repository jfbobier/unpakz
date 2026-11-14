// libarchive reader setup, shared by the engine. Only formats and filters we intend to support are enabled; filters that spawn
// external programs are never enabled.
#pragma once
#include <archive.h>
#include <archive_entry.h>

#include <memory>

#include "engine.h"

namespace unpakz {

struct ArchiveDeleter {
    void operator()(struct archive* a) const noexcept { if (a) archive_read_free(a); }
};
using ReaderPtr = std::unique_ptr<struct archive, ArchiveDeleter>;

// A libarchive reader plus whatever its callbacks point at. `keep` is declared first so it is destroyed last.
struct Reader {
    std::shared_ptr<void> keep;
    ReaderPtr a;
    struct archive* get() const noexcept { return a.get(); }
};

// Opens the source for reading (throws Error). For memory sources the buffer must outlive the reader.
Reader open_reader(const ArchiveSource& source, const Limits& limits);

// Converts the reader's last error into a typed exception.
[[noreturn]] void throw_reader_error(struct archive* a, const char* context);

}  // namespace unpakz
