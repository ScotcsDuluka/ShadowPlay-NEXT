#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <atomic>
#include <string>
#include <utility>

namespace osc {
inline constexpr LONGLONG MaxOscConfigBytes = 1024 * 1024;

inline bool ReadConfigFile(const std::wstring& path, std::string& json,
    std::string& error) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        error = "Cannot read OSC config (" + std::to_string(GetLastError()) + ")";
        return false;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 ||
        size.QuadPart > MaxOscConfigBytes) {
        CloseHandle(file);
        error = "OSC config exceeds the supported size";
        return false;
    }
    std::string contents(static_cast<std::size_t>(size.QuadPart), '\0');
    DWORD bytes = 0;
    const bool read = contents.empty() || (ReadFile(file, contents.data(),
        static_cast<DWORD>(contents.size()), &bytes, nullptr) &&
        bytes == contents.size());
    const DWORD readError = read ? ERROR_SUCCESS : GetLastError();
    CloseHandle(file);
    if (!read) {
        error = "Cannot read complete OSC config (" + std::to_string(readError) + ")";
        return false;
    }
    json = std::move(contents);
    return true;
}

// Replace the existing file only after the complete new contents reach disk.
// ReplaceFile preserves the original file's security information.
inline bool WriteConfigFileAtomically(const std::wstring& path,
    const std::string& json, std::string& error) {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY)) ||
        json.size() > static_cast<std::size_t>(MaxOscConfigBytes)) {
        error = "OSC config is missing, read-only, or too large";
        return false;
    }
    static std::atomic<unsigned long> nextTemporary{ 0 };
    std::wstring temporary;
    HANDLE file = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 16; ++attempt) {
        temporary = path + L".fps-" + std::to_wstring(GetCurrentProcessId()) +
            L"-" + std::to_wstring(nextTemporary.fetch_add(1)) + L".tmp";
        file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr,
            CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE || GetLastError() != ERROR_FILE_EXISTS)
            break;
    }
    if (file == INVALID_HANDLE_VALUE) {
        error = "Cannot create OSC config temporary file (" +
            std::to_string(GetLastError()) + ")";
        return false;
    }
    DWORD written = 0;
    const bool saved = WriteFile(file, json.data(), static_cast<DWORD>(json.size()),
        &written, nullptr) && written == json.size() && FlushFileBuffers(file);
    const DWORD writeError = saved ? ERROR_SUCCESS : GetLastError();
    CloseHandle(file);
    if (!saved) {
        DeleteFileW(temporary.c_str());
        error = "Cannot save complete OSC config (" + std::to_string(writeError) + ")";
        return false;
    }
    if (!ReplaceFileW(path.c_str(), temporary.c_str(), nullptr, 0, nullptr, nullptr)) {
        const DWORD replaceError = GetLastError();
        DeleteFileW(temporary.c_str());
        error = "Cannot replace OSC config (" + std::to_string(replaceError) + ")";
        return false;
    }
    return true;
}
} // namespace osc
