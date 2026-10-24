# D3D9 Hook

D3D9Hook.cpp owns D3D9/D3D9Ex factory import patching, device-vtable Present/PresentEx and Reset/ResetEx interception, hook removal, and exported test/status entry points. Frame-bridge reads, D3D9 texture/state-block resources, CPU texture upload and composition live in src/Renderer/D3D9/D3D9Renderer.cpp.
## DX9 late attachment boundary (verified 2026-10-23)

Import/factory interception hooks devices created after the DLL is loaded.
An already-created D3D9 Device does **not** share its heap VTable with a
temporary D3D9 Device, even if the Present function addresses match.
The isolated `tests/d3d9_late_attach_test.cpp` reproduces the gap with
`presents=0` instead of 3. Do not claim late attachment is supported.

For working-path validation that does not require foreground, use
`--hook-counter-only` (4 native Present/PresentEx calls), and use
`--renderer-only` separately (5 composition draws, pixel/alpha/reset
and malformed rowPitch recovery) on both Win32 and x64.
Vendor ReShade, foreground compositing and safe detach need separate tests.

## Cooperative registration API (2026-10-23)

`BOOL WINAPI NvOscAdapterRegisterD3D9Device(IDirect3DDevice9 *device)`
attaches to the exact pre-existing D3D9 or D3D9Ex Device in a trusted
same-process integrator; it rejects NULL and supports repeated calls.

`BOOL WINAPI NvOscAdapterUnregisterD3D9Device(IDirect3DDevice9 *device)`
restores this component's original Present/PresentEx and Reset/ResetEx
VTable slots **before the owner calls Release()**. The caller must
serialize and wait for any active rendering callbacks independently;
this operation is NOT a safe general-purpose unload.

The isolated fixture tests 4/4 interception across Reset, restores
the VTable, verifies no callback after unregister, and confirms the
original unmanaged auto-late-attach limitation (0/4).

## Optional D3D9Ex GPU frame (2026-10-23)

Native exports:
- `NvOscAdapterBindD3D9ExGpuFrame(deviceEx, legacyHandle, width, height,
  frameNumber)`
- `NvOscAdapterUnbindD3D9ExGpuFrame(deviceEx)`

The renderer accepts same-device, legacy-shared, premultiplied BGRA8 GPU
texture frames; GPU composition bypasses the CPU pixel mapping.
The existing D3D9 CPU bridge is retained as fallback.
The caller must synchronize producer writes and shared resource lifetime.
No host IPC or automatic producer discovery is implemented here.
The isolated D3D11→D3D9Ex GPU producer/Hook backbuffer test is included
in `verify-adapter.ps1`; do not claim live Host compatibility yet.

## 2026-10-24: automatic late attach when no D3D9 factory imports
The default D3D9 import/factory hook remains unchanged for early-load games.
When the loaded modules have no Direct3DCreate9[Ex] imports, the DLL now
installs MinHook 1.3.4 implementation-code detours on D3D9 and D3D9Ex
Present/Reset entrypoints obtained from a disposable D3D9 device.
This does NOT patch the temporary device's VTable, discover the live device
pointer, or bypass game protection. Offline regression can explicitly exercise
the same fallback with DULUKA_D3D9_FORCE_LATE_ATTACH=1, before Hook init.
Pre-created fixture devices must pass 4 Presents/PresentEx callbacks on
x86 and x64. Cooperative Register/Unregister ABI remains unchanged.

Limitations: pre-existing devices in applications with an unrelated imported
D3D9 factory function still need a dedicated late-attach route; game-device
Release safety and concurrent explicit DLL unload require device-thread
quiescence. Do not deploy while the target game is running, and do not
claim live Overlay composition until the OSC CPU frame publisher is verified.
MinHook source is pinned under third_party/minhook (BSD-2-Clause).