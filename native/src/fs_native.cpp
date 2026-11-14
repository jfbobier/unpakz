// Native IFileSystem and RawFile. Windows is the product; the POSIX branch exists so the whole engine can be built, tested and
// fuzzed under sanitizers on WSL.
#include <atomic>
#include <chrono>
#include <cstring>
#include <vector>

#include "fs.h"
#include "guard.h"
#include "errors.h"
#include "platform.h"

#ifdef _WIN32
#include <windows.h>
#include <rpc.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace unpakz {
namespace platform {

#ifdef _WIN32

std::wstring win_path(const std::u16string& path) {
    std::wstring w(reinterpret_cast<const wchar_t*>(path.data()), path.size());
    if (w.rfind(L"\\\\?\\", 0) == 0) return w;                       // already extended
    DWORD n = GetFullPathNameW(w.c_str(), 0, nullptr, nullptr);       // normalises '/', '..', '.', relative paths
    if (n == 0) throw Error(Status::Io, "invalid path");
    std::wstring full(n, L'\0');
    n = GetFullPathNameW(w.c_str(), n, full.data(), nullptr);
    full.resize(n);
    if (full.rfind(L"\\\\", 0) == 0) return L"\\\\?\\UNC\\" + full.substr(2);
    return L"\\\\?\\" + full;
}

static Error win_error(const char* what) {
    return Error(Status::Io, std::string(what) + " (Windows error " + std::to_string(GetLastError()) + ")");
}

RawFile::~RawFile() { close(); }
bool RawFile::is_open() const noexcept { return handle_ != nullptr; }
void RawFile::close() noexcept {
    if (handle_) CloseHandle(handle_);
    handle_ = nullptr;
}
void RawFile::open(const std::u16string& path) {
    close();
    HANDLE h = CreateFileW(win_path(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (h == INVALID_HANDLE_VALUE) throw win_error("cannot open archive");
    handle_ = h;
}
std::size_t RawFile::read(void* buffer, std::size_t size) {
    DWORD got = 0;
    if (size > (1u << 30)) size = 1u << 30;
    if (!ReadFile(handle_, buffer, static_cast<DWORD>(size), &got, nullptr)) throw win_error("read failed");
    return got;
}
std::int64_t RawFile::seek(std::int64_t offset, int whence) {
    LARGE_INTEGER in, out;
    in.QuadPart = offset;
    DWORD method = whence == 0 ? FILE_BEGIN : whence == 1 ? FILE_CURRENT : FILE_END;
    if (!SetFilePointerEx(handle_, in, &out, method)) throw win_error("seek failed");
    return out.QuadPart;
}

#else  // POSIX

std::string posix_path(const std::u16string& path) { return utf16_to_utf8(path); }

RawFile::~RawFile() { close(); }
bool RawFile::is_open() const noexcept { return fd_ >= 0; }
void RawFile::close() noexcept {
    if (fd_ >= 0) ::close(fd_);
    fd_ = -1;
}
void RawFile::open(const std::u16string& path) {
    close();
    fd_ = ::open(posix_path(path).c_str(), O_RDONLY | O_CLOEXEC);
    if (fd_ < 0) throw Error(Status::Io, std::string("cannot open archive: ") + std::strerror(errno));
}
std::size_t RawFile::read(void* buffer, std::size_t size) {
    for (;;) {
        auto n = ::read(fd_, buffer, size);
        if (n >= 0) return static_cast<std::size_t>(n);
        if (errno != EINTR) throw Error(Status::Io, std::string("read failed: ") + std::strerror(errno));
    }
}
std::int64_t RawFile::seek(std::int64_t offset, int whence) {
    auto r = ::lseek(fd_, offset, whence == 0 ? SEEK_SET : whence == 1 ? SEEK_CUR : SEEK_END);
    if (r < 0) throw Error(Status::Io, std::string("seek failed: ") + std::strerror(errno));
    return r;
}

#endif
}  // namespace platform

namespace {

std::u16string unique_temp_name() {
    static std::atomic<std::uint64_t> counter{0};
    std::uint64_t a = static_cast<std::uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count());
    std::uint64_t b = counter.fetch_add(1) * 0x9E3779B97F4A7C15ull;
#ifdef _WIN32
    a ^= static_cast<std::uint64_t>(GetCurrentProcessId()) << 32;
#else
    a ^= static_cast<std::uint64_t>(::getpid()) << 32;
#endif
    const char* hex = "0123456789abcdef";
    std::u16string s = u".unpakz-";
    for (auto v : {a ^ b, (a * 31) ^ (b >> 7)})
        for (int i = 0; i < 16; ++i) s.push_back(static_cast<char16_t>(hex[(v >> (i * 4)) & 0xF]));
    s += u".tmp";
    return s;
}

std::u16string parent_of(const std::u16string& p) {
    auto i = p.find_last_of(u"/\\");
    return i == std::u16string::npos ? std::u16string() : p.substr(0, i == 0 ? 1 : i);
}

#ifdef _WIN32

class WinWriter : public IFileWriter {
public:
    WinWriter(HANDLE h, std::u16string temp, std::u16string final_path) : h_(h), temp_(std::move(temp)), final_(std::move(final_path)) {}
    ~WinWriter() override {
        if (h_ != INVALID_HANDLE_VALUE) {
            FILE_DISPOSITION_INFO d{};
            d.DeleteFile = TRUE;
            SetFileInformationByHandle(h_, FileDispositionInfo, &d, sizeof d);   // an uncommitted temp file never survives
            CloseHandle(h_);
        }
    }
    void write(const std::uint8_t* data, std::size_t size) override {
        while (size > 0) {
            DWORD chunk = size > (1u << 30) ? (1u << 30) : static_cast<DWORD>(size);
            DWORD done = 0;
            if (!WriteFile(h_, data, chunk, &done, nullptr)) throw Error(Status::Io, "write failed (Windows error " + std::to_string(GetLastError()) + ")");
            data += done;
            size -= done;
        }
    }
    void commit(std::optional<Timestamp> mtime, bool overwrite) override {
        if (mtime) {
            ULARGE_INTEGER t;
            t.QuadPart = static_cast<std::uint64_t>(mtime->unix_seconds + 11644473600ll) * 10000000ull + static_cast<std::uint64_t>(mtime->nanos / 100);
            FILETIME ft{t.LowPart, t.HighPart};
            SetFileTime(h_, nullptr, nullptr, &ft);
        }
        auto target = platform::win_path(final_);
        const std::size_t bytes = sizeof(FILE_RENAME_INFO) + target.size() * sizeof(wchar_t);
        std::vector<char> buf(bytes, 0);
        auto* info = reinterpret_cast<FILE_RENAME_INFO*>(buf.data());
        info->FileNameLength = static_cast<DWORD>(target.size() * sizeof(wchar_t));
        std::memcpy(info->FileName, target.c_str(), info->FileNameLength);
        // POSIX semantics let us replace a file that another program has open; fall back to the classic rename on file systems without it.
        info->Flags = (overwrite ? (FILE_RENAME_FLAG_REPLACE_IF_EXISTS) : 0) | FILE_RENAME_FLAG_POSIX_SEMANTICS;
        BOOL ok = SetFileInformationByHandle(h_, FileRenameInfoEx, info, static_cast<DWORD>(bytes));
        if (!ok && GetLastError() == ERROR_INVALID_PARAMETER) {
            auto* classic = reinterpret_cast<FILE_RENAME_INFO*>(buf.data());
            classic->ReplaceIfExists = overwrite ? TRUE : FALSE;
            ok = SetFileInformationByHandle(h_, FileRenameInfo, classic, static_cast<DWORD>(bytes));
        }
        if (!ok) {
            auto e = GetLastError();
            if (e == ERROR_ALREADY_EXISTS || e == ERROR_FILE_EXISTS || e == ERROR_ACCESS_DENIED)
                throw Error(Status::Conflict, "the destination file already exists or is not replaceable");
            throw Error(Status::Io, "rename failed (Windows error " + std::to_string(e) + ")");
        }
        CloseHandle(h_);
        h_ = INVALID_HANDLE_VALUE;
    }

private:
    HANDLE h_;
    std::u16string temp_, final_;
};

class NativeFs : public IFileSystem {
public:
    PathKind get_kind(const std::u16string& path) override {
        WIN32_FILE_ATTRIBUTE_DATA d;
        if (!GetFileAttributesExW(platform::win_path(path).c_str(), GetFileExInfoStandard, &d)) {
            auto e = GetLastError();
            if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND || e == ERROR_DIRECTORY) return PathKind::None;
            throw Error(Status::Io, "cannot inspect path (Windows error " + std::to_string(e) + ")");
        }
        if (d.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) return PathKind::ReparsePoint;
        if (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) return PathKind::Directory;
        return PathKind::File;
    }
    void create_directory(const std::u16string& path) override {
        if (CreateDirectoryW(platform::win_path(path).c_str(), nullptr)) return;
        auto e = GetLastError();
        if (e == ERROR_ALREADY_EXISTS && get_kind(path) == PathKind::Directory) return;
        if (e == ERROR_ALREADY_EXISTS) throw Error(Status::Conflict, "a file exists where a folder is needed");
        throw Error(Status::Io, "cannot create folder (Windows error " + std::to_string(e) + ")");
    }
    std::unique_ptr<IFileWriter> create_file(const std::u16string& final_path, std::uint64_t preallocate) override {
        auto temp = parent_of(final_path) + kNativeSeparator + unique_temp_name();
        HANDLE h = CreateFileW(platform::win_path(temp).c_str(), GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
                               FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (h == INVALID_HANDLE_VALUE) throw Error(Status::Io, "cannot create file (Windows error " + std::to_string(GetLastError()) + ")");
        if (preallocate > 0) {
            FILE_ALLOCATION_INFO a{};
            a.AllocationSize.QuadPart = static_cast<LONGLONG>(preallocate);
            SetFileInformationByHandle(h, FileAllocationInfo, &a, sizeof a);   // best effort: avoids fragmentation
        }
        return std::make_unique<WinWriter>(h, temp, final_path);
    }
};

#else

class PosixWriter : public IFileWriter {
public:
    PosixWriter(int fd, std::string temp, std::string final_path) : fd_(fd), temp_(std::move(temp)), final_(std::move(final_path)) {}
    ~PosixWriter() override {
        if (fd_ >= 0) { ::close(fd_); ::unlink(temp_.c_str()); }
    }
    void write(const std::uint8_t* data, std::size_t size) override {
        while (size > 0) {
            auto n = ::write(fd_, data, size);
            if (n < 0) {
                if (errno == EINTR) continue;
                throw Error(Status::Io, std::string("write failed: ") + std::strerror(errno));
            }
            data += n;
            size -= static_cast<std::size_t>(n);
        }
    }
    void commit(std::optional<Timestamp> mtime, bool overwrite) override {
        if (mtime) {
            timespec ts[2] = {{0, UTIME_OMIT}, {static_cast<time_t>(mtime->unix_seconds), mtime->nanos}};
            ::futimens(fd_, ts);
        }
        ::close(fd_);
        fd_ = -1;
        if (overwrite) {
            if (::rename(temp_.c_str(), final_.c_str()) != 0) {
                auto e = errno;
                ::unlink(temp_.c_str());
                if (e == EISDIR || e == ENOTDIR) throw Error(Status::Conflict, "a folder exists at the file path");
                throw Error(Status::Io, std::string("rename failed: ") + std::strerror(e));
            }
        } else {
            // link() fails if the name exists: an atomic "create only if absent" publish.
            if (::link(temp_.c_str(), final_.c_str()) != 0) {
                auto e = errno;
                ::unlink(temp_.c_str());
                if (e == EEXIST) throw Error(Status::Conflict, "the destination file already exists");
                throw Error(Status::Io, std::string("publish failed: ") + std::strerror(e));
            }
            ::unlink(temp_.c_str());
        }
    }

private:
    int fd_;
    std::string temp_, final_;
};

class NativeFs : public IFileSystem {
public:
    PathKind get_kind(const std::u16string& path) override {
        struct stat st;
        if (::lstat(platform::posix_path(path).c_str(), &st) != 0) {
            if (errno == ENOENT || errno == ENOTDIR) return PathKind::None;
            throw Error(Status::Io, std::string("cannot inspect path: ") + std::strerror(errno));
        }
        if (S_ISLNK(st.st_mode)) return PathKind::ReparsePoint;
        if (S_ISDIR(st.st_mode)) return PathKind::Directory;
        return PathKind::File;
    }
    void create_directory(const std::u16string& path) override {
        if (::mkdir(platform::posix_path(path).c_str(), 0777) == 0) return;
        if (errno == EEXIST) {
            if (get_kind(path) == PathKind::Directory) return;
            throw Error(Status::Conflict, "a file exists where a folder is needed");
        }
        throw Error(Status::Io, std::string("cannot create folder: ") + std::strerror(errno));
    }
    std::unique_ptr<IFileWriter> create_file(const std::u16string& final_path, std::uint64_t preallocate) override {
        auto temp = platform::posix_path(parent_of(final_path) + kNativeSeparator + unique_temp_name());
        int fd = ::open(temp.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0666);
        if (fd < 0) throw Error(Status::Io, std::string("cannot create file: ") + std::strerror(errno));
        if (preallocate > 0) (void)::posix_fallocate(fd, 0, static_cast<off_t>(preallocate));   // best effort
        return std::make_unique<PosixWriter>(fd, temp, platform::posix_path(final_path));
    }
};

#endif

}  // namespace

IFileSystem& native_file_system() {
    static NativeFs fs;
    return fs;
}

}  // namespace unpakz
