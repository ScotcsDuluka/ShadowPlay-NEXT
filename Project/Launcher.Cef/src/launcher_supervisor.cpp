// launcher_supervisor.cpp - see launcher_supervisor.h.
#include "launcher_supervisor.h"

#include <stdio.h>

#include <windows.h>

#include "launcher_util.h"
#include "launcher_shutdown_policy.h"

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
  // Port-open alone may belong to an unrelated or half-ready local service.
  // The bundled Web Helper must exist and the HTTP static entry must answer.
  return launcherutil::ProcessRunningFromPath(
             kWebHelper, RootP(L"Overlay On-Screen Control\\NVIDIA NodeAPI",
                               L"NVIDIA Web Helper.exe")) &&
         launcherutil::HttpGetLocal(59011, L"/");
}

std::wstring OscExePath() {
  return RootP(L"Overlay On-Screen Control\\NVIDIA OSC", L"NVIDIA OSC.exe");
}

bool OscRunning() {
  // Landing HTTP 200 proves the UI host responds, not that Hook/CEF pixels
  // are actually drawing in a game; keep those readiness concepts separate.
  return launcherutil::ProcessRunningFromPath(kCefOverlay, OscExePath()) &&
         launcherutil::HttpGetLocal(59013, L"/");
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

LauncherModuleState ProcessModule(const wchar_t* name,
                                  const std::wstring& path) {
  LauncherModuleState state(true);
  state.installed = launcherutil::FileExists(path);
  state.running = launcherutil::ProcessRunningFromPath(name, path);
  return state;
}

LauncherModuleState InstalledModule(bool installed) {
  LauncherModuleState state;
  state.installed = installed;
  return state;
}

std::string ModuleJson(const LauncherModuleState& state) {
  return "{\"installed\":" + std::string(state.installed ? "true" : "false") +
         ",\"processTracking\":" +
         std::string(state.process_tracking ? "true" : "false") +
         ",\"running\":" +
         std::string(state.process_tracking && state.running ? "true" : "false") +
         "}";
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
  o += ",\"modules\":{\"Capture\":" + ModuleJson(modules.capture);
  o += ",\"Graphics\":" + ModuleJson(modules.graphics);
  o += ",\"Overlay\":" + ModuleJson(modules.overlay);
  o += ",\"NodeApi\":" + ModuleJson(modules.node_api);
  o += ",\"Container\":" + ModuleJson(modules.container);
  o += ",\"Adapter\":" + ModuleJson(modules.adapter);
  o += ",\"Hook\":" + ModuleJson(modules.hook);
  o += ",\"Ansel\":" + ModuleJson(modules.ansel);
  o += ",\"Helper\":" + ModuleJson(modules.helper);
  o += ",\"Ffmpeg\":" + ModuleJson(modules.ffmpeg);
  o += ",\"Config\":" + ModuleJson(modules.config) + "}";
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

  // Report only exact files/processes owned by this product root. These
  // inventory fields deliberately do not reuse HTTP readiness as process
  // presence, and never inspect configuration contents or load native DLLs.
  st.modules.capture = ProcessModule(
      L"NvCapture", RootP(L"NVIDIA Capture", L"NvCapture.exe"));
  st.modules.graphics = ProcessModule(
      L"NvGraphics", RootP(L"NVIDIA Graphics", L"NvGraphics.exe"));
  st.modules.overlay = ProcessModule(kCefOverlay, OscExePath());
  st.modules.node_api = ProcessModule(
      kWebHelper, RootP(L"Overlay On-Screen Control\\NVIDIA NodeAPI",
                       L"NVIDIA Web Helper.exe"));
  st.modules.container.installed = launcherutil::FileExists(
      RootP(L"NVIDIA Container", L"NvContainer.exe"));
  st.modules.container.running = st.container;
  st.modules.adapter = ProcessModule(
      L"NVIDIA OSC Adapter",
      RootP(L"NVIDIA Adapter", L"NVIDIA OSC Adapter.exe"));
  const LauncherModuleState adapter_x86 = ProcessModule(
      L"NVIDIA OSC Adapter",
      RootP(L"NVIDIA Adapter\\x86", L"NVIDIA OSC Adapter.exe"));
  st.modules.adapter.installed =
      st.modules.adapter.installed || adapter_x86.installed;
  st.modules.adapter.running =
      st.modules.adapter.running || adapter_x86.running;
  st.modules.hook = InstalledModule(launcherutil::FileExists(
      RootP(L"NVIDIA Adapter", L"NVIDIA OSC Adapter Hook.dll")) ||
      launcherutil::FileExists(
          RootP(L"NVIDIA Adapter\\x86", L"NVIDIA OSC Adapter Hook.dll")));
  st.modules.ansel = InstalledModule(launcherutil::FileExists(
      RootP(L"NVIDIA Adapter", L"NVIDIA Ansel Adapter.dll")));
  st.modules.helper = ProcessModule(
      L"nvsphelper", RootP(L"NVIDIA Container", L"nvsphelper.exe"));
  st.modules.ffmpeg = InstalledModule(launcherutil::FileExists(
      RootP(L"NVIDIA Capture\\FFmpeg", L"ffmpeg.exe")) &&
      launcherutil::FileExists(
          RootP(L"NVIDIA Capture\\FFmpeg", L"ffprobe.exe")));
  st.modules.config = InstalledModule(launcherutil::FileExists(
      RootP(L"NVIDIA Config", L"config.json")));
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
  const auto should_run_cef = []() {
    return launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false) &&
           launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false);
  };
  // A background chain can outlive a power/mode toggle. Abort stale starts.
  if (!should_run_cef() || stop_) return;
  LogLine("engine overlay chain: begin");
  // NvContainer is the sole owner of the Node API helper and CEF host.
  StartIfMissing(kContainer, RootP(L"NVIDIA Container", L"NvContainer.exe"));
  if (!launcherutil::WaitForTcpPort(59011, 15000)) {
    LogLine("NvContainer API backend did not become ready on port 59011");
    return;
  }
  if (!should_run_cef() || stop_) return;
  if (!launcherutil::WaitForTcpPort(59013, 15000)) {
    LogLine("NvContainer CEF host did not become ready on port 59013");
    return;
  }
  if (!should_run_cef() || stop_) return;
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
  // Ignore a queued hide from a previous OFF/WinForm selection if the
  // user has since switched back to powered-on CEF.
  if (launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false) &&
      launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false))
    return;
  // NvContainer owns the OSC process; hide it rather than killing a child
  // that the container watchdog would immediately restart.
  if (!launcherutil::HttpPostLocal(59013, L"/hide")) {
    LogLine("CEF host hide request failed on port 59013");
  }
}

bool LauncherSupervisor::SendOpenOverlay() {
  // Power OFF always wins: never wake an old resident CEF host or WinForm hub.
  if (!launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false) ||
      stop_) {
    LogLine("OPEN OVERLAY rejected: system disabled or stopping");
    return false;
  }
  if (launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false)) {
    // CEF lane: ask the NvContainer-owned OSC host to show, not toggle.
    if (!launcherutil::IsTcpPortOpen(59011) ||
        !launcherutil::IsTcpPortOpen(59013)) {
      LogLine("OPEN OVERLAY (CEF): chain down - starting");
      StartEngineOverlayChain();
    }
    if (!launcherutil::WaitForTcpPort(59013, 500)) return false;
    // A concurrent power-off or mode switch may have happened during the
    // backend wait: do not let this old request reopen a closed overlay.
    if (stop_ ||
        !launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false) ||
        !launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false))
      return false;
    return launcherutil::HttpPostLocal(59013, L"/show");
  }
  if (stop_ || !launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false) ||
      launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false))
    return false;
  // WINFORM lane: TcpClientHelper.Send("open_overlay") wire parity:
  //   "[Send] NVIDIA  APP|open_overlay\r\n" to 127.0.0.1:5001.
  return launcherutil::SendHubLine("[Send] NVIDIA  APP|open_overlay");
}

void LauncherSupervisor::InstallerExit() {
  // A failed config write must not trigger a kill pass: the supervisor would
  // see power still enabled and could respawn the child service family.
  if (!launcherutil::WriteConfigBool(L"Overlay", L"UseOverlayEnabled", false)) {
    LogLine("installer exit aborted: could not persist overlay disabled");
    return;
  }
  std::size_t count = 0;
  const auto* owned = launcher_shutdown::OwnedExecutables(&count);
  for (std::size_t i = 0; i < count; ++i) {
    launcherutil::StopOwnedProcessFromPath(
        owned[i].image_name, RootP(L"", owned[i].relative_path));
  }
  LogLine("installer exit finished: exact-path graceful/forced stop pass");
}
