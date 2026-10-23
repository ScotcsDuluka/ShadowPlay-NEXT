# Phase 5B — Exact FFmpeg audio mux regression (2026-10-23)

Source tree: `C:\My Project\NVIDIA-Shadowplay`, branch `Stable`.

## Scope

**Capture-only changes.** Did not edit OSC UI, the OSC/NodeAPI integration, NvContainer, the deployed NvCapture service, or other agents' NVIDIA Adapter work. No device audio or personal microphone was recorded. Output artifacts from offline FFmpeg test sources remain in the ignored `NVIDIA Capture/bin/Release/MuxSynthetic` directory.

This is the **offline/synthetic part of Phase 5B**, not certification of real WASAPI capture.

## Implemented

- `EngineCapture/Mux.Tests`: a .NET 10 test executable uses reflection to invoke the **actual nonpublic `LegacyCaptureEngine.BuildMuxArguments`** method from the freshly built `NvCapture.dll`. It runs those exact arguments with FFmpeg (synthetic test video, synthetic system-like stereo 440 Hz WAV, synthetic microphone-like mono 880 Hz WAV) and checks the finalized MP4 via the existing production `CaptureMediaInspector.Inspect` and ffprobe. No duplication of Mux argument-generation logic in the test.
- Matrices: video-only (0 audio streams); system-only (1 AAC); mic-only (1 AAC); system+mic mixed (1 AAC); system+mic separate (2 AAC); both tracks with independent positive and negative timing offsets (2 AAC). Every result validates container video/audio stream counts, AAC codec where applicable, and near-2s duration.
- `NVIDIA Capture.vbproj` excludes nested `EngineCapture/Mux.Tests/**/*.vb` files from production WinForms SDK recursive source glob (same pattern as the existing tests).
- **Direct REST audio-intent correctness:** `CaptureRestApiServer.StartRestOnUi` snapshots system/mic requested flags **after** the FFmpeg/Duluka handler reloads the effective `CaptureSettings` from unified configuration. Before this change, the `systemAudioRequestedAtStart` and `microphoneRequestedAtStart` fields could report stale settings from initial UI startup, instead of the session actually requested. Audio Tracks in the finalized MP4 continue to be reported independently via media evidence.

## Actual validation on AORUS-DESKTOP

| Test | Passing |
| --- | ---: |
| Exact production FFmpeg Mux path with synthetic WAV + MP4 | 25/25 |
| CaptureMediaInspector synthetic MP4 evidence | 21/21 |
| Capture route/capability/HTTP isolation guards | 36/36 |
| Capture routing guards | 13/13 |
| NvContainer capture policy/catalog (read-only test) | 15/15 |
| NodeAPI capture-status shim (read-only test) | 5/5 |
| **Total** | **115/115** |

`NvCapture` Release build: **0 errors, 3 existing warnings**. No deployed worker restart. These totals include old suites rerun, not 115 brand-new tests.

## Remaining Phase 5B acceptance gates

- System WASAPI live capture, selected microphone device, device disconnect/failure, and audio sidecar WAV flush still require a *separate controlled test with user consent*; synthetic sine sources cannot verify those behaviors.
- Timestamp alignment derived from ffprobe is **not** proof of lip-sync, audible signal fidelity, or an accurate per-device source.
- Long-run drift, simultaneous high-load capture, missing WAV fallback at the running-engine level, and recovery after audio errors remain unverified.
- Later OSC UI/NvContainer wiring is **out of scope** and should be announced to the user before any changes to those components.

## Run exact audio mux regression

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project'
$c = "$r\NVIDIA Capture"
dotnet build "$c\NVIDIA Capture.vbproj" -c Release
dotnet run --project "$c\EngineCapture\Mux.Tests\CaptureMux.Tests.vbproj" -c Release --no-launch-profile -- `
  "$c\bin\Release\net10.0-windows10.0.26100.0\NvCapture.dll" `
  "$r\FFmpeg\ffmpeg.exe" "$r\FFmpeg\ffprobe.exe" `
  "$c\bin\Release\MuxSynthetic"
```
