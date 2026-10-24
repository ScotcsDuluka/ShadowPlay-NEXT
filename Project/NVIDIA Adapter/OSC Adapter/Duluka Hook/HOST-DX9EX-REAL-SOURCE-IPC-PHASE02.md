# ShadowPlay NEXT — actual OSC Host DX9Ex GPU Publisher ↔ Duluka Hook (Phase 0.2)

Date: 2026-10-24. Branch: Stable. **ISOLATED TESTING ONLY — NOT DEPLOYED.**
Owner: Duluka Hook. Host publisher source owned by OSC Host chat.

## Confirmed integration — actual, UNMODIFIED Host implementation

The isolated x86/x64 IPC fixture `tests/NVIDIA OSC Adapter D3D9Ex
Mailbox IPC Test.vcxproj` now **compiles the actual original production**
`Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/
OscD3D9ExFramePublisher/OscD3D9ExFramePublisher.cpp` directly.

No Host source clone/patch. The Host now supports a private mailbox name
override `NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME`; the fixture child sets it
only to the parent test's unique per-PID name, and also sets Host's
`OSC_DX9EX_HOST_PREVIEW=1` flag. The separate receiver process uses
`NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_NAME` and its own preview flag.
**No production OSC Host mailbox is touched.**

The fixture invokes the real Host public methods `Poll()`,
`CanPublish()`, `TryPublish()`, `TryPublishGpuTexture()`,
`Withdraw()`, and `Stop()`. The receiving process loads the real
Duluka Hook DLL, explicitly calls
`NvOscAdapterPumpD3D9ExGpuMailbox` on a legal D3D9Ex device thread,
outside Present, and checks output pixels with D3D9Ex GetRenderTargetData.

## Tests (repeatable via tests/verify-adapter.ps1)

- x64 Host → x64 Hook; x86 Host → x86 Hook; **x64 Host → x86 Hook**.
- CPU-seeded Host `TryPublish` → D3D11 Legacy BGRA8 shared texture →
  Hook Stage/Commit → game-device backbuffer pixels 0x123456, 0x789ABC.
- **GPU-to-GPU Host CopyResource**: Host's real
  `TryPublishGpuTexture` is handed a BGRA8 D3D11 source texture
  **on the same selected game-adapter D3D11 device**; it uses GPU
  CopyResource into a legacy shared texture, GPU EVENT completion,
  mailbox Ready, Hook Stage/Commit and D3D9Ex readback. Tested all three
  architecture pairs. The source fixture initializes its GPU texture
  with CPU test pixels once; there is no CPU pixel bridge required
  between the Host's source GPU texture and Hook.
- Producer does NOT publish Ready before query completion. Stage/Commit
  and Release ACK sent only after Hook operations succeed. Wrong-LUID
  and early Ready rejection continue in the independent synthetic test.
- **Graceful overlay withdrawal**: test calls the actual Host
  `Withdraw()` after active frames finish. `Withdraw` clears Offer,
  retains active resource until Hook's `releasedGeneration` ACK;
  `Poll()` then reclaims resource and `Stop()` safely finishes.
- **Unexpected Host death**: first Host process exits after first
  Commit without `Stop` or Release ACK. Hook detects dead publisher
  at next game-device Pump, unbinds GPU alias/CPU fallback; new Host
  process reclaims mailbox under new PID, restarts generation 1,
  and the Hook correctly draws new first and second textures.
  Passed x64→x64, x86→x86, x64→x86.

Hook release ACK now checks whether `UnbindExSharedGpuTexture`
succeeded (or renderer reports no bound GPU alias), refusing to
acknowledge retirement of a resource still reported bound.

## Still blocked / limits

1. **Game safe-point integration.** This test CALLS Pump explicitly.
   Production game Hook does not auto-run GPU mailbox Pump. Need a
   cooperating owner-thread connector outside Present. Do not pass
   an `IDirect3DDevice9Ex*` across process boundaries or call it on
   an arbitrary Host worker thread. No live-game deployment.
2. **Live Host shutdown policy.** Host now provides `Withdraw()`
   for a graceful handshake. But `Stop()` itself releases
   `g_pending/g_retiring/g_active` immediately; all production
   callers must ensure `Withdraw` + Release ACK (or verified
   game-process termination) before Stop whenever GPU aliases exist.
   A Host owner review is needed to confirm actual shutdown ordering.
3. **Real CEF GPU surface.** Test shows Host's direct-GPU CopyResource
   path works on the same D3D11 device as game adapter, but does
   NOT prove CEF GPU surface acquisition without readback or
   cross-GPU resource copies for Intel/NVIDIA/AMD mixtures.
4. **Security/compatibility:** Mailbox v1 has no authenticated
   session epoch; PID reuse and arbitrary untrusted consumers require
   further hardening. No high-FPS throughput benchmark, foreground
   vendor ReShade test, guaranteed zero Present stall or safe
   FreeLibrary during active interception.
5. **Classic DX9:** remains CPU Shared Memory v1 publisher to
   GPU texture upload; no direct legacy GPU transport claim.

Keep Host and Hook Preview flags OFF for regular use. The existing
CPU Shared Memory v1, ConsumerAdapterInfo v2, D3D11 v3 ABI and Host
GPU Selection were not modified by this Hook batch. No Commit/Push
performed because Stable is shared by parallel tasks.

## Reproduce

Run `Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/tests/
verify-adapter.ps1` in Windows PowerShell. Its test fixture builds
the latest current Host CPP **directly**, and tests same-bitness,
cross-bitness, GPU CopyResource, graceful Withdraw and crashed
publisher recovery without opening an actual game or live Host UI.
