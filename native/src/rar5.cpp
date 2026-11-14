#include "rar5.h"

#include <zlib.h>

extern "C" {
#include "archive_blake2.h"   // libarchive's BLAKE2sp (RAR 5 can use it instead of CRC-32); linked from the vendored static library
}

#include <algorithm>
#include <array>
#include <cstring>
#include <deque>
#include <optional>

#include "guard.h"
#include "platform.h"
#include "rar5_testing.h"

namespace unpakz::rar5 {
namespace {

[[noreturn]] void corrupt(const std::string& m) { throw Error(Status::Corrupt, m); }
[[noreturn]] void unsupported(const std::string& m) { throw Error(Status::Unsupported, m); }

std::uint32_t crc32_update(std::uint32_t crc, const std::uint8_t* data, std::size_t size) {
    while (size > 0) {
        auto n = static_cast<uInt>(std::min<std::size_t>(size, 1u << 30));
        crc = static_cast<std::uint32_t>(::crc32(crc, data, n));
        data += n;
        size -= n;
    }
    return crc;
}

constexpr std::uint8_t kSignature[8] = {0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00};
constexpr std::uint8_t kSignature4[7] = {0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00};
constexpr std::uint64_t kMaxHeaderSize = 2ull * 1024 * 1024;
constexpr std::uint64_t kMaxFileTime = 2'650'467'743'999'999'999ull;

// ---------------------------------------------------------------- volumes

class MemoryVolume : public Volume {
public:
    MemoryVolume(const std::uint8_t* d, std::size_t n) : data_(d), size_(n) {}
    std::uint64_t size() const override { return size_; }
    void read_at(std::uint64_t offset, std::uint8_t* dst, std::size_t count) override {
        if (offset > size_ || count > size_ - offset) corrupt("read past the end of the archive");
        if (count) std::memcpy(dst, data_ + offset, count);
    }

private:
    const std::uint8_t* data_;
    std::size_t size_;
};

class FileVolume : public Volume {
public:
    explicit FileVolume(const std::u16string& path) {
        file_.open(path);
        size_ = static_cast<std::uint64_t>(file_.seek(0, 2));
    }
    std::uint64_t size() const override { return size_; }
    void read_at(std::uint64_t offset, std::uint8_t* dst, std::size_t count) override {
        if (offset > size_ || count > size_ - offset) corrupt("read past the end of the archive");
        file_.seek(static_cast<std::int64_t>(offset), 0);
        while (count > 0) {
            auto n = file_.read(dst, count);
            if (n == 0) corrupt("the archive file ended unexpectedly");
            dst += n;
            count -= n;
        }
    }

private:
    platform::RawFile file_;
    std::uint64_t size_ = 0;
};

// ---------------------------------------------------------------- vint and cursor

bool read_vint(const std::uint8_t* data, std::size_t size, std::uint64_t& value, std::size_t& length) {
    value = 0;
    length = 0;
    for (std::size_t i = 0; i < size && i < 10; ++i) {
        const std::uint8_t b = data[i];
        const std::uint64_t payload = b & 0x7Fu;
        if (i == 9 && payload > 1) return false;   // would not fit in 64 bits
        value |= payload << (7 * i);
        if ((b & 0x80) == 0) {
            length = i + 1;
            return true;
        }
    }
    value = 0;
    return false;   // truncated, or longer than 10 bytes
}

// Bounds-checked forward reader over a header; any overrun is a corrupt archive.
class Cursor {
public:
    Cursor(const std::uint8_t* s, std::size_t n) : s_(s), n_(n) {}
    std::size_t pos() const { return pos_; }
    std::size_t remaining() const { return n_ - pos_; }
    std::uint64_t vint() {
        std::uint64_t v;
        std::size_t len;
        if (!read_vint(s_ + pos_, n_ - pos_, v, len)) truncated();
        pos_ += len;
        return v;
    }
    std::uint32_t u32() {
        if (remaining() < 4) truncated();
        std::uint32_t v = s_[pos_] | (std::uint32_t{s_[pos_ + 1]} << 8) | (std::uint32_t{s_[pos_ + 2]} << 16) | (std::uint32_t{s_[pos_ + 3]} << 24);
        pos_ += 4;
        return v;
    }
    std::uint64_t u64() {
        auto lo = u32();
        auto hi = u32();
        return lo | (std::uint64_t{hi} << 32);
    }
    const std::uint8_t* bytes(std::size_t count) {
        if (count > remaining()) truncated();
        auto* p = s_ + pos_;
        pos_ += count;
        return p;
    }

private:
    [[noreturn]] static void truncated() { corrupt("Truncated RAR header field."); }
    const std::uint8_t* s_;
    std::size_t n_;
    std::size_t pos_ = 0;
};

bool valid_utf8(const std::uint8_t* p, std::size_t n, std::string& out) {
    std::u16string tmp;
    std::string s(reinterpret_cast<const char*>(p), n);
    if (!utf8_to_utf16(s, tmp)) return false;
    out = std::move(s);
    return true;
}

// ---------------------------------------------------------------- files, parts

struct Segment {
    std::size_t volume;
    std::uint64_t offset;
    std::uint64_t size;
};

struct FileInfo {
    std::string name;
    EntryKind kind = EntryKind::File;
    std::uint64_t packed = 0, unpacked = 0;
    std::optional<std::uint32_t> crc;
    std::optional<std::array<std::uint8_t, 32>> blake2;
    bool unknown_size = false, encrypted = false;
    int method = 0;
    std::vector<Segment> segments;
    std::optional<Timestamp> mtime;
    bool solid = false;
    int dict_log = 0, version = 0;
    int group = -1, group_index = 0;
};

struct FilePart {
    std::string name;
    EntryKind kind;
    std::uint64_t unpacked;
    bool unknown_size;
    std::optional<std::uint32_t> crc;
    std::optional<std::array<std::uint8_t, 32>> blake2;
    std::uint64_t comp_info, host;
    bool encrypted;
    std::optional<Timestamp> mtime;
};

std::optional<Timestamp> from_unix(std::uint32_t seconds) { return Timestamp{static_cast<std::int64_t>(seconds), 0}; }

std::optional<Timestamp> from_filetime(std::uint64_t ft) {
    if (ft > kMaxFileTime) return std::nullopt;
    return Timestamp{static_cast<std::int64_t>(ft / 10'000'000ull) - 11644473600ll, static_cast<std::int32_t>((ft % 10'000'000ull) * 100)};
}

EntryKind kind_of(bool redirected, bool directory, std::uint64_t host, std::uint64_t attributes) {
    if (redirected) return EntryKind::Symlink;
    if (directory) return EntryKind::Directory;
    if (host != 1) return EntryKind::File;
    switch (attributes & 0xF000) {
        case 0:
        case 0x8000: return EntryKind::File;
        case 0x4000: return EntryKind::Directory;
        case 0xA000: return EntryKind::Symlink;
        default: return EntryKind::Special;
    }
}

FilePart read_file_part(const std::uint8_t* header, std::size_t header_size, Cursor c, std::size_t extra_size) {
    const auto file_flags = c.vint();
    const auto unpacked = c.vint();
    const auto attributes = c.vint();
    std::optional<Timestamp> mtime;
    if (file_flags & 0x02) mtime = from_unix(c.u32());
    std::optional<std::uint32_t> crc;
    if (file_flags & 0x04) crc = c.u32();
    const auto comp_info = c.vint();
    const auto host = c.vint();
    const auto name_len = c.vint();
    if (name_len > c.remaining()) corrupt("RAR file name overruns its header.");

    const auto* name_bytes = c.bytes(static_cast<std::size_t>(name_len));
    const std::size_t extra_start = header_size - extra_size;
    if (c.pos() > extra_start) corrupt("RAR file name overlaps the extra area.");

    std::string name;
    if (!valid_utf8(name_bytes, static_cast<std::size_t>(name_len), name)) corrupt("RAR file name is not valid UTF-8.");

    bool encrypted = false, redirected = false;
    std::optional<std::array<std::uint8_t, 32>> blake2;
    std::optional<std::uint64_t> version;
    Cursor extra(header + extra_start, extra_size);
    while (extra.remaining() > 0) {
        const auto size = extra.vint();
        if (size == 0 || size > extra.remaining()) corrupt("Malformed RAR extra record.");
        Cursor rc(extra.bytes(static_cast<std::size_t>(size)), static_cast<std::size_t>(size));
        switch (rc.vint()) {
            case 1: encrypted = true; break;
            case 2: {
                if (rc.vint() == 0 && rc.remaining() >= 32) {      // hash type 0 = BLAKE2sp
                    std::array<std::uint8_t, 32> h;
                    std::memcpy(h.data(), rc.bytes(32), 32);
                    blake2 = h;
                }
                break;
            }
            case 4: {                                              // file version: keep every version by suffixing ";N" (as libarchive does)
                rc.vint();                                         // flags
                version = rc.vint();
                break;
            }
            case 5: redirected = true; break;
            case 3: {
                const auto tf = rc.vint();
                if (tf & 0x02) {
                    auto t = (tf & 0x01) ? from_unix(rc.u32()) : from_filetime(rc.u64());
                    if (t) mtime = t;
                }
                break;
            }
            default: break;
        }
    }

    if (version) name += ";" + std::to_string(*version);

    FilePart p;
    p.name = std::move(name);
    p.kind = kind_of(redirected, (file_flags & 0x01) != 0, host, attributes);
    p.unpacked = unpacked;
    p.unknown_size = (file_flags & 0x08) != 0;
    p.crc = crc;
    p.blake2 = blake2;
    p.comp_info = comp_info;
    p.host = host;
    p.encrypted = encrypted;
    p.mtime = mtime;
    return p;
}

// A file whose data started in one volume and continues in the next.
struct Pending {
    FilePart first;
    std::vector<Segment> segments;
    std::uint64_t packed = 0;
};

FileInfo complete(Pending& p, const FilePart& last) {
    const auto& f = p.first;
    FileInfo r;
    r.name = f.name;
    r.kind = f.kind;
    r.packed = p.packed;
    r.unpacked = f.unknown_size ? p.packed : f.unpacked;
    r.crc = last.crc;
    r.blake2 = last.blake2;
    r.unknown_size = f.unknown_size;
    r.encrypted = f.encrypted;
    r.method = static_cast<int>((f.comp_info >> 7) & 7);
    r.segments = std::move(p.segments);
    r.mtime = f.mtime;
    r.solid = (f.comp_info & 0x40) != 0;
    r.dict_log = static_cast<int>((f.comp_info >> 10) & 0xF);
    r.version = static_cast<int>(f.comp_info & 0x3F);
    return r;
}

class VolumeParser {
public:
    VolumeParser(std::vector<std::unique_ptr<Volume>>& volumes, const Limits& limits) : volumes_(volumes), limits_(limits) {}

    std::vector<FileInfo> run() {
        for (std::size_t v = 0; v < volumes_.size(); ++v) {
            const bool more = parse_volume(v);
            const bool last = v == volumes_.size() - 1;
            if (!last && !more) corrupt("Volume " + std::to_string(v + 1) + " says it is the last volume, but more volumes were supplied.");
            if (last && more)
                throw Error(Status::MissingVolume, "The set is incomplete: volume " + std::to_string(v + 2) + " is missing.", static_cast<int>(v + 2));
        }
        if (pending_) corrupt("File '" + pending_->first.name + "' starts in one volume but never continues.");
        return std::move(files_);
    }

private:
    std::vector<std::unique_ptr<Volume>>& volumes_;
    const Limits& limits_;
    std::vector<FileInfo> files_;
    std::optional<Pending> pending_;

    bool parse_volume(std::size_t volume) {
        Volume& vol = *volumes_[volume];
        const std::uint64_t len = vol.size();
        std::uint8_t sig[8] = {};
        if (len >= 8) vol.read_at(0, sig, 8);
        if (len < 8 || std::memcmp(sig, kSignature, 8) != 0) corrupt("Volume " + std::to_string(volume + 1) + " is not a RAR 5 archive.");

        std::uint64_t pos = 8;
        bool first = true, more = false, saw_end = false;

        while (pos < len && !saw_end) {
            if (len - pos < 5) corrupt("Truncated RAR block header.");
            std::uint8_t head[14];
            const auto head_n = static_cast<std::size_t>(std::min<std::uint64_t>(sizeof head, len - pos));
            vol.read_at(pos, head, head_n);
            const std::uint32_t crc_stored = head[0] | (std::uint32_t{head[1]} << 8) | (std::uint32_t{head[2]} << 16) | (std::uint32_t{head[3]} << 24);
            std::uint64_t header_size;
            std::size_t size_len;
            if (!read_vint(head + 4, head_n - 4, header_size, size_len)) corrupt("Invalid RAR header size.");
            if (header_size == 0 || header_size > kMaxHeaderSize) corrupt("RAR header size out of range.");

            const std::uint64_t header_start = pos + 4 + size_len;
            const std::uint64_t header_end = header_start + header_size;
            if (header_end > len) corrupt("RAR header extends past the archive.");

            std::vector<std::uint8_t> raw(size_len + static_cast<std::size_t>(header_size));
            vol.read_at(pos + 4, raw.data(), raw.size());
            if (crc32_update(0, raw.data(), raw.size()) != crc_stored) corrupt("RAR header CRC mismatch.");

            const std::uint8_t* header = raw.data() + size_len;
            const auto header_len = static_cast<std::size_t>(header_size);
            Cursor c(header, header_len);
            const auto type = c.vint();
            const auto flags = c.vint();
            const std::uint64_t extra_size = (flags & 1) ? c.vint() : 0;
            const std::uint64_t data_size = (flags & 2) ? c.vint() : 0;
            if (extra_size > c.remaining()) corrupt("RAR extra area larger than its header.");
            if (data_size > len - header_end) corrupt("RAR data area extends past the archive.");

            if (first) {
                if (type == 4) unsupported("Encrypted RAR headers are not supported.");
                if (type != 1) corrupt("RAR main header missing.");
            }

            const std::uint64_t data_start = header_end;
            switch (type) {
                case 1:
                    if (!first) corrupt("Duplicate RAR main header.");
                    read_main_header(c, volume);
                    break;
                case 2:
                    add_part(read_file_part(header, header_len, c, static_cast<std::size_t>(extra_size)), flags, volume, data_start, data_size);
                    break;
                case 3: break;   // service header (comment, recovery record, quick open, NTFS streams): not extracted
                case 4: unsupported("Encrypted RAR headers are not supported.");
                case 5:
                    saw_end = true;
                    more = c.remaining() > 0 && (c.vint() & 1) != 0;
                    break;
                default:
                    if ((flags & 4) == 0) unsupported("Unknown mandatory RAR block type " + std::to_string(type) + ".");
                    break;
            }

            first = false;
            pos = data_start + data_size;
        }

        if (!saw_end) corrupt("RAR end-of-archive block missing.");
        return more;
    }

    void read_main_header(Cursor& c, std::size_t volume) {
        const auto archive_flags = c.vint();
        const bool is_volume = (archive_flags & 0x01) != 0;
        const std::uint64_t number = (archive_flags & 0x02) ? c.vint() : 0;

        if (volumes_.size() > 1 && !is_volume) corrupt("Volume " + std::to_string(volume + 1) + " is not part of a multi-volume set.");
        if (number > volume) throw Error(Status::MissingVolume, "The set is incomplete: volume " + std::to_string(volume + 1) + " is missing.", static_cast<int>(volume + 1));
        if (number < volume) corrupt("Volume " + std::to_string(volume + 1) + " is out of order or duplicated.");
    }

    void add_part(FilePart part, std::uint64_t flags, std::size_t volume, std::uint64_t data_start, std::uint64_t data_size) {
        const bool split_before = (flags & 0x08) != 0;
        const bool split_after = (flags & 0x10) != 0;
        const Segment segment{volume, data_start, data_size};

        if (pending_) {
            if (!split_before) corrupt("File '" + pending_->first.name + "' should continue in this volume, but a different entry follows.");
            const auto& f = pending_->first;
            if (part.name != f.name || part.unpacked != f.unpacked || part.comp_info != f.comp_info || part.host != f.host)
                corrupt("The parts of '" + f.name + "' disagree about the file.");

            pending_->segments.push_back(segment);
            pending_->packed += data_size;
            if (split_after) return;

            auto done = std::move(*pending_);
            pending_.reset();
            files_.push_back(complete(done, part));
            return;
        }

        if (split_before) corrupt("'" + part.name + "' is a continuation, but nothing precedes it.");
        if (files_.size() >= limits_.max_entries) throw Error(Status::LimitExceeded, "Too many entries.");

        Pending started{std::move(part), {segment}, data_size};
        if (split_after) {
            pending_ = std::move(started);
            return;
        }
        files_.push_back(complete(started, started.first));
    }
};

// A file's compressed data as one forward-only stream, even when it is split across volumes.
class PackedStream {
public:
    PackedStream(std::vector<std::unique_ptr<Volume>>& volumes, const std::vector<Segment>& segments) : volumes_(volumes), segments_(segments) {
        for (auto& s : segments) remaining_ += s.size;
    }
    std::uint64_t remaining() const { return remaining_; }

    void read_exactly(std::uint8_t* dst, std::size_t count) {
        if (count > remaining_) corrupt("Compressed data ended unexpectedly.");
        while (count > 0) {
            while (offset_ >= segments_[segment_].size) { ++segment_; offset_ = 0; }
            const auto& s = segments_[segment_];
            const auto n = static_cast<std::size_t>(std::min<std::uint64_t>(s.size - offset_, count));
            volumes_[s.volume]->read_at(s.offset + offset_, dst, n);
            dst += n;
            count -= n;
            offset_ += n;
            remaining_ -= n;
        }
    }

private:
    std::vector<std::unique_ptr<Volume>>& volumes_;
    const std::vector<Segment>& segments_;
    std::size_t segment_ = 0;
    std::uint64_t offset_ = 0;
    std::uint64_t remaining_ = 0;
};

// ---------------------------------------------------------------- filters

namespace filters {

constexpr std::uint32_t kFileSize = 0x1000000;

inline std::uint32_t rd32(const std::uint8_t* s, std::size_t i) {
    return s[i] | (std::uint32_t{s[i + 1]} << 8) | (std::uint32_t{s[i + 2]} << 16) | (std::uint32_t{s[i + 3]} << 24);
}
inline void wr32(std::uint8_t* d, std::size_t i, std::uint32_t v) {
    d[i] = static_cast<std::uint8_t>(v);
    d[i + 1] = static_cast<std::uint8_t>(v >> 8);
    d[i + 2] = static_cast<std::uint8_t>(v >> 16);
    d[i + 3] = static_cast<std::uint8_t>(v >> 24);
}

// Undoes delta coding: the block holds `channels` interleaved planes stored one after another.
void delta(const std::uint8_t* src, std::size_t n, int channels, std::uint8_t* dst) {
    std::size_t s = 0;
    for (int channel = 0; channel < channels; ++channel) {
        std::uint8_t prev = 0;
        for (std::size_t d = static_cast<std::size_t>(channel); d < n; d += static_cast<std::size_t>(channels)) {
            prev = static_cast<std::uint8_t>(prev - src[s++]);
            dst[d] = prev;
        }
    }
}

// Converts absolute x86 CALL (E8) and optionally JMP (E9) targets back to relative ones.
void e8(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, bool extended, std::uint8_t* dst) {
    std::memcpy(dst, src, n);
    for (std::size_t i = 0; n >= 4 && i < n - 4;) {
        const std::uint8_t b = src[i++];
        if (b != 0xE8 && !(extended && b == 0xE9)) continue;

        const auto offset = static_cast<std::uint32_t>((static_cast<std::int64_t>(i) + start_rel) % kFileSize);
        const std::uint32_t addr = rd32(src, i);

        if ((addr & 0x80000000u) != 0) {
            if (((addr + offset) & 0x80000000u) == 0) wr32(dst, i, addr + kFileSize);
        } else if (((addr - kFileSize) & 0x80000000u) != 0) {
            wr32(dst, i, addr - offset);
        }
        i += 4;
    }
}

// Converts absolute ARM BL (0xEB) targets back to relative ones.
void arm(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, std::uint8_t* dst) {
    std::memcpy(dst, src, n);
    for (std::size_t i = 0; n >= 3 && i < n - 3; i += 4) {
        if (src[i + 3] != 0xEB) continue;
        std::uint32_t offset = rd32(src, i) & 0x00FFFFFFu;
        offset -= static_cast<std::uint32_t>((static_cast<std::int64_t>(i) + start_rel) / 4);
        offset = (offset & 0x00FFFFFFu) | 0xEB000000u;
        wr32(dst, i, offset);
    }
}

}  // namespace filters

// ---------------------------------------------------------------- bit reader and Huffman tables

constexpr int kHuffBc = 20, kHuffNc = 306, kHuffDc = 64, kHuffLdc = 16, kHuffRc = 44;
constexpr int kHuffTableSize = kHuffNc + kHuffDc + kHuffRc + kHuffLdc;
constexpr std::int64_t kMaxFilterLength = 0x400000;
constexpr std::size_t kMaxPendingFilters = 1024;
constexpr std::uint64_t kInitialWindow = 1ull << 16;

// MSB-first bit reader over one block. Reading at or beyond the end is a corrupt stream.
struct Bits {
    const std::uint8_t* data;
    std::size_t size;
    std::size_t in = 0;
    int bit = 0;

    Bits(const std::uint8_t* d, std::size_t n) : data(d), size(n) {}

    std::uint32_t at(std::size_t i) const { return i < size ? data[i] : 0u; }
    int peek16() const {
        if (in >= size) corrupt("Premature end of RAR block data.");
        const std::uint32_t v = (at(in) << 16) | (at(in + 1) << 8) | at(in + 2);
        return static_cast<int>((v >> (8 - bit)) & 0xFFFFu);
    }
    std::uint32_t peek32() const {
        if (in >= size) corrupt("Premature end of RAR block data.");
        const std::uint32_t v = (at(in) << 24) | (at(in + 1) << 16) | (at(in + 2) << 8) | at(in + 3);
        return (v << bit) | (at(in + 4) >> (8 - bit));
    }
    void skip(int n) {
        const int nb = bit + n;
        in += static_cast<std::size_t>(nb >> 3);
        bit = nb & 7;
    }
    int read_bits(int n) {
        const int v = peek16() >> (16 - n);
        skip(n);
        return v;
    }
};

class HuffTable {
public:
    explicit HuffTable(int size) : size_(size), quick_bits_(size == kHuffNc ? 10 : 7), decode_num_(static_cast<std::size_t>(size), 0) {}

    // Builds the decode tables from 4-bit code lengths; false if the lengths over-subscribe the code space.
    bool build(const std::uint8_t* lengths) {
        std::array<int, 16> lc{};
        std::fill(decode_num_.begin(), decode_num_.end(), 0);
        for (int i = 0; i < size_; ++i) lc[lengths[i] & 15u]++;

        lc[0] = 0;
        decode_pos_[0] = 0;
        decode_len_[0] = 0;
        int upper = 0;
        for (int i = 1; i < 16; ++i) {
            upper += lc[static_cast<std::size_t>(i)];
            decode_len_[i] = upper << (16 - i);
            decode_pos_[i] = decode_pos_[i - 1] + lc[static_cast<std::size_t>(i - 1)];
            upper <<= 1;
        }
        if (upper > 65536) return false;

        std::array<int, 16> clone = decode_pos_;
        for (int i = 0; i < size_; ++i) {
            const int clen = lengths[i] & 15;
            if (clen > 0) decode_num_[static_cast<std::size_t>(clone[static_cast<std::size_t>(clen)]++)] = static_cast<std::uint16_t>(i);
        }

        const int quick_size = 1 << quick_bits_;
        int cur_len = 1;
        for (int code = 0; code < quick_size; ++code) {
            const int bit_field = code << (16 - quick_bits_);
            while (cur_len < 16 && bit_field >= decode_len_[cur_len]) ++cur_len;

            quick_len_[static_cast<std::size_t>(code)] = static_cast<std::uint8_t>(cur_len);
            const int dist = (bit_field - decode_len_[cur_len - 1]) >> (16 - cur_len);
            const int pos = decode_pos_[cur_len & 15] + dist;
            quick_num_[static_cast<std::size_t>(code)] = (cur_len < 16 && pos < size_) ? decode_num_[static_cast<std::size_t>(pos)] : std::uint16_t{0};
        }
        return true;
    }

    int decode(Bits& bits) const {
        const int bit_field = bits.peek16() & 0xFFFE;

        if (bit_field < decode_len_[quick_bits_]) {
            const int code = bit_field >> (16 - quick_bits_);
            bits.skip(quick_len_[static_cast<std::size_t>(code)]);
            return quick_num_[static_cast<std::size_t>(code)];
        }

        int n = 15;
        for (int i = quick_bits_ + 1; i < 15; ++i) {
            if (bit_field < decode_len_[i]) {
                n = i;
                break;
            }
        }

        bits.skip(n);
        const int dist = (bit_field - decode_len_[n - 1]) >> (16 - n);
        int pos = decode_pos_[n] + dist;
        if (pos >= size_ || pos < 0) pos = 0;
        return decode_num_[static_cast<std::size_t>(pos)];
    }

private:
    int size_;
    int quick_bits_;
    std::array<int, 16> decode_len_{};
    std::array<int, 16> decode_pos_{};
    std::vector<std::uint16_t> decode_num_;
    std::array<std::uint8_t, 1 << 10> quick_len_{};
    std::array<std::uint16_t, 1 << 10> quick_num_{};
};

// ---------------------------------------------------------------- decoder

using Emit = std::function<void(const std::uint8_t*, std::size_t)>;

class Decoder {
public:
    Decoder(std::uint64_t dictionary, std::uint64_t window_limit, DecoderStats& stats)
        : dictionary_(dictionary), window_limit_(window_limit), bd_(kHuffBc), ld_(kHuffNc), dd_(kHuffDc), ldd_(kHuffLdc), rd_(kHuffRc), stats_(stats) {
        if (dictionary == 0 || window_limit == 0) throw Error(Status::InvalidArgument, "decoder sizes must be positive");
        const auto initial = std::min<std::uint64_t>(kInitialWindow, window_limit);
        window_.assign(static_cast<std::size_t>(initial), 0);
        mask_ = static_cast<std::size_t>(initial - 1);
    }

    static std::uint64_t dictionary_size(int dictionary_log) { return 0x20000ull << dictionary_log; }

    // Decodes one file's compressed data. `solid` continues from the previous file's window.
    void decode(PackedStream& input, std::uint64_t unpacked_size, bool solid, const Emit& sink) {
        if (!solid) reset_stream();
        file_base_ = write_;
        file_size_ = unpacked_size;
        filters_.clear();
        last_filter_start_ = last_filter_length_ = 0;

        if (input.remaining() == 0) {
            // No packed data at all: only a zero-length file can look like that.
            if (unpacked_size != 0) corrupt("Compressed data ended unexpectedly.");
            return;
        }

        // A file ends at its flagged last block, or when its packed data is used up (solid archives do not always flag it);
        // either way the exact size check below is what proves the output is complete.
        for (;;) {
            const bool last = decode_block(input, sink);
            if (last || input.remaining() == 0) break;
        }

        flush(sink, true);
        if (write_ - file_base_ != unpacked_size) corrupt("Decompressed size does not match the declared size.");
    }

private:
    struct Filter {
        int type;
        std::uint64_t start_abs;
        std::int64_t start_rel;
        std::int64_t length;
        int channels;
    };

    std::uint64_t dictionary_, window_limit_;
    std::vector<std::uint8_t> window_;
    std::size_t mask_ = 0;
    std::uint64_t write_ = 0, last_write_ = 0, file_base_ = 0, file_size_ = 0;
    std::array<std::uint64_t, 4> dist_{};
    std::uint64_t last_len_ = 0;
    HuffTable bd_, ld_, dd_, ldd_, rd_;
    bool have_tables_ = false;
    std::deque<Filter> filters_;
    std::int64_t last_filter_start_ = 0, last_filter_length_ = 0;
    std::vector<std::uint8_t> filter_src_, filter_dst_, block_;
    DecoderStats& stats_;

    void reset_stream() {
        write_ = last_write_ = 0;
        dist_.fill(0);
        last_len_ = 0;
        have_tables_ = false;
    }

    bool decode_block(PackedStream& input, const Emit& sink) {
        std::uint8_t head[5];
        input.read_exactly(head, 2);
        const std::uint8_t flags = head[0];
        const std::uint8_t checksum = head[1];
        const int size_bytes = ((flags >> 3) & 7) + 1;
        if (size_bytes > 3) corrupt("Unsupported RAR block size field.");
        input.read_exactly(head + 2, static_cast<std::size_t>(size_bytes));

        std::uint32_t block_size = 0;
        for (int i = 0; i < size_bytes; ++i) block_size |= std::uint32_t{head[2 + i]} << (8 * i);

        const auto expected = static_cast<std::uint8_t>(0x5A ^ flags ^ (block_size & 0xFF) ^ ((block_size >> 8) & 0xFF) ^ ((block_size >> 16) & 0xFF));
        if (expected != checksum) corrupt("RAR block header checksum mismatch.");

        const bool table_present = (flags & 0x80) != 0;
        const bool is_last = (flags & 0x40) != 0;
        const int bit_size = (flags & 7) + 1;

        if (block_size > input.remaining()) corrupt("Compressed data ended unexpectedly.");
        block_.resize(block_size);
        if (block_size) input.read_exactly(block_.data(), block_size);

        Bits bits(block_.data(), block_.size());
        if (table_present) parse_tables(bits);
        else if (!have_tables_) corrupt("RAR block uses Huffman tables that were never defined.");

        decode_symbols(bits, static_cast<int>(block_size), bit_size, sink);
        return is_last;
    }

    static int read_nibble(const std::uint8_t* data, std::size_t size, int& nibble) {
        const auto byte_index = static_cast<std::size_t>(nibble >> 1);
        if (byte_index >= size) corrupt("Truncated Huffman table data.");
        const int v = (nibble & 1) == 0 ? data[byte_index] >> 4 : data[byte_index] & 0x0F;
        ++nibble;
        return v;
    }

    void parse_tables(Bits& bits) {
        std::uint8_t bit_length[kHuffBc] = {};
        int nibble = 0;   // index of the next 4-bit unit

        for (int w = 0; w < kHuffBc;) {
            int value = read_nibble(bits.data, bits.size, nibble);
            if (value != 15) {
                bit_length[w++] = static_cast<std::uint8_t>(value);
                continue;
            }
            value = read_nibble(bits.data, bits.size, nibble);
            if (value == 0) {
                bit_length[w++] = 15;
            } else {
                for (int k = 0; k < value + 2 && w < kHuffBc; ++k) bit_length[w++] = 0;
            }
        }

        bits.in = static_cast<std::size_t>(nibble >> 1);
        bits.bit = (nibble & 1) * 4;

        if (!bd_.build(bit_length)) corrupt("Invalid RAR Huffman table.");

        std::array<std::uint8_t, kHuffTableSize> table{};
        for (int i = 0; i < kHuffTableSize;) {
            const int num = bd_.decode(bits);
            if (num < 16) {
                table[static_cast<std::size_t>(i++)] = static_cast<std::uint8_t>(num);
            } else if (num < 18) {
                int n;
                if (num == 16) { n = (bits.peek16() >> 13) + 3; bits.skip(3); }
                else { n = (bits.peek16() >> 9) + 11; bits.skip(7); }

                if (i == 0) corrupt("Invalid RAR Huffman table repeat.");
                while (n-- > 0 && i < kHuffTableSize) {
                    table[static_cast<std::size_t>(i)] = table[static_cast<std::size_t>(i - 1)];
                    ++i;
                }
            } else {
                int n;
                if (num == 18) { n = (bits.peek16() >> 13) + 3; bits.skip(3); }
                else { n = (bits.peek16() >> 9) + 11; bits.skip(7); }

                while (n-- > 0 && i < kHuffTableSize) table[static_cast<std::size_t>(i++)] = 0;
            }
        }

        if (!ld_.build(table.data()) || !dd_.build(table.data() + kHuffNc) || !ldd_.build(table.data() + kHuffNc + kHuffDc) ||
            !rd_.build(table.data() + kHuffNc + kHuffDc + kHuffLdc))
            corrupt("Invalid RAR Huffman table.");

        have_tables_ = true;
    }

    void decode_symbols(Bits& bits, int block_size, int bit_size, const Emit& sink) {
        const std::uint64_t half = window_limit_ >> 1;

        for (;;) {
            if (write_ - last_write_ >= half) flush(sink, false);

            if (static_cast<int>(bits.in) > block_size - 1 || (static_cast<int>(bits.in) == block_size - 1 && bits.bit >= bit_size)) return;

            const int num = ld_.decode(bits);

            if (num < 256) {
                if (write_ - file_base_ >= file_size_) corrupt("Decompressed data exceeds the declared size.");
                ensure_window(write_ + 1);
                window_[static_cast<std::size_t>(write_ & mask_)] = static_cast<std::uint8_t>(num);
                ++write_;
            } else if (num >= 262) {
                std::uint64_t len = static_cast<std::uint64_t>(decode_length(bits, num - 262));
                const int slot = dd_.decode(bits);
                const std::uint64_t dist = decode_distance(bits, slot);

                if (dist > 0x100) {
                    ++len;
                    if (dist > 0x2000) {
                        ++len;
                        if (dist > 0x40000) ++len;
                    }
                }

                dist_[3] = dist_[2];
                dist_[2] = dist_[1];
                dist_[1] = dist_[0];
                dist_[0] = dist;
                last_len_ = len;
                copy_string(len, dist);
            } else if (num == 256) {
                parse_filter(bits);
            } else if (num == 257) {
                if (last_len_ != 0) copy_string(last_len_, dist_[0]);
            } else {
                const auto idx = static_cast<std::size_t>(num - 258);
                const std::uint64_t dist = dist_[idx];
                for (std::size_t i = idx; i > 0; --i) dist_[i] = dist_[i - 1];
                dist_[0] = dist;

                const auto len = static_cast<std::uint64_t>(decode_length(bits, rd_.decode(bits)));
                last_len_ = len;
                copy_string(len, dist);
            }
        }
    }

    static int decode_length(Bits& bits, int code) {
        int length = 2;
        int lbits;
        if (code < 8) {
            lbits = 0;
            length += code;
        } else {
            lbits = (code / 4) - 1;
            length += (4 | (code & 3)) << lbits;
        }
        if (lbits > 0) length += bits.read_bits(lbits);
        return length;
    }

    std::uint64_t decode_distance(Bits& bits, int slot) {
        std::uint64_t dist = 1;
        int dbits;
        if (slot < 4) {
            dbits = 0;
            dist += static_cast<std::uint64_t>(slot);
        } else {
            dbits = (slot / 2) - 1;
            dist += static_cast<std::uint64_t>(2 | (slot & 1)) << dbits;
        }

        if (dbits > 0) {
            if (dbits >= 4) {
                if (dbits > 4) {
                    const std::uint32_t add = bits.peek32();
                    bits.skip(dbits - 4);
                    dist += static_cast<std::uint64_t>((add >> (36 - dbits)) << 4);
                }
                dist += static_cast<std::uint64_t>(ldd_.decode(bits));   // the low four bits come from their own Huffman table
            } else {
                dist += static_cast<std::uint64_t>(bits.read_bits(dbits));
            }
        }
        return dist;
    }

    // ---- window

    void ensure_window(std::uint64_t need_total) {
        if (need_total <= window_.size()) return;
        if (window_.size() >= window_limit_) return;   // full size: the window wraps from here on

        std::uint64_t size = window_.size();
        while (size < need_total && size < window_limit_) size <<= 1;
        size = std::min(size, window_limit_);

        window_.resize(static_cast<std::size_t>(size), 0);   // nothing has wrapped yet, so positions are preserved
        mask_ = static_cast<std::size_t>(size - 1);
    }

    void copy_string(std::uint64_t len, std::uint64_t dist) {
        const std::uint64_t produced = write_ - file_base_;
        if (produced > file_size_ || len > file_size_ - produced) corrupt("Decompressed data exceeds the declared size.");
        if (dist == 0 || dist > dictionary_ || dist > write_ || dist > window_limit_) corrupt("Invalid match distance.");

        ensure_window(write_ + len);
        std::uint8_t* window = window_.data();
        const std::size_t mask = mask_;
        const std::uint64_t w = write_;
        for (std::uint64_t i = 0; i < len; ++i) window[static_cast<std::size_t>((w + i) & mask)] = window[static_cast<std::size_t>((w + i - dist) & mask)];
        write_ += len;
    }

    // ---- output + filters

    void flush(const Emit& sink, bool final) {
        for (;;) {
            if (filters_.empty()) {
                emit(sink, last_write_, write_);
                last_write_ = write_;
                return;
            }

            const Filter f = filters_.front();
            if (write_ >= f.start_abs + static_cast<std::uint64_t>(f.length)) {
                if (last_write_ < f.start_abs) {
                    emit(sink, last_write_, f.start_abs);
                    last_write_ = f.start_abs;
                }
                run_filter(f, sink);
                filters_.pop_front();
                last_write_ = f.start_abs + static_cast<std::uint64_t>(f.length);
                continue;
            }

            // Filter range not fully decoded yet: everything before it can go out now.
            const std::uint64_t up_to = std::min(f.start_abs, write_);
            if (up_to > last_write_) {
                emit(sink, last_write_, up_to);
                last_write_ = up_to;
            }

            if (final) corrupt("A RAR filter extends beyond the end of the data.");
            return;
        }
    }

    void emit(const Emit& sink, std::uint64_t from, std::uint64_t to) {
        if (to <= from) return;
        const auto start = static_cast<std::size_t>(from & mask_);
        const auto length = static_cast<std::size_t>(to - from);
        if (start + length <= window_.size()) {
            sink(window_.data() + start, length);
            return;
        }
        const std::size_t first = window_.size() - start;
        sink(window_.data() + start, first);
        sink(window_.data(), length - first);
    }

    void run_filter(const Filter& f, const Emit& sink) {
        const auto length = static_cast<std::size_t>(f.length);
        if (filter_src_.size() < length) {
            filter_src_.resize(length);
            filter_dst_.resize(length);
        }

        copy_from_window(f.start_abs, length, filter_src_.data());
        ++stats_.filters;

        switch (f.type) {
            case 0: ++stats_.delta; filters::delta(filter_src_.data(), length, f.channels, filter_dst_.data()); break;
            case 1:
            case 2:
                if (f.type == 1) ++stats_.e8; else ++stats_.e8e9;
                filters::e8(filter_src_.data(), length, f.start_rel, f.type == 2, filter_dst_.data());
                break;
            default: ++stats_.arm; filters::arm(filter_src_.data(), length, f.start_rel, filter_dst_.data()); break;
        }
        sink(filter_dst_.data(), length);
    }

    void copy_from_window(std::uint64_t start_abs, std::size_t length, std::uint8_t* dst) {
        const auto start = static_cast<std::size_t>(start_abs & mask_);
        if (start + length <= window_.size()) {
            std::memcpy(dst, window_.data() + start, length);
            return;
        }
        const std::size_t first = window_.size() - start;
        std::memcpy(dst, window_.data() + start, first);
        std::memcpy(dst + first, window_.data(), length - first);
    }

    void parse_filter(Bits& bits) {
        const std::int64_t start = read_filter_value(bits);
        const std::int64_t length = read_filter_value(bits);
        const int type = bits.peek16() >> 13;
        bits.skip(3);
        if (type > 3) unsupported("Unsupported RAR filter type " + std::to_string(type) + ".");

        const std::int64_t start_rel = static_cast<std::int64_t>(write_ - file_base_) + start;
        const bool valid_start = start_rel >= last_filter_start_ + last_filter_length_;   // filters must not overlap (0/0 = none yet)
        if (length < 4 || length > kMaxFilterLength || !valid_start || static_cast<std::uint64_t>(length) > (dictionary_ >> 1)) corrupt("Invalid RAR filter.");
        if (filters_.size() >= kMaxPendingFilters) corrupt("Too many pending RAR filters.");

        int channels = 0;
        if (type == 0) channels = bits.read_bits(5) + 1;

        filters_.push_back({type, write_ + static_cast<std::uint64_t>(start), start_rel, length, channels});
        last_filter_start_ = start_rel;
        last_filter_length_ = length;
    }

    static std::int64_t read_filter_value(Bits& bits) {
        const int bytes = bits.read_bits(2) + 1;
        std::int64_t value = 0;
        for (int i = 0; i < bytes; ++i) {
            value += static_cast<std::int64_t>(bits.peek16() >> 8) << (i * 8);
            bits.skip(8);
        }
        return value;
    }
};

// ---------------------------------------------------------------- reader

std::uint64_t next_power_of_two(std::uint64_t value) {
    std::uint64_t size = 1ull << 16;
    while (size < value && size < (1ull << 40)) size <<= 1;
    return size;
}

// Running integrity check over a file's output: CRC-32 and/or BLAKE2sp, whichever the header carries (both if both).
class Integrity {
public:
    explicit Integrity(const FileInfo& f) : crc_expected_(f.crc), b2_expected_(f.blake2) {
        if (b2_expected_) blake2sp_init(&b2_, 32);
    }
    void update(const std::uint8_t* d, std::size_t n) {
        crc_ = crc32_update(crc_, d, n);
        if (b2_expected_) blake2sp_update(&b2_, d, n);
    }
    void verify() {
        if (crc_expected_ && crc_ != *crc_expected_) corrupt("CRC-32 mismatch.");
        if (b2_expected_) {
            std::uint8_t out[32];
            blake2sp_final(&b2_, out, 32);
            if (std::memcmp(out, b2_expected_->data(), 32) != 0) corrupt("BLAKE2sp mismatch.");
        }
    }

private:
    std::optional<std::uint32_t> crc_expected_;
    std::optional<std::array<std::uint8_t, 32>> b2_expected_;
    std::uint32_t crc_ = 0;
    blake2sp_state b2_{};
};

class ReaderImpl : public Reader {
public:
    ReaderImpl(std::vector<std::unique_ptr<Volume>> volumes, std::vector<FileInfo> files, const Limits& limits)
        : volumes_(std::move(volumes)), files_(std::move(files)), limits_(limits) {
        assign_groups();
    }

    const DecoderStats& stats() const override { return stats_; }
    int volume_count() const override { return static_cast<int>(volumes_.size()); }

    bool next(EntryFacts& facts) override {
        if (next_ >= files_.size()) return false;
        current_ = next_++;
        const FileInfo& f = files_[current_];
        if (f.kind == EntryKind::Directory && f.unpacked != 0) corrupt("Directory entry has data.");   // found at plan time, before anything is written
        facts = EntryFacts{};
        facts.name_utf8 = f.name;
        facts.name_valid = true;
        facts.kind = f.kind;
        facts.size_known = !f.unknown_size;
        facts.size = f.unpacked;
        facts.mtime = f.mtime;
        if (f.kind == EntryKind::File) facts.unsupported = unsupported_reason(f);
        return true;
    }

    void read_data(const DataSink& sink) override {
        const FileInfo& f = files_[current_];
        if (f.kind == EntryKind::Directory) {
            if (f.unpacked != 0) corrupt("Directory entry has data.");
            return;
        }
        if (f.kind != EntryKind::File) return;

        if (auto why = unsupported_reason(f); !why.empty()) unsupported(why);

        if (f.method == 0) {
            if (f.packed != f.unpacked) corrupt("Stored entry has mismatched sizes.");
            read_stored(f, sink);
            return;
        }
        read_compressed(current_, sink);
    }

private:
    std::vector<std::unique_ptr<Volume>> volumes_;
    std::vector<FileInfo> files_;
    Limits limits_;
    std::vector<std::vector<std::size_t>> groups_;
    DecoderStats stats_;
    std::size_t next_ = 0, current_ = 0;

    std::unique_ptr<Decoder> decoder_;
    int decoder_group_ = -1;
    int decoder_next_ = 0;

    static std::string unsupported_reason(const FileInfo& f) {
        if (f.encrypted) return "Encrypted RAR entries are not supported yet.";
        if (f.unknown_size) return "RAR entries of unknown size are not supported.";
        if (f.method > 5) return "RAR compression method " + std::to_string(f.method) + " is not supported.";
        if (f.method != 0 && f.version != 0) return "RAR compression version " + std::to_string(f.version) + " is not supported.";
        if (!f.crc && !f.blake2) return "RAR entry carries no integrity data (CRC-32 or BLAKE2sp).";
        return {};
    }

    // Chains compressed files into solid groups (a non-solid file starts a new group); stored files and non-files take no part.
    void assign_groups() {
        std::vector<std::size_t>* current = nullptr;
        for (std::size_t i = 0; i < files_.size(); ++i) {
            FileInfo& f = files_[i];
            if (f.kind != EntryKind::File || f.method == 0) continue;
            if (!current || !f.solid) {
                groups_.emplace_back();
                current = &groups_.back();
            }
            f.group = static_cast<int>(groups_.size() - 1);
            f.group_index = static_cast<int>(current->size());
            current->push_back(i);
        }
    }

    void read_stored(const FileInfo& f, const DataSink& sink) {
        constexpr std::size_t kChunk = 1 << 20;
        std::vector<std::uint8_t> buf(static_cast<std::size_t>(std::min<std::uint64_t>(kChunk, std::max<std::uint64_t>(f.packed, 1))));
        Integrity integrity(f);
        std::uint64_t offset = 0;
        for (const auto& s : f.segments) {
            for (std::uint64_t pos = 0; pos < s.size;) {
                const auto n = static_cast<std::size_t>(std::min<std::uint64_t>(buf.size(), s.size - pos));
                volumes_[s.volume]->read_at(s.offset + pos, buf.data(), n);
                integrity.update(buf.data(), n);
                sink(buf.data(), n, offset);
                offset += n;
                pos += n;
            }
        }
        integrity.verify();
    }

    void read_compressed(std::size_t index, const DataSink& sink) {
        const FileInfo& f = files_[index];
        const auto& members = groups_[static_cast<std::size_t>(f.group)];
        int dict_log = 0;
        std::uint64_t total = 0;
        for (auto idx : members) {
            dict_log = std::max(dict_log, files_[idx].dict_log);
            total += files_[idx].unpacked;
            if (total < files_[idx].unpacked) total = ~0ull;
        }

        const std::uint64_t dictionary = Decoder::dictionary_size(dict_log);
        const std::uint64_t window_limit = std::min(dictionary, next_power_of_two(total));
        if (window_limit > limits_.max_dictionary) throw Error(Status::LimitExceeded, "The RAR dictionary exceeds the configured memory limit.");

        Integrity integrity(f);
        std::uint64_t offset = 0;
        const Emit verify = [&](const std::uint8_t* d, std::size_t n) {
            integrity.update(d, n);
            sink(d, n, offset);
            offset += n;
        };
        const Emit discard = [](const std::uint8_t*, std::size_t) {};

        try {
            if (!decoder_ || decoder_group_ != f.group || decoder_next_ > f.group_index) {
                decoder_ = std::make_unique<Decoder>(dictionary, window_limit, stats_);
                decoder_group_ = f.group;
                decoder_next_ = 0;
            }

            while (decoder_next_ < f.group_index) {
                const FileInfo& earlier = files_[members[static_cast<std::size_t>(decoder_next_)]];
                if (auto why = unsupported_reason(earlier); !why.empty()) unsupported(why);
                PackedStream packed(volumes_, earlier.segments);
                decoder_->decode(packed, earlier.unpacked, decoder_next_ > 0, discard);
                ++stats_.files_decoded;
                ++decoder_next_;
            }

            PackedStream packed(volumes_, f.segments);
            decoder_->decode(packed, f.unpacked, f.group_index > 0, verify);
            ++stats_.files_decoded;
            decoder_next_ = f.group_index + 1;
        } catch (...) {
            decoder_.reset();   // a failed decode leaves the shared window in an unknown state
            decoder_group_ = -1;
            throw;
        }

        integrity.verify();
    }
};

}  // namespace

std::unique_ptr<Volume> memory_volume(const std::uint8_t* data, std::size_t size) { return std::make_unique<MemoryVolume>(data, size); }
std::unique_ptr<Volume> file_volume(const std::u16string& path) { return std::make_unique<FileVolume>(path); }

bool has_signature(const std::uint8_t* data, std::size_t size) { return size >= sizeof kSignature && std::memcmp(data, kSignature, sizeof kSignature) == 0; }
bool has_rar4_signature(const std::uint8_t* data, std::size_t size) { return size >= sizeof kSignature4 && std::memcmp(data, kSignature4, sizeof kSignature4) == 0; }

std::unique_ptr<Reader> open(std::vector<std::unique_ptr<Volume>> volumes, const Limits& limits) {
    if (volumes.empty()) throw Error(Status::InvalidArgument, "At least one volume is required.");
    if (volumes.size() > limits.max_volumes) throw Error(Status::LimitExceeded, "Too many volumes.");
    VolumeParser parser(volumes, limits);
    auto files = parser.run();
    return std::make_unique<ReaderImpl>(std::move(volumes), std::move(files), limits);
}

}  // namespace unpakz::rar5

// ---------------------------------------------------------------- test hooks (see rar5_testing.h)

namespace unpakz::rar5::testing {

struct TestDecoder::Impl {
    Impl(std::uint64_t dictionary, std::uint64_t window) : decoder(dictionary, window, stats) {}
    DecoderStats stats;
    Decoder decoder;
    std::vector<std::unique_ptr<Volume>> volumes;
    std::vector<Segment> segments;
};

TestDecoder::TestDecoder(std::uint64_t dictionary, std::uint64_t window_limit) : impl_(std::make_unique<Impl>(dictionary, window_limit)) {}
TestDecoder::~TestDecoder() = default;

void TestDecoder::decode(const std::vector<std::pair<const std::uint8_t*, std::size_t>>& segments, std::uint64_t unpacked_size, bool solid,
                         const std::function<void(const std::uint8_t*, std::size_t)>& sink) {
    impl_->volumes.clear();
    impl_->segments.clear();
    for (auto& [p, n] : segments) {
        impl_->segments.push_back({impl_->volumes.size(), 0, n});
        impl_->volumes.push_back(memory_volume(p, n));
    }
    PackedStream stream(impl_->volumes, impl_->segments);
    impl_->decoder.decode(stream, unpacked_size, solid, sink);
}

std::uint64_t dictionary_size(int dictionary_log) { return Decoder::dictionary_size(dictionary_log); }
void delta(const std::uint8_t* src, std::size_t n, int channels, std::uint8_t* dst) { filters::delta(src, n, channels, dst); }
void e8(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, bool extended, std::uint8_t* dst) { filters::e8(src, n, start_rel, extended, dst); }
void arm(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, std::uint8_t* dst) { filters::arm(src, n, start_rel, dst); }

}  // namespace unpakz::rar5::testing
