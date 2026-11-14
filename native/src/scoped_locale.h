// libarchive converts entry names through the calling thread's C locale. In a host process (for example .NET) that locale is "C"
// or an ANSI code page, which mangles or loses non-ASCII names. Every public entry point therefore switches the *calling thread*
// to a UTF-8 locale for its duration; the process-wide locale is never touched.
#pragma once
#include <clocale>
#include <string>

#ifdef _WIN32
#include <locale.h>
#else
#include <locale.h>
#endif

namespace unpakz {

class ScopedUtf8Locale {
public:
    ScopedUtf8Locale() {
#ifdef _WIN32
        thread_mode_ = _configthreadlocale(_ENABLE_PER_THREAD_LOCALE);
        const char* old = std::setlocale(LC_CTYPE, nullptr);
        if (old) { saved_ = old; has_saved_ = true; }
        std::setlocale(LC_CTYPE, ".UTF-8");
#else
        locale_ = newlocale(LC_CTYPE_MASK, "C.UTF-8", static_cast<locale_t>(nullptr));
        if (locale_ == static_cast<locale_t>(nullptr)) locale_ = newlocale(LC_CTYPE_MASK, "en_US.UTF-8", static_cast<locale_t>(nullptr));
        if (locale_ != static_cast<locale_t>(nullptr)) previous_ = uselocale(locale_);
#endif
    }
    ~ScopedUtf8Locale() {
#ifdef _WIN32
        if (has_saved_) std::setlocale(LC_CTYPE, saved_.c_str());
        if (thread_mode_ != -1) _configthreadlocale(thread_mode_);
#else
        if (locale_ != static_cast<locale_t>(nullptr)) { uselocale(previous_); freelocale(locale_); }
#endif
    }
    ScopedUtf8Locale(const ScopedUtf8Locale&) = delete;
    ScopedUtf8Locale& operator=(const ScopedUtf8Locale&) = delete;

private:
#ifdef _WIN32
    int thread_mode_ = -1;
    std::string saved_;
    bool has_saved_ = false;
#else
    locale_t locale_ = static_cast<locale_t>(nullptr);
    locale_t previous_ = static_cast<locale_t>(nullptr);
#endif
};

}  // namespace unpakz
