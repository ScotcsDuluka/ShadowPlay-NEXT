// launcher_util.cpp - see launcher_util.h.
// winsock2.h MUST precede any windows.h include (tlhelp32 pulls windows.h
// in) so the older winsock.h is suppressed by _WINSOCKAPI_.
#define _CRT_RAND_S 1

#include <winsock2.h>
#include <ws2tcpip.h>

#include "launcher_util.h"
#include "launcher_shutdown_policy.h"

#include <stdio.h>
#include <tlhelp32.h>
#include <wchar.h>

#include <fstream>
#include <algorithm>
#include <sstream>
#include <vector>

namespace launcherutil {

// Linker-provided image header of THIS module (Launcher.dll) — used to
// resolve the body DLL's own directory independent of the process exe.
EXTERN_C IMAGE_DOS_HEADER __ImageBase;

namespace {

std::wstring g_log_path;

CRITICAL_SECTION* LogLock() {
  static CRITICAL_SECTION cs = {};
  static bool init = false;
  if (!init) {
    InitializeCriticalSection(&cs);
    init = true;
  }
  return &cs;
}

void LogLineLocked(const std::string& line) {
  if (g_log_path.empty()) return;
  HANDLE f = CreateFileW(g_log_path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                         NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
  if (f == INVALID_HANDLE_VALUE) return;
  SYSTEMTIME st;
  GetLocalTime(&st);
  char ts[40];
  sprintf(ts, "%04u-%02u-%02u %02u:%02u:%02u.%03u ", st.wYear, st.wMonth,
          st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
  LARGE_INTEGER qpc;
  QueryPerformanceCounter(&qpc);
  std::string out = std::string(ts) + "[" + std::to_string(qpc.QuadPart) +
                    "] " + line + "\r\n";
  DWORD written = 0;
  WriteFile(f, out.data(), (DWORD)out.size(), &written, NULL);
  CloseHandle(f);
}

}  // namespace

std::string LowerAscii(std::string s) {
  for (size_t i = 0; i < s.size(); ++i) {
    if (s[i] >= 'A' && s[i] <= 'Z') s[i] += 32;
  }
  return s;
}

std::string WideToUtf8(const std::wstring& in) {
  if (in.empty()) return std::string();
  int n = WideCharToMultiByte(CP_UTF8, 0, in.c_str(), (int)in.size(), NULL, 0,
                              NULL, NULL);
  std::string out(n, 0);
  if (n > 0) {
    WideCharToMultiByte(CP_UTF8, 0, in.c_str(), (int)in.size(), &out[0], n,
                        NULL, NULL);
  }
  return out;
}

std::wstring Utf8ToWide(const std::string& in) {
  if (in.empty()) return std::wstring();
  int n = MultiByteToWideChar(CP_UTF8, 0, in.c_str(), (int)in.size(), NULL, 0);
  std::wstring out(n, 0);
  if (n > 0) {
    MultiByteToWideChar(CP_UTF8, 0, in.c_str(), (int)in.size(), &out[0], n);
  }
  return out;
}

std::wstring GetExeDir() {
  wchar_t exe[MAX_PATH];
  GetModuleFileNameW(NULL, exe, MAX_PATH);
  std::wstring dir(exe);
  size_t slash = dir.find_last_of(L'\\');
  if (slash != std::wstring::npos) dir.resize(slash);
  return dir;
}

std::wstring GetDllDir() {
  wchar_t dll[MAX_PATH];
  GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), dll, MAX_PATH);  std::wstring dir(dll);
  size_t slash = dir.find_last_of(L'\\');
  if (slash != std::wstring::npos) dir.resize(slash);
  return dir;
}

std::wstring GetRootDir() {
  // Bootstrap exe at the root -> its dir IS the root. Defensive fallback
  // for a body DLL launched from NVIDIA Launcher\Cef: climb two levels so the
  // supervisor still finds NvConfig\ and the family exes.
  std::wstring dir = GetExeDir();
  const std::wstring launcher_suffix = L"\\NVIDIA Launcher\\Cef";
  size_t tail = dir.rfind(launcher_suffix);
  if (tail != std::wstring::npos &&
      tail + launcher_suffix.size() == dir.size()) {
    dir.resize(tail);
  }
  return dir;
}

std::wstring JoinPath(const std::wstring& a, const std::wstring& b) {
  if (a.empty()) return b;
  if (b.empty()) return a;
  if (a[a.size() - 1] == L'\\') return a + b;
  return a + L"\\" + b;
}

std::string WidePathUtf8(const std::wstring& p) { return WideToUtf8(p); }

bool FileExists(const std::wstring& path) {
  DWORD attr = GetFileAttributesW(path.c_str());
  return attr != INVALID_FILE_ATTRIBUTES &&
         !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

void EnsureParentDir(const std::wstring& path) {
  size_t slash = path.find_last_of(L'\\');
  if (slash == std::wstring::npos || slash == 0) return;
  std::wstring parent = path.substr(0, slash);
  if (!FileExists(parent)) {
    // AppLayout.EnsureParentDir parity: create intermediate directories.
    std::wstring probe;
    for (size_t i = 0; i < parent.size(); ++i) {
      probe.push_back(parent[i]);
      if (parent[i] == L'\\' && i > 2) CreateDirectoryW(probe.c_str(), NULL);
    }
    CreateDirectoryW(parent.c_str(), NULL);
  }
}

void LogInit() {
  EnterCriticalSection(LogLock());
  std::wstring root = GetRootDir();
  std::wstring logs = JoinPath(root, L"Logs");
  CreateDirectoryW(logs.c_str(), NULL);
  g_log_path = JoinPath(logs, L"launcher-cef.log");
  LeaveCriticalSection(LogLock());
  LogLineLocked("launcher-cef log opened (pid " +
                std::to_string(GetCurrentProcessId()) + ")");
}

void LogLine(const std::string& line) { LogLineLocked(line); }

bool ProcessRunning(const wchar_t* name) {
  // Toolhelp32 reports the full image name WITH extension; callers pass
  // the .NET-style process name WITHOUT it (Process.GetProcessesByName
  // parity). Accept both spellings.
  std::wstring with_ext = std::wstring(name) + L".exe";
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snap == INVALID_HANDLE_VALUE) return false;
  PROCESSENTRY32W pe;
  pe.dwSize = sizeof(pe);
  bool found = false;
  if (Process32FirstW(snap, &pe)) {
    do {
      if (_wcsicmp(pe.szExeFile, name) == 0 ||
          _wcsicmp(pe.szExeFile, with_ext.c_str()) == 0) {
        found = true;
        break;
      }
    } while (Process32NextW(snap, &pe));
  }
  CloseHandle(snap);
  return found;
}

bool ProcessRunningFromPath(const wchar_t* name,
                            const std::wstring& exe_path) {
  std::wstring with_ext = std::wstring(name) + L".exe";
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snap == INVALID_HANDLE_VALUE) return false;
  PROCESSENTRY32W pe;
  pe.dwSize = sizeof(pe);
  bool found = false;
  if (Process32FirstW(snap, &pe)) {
    do {
      if (_wcsicmp(pe.szExeFile, name) != 0 &&
          _wcsicmp(pe.szExeFile, with_ext.c_str()) != 0) {
        continue;
      }
      HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                             pe.th32ProcessID);
      if (!p) continue;
      wchar_t path[MAX_PATH];
      DWORD size = MAX_PATH;
      if (QueryFullProcessImageNameW(p, 0, path, &size)) {
        if (_wcsicmp(path, exe_path.c_str()) == 0) found = true;
      }
      CloseHandle(p);
      if (found) break;
    } while (Process32NextW(snap, &pe));
  }
  CloseHandle(snap);
  return found;
}

bool StartProcess(const std::wstring& exe_path, const std::wstring& args) {
  if (!FileExists(exe_path)) {
    LogLine("start skipped (missing): " + WideToUtf8(exe_path));
    return false;
  }
  std::wstring dir;
  size_t slash = exe_path.find_last_of(L'\\');
  if (slash != std::wstring::npos) dir = exe_path.substr(0, slash);
  std::wstring cmdline = L"\"" + exe_path + L"\"";
  if (!args.empty()) cmdline += L" " + args;
  STARTUPINFOW si;
  memset(&si, 0, sizeof(si));
  si.cb = sizeof(si);
  PROCESS_INFORMATION pi;
  memset(&pi, 0, sizeof(pi));
  std::vector<wchar_t> cmd(cmdline.begin(), cmdline.end());
  cmd.push_back(L'\0');
  BOOL ok = CreateProcessW(NULL, cmd.data(), NULL, NULL, FALSE, 0, NULL,
                           dir.c_str(), &si, &pi);
  if (ok) {
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    LogLine("started: " + WideToUtf8(cmdline));
  } else {
    LogLine("start FAILED (" + std::to_string(GetLastError()) + "): " +
            WideToUtf8(cmdline));
  }
  return ok != FALSE;
}

void KillProcessByName(const wchar_t* name) {
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snap == INVALID_HANDLE_VALUE) return;
  PROCESSENTRY32W pe;
  pe.dwSize = sizeof(pe);
  if (Process32FirstW(snap, &pe)) {
    do {
      if (_wcsicmp(pe.szExeFile, name) != 0) continue;
      if (pe.th32ProcessID == GetCurrentProcessId()) continue;
      HANDLE p = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE,
                             pe.th32ProcessID);
      if (!p) {
        LogLine(std::string("kill open failed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID) + " err " +
                std::to_string(GetLastError()));
        continue;
      }
      if (!TerminateProcess(p, 1)) {
        LogLine(std::string("kill failed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID) + " err " +
                std::to_string(GetLastError()));
      } else {
        WaitForSingleObject(p, 3000);
        LogLine(std::string("killed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID));
      }
      CloseHandle(p);
    } while (Process32NextW(snap, &pe));
  }
  CloseHandle(snap);
}

void KillProcessFromPath(const wchar_t* name, const std::wstring& exe_path) {
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snap == INVALID_HANDLE_VALUE) return;
  std::wstring with_ext = std::wstring(name) + L".exe";
  PROCESSENTRY32W pe;
  pe.dwSize = sizeof(pe);
  if (Process32FirstW(snap, &pe)) {
    do {
      if (_wcsicmp(pe.szExeFile, name) != 0 &&
          _wcsicmp(pe.szExeFile, with_ext.c_str()) != 0) {
        continue;
      }
      if (pe.th32ProcessID == GetCurrentProcessId()) continue;
      HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
                             FALSE, pe.th32ProcessID);
      if (!p) {
        LogLine(std::string("kill open failed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID) + " err " +
                std::to_string(GetLastError()));
        continue;
      }
      wchar_t path[MAX_PATH];
      DWORD size = MAX_PATH;
      bool match = false;
      if (QueryFullProcessImageNameW(p, 0, path, &size)) {
        match = _wcsicmp(path, exe_path.c_str()) == 0;
      }
      if (!match) {
        CloseHandle(p);
        continue;
      }
      if (!TerminateProcess(p, 1)) {
        LogLine(std::string("kill failed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID) + " err " +
                std::to_string(GetLastError()));
      } else {
        WaitForSingleObject(p, 3000);
        LogLine(std::string("killed: ") + WideToUtf8(name) + " pid " +
                std::to_string(pe.th32ProcessID) + " (overlay mode OFF)");
      }
      CloseHandle(p);
    } while (Process32NextW(snap, &pe));
  }
  CloseHandle(snap);
}

// EXIT ALL only: ask matched GUI windows to close, then wait with a hard
// deadline and escalate on the SAME opened process handle. The simulator
// tests in tests/ never instantiate this Win32 adapter.
class Win32ShutdownBackend {
 public:
  using Process = HANDLE;

  std::vector<Process> OpenCandidates(const wchar_t* name) {
    std::vector<Process> found;
    const std::wstring with_ext = std::wstring(name) + L".exe";
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return found;
    PROCESSENTRY32W entry = {};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snap, &entry)) {
      do {
        if (_wcsicmp(entry.szExeFile, name) != 0 &&
            _wcsicmp(entry.szExeFile, with_ext.c_str()) != 0)
          continue;
        if (entry.th32ProcessID == GetCurrentProcessId()) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION |
                                         PROCESS_TERMINATE | SYNCHRONIZE,
                                     FALSE, entry.th32ProcessID);
        if (process) found.push_back(process);
        else LogLine("exit-all: could not open " + WideToUtf8(name) +
                     " pid=" + std::to_string(entry.th32ProcessID));
      } while (Process32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return found;
  }

  bool MatchesImagePath(Process process, const std::wstring& full_path) {
    wchar_t image[32768];
    DWORD length = static_cast<DWORD>(sizeof(image) / sizeof(image[0]));
    return QueryFullProcessImageNameW(process, 0, image, &length) &&
           _wcsicmp(image, full_path.c_str()) == 0;
  }

  bool WaitExited(Process process, unsigned milliseconds) {
    return WaitForSingleObject(process, milliseconds) == WAIT_OBJECT_0;
  }

  bool RequestGracefulExit(Process process) {
    struct WindowContext { DWORD pid; bool requested; };
    WindowContext ctx = {GetProcessId(process), false};
    EnumWindows([](HWND hwnd, LPARAM userdata) -> BOOL {
      auto* context = reinterpret_cast<WindowContext*>(userdata);
      DWORD owner = 0;
      GetWindowThreadProcessId(hwnd, &owner);
      // Do not send WM_CLOSE to unrelated apps or to child/owned windows.
      if (owner == context->pid && GetWindow(hwnd, GW_OWNER) == NULL &&
          PostMessageW(hwnd, WM_CLOSE, 0, 0))
        context->requested = true;
      return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return ctx.requested;
  }

  bool ForceTerminate(Process process) {
    return TerminateProcess(process, 1) != 0;
  }

  void Close(Process process) { CloseHandle(process); }
};

void StopOwnedProcessFromPath(const wchar_t* name,
                              const std::wstring& exe_path) {
  Win32ShutdownBackend backend;
  const auto summary = launcher_shutdown::StopExactPath(
      backend, name, exe_path, 1500, 1500);
  if (summary.matched || summary.failed) {
    LogLine("exit-all: " + WideToUtf8(exe_path) +
            " matched=" + std::to_string(summary.matched) +
            " graceful=" + std::to_string(summary.graceful) +
            " forced=" + std::to_string(summary.forced) +
            " already-exited=" + std::to_string(summary.already_exited) +
            " failed=" + std::to_string(summary.failed));
  }
}

// Loopback connect with a hard deadline. A plain blocking connect can
// stall the caller (including the CEF UI thread) while a local endpoint is
// half-open or the network stack is unhealthy.
static bool ConnectLoopbackSocket(SOCKET s, unsigned port,
                                  unsigned timeout_ms) {
  u_long nonblocking = 1;
  if (ioctlsocket(s, FIONBIO, &nonblocking) == SOCKET_ERROR) return false;

  sockaddr_in a;
  memset(&a, 0, sizeof(a));
  a.sin_family = AF_INET;
  a.sin_port = htons(static_cast<u_short>(port));
  a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

  int rc = connect(s, reinterpret_cast<sockaddr*>(&a), sizeof(a));
  if (rc == SOCKET_ERROR) {
    const int connect_error = WSAGetLastError();
    if (connect_error != WSAEWOULDBLOCK &&
        connect_error != WSAEINPROGRESS &&
        connect_error != WSAEALREADY) {
      u_long blocking = 0;
      ioctlsocket(s, FIONBIO, &blocking);
      WSASetLastError(connect_error);
      return false;
    }

    fd_set writable;
    fd_set exceptional;
    FD_ZERO(&writable);
    FD_ZERO(&exceptional);
    FD_SET(s, &writable);
    FD_SET(s, &exceptional);
    timeval timeout;
    timeout.tv_sec = static_cast<long>(timeout_ms / 1000);
    timeout.tv_usec = static_cast<long>((timeout_ms % 1000) * 1000);
    rc = select(0, NULL, &writable, &exceptional, &timeout);
    if (rc <= 0) {
      const int wait_error = (rc == 0) ? WSAETIMEDOUT : WSAGetLastError();
      u_long blocking = 0;
      ioctlsocket(s, FIONBIO, &blocking);
      WSASetLastError(wait_error);
      return false;
    }

    int socket_error = 0;
    int error_size = sizeof(socket_error);
    if (getsockopt(s, SOL_SOCKET, SO_ERROR,
                   reinterpret_cast<char*>(&socket_error),
                   &error_size) == SOCKET_ERROR ||
        socket_error != 0) {
      const int wait_error = socket_error != 0
                                 ? socket_error
                                 : WSAGetLastError();
      u_long blocking = 0;
      ioctlsocket(s, FIONBIO, &blocking);
      WSASetLastError(wait_error);
      return false;
    }
  }

  u_long blocking = 0;
  if (ioctlsocket(s, FIONBIO, &blocking) == SOCKET_ERROR) return false;
  return true;
}

bool IsTcpPortOpen(unsigned port) {
  WSADATA wsa;
  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
  bool alive = false;
  SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (s != INVALID_SOCKET) {
    alive = ConnectLoopbackSocket(s, port, 200);
    closesocket(s);
  }
  WSACleanup();
  return alive;
}

bool WaitForTcpPort(unsigned port, unsigned timeout_ms) {
  const ULONGLONG started = GetTickCount64();
  bool alive = IsTcpPortOpen(port);
  while (!alive) {
    const ULONGLONG elapsed = GetTickCount64() - started;
    if (elapsed >= timeout_ms) break;
    Sleep(static_cast<DWORD>(std::min<ULONGLONG>(50, timeout_ms - elapsed)));
    alive = IsTcpPortOpen(port);
  }
  if (!alive) {
    LogLine("backend wait timeout on port " + std::to_string(port));
  }
  return alive;
}

bool SendHubLine(const std::string& line) {
  WSADATA wsa;
  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
  bool ok = false;
  SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (s != INVALID_SOCKET) {
    const DWORD send_timeout_ms = 750;
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO,
               reinterpret_cast<const char*>(&send_timeout_ms),
               sizeof(send_timeout_ms));
    // OPEN OVERLAY calls this from a worker; bound the worker too, rather
    // than allowing a stalled localhost connect/send to linger indefinitely.
    if (ConnectLoopbackSocket(s, 5001, 300)) {
      std::string frame = line + "\r\n";
      int sent = 0;
      const int frame_size = static_cast<int>(frame.size());
      while (sent < frame_size) {
        const int n = send(s, frame.data() + sent, frame_size - sent, 0);
        if (n <= 0) break;
        sent += n;
      }
      ok = sent == frame_size;
    }
    closesocket(s);
  }
  WSACleanup();
  LogLine(std::string("hub send \"") + line + "\" -> " + (ok ? "ok" : "failed"));
  return ok;
}

static bool HttpRequestLocal(unsigned port, const std::wstring& path,
                             bool post) {
  WSADATA wsa;
  if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
  bool ok = false;
  SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (s != INVALID_SOCKET) {
    // Connect is separately bounded because SO_RCVTIMEO does not apply to it.
    const DWORD io_timeout_ms = 750;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO,
               reinterpret_cast<const char*>(&io_timeout_ms),
               sizeof(io_timeout_ms));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO,
               reinterpret_cast<const char*>(&io_timeout_ms),
               sizeof(io_timeout_ms));

    if (ConnectLoopbackSocket(s, port, 300)) {
      std::string req = std::string(post ? "POST " : "GET ") +
                        WideToUtf8(path) +
                        " HTTP/1.1\r\nHost: 127.0.0.1\r\n"
                        "Connection: close\r\n";
      if (post) req += "Content-Type: application/json\r\n"
                       "Content-Length: 2\r\n";
      req += post ? "\r\n{}" : "\r\n";
      int sent = 0;
      const int request_size = static_cast<int>(req.size());
      while (sent < request_size) {
        const int n = send(s, req.data() + sent, request_size - sent, 0);
        if (n <= 0) break;
        sent += n;
      }
      if (sent == request_size) {
        // Read a complete status line instead of assuming a single recv()
        // contains the whole header. SO_RCVTIMEO bounds every blocking read.
        std::string status_line;
        char buf[64] = {};
        const ULONGLONG response_deadline = GetTickCount64() + io_timeout_ms;
        while (status_line.size() < 256) {
          const ULONGLONG now = GetTickCount64();
          if (now >= response_deadline) break;
          const DWORD remaining = static_cast<DWORD>(response_deadline - now);
          fd_set readable;
          FD_ZERO(&readable);
          FD_SET(s, &readable);
          timeval timeout;
          timeout.tv_sec = static_cast<long>(remaining / 1000);
          timeout.tv_usec = static_cast<long>((remaining % 1000) * 1000);
          const int ready = select(0, &readable, NULL, NULL, &timeout);
          if (ready <= 0) break;
          const int n = recv(s, buf, sizeof(buf), 0);
          if (n <= 0) break;
          status_line.append(buf, n);
          if (status_line.find("\r\n") != std::string::npos) break;
        }
        const size_t line_end = status_line.find("\r\n");
        if (line_end != std::string::npos) status_line.resize(line_end);
        ok = status_line.find("HTTP/1.") == 0 &&
             status_line.find(" 200 ") != std::string::npos;
      }
    }
    closesocket(s);
  }
  WSACleanup();
  if (post)
    LogLine("loopback POST " + WideToUtf8(path) + " -> " +
            (ok ? "ok" : "failed"));
  return ok;
}

bool HttpPostLocal(unsigned port, const std::wstring& path) {
  return HttpRequestLocal(port, path, true);
}

bool HttpGetLocal(unsigned port, const std::wstring& path) {
  return HttpRequestLocal(port, path, false);
}

static std::wstring ConfigPath() {
  return JoinPath(GetRootDir(), L"NVIDIA Config\\config.json");
}

bool ReadConfigBool(const wchar_t* section, const wchar_t* key,
                    bool fallback) {
  std::ifstream f(ConfigPath().c_str(), std::ios::binary);
  if (!f.is_open()) return fallback;
  std::ostringstream buf;
  buf << f.rdbuf();
  launcherjson::JVal root;
  if (!launcherjson::Parse(buf.str(), &root) ||
      root.type != launcherjson::JVal::kObj) {
    return fallback;
  }
  const launcherjson::JVal* sec =
      root.FindNoCase(WideToUtf8(section).c_str());
  if (!sec || sec->type != launcherjson::JVal::kObj) return fallback;
  const launcherjson::JVal* v = sec->FindNoCase(WideToUtf8(key).c_str());
  if (!v || v->type != launcherjson::JVal::kBool) return fallback;
  return v->b;
}

bool WriteConfigBool(const wchar_t* section, const wchar_t* key, bool value) {
  std::wstring path = ConfigPath();
  EnsureParentDir(path);

  std::string text;
  {
    std::ifstream f(path.c_str(), std::ios::binary);
    if (f.is_open()) {
      std::ostringstream buf;
      buf << f.rdbuf();
      text = buf.str();
    }
  }
  launcherjson::JVal root;
  if (!text.empty()) {
    if (!launcherjson::Parse(text, &root) ||
        root.type != launcherjson::JVal::kObj) {
      LogLine("config.json unparsable - refusing to overwrite");
      return false;
    }
  } else {
    root.type = launcherjson::JVal::kObj;
  }

  if (!launcherjson::SetBoolInObject(&root, WideToUtf8(section).c_str(),
                                     WideToUtf8(key).c_str(), value)) {
    LogLine("config.json SetBool failed: section type collision");
    return false;
  }
  std::string final_json = launcherjson::Serialize(root);

  // Atomic swap + .bak recovery copy — AppConfigShared.WriteBool contract.
  std::wstring tmp = path + L"." + std::to_wstring(GetCurrentProcessId()) +
                     L".tmp";
  std::wstring bak = path + L".bak";
  {
    std::ofstream f(tmp.c_str(), std::ios::binary | std::ios::trunc);
    if (!f.is_open()) {
      LogLine("config.json tmp write failed");
      return false;
    }
    f.write(final_json.data(), (std::streamsize)final_json.size());
  }
  if (FileExists(path)) CopyFileW(path.c_str(), bak.c_str(), FALSE);
  if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
    LogLine("config.json move failed err " + std::to_string(GetLastError()));
    DeleteFileW(tmp.c_str());
    return false;
  }
  LogLine(std::string("config.json ") + WideToUtf8(section) + "." +
          WideToUtf8(key) + " = " + (value ? "true" : "false"));
  return true;
}

}  // namespace launcherutil
