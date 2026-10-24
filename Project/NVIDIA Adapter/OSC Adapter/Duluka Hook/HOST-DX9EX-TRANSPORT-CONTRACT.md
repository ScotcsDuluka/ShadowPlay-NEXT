# OSC Host ↔ Duluka D3D9 / D3D9Ex transport contract
Updated 2026-10-23 — **proposal + verified receiver**, not a live publisher integration.

## Ownership — avoid competing edits
- **OSC Host chat owns:** generating actual OSC pixels, publishing CPU or GPU frames,
  resource lifetime, GPU synchronization, cross-process handle transport and
  producer telemetry. Do not modify the DX9 Hook's internals in that chat.
- **Duluka Hook chat owns:** D3D9/D3D9Ex hook/renderer, accepting shared frames,
  fallback, validation, device Reset, receiver metrics and native tests.
- Coordinate protocol changes via this file before enabling them in a runtime.
  No one needs to duplicate the NVIDIA OSC HTML/CEF UI for D3D9.

## Transport currently deployed or present in source
- **DX9/DX9Ex CPU fallback:** `Local\\NVIDIAOSC.Frame.D3D9.v1`
  (wire structure `include/osc_frame_bridge.h:D3D9Frame`, magic
  `0x4F534439`, version 1). CPU-premultiplied BGRA8 pixels,
  width/height/rowPitch/pixelBytes and sequence-stable frameNumber. The
  D3D9 Renderer copies this into a GPU D3D9 texture.
- **DX11 source:** a separately published named D3D11 Texture using
  NT Shared Handle + Keyed Mutex. **This cannot be handed directly to
  D3D9Ex CreateTexture.** Do not change/break the existing DX11 path.
- No live Host-originated D3D9Ex GPU publisher or cross-process metadata
  subscription was implemented by this DX9 work. Keep the CPU path enabled.

## Optional D3D9Ex GPU path — verified at consumer
- Same GPU Adapter/LUID for the D3D11 producer and D3D9Ex consumer.
- Pixel format: premultiplied **BGRA8** (`DXGI_FORMAT_B8G8R8A8_UNORM`
  ↔ `D3DFMT_A8R8G8B8`); one mip, one array element, sample count 1,
  default-usage shareable render-target texture.
- **D3D11 producer must allocate an additional legacy-shared texture**
  with `D3D11_RESOURCE_MISC_SHARED` (not `SHARED_NTHANDLE` or
  `SHARED_KEYEDMUTEX`) and acquire its opaque handle from
  `IDXGIResource::GetSharedHandle`. Do **not** call `CloseHandle` on
  this legacy resource handle. The producer keeps the resource alive
  until the consumer is finished.
- **Consumer:** same-process trusted adapter integration can invoke:
  `BOOL WINAPI NvOscAdapterBindD3D9ExGpuFrame(IDirect3DDevice9Ex* device,
  HANDLE legacySharedHandle, uint32_t width, uint32_t height,
  uint64_t frameNumber)`. Frame 0, null handle, invalid dimensions and
  a failed GPU open are rejected. Supported dimensions: 1..8192 each.
- Consumer creates an alias with
  `IDirect3DDevice9Ex::CreateTexture(...,D3DUSAGE_RENDERTARGET,
  D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&texture,&legacyHandle)`, then
  samples it directly in the existing premultiplied-alpha overlay quad;
  there is **no CPU pixel copy** in this mode.
- `BOOL WINAPI NvOscAdapterUnbindD3D9ExGpuFrame(IDirect3DDevice9Ex*)`
  releases the consumer's alias, on Hide/producer retirement or before
  ending the device lifetime. Device Reset also releases this alias.
- When no GPU input is bound, failed to bind, or was reset, the existing
  CPU Shared Memory reader is used as fallback **if the CPU publisher
  supplies a valid frame**. Failed replacement must not keep stale GPU
  frames on screen.

## Coordination and synchronization still REQUIRED
- D3D9Ex does not offer the D3D11 Keyed Mutex protocol on its own
  legacy-texture path. The producer must guarantee all GPU writes to
  a published frame are complete (GPU flush/fence), and **must not
  overwrite a texture that the consumer is still using**. Decide on a
  double/triple-buffered producer slot + explicit acknowledgement/fence
  protocol before production use; publishing a new frame number alone
  does not synchronize GPU access.
- A shared HANDLE value passed via IPC does not transport a live COM
  device pointer. The in-game adapter must use the actual D3D9Ex
  device from its own process and verify producer identity and adapter
  LUID before invoking the exported bind API.
- Cross-process metadata/handle delivery, generation/resize/retirement,
  foreground-frame test, actual CEF frame producer, vendor ReShade
  coexistence and quiescent DLL unload remain **unimplemented / unverified**.
- Never allow untrusted arbitrary pointer or handle values to drive
  these exports. Protect the bridge producer/consumer boundary.

## Tests that establish the current feasibility
`tests/d3d9ex_gpu_interop_test.cpp` plus its vcxproj run in owned
isolated Win32 and x64 processes. Verified on the current machine:
1. D3D9Ex creates legacy BGRA8 texture, D3D11 opens and writes it;
   D3D9Ex GPU readback receives exactly `0xFF123456`.
2. D3D11 creates a legacy BGRA8 texture and publishes GetSharedHandle;
   D3D9Ex opens it and gets `0xFF123456`.
3. `NvOscAdapterBindD3D9ExGpuFrame` makes the existing native D3D9
   Renderer composite this texture directly to the **game backbuffer**;
   readback RGB `0x123456` for both producer directions.
4. Win32 and x64 native Hook binaries and test fixture pass.
5. RGBA D3DFMT_A8B8G8R8 failed resource creation in the local probe,
   so BGRA8 is the currently verified format, not a universal guarantee.

These are same-process fixtures that use the actual D3D11 and D3D9Ex GPU
devices. They **do not prove** live OSC Host delivery across processes.
General dynamic FreeLibrary/unload is still unsafe; the fixture exits
without forcibly unloading the Hook DLL.

## Work request for OSC Host chat
1. Preserve existing DX11 NT-handle/keyed-mutex and DX9 CPU v1 paths.
2. Prepare **separate optional D3D9Ex legacy BGRA8 GPU publisher**,
   only if receiver adapter LUID matches; do not replace the old handle.
3. Propose a versioned cross-process metadata and synchronization
   protocol (frame number, generation, producer PID, dimensions, format,
   adapter LUID, legacy handle and ownership/fence signals).
4. Do NOT flip the live feature on until both projects test automatic
   consumer bridge, resource lifetime and foreground on-screen output.

## Multi-GPU Phase 0 scope confirmation — 2026-10-23

**Authoritative division:** OSC Host owns rendering mode selection,
GPU adapter selection and any inter-adapter transfer (Intel / AMD /
NVIDIA). Duluka Hook owns only the game's receiver/renderer. A Host
rendering on GPU A must deliver a compatible frame onto GPU B if the
game runs on GPU B. Hook does not attempt its own cross-GPU copy.

Read-only game adapter report is in `PHASE0-DX9-HOOK-REPORT.md`;
the old consumer identity v2 mapping remains unchanged.
Same-process `NvOscAdapterGetD3D9AdapterStatus` reports:
game LUID, isD3D9Ex, gpuFrameBound and busyFrameSkips. No remote Host
telemetry API was changed or wired to this export.

Present callbacks avoid waiting on the Hook's renderer mutex; if
Host has not made a frame ready, skip OSC and always pass through the
original Present. CPU Shared Memory is retained as the standard DX9
and DX9Ex fallback. Legacy DX9Ex GPU bind stays opt-in and requires
Host-side pre-transfer, compatible same-adapter resource, producer
synchronization and explicit lifetime/retirement protocol.

## Phase 0.1 — explicitly staged D3D9Ex GPU switch (2026-10-23)

To keep a texture from being sampled **before** Host finishes an
inter-GPU transfer, Duluka now exports a separate optional two-phase
handoff. This supplements the old immediate Bind/Unbind API; existing
ABI and CPU Frame Bridge layout are unchanged:

```cpp
BOOL WINAPI NvOscAdapterStageD3D9ExGpuFrame(
    IDirect3DDevice9Ex* device, HANDLE legacySharedHandle,
    uint32_t width, uint32_t height, uint64_t generation);
BOOL WINAPI NvOscAdapterCommitD3D9ExGpuFrame(
    IDirect3DDevice9Ex* device, uint64_t generation);
BOOL WINAPI NvOscAdapterDiscardD3D9ExPendingGpuFrame(
    IDirect3DDevice9Ex* device);
```

State model: **current committed frame** remains active while the new
legacy-shared BGRA8 resource is staged. Only a matching generation
can be committed. Discard frees the pending alias without touching
current output. Reset / Unbind / direct Bind clean up pending state.
Failed Stage preserves the previous committed frame; if that producer
has already retired it, Host must explicitly Unbind or publish a CPU
fallback to avoid an invalid lifetime.

**Synchronization contract:** Host must publish the GPU resource on the
*game's accessible adapter* and verify its GPU write/copy completion,
then call Commit from a trusted same-process integrator. Commit itself
is an explicit Host-ready declaration, **not** a GPU Fence wait or
proof of a synchronized cross-process transfer. Host must keep the
committed resource alive until Hook discards/replaces it.
Stage may perform D3D9Ex resource creation on the calling/connector
thread; `Present` uses try-lock and may skip OSC drawing during that
operation rather than wait on the Hook renderer mutex.

Test: `tests/d3d9ex_gpu_interop_test.cpp` creates a second GPU texture
with different pixels, asserts output remains old color (`0x123456`)
until Commit, rejects an incorrect generation, switches to
`0x789abc` after Commit, discards pending and verifies restoration.
Passed in isolated x86 and x64 fixtures both producer directions.

**Also performance:** Native D3D9 Present/PresentEx no longer writes
periodic diagnostics to the filesystem from the game callback.
`NvOscAdapterGetD3D9TimingSnapshot(out, sizeof(*out))` provides a
versioned, read-only lifetime timing snapshot with no reset or logging.
ABI: `include/d3d9_timing_diagnostics.h` (80 bytes Win32/x64).
Read it outside the game Present callback to compute deltas.

**D3D9 thread safety:** An optional same-process Stage call invokes
`IDirect3DDevice9Ex::CreateTexture`. Host/connector must not use a
foreign worker thread against a non-multithread-safe game D3D9Ex
device. The Hook render mutex protects the Hook's data but does not
synchronize unrelated game API calls. Integrators must obey the game's
device thread-ownership policy, and test it in the actual game.

## Hook mailbox consumer status — 2026-10-24

**Implementation available, still opt-in and not auto-connected:**
`src/Hook/D3D9/D3D9ExGpuMailbox.cpp`, mirrored 104-byte schema in
`include/d3d9ex_gpu_mailbox.h`, and
`NvOscAdapterPumpD3D9ExGpuMailbox(IDirect3DDevice9Ex*)` export.

**Trusted in-game connector obligation:** invoke Pump on a legal
D3D9Ex device-owner thread at a safe point **outside Present**. Pump
must not be called with an IPC-supplied device pointer. This split
avoids texture Create/Open inside the game's Present.
Receiver opt-in requires
`NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW=1`.
Host opt-in `OSC_DX9EX_HOST_PREVIEW=1` remains independent.

**Cross-process synthetic GPU IPC tested:** x64→x64, x86→x86 and
x64→x86. Tests exercised GPU pixel readback, Stage/Commit and
Release ACK, unready generation rejection, wrong-LUID rejection.
The complete native OSC Host/CEF Publisher was **not** the synthetic
fixture producer, and no automatic in-game connector was installed.
Exact receiver status and risk details are in
`HOOK-DX9EX-GPU-MAILBOX-PHASE01.md`.

## Phase 0.2: actual OSC Host publisher source test result — 2026-10-24

**Improvement:** unmodified real `OscD3D9ExFramePublisher.cpp`
source is compiled directly and runs as a separate Host Producer against
the Hook, x64→x64, x86→x86, x64→x86; real GPU pixels and ACKs pass.
The Host's own preview-only mapping override creates a private per-PID
mailbox; no cloned or patched source is used. Its GPU-to-GPU
`TryPublishGpuTexture` also passes (no CPU pixel bridge on this path).
Abrupt Host process death and generation-1 restart also pass with safe
Hook alias retirement and CPU fallback.

**Outstanding Host-team work:** Host now provides `Withdraw()`,
and the test calls that real API before waiting for Release ACK and
then calling Stop. Production shutdown callers must guarantee the same
ordering, or verify the consumer game process has stopped, because
`Stop()` itself still immediately frees `g_pending/g_retiring/g_active`.
The GPU-source CEF acquisition, game safe-point connector and runtime
security are not covered by this fixture. No live CEF/OSC game test or automatic device-thread
connector is yet verified. See `HOST-DX9EX-REAL-SOURCE-IPC-PHASE02.md`.
