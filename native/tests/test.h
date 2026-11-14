// Minimal dependency-free test framework: TEST(name) { CHECK(...); }  Exit code = number of failed tests (capped at 255).
#pragma once
#include <cstdio>
#include <functional>
#include <string>
#include <vector>

namespace ut {
struct Case { const char* name; std::function<void()> fn; };
inline std::vector<Case>& cases() { static std::vector<Case> c; return c; }
inline int& failures_in_case() { static int f = 0; return f; }
struct Reg { Reg(const char* n, std::function<void()> f) { cases().push_back({n, std::move(f)}); } };
}  // namespace ut

#define UT_CAT2(a, b) a##b
#define UT_CAT(a, b) UT_CAT2(a, b)
#define TEST(name) \
    static void UT_CAT(test_fn_, name)(); \
    static ut::Reg UT_CAT(test_reg_, name)(#name, UT_CAT(test_fn_, name)); \
    static void UT_CAT(test_fn_, name)()

#define CHECK(cond) \
    do { if (!(cond)) { std::fprintf(stderr, "  CHECK failed: %s  (%s:%d)\n", #cond, __FILE__, __LINE__); ++ut::failures_in_case(); } } while (0)
#define CHECK_EQ(a, b) \
    do { auto&& va_ = (a); auto&& vb_ = (b); if (!(va_ == vb_)) { std::fprintf(stderr, "  CHECK_EQ failed: %s == %s  (%s:%d)\n", #a, #b, __FILE__, __LINE__); ++ut::failures_in_case(); } } while (0)

#define UT_MAIN() \
    int main(int argc, char** argv) { \
        int failed = 0; int ran = 0; \
        for (auto& c : ut::cases()) { \
            if (argc > 1 && std::string(c.name).find(argv[1]) == std::string::npos) continue; \
            ut::failures_in_case() = 0; ++ran; \
            try { c.fn(); } catch (const std::exception& e) { std::fprintf(stderr, "  exception: %s\n", e.what()); ++ut::failures_in_case(); } \
            if (ut::failures_in_case() != 0) { std::fprintf(stderr, "FAIL %s\n", c.name); ++failed; } \
        } \
        std::printf("%d tests, %d failed\n", ran, failed); \
        return failed > 255 ? 255 : failed; \
    }
