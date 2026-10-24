// launcher_supervisor.h - the C++ port of the Launcher's Main.vb
// supervision contract:
//   - base chain ALWAYS: NvContainer (engine lane) + NVIDIA Backend.exe
//     (TCP hub) started if missing;
//   - ENGINE OVERLAY chain (config Overlay.EngineOverlayMode=true):
//     NvContainer owns the Node API on :59011 and NVIDIA OSC on :59013;
//   - 1s status poll (Overlay API / Notifier API / NVIDIA API dots +
//     Flags\Ready) pushed to the page;
//   - config toggles Overlay.UseOverlayEnabled / Overlay.EngineOverlayMode
//     (single source of truth: config.json — user actions only);
//   - open_overlay via the hub wire frame;
//   - installer exit: UseOverlayEnabled=false + kill the family.
#ifndef LAUNCHER_SUPERVISOR_H_
#define LAUNCHER_SUPERVISOR_H_

#include <windows.h>

#include <string>

#include "launcher_json.h"

// Read-only package/process inventory. Process presence does not establish
// recording readiness, game attachment, or rendered Hook output.
struct LauncherModuleState {
  explicit LauncherModuleState(bool track_process = false)
      : process_tracking(track_process) {}

  bool installed = false;
  bool process_tracking = false;
  bool running = false;
};

struct LauncherModuleStates {
  LauncherModuleState capture{true};
  LauncherModuleState graphics{true};
  LauncherModuleState overlay{true};
  LauncherModuleState node_api{true};
  LauncherModuleState container{true};
  LauncherModuleState adapter{true};
  LauncherModuleState hook;
  LauncherModuleState ansel;
  LauncherModuleState helper{true};
  LauncherModuleState ffmpeg;
  LauncherModuleState config;
};

struct LauncherState {
  bool nv_api = false;        // "NVIDIA Backend" (TCP hub) running
  bool shadowplay = false;    // "NVIDIA ShadowPlay" running
  bool overlay_ready = false; // Flags\Ready marker exists
  bool notifier = false;      // "NVIDIA Notifier" running
  bool container = false;     // "NvContainer" running
  bool web_helper = false;    // "NVIDIA Web Helper" running
  bool cef_overlay = false;   // "NVIDIA Share" running (any lane)
  bool overlay_enabled = false;  // config Overlay.UseOverlayEnabled
  bool engine_overlay = false;   // config Overlay.EngineOverlayMode
  LauncherModuleStates modules;

  std::string ToJson() const;
};

class LauncherSupervisor {
 public:
  // Push callback invoked on the poll thread with the serialized state.
  typedef void (*PushFn)(const std::string& json);

  static LauncherSupervisor* Get();

  // Start the always-on base chain (NvContainer + API hub) and the poll
  // thread. With supervise=false (smoke/proof runs) nothing is started and
  // the poll still reports real process states.
  void Start(bool supervise, PushFn push);
  void Stop();

  // Actions (page commands). All idempotent.
  void StartBaseChain();
  void StartEngineOverlayChainAsync();  // prepare services; never shows overlay
  void StopCefOverlay();  // hide the NvContainer-owned OSC presenter
  bool SendOpenOverlay();
  void InstallerExit();  // config reset + kill family (Main.vb RadioButton2)

  LauncherState Snapshot();

 private:
  LauncherSupervisor() = default;

  static unsigned long __stdcall PollTramp(void* self);
  static unsigned long __stdcall ChainTramp(void* self);
  void PollLoop();
  void StartEngineOverlayChain();

  volatile long stop_ = 0;
  HANDLE poll_thread_ = NULL;
  HANDLE chain_thread_ = NULL;
  PushFn push_ = NULL;
  bool supervise_ = false;
  std::string last_pushed_;
};

#endif  // LAUNCHER_SUPERVISOR_H_
