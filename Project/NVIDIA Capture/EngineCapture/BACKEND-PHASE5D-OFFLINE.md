# Phase 5D — Audio Timeline Hardening, Offline Only

Date: 2026-10-23. Branch: `Stable`.

## Safety boundary

All tests and fixes are limited to `Project/NVIDIA Capture`. We did **not** access the microphone, speaker/loopback devices, WASAPI, record the desktop, launch a game, restart NvCapture, deploy output, or modify OSC or NvContainer. Offline tests were run at **BelowNormal** PowerShell process priority; only synthetic PCM and generated WAV/MP4 fixtures were involved. The live worker on `127.0.0.1:59077` was repeatedly healthy and idle.

## Issues demonstrated before fixing and resolutions

1. **Truncated PCM packet shortens WAV:** `AudioTimelineWavSink.WriteAlignedLocked` previously advanced `_expectedPts100ns` to the packet's declared ending timestamp even if `packet.Data` contained fewer frames than `packet.Frames`. This caused underlength audio, a silent hole without corresponding PCM, or partial PCM frame bytes. The production sink now copies only whole available PCM frames and fills any missing part of the declared packet interval with complete silence samples before advancing its clock.
2. **Duplicate video T0 replays old timeline:** `SetVideoStart` always reset `_expectedPts100ns`; a repeated call after audio had been serialized could add duplicate initial silence and inflate WAV duration. The first positive video anchor now remains authoritative; subsequent callbacks do not rewind the sample cursor.
3. **Unreported dropped prealignment audio:** the 16 MiB packet backlog silently discarded packets above the cap without contributing to `BytesDropped`. Added `_pendingDroppedBytes` accounting and counted unanchored queued bytes as lost when `Complete` runs without a video T0. `Complete` is idempotent, forbids subsequent writes/anchors; `Dispose` synchronizes teardown and clears the pending buffer.

## Tests

The permanent `EngineCapture/AudioOffline.Tests/Program.cs` regression suite uses the **actual `DulukaEngine.Audio` project**, synthetic packets, and no hardware. It now contains:

- The previous PCM duration/header, initial silence, out-of-order pending packet, gap repair, and video-start clip tests.
- New short-PCM-frame, odd-byte PCM, and duplicated-video-anchor tests.
- New saturation of the 16 MiB pre-video backlog with loss-accounting checks; when there is no video anchor, finalized WAV has only a RIFF header, with no invented audio samples.

**50/50 offline audio regression checks passed**. The short-packet and buffer-overflow tests were first run against the unfixed code and failed as expected, then passed after the fixes.

Other independently rerun suites:
- Media evidence synthetic MP4: **21/21**.
- Production FFmpeg Mux arguments + synthesized WAVs: **25/25**.

Total for these suites: **96/96**. `NvCapture` Release build: **0 errors, 3 existing warnings** (obsolete `HttpWebRequest` and a `ByRef` initialization warning). These are not new warnings from this change. The build happened on the source tree, **not** a deployed/restarted production worker.

## Still open / limitations

- Real system loopback and microphone WASAPI capture have not been exercised because that can record private ambient audio and is not needed for the offline scope.
- No actual acoustic lip-sync, sustained recording drift, or game-performance certification.
- A detected packet loss is now reported accurately; no recovery claim is made for previously discarded samples.
- Other worktrees, in-flight commits, and unrelated Agent/OSC/NvContainer code remain untouched.

## Reproduction

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture'
dotnet run --project "$r\EngineCapture\AudioOffline.Tests\AudioOffline.Tests.csproj" -c Release --no-launch-profile
dotnet build "$r\NVIDIA Capture.vbproj" -c Release -p:Platform=AnyCPU -p:UseSharedCompilation=false
```
