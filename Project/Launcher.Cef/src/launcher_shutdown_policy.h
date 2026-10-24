// Launcher EXIT ALL: portable, mock-testable lifecycle and exact-path manifest.
// No Win32 calls here: isolated tests can exercise this contract without
// touching the running ShadowPlay NEXT or genuine NVIDIA services.
#ifndef LAUNCHER_SHUTDOWN_POLICY_H_
#define LAUNCHER_SHUTDOWN_POLICY_H_

#include <cstddef>
#include <string>

namespace launcher_shutdown {

struct OwnedExecutable {
  const wchar_t* image_name;     // process basename, without .exe
  const wchar_t* relative_path;  // exact deployed path beneath product root
};

inline const OwnedExecutable* OwnedExecutables(std::size_t* count) {
  // Supervisor FIRST: prevents NvContainer from re-spawning its children.
  // Legacy slots are explicit exact-path compatibility entries, not wildcards.
  static const OwnedExecutable kOwned[] = {
      {L"NvContainer", L"NVIDIA Container\\NvContainer.exe"},
      {L"NvCapture", L"NVIDIA Capture\\NvCapture.exe"},
      {L"NvGraphics", L"NVIDIA Graphics\\NvGraphics.exe"},
      {L"NVIDIA OSC", L"Overlay On-Screen Control\\NVIDIA OSC\\NVIDIA OSC.exe"},
      {L"NVIDIA Web Helper", L"Overlay On-Screen Control\\NVIDIA NodeAPI\\NVIDIA Web Helper.exe"},
      {L"NvNode", L"Overlay On-Screen Control\\NVIDIA NodeAPI\\NvNode.exe"},
      {L"nvsphelper", L"NVIDIA Container\\nvsphelper.exe"},
      {L"NvBackend", L"NvBackend\\NvBackend.exe"},
      {L"NVIDIA Backend", L"NvBackend\\NVIDIA Backend.exe"},
      {L"NVIDIA Backend", L"NVIDIA Backend.exe"},
      {L"NVIDIA Notifier", L"NvOverlay\\WinForm\\NVIDIA Notifier.exe"},
      {L"NVIDIA ShadowPlay", L"NvOverlay\\WinForm\\NVIDIA ShadowPlay.exe"},
      {L"nvsphelper64", L"ShadowPlay\\nvsphelper64.exe"},
      {L"nvsphelper64", L"NVIDIA Container\\nvsphelper64.exe"},
      // Historical capture slot: safe only when it matches this exact root path.
      {L"NvCapture", L"NVIDIA Container\\CaptureEngine\\NvCapture.exe"},
  };
  *count = sizeof(kOwned) / sizeof(kOwned[0]);
  return kOwned;
}

struct ShutdownSummary {
  unsigned matched = 0;
  unsigned already_exited = 0;
  unsigned graceful = 0;
  unsigned forced = 0;
  unsigned failed = 0;
};

// Backend owns opaque process handles. OpenCandidates must filter by exact
// process image NAME; MatchesImagePath rechecks FULL executable path on the
// opened handle. WaitExited is bounded; ForceTerminate is last resort.
// Close must run for every opened handle, even on a path mismatch.
template <typename Backend>
ShutdownSummary StopExactPath(Backend& backend, const wchar_t* image_name,
                              const std::wstring& expected_full_path,
                              unsigned grace_ms = 1500,
                              unsigned force_wait_ms = 1500) {
  ShutdownSummary summary;
  auto candidates = backend.OpenCandidates(image_name);
  for (auto process : candidates) {
    if (!backend.MatchesImagePath(process, expected_full_path)) {
      backend.Close(process);
      continue;
    }
    ++summary.matched;
    if (backend.WaitExited(process, 0)) {
      ++summary.already_exited;
    } else {
      // For GUI services this posts WM_CLOSE to owned windows. Headless
      // services have no OS-level polite exit signal here; we still allow a
      // bounded chance for them to exit on their own before escalation.
      backend.RequestGracefulExit(process);
      if (backend.WaitExited(process, grace_ms)) {
        ++summary.graceful;
      } else if (backend.ForceTerminate(process) &&
                 backend.WaitExited(process, force_wait_ms)) {
        ++summary.forced;
      } else {
        ++summary.failed;
      }
    }
    backend.Close(process);
  }
  return summary;
}

}  // namespace launcher_shutdown

#endif  // LAUNCHER_SHUTDOWN_POLICY_H_
