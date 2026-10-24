// Isolated EXIT ALL regression: NO Win32 APIs, NO live processes or config.
// Compile with MSVC: cl /nologo /EHsc /std:c++17 shutdown_policy_test.cpp
#include "../src/launcher_shutdown_policy.h"

#include <cassert>
#include <iostream>
#include <string>
#include <vector>

struct SimulatedProcess {
  std::wstring name;
  std::wstring path;
  bool running = true;
  bool graceful_exit = false;
  bool force_exit = true;
  int polite_requests = 0;
  int forced = 0;
  int closed = 0;
};

class FakeBackend {
 public:
  using Process = int;
  std::vector<SimulatedProcess> items;

  std::vector<int> OpenCandidates(const wchar_t* name) {
    std::vector<int> result;
    for (size_t i = 0; i < items.size(); ++i)
      if (items[i].name == name)
        result.push_back(static_cast<int>(i));
    return result;
  }
  bool MatchesImagePath(Process p, const std::wstring& path) {
    return items[p].path == path;
  }
  bool WaitExited(Process p, unsigned milliseconds) {
    auto& item = items[p];
    if (!item.running) return true;
    if (milliseconds != 0 && item.polite_requests && item.graceful_exit) {
      item.running = false;
      return true;
    }
    return false;
  }
  bool RequestGracefulExit(Process p) {
    ++items[p].polite_requests;
    return items[p].graceful_exit;
  }
  bool ForceTerminate(Process p) {
    ++items[p].forced;
    if (!items[p].force_exit) return false;
    items[p].running = false;
    return true;
  }
  void Close(Process p) { ++items[p].closed; }
};

int main() {
  const std::wstring root = L"C:\\ProgramData\\ShadowPlay-NEXT";
  const std::wstring capture = root + L"\\NVIDIA Capture\\NvCapture.exe";
  using launcher_shutdown::StopExactPath;

  // Missing service: do not signal or force anything.
  FakeBackend missing;
  auto a = StopExactPath(missing, L"NvCapture", capture);
  assert(a.matched == 0 && a.forced == 0);

  // Same name, WRONG PATH (including genuine NVIDIA directory): never stop.
  FakeBackend foreign;
  foreign.items.push_back({L"NvCapture", L"C:\\Program Files\\NVIDIA Corporation\\NvCapture.exe"});
  a = StopExactPath(foreign, L"NvCapture", capture);
  assert(a.matched == 0 && foreign.items[0].polite_requests == 0 &&
         foreign.items[0].forced == 0 && foreign.items[0].closed == 1);

  // Graceful exit must not invoke force.
  FakeBackend graceful;
  graceful.items.push_back({L"NvCapture", capture, true, true, true});
  a = StopExactPath(graceful, L"NvCapture", capture);
  assert(a.graceful == 1 && a.forced == 0 && graceful.items[0].forced == 0 &&
         graceful.items[0].closed == 1);

  // Unresponsive: exactly one bounded escalation on same matched instance.
  FakeBackend hung;
  hung.items.push_back({L"NvCapture", capture, true, false, true});
  a = StopExactPath(hung, L"NvCapture", capture);
  assert(a.forced == 1 && a.failed == 0 && hung.items[0].polite_requests == 1 &&
         hung.items[0].forced == 1 && hung.items[0].closed == 1);

  // Force failed: report failure without retrying or touching unrelated PID.
  FakeBackend denied;
  denied.items.push_back({L"NvCapture", capture, true, false, false});
  a = StopExactPath(denied, L"NvCapture", capture);
  assert(a.failed == 1 && a.forced == 0 && denied.items[0].closed == 1);

  // Duplicate same-path processes: handle BOTH; unrelated same-name skipped.
  FakeBackend many;
  many.items.push_back({L"NvCapture", capture, true, true, true});
  many.items.push_back({L"NvCapture", capture, true, false, true});
  many.items.push_back({L"NvCapture", root + L"\\other\\NvCapture.exe"});
  a = StopExactPath(many, L"NvCapture", capture);
  assert(a.matched == 2 && a.graceful == 1 && a.forced == 1 &&
         many.items[2].polite_requests == 0 && many.items[2].forced == 0);
  for (const auto& proc : many.items) assert(proc.closed == 1);

  // Process that exited between snapshot and signal: no extra action.
  FakeBackend exited;
  exited.items.push_back({L"NvCapture", capture, false, false, true});
  a = StopExactPath(exited, L"NvCapture", capture);
  assert(a.already_exited == 1 && exited.items[0].polite_requests == 0 &&
         exited.items[0].forced == 0);

  // Deployment inventory contract: current paths and no absolute vendor dirs.
  std::size_t n = 0;
  const auto* services = launcher_shutdown::OwnedExecutables(&n);
  bool has_capture = false, has_graphics = false, has_helper = false, has_node = false;
  assert(n >= 10);
  for (std::size_t i = 0; i < n; ++i) {
    const std::wstring path = services[i].relative_path;
    assert(path.find(L":") == std::wstring::npos);
    assert(path.find(L"..") == std::wstring::npos);
    assert(path.find(L"Program Files") == std::wstring::npos);
    has_capture |= path == L"NVIDIA Capture\\NvCapture.exe";
    has_graphics |= path == L"NVIDIA Graphics\\NvGraphics.exe";
    has_helper |= path == L"NVIDIA Container\\nvsphelper.exe";
    has_node |= path == L"Overlay On-Screen Control\\NVIDIA NodeAPI\\NvNode.exe";
  }
  assert(has_capture && has_graphics && has_helper && has_node);
  std::cout << "PASS: isolated EXIT ALL lifecycle regression (7 scenarios + manifest)\n";
}
