# ShadowPlay NEXT — Controlled Recording Readiness Gate

Date: 2026-10-24 (Thailand time; check ran approximately 18:35–18:45)
Workspace: `C:\My Project\NVIDIA-Shadowplay`
Branch: `Stable` (local was behind `origin/Stable` by 4 commits at preflight; repository has other uncommitted edits, do not pull/reset/push uncoordinated).

## Goal and boundary

Evaluate whether the current machine is ready for controlled recording **without sampling the user's real desktop, windows, music, microphone, or speakers**. This phase deliberately sends NO POST to `/record/start`, `/record/stop`, or `/Record/Enable`, and does not alter the recording gateway, audio settings, selected display, or user videos. Production modules are not redeployed/restarted because no production source was changed during this phase.

## Actual live worker and monitor status

- NvCapture GET `127.0.0.1:59077/health`: `ready=true, recording=false`.
- GET `/state`: `actualSessionBackend=""`, `recording=false`, `lastFile=""`.
- Container `127.0.0.1:59012/v1/capture/status`: `availability=Available`, `runtimeReady=true`, `recording=false`.
- NodeAPI legacy `/ShadowPlay/v.1.0/Record/Running`: `running=false`.
- NodeAPI `/DulukaCapture/v.1.0/status`: `ready=true`, `recording=false`.
- Displays now enumerated: two attached, not the earlier three. Selected output index 0 = primary 1680x1050; second attached output 1280x800. Re-enumerate before any future live test.
- Production branch uses Duluka / `dxgi_desktop_duplication` / NVENC_H264 as configured; this is a route declaration, not proof of live completed MP4.
- Container gate `captureRuntime.allowRecordCommands` was absent in current nvcontainer.json (defaults to disabled per Container contract). `testMode.enabled=false`. Do not bypass via direct worker write API to shortcut gateway authorization.

## Tests executed and independently observed

1. `EngineCapture/Invoke-IsolatedNvencSyntheticSmoke.ps1`: **2/2 synthetic NVENC H.264 → MP4 → FFprobe packets → full video-decode profiles passed**. Black 320x180/10 frames (1,337 bytes); animated testsrc2 640x360/15 frames (121,388 bytes). Both video-only. Isolated test-owned files deleted; no user screen/audio and no production NvCapture session.
2. `EngineCapture/SessionRecovery.Tests`: **29/29 checks, 5 synthetic FFmpeg sessions passed** for graceful stop, invalid-encoder failure, bounded kill, recovery after failure, and truncated 90%/65%/35%/12% MP4 rejection by actual packet scan. No device capture.
3. `EngineCapture/Invoke-CaptureHardwarePreflight.ps1`: **PASS**. HardwareReadiness dry-run 24/24; read-only DXGI output inventory valid. 2 NVIDIA GTX 1080 Ti cards with driver 582.78; 4 NVIDIA-vendor DXGI adapter entries and Microsoft software adapter; first NVIDIA LUID=00000000:00013131 and two attached outputs. Synthetic disk estimator 75,000,000 bytes for 15s@20Mb/s with two-copy peak; test volume approximately 31.31 GiB free at run time.
4. NodeAPI Record notification/Actual-route tests: **9/9 PASS**. Desired toggle and unsuccessful stop do not emit a "saved" notification, verified success test emits one saved notification.
5. Live cross-component read-only Record Idle Contract: **9/9 PASS**. `NvCapture /state`, Container, NodeAPI legacy Record/Running, and OSC Capture/State agree on Idle. Zero `NvencSynthetic_*` output folders left.

## Important outstanding gates — do NOT call this full production E2E certified

- A real, end-to-end **Duluka capture** session (`DXGI Desktop Duplication → native NVIDIA NVENC → Stop → finalized MP4 → CEF toast`) has NOT been performed in this run. Synthetic FFmpeg NVENC is a different path.
- The primary monitor is actively used; no blank, privacy-cleared test scene was established. A real desktop recording would sample its pixels. The current Record Settings API reports Custom, In-game, 60 FPS, 107,000,000 bps. This high bitrate is not the bounded 20Mb/s synthetic preflight profile.
- Audio behavior for an actual native session must be explicitly set to both `SystemAudioCapture=false` and `MicCapture=false`, and verified in effective CaptureSettings; avoid recording currently playing music/voice. This was NOT changed in this phase.
- An operator-controlled 5–15 second live test must use a pre-cleared neutral scene on the selected output and a known isolated disposable MP4 path. Stop after a hard maximum duration and verify actual worker state. Do not record private apps or switch display settings behind the user's back.
- Record gateway is opt-in; preserve Container's explicit authorization and current test mode. Do not enable it without a scoped real recording test and operator-approved boundary.
- For a true success result, require actual native SessionResult Pass, readable output file, stream count, actual packet count, full decode, no audio when audio off, confirmed `Record/Running=false`, and exactly one saved toast. On any failure, no saved toast and no orphan capture process.
- Current screen contents and private media were not inspected or sampled. The user did not supply a privacy-cleared test scene in this turn.

## Next controlled test acceptance sequence

1. User prepares a neutral, privacy-cleared fullscreen test image/window on the exact selected output; pauses or excludes all audio; confirms desired output and no private content.
2. Read-only preflight again: worker Idle, selected LUID and output, disk and GPU, and no competing recording.
3. Under a scoped approved test setting only, start a single short native capture; watchdog fails closed and ensures stop; NEVER blindly retry a timed-out start/stop.
4. Verify real MP4 using ffprobe packet scan and decode in test-only output, compare frame timestamps; check Native SessionResult and status transitions.
5. Confirm UI doesn't claim success without file and that Record returns Idle at Capture, Container, NodeAPI and OSC. Restore settings and preserve logs/backups.

No source edits, no commit/push, no production Deploy/Restart were needed for this read-only/synthetic-validation phase.
