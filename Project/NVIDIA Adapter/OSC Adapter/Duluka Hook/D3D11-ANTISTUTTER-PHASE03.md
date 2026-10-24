# D3D11 Anti-Stutter Phase 0.3 — GLM Live-Test Follow-up
Date: 2026-10-24. Branch: Stable. **Isolated Build only; no live deployment.**

## Source of diagnosis
GLM report from SMASH LEGENDS 2026-10-24 07:49–07:54:
D3D11 ~72 FPS, OSC main menu open, severe in-game stutter.
Host GPU_SHARED publication ~72/s (avg ~150–240 us, 100% present OK)
but game-side overlay Refresh/AcquireSync/CopyResource occurs every new
Host frame; old RTV creation and log writes reachable from callbacks.
This task changes **D3D11 Hook only**, not DX9Ex transport, Host GPU
Selection, NVIDIA Ansel Filter, frame wire layouts or live deployment.

## What changed
- `src/Renderer/D3D11/D3D11Renderer.cpp`: 
  Set **both** relay Refresh and DXGI keyed-mutex AcquireSync timeout to
  0 ms, including initial-frame miss; return/skip instead of waiting.
  Copies continue only when `frameNumber` differs. Host's separate
  publish-when-dirty change is essential for a static menu to stop
  changing that number.
- D3D11 Renderer: use `OMGetRenderTargets` to obtain an already-bound
  RTV *only if* its resource matches the actual swapchain backbuffer.
  Otherwise use `CreateRenderTargetView` and Release after drawing.
  Counters report RTV reused/created. **Important: this is NOT a
  persistent RTV cache.** Holding a RTV reference across Present while
  leaving ResizeBuffers unhooked would block DXGI ResizeBuffers;
  per-swapchain permanent caching requires a coordinated resize
  lifecycle + HOOK-CONTRACT and ReShade chain review, not done here.
- `src/Hook/D3D11/D3D11Hook.cpp`: remove first Present and periodic
  every-1200 Present filesystem logs and any `GetStatus` lock
  requested from that periodic callback. During raw Present and
  ReShade render callback, `Log()` returns immediately and increments
  a suppression counter: it performs **zero CreateDirectoryW/CreateFileW
  and zero OutputDebugStringA** within these callback scopes.
- Renderer ReShade callback: removed repeated once-per-second
  `[reshade] frame diagnostic` formatting and logging. Atomic
  `reshadeCallbackCount`, frame copy and draw counters remain
  available read-only.
- Foreground check uses a 50-ms thread-local cache, rather than
  querying the desktop/environment every frame.
- Added `include/d3d11_timing_diagnostics.h` and
  `NvOscAdapterGetD3D11TimingSnapshot(Snapshot*, uint32_t bytes)`.
  Struct version=1, size=160 bytes, all timestamps in raw
  `QueryPerformanceCounter` ticks. Caller uses QPF to convert to
  seconds. Counters: raw Presents / busy and recursive skips,
  ReShade callbacks, texture cache copies, RTV reuses/creates,
  suppressed render-thread logs, QPC total/max/samples for
  Raw overlay callback, original game Present and ReShade callback.
  **Poll on a non-render thread**; GetStatus internally uses a mutex.
- `tests/smoke_test.cpp`: verifies export, invalid buffer rejection,
  size/version, original Present and overlay QPC counts; existing
  hide/show/ResizeBuffers regression retained. The original DXGI
  Present vtable slot ownership is unchanged and ResizeBuffers is
  still pass-through.

## Confirmed / NOT confirmed

Local isolated fixture: D3D11 smoke preserves 9 Hooked Presents /
6 composed frames and texture publication/exit/ResizeBuffers if
full verify-adapter.ps1 passes. Test shows nonzero telemetry and
`cacheCopies=2` for fixture updates; the test does not model
sustained menu at 72 FPS.

**Not yet proven:** GLM's >5-minute live SMASH LEGENDS playable test,
any before/after P95/P99 frame-time improvement, driver-level
CreateRenderTargetView cost reduction in real game,
ProcMon confirmation of no render-thread filesystem accesses
(in source renderer Log -> Hook Log guard is designed to prevent them),
and elimination of GPU CopyResource when Host sends unnecessary
frameNumber increments. Host must publish only on changed CEF
pixels (separate Host-owner task).

## Retest / handoff
1. Leave production OSC/game DLL untouched until a planned test.
2. Once Host publish-when-dirty is merged, test game with OSC menu
   static for >5 minutes, collecting timing snapshot once/second on
   another thread (NOT from Present). Save QPF and delta counters.
3. Compare `overlayCacheCopies` delta / `presentCallbacks` delta
   close to 0 during STATIC menus, ReShade QPC max/mean, raw overlay
   callback QPC max/mean and game Present QPC, against baseline.
4. ProcMon: filter game PID, CreateFile/CreateDirectory, and inspect
   call stacks. Correlate with performance traces and GPU captures.
5. If RTV creation remains high in real game (rtvCreated rises at
   present rate), coordinate a ResizeBuffers-aware RTV lifetime
   change with the DXGI/ReShade slot owner; do not simply AddRef a
   cached RTV across Present without ResizeBuffers teardown.

No Git Commit/Push here because other chats share Stable worktree.


## Completed local verification (2026-10-24)

- `tests/verify-adapter.ps1`: **PASS exit code 0** with D3D11
  baseline, DX9 x86/x64, DX9Ex GPU mailbox and ACTUAL Host-source
  GPU-to-GPU x64/x86/cross-bitness and Host recovery scenarios.
- D3D11 smoke baseline: 9 hooked Presents, 6 composed frames,
  `cacheCopies=2`, hides/shows, producer exit and ResizeBuffers PASS.
- Additional D3D11 **RTV-reuse** smoke binds its real BackBuffer RTV
  to the game's immediate context before each Present. Result:
  **rtvCreated=0, rtvReused=6**, with 9 Presents, 6 composed
  frames, frame pixels and ResizeBuffers pass-through all PASS.
  Without an already-bound RTV the Hook will still create/release one
  for that frame. No unsafe reference held across resize.
- Timing snapshot validates null/short-buffer failure and valid
  struct version/size (160B); QPC counters nonzero. ReShade timing
  counters are additionally instrumented but not exercised by the
  non-ReShade smoke fixture.
- No actual live 5-minute SMASH LEGENDS foreground retest or ProcMon
  trace was executed, so no claim of fixed real-game stutter yet.
