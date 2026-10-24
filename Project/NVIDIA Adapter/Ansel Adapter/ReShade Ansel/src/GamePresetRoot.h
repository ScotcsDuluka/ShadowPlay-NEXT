#pragma once
#include <windows.h>
#include <filesystem>
#include <system_error>

namespace ansel::game_presets {
namespace fs = std::filesystem;

inline bool equal_name(const fs::path &part, const wchar_t *expected) {
    const auto name = part.wstring();
    return CompareStringOrdinal(name.c_str(), -1, expected, -1, TRUE) == CSTR_EQUAL;
}
inline bool regular_file(const fs::path &path) {
    std::error_code ec;
    return fs::is_regular_file(path, ec) && !ec;
}
inline bool directory(const fs::path &path) {
    std::error_code ec;
    return fs::is_directory(path, ec) && !ec;
}
// ReShade may expose a relative INI filename. Resolve it against the actual
// installation, never the launcher's mutable process working directory.
inline fs::path resolve_preset_path(const fs::path &reported,
                                    const fs::path &gameRoot,
                                    const fs::path &executableDirectory) {
    if (reported.empty() || reported.is_absolute()) return reported.lexically_normal();
    const auto rootPath = (gameRoot / reported).lexically_normal();
    if (regular_file(rootPath)) return rootPath;
    const auto legacyPath = (executableDirectory / reported).lexically_normal();
    if (regular_file(legacyPath)) return legacyPath;
    return rootPath;
}
// Inspect only the installation's top-level entries, never recurse into a
// library or game asset tree. Game launchers often live above UE binaries.
inline bool installation_marker(const fs::path &candidate) {
    if (candidate.empty() || candidate == candidate.root_path()) return false;
    if (regular_file(candidate / L"ReShade.ini") ||
        regular_file(candidate / L"steam_appid.txt") ||
        directory(candidate / L".egstore") ||
        directory(candidate / L"Engine")) return true;
    std::error_code ec;
    size_t checked = 0;
    for (fs::directory_iterator it(candidate, fs::directory_options::skip_permission_denied, ec), end;
         !ec && it != end && checked++ < 128; it.increment(ec)) {
        const auto &entry = *it;
        if (entry.is_regular_file(ec) && !ec && equal_name(entry.path().extension(), L".exe"))
            return true;
        ec.clear();
    }
    return false;
}
// Return an installation root, not e.g. Game/Binaries/Win64. Use a second
// parent for UE installations containing Engine or an actual root launcher.
// Never climb into steamapps/common or a drive just because it is a parent.
inline fs::path resolve_root(const fs::path &executablePath) {
    const auto executableDirectory = executablePath.parent_path().lexically_normal();
    if (executableDirectory.empty() || executableDirectory == executableDirectory.root_path())
        return {};
    auto candidate = executableDirectory;
    for (unsigned level = 0; level < 5; ++level) {
        if (equal_name(candidate.filename(), L"Binaries")) {
            auto projectRoot = candidate.parent_path();
            if (projectRoot.empty() || projectRoot == projectRoot.root_path()) return {};
            auto outer = projectRoot.parent_path();
            if (equal_name(projectRoot.filename(), L"Engine") ||
                (!outer.empty() && outer != outer.root_path() && installation_marker(outer)))
                return outer;
            return projectRoot;
        }
        if (candidate == candidate.root_path()) break;
        const auto parent = candidate.parent_path();
        if (parent == candidate) break;
        candidate = parent;
    }
    // Some games place the playable executable under bin or x64 rather than
    // the UE Binaries hierarchy. Climb only when the parent looks like a game.
    if (equal_name(executableDirectory.filename(), L"bin") ||
        equal_name(executableDirectory.filename(), L"bin64") ||
        equal_name(executableDirectory.filename(), L"x64") ||
        equal_name(executableDirectory.filename(), L"x86") ||
        equal_name(executableDirectory.filename(), L"Win64") ||
        equal_name(executableDirectory.filename(), L"Win32")) {
        const auto parent = executableDirectory.parent_path();
        if (installation_marker(parent)) return parent;
    }
    return executableDirectory;
}
} // namespace ansel::game_presets
