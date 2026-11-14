// Small platform layer shared by the file source and the sink. All paths are UTF-16 (the Windows native form).
#pragma once
#include <cstdint>
#include <string>

namespace unpakz::platform {

// Windows: absolute, normalised, \\?\-prefixed so long paths work. POSIX: UTF-8.
#ifdef _WIN32
std::wstring win_path(const std::u16string& path);
#else
std::string posix_path(const std::u16string& path);
#endif

// Read-only random-access file with 64-bit offsets (archives over 2 GiB).
class RawFile {
public:
    RawFile() = default;
    ~RawFile();
    RawFile(const RawFile&) = delete;
    RawFile& operator=(const RawFile&) = delete;
    void open(const std::u16string& path);     // throws Error(Io) (Error(MissingVolume) is decided by the caller)
    bool is_open() const noexcept;
    std::size_t read(void* buffer, std::size_t size);   // returns 0 at end of file; throws Error(Io)
    std::int64_t seek(std::int64_t offset, int whence); // whence: 0 set, 1 cur, 2 end; returns the new position
    void close() noexcept;

private:
#ifdef _WIN32
    void* handle_ = nullptr;
#else
    int fd_ = -1;
#endif
};

}  // namespace unpakz::platform
