// Launcher Appearance Package UI controls (DATA-ONLY install/uninstall).
// The browser cannot pick arbitrary script paths or execute module code.
// A trusted local script shipped inside NVIDIA Launcher/Cef/Modules performs
// bounded archive validation and atomic profile catalog management.
#ifndef LAUNCHER_MODULE_ACTIONS_H_
#define LAUNCHER_MODULE_ACTIONS_H_

#include <windows.h>
#include <commdlg.h>
#include <cwchar>
#include <string>

#include "launcher_util.h"

#pragma comment(lib, "comdlg32.lib")

namespace launchermodules {

struct Result {
  bool ok = false;
  bool cancelled = false;
  std::string message;
};

inline bool ValidModuleId(const std::string& id) {
  if (id.empty() || id.size() > 40 || id == "default") return false;
  if (id[0] < 'a' || id[0] > 'z') return false;
  for (char c : id) {
    if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
      return false;
  }
  return true;
}

// File selection is a visible user gesture. A file path never crosses the JS
// bridge and the file-picker restricts input to .spmod archive extensions.
inline Result PickPackage(HWND owner, std::wstring* file) {
  wchar_t selected[32768] = {};
  static const wchar_t kFilter[] =
      L"ShadowPlay NEXT Modules (*.spmod)\0*.spmod\0\0";
  OPENFILENAMEW dialog = {};
  dialog.lStructSize = sizeof(dialog);
  dialog.hwndOwner = owner;
  dialog.lpstrFilter = kFilter;
  dialog.lpstrFile = selected;
  dialog.nMaxFile = static_cast<DWORD>(sizeof(selected) / sizeof(selected[0]));
  dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST |
                 OFN_EXPLORER | OFN_NOCHANGEDIR;
  if (!GetOpenFileNameW(&dialog)) {
    const DWORD error = CommDlgExtendedError();
    return {false, error == 0,
            error == 0 ? "cancelled" : "file picker failed"};
  }
  std::wstring full = selected;
  const auto dot = full.find_last_of(L'.');
  if (dot == std::wstring::npos ||
      _wcsicmp(full.c_str() + dot, L".spmod") != 0) {
    return {false, false, "only .spmod packages are supported"};
  }
  *file = full;
  return {true, false, "selected"};
}

// For single-click local background import: only native file selection.
// The bundled PowerShell script then creates and validates a .spmod package.
inline Result PickBackground(HWND owner, std::wstring* file) {
  wchar_t selected[32768] = {};
  static const wchar_t kFilter[] =
      L"Background Images (*.png;*.jpg;*.jpeg;*.webp)\0*.png;*.jpg;*.jpeg;*.webp\0\0";
  OPENFILENAMEW dialog = {};
  dialog.lStructSize = sizeof(dialog);
  dialog.hwndOwner = owner;
  dialog.lpstrFilter = kFilter;
  dialog.lpstrFile = selected;
  dialog.nMaxFile = static_cast<DWORD>(sizeof(selected)/sizeof(selected[0]));
  dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST |
                 OFN_EXPLORER | OFN_NOCHANGEDIR;
  if (!GetOpenFileNameW(&dialog)) {
    const DWORD error = CommDlgExtendedError();
    return {false, error == 0,
            error == 0 ? "cancelled" : "background picker failed"};
  }
  const std::wstring full = selected;
  const auto dot = full.find_last_of(L'.');
  if (dot == std::wstring::npos) return {false,false,"image extension missing"};
  const wchar_t* ext = full.c_str() + dot;
  if (_wcsicmp(ext,L".png") != 0 && _wcsicmp(ext,L".jpg") != 0 &&
      _wcsicmp(ext,L".jpeg") != 0 && _wcsicmp(ext,L".webp") != 0)
    return {false,false,"unsupported image type"};
  *file = full;
  return {true,false,"selected"};
}

inline std::wstring QuoteArg(const std::wstring& text) {
  // Windows filesystem paths cannot contain double quotes; no cmd.exe used.
  return L"\"" + text + L"\"";
}

inline Result RunInstalledScript(const wchar_t* script_name,
                                 const wchar_t* property,
                                 const std::wstring& value) {
  const std::wstring root = launcherutil::GetRootDir();
  const std::wstring modules = launcherutil::JoinPath(root,
      L"NVIDIA Launcher\\Cef\\Modules");
  const std::wstring script = launcherutil::JoinPath(modules, script_name);
  if (!launcherutil::FileExists(script))
    return {false, false, "trusted module installer is missing"};

  const std::wstring profileRoot = launcherutil::JoinPath(root,
      L"NVIDIA Launcher\\Cef\\Resources\\launcher\\profiles");
  wchar_t system_dir[MAX_PATH] = {};
  if (!GetSystemDirectoryW(system_dir, MAX_PATH))
    return {false, false, "Windows system directory unavailable"};
  const std::wstring powershell = launcherutil::JoinPath(
      system_dir, L"WindowsPowerShell\\v1.0\\powershell.exe");
  if (!launcherutil::FileExists(powershell))
    return {false, false, "Windows PowerShell unavailable"};

  std::wstring line = QuoteArg(powershell) +
      L" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
      QuoteArg(script) + L" " + property + L" " + QuoteArg(value) +
      L" -AssetsRoot " + QuoteArg(profileRoot);
  STARTUPINFOW si = {};
  si.cb = sizeof(si);
  si.dwFlags = STARTF_USESHOWWINDOW;
  si.wShowWindow = SW_HIDE;
  PROCESS_INFORMATION pi = {};
  std::wstring mutable_line = line;
  if (!CreateProcessW(powershell.c_str(), &mutable_line[0], NULL, NULL,
                      FALSE, CREATE_NO_WINDOW, NULL, root.c_str(),
                      &si, &pi))
    return {false, false, "could not start bundled installer"};

  const DWORD wait = WaitForSingleObject(pi.hProcess, 90000);
  DWORD exit_code = 1;
  if (wait == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &exit_code);
  if (wait == WAIT_TIMEOUT) {
    // Only our exact newly created installer child may be terminated.
    // This is not the EXIT ALL mechanism and never targets game services.
    TerminateProcess(pi.hProcess, 1);
    WaitForSingleObject(pi.hProcess, 2000);
  }
  CloseHandle(pi.hThread);
  CloseHandle(pi.hProcess);
  if (wait == WAIT_TIMEOUT) return {false, false, "installation timed out"};
  if (wait != WAIT_OBJECT_0 || exit_code != 0)
    return {false, false, "module package validation or file operation failed"};
  return {true, false, "module operation completed"};
}

} // namespace launchermodules

#endif  // LAUNCHER_MODULE_ACTIONS_H_
