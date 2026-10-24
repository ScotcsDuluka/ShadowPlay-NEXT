# Phase 6H — Complete non-invasive Capture Offline Release Gate

Date: 2026-10-24. Branch: `Stable`. Scope: `Project/NVIDIA Capture` exclusively.

## Implementation

New `EngineCapture/Invoke-CaptureOfflineGate.ps1` runs the existing device-free tests in a reproducible order. The script:

1. Checks the **existing NvCapture production worker** on `http://127.0.0.1:59077/health` reports `ready=true, recording=false` before any work. If unavailable, busy or recording, it **refuses** to run the full offline test gate.
2. Uses a **BelowNormal priority** PowerShell process and executes builds and tests **serially** to avoid resource contention.
3. Builds the actual main `NVIDIA Capture.vbproj` Release, without deploying or restarting the worker.
4. Runs the production-linked synthetic audio timeline, RecordingEngine recovery, FFmpeg lifecycle, media evidence, mux, optional deep bitstream decode, and capped packet-scan performance suites.
5. Stops immediately on test failure. Each synthetic image/audio test uses generated media, never real hardware devices.
6. Checks the worker is still healthy and idle at the end.

The smoke tests that use FFmpeg `gdigrab`, DXGI, NVENC, WASAPI or a real recording REST request are deliberately excluded.

## Live verification on AORUS-DESKTOP

The exact combined gate ran to completion and printed:

```text
GATE_PASS MainReleaseBuild
GATE_PASS AudioOffline
GATE_PASS RecordingRecovery
GATE_PASS SessionRecovery
GATE_PASS SyntheticMediaEvidence
GATE_PASS SyntheticMux
GATE_PASS SyntheticDeepDecode
GATE_PASS BoundedScanPerformance
CAPTURE_OFFLINE_RELEASE_GATE_PASS stages=8 no_live_devices=true no_deployment=true
```

| Acceptance gate | Result |
| --- | ---: |
| Audio Timeline & Stress | **50 + 1,167 = 1,217** passing checks |
| RecordingEngine failed-state Recovery | **15** passing checks |
| Synthetic FFmpeg Session stop/restart/truncation | **29** passing checks |
| Full Media Evidence (synthetic AV tracks and parsing) | **25** passing checks |
| Actual Capture Mux argument regression, synthetic inputs | **25** passing checks |
| Optional video bitstream full-decode (healthy/missing tool/corrupt) | **10** passing checks |
| Bounded long-media packet scan, virtual 1 and 5 min | **2** passing cases |
| Main NvCapture Release build | **0 errors**, 3 existing warnings |
| **Total counted synthetic/offline checks and cases** | **1,323 / 1,323** |

The long packet scan reported 1m = 5,597,787 bytes, **303 ms**, 1,800/1,800 packets; 5m = 27,985,539 bytes, **659 ms**, 9,000/9,000 packets. Performance depends on disk cache and CPU load; neither result guarantees behavior for multi-GB captures.

Total gate runtime was approximately **223 seconds on this run**, dominated by sequential .NET builds. This is a measured completed run, not a future work estimate.

## Reproduction

```powershell
& "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture\Invoke-CaptureOfflineGate.ps1"
```

The test harness generates some ignored synthetic media under `bin/Release`. Temporary per-run `ScanPerf_*` and `DecodeIntegrity_*` directories are deleted by their own `finally` blocks. No game files, actual user captures, microphone recordings, or installed application files were removed or overwritten.

## Not certified

- Hardware WASAPI loopback and microphone capture, audio-driver selection, actual acoustic A/V sync;
- Real DXGI/NVENC encode under load, real in-game performance, GPU driver device-loss recovery, game-hook lifecycle;
- Behavior of packet integrity verification on multi-GB recordings and slow saturated drives;
- Actual REST start/stop when installed, NvContainer or OSC UI integration.

These need separately controlled acceptance tests. The user must be notified before actual desktop or audio recording, and before changing OSC UI/OSC transport/NvContainer. All unrelated agent worktrees and dirty changes are out of scope.
