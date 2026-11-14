// Chooses the reader for a source: our RAR 5 reader when the signature says RAR 5, libarchive for everything else.
#include <cstring>

#include "entry_reader.h"
#include "platform.h"
#include "rar5.h"

namespace unpakz {

std::unique_ptr<IEntryReader> open_libarchive_entries(const ArchiveSource& source, const Limits& limits);

namespace {

bool sniff_rar5(const ArchiveSource& source) {
    std::uint8_t head[8] = {};
    std::size_t n = 0;
    if (source.data != nullptr || source.files.empty()) {
        n = std::min<std::size_t>(source.size, sizeof head);
        if (n) std::memcpy(head, source.data, n);
    } else {
        platform::RawFile f;
        f.open(source.files.front());
        while (n < sizeof head) {
            auto got = f.read(head + n, sizeof head - n);
            if (got == 0) break;
            n += got;
        }
    }
    return rar5::has_signature(head, n);
}

}  // namespace

std::unique_ptr<IEntryReader> open_entries(const ArchiveSource& source, const Limits& limits, RarEngine rar5_engine) {
    if (rar5_engine == RarEngine::Native && sniff_rar5(source)) {
        std::vector<std::unique_ptr<rar5::Volume>> volumes;
        if (source.data != nullptr || source.files.empty()) {
            volumes.push_back(rar5::memory_volume(source.data, source.size));
        } else {
            if (source.files.size() > limits.max_volumes) throw Error(Status::LimitExceeded, "too many volumes");
            for (auto& path : source.files) volumes.push_back(rar5::file_volume(path));
        }
        return rar5::open(std::move(volumes), limits);
    }
    return open_libarchive_entries(source, limits);
}

}  // namespace unpakz
