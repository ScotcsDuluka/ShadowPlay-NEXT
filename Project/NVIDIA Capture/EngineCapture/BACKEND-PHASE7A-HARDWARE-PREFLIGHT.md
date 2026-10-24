# Phase 7A — Hardware readiness preflight (read-only; NO capture)

Date: 2026-10-24. Branch: `Stable`. Scoped ONLY to `Project/NVIDIA Capture`.

## Test boundary

**No screen recording, no desktop duplication, no microphone or system loopback, no GPU capture or NVENC encoder session, no output MP4, and no restart/deploy of NvCapture.** The user's music continues uninterrupted. Tool commands run with BelowNormal PowerShell priority. Information is collected from existing files, nvidia-smi's **read-only** inventory, FFmpeg encoder catalog (NOT encoding), production `DiskHeadroom.Evaluate`, and the existing worker's read-only `/health` endpoint.

The script is `EngineCapture/Invoke-CaptureHardwarePreflight.ps1`. It REFUSES to proceed unless the production worker is ready and **not recording**, the local Capture build's core assemblies and bundled FFmpeg/FFprobe exist, NVIDIA read-only driver inventory works, FFmpeg advertises `h264_nvenc`, and the synthetic-test destination drive has enough free space (default 5 GiB).

## Actual observed machine inventory

- Connected machine: **AORUS-DESKTOP**.
- `nvidia-smi --query-gpu=index,name,driver_version` returned two NVIDIA GeForce GTX 1080 Ti adapters (indexes 0 and 1), NVIDIA driver **582.78**.
- FFmpeg bundled tool advertises `h264_nvenc` (also other codecs), and `NvCapture.dll`, `DulukaEngine.Recording.dll`, `DulukaEngine.Encoder.Nvenc.dll`, and `DulukaEngine.Video.Ddagrab.dll` exist in the Capture Release output. **The catalog is not proof the selected GPU supports a codec or that NVENC device initialization will succeed.**
- Test workspace on C: had approximately **35.35 GiB free**. That is NOT an estimate for a user-selected recording gallery on a different disk.
- Live worker before/after test: `ok=true, ready=true, recording=false`. No recording actions were issued.

## Production-algorithm readiness test

New `EngineCapture/HardwareReadiness.Tests` references the ACTUAL `DulukaEngine.Recording` project. It exercises `SessionConfig`, `RequestsAnyAudio`, and `DiskHeadroom.Evaluate` **without constructing/initializing any GPU backend or writing any output file**.

The actual production disk estimator was checked with a video-only, both-audio-flags-off **15s 20 Mbps** dry-run: requires **75,000,000 bytes** peak video headroom. Tests verify system-only, mic-only, two PCM sidecars, mixed AAC and two separate AAC tracks; simulate insufficient space with `DiskHeadroomOverrideBytes=long.MaxValue` without filling the disk.

**17/17 test assertions passed.** The wrapper preflight passed all readiness stages, reporting `CAPTURE_PHASE7A_PREFLIGHT_PASS gpus=2 read_only=true gpu_initialized=false screen_captured=false audio_opened=false deployed=false`.

**Fail-closed behavior also tested:** invoke preflight with `-MinFreeGiB 100` against the actual ~35 GiB available. The preflight refused at `PHASE7A_FAIL TestVolumeMinimumFree` without opening any capture devices or writing an MP4; NvCapture remained idle.

## Blocking gates before real DXGI/NVENC certification

1. **Multi-GPU identity.** `DdagrabBackend.vb` chooses the first NVIDIA IDXGIAdapter1; `D3D11DeviceFactory.vb` in the encoder independently chooses the first NVIDIA IDXGIAdapter1. The backends expose adapter LUIDs, but the `RecordingEngine.Initialize` orchestration examined in this preflight does **not** compare capture LUID against encoder LUID. With two physically identical GPUs, static source similarity does not prove they selected the same adapter in a live session. Require runtime equality evidence before cross-device textures are considered certified.
2. **Codec scope.** The Duluka NVENC `NvencEncoderBackend.Initialize` explicitly rejects `CodecKey` values other than `NVENC_H264`. FFmpeg listing of HEVC or AV1 encoder plugins does not mean they are supported in this runtime implementation, or supported by the actual installed GPU. The readiness script checks only for catalog H.264 presence.
3. **DXGI/NVENC device initialization and actual encoding remain untested.** `RecordingEngine.Initialize()` immediately allocates live D3D11/DXGI Duplication and NVENC device resources. This phase deliberately avoids calling it, to protect the user's screen/privacy/performance.
4. **Real WASAPI system audio/mic and lip sync remain untested.** Do not record the user's ongoing music without explicit prior informed consent.
5. **Live smoke test must be isolated from production worker and actual user content.** Defer it until the user explicitly agrees to the precise screen/audio scope. Do not treat this read-only preflight as live capture certification.

## Reproduce

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture'
& "$r\Invoke-CaptureHardwarePreflight.ps1"
dotnet run --project "$r\HardwareReadiness.Tests\HardwareReadiness.Tests.csproj" -c Release --no-launch-profile
```

**Scope exclusions:** no edits to OSC UI, OSC transport/NodeAPI, NvContainer, or other agents' pending work. No GitHub Stable push containing unrelated unpublished commits.
