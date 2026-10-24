# OSC Host Multi-GPU — Phase 0 (2026-10-23)

Scope: **OSC Host only**, branch `Stable`. The independent Hook team owns D3D9/D3D9Ex/D3D11 interception and rendering. Do not edit or deploy its work from this task.

## Current implementation (not an active GPU override)

- `src/osc_main.cpp` handles renderer preferences through native CEF queries `QUERY_OSC_GET_RENDERERS` and `QUERY_OSC_SET_RENDERERS`.
- `gpuAdapter` in `NVIDIA Config/nvidia-osc.json` is an **intended/preferred** adapter, persisted as a DXGI LUID or `auto`. The Host still uses Windows' default D3D11 hardware device for the compositor. A selected adapter is **not yet applied** to CEF or the compositor.
- DXGI enumeration returns available adapters; menu labels include the DXGI enumeration index and LUID because display names may repeat.
- Query diagnostics: `activeGpuAdapter`, `activeGpuAdapterName` (actual Host compositor device), `cefFrameTransport` (`gpu-shared-opened`, `cpu-paint`, `idle`). A successfully opened CEF resource demonstrates compatibility with the compositor, **not** CEF's separately verified GPU identity.
- UI: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/osc-renderer.js`. Localized strings in `l10n/en-US.json`, `l10n/th-TH.json`.
- The selector uses native NVIDIA `md-select`, validates available LUIDs before saving, and clearly marks the GPU routing feature pending rather than pretending it changed the GPU.

## Read-only adapter inspection and isolated GPU transfer probe

- Windows WMI displays two physical NVIDIA GTX 1080 Ti models: ASUS ROG STRIX, GALAX EXOC; plus Parsec and spacedesk display adapters.
- DXGI D3D11 probe enumerated four adapter LUIDs (three with ASUS's display name, one GALAX). A repeated DXGI display name does not prove three physical ASUS cards; never deduplicate solely by name.
- Probe sources: `build_test/GpuAdapterProbe/GpuAdapterProbe.cpp`, `GpuAdapterProbe.vcxproj`.
- Probe is an isolated C++ process: creates D3D11 devices/resources, reads BGRA8 pixels from GPU-A staging, uploads them to GPU-B, reads back and compares a known pixel. No Hook, game injection, CEF startup, overlay restart, or live deployment.
- On this machine, 4 logical adapters, 12 ordered adapter pairs, 2 image sizes (256x256, 1920x1080), **24/24 transfers passed**.
- One-shot 1920x1080 test took approximately 6.3–26.4 ms including resource creation and GPU synchronization; **not** a steady-state benchmark, FPS-loss estimate, or proof of a nonblocking worker.
- Exact live GPU-to-GPU sharing and CEF shared texture opening on a selected adapter remain **unverified**.
- DX9 CPU bridge remains the baseline fallback; DX9Ex GPU path is owned by the Hook team and requires a separately agreed compatible Host publisher.

## Build / validation

- OSC x64 Release isolated test build: `build_test/build-osc-test.ps1` (build only, no deploy).
- Node UI/background/localization regression: `node --test tests/oscRenderer.test.js tests/oscBackground.test.js tests/oscBgWebgl.test.js tests/oscUiLocalization.test.js` under NodeAPI; **32/32 passed**.
- GPU probe: MSBuild `build_test/GpuAdapterProbe/GpuAdapterProbe.vcxproj` Release|x64; run `build_test/GpuAdapterProbe/bin/GpuAdapterProbe.exe`.
- No production deployment. Uncommitted Stable source edits must be reviewed before merging/staging due to parallel agents.

## Next engineering gates

1. Reconcile Chromium/CEF actual GPU device versus Host compositor GPU. A shared handle cannot safely be assumed portable across adapters.
2. Implement **opt-in isolated** preferred-adapter compositor initialization with a verified CEF-frame open and rollback to Windows default on a failed compatibility test. Do not turn it on silently for live users.
3. Measure asynchronous steady-state readback/upload and backpressure separately from the game Present thread.
4. Keep Frame Bridge layout unchanged unless Hook and Host owners explicitly agree on a versioned update.
5. Mark GPU selection active only after proof of actual Host and CEF use on the selected adapter and robust fallback for missing GPUs.

## 2026-10-24 DX9 / DX9Ex publisher continuation

See [HOST-DX9-PUBLISHER-PHASE0.md](HOST-DX9-PUBLISHER-PHASE0.md) for the new Host-only implementation.

- DX9 CPU Shared Memory v1 stays the baseline and now serializes visibility and pixel writes through the same mutex.
- The existing Host D3D11 publisher worker can create an additional DX9Ex-compatible, same-game-adapter **legacy BGRA8 shared texture**, after snapshotting complete CPU v1 pixels.
- A separate versioned Host mailbox announces a GPU-completed generation for the trusted in-game Stage/Commit connector. The Hook receiver is **not** connected to this mailbox yet. Never infer that a game is DX9Ex from the existing `renderApi=9` identity alone.
- New GPU channel defaults OFF and additionally requires an explicit consumer PID/LUID + DX9Ex Stage/Commit capability handshake.
- The isolated Host test has **passed Win32 and x64** (single-process simulated consumer ACK). No live game integration or production deploy.
- Do not overwrite the shared Hook contract or deploy until the Hook team implements and validates the mailbox receiver and release ACK.
