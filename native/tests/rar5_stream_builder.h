// Test-side RAR5 compressed-block encoder (ported from the C# test support): a bit writer, canonical Huffman codes assigned the way RAR's
// decode tables expect, and a model of the expected output. Shares nothing with the decoder under test.
#pragma once
#include <algorithm>
#include <cstdint>
#include <functional>
#include <numeric>
#include <optional>
#include <stdexcept>
#include <vector>

namespace tsupport {

using Bytes = std::vector<std::uint8_t>;

// MSB-first bit writer (the order RAR5 uses).
class RarBitWriter {
public:
    int total_bits() const { return bits_; }
    void write(std::int64_t value, int count) {
        for (int i = count - 1; i >= 0; --i) {
            const int bit = static_cast<int>((value >> i) & 1);
            if (bits_ % 8 == 0) bytes_.push_back(0);
            bytes_.back() = static_cast<std::uint8_t>(bytes_.back() | (bit << (7 - (bits_ % 8))));
            ++bits_;
        }
    }
    Bytes to_bytes() const { return bytes_; }

private:
    Bytes bytes_;
    int bits_ = 0;
};

class RarHuffmanEncoder {
public:
    explicit RarHuffmanEncoder(const std::vector<int>& lengths) : len_(lengths), code_(lengths.size(), 0) {
        std::vector<int> count(17, 0);
        for (int l : lengths) count[static_cast<std::size_t>(l)]++;
        count[0] = 0;
        std::vector<int> next(17, 0);
        int code = 0;
        for (int l = 1; l <= 16; ++l) {
            code = (code + count[static_cast<std::size_t>(l - 1)]) << 1;
            next[static_cast<std::size_t>(l)] = code;
        }
        for (std::size_t s = 0; s < lengths.size(); ++s)
            if (lengths[s] > 0) code_[s] = next[static_cast<std::size_t>(lengths[s])]++;
    }
    void write(RarBitWriter& w, int symbol) const {
        if (len_[static_cast<std::size_t>(symbol)] == 0) throw std::runtime_error("symbol has no code");
        w.write(code_[static_cast<std::size_t>(symbol)], len_[static_cast<std::size_t>(symbol)]);
    }

private:
    std::vector<int> len_, code_;
};

// Expected-output model shared across blocks and files (the LZ77 window and RAR's repeat-distance cache).
struct RarModel {
    Bytes history;
    std::int64_t dist[4] = {0, 0, 0, 0};
    int last_len = 0;
    std::size_t file_start = 0;

    Bytes file_output() const { return Bytes(history.begin() + static_cast<std::ptrdiff_t>(file_start), history.end()); }
    void copy(int len, std::int64_t d) {
        if (d < 1 || d > static_cast<std::int64_t>(history.size())) throw std::runtime_error("model: distance outside history");
        for (int i = 0; i < len; ++i) history.push_back(history[history.size() - static_cast<std::size_t>(d)]);
    }
    void start_new_file(bool solid) {
        if (!solid) { history.clear(); for (auto& d : dist) d = 0; last_len = 0; }
        file_start = history.size();
    }
};

// Builds one RAR5 compressed block with fixed or custom Huffman code lengths.
class RarBlockBuilder {
public:
    static constexpr int Nc = 306, Dc = 64, Ldc = 16, Rc = 44;

    struct Config {
        bool write_tables = true;
        std::optional<std::vector<int>> ld, dd, ldd, rd;
        std::function<void(RarBitWriter&)> raw_tables;
    };

    explicit RarBlockBuilder(RarModel& model) : RarBlockBuilder(model, Config()) {}

    RarBlockBuilder(RarModel& model, Config c)
        : model_(model),
          ld_lens_(c.ld ? *c.ld : std::vector<int>(Nc, 9)), dd_lens_(c.dd ? *c.dd : std::vector<int>(Dc, 6)),
          ldd_lens_(c.ldd ? *c.ldd : std::vector<int>(Ldc, 4)), rd_lens_(c.rd ? *c.rd : std::vector<int>(Rc, 6)),
          ld_(ld_lens_), dd_(dd_lens_), ldd_(ldd_lens_), rd_(rd_lens_) {
        has_tables_ = c.write_tables || static_cast<bool>(c.raw_tables);
        if (c.raw_tables) c.raw_tables(w_);
        else if (c.write_tables) write_tables();
    }

    RarBitWriter& raw() { return w_; }

    RarBlockBuilder& literal(std::uint8_t b) { ld_.write(w_, b); model_.history.push_back(b); return *this; }
    RarBlockBuilder& literals(const Bytes& bytes) { for (auto b : bytes) literal(b); return *this; }
    RarBlockBuilder& literals(const std::string& s) { for (char c : s) literal(static_cast<std::uint8_t>(c)); return *this; }

    RarBlockBuilder& match(int len, std::int64_t dist, bool track_model = true) {
        const int adj = (dist > 0x100 ? 1 : 0) + (dist > 0x2000 ? 1 : 0) + (dist > 0x40000 ? 1 : 0);
        auto [code, extra, bits] = len_code(len - adj);
        ld_.write(w_, 262 + code);
        if (bits > 0) w_.write(extra, bits);

        const std::int64_t d = dist - 1;
        int slot;
        std::int64_t d_extra = 0;
        if (d < 4) slot = static_cast<int>(d);
        else {
            slot = 4;
            for (;;) {
                const int dbits = (slot / 2) - 1;
                const std::int64_t base = static_cast<std::int64_t>(2 | (slot & 1)) << dbits;
                if (d >= base && d < base + (1ll << dbits)) { d_extra = d - base; break; }
                ++slot;
            }
        }
        dd_.write(w_, slot);
        if (slot >= 4) {
            const int dbits = (slot / 2) - 1;
            if (dbits < 4) w_.write(d_extra, dbits);
            else {
                if (dbits > 4) w_.write(d_extra >> 4, dbits - 4);
                ldd_.write(w_, static_cast<int>(d_extra & 15));
            }
        }
        if (!track_model) return *this;
        model_.copy(len, dist);
        model_.dist[3] = model_.dist[2]; model_.dist[2] = model_.dist[1]; model_.dist[1] = model_.dist[0]; model_.dist[0] = dist;
        model_.last_len = len;
        return *this;
    }

    RarBlockBuilder& repeat_last() {
        ld_.write(w_, 257);
        if (model_.last_len != 0) model_.copy(model_.last_len, model_.dist[0]);
        return *this;
    }

    RarBlockBuilder& repeat_distance(int index, int len, bool track_model = true) {
        ld_.write(w_, 258 + index);
        if (!track_model) {
            auto [c, e, b] = len_code(len);
            rd_.write(w_, c);
            if (b > 0) w_.write(e, b);
            return *this;
        }
        const std::int64_t dist = model_.dist[index];
        for (int i = index; i > 0; --i) model_.dist[i] = model_.dist[i - 1];
        model_.dist[0] = dist;
        auto [code, extra, bits] = len_code(len);
        rd_.write(w_, code);
        if (bits > 0) w_.write(extra, bits);
        model_.copy(len, dist);
        model_.last_len = len;
        return *this;
    }

    // Declares a filter. Does not change the model: filtered output is computed by the test.
    RarBlockBuilder& filter(int type, std::int64_t start, std::int64_t length, int channels = 1) {
        ld_.write(w_, 256);
        write_filter_value(start);
        write_filter_value(length);
        w_.write(type, 3);
        if (type == 0) w_.write(channels - 1, 5);
        return *this;
    }

    struct BuildOpts {
        bool last = true;
        std::optional<int> valid_bits_override, size_field_bytes;
        std::optional<std::uint8_t> checksum_xor;
        std::optional<std::int64_t> size_override;
        int cut_data_bytes = 0;
    };

    Bytes build() const { return build(BuildOpts()); }

    Bytes build(BuildOpts o) const {
        Bytes data = w_.to_bytes();
        if (o.cut_data_bytes > 0) data.resize(data.size() - static_cast<std::size_t>(o.cut_data_bytes));
        const int valid = o.valid_bits_override.value_or(o.cut_data_bytes > 0 ? 8 : (w_.total_bits() % 8 == 0 ? 8 : w_.total_bits() % 8));
        const std::int64_t size = o.size_override.value_or(static_cast<std::int64_t>(data.size()));
        const int size_bytes = o.size_field_bytes.value_or(size < 256 ? 1 : size < 65536 ? 2 : 3);

        const auto flags = static_cast<std::uint8_t>((has_tables_ ? 0x80 : 0) | (o.last ? 0x40 : 0) | ((size_bytes - 1) << 3) | (valid - 1));
        auto check = static_cast<std::uint8_t>(0x5A ^ flags);
        Bytes header = {flags, 0};
        for (int i = 0; i < size_bytes; ++i) {
            const auto b = static_cast<std::uint8_t>((size >> (8 * i)) & 0xFF);
            header.push_back(b);
            check = static_cast<std::uint8_t>(check ^ b);
        }
        header[1] = static_cast<std::uint8_t>(check ^ o.checksum_xor.value_or(0));
        header.insert(header.end(), data.begin(), data.end());
        return header;
    }

    struct LenCode { int code, extra, bits; };
    static LenCode len_code(int len) {
        const int x = len - 2;
        if (x < 0) throw std::runtime_error("length too small");
        for (int code = 0; code < 44; ++code) {
            if (code < 8) { if (x == code) return {code, 0, 0}; continue; }
            const int bits = (code / 4) - 1;
            const int base = (4 | (code & 3)) << bits;
            if (x >= base && x < base + (1 << bits)) return {code, x - base, bits};
        }
        throw std::runtime_error("length not representable");
    }

private:
    RarModel& model_;
    RarBitWriter w_;
    std::vector<int> ld_lens_, dd_lens_, ldd_lens_, rd_lens_;
    RarHuffmanEncoder ld_, dd_, ldd_, rd_;
    bool has_tables_ = false;

    // The bit-length table is "all 20 symbols, 5 bits each", so every value 0..19 is just its own 5-bit number.
    void write_tables() {
        for (int i = 0; i < 20; ++i) w_.write(5, 4);
        for (auto* lens : {&ld_lens_, &dd_lens_, &ldd_lens_, &rd_lens_}) for (int l : *lens) w_.write(l, 5);
    }
    void write_filter_value(std::int64_t v) {
        const int bytes = v <= 0xFF ? 1 : v <= 0xFFFF ? 2 : v <= 0xFFFFFF ? 3 : 4;
        w_.write(bytes - 1, 2);
        for (int i = 0; i < bytes; ++i) w_.write((v >> (8 * i)) & 0xFF, 8);
    }
};

}  // namespace tsupport
