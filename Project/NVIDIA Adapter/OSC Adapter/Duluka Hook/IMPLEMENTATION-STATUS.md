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
