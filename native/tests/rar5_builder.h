// Independent RAR5 writer for fixtures (stored data only), ported from the C# test builder. Shares no code with the reader under test.
#pragma once
#include <zlib.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace tsupport {

using Bytes = std::vector<std::uint8_t>;

struct FileOpts {
    int method = 0;
    int compression_version = 0;
    int dictionary_log = 0;
    bool omit_crc = false;
    std::optional<std::uint32_t> crc_override;
    std::uint64_t host = 1;                  // 0 = Windows, 1 = Unix
    std::uint64_t attr = 0x81A4;             // regular, 0644
    std::optional<std::uint32_t> mtime = 1'700'000'000u;
    std::vector<Bytes> extras;
    bool solid = false;
    bool unknown_size = false;
    std::optional<std::uint64_t> unpacked_override;
    std::optional<std::uint64_t> data_size_field;
    std::optional<Bytes> name_bytes;
    std::optional<std::uint64_t> name_len_field;
    std::optional<std::uint64_t> extra_size_field;
};

inline std::uint32_t crc_of(const std::uint8_t* d, std::size_t n) { return static_cast<std::uint32_t>(::crc32(0, d, static_cast<uInt>(n))); }
inline std::uint32_t crc_of(const Bytes& b) { return b.empty() ? 0 : crc_of(b.data(), b.size()); }

inline Bytes vint(std::uint64_t v) {
    Bytes o;
    do {
        auto b = static_cast<std::uint8_t>(v & 0x7F);
        v >>= 7;
        if (v != 0) b |= 0x80;
        o.push_back(b);
    } while (v != 0);
    return o;
}
inline Bytes u32(std::uint32_t v) { return {static_cast<std::uint8_t>(v), static_cast<std::uint8_t>(v >> 8), static_cast<std::uint8_t>(v >> 16), static_cast<std::uint8_t>(v >> 24)}; }
inline Bytes u64(std::uint64_t v) { Bytes b; for (int i = 0; i < 8; ++i) b.push_back(static_cast<std::uint8_t>(v >> (8 * i))); return b; }
inline void append(Bytes& to, const Bytes& from) { to.insert(to.end(), from.begin(), from.end()); }
inline Bytes cat(std::initializer_list<Bytes> parts) { Bytes r; for (auto& p : parts) append(r, p); return r; }
inline Bytes str(const std::string& s) { return Bytes(s.begin(), s.end()); }

class Rar5Builder {
public:
    static inline const Bytes signature{0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00};

    std::uint64_t archive_flags = 0;
    bool omit_main = false, omit_end = false;
    Bytes trailing;

    Rar5Builder& add_file(const std::string& name, const Bytes& data, const FileOpts& o = {}) {
        blocks_.push_back(file_block(o.name_bytes ? *o.name_bytes : str(name), data, o, false));
        entries_.push_back({name, data, o, false});
        return *this;
    }
    Rar5Builder& add_directory(const std::string& name, std::optional<FileOpts> o = std::nullopt) {
        FileOpts opts = o ? *o : FileOpts{};
        if (!o) opts.attr = 0x41ED;
        blocks_.push_back(file_block(str(name), {}, opts, true));
        entries_.push_back({name, {}, opts, true});
        return *this;
    }
    Rar5Builder& add_block(std::uint64_t type, std::uint64_t flags, const Bytes& specific, const Bytes& data = {}, const Bytes& extra = {},
                           std::optional<std::uint64_t> data_size_field = std::nullopt) {
        blocks_.push_back(block(type, flags, specific, extra, data, data_size_field, std::nullopt));
        return *this;
    }

    Bytes build() const {
        Bytes out = signature;
        if (!omit_main) {
            Bytes spec = vint(archive_flags);
            if (archive_flags & 2) append(spec, vint(1));
            append(out, block(1, 0, spec, {}, {}, std::nullopt, std::nullopt));
        }
        for (auto& b : blocks_) append(out, b);
        if (!omit_end) append(out, block(5, 0, vint(0), {}, {}, std::nullopt, std::nullopt));
        append(out, trailing);
        return out;
    }

    // Splits each file's data so that no volume holds more than `data_per_volume` bytes of file data; non-final parts carry the CRC of
    // their own data and the final part the CRC of the whole file (as real rar does).
    std::vector<Bytes> build_volumes(std::size_t data_per_volume) const {
        std::vector<std::vector<Bytes>> volumes(1);
        std::size_t room = data_per_volume;
        for (auto& e : entries_) {
            const Bytes name = e.opts.name_bytes ? *e.opts.name_bytes : str(e.name);
            if (e.is_dir || e.data.empty()) { volumes.back().push_back(file_block(name, e.data, e.opts, e.is_dir)); continue; }
            std::size_t offset = 0;
            bool first = true;
            while (offset < e.data.size()) {
                if (room == 0) { volumes.emplace_back(); room = data_per_volume; }
                const std::size_t take = std::min(room, e.data.size() - offset);
                const bool last = offset + take == e.data.size();
                Bytes part(e.data.begin() + static_cast<std::ptrdiff_t>(offset), e.data.begin() + static_cast<std::ptrdiff_t>(offset + take));
                const std::uint32_t crc = last ? e.opts.crc_override.value_or(crc_of(e.data)) : crc_of(part);
                volumes.back().push_back(part_block(name, part, e.data.size(), crc, e.opts, !first, !last));
                offset += take; room -= take; first = false;
            }
        }
        std::vector<Bytes> result;
        for (std::size_t i = 0; i < volumes.size(); ++i) {
            Bytes out = signature;
            Bytes spec = vint(i == 0 ? 0x01u : 0x03u);
            if (i > 0) append(spec, vint(i));
            append(out, block(1, 0, spec, {}, {}, std::nullopt, std::nullopt));
            for (auto& b : volumes[i]) append(out, b);
            append(out, block(5, 0, vint(i + 1 < volumes.size() ? 1 : 0), {}, {}, std::nullopt, std::nullopt));
            result.push_back(std::move(out));
        }
        return result;
    }

    // ---- extra records ----
    static Bytes extra(std::uint64_t type, const Bytes& data) { return cat({vint(vint(type).size() + data.size()), vint(type), data}); }
    static Bytes encryption_extra() { Bytes d = cat({vint(0), vint(0)}); d.push_back(15); d.resize(d.size() + 16 + 12, 0); return extra(1, d); }
    static Bytes hash_extra() { Bytes d = vint(0); d.resize(d.size() + 32, 0); return extra(2, d); }
    static Bytes redirection_extra(std::uint64_t link_type, const std::string& target) { return extra(5, cat({vint(link_type), vint(0), vint(target.size()), str(target)})); }
    static Bytes unix_time_extra(std::uint32_t sec) { return extra(3, cat({vint(0x03), u32(sec)})); }
    static Bytes windows_time_extra(std::uint64_t ft) { return extra(3, cat({vint(0x02), u64(ft)})); }

    // Recomputes and patches the header CRC of the block starting at `block_start` after a test edits its bytes.
    static void fix_header_crc(Bytes& archive, std::size_t block_start) {
        std::size_t p = block_start + 4, size = 0, shift = 0, n = 0;
        for (;;) { auto b = archive[p + n]; size |= static_cast<std::size_t>(b & 0x7F) << shift; shift += 7; ++n; if ((b & 0x80) == 0) break; }
        auto crc = crc_of(archive.data() + p, n + size);
        for (int i = 0; i < 4; ++i) archive[block_start + static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(crc >> (8 * i));
    }

private:
    struct Entry { std::string name; Bytes data; FileOpts opts; bool is_dir; };
    std::vector<Bytes> blocks_;
    std::vector<Entry> entries_;

    static std::uint64_t comp_of(const FileOpts& o) {
        return (static_cast<std::uint64_t>(o.method) << 7) | (o.solid ? 1ull << 6 : 0) | static_cast<std::uint32_t>(o.compression_version) | (static_cast<std::uint64_t>(static_cast<std::uint32_t>(o.dictionary_log)) << 10);
    }

    static Bytes part_block(const Bytes& name, const Bytes& part, std::size_t whole, std::uint32_t crc, const FileOpts& o, bool split_before, bool split_after) {
        Bytes spec = vint(4ull | (o.mtime ? 2ull : 0));
        append(spec, vint(whole)); append(spec, vint(o.attr));
        if (o.mtime) append(spec, u32(*o.mtime));
        append(spec, u32(crc)); append(spec, vint(comp_of(o))); append(spec, vint(o.host)); append(spec, vint(name.size())); append(spec, name);
        Bytes extra; for (auto& e : o.extras) append(extra, e);
        return block(2, (split_before ? 0x08ull : 0) | (split_after ? 0x10ull : 0), spec, extra, part, std::nullopt, std::nullopt);
    }

    static Bytes file_block(const Bytes& name, const Bytes& data, const FileOpts& o, bool is_dir) {
        const std::uint64_t fflags = (is_dir ? 1ull : 0) | (o.mtime ? 2ull : 0) | (o.omit_crc ? 0ull : 4ull) | (o.unknown_size ? 8ull : 0);
        const std::uint32_t crc = o.crc_override.value_or(crc_of(data));
        Bytes spec = vint(fflags);
        append(spec, vint(o.unpacked_override.value_or(data.size()))); append(spec, vint(o.attr));
        if (o.mtime) append(spec, u32(*o.mtime));
        if (!o.omit_crc) append(spec, u32(crc));
        append(spec, vint(comp_of(o))); append(spec, vint(o.host)); append(spec, vint(o.name_len_field.value_or(name.size()))); append(spec, name);
        Bytes extra; for (auto& e : o.extras) append(extra, e);
        return block(2, 0, spec, extra, data, o.data_size_field, o.extra_size_field);
    }

    static Bytes block(std::uint64_t type, std::uint64_t flags, const Bytes& specific, const Bytes& extra, const Bytes& data,
                       std::optional<std::uint64_t> data_size_field, std::optional<std::uint64_t> extra_size_field) {
        const bool has_data = !data.empty() || data_size_field.has_value();
        const bool has_extra = !extra.empty() || extra_size_field.has_value();
        const std::uint64_t f = flags | (has_extra ? 1ull : 0) | (has_data ? 2ull : 0);
        Bytes h = vint(type);
        append(h, vint(f));
        if (has_extra) append(h, vint(extra_size_field.value_or(extra.size())));
        if (has_data) append(h, vint(data_size_field.value_or(data.size())));
        append(h, specific); append(h, extra);
        Bytes crc_input = vint(h.size());
        append(crc_input, h);
        Bytes out = u32(crc_of(crc_input));
        append(out, crc_input); append(out, data);
        return out;
    }
};

}  // namespace tsupport
