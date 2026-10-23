# Phase 5A — Audio media evidence and honest Native REST start

Branch: `Stable`. Date: 2026-10-23. Work performed directly under `C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture`.

## Changes

- `src/[API]/CaptureMediaInspector.vb`: **read-only**, bounded `ffprobe` against a **finalized** MP4. Returns structured evidence: actual video-stream count, actual audio-stream count, audio codec and channel count, format duration, video start, first-audio start, start offset in milliseconds, maximum audio/video end drift in milliseconds where both stream durations are known, and `verified/reason`. Handles AAC negative priming timestamps. Audio/video offsets are **container timestamp estimates**, not an acoustic lipsync measurement or proof of system/microphone audio capture.
- `CaptureRestApiServer.vb`: Direct REST `POST /record/stop` now includes `media`, `systemAudioRequestedAtStart` and `microphoneRequestedAtStart`. **Configured audio** is not confused with audio actually present in the output file. Missing or failed ffprobe returns `media.verified=false` and a reason; never fabricates tracks. Existing `stopped` semantics are preserved.
- **Native Start correctness**: Direct REST now waits for `RecordingEngine.GetStatus().CurrentSessionId` to become nonempty before declaring `started:true`. Previously the background task could be queued during NVENC setup, and a 2-second client record produced only 0.63s of MP4 while REST incorrectly claimed immediate start. The 10-second polling wait yields to the UI scheduler; it rejects a completed/failed session. If the worker times out, client must check `GET /state` rather than blindly retry.
- `EngineCapture/MediaEvidence.Tests`: 14 pure parser/error tests and 7 checks on real synthetic MP4s from FFmpeg lavfi (video-only, system-style sine source, system+mic-style two independent sine sources). No actual mic, speaker capture, game capture, or personal audio is used in these synthetic files.
- `EngineCapture/IsolatedTest/Invoke-CaptureSmoke.ps1`: checks REST `media` on an actual isolated video-only capture, including a duration guard of requested seconds minus 0.75 seconds (short runs only). All synthetic and isolated outputs stay under ignored `bin/Release` directories.

## Verified on AORUS-DESKTOP

- **Automated regression**: Media Evidence (21) + RouteGuard (36) + Routing (13) + Container Catalog (15) + NodeAPI (5) = **90/90 passing**.
- `dotnet build NVIDIA Capture.vbproj -c Release`: **0 errors**, 3 preexisting warnings. No changes under `NVIDIA NodeAPI/osc/`.
- Synthetic: mute.mp4 (no audio), single-system.mp4 (1 AAC audio stream), dual-system-mic.mp4 (2 AAC audio streams). These are **synthetic tracks only**, not proof of real WASAPI capture.
- Isolated REST FFmpeg/gdigrab/libx264 video-only: 2-second request produced **2.1s** MP4, H.264, 2960x1082, 579,996 bytes; `media.videoStreams=1`, `media.audioStreams=0`.
- Isolated REST Duluka DXGI/NVENC video-only: 2-second request initially produced **0.633346s** MP4 with a misleading immediate `started:true`. After waiting for actual `CurrentSessionId` in REST, same 2-second script produced **1.983373s**, 1680x1050 H.264, 12,398,098 bytes and `media.videoStreams=1, audioStreams=0`.
- Production Stable worker bound to `127.0.0.1:59077` was not restarted or deployed; test process used loopback `:59477` and the isolated root, then exited.

## Not yet certified

1. **System audio and microphone** were not recorded live in this phase. Their actual WASAPI capture, sidecar WAV finalization and FFmpeg/Duluka A/V mux acceptance require controlled, explicit audio-device testing.
2. Dual AAC stream synthetic test checks **stream count and container timing**, not perceptual A/V sync, per-device identity, drift over a long session, or audio data integrity under load.
3. The 10-second native session-ready wait is a compatibility change for **Direct REST only**; the older TCP/event-bus path can still announce an earlier state and needs review.
4. Long recordings, repeated Start/Stop under load and audio-failure fallback need future tests.
5. `Stable` checkout contains concurrent uncommitted D3D9 Adapter work by another task. That work is intentionally excluded from this phase's commit and must not be reset or staged.

## Test commands

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture'
& "$r\EngineCapture\MediaEvidence.Tests\Run-SyntheticAudioEvidence.ps1"
dotnet build "$r\NVIDIA Capture.vbproj" -c Release
& "$r\EngineCapture\IsolatedTest\Invoke-CaptureSmoke.ps1" -RunSmoke -Engine FFmpeg -Seconds 2
& "$r\EngineCapture\IsolatedTest\Invoke-CaptureSmoke.ps1" -RunSmoke -Engine Duluka -Seconds 2
```
