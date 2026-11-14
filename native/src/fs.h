// The only way extraction touches the file system. Every output file is created through IFileSystem::create_file,
// which writes a temp file next to its final name and publishes it with an atomic rename (never a copy).
#pragma once
#include <cstdint>
#include <memory>
#include <optional>
#include <string>

namespace unpakz {

enum class PathKind { None, File, Directory, ReparsePoint };

struct Timestamp {
    std::int64_t unix_seconds = 0;
    std::int32_t nanos = 0;
};

class IFileWriter {
public:
    virtual ~IFileWriter() = default;  // an uncommitted writer removes its temp file
    virtual void write(const std::uint8_t* data, std::size_t size) = 0;
    // Publishes the file under its final name. overwrite=false must fail with Status::Conflict if the name exists (race safe).
    virtual void commit(std::optional<Timestamp> mtime, bool overwrite) = 0;
};

class IFileSystem {
public:
    virtual ~IFileSystem() = default;
    virtual PathKind get_kind(const std::u16string& path) = 0;
    virtual void create_directory(const std::u16string& path) = 0;
    virtual std::unique_ptr<IFileWriter> create_file(const std::u16string& final_path, std::uint64_t preallocate) = 0;
};

// The real file system (Win32 on Windows, POSIX elsewhere, used for tests on WSL).
IFileSystem& native_file_system();

constexpr char16_t kNativeSeparator =
#ifdef _WIN32
    u'\\';
#else
    u'/';
#endif

}  // namespace unpakz
