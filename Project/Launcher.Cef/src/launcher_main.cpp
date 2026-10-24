// launcher_main.cpp - DLL entry surface for NVIDIA Launcher\Cef\Launcher.dll.
// Exports NvLauncherCefMain: the full CEF process split (subprocess
// relaunch -> CefExecuteProcess; browser process -> supervisor chain +
// loopback UI server + CefInitialize + message loop). The ROOT
// Launcher.exe (thin bootstrap) loads this DLL and calls the export.
// Layout contract: Launcher.dll and its UI sit in NVIDIA Launcher\Cef. The
// Launcher loads CEF binaries/resources from the shared runtime beside
// NVIDIA OSC.exe; Launcher cache and user data remain in its own slot.
// winsock2.h MUST precede any windows.h include (cef headers pull it in).
#include <winsock2.h>
#include <ws2tcpip.h>

#include <stdio.h>

#include <string>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_task.h"

#include <memory>

#include "launcher_app.h"
#include "launcher_client.h"
#include "launcher_http_server.h"
#include "launcher_json.h"
#include "launcher_main.h"
#include "launcher_supervisor.h"
#include "launcher_util.h"

namespace {

class ExitTask : public CefTask {
 public:
  void Execute() override {
    launcherutil::LogLine("self-exit watchdog fired");
    if (LauncherClient::active_client_) {
      LauncherClient::active_client_->RequestClose();
    }
  }
 private:
  IMPLEMENT_REFCOUNTING(ExitTask);
};

LauncherLaunchParams* Params() {
  static LauncherLaunchParams* p = new LauncherLaunchParams();
  return p;
}

unsigned ParseUnsigned(const std::string& s, unsigned def) {
  if (s.empty()) return def;
  unsigned v = def;
  if (sscanf(s.c_str(), "%u", &v) == 1) return v;
  return def;
}

int ParseInt(const std::string& s, int def) {
  if (s.empty()) return def;
  int v = def;
  if (sscanf(s.c_str(), "%d", &v) == 1) return v;
  return def;
}

// Resolves the launcher UI bundle root: --ui-root=... >
// <root>\NVIDIA Launcher\Cef\Resources\launcher (production) > <exeDir>\ui
// (dev run from the project bin). Returns "" when none exists (logged).
std::string ResolveUiRoot(CefRefPtr<CefCommandLine> cl) {
  std::vector<std::wstring> candidates;
  if (cl->HasSwitch("ui-root")) {
    candidates.push_back(
        launcherutil::Utf8ToWide(cl->GetSwitchValue("ui-root").ToString()));
  }
  candidates.push_back(launcherutil::JoinPath(
      launcherutil::GetRootDir(), L"NVIDIA Launcher\\Cef\\Resources\\launcher"));
  candidates.push_back(launcherutil::JoinPath(launcherutil::GetExeDir(),
                                              L"ui"));
  for (size_t i = 0; i < candidates.size(); ++i) {
    DWORD attr = GetFileAttributesW(candidates[i].c_str());
    if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) {
      return launcherutil::WideToUtf8(candidates[i]);
    }
  }
  return "";
}

}  // namespace

LauncherLaunchParams* GetLaunchParams() { return Params(); }

extern "C" __declspec(dllexport)
int NvLauncherCefMain(void) {
  // CEF requires CefExecuteProcess before any other CEF call.
  launcherutil::LogInit();
  launcherutil::LogLine("NvLauncherCefMain entered (pid " +
                        std::to_string(GetCurrentProcessId()) + ")");

  CefMainArgs args(GetModuleHandleW(NULL));
  CefRefPtr<LauncherApp> app(new LauncherApp());

  int exit_code = CefExecuteProcess(args, app.get(), NULL);
  if (exit_code >= 0) {
    // Subprocess relaunch (renderer/gpu/utility) handled and finished.
    launcherutil::LogLine("subprocess exit code " + std::to_string(exit_code));
    return exit_code;
  }
  launcherutil::LogLine("browser process path");

  // ── browser process only from here on ────────────────────────────────
  CefRefPtr<CefCommandLine> cl = CefCommandLine::CreateCommandLine();
  cl->InitFromString(GetCommandLineW());

  LauncherLaunchParams* params = Params();
  params->show_window = !cl->HasSwitch("hidden");
  params->supervise = !cl->HasSwitch("no-supervise");
  params->self_exit_ms =
      ParseUnsigned(cl->GetSwitchValue("self-exit-ms").ToString(), 0);
  params->width = ParseInt(cl->GetSwitchValue("width").ToString(), 1152);
  params->height = ParseInt(cl->GetSwitchValue("height").ToString(), 648);

  // ── single-instance guard (browser process) ──────────────────────────
  // Subprocess relaunches exit earlier inside CefExecuteProcess. Keep the
  // name independent of the install path; backslashes are invalid in a
  // Windows kernel-object name after the Local namespace prefix.
  static HANDLE s_instance_mutex = NULL;
  s_instance_mutex = CreateMutexW(NULL, TRUE, L"Local\\NVIDIA.ShadowPlay.Launcher");
  if (!s_instance_mutex) {
    launcherutil::LogLine("single-instance mutex creation failed (error " +
                          std::to_string(GetLastError()) + ")");
    return 4;
  }
  if (GetLastError() == ERROR_ALREADY_EXISTS) {
    launcherutil::LogLine("second launcher process - exiting");
    CloseHandle(s_instance_mutex);
    s_instance_mutex = NULL;
    return 0;
  }

  // UI bundle + loopback server (page loads over localhost HTTP).
  std::string ui_root = ResolveUiRoot(cl);
  static LauncherHttpServer server;
  if (ui_root.empty()) {
    launcherutil::LogLine("launcher ui root missing (tried --ui-root, "
                          "NVIDIA Launcher\\Cef\\Resources\\launcher, exeDir\\ui)");
    return 2;
  }
  std::string err;
  if (!server.Start(ui_root, &err)) {
    launcherutil::LogLine("ui http server FAILED: " + err);
    return 2;
  }
  launcherutil::LogLine("http://127.0.0.1:" + std::to_string(server.port()) +
                        "/ serving " + ui_root);

  params->trusted_url =
      "http://127.0.0.1:" + std::to_string(server.port()) + "/index.html";
  if (cl->HasSwitch("url")) {
    params->url = cl->GetSwitchValue("url").ToString();
  } else {
    params->url = params->trusted_url;
  }
  launcherutil::LogLine("navigating to " + params->url);

  // Supervisor start happens AFTER CefInitialize (below): the poll
  // thread's first push posts into the CEF UI task runner, which must
  // already exist.

  CefSettings settings;
  settings.size = sizeof(CefSettings);
  settings.no_sandbox = 1;  // sandbox not linked (no cef_sandbox.lib)
  settings.multi_threaded_message_loop = false;
  settings.windowless_rendering_enabled = false;
  // Dark page background — never the white flash.
  settings.background_color =
      CefColorSetARGB(0xFF, 0x16, 0x17, 0x19);
  settings.log_severity = LOGSEVERITY_INFO;

  const std::wstring exe_dir = launcherutil::GetExeDir();
  const std::wstring root = launcherutil::GetRootDir();
  const std::wstring launcher_dir =
      launcherutil::JoinPath(root, L"NVIDIA Launcher\\Cef");
  const std::wstring cef_runtime_dir =
      launcherutil::JoinPath(root, L"Overlay On-Screen Control\\NVIDIA OSC");

  // Subprocess: this same exe (root bootstrap). It re-enters
  // NvLauncherCefMain, loads Launcher.dll by ABSOLUTE path and hands the
  // --type= relaunch to CefExecuteProcess.
  CefString(&settings.browser_subprocess_path).FromString(
      launcherutil::WideToUtf8(launcherutil::JoinPath(exe_dir,
                                                      L"Launcher.exe")));
  // Both CEF hosts use the one runtime staged beside NVIDIA OSC.exe.
  CefString(&settings.resources_dir_path).FromString(
      launcherutil::WideToUtf8(cef_runtime_dir));
  CefString(&settings.locales_dir_path).FromString(
      launcherutil::WideToUtf8(
          launcherutil::JoinPath(cef_runtime_dir, L"locales")));
  CefString(&settings.cache_path).FromString(
      launcherutil::WideToUtf8(launcherutil::JoinPath(
          launcher_dir, L"Data\\launcher-cef-cache")));
  CefString(&settings.user_data_path).FromString(
      launcherutil::WideToUtf8(launcherutil::JoinPath(
          launcher_dir, L"Data\\launcher-cef-user-data")));
  launcherutil::LogLine("CEF runtime dir " +
                        launcherutil::WidePathUtf8(cef_runtime_dir));
  CefString(&settings.log_file).FromString(
      launcherutil::WideToUtf8(launcherutil::JoinPath(
          root, L"Logs\\launcher-cef-debug.log")));

  if (!CefInitialize(args, settings, app.get(), NULL)) {
    launcherutil::LogLine("CefInitialize returned false");
    LauncherSupervisor::Get()->Stop();
    return 3;
  }
  launcherutil::LogLine("CefInitialize ok (window " +
                        std::to_string(params->width) + "x" +
                        std::to_string(params->height) + ")");

  // Supervisor: base chain (NvContainer + NVIDIA Backend) + poll thread.
  // The ENGINE OVERLAY chain starts here when config says it was left ON
  // (Main.vb contract) — background thread, never blocking startup.
  LauncherSupervisor* supervisor = LauncherSupervisor::Get();
  supervisor->Start(params->supervise, &LauncherPushStateAnyThread);

  if (params->self_exit_ms > 0) {
    // Smoke-run watchdog: always terminates, always leaves a log line.
    CefPostDelayedTask(TID_UI, new ExitTask(),
                       static_cast<int64>(params->self_exit_ms));
  }

  CefRunMessageLoop();
  supervisor->Stop();
  CefShutdown();
  server.Stop();

  launcherutil::LogLine("host exit");
  return 0;
}
