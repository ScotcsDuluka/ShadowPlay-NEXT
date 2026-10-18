// bootstrap.cpp - Launcher.exe: thin CEF bootstrap at the product root
// slot (the old WinForm Launcher.exe double-click entry). Loads the host
// body NvLauncher\Cef\Launcher.dll by ABSOLUTE path and calls its
// NvLauncherCefMain export. CEF itself is loaded from the NVIDIA OSC runtime.
// which implements the whole CEF process split (browser process +
// subprocess relaunches of this same exe).
#include <windows.h>

#include <stdio.h>
#include <string>

namespace {

bool ResolveProductRoot(std::wstring* out) {
  wchar_t exe[MAX_PATH];
  if (!GetModuleFileNameW(NULL, exe, MAX_PATH)) return false;
  std::wstring dir(exe);
  size_t slash = dir.find_last_of(L'\\');
  if (slash == std::wstring::npos) return false;
  dir.resize(slash);
  *out = dir;
  return true;
}

}  // namespace

int APIENTRY wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
  std::wstring root;
  if (!ResolveProductRoot(&root)) {
    MessageBoxW(NULL, L"Launcher product root could not be resolved.",
                L"NVIDIA ShadowPlay", MB_ICONERROR);
    return 1;
  }

  const std::wstring body_path = root + L"\\NVIDIA Launcher\\Cef\\Launcher.dll";
  const std::wstring cef_runtime_dir =
      root + L"\\Overlay On-Screen Control\\NVIDIA OSC";
  const std::wstring libcef_path = cef_runtime_dir + L"\\libcef.dll";
  if (GetFileAttributesW(libcef_path.c_str()) == INVALID_FILE_ATTRIBUTES) {
    wchar_t msg[1024];
    swprintf(msg, 1024,
             L"The NVIDIA OSC CEF runtime is missing:\n%s",
             libcef_path.c_str());
    MessageBoxW(NULL, msg, L"NVIDIA ShadowPlay", MB_ICONERROR);
    return 1;
  }

  // Launcher.dll statically imports libcef.dll. Resolve it from the one
  // runtime staged beside NVIDIA OSC.exe, not from NvLauncher\Cef.
  if (!SetDllDirectoryW(cef_runtime_dir.c_str())) {
    wchar_t msg[1024];
    swprintf(msg, 1024,
             L"The NVIDIA OSC CEF runtime path could not be registered "
             L"(error %lu):\n%s",
             GetLastError(), cef_runtime_dir.c_str());
    MessageBoxW(NULL, msg, L"NVIDIA ShadowPlay", MB_ICONERROR);
    return 1;
  }

  HMODULE host = LoadLibraryW(body_path.c_str());
  if (!host) {
    wchar_t msg[1024];
    swprintf(msg, 1024,
             L"NVIDIA Launcher\\Cef\\Launcher.dll could not be loaded (error %lu).\n\n"
             L"Launcher CEF dependencies are loaded from "
             L"Overlay On-Screen Control\\NVIDIA OSC:\n%s",
             GetLastError(), body_path.c_str());
    MessageBoxW(NULL, msg, L"NVIDIA ShadowPlay", MB_ICONERROR);
    return 1;
  }
  typedef int (*NvLauncherCefMainFn)(void);
  NvLauncherCefMainFn main_fn =
      reinterpret_cast<NvLauncherCefMainFn>(GetProcAddress(host,
                                                           "NvLauncherCefMain"));
  if (!main_fn) {
    MessageBoxW(NULL,
                L"Launcher.dll is missing the NvLauncherCefMain export.",
                L"NVIDIA ShadowPlay", MB_ICONERROR);
    return 1;
  }
  return main_fn();
}
