# Phase 7D — Real FFmpeg NVENC with entirely synthetic video (no screen capture)

Date: 2026-10-24. Scope: `Project/NVIDIA Capture` only. Local `Stable` branch.

## Safety boundary

The connected AORUS-DESKTOP continued playing audio normally. **Neither system audio nor microphone was opened**, no screen pixels were sampled, no `DuplicateOutput` / `AcquireNextFrame` was called, and no window/game/desktop recording was started. The installed NvCapture worker was not restarted or updated. Worker `http://127.0.0.1:59077/health` was checked before each synthetic hardware invocation and remained `ok=true, ready=true, recording=false` afterward.

This step **does initialize the real hardware NVENC encoder via the bundled FFmpeg h264_nvenc backend**, unlike the preceding read-only DXGI inventories. Its inputs are `lavfi`-generated textures/frames, **not** existing screens or user media. Only test-owned low-resolution MP4 files within a uniquely named `bin/Release/NvencSynthetic_<GUID>` folder are produced, and they are deleted in `finally`. Tools run at below-normal priority with bounded subprocess runtimes. Disk guard refuses if free space is under 5 GiB.

## What is implemented

`EngineCapture/Invoke-IsolatedNvencSyntheticSmoke.ps1`:
1. Requires an existing, healthy and idle production NvCapture worker, bundled `ffmpeg.exe` and `ffprobe.exe`, and the already compiled production-linked `CaptureMediaInspector.Tests.dll`.
2. Generates a 320×180 black video at 10 FPS for 10 frames, with `-an -c:v h264_nvenc -preset p4 -b:v 500k`.
3. Generates a 640×360 animated `testsrc2` video at 15 FPS for 15 frames, using the same **real hardware H.264 NVENC** path.
4. Validates process exit, nonempty finalized MP4, **exact declared and readable H.264 packet count**, exactly one video stream and zero audio streams using FFprobe.
5. Invokes the existing source-linked `CaptureMediaInspector.InspectWithVideoDecode` path in `MediaEvidence.Tests` to scan packets and **fully decode the resulting hardware-encoded H.264 video**, verifying that both files are playable and not merely header-valid.
6. Checks worker idleness after each case, and removes all generated files after success or failure.

The bounded child-process helper uses `System.Diagnostics.ProcessStartInfo` and asynchronous standard output/error reads. This is necessary because Windows PowerShell 5.1 `Start-Process -RedirectStandardOutput` sometimes supplied **a null ExitCode even after a clean process exit**: an early harness prototype rejected an actual passing encoder run due to that test-harness issue. The issue was corrected; no production code had to be changed.

## Results

- Independent initial low-impact null-output NVENC smoke: **10/10 frames successfully passed to real hardware `h264_nvenc`**, FFmpeg exit 0, no generated video file, no screen access.
- Full two-profile harness: **2/2 independent hardware encode → MP4 → ffprobe → full video decode sessions passed**.
  - Black H.264: 10/10 frames, 1,337-byte finalized MP4, zero audio.
  - Animated H.264: 15/15 frames, 121,388-byte finalized MP4, zero audio.
- Full harness repeated **three more times**: **6/6 additional sessions passed**, total **8/8 synthetic hardware NVENC MP4 sessions**.
- Test-only unique directories remaining after repeats: **0**.
- Existing production worker: `ready=true, recording=false`, unchanged.
- Main `NvCapture` Release build: 0 errors and the existing 3 warnings (where reported).

## What this does NOT certify

- This uses **FFmpeg's own H.264 NVENC integration**, not the Duluka `NvencEncoderBackend` implementation. It proves the OS/driver/GPU combination can execute at least one small H.264 NVENC workload under these test conditions, not that the in-app encoder is correct.
- FFmpeg `-gpu` was not pinned and NVIDIA's `nvidia-smi` index is not the same as a DXGI adapter index. The test does **not** prove which physical GTX 1080 Ti performed encoding, or that it is the one attached to the primary monitor.
- No `DdagrabBackend.Initialize`, DXGI Desktop Duplication or shared texture interoperability has been exercised. Source-level Phase 7B adapter LUID guard is not yet deployed to the running worker.
- Real desktop/video capture, continuous frames under GPU load, real hardware device-loss recovery, system loopback/microphone sampling, physical A/V sync and gameplay frame pacing remain **unverified**.
- A separate explicitly approved test is required before reading real desktop pixels or recording actual music and voice.

## Reproduce

```powershell
& 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture\Invoke-IsolatedNvencSyntheticSmoke.ps1'
```

No OSC UI, OSC transport, NodeAPI or NvContainer changes were made. No user output captures or game processes were modified. Commit changes under Capture only and do not push unrelated unpublished Stable commits.
