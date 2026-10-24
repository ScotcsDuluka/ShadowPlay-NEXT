# Phase 6E — Detect truncated faststart MP4 by scanning actual video packets

Date: 2026-10-24. Branch: `Stable`. Scope: `Project/NVIDIA Capture` only.

## No-interference test boundary

All files in this phase are generated from FFmpeg's **`lavfi:testsrc2` synthetic video**. Synthetic `sine` inputs from the pre-existing media suite are internal to FFmpeg and contain no microphone or speaker capture. No desktop capture, NVENC device capture, WASAPI, microphone, system loopback, games, OSC, NvContainer, live recording, deployment, restart, or modification to user media was performed. All external tools were invoked below normal host priority; the production `/health` endpoint remained `ready=true, recording=false`.

## Production bug reproduced

An MP4 created using `-movflags +faststart` stores its `moov` track table before the media payload (`mdat`). Cutting the file down to 90%, 65%, 35%, or **12%** of its complete size left ffprobe's **header-only** command returning exit 0, `duration=3.000000`, and a valid video track for **every** truncated file.

At only 12%, ffprobe had metadata advertising **90 video frames**, but a full **`-count_packets`** scan could read just **10**. The original `CaptureMediaInspector.Inspect()` accepted the header-only result and marked it `Verified=True`, even though most video content was missing.

## Fix

`src/[API]/CaptureMediaInspector.vb` now passes `-count_packets` to its existing bounded ffprobe process and queries `nb_frames` and `nb_read_packets` for each video stream. `Inspect` only reports verified when:
- Existing video stream and positive container-duration gates pass;
- FFprobe can provide both the declared video frame total and the count of video packets **actually read**;
- Every video stream has a positive count, and **actual readable packets equal declared frames**.

New reasons for failure: `video_packet_count_mismatch`, `video_frame_total_unavailable`, `video_packet_scan_unavailable`. Results expose `PacketCountChecked`, `VideoPacketsRead` and `VideoFramesDeclared` for diagnostics. The pure `ParseProbeJson` API remains metadata-only by default for existing callers; production `Inspect` explicitly requires packet validation. On scan timeout ffprobe is killed **and reaped**, and the media remains unverified.

**Important limitation:** This checks packet completeness against MP4 metadata. It is **not** full video-frame decode/CRC verification. In particular, bitstream damage within existing packets can still escape this check. Some otherwise valid files without a declared frame total will now return an **unverified** reason rather than falsely asserting completeness.

**Performance limitation:** `-count_packets` performs a bounded **full scan** of the video packets. Long recordings and slow disks could hit the existing 8-second ffprobe timeout, yielding `ffprobe_timeout` instead of a successful verification. Production long-run performance and alternative tail validation are separate follow-up work; this source change was not deployed to the live worker.

## Regression evidence

- Session/MP4 recovery suite (`EngineCapture/SessionRecovery.Tests`): **29/29 checks**, 5 FFmpeg child sessions. Added tests create four deliberately truncated real MP4 files, and confirm each is rejected with `video_packet_count_mismatch` and observed packets fewer than declared frames. Healthy MP4, double stop, invalid encoder, forced abort, and fresh session after failure continue passing.
- Media inspector evidence suite (`EngineCapture/MediaEvidence.Tests`): **25/25 checks**. Added complete vs short packet count JSON, unavailable declared frame count and unreadable packet count tests. Existing synthetic video-only, single-track, and dual-track AAC MP4s remain verified.
- Legacy engine Mux suite (`EngineCapture/Mux.Tests`): **25/25 checks** against actual `NvCapture.dll` FFmpeg command generation and finalized synthetic files.
- Main `NVIDIA Capture.vbproj` Release build: **0 errors, 3 pre-existing warnings**.
- **79/79 checks** across these three suites for this phase.

The user's existing music was never recorded and no devices were queried. Audio and real capture acceptance remain outstanding.

## Reproduction

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project'
dotnet run --project "$r\NVIDIA Capture\EngineCapture\SessionRecovery.Tests\SessionRecovery.Tests.vbproj" -c Release -- "$r\FFmpeg\ffmpeg.exe" "$r\FFmpeg\ffprobe.exe"
& "$r\NVIDIA Capture\EngineCapture\MediaEvidence.Tests\Run-SyntheticAudioEvidence.ps1"
dotnet build "$r\NVIDIA Capture\NVIDIA Capture.vbproj" -c Release -p:Platform=AnyCPU
```

## Open gates

1. Real desktop/video encoding under load, long-running capture and NVENC device-loss recovery are unverified.
2. Real WASAPI system/mic audio and acoustic A/V sync are unverified. Ask the user before recording actual song or microphone content.
3. Once appropriate, test packet-scan cost on long synthetic recordings in an isolated device-free test. Production worker was not redeployed.
4. No changes to OSC UI, OSC/NodeAPI transport, Nvidia Container, or the concurrently edited DX9 Hook.
