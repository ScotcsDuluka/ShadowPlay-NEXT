# Overlay and Hook stability baseline

This document separates verified behavior from gaps that must be measured
before expanding Hook or Overlay use. It is a baseline, not a claim that the
system has passed a stability qualification.

## Scope

- **Overlay/OSC:** OSC and CEF desktop presentation, frame publication, open and
  close behavior, and recovery after the publisher restarts.
- **In-game Hook:** x64 D3D11/DXGI composition, shared-texture lifecycle,
  focus transitions, ReShade ordering, input forwarding, and game impact.
- **Not in scope yet:** DX9, DX12, OpenGL, Vulkan, protected/anti-cheat games,
  and enabling the adapter by default.

## Existing evidence

- `NvContainer` exposes loopback-only status endpoints:
  `/v1/ontology/status`, `/v1/game-watcher/status`, and
  `/v1/osc-adapter/status`.
- `oscAdapter.enabled` is `false` in `NvConfig/nvcontainer.json`.
- The adapter README records a synthetic D3D11 smoke test that checks pixels
  after composition, shared-handle opening, keyed-mutex synchronization, and
  release after the publisher exits.
- The adapter README also records one successful live D3D11/ReShade game
  composition. It explicitly does not establish soak stability, reliable live
  focus switching, full input behavior, resizing, or sustained performance.
- Hook diagnostics are written to
  `%LOCALAPPDATA%/NVIDIA/OSC Adapter/Logs/adapter-hook-<pid>.log`. A
  `Present diagnostics` entry confirms the callback; host logs are needed to
  confirm DLL loading.
- No measured desktop Overlay/OSC soak or repeated open/close result was found
  in the inspected documentation. Treat that behavior as unqualified.
- The user reports that Overlay/OSC has sometimes become stuck after being
  left open and unused for an extended period. The duration, exact stuck
  behavior, and recovery method have not yet been recorded, so this is a
  reported symptom to reproduce rather than a diagnosed cause.
- Hook detach is not safely implemented. NvContainer intentionally retains a
  successfully attached adapter host while its game is still alive; forced
  termination could leave the game executing unloaded or partially torn-down
  hook code.
- The in-game renderer supports D3D11/DXGI only. DX9, DX12, OpenGL, and Vulkan
  have no implemented or qualified path.
- One live ReShade-enabled D3D11 composition was recorded, but it does not
  qualify focus transitions, cursor/keyboard forwarding, resizing, sustained
  runtime, or game performance.
- Cross-adapter composition uses a CPU-visible staging/readback path and
  drops frames rather than waiting on a busy shared-texture mutex. Its
  sustained frame-time/FPS cost has not been measured; same-adapter operation
  avoids the readback.
- A startup shared-handle failure against a previous publisher was observed
  and recovered after restarting the publisher. Recovery and stale-frame
  behavior need repeatable lifecycle coverage.

## Initial test run

On 2026-10-15, the existing x64 Release synthetic D3D11 adapter smoke test
was run three consecutive times using the checked-in Release test executable
and Hook DLL. All three runs exited with code 0 and reported:

- Hidden frames were skipped and composition resumed when a visible frame
  returned.
- The adapter released its source texture after the synthetic publisher exited.
- The Hook intercepted 7 Presents and composited 6 shared-texture frames.

This is a passing synthetic smoke baseline only. It is not a desktop OSC idle
soak, a live-game test, or evidence about focus/input, long-term stability, or
cross-adapter performance.

## Known gaps and safe boundaries

- Reproduce the reported desktop idle hang and capture its exact visible
  symptom, idle duration, OSC/CEF process state, logs, and recovery action.
  Do not infer a root cause until a run captures evidence.
- Qualify desktop OSC independently from the in-game Hook: repeated
  open/close, an open-but-unused soak, and recovery when interaction resumes.
- Run the existing synthetic D3D11 smoke test before any live Hook trial. It
  checks final pixels and bridge/resource behavior, but does not validate
  desktop idle stability or game input/focus behavior.
- Only test live Hook behavior in a controlled, non-protected D3D11 app with
  `oscAdapter.enabled` left false globally. Do not test protected/anti-cheat
  processes, force-detach from a live game, or claim support for unimplemented
  graphics APIs.
- Collect Hook-off and Hook-on performance under the same repeatable scene
  before defining an acceptable regression threshold.

## Measurements to collect

For every run, record the build revision, Windows/GPU/driver, game and graphics
API, whether ReShade is present, OSC/game adapter LUIDs, test duration, and
whether the run is synthetic or live. Keep desktop OSC results separate from
in-game Hook results.

| Area | Record | Baseline pass condition |
|---|---|---|
| Desktop Overlay/OSC | Open/close count, idle duration, window responsiveness, CEF frame/render errors, process exits, stale or black frames, and recovery after interaction resumes | 100 open/close cycles plus a separate 2-hour open-but-unused soak; no crash, unresponsive window, stuck process, or unrecovered stale/black frame |
| Synthetic Hook | Smoke-test exit/result, pixel assertions, bridge owner/frame changes, resource release | Existing pixel/bridge smoke test passes repeatedly; no hook or publisher remains after the test |
| Live Hook | Present/draw counters, HRESULT/stage, focus transitions, visible-frame recovery, input behavior | 1-hour run and 100 focus transitions with no game crash/hang, stale overlay after focus return, unintended gameplay input capture, or unrecovered composition failure |
| Cross-adapter performance | OSC/game adapter LUIDs, frame time/FPS with Hook off/on, frame drops, and recovery after consumer contention | Record repeatable same-adapter and cross-adapter runs; do not set a pass budget until Hook-off baseline is stable |
| Focus and input | Focus transitions, frame visibility/recovery, mouse/cursor forwarding, keyboard capture/release, and gameplay input | 100 transitions without stale overlay after focus return or unintended gameplay input capture |
| Resize and publisher lifecycle | Swap-chain/bridge dimensions, OSC restart/exit, handle-open HRESULT/stage, stale-frame duration, and resource release | Repeated resize and publisher restart/exit with no unrecovered stale frame or retained resources |
| Lifecycle | NvContainer/host/game PIDs and exit behavior | No orphan host after game exit; do not force-detach from a live game because safe DLL detach is not implemented |

Any failed run should retain the relevant NvContainer, adapter-host, and
per-game Hook logs with a timestamp and test conditions. Do not interpret a
successful hook load or draw counter alone as proof that pixels were composed.

## Gate before expanding use

Keep the adapter opt-in and test only controlled, non-protected D3D11
applications. First qualify desktop OSC separately; then qualify synthetic
Hook behavior; then repeat the live Hook lifecycle and performance checks on a
small allowlist of games. Do not enable unsupported graphics APIs or make the
Hook default-on until their own compatibility and lifecycle evidence exists.
