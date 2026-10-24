# Phase 7C — Read-only DXGI GPU/output topology and display choice

Date: 2026-10-24. Capture-only scope. No desktop recording or audio capture was authorized or performed.

## Why this matters

This AORUS-DESKTOP has **two NVIDIA GeForce GTX 1080 Ti cards** reported by NVIDIA's `nvidia-smi` inventory, but a read-only IDXGIFactory1 enumeration returns **four NVIDIA-vendor DXGI adapter entries** and one Microsoft adapter. These inventories are NOT interchangeable: indexes and entry counts must never be blindly equated. The Windows adapter LUID is the comparison mechanism already guarded by Phase 7B in `RecordingEngine.RequireMatchingAdapterLuid`; **a live capture/encoder LUID match has not yet been observed**.

Read-only DXGI results from this session:

| DXGI index | Vendor | Attached outputs | Output sizes |
| --- | --- | ---: | --- |
| 0 | NVIDIA (0x10DE) | 3 | 1680x1050, 1280x800, 1080x1920 |
| 1 | NVIDIA (0x10DE) | 0 | none |
| 2 | NVIDIA (0x10DE) | 0 | none |
| 3 | NVIDIA (0x10DE) | 0 | none |
| 4 | Microsoft (0x1414) | 0 | none |

The **first NVIDIA adapter (DXGI index 0)** has three `AttachedToDesktop=True` outputs. Its first attached output is **1680×1050** and has desktop coordinates starting at **(0,0)**. Independent read-only `System.Windows.Forms.Screen` enumerations confirm that the Windows Primary display is 1680×1050 at (0,0); the other two monitors are 1280×800 and portrait 1080×1920.

This means the current `DdagrabBackend.Initialize` algorithm — first NVIDIA adapter, first attached output — **is currently expected to choose the Windows primary screen**, not a detached output. This is only a metadata expectation: `Initialize()`, `DuplicateOutput()`, `AcquireNextFrame()` and the encoder were **not called**.

## Files and safety

Added `HardwareReadiness.Tests/DxgiInventory.cs` that creates only `IDXGIFactory1`, enumerates `IDXGIAdapter1` and `IDXGIOutput`, and reads `AdapterDescription1` / `OutputDescription`. Every COM object is disposed after metadata collection. It **does not create a D3D11 device, IDXGIOutputDuplication, a texture, framebuffer, screenshot, MP4, audio stream, microphone, or FFmpeg encoder**. The output logs only adapter indices, vendor IDs, display attachment flags, geometry and LUID; no window titles, screen pixels or audio content.

`HardwareReadiness.Tests/Program.cs` has `--dxgi-only` mode that verifies the chosen first NVIDIA DXGI adapter has nonzero LUID and at least one attached desktop output; otherwise it **fails closed**. If the first attached output is not at the primary origin (0,0), it reports a warning rather than claiming primary selection. `Invoke-CaptureHardwarePreflight.ps1` now runs this mode **after** the 24 existing non-capture disk/config/LUID tests (with `--no-build`) and fails if metadata discovery did not pass. The existing service health read-before/read-after guards remain.

## Acceptance

- Read-only DXGI inventory: 5 adapters, 4 NVIDIA-vendor entries, 3 attached desktop outputs on index 0; first attached output matches actual Windows primary display.
- HardwareReadiness unit/preflight tests: **24/24 configuration/headroom/LUID checks** plus read-only DXGI adapter/output checks.
- No NvCapture production worker or production binary deployment, restart or record request.
- No OSC UI, OSC transport, NodeAPI, NvContainer or other shared subsystem edits.

## Remaining gates — IMPORTANT

1. Production capture/encode selection has **not** been proven to use the same physical GPU: read-only enumeration does not instantiate GPU devices. Phase 7B's matching-LUID runtime guard is source-only, not deployed.
2. The primary output may not be the desired recording monitor. Present behavior is first-attached output, which happens to match Primary on this machine. A future output-selection feature would need deliberate product/UX design.
3. DXGI enumeration alone cannot establish that a particular NVIDIA GPU is NVENC-capable, or that capture/encode will avoid game stutter. No live frames were sampled.
4. A separate, explicitly approved test is required before touching real video content or recording system audio/music and microphone.
5. The prior full 8-stage offline media gate passed 1,323 synthetic/offline checks in Phase 6H; this phase does not repeat or substitute for device acceptance.

## Reproduce (no pixels or audio)

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture'
dotnet run --project "$r\HardwareReadiness.Tests\HardwareReadiness.Tests.csproj" -c Release -- --dxgi-only
& "$r\Invoke-CaptureHardwarePreflight.ps1"
```
