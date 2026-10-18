// launcher_supervisor.cpp - see launcher_supervisor.h.
#include "launcher_supervisor.h"

#include <stdio.h>

#include <windows.h>

#include "launcher_util.h"

using launcherutil::LogLine;

namespace {

// Process image names WITHOUT .exe — Process.GetProcessesByName parity.
const wchar_t* kShadowPlay = L"NVIDIA ShadowPlay";
const wchar_t* kNotifier = L"NVIDIA Notifier";
const wchar_t* kContainer = L"NvContainer";
const wchar_t* kWebHelper = L"NVIDIA Web Helper";
const wchar_t* kCefOverlay = L"NVIDIA OSC";

// The WinForm TCP hub uses NvBackend.exe; the older NVIDIA Backend.exe path
// remains a compatibility candidate.
const wchar_t* kHub = L"NvBackend";

std::wstring RootP(const wchar_t* folder, const wchar_t* file);

bool HubRunning() {
  const std::wstring candidates[] = {
      RootP(L"NvBackend", L"NvBackend.exe"),
      RootP(L"NvBackend", L"NVIDIA Backend.exe"),
      RootP(L"", L"NVIDIA Backend.exe"),
  };
  for (size_t i = 0; i < sizeof(candidates) / sizeof(candidates[0]); ++i) {
    const std::wstring& path = candidates[i];
    const size_t slash = path.find_last_of(L'\\');
    const size_t dot = path.find_last_of(L'.');
    const std::wstring name = path.substr(
        slash == std::wstring::npos ? 0 : slash + 1,
        (dot == std::wstring::npos ? path.size() : dot) -
            (slash == std::wstring::npos ? 0 : slash + 1));
    if (launcherutil::ProcessRunningFromPath(name.c_str(), path)) return true;
  }
  return false;
}

bool BackendRunning() {
  return launcherutil::IsTcpPortOpen(59011);
}

std::wstring OscExePath() {
  return RootP(L"Overlay On-Screen Control\\NVIDIA OSC", L"NVIDIA OSC.exe");
}

bool OscRunning() {
  return launcherutil::ProcessRunningFromPath(kCefOverlay, OscExePath());
}

void StartIfMissing(const wchar_t* name, const std::wstring& exe_path,
                    const std::wstring& args = L"") {
  if (!launcherutil::FileExists(exe_path)) {
    LogLine(std::string("service executable missing: ") +
            launcherutil::WideToUtf8(exe_path));
    return;
  }
  const size_t slash = exe_path.find_last_of(L'\\');
  const size_t dot = exe_path.find_last_of(L'.');
  const std::wstring image_name = exe_path.substr(
      slash == std::wstring::npos ? 0 : slash + 1,
      (dot == std::wstring::npos ? exe_path.size() : dot) -
          (slash == std::wstring::npos ? 0 : slash + 1));
  if (launcherutil::ProcessRunningFromPath(image_name.c_str(), exe_path)) {
    LogLine(std::string("adopt running: ") + launcherutil::WideToUtf8(name));
    return;
  }
  launcherutil::StartProcess(exe_path, args);
}

std::wstring RootP(const wchar_t* folder, const wchar_t* file) {
  return launcherutil::JoinPath(
      launcherutil::JoinPath(launcherutil::GetRootDir(), folder), file);
}

// First existing hub exe wins: staged name, legacy name, then the layout
// root (dev bin contract).
std::wstring ResolveHubExe() {
  const std::wstring candidates[] = {
      RootP(L"NvBackend", L"NvBackend.exe"),
      RootP(L"NvBackend", L"NVIDIA Backend.exe"),
      RootP(L"", L"NVIDIA Backend.exe"),
  };
  for (size_t i = 0; i < 3; ++i) {
    if (launcherutil::FileExists(candidates[i])) return candidates[i];
  }
  return candidates[0];
}

}  // namespace

std::string LauncherState::ToJson() const {
  // Overlay API label contract (Main.vb IF_APP_Tick):
  //   !shadowplay || overlay_ready -> "OVERLAY API" else "Loading..."
  const char* overlay_label =
      (!shadowplay || overlay_ready) ? "OVERLAY API" : "LOADING";
  std::string o = "{";
  o += "\"overlayApi\":{\"running\":" + std::string(shadowplay ? "true" : "false") +
       ",\"ready\":" + std::string(overlay_ready ? "true" : "false") +
       ",\"label\":\"" + overlay_label + "\"}";
  o += ",\"notifierApi\":{\"running\":" + std::string(notifier ? "true" : "false") + "}";
  o += ",\"nvApi\":{\"running\":" + std::string(nv_api ? "true" : "false") + "}";
  o += ",\"lanes\":{\"container\":" + std::string(container ? "true" : "false") +
       ",\"webHelper\":" + std::string(web_helper ? "true" : "false") +
       ",\"cefOverlay\":" + std::string(cef_overlay ? "true" : "false") + "}";
  o += ",\"overlayEnabled\":" + std::string(overlay_enabled ? "true" : "false");
  o += ",\"engineOverlay\":" + std::string(engine_overlay ? "true" : "false");
  o += "}";
  return o;
}

LauncherSupervisor* LauncherSupervisor::Get() {
  static LauncherSupervisor* s = new LauncherSupervisor();
  return s;
}

LauncherState LauncherSupervisor::Snapshot() {
  LauncherState st;
  st.engine_overlay =
      launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false);
  st.overlay_enabled =
      launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false);
  st.cef_overlay = OscRunning();
  st.web_helper = BackendRunning();
  st.nv_api = st.engine_overlay ? st.web_helper : HubRunning();
  st.shadowplay = st.overlay_enabled &&
                  (launcherutil::ProcessRunning(kShadowPlay) ||
                   (st.engine_overlay && st.cef_overlay));
  st.overlay_ready = st.engine_overlay
                         ? (st.web_helper && st.cef_overlay)
                         : launcherutil::FileExists(RootP(L"Flags", L"Ready"));
  st.notifier = launcherutil::ProcessRunning(kNotifier);
  st.container = launcherutil::ProcessRunningFromPath(
      kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"));
  return st;
}

void LauncherSupervisor::Start(bool supervise, PushFn push) {
  supervise_ = supervise;
  push_ = push;
  stop_ = 0;
  if (supervise_) StartBaseChain();
  // Engine mode persisted as CEF: bring the genuine chain up at boot too
  // (the engine chain otherwise starts only on the mode toggle).
  if (supervise_ &&
      launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false) &&
      launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false)) {
    StartEngineOverlayChainAsync();
  }
  poll_thread_ = CreateThread(NULL, 0, PollTramp, this, 0, NULL);
}

void LauncherSupervisor::Stop() {
  InterlockedExchange(&stop_, 1);
  if (poll_thread_) {
    WaitForSingleObject(poll_thread_, 2500);
    CloseHandle(poll_thread_);
    poll_thread_ = NULL;
  }
  if (chain_thread_) {
    // The chain thread is bounded by the :59001 wait (<=10s); detach-safe.
    CloseHandle(chain_thread_);
    chain_thread_ = NULL;
  }
}

unsigned long LauncherSupervisor::PollTramp(void* self) {
  static_cast<LauncherSupervisor*>(self)->PollLoop();
  return 0;
}

unsigned long LauncherSupervisor::ChainTramp(void* self) {
  static_cast<LauncherSupervisor*>(self)->StartEngineOverlayChain();
  return 0;
}

void LauncherSupervisor::PollLoop() {
  // 1s tick = Main.vb IF_APP.Interval. First snapshot goes out immediately
  // so the page paints real state without a startup blink.
  int tick = 0;
  while (!stop_) {
    LauncherState st = Snapshot();
    std::string json = st.ToJson();
    if (json != last_pushed_ && push_) {
      last_pushed_ = json;
      push_(json);
    }
    // Main.vb parity: if the NOTIFIER lane is down, the Ready marker is
    // stale — remove it (the overlay writes it when its API surface opens).
    if (!st.engine_overlay && !st.notifier && st.overlay_ready && supervise_) {
      DeleteFileW(RootP(L"Flags", L"Ready").c_str());
      LogLine("stale Flags\\Ready removed (notifier down)");
    }
    // Slow lane housekeeping (every 5s): keep the base chain alive.
    if (supervise_ && (++tick % 5) == 0) {
      if (!launcherutil::ProcessRunningFromPath(
              kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"))) {
        LogLine("NvContainer lost - restarting");
        StartIfMissing(kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"));
      }
      const bool winform_mode =
          !launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false);
      if (winform_mode && !HubRunning()) {
        const std::wstring hub_exe = ResolveHubExe();
        if (launcherutil::FileExists(hub_exe)) {
          LogLine("hub lost - restarting");
          StartIfMissing(kHub, hub_exe);
        }
      }
    }
    for (int i = 0; i < 10 && !stop_; ++i) Sleep(100);
  }
}

void LauncherSupervisor::StartBaseChain() {
  // NvContainer runs in both modes and owns the managed helper/presenter.
  StartIfMissing(kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"));
  // The TCP hub belongs only to the WinForm engine lane.
  if (!launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false)) {
    StartIfMissing(kHub, ResolveHubExe());
  }
}

void LauncherSupervisor::StartEngineOverlayChain() {
  LogLine("engine overlay chain: begin");
  // NvContainer is the sole owner of the Node API helper and CEF host.
  StartIfMissing(kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"));
  if (!launcherutil::WaitForTcpPort(59011, 15000)) {
    LogLine("NvContainer API backend did not become ready on port 59011");
    return;
  }
  if (!launcherutil::WaitForTcpPort(59013, 15000)) {
    LogLine("NvContainer CEF host did not become ready on port 59013");
    return;
  }
  LogLine("NvContainer-managed API and CEF host ready (presenter remains hidden)");
  LogLine("engine overlay chain: done");
}

void LauncherSupervisor::StartEngineOverlayChainAsync() {
  if (chain_thread_) {
    if (WaitForSingleObject(chain_thread_, 0) == WAIT_OBJECT_0) {
      CloseHandle(chain_thread_);
      chain_thread_ = NULL;
    } else {
      LogLine("engine overlay chain already running");
      return;
    }
  }
  chain_thread_ = CreateThread(NULL, 0, ChainTramp, this, 0, NULL);
  // The thread self-terminates after the chain; the handle is reaped in
  // Stop() or on the next chain request once done.
  if (WaitForSingleObject(chain_thread_, 0) == WAIT_OBJECT_0) {
    CloseHandle(chain_thread_);
    chain_thread_ = NULL;
  }
}

void LauncherSupervisor::StopCefOverlay() {
  // NvContainer owns the OSC process; hide it rather than killing a child
  // that the container watchdog would immediately restart.
  if (!launcherutil::HttpPostLocal(59013, L"/hide")) {
    LogLine("CEF host hide request failed on port 59013");
  }
}

bool LauncherSupervisor::SendOpenOverlay() {
  if (launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false)) {
    // CEF lane: ask the NvContainer-owned OSC host to show, not toggle.
    if (!launcherutil::IsTcpPortOpen(59011) ||
        !launcherutil::IsTcpPortOpen(59013)) {
      LogLine("OPEN OVERLAY (CEF): chain down - starting");
      StartEngineOverlayChain();
    }
    if (!launcherutil::WaitForTcpPort(59013, 500)) return false;
    return launcherutil::HttpPostLocal(59013, L"/show");
  }
  // WINFORM lane: TcpClientHelper.Send("open_overlay") wire parity:
  //   "[Send] NVIDIA  APP|open_overlay\r\n" to 127.0.0.1:5001.
  return launcherutil::SendHubLine("[Send] NVIDIA  APP|open_overlay");
}

void LauncherSupervisor::InstallerExit() {
  // Single-source config: clear the overlay switch (Main.vb RadioButton2).
  launcherutil::WriteConfigBool(L"Overlay", L"UseOverlayEnabled", false);
  const struct { const wchar_t* name; const wchar_t* path; } owned[] = {
      {L"NVIDIA Notifier", L"NvOverlay\\WinForm\\NVIDIA Notifier.exe"},
      {L"NVIDIA ShadowPlay", L"NvOverlay\\WinForm\\NVIDIA ShadowPlay.exe"},
      {L"nvsphelper64", L"ShadowPlay\\nvsphelper64.exe"},
      {L"nvsphelper64", L"NVIDIA Container\\nvsphelper64.exe"},
      {L"NvContainer", L"NVIDIA Container\\NvContainer.exe"},
      {L"NvBackend", L"NvBackend\\NvBackend.exe"},
      {L"NVIDIA Backend", L"NvBackend\\NVIDIA Backend.exe"},
      {L"NVIDIA Backend", L"NVIDIA Backend.exe"},
      {L"NvCapture", L"NVIDIA Container\\CaptureEngine\\NvCapture.exe"},
      {L"NVIDIA Web Helper", L"Overlay On-Screen Control\\NVIDIA NodeAPI\\NVIDIA Web Helper.exe"},
      {L"NVIDIA OSC", L"Overlay On-Screen Control\\NVIDIA OSC\\NVIDIA OSC.exe"},
  };
  for (size_t i = 0; i < sizeof(owned) / sizeof(owned[0]); ++i) {
    launcherutil::KillProcessFromPath(
        owned[i].name, RootP(L"", owned[i].path));
  }
  LogLine("installer exit complete");
}
