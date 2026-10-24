// launcher_util.h - shared primitives of the Launcher CEF lane: UTF-8
// helpers, layout paths (AppLayout.vb root contract), a small line logger,
// Win32 process plumbing and the config.json read/patch API (the C++
// counterpart of Common\AppConfigShared.vb WriteBool — read-modify-write
// on the CURRENT file content so keys owned by other processes survive).
#ifndef LAUNCHER_UTIL_H_
#define LAUNCHER_UTIL_H_

#include <string>

#include "launcher_json.h"

namespace launcherutil {

// ── strings / paths ─────────────────────────────────────────────────────
std::string WideToUtf8(const std::wstring& in);
std::wstring Utf8ToWide(const std::string& in);
std::string LowerAscii(std::string s);
std::wstring GetExeDir();      // directory of the running .exe (process-wide)
std::wstring GetDllDir();      // directory of Launcher.dll (host body owner)
// Product layout root: the folder holding NvConfig\, NvLauncher\, Flags\...
// The bootstrap exe sits at the root; the body DLL sits in
// NvLauncher\Cef\, while CEF runtime files live beside NVIDIA OSC.exe. Both
// resolve to the same root.
std::wstring GetRootDir();
std::wstring JoinPath(const std::wstring& a, const std::wstring& b);
std::string WidePathUtf8(const std::wstring& p);
bool FileExists(const std::wstring& path);
void EnsureParentDir(const std::wstring& path);

// ── logging ─────────────────────────────────────────────────────────────
// Appends one timestamped line to <root>\Logs\launcher-cef.log.
// Safe on any thread; never throws; a logging failure is silently dropped.
void LogInit();
void LogLine(const std::string& line);

// ── processes ───────────────────────────────────────────────────────────
bool ProcessRunning(const wchar_t* name);  // name WITHOUT .exe
// True when a process named |name| runs FROM |exe_path| (path dedupe —
// Main.vb StartCefOverlay contract: the WinForm overlay is also named
// "NVIDIA Share"; only the exact NvLauncher\Cef exe counts).
bool ProcessRunningFromPath(const wchar_t* name, const std::wstring& exe_path);
bool StartProcess(const std::wstring& exe_path, const std::wstring& args);
// TerminateProcess on every process named |name| (except our own PID).
void KillProcessByName(const wchar_t* name);
// TerminateProcess only on instances of |name| running FROM |exe_path|
// (exact image-path match, StartCefOverlay dedupe contract).
void KillProcessFromPath(const wchar_t* name, const std::wstring& exe_path);
// EXIT ALL only: exact path+name verification, bounded graceful window close,
// then targeted force fallback. Never matches another vendor's installation.
void StopOwnedProcessFromPath(const wchar_t* name,
                              const std::wstring& exe_path);
bool IsTcpPortOpen(unsigned port);
bool WaitForTcpPort(unsigned port, unsigned timeout_ms);
// One-shot loopback send to the NVIDIA API hub (:5001). Frame parity with
// TcpClientHelper.Send: "[Send] <app>|<cmd>\r\n". Best effort.
bool SendHubLine(const std::string& line);
// One-shot loopback HTTP POST (CEF lane OPEN OVERLAY: Hotkey/Toggle makes
// the node broadcast WindowState and the genuine page opens). Minimal
// HTTP/1.1 over raw TCP, SendHubLine plumbing class. Best effort.
bool HttpPostLocal(unsigned port, const std::wstring& path);
// HTTP 200 for a product-owned local service's static landing page, NOT
// hardware/recording readiness. Bounded connect/send/response deadlines.
bool HttpGetLocal(unsigned port, const std::wstring& path);

// ── config.json (AppConfigShared.vb contract) ───────────────────────────
bool ReadConfigBool(const wchar_t* section, const wchar_t* key,
                    bool fallback);
bool WriteConfigBool(const wchar_t* section, const wchar_t* key, bool value);

}  // namespace launcherutil

#endif  // LAUNCHER_UTIL_H_
