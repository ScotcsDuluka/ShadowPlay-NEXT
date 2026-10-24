# Phase 7B — Reject mismatched Capture/NVENC GPU adapters

Date: 2026-10-24. Branch: `Stable`. Capture-only source change. **No real device initialized, no video or audio recorded**.

## Finding from the two-GPU workstation

Read-only `nvidia-smi` inventory: two NVIDIA GeForce GTX 1080 Ti adapters using driver 582.78. The production `DdagrabBackend.Initialize` and `D3D11DeviceFactory.Create` independently enumerate and select the **first NVIDIA DXGI adapter**. Both expose their chosen adapter's Windows LUID, but `RecordingEngine.Initialize` previously made no capture-vs-encoder comparison after both returned. Two identically named GPUs are different DXGI adapters; matching names or first-in-list assumptions are not enough to establish compatible texture handoff.

## Production fix

In `EngineCapture/Duluka Capture/Recording/DulukaEngine.Recording/RecordingEngine.vb`, immediately after `_encoder.Initialize(encConfig)` and before publishing Idle, require the capture backend and NVENC backend to have **equal, nonzero Windows adapter LUIDs (high and low parts)**.

The new pure public function `RecordingEngine.RequireMatchingAdapterLuid()` throws on mismatched or unavailable values. The pre-existing outer initialization Catch converts the error to `Faulted`, logs it, disposes partially created capture/encoder backends, and propagates the failure. Correct matching LUIDs proceed normally with an explicit logged confirmation. This is a defensive fail-closed check — it does not change which adapter either backend picks and it cannot, by itself, certify multi-GPU capture.

This guard runs only during normal production engine initialization. The worker was **not restarted/deployed** in this test, so the running NvCapture binary does not yet contain this code.

## Tests

`EngineCapture/HardwareReadiness.Tests/Program.cs` now verifies using synthetic LUID pairs:

- Identical valid low/high portions are accepted, including a signed high part.
- Different low or high portions are rejected.
- All missing-identity cases (capture zero, encoder zero, both zero) fail closed.
- Existing no-capture `SessionConfig`/headroom checks still pass.

**24/24 device-free readiness and identity checks passed.** The source-linked test does not initialize D3D11, DXGI desktop duplication, NVENC, WASAPI or FFmpeg. It does not create MP4 or audio.

Additional acceptance:
- `EngineCapture/Recovery.Tests` stays passing with real `RecordingEngine` fault lifecycle (15/15).
- `NVIDIA Capture.vbproj` Release build succeeds with 0 errors, and its existing 3 warnings remain unchanged.

## Remaining risks / honest limits

- **Actual adapter identity has not been measured inside a running DXGI/NVENC session.** The defensive check can catch an error but does not demonstrate a successful device match on this workstation.
- GPUs may be connected to different monitors; independent selection of the first NVIDIA GPU may capture the wrong display even if encoder and capture LUIDs match. Display/output selection remains a separate design task.
- Live capture could affect the user's current desktop and system sound. No such test was performed without explicit permission.
- The Duluka NVENC backend currently implements only `NVENC_H264`; FFmpeg advertising `av1_nvenc` or `hevc_nvenc` is not proof of this application's codec support.
- No OSC UI, NodeAPI/OSC transport, NvContainer or other owners' in-flight work was touched.

## Reproduce (device-free)

```powershell
$root = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture'
dotnet run --project "$root\HardwareReadiness.Tests\HardwareReadiness.Tests.csproj" -c Release --no-launch-profile
& "$root\Invoke-CaptureHardwarePreflight.ps1"
```
