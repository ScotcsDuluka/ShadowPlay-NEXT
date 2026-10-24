// launcher_main.h - launch parameters handed over before CefInitialize
// (single-threaded window of time, read afterwards on the UI thread) and
// the exported entry the root bootstrap exe calls.
#ifndef LAUNCHER_MAIN_H_
#define LAUNCHER_MAIN_H_

#include <string>

struct LauncherLaunchParams {
  std::string url;            // page to load (http://127.0.0.1:<port>/index.html)
  std::string trusted_url;    // ONLY Launcher-hosted page may use privileged CEF RPC
  bool show_window = true;    // --hidden suppresses
  bool supervise = true;      // --no-supervise (smoke/proof runs)
  bool gpu_accel = true;      // NVIDIA Share.json nv-gpu-accel parity switch
  unsigned self_exit_ms = 0;  // --self-exit-ms=N watchdog (smoke runs); 0=off
  int width = 1152;           // --width=W (16:9)
  int height = 648;           // --height=H
};

LauncherLaunchParams* GetLaunchParams();

// Exported host entry (bootstrap.cpp of the root Launcher.exe loads
// NvLauncher\Cef\Launcher.dll and calls this).
extern "C" __declspec(dllexport) int NvLauncherCefMain(void);

#endif  // LAUNCHER_MAIN_H_
