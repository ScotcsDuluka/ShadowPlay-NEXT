# Duluka Hook — verified implementation status (2026-10-22)

## What was actually implemented and checked
- Native D3D9/D3D9Ex Hook + CPU-frame Renderer already exist.
- Native DXGI/D3D11 Hook + Shared Texture Renderer already exist.
- ReShade add-on arbitration exists for these paths; it was not revalidated
  against a genuine ReShade installation in this work session.
- The x64 Adapter Host can route supported x86 processes to its x86 companion.
- NEW: `src/Detection/GraphicsApiProbe.cs` observes loaded graphics modules
  (D3D9, D3D10, D3D11, D3D12, Vulkan, OpenGL and DXGI).
  `--probe-only true` does not load the Hook DLL or change the target process.
  Normal launches also log the passive evidence.
  Module presence is not proof of actual game rendering or overlay support.
- NEW: target executable identity validation has a Windows
  QueryFullProcessImageNameW / limited-query-rights fallback.
  Passive probes never automatically request elevation.
- Native project no longer includes the retired `Close Project` ReShade folder.
  It builds against `include/reshade-api-v3` inside active Project.
- NEW: `tests/verify-host-probe.ps1` checks read-only x64/x86 Host probes
  and rejects incorrect target executable paths.
- NEW: `tests/verify-adapter.ps1` runs non-deploying Release builds,
  Host probes and D3D11 test. `-RunDx9WindowTests` is an opt-in
  interactive test of DX9 x86+x64 and cross-bitness attachment.

## Verified test results (2026-10-22)
- Native Hook DLL Release Build: Win32 PASS, x64 PASS.
- Managed Adapter Host Release Build: x86 PASS, x64 PASS, 0 warnings/errors.
- Host probe and wrong executable refusal: x86 PASS, x64 PASS.
  An x86 Host probing x64 modules returns UNKNOWN (Win32 299), not invented evidence.
- D3D11 smoke: PASS. 9 hooked Presents, 6 composed frames.
  Hidden frames suppressed, compositing resumed, source resources released,
  ResizeBuffers remained pass-through and swap chain recovered.
- DX9 x86 smoke was ATTEMPTED but BLOCKED: test window could not become
  foreground in the current desktop automation session. Do not claim PASS.
- Real game compatibility, x86 game integration, ReShade vendor smoke and
  shader output were not verified in this session.

## API capability truth
| API | Native Hook | Pixel/Texture Renderer | Verified this run |
| --- | --- | --- | --- |
| D3D9/D3D9Ex | Implemented | Implemented | Build only; interactive smoke blocked |
| D3D11 (DXGI Present) | Implemented | Implemented | x64 9 Presents / 6 frames |
| D3D10 | NOT implemented | NOT implemented | No |
| D3D12 | NOT implemented | NOT implemented | No |
| Vulkan | NOT implemented | NOT implemented | No |
| OpenGL | NOT implemented | NOT implemented | No |

Do not create empty Renderer/Hook stubs that report success.
Per `HOOK-CONTRACT.md`, the DXGI Present slot is owned by the existing
D3D11 layer, and other Direct3D APIs must use their own real renderer probes.

## Live runtime findings (do not overwrite automatically)
- Live Build `NVIDIA Config/nvcontainer.json`: `oscAdapter.enabled=true`,
  executable `NVIDIA Adapter/CrossIntegrity/NVIDIA OSC Adapter.exe`.
- Project `NVIDIA Config/nvcontainer.json`: `oscAdapter.enabled=false`,
  executable `NVIDIA Adapter/NVIDIA OSC Adapter.exe`.
- `Scripts/build-dev.ps1` currently stages Project JSON into Build;
  a complete build may overwrite the live Adapter enable state or executable.
  Preserve/reconcile the intended runtime configuration before deployment.
- Runtime Control API `GET http://127.0.0.1:59012/v1/osc-adapter/status`
  confirmed enabled, configuredExecutable=CrossIntegrity, activeCount=0
  at inspection time.
- Neither running games nor genuine NVIDIA processes were modified by this work.
- This build was NOT deployed to the live CrossIntegrity directory.

## Remaining engineering work
1. Verify D3D9/9Ex x86/x64 pixel/alpha/Reset with a foreground desktop
   and run ReShade vendor isolated tests.
2. Resolve explicit unload/thread/COM cleanup outside DllMain and define
   a safe detach protocol before shipping to live games.
3. Improve late device/factory attachment and handle Present1/flip-model
   coverage with real D3D11 tests.
4. Implement separate, fully tested D3D10, D3D12 (command queue aware),
   Vulkan and OpenGL renderer paths only when concrete tests are available.
5. Integrate evidence-driven API choice and status reporting with NvContainer,
   and reconcile live vs Project Adapter Config before production deployment.

## Reproduction

```powershell
& "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Adapter\OSC Adapter\Duluka Hook\tests\verify-adapter.ps1"
# When using an interactive Windows desktop with foreground access:
& "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Adapter\OSC Adapter\Duluka Hook\tests\verify-adapter.ps1" -RunDx9WindowTests
```

This status report records verified work, not a claim that every graphics API
is complete or that the adapter is safe to inject into arbitrary games.

## DX9 focused update — 2026-10-23 (Stable)

Only the `C:\My Project\NVIDIA-Shadowplay` Stable worktree was edited.

### DX9 fixes and verified behavior
- D3D9 Shared Memory SnapshotFrame now guards producer-supplied rowPitch
  against the LAST accessed source byte (64-bit multiply/offset), not just
  packed width*height bytes. This prevents an oversized pitch from reading
  beyond the 64 MiB OSC frame mapping.
- D3D9Ex Hook installation now assigns the original PresentEx function
  immediately after a successful PresentEx vtable patch, even if a following
  ResetEx patch fails. Rehooking an already-hooked slot without a known
  original forwarding pointer is rejected.
- New DX9 `--renderer-only` smoke mode: Win32 PASS, x64 PASS.
  Pixel orientation, alpha, regular Reset and ResetEx, malformed rowPitch
  rejection, and recovery after valid metadata. **5 successful draws**.
- New DX9 `--hook-counter-only` smoke mode: Win32 PASS, x64 PASS.
  Native Present+PresentEx callbacks exercised through Reset/ResetEx;
  **4 hooked Presents** per architecture, with invisible frame and 0 draws.
- New `tests/verify-dx9-cross-bitness.ps1`: x64 Adapter Host successfully
  routes to x86 Host and loads the x86 Native Hook in an isolated owned test
  process. **Test exit=0, Host exit=0, hooked Present=4**.
- D3D11 regression after DX9 changes: PASS, 9 Presents and 6 composed
  frames; hide/show, ResizeBuffers and resource release passed.
- `tests/verify-adapter.ps1` includes these checks as the safe default
  (does not modify games or deploy to Build Stable).

### Known gap confirmed by a failing regression
- Dedicated preexisting-device test
  `tests/d3d9_late_attach_test.cpp` creates D3D9 Device BEFORE Hook loads.
  Current output: `FAIL presents=0 expected=3`. This is a real functional
  gap of the factory-import/new-device-vtable approach, not a test-window
  focus problem.
- Our observed native D3D9 device VTables are **per-object heap VTables**.
  A temporary device's Present function code pointer matches the earlier
  device's code, but their VTable addresses differ. The experimental temporary
  VTable patch did not intercept the earlier device and was reverted.
  Do not patch or free temporary device VTables as a workaround.
- A tested and safely removable interception strategy for already-existing
  D3D9/9Ex device methods remains to be designed. Avoid blind inline
  detours or injecting this experimental behavior into production games.
- Combined pixel composition via the real Present hook still requires a
  foreground-capable interactive desktop. Vendor ReShade tests, real game
  compatibility and safe unload outside DllMain are not certified.

### Deployment
- **NOT DEPLOYED** to `Build/NVIDIA ShadowPlay`, CrossIntegrity, or games.
- Live Config and native NVIDIA processes were not modified.
- This status is a DX9 **work-in-progress**, not all-DX9 feature completion.

## DX9 continuation — 2026-10-23 cooperative existing-device attach

This work is in the **main Stable worktree**, NOT in a new worktree,
and was not deployed to the live NVIDIA Adapter CrossIntegrity directory.

### Implemented
- Exported `NvOscAdapterRegisterD3D9Device(IDirect3DDevice9*)` for
  **trusted, cooperating in-process hosts** that already own a valid device.
  It checks D3D9/D3D9Ex and hooks the exact provided device vtable.
  NULL pointers are rejected and duplicate registration is idempotent.
  This is NOT an automatic late-attach facility for arbitrary games.
- Exported `NvOscAdapterUnregisterD3D9Device(IDirect3DDevice9*)` to
  restore only vtable slots owned by this Hook before the host releases
  that device. It guards against stomping another hook's modified slot,
  rejects NULL/double unregister, and removes restored slots from the
  cleanup list. **The owner must ensure no Present/Reset callback is
  active on any other thread** while unregistering. This does not
  implement thread quiescence or safe dynamic DLL unload for arbitrary games.
- Corrected D3D9 Renderer GPU upload caching on hidden frame: when
  `frameNumber == 0`, reset `g_uploadedFrameNumber` so reappearance
  with the SAME frame ID and changed pixels reuploads the new image.

### Verified, isolated D3D9 tests (Win32 and x64)
- 6 real GPU renderer draws per architecture: pixel orientation,
  premultiplied alpha, Reset and ResetEx, oversized source pitch rejection,
  recovery from valid metadata, hidden/reappeared frame with same ID
  but a changed blue pixel (not a stale green texture).
- Native D3D9 + D3D9Ex Present interception: 4 callback counts,
  without a foreground assertion (no renderer claim).
- Device created BEFORE Hook loaded, then registered from owner:
  `--cooperative` D3D9 and `--cooperative-ex` D3D9Ex both PASS,
  4 hooked Present/PresentEx calls including after Reset/ResetEx,
  second registration accepted, unregister succeeds, vtable pointers
  restored, and a post-unregister Present does not invoke the Hook.
- Expected negative test, `d3d9_late_attach_test.exe <dll>` with no
  registration: **FAIL presents=0 expected=4**, both Win32/x64.
  This documents that automatic existing-device discovery remains absent.
- x64 managed Host -> x86 owned test process: PASS, Present 4,
  both Host and test exit 0.
- Entire `tests/verify-adapter.ps1`: PASS, exit 0, including D3D11
  regression (9 Presents / 6 composed shared-texture frames).

### Not yet production-complete
- **Automatic** attachment to a device created before DLL load
  needs its own safely reversible interception design. Never patch a
  temporary D3D9 device vtable and pretend it covers existing devices.
- Cooperative registration requires **same-process integration** by
  device-owning code. The current x64 Host cannot discover a remote
  COM device pointer or invoke this interface by itself.
- Safe teardown across game render threads / ReShade, including
  loader-lock DllMain cleanup, is **not certified**.
- Original combined pixel composition via Present in a foreground
  window, real vendor ReShade, and actual games remain unverified.
- Do not deploy the cooperative patch to live CrossIntegrity until
  compatibility, ownership and detach paths have been reviewed.

## DX9Ex optional GPU delivery — verified 2026-10-23

Owned scope: Duluka Hook renderer and native fixtures, not OSC Host publisher.

- Added optional D3D9Ex GPU texture alias to the D3D9 Renderer:
  `NvOscAdapterBindD3D9ExGpuFrame(IDirect3DDevice9Ex*,HANDLE,
  uint32_t,uint32_t,uint64_t)` and
  `NvOscAdapterUnbindD3D9ExGpuFrame(IDirect3DDevice9Ex*)`.
  The renderer uses the bound legacy-shared BGRA8 texture directly as
  the overlay's shader input, without CPU-frame memcpy/upload.
  When not bound, on failed opening, and after Reset/unbind, the existing
  CPU Shared Memory frame path remains active (if publisher is present).
  The API is opt-in, callable only from trusted code in the game process.
- Implemented GPU interop feasibility and pixel-output fixture:
  `tests/d3d9ex_gpu_interop_test.cpp` builds x86 and x64.
  D3D9Ex-created BGRA8 ↔ D3D11 GPU shared resource: PASS.
  **D3D11-created** legacy-shared BGRA8 (the Host-like direction)
  opened by D3D9Ex and rendered by the **actual Hook Renderer**:
  PASS, same process, x86 and x64. GPU texture pixel `0xFF123456`
  and composed game-backbuffer RGB `0x123456` were verified.
  Local `D3DFMT_A8B8G8R8` shared resource creation FAILED; only
  BGRA8 is verified for this device/driver.
- Full `tests/verify-adapter.ps1` passed (exit 0):
  D3D9 CPU renderer x86/x64 6 draws each;
  D3D9 Present x86/x64 4 callbacks each;
  cooperative preexisting-device D3D9+9Ex register/reset/unregister;
  D3D9Ex GPU render x86/x64 both directions;
  x64 Host -> x86 Hook 4 Presents;
  D3D11 9 Presents / 6 composed Shared Texture frames.
- The test process deliberately does not dynamically FreeLibrary the
  Hook: an access violation was observed during a preliminary GPU fixture
  run with that teardown; dynamic multi-thread safe unload remains
  unverified. The isolated fixture exits normally without forcibly
  unloading the native Hook DLL.

**NOT YET CONNECTED TO LIVE OSC HOST:**
The existing DX11 Host texture uses a named NT/keyed-mutex protocol
different from D3D9Ex's legacy shared resource handle.
No cross-process metadata consumer, lifetime/fence/ack protocol, host
legacy BGRA8 publisher, or real-game foreground GPU path was implemented.
Do not use an NT Shared Handle with the D3D9Ex legacy receiver.

Source of truth for the separate Host chat:
`HOST-DX9EX-TRANSPORT-CONTRACT.md`.
Live CrossIntegrity/NvContainer config NOT touched or deployed.

## Multi-GPU Phase 0 DX9 receiver (2026-10-23)

See `PHASE0-DX9-HOOK-REPORT.md`. The scope is strictly Hook-side;
OSC Host GPU Selection/Auto/CPU/GPU UI and publisher are untouched.

- Added read-only `NvOscAdapterGetD3D9AdapterStatus`: game adapter
  LUID, real D3D9Ex-device flag, GPU frame bound flag, and count of
  OSC draws skipped because renderer mutex is busy.
- Existing `ConsumerAdapterInfo v2` ABI/layout and `renderApi=9`
  remain unchanged. Export is SAME-PROCESS; exact API flag still needs
  a Host-side connector/IPC for remote access.
- D3D9Ex LUID is hardware API query; D3D9 regular monitor->DXGI
  lookup is best-effort, especially under hybrid graphics.
- Present callback uses try-lock so a Host GPU texture rebind does not
  make the game's Present wait on our renderer lock. ReShade callback
  uses try-lock too.
- CPU seqlock reader makes one copy attempt per Present instead of
  copying large frames up to three times if producer updates mid-copy,
  and caches a SYNCHRONIZE handle for the publisher process.
- Bound GPU texture is invalidated on incompatible replacement;
  CPU path stays available if Host publishes a CPU frame.
- Renderer tests validate odd (writer-in-progress) seqlock skips
  and GPU generation/rebind status, in addition to existing cases.
- Multi-GPU GPU-frame producer, explicit producer LUID/fence
  handshake and actual OSC Host integration remain separate work.

## DX9 Phase 0.1 continuation — 2026-10-23

- Eliminated synchronous periodic disk logging from game
  `HookPresent`/`HookPresentEx`. Added 80-byte native
  `D3D9TimingSnapshotV1` ABI in
  `include/d3d9_timing_diagnostics.h` and read-only
  `NvOscAdapterGetD3D9TimingSnapshot` export. The numbers are
  cumulative and are never reset by a diagnostic query.
- Added D3D9Ex `Stage`/`Commit`/`Discard` legacy shared GPU
  resource management with generation matching. Existing committed
  frame stays visible until Host attests a new GPU frame is ready.
  `Present` does not wait for a staging operation: it skips OSC
  when the renderer mutex is held and forwards the original Present.
- Native x86/x64 fixture verified real Backbuffer pixel switch from
  `0x123456` to `0x789abc` strictly after Commit, bad-generation
  rejection and Discard without corrupting the committed frame.
  Timing snapshot smoke x86/x64 passed.
- This patch **does not** validate GPU fence completion: Host must
  synchronize cross-GPU writes and resource lifetime before Commit.
  Automatic Host binding to real games remains unimplemented.

- D3D9 and D3D9Ex Reset callbacks now forward the real Reset device
  to the renderer so an unrelated device reset cannot clear the
  active primary device's GPU/adapter state.
- D3D9Ex GPU Stage/Commit thread safety still depends on host/game
  device flags and owner-thread discipline; try_lock does NOT make
  arbitrary D3D9Ex device calls thread safe.

## DX9Ex GPU mailbox receiver Phase 0.1 — 2026-10-24

- Added mirror of Host's 104-byte v1 Mailbox, opt-in receiver
  `D3D9ExGpuMailbox.cpp` and exported, explicit same-process
  `NvOscAdapterPumpD3D9ExGpuMailbox` for legal device-thread
  invocation **outside Present**. Default OFF.
- Game adapter LUID and mailbox Producer PID/liveness verified; the
  consumer registers its PID/capability/LUID, waits nonblockingly
  for Ready generation, stages, commits, ACKs only successful calls,
  then ACKs Release after old GPU alias was actually released.
- Reset, empty Host offer, producer death and producer PID change
  retire the active alias without using old generations as new ACKs.
- Cross-process GPU test x64→x64, x86→x86 and **x64→x86** PASS using
  synthetic producer and private mailbox. Negative tests reject
  unready generation and wrong target LUID.
- `tests/verify-adapter.ps1` includes isolated mailbox fixtures.
- **No actual Host UI/CEF end-to-end or automatic game-loop Pump
  integration yet**; no live Deploy, no GPU Selection changes.
- Mailbox v1 is not authenticated, producer restart has no full epoch,
  and multithreaded D3D9Ex device safety requires game-specific
  validation before enabling any live feature.

## Actual OSC Host publisher source end-to-end IPC Phase 0.2 — 2026-10-24

- The **unmodified current native Host** `OscD3D9ExFramePublisher.cpp`
  is compiled DIRECTLY in a private x64/x86 fixture and driven via
  `Poll/CanPublish/TryPublish/TryPublishGpuTexture/Withdraw/Stop`.
  Host's existing `NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME` env override
  guarantees per-PID isolated mailbox; no live Host source modified.
- Cross-process x64→x64, x86→x86, **x64→x86** Host-source Producer to Hook
  GPU Renderer all PASS: two BGRA8 frames 0x123456→0x789ABC and
  Stage/Commit/Release ACK + D3D9Ex Backbuffer pixels verified.
- Real Host source controlled shutdown in fixture withdraws the offer
  and awaits Release ACK before calling `Stop()`.
- Abrupt Host exit and new Host PID reclaims same mailbox, restarts
  generation 1; Hook drops old GPU alias and reconnects. All three
  architecture pairs PASS.
- Hook `ReleaseCurrent` validates Unbind result before publishing
  Release ACK, or requires the renderer to report that the alias
  was already retired. Never ACK an unverified bound texture.
- The latest Host now exposes graceful `Withdraw()`; the fixture
  verifies Host holds active GPU aliases until Release ACK before Stop.
  `Stop()` alone still drops resources immediately, so production
  shutdown must call Withdraw and await ACK (or prove receiver exit).
  Actual live game-thread `Pump` scheduling and CEF no-readback
  integration are NOT yet verified.
- NEW: `TryPublishGpuTexture` GPU-to-GPU CopyResource tested in all
  three architecture pairs with real Host code and GPU pixels.
- Full explanation and owner handoff:
  `HOST-DX9EX-REAL-SOURCE-IPC-PHASE02.md`.
