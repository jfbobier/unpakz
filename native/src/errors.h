#pragma once
#include <stdexcept>
#include <string>

namespace unpakz {

// Typed failure categories; mirror the C ABI's unpakz_status values.
enum class Status {
    Ok = 0,
    Corrupt = 1,
    Unsupported = 2,
    LimitExceeded = 3,
    UnsafePath = 4,
    Conflict = 5,
    MissingVolume = 6,
    Io = 7,
    Cancelled = 8,
    InvalidArgument = 9,
};

class Error : public std::runtime_error {
public:
    Error(Status s, const std::string& message, int volume = 0) : std::runtime_error(message), status_(s), volume_(volume) {}
    Status status() const noexcept { return status_; }
    int volume() const noexcept { return volume_; }

private:
    Status status_;
    int volume_;
};

}  // namespace unpakz
