# Phase 6B — Capture Session Outcome Truth (Offline)

**Date:** 2026-10-23
**Branch:** `Stable`
**Scope:** `Project/NVIDIA Capture/EngineCapture/Duluka Capture/` only.

## Safety boundary

This is a **deterministic, no-hardware contract test**. No access to WASAPI, system speakers, microphone, game window, desktop capture, NVENC capture, NvContainer or OSC UI. The user's music may keep playing. No production process was killed, restarted, or updated; only the source and local build/test output were changed. The capture worker on `127.0.0.1:59077` was healthy and idle at the start of work.

## Finding: video-only false PASS on real mux loss

Pre-existing audit evidence (`SessionResultContractTests.vb`, M2-W2) documented 11,517 bytes of mux-pipe loss in a video-only recording whose MP4 looked superficially valid. The earlier `SessionResult.Pass` checked `MuxDroppedBytes = 0` **only if `AudioRequested=True`**, therefore the damaged video-only recording could be marked PASS.

The Phase 6B **red test run before the fix** ran the true production DTO with `--result-contract-only`: **7 passed, 2 failed**. The failing invariants were:
1. Video-only + mux-pipe drops must report `Pass=False`.
2. A session carrying an explicit `ErrorMessage` must report `Pass=False` even when the file exists.

## Fixes

- `RecordingDTOs.vb`: `SessionResult.Pass` now rejects **all** mux-pipe losses, including video-only; also rejects nonempty session error messages. Existing requirements remain: encoded frames, zero NVENC errors, non-partial output, actual file, nonzero file size, a video stream, requested audio stream when applicable, zero audio and mic drops.
- `RecordingDTOs.vb`: added `SessionConfig.RequestsAnyAudio` as a read-only boolean `AudioEnabled OrElse MicEnabled`.
- `CaptureSession.vb`: populate `SessionResult.AudioRequested` using `RequestsAnyAudio`. The old system-only mapping mislabeled **mic-only recordings** as video-only for verification purposes.
- `SessionResultContractTests.vb`: promoted the previously ignored **KNOWN-FAIL** test to a required regression gate, and added checks for error messages, missing files/audio, encoder errors, partial salvage, dropped audio, and all four system/mic intent combinations.
- `Tests/Recording/.../Program.vb`: introduced a targeted `--result-contract-only` switch to run this gate **without** launching the full recording test suite. The full suite has other tests that intentionally use devices/sleeps, and must not be invoked on an active workstation when no-interference testing is requested.

## Verified outcomes

```powershell
$p = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture\Duluka Capture\Tests\Recording\DulukaEngine.Recording.Tests\DulukaEngine.Recording.Tests.vbproj'
dotnet run --project $p -c Release --no-launch-profile -- --result-contract-only
```

**10/10 targeted tests passed, 0 failures, 0 known-fails** after the fix.
`dotnet build 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\NVIDIA Capture.vbproj' -c Release` passed with **0 errors, 3 pre-existing warnings**.

These tests validate the **truthfulness of a session result**, not actual GPU capture restart/recovery. They do not prove the source of mux loss is fixed: a damaged recording is now reported as failed rather than passed. Live capture failure injection and recovery under load remain a separate sandbox acceptance gate.

## Next

1. Isolated real FFmpeg/NVENC failure and Start/Stop recovery test, with explicit output scope and **no system/mic audio capture**. Run only when the user accepts screen capture impact.
2. Real WASAPI audio and long-run sync remain unverified; ask before capturing any real speaker/microphone audio.
3. Notify the user before editing OSC UI, OSC transport, or NvContainer. No such integration is part of Phase 6B.
