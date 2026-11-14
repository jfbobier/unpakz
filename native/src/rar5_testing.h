// Test-only entry points into the RAR5 decoder internals (the decoder and the filters are otherwise private to rar5.cpp).
#pragma once
#include <cstdint>
#include <functional>
#include <memory>
#include <utility>
#include <vector>

#include "errors.h"

namespace unpakz::rar5::testing {

class TestDecoder {
public:
    TestDecoder(std::uint64_t dictionary, std::uint64_t window_limit);   // throws Error(InvalidArgument) for zero sizes
    ~TestDecoder();
    // Decodes one file whose compressed data is the given segments laid end to end.
    void decode(const std::vector<std::pair<const std::uint8_t*, std::size_t>>& segments, std::uint64_t unpacked_size, bool solid,
                const std::function<void(const std::uint8_t*, std::size_t)>& sink);

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

std::uint64_t dictionary_size(int dictionary_log);
void delta(const std::uint8_t* src, std::size_t n, int channels, std::uint8_t* dst);
void e8(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, bool extended, std::uint8_t* dst);
void arm(const std::uint8_t* src, std::size_t n, std::int64_t start_rel, std::uint8_t* dst);

}  // namespace unpakz::rar5::testing
