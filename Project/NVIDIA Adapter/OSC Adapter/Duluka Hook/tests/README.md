# Native Hook tests

> **Current interpretation (24 October 2026):** Earlier **expected-failure** entries for automatic D3D9 late attachment are historical and scoped to the original import/factory technique. The later no-import MinHook detour has its own offline fixture coverage, and DX9Ex mailbox tests now include the real Host publisher in separate processes. None of these should be advertised as proof of smooth in-game CEF→GPU→Hook composition. Read the newest sections and the [actual Host IPC record](../../../../Overlay%20On-Screen%20Control/NVIDIA%20OSC/HOST-DX9EX-IPC-DIRECT-GPU-20261024.md) before quoting a test result.



Existing smoke-test sources and Visual C++ projects are intentionally kept at their current paths during this structural pass so that local invocation paths are not silently broken.

- d3d9_smoke_test.cpp / NVIDIA OSC Adapter D3D9 Smoke Test.vcxproj: D3D9 and D3D9Ex composition path.
- smoke_test.cpp / NVIDIA OSC Adapter Smoke Test.vcxproj: DXGI/D3D11 Present, bridge and composition checks.

After the project builds from the moved source paths, rerun both smoke tests. Passing a standalone smoke test is not the same as proving that a real game uses the matching API path.
DX9 x86/x64 validation: run `Scripts/verify-dx9.ps1` from the repository root.
The DX9 test isolates both pixel and consumer-identity mappings from live OSC.
It draws only through the installed Present hooks, checks premultiplied alpha
and orientation, resets both device types, and requires exactly four draws for
four Presents. `--external-attach` waits for the attachment host to load the DLL
instead of loading it locally; used to test x64 routing into the x86 target.

Real ReShade validation: build both native architectures and run
`Scripts/verify-dx9-reshade.ps1 -ReShade32 <vendor-ReShade32.dll> -ReShade64 <vendor-ReShade64.dll>`.
The script creates isolated test folders, never edits a game installation, and
uses a blue-output shader from `tests/reshade/Isolation.fx`. Both D3D9 and 9Ex
must preserve the opaque green/red OSC markers, blend translucent red over the
shader's blue exactly once, and survive Reset/ResetEx. Each architecture renders
1680 frames. ReShade logs remain under `Build/Dx9Validation/ReShade/<arch>/`.
The larger 640x480 back buffer avoids ReShade's dummy-device exclusion.

A foreground test-window failure is an environmental failure, not a pass. Close
any foreground-locking overlay before running these tests.

## DX9 validation modes (2026-10-23)

The smoke executable now has three independent test modes:
- `--renderer-only`: invokes the **test-only** D3D9 renderer entry; verifies
  640x480 pixel orientation, premultiplied-alpha blend, Reset/ResetEx,
  malformed large rowPitch rejection, and recovery (5 successful draws).
  **Present interception is not used or claimed** in this mode.
- `--hook-counter-only`: supplies an invisible frame, so the real installed
  D3D9/D3D9Ex Present hooks run and count 4 Presents through Reset/ResetEx.
  Pixel composition is **not** claimed. This mode does not require foreground.
- Standard mode: original combined foreground composition assertions. It must
  have a foreground-capable interactive desktop to pass.

The `--external-attach --hook-counter-only` combination runs the counter
fixture under the x64 Host → x86 Host → x86 Hook DLL path, without loading
anything into a real game. See `tests/verify-dx9-cross-bitness.ps1`.

`tests/verify-adapter.ps1` performs both DX9 modes on Win32/x64, the
cross-bitness target test, native/managed builds and D3D11 regression.
It does **not** deploy. `-RunDx9WindowTests` additionally runs the original
combined foreground-dependent DX9 tests.

`d3d9_late_attach_test.cpp` and its own vcxproj are a **known failing**
regression fixture: create a D3D9 device BEFORE loading the DLL. Present count
remains 0 with the current import/factory-vtable Hook implementation.
Experimentally patching a temporary device's VTable is NOT a safe solution:
the test device and temporary device own distinct heap VTables. That
experimental code was reverted. This feature is not production-ready.

## 2026-10-23 cooperative late-attach regression

Build `NVIDIA OSC Adapter D3D9 Late Attach Test.vcxproj` for Win32/x64.
It creates the D3D9/9Ex Device **BEFORE** loading the Hook DLL.

- `<test>.exe <matching-hook.dll> --cooperative`:
  D3D9 original Present/Reset hooks verified, 4 Presents, then
  unregister restores the original vtable; exit 0.
- `<test>.exe <matching-hook.dll> --cooperative-ex`:
  D3D9Ex PresentEx/ResetEx verified, 4 Presents, then unregister;
  exit 0.
- `<test>.exe <matching-hook.dll>`: expected negative case:
  automatic late attach is NOT implemented, `presents=0 expected=4`,
  exit 10. Not a regression suite success criterion.

`verify-adapter.ps1` runs the cooperative cases automatically, in
addition to the Renderer-only / Hook-counter-only / cross-bitness tests.
The Renderer-only case now includes hidden/reappeared same-frame-ID
pixel reupload; successful draws increased from 5 to 6.

Cooperative registration/unregistration must execute inside the device's
own process, while the owner holds a valid COM pointer and has quiesced
all render callbacks before unregister. It does NOT constitute safe
DLL unloading from arbitrary running games.

## 2026-10-23 D3D9Ex legacy-shared GPU test

`tests/NVIDIA OSC Adapter D3D9Ex GPU Interop Test.vcxproj` builds
the isolated Win32 and x64 test executable. Invoke it with the
matching `NVIDIA OSC Adapter Hook.dll` as its argument; it verifies
*both* GPU sharing directions (D3D9Ex publisher → D3D11 and
D3D11 publisher → D3D9Ex), Hook bind/unbind ABI and actual pixel
composition to a native D3D9Ex game Backbuffer.

This fixture does NOT use the live OSC Host, a real game, or an
untrusted process. It purposely avoids dynamic FreeLibrary because
Hook DLL teardown across running initialization/callback threads is
not certified safe. Process exit reclaims fixture-owned resources.

The default `tests/verify-adapter.ps1` now runs this test for x86/x64.
See `HOST-DX9EX-TRANSPORT-CONTRACT.md` for Host ownership, handle
format and required synchronization before a live GPU bridge.

## Phase 0.1 diagnostics and staged GPU switch (2026-10-23)

Native D3D9 Hook now exposes versioned read-only
`NvOscAdapterGetD3D9TimingSnapshot`
(`include/d3d9_timing_diagnostics.h`); smoke validates its ABI
and non-destructive counters in Win32/x64 tests. No periodic disk I/O
occurs in Present/PresentEx.

The D3D9Ex GPU fixture stages a **second**, differently colored
legacy-shared resource, asserts output remains the old pixel until a
matching-generation Commit, rejects the wrong generation, verifies
the new pixel and then Discards an unready resource. See the Host
contract for the producer's explicit synchronization obligations.

## 2026-10-24: separate-process GPU mailbox receiver regression

`tests/NVIDIA OSC Adapter D3D9Ex Mailbox IPC Test.vcxproj`
builds Win32/x64; producer child uses a private, per-PID mailbox.
Parent loads the actual Hook, invokes its opt-in Pump on the real
D3D9Ex device thread **outside Present**, and validates two frame
colors (0x123456→0x789ABC) and Stage/Commit/Release ACKs.
Runs same-bitness and x64-Host→x86-game cases; denies early Ready,
wrong LUID and stale generations. `verify-adapter.ps1` includes
all 3. This is a **synthetic publisher**, not the OSC Host UI.

## Actual Host implementation cross-process test — Phase 0.2

The IPC fixture now compiles the UNMODIFIED current Host
`OscD3D9ExFramePublisher.cpp` directly from OSC Host project.
Host's own `NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME` preview override
creates a private per-PID mailbox; no Host source cloning or deploy.

`d3d9ex_mailbox_ipc_test.cpp` uses real Host `Poll`,
`CanPublish`, `TryPublish`, `TryPublishGpuTexture`, `Withdraw`
and `Stop` via `--host`, `--gpu-source` and `--recovery`.
GPU Source mode exercises GPU-to-GPU `CopyResource` without CPU
pixels in the Host-to-Hook path; `--recovery` simulates Host process
death followed by new PID/generation-1 reconnect. All modes run
x64→x64, x86→x86, x64→x86 in `verify-adapter.ps1`.
Graceful shutdown uses actual Host `Withdraw()` + Release ACK before
`Stop()`; production shutdown callers must respect this ordering.