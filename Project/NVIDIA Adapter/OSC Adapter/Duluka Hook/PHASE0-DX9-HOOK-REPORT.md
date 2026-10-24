# ShadowPlay NEXT — DX9/DX9Ex Hook Phase 0 Report
2026-10-23. Worktree: `C:\My Project\NVIDIA-Shadowplay`, branch `Stable`.

## Ownership and compatibility
- OSC Host owns Auto/CPU/GPU selection, adapter choice, cross-GPU transfer,
  the producer frame/fence/metadata protocol. **No Host code or GPU Selection
  was edited in this Hook work.**
- Hook reports the actual renderer's available GPU identity and consumes
  a frame on the game's accessible adapter. For D3D11 and D3D9Ex shared GPU
  handles the Host must publish on an adapter the game can access; the
  selected OSC *rendering* GPU may be entirely different.
- **No existing Shared Memory wire layout or field semantics changed.**
  `ConsumerAdapterInfo v2` still writes `renderApi=9` for both D3D9
  and D3D9Ex for backward compatibility. It publishes the game adapter
  LUID and game owner PID. Exact API differentiation is available via
  the new local read-only export below, not yet an independent Host IPC.

## Optional native DX9 status export
```cpp
extern "C" BOOL WINAPI NvOscAdapterGetD3D9AdapterStatus(
    std::uint64_t *adapterLuid,
    BOOL *isD3D9Ex,
    BOOL *gpuFrameBound,
    std::uint64_t *busyFrameSkips);
```
- Read-only, same-process: **all four pointers must be non-null**.
- `adapterLuid=0`: no GPU adapter identity verified yet (before the
  first renderer callback, after Reset, or LUID lookup failure).
- `isD3D9Ex`: based on the game's actual device COM interface;
  does not infer it from a process/module name.
- `gpuFrameBound`: current *optional* legacy-shared BGRA8 frame alias
  is active; false on Unbind, device Reset or failed GPU replacement.
- `busyFrameSkips`: number of OSC callbacks skipped because render
  mutex was busy. The original game's Present still gets forwarded.
- D3D9Ex uses native `IDirect3D9Ex::GetAdapterLUID`. For regular D3D9
  only the display monitor -> DXGI adapter mapping is available;
  this is **best-effort** on heterogeneous/hybrid-GPU machines, not
  a guarantee of exact physical GPU for every D3D9 driver.
- Values are cleared/re-evaluated on device Reset or device change.
  This is not a remote Host query by itself: a trusted connector must
  still bridge the exported information into the Host's control plane.

## Present behavior when the Host is busy
- `OnPresent` and the ReShade presentation callback use a `try_lock`
  instead of waiting for the resource/renderer mutex.
- If the Host is changing a GPU Resource, Hook skips OSC composition
  for that frame; original game Present is **always forwarded**.
- CPU v1 producer has a seqlock; odd/inconsistent sequence means the
  frame is not ready. Skip it with zero-time polling and retry next Present.
- At most **one** large CPU frame copy is attempted per Present, not
  three expensive retry copies. The publisher process handle is cached
  instead of reopening the process every game frame.
- These choices prevent waiting on the Host's frame producer. They are
  **not a guarantee** of a fixed frame-time budget: actual D3D9 drawing,
  first-time resource allocation and driver calls can still cost time.

## DX9Ex GPU resource switch (opt-in only)
- Exposed receiver API:
  `NvOscAdapterBindD3D9ExGpuFrame(deviceEx, legacyHandle, w, h, frameNumber)`
  and `NvOscAdapterUnbindD3D9ExGpuFrame(deviceEx)`.
- Bind accepts a legacy DXGI BGRA8 shared handle. An incompatible
  adapter/handle/format fails D3D9Ex CreateTexture: GPU alias is released,
  `gpuFrameBound=false`, and the existing CPU Frame Bridge can be used
  if the Host is publishing valid CPU frames.
- Same-resource rebind/generation and deliberately incompatible
  dimension replacement were exercised by tests with correct status
  transitions, GPU alias retirement and subsequent recovery.
- For production cross-process use, explicit producer adapter LUID
  validation, lifetime coordination, resource-generation and GPU
  synchronization/ack protocol still require a **joint Host contract**.
  Do not feed NT-style DX11 handles into D3D9Ex legacy CreateTexture.
- In particular, a GPU texture that is bound is **not proof** that the
  Host finished writing it. No unsynchronized GPU frame should be
  offered to the live Hook.

## Tested
`tests/verify-adapter.ps1` on Win32/x64 includes:
- D3D9 CPU Renderer pixel/alpha/reset/invalid-pitch/hidden-ID and
  incomplete-seqlock skip without waiting.
- D3D9/D3D9Ex Present hook count 4 and cooperative late attach.
- D3D9Ex D3D11<->D3D9Ex BGRA8 GPU legacy resource sharing and
  actual native Hook GPU backbuffer output.
- New status export LUID/API/bound state, rebind/fallback/recovery.
- x64 managed Host -> x86 fixture and D3D11 render regression.

## Still incomplete
No live OSC Host Multi-GPU selection, automatic DX9Ex GPU publisher,
cross-process GPU metadata sync, automatic preexisting-device attach,
safe multi-threaded unload, interactive foreground game pixel test or
real ReShade vendor compatibility is claimed. **Do not deploy to live
CrossIntegrity until Host and Hook are integrated and verified.**

## Phase 0.1 — GPU stage/commit + nonblocking timing diagnostics

Tested two-phase D3D9Ex GPU alias replacement: Stage accepts compatible
Legacy BGRA8 handle without activating it, Commit checks generation
and swaps it atomically under renderer mutex, Discard preserves current
frame; active/pending GPU aliases are cleaned on device Reset and
explicit Unbind. Host must guarantee a completed transfer and keep
producer memory alive; no Host-side publisher/IPC was implemented here.

Removed all synchronous periodic file logging from D3D9 Present and
PresentEx paths. Diagnostics now use
`NvOscAdapterGetD3D9TimingSnapshot` with
`include/d3d9_timing_diagnostics.h`; snapshot is read-only and does
not reset counters. Native Win32/x64 smoke validates version, size,
frequency, counts and invalid output/size rejection.

Win32/x64 GPU tests verify **pixels remain `0x123456` until Commit**
and change to `0x789abc` afterward, including bad-generation and
discard tests. These are isolated fixtures, not a production OSC Host.

### Multiple D3D9 devices and thread ownership

The D3D9 / D3D9Ex Reset hooks now pass the **actual Reset device** to the
renderer. Resetting an unrelated secondary device must not unbind the
primary device's OSC GPU alias or erase its cached LUID. Process-wide
shutdown still intentionally calls the global Reset path.

D3D9 device calls have a separate thread-safety requirement: Stage opens
a texture through a D3D9Ex device. An integrator may perform it on an
alternate worker thread only if device creation flags/thread ownership
and the game renderer are compatible with that usage. Otherwise stage
on the owning device thread at a safe point; **try-lock alone does not
make arbitrary concurrent Direct3D9 device method calls safe**.
No production concurrent-driver stress test has been certified.
