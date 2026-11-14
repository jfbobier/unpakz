// IEntryReader over libarchive (zip, tar, gzip, RAR 4, and optionally RAR 5).
#include "entry_reader.h"
#include "reader.h"

namespace unpakz {
namespace {

class LibarchiveReader : public IEntryReader {
public:
    explicit LibarchiveReader(Reader r) : reader_(std::move(r)), entry_(archive_entry_new(), archive_entry_free) {}

    bool next(EntryFacts& f) override {
        struct archive* a = reader_.get();
        archive_entry_clear(entry_.get());
        int r = archive_read_next_header2(a, entry_.get());
        if (r == ARCHIVE_EOF) return false;
        if (r != ARCHIVE_OK && r != ARCHIVE_WARN) throw_reader_error(a, "cannot read entry");   // WARN on a header is advisory

        auto* e = entry_.get();
        f = EntryFacts{};
        const char* utf8 = archive_entry_pathname_utf8(e);
        const char* raw = utf8 ? utf8 : archive_entry_pathname(e);
        f.name_utf8 = raw ? raw : "";

        const bool hardlink = archive_entry_hardlink(e) != nullptr || archive_entry_hardlink_utf8(e) != nullptr;
        switch (archive_entry_filetype(e)) {
            case 0:                      // unset: formats from DOS-era hosts (RAR 1.x-3.x on Windows) carry no Unix file type
            case AE_IFREG: f.kind = hardlink ? EntryKind::Hardlink : EntryKind::File; break;
            case AE_IFDIR: f.kind = EntryKind::Directory; break;
            case AE_IFLNK: f.kind = EntryKind::Symlink; break;
            default: f.kind = hardlink ? EntryKind::Hardlink : EntryKind::Special; break;
        }

        f.size_known = archive_entry_size_is_set(e) != 0;
        if (f.size_known) {
            auto sz = archive_entry_size(e);
            if (sz < 0) throw Error(Status::Corrupt, "entry has a negative size");
            f.size = static_cast<std::uint64_t>(sz);
        }
        if (archive_entry_mtime_is_set(e))
            f.mtime = Timestamp{static_cast<std::int64_t>(archive_entry_mtime(e)), static_cast<std::int32_t>(archive_entry_mtime_nsec(e))};
        if (f.kind == EntryKind::File && archive_entry_is_data_encrypted(e)) f.unsupported = "encrypted entries are not supported";
        return true;
    }

    void read_data(const DataSink& sink) override {
        struct archive* a = reader_.get();
        for (;;) {
            const void* buf = nullptr;
            std::size_t size = 0;
            la_int64_t offset = 0;
            int r = archive_read_data_block(a, &buf, &size, &offset);
            if (r == ARCHIVE_EOF) return;
            if (r != ARCHIVE_OK) throw_reader_error(a, "cannot read data");     // WARN too: some readers report a CRC mismatch that way
            if (offset < 0) throw Error(Status::Corrupt, "negative data offset");
            sink(static_cast<const std::uint8_t*>(buf), size, static_cast<std::uint64_t>(offset));
        }
    }

private:
    Reader reader_;
    std::unique_ptr<archive_entry, decltype(&archive_entry_free)> entry_;
};

}  // namespace

std::unique_ptr<IEntryReader> open_libarchive_entries(const ArchiveSource& source, const Limits& limits) {
    return std::make_unique<LibarchiveReader>(open_reader(source, limits));
}

}  // namespace unpakz
