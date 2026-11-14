// Format-neutral view of "an archive being read entry by entry". Implemented by the libarchive wrapper (zip, tar.gz, RAR4) and by our
// own RAR5 reader. The engine only talks to this interface.
#pragma once
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>

#include "engine.h"

namespace unpakz {

struct EntryFacts {
    std::string name_utf8;                 // as stored, decoded to UTF-8 by the reader
    bool name_valid = true;                // false if the name could not be decoded as text
    EntryKind kind = EntryKind::File;
    bool size_known = false;
    std::uint64_t size = 0;
    std::optional<Timestamp> mtime;
    // Set when this reader knows the entry cannot be extracted (encryption, unknown size, unsupported method). Found at plan time,
    // before anything is written.
    std::string unsupported;
};

// Receives decoded bytes. `offset` is the position of `data` inside the entry (blocks are contiguous unless the format has holes).
using DataSink = std::function<void(const std::uint8_t* data, std::size_t size, std::uint64_t offset)>;

class IEntryReader {
public:
    virtual ~IEntryReader() = default;
    // Advances to the next entry; false at the end. Unread data of the previous entry is skipped (decoded and discarded when a
    // solid stream needs it).
    virtual bool next(EntryFacts& facts) = 0;
    // Delivers the data of the current entry and verifies its integrity; throws Error on any problem (including a checksum mismatch,
    // which is thrown only after the last byte was delivered, so callers must not publish data before this returns).
    virtual void read_data(const DataSink& sink) = 0;
};

std::unique_ptr<IEntryReader> open_entries(const ArchiveSource& source, const Limits& limits, RarEngine rar5);

}  // namespace unpakz
