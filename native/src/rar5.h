// RAR 5 reader: container parsing (layout from RARLAB's public technical note), the LZ+Huffman decoder with its filters, solid groups and
// multi-volume sets. A C++ port of the C# decoder that was verified against 7-Zip and unrar; the decoder algorithm itself follows the
// behaviour of libarchive's RAR5 reader (archive_read_support_format_rar5.c, Copyright (c) 2018 Grzegorz Antoniak, BSD-2-Clause),
// re-expressed with our own structure, bounds checking and streaming window. See docs/THIRD-PARTY.md.
#pragma once
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "entry_reader.h"

namespace unpakz::rar5 {

// A readable byte range (one volume of the set). Reads are positional; a short read is an error.
class Volume {
public:
    virtual ~Volume() = default;
    virtual std::uint64_t size() const = 0;
    virtual void read_at(std::uint64_t offset, std::uint8_t* dst, std::size_t count) = 0;
};

std::unique_ptr<Volume> memory_volume(const std::uint8_t* data, std::size_t size);
std::unique_ptr<Volume> file_volume(const std::u16string& path);   // throws Error(Io)

bool has_signature(const std::uint8_t* data, std::size_t size);
bool has_rar4_signature(const std::uint8_t* data, std::size_t size);

// Counters so tests can prove which code paths a real archive exercised.
struct DecoderStats {
    int filters = 0, delta = 0, e8 = 0, e8e9 = 0, arm = 0, files_decoded = 0;
};

class Reader : public IEntryReader {
public:
    virtual const DecoderStats& stats() const = 0;
    virtual int volume_count() const = 0;
};

// Parses every header of the set (volumes in order, first volume first) and returns a reader. Throws Error.
std::unique_ptr<Reader> open(std::vector<std::unique_ptr<Volume>> volumes, const Limits& limits);

}  // namespace unpakz::rar5
