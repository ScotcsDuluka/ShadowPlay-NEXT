# Phase 6A — Offline Audio Stability and Recovery

Date: 2026-10-23. Branch: `Stable`. Scope: `Project/NVIDIA Capture` exclusively.

## Purpose and safety

Exercise the **production `DulukaEngine.Audio.AudioTimelineWavSink`** under repeatable stress without touching hardware, user audio, desktop capture, games, OSC UI, OSC transport, NvContainer, production NvCapture, or deployed binaries. A unique test output directory is created under the test assembly's `bin/Release` folder, and deleted in `finally`. All tools were invoked at BelowNormal PowerShell priority. The local NvCapture health API was confirmed healthy and idle.

**No microphone or speaker recording and no real Windows audio devices were accessed.**

## New tests

`EngineCapture/AudioOffline.Tests/AudioStressTests.cs` is included in the existing device-free C# test project and uses its actual production `DulukaEngine.Audio` project reference.

- **96 repeat sessions**: fresh mono or stereo WAV sink per cycle, in-order or shuffled pre-video packets, gap repair, duplicate video anchor, repeated finalization and rejection of post-finalize packets. Verifies exact 48 kHz WAV duration and RIFF/data header counts, zero unexpected drops.
- **24 concurrent-producer sessions**: two tasks submit PCM concurrently; serialized sink commits a valid WAV at a known video timeline end.
- **1 explicit failure/recovery session**: attempts to write a WAV at a path occupied by a directory, checks that output startup refuses it, then creates a fresh valid sink and verifies recovery.
- **1 virtual 30-second timeline**: generates a short real synthetic PCM packet followed by 30 seconds total of timestamp-anchored silence; validates precise WAV sample length without running recording or waiting for 30 seconds.
- **24 producer-vs-finalize race sessions**: one PCM producer and one finalizer run concurrently; the WAV remains valid and bounded, and a second finalize cannot grow it.
- All stress outputs self-clean by removing only a uniquely named subdirectory.

## Measured acceptance

| Test | Result |
| --- | --- |
| Existing offline audio regression | 50/50 checks passed |
| New stress suite | 1,167/1,167 checks passed; 146 sessions |
| New stress elapsed wall clock | ~1.14 seconds in the expanded first run |
| Repeatability | 5 further passes; 730 additional sessions / 5,835 additional stress checks |
| Synthetic media evidence | 21/21 checks passed |
| Production FFmpeg synthetic WAV -> MP4 mux | 25/25 checks passed |
| NvCapture Release build | 0 errors, 3 existing warnings |
| Existing live worker | `ready=true, recording=false`, not restarted |

**The above are offline and synthetic test counts, not independent recording integrations.** The 30-second file uses a *virtual* clock timeline, not a continuous 30-second recording.

## Commands

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture'
dotnet run --project "$r\EngineCapture\AudioOffline.Tests\AudioOffline.Tests.csproj" -c Release --no-launch-profile
dotnet build "$r\NVIDIA Capture.vbproj" -c Release -p:Platform=AnyCPU -p:UseSharedCompilation=false -m:1
```

## Next acceptance gates (not yet done)

1. Video/audio long-term synchronization through the **actual** capture engine and container with real audio streams; cannot infer from synthetic WAV.
2. Hardware-specific WASAPI microphone/system loopback correctness requires a separately approved test; do not collect real personal audio without explicit consent.
3. Heavy game/GPU load, repeated headless real capture start/stop and recovery, disk-full behavior and interruption of live encode must be validated in a strictly isolated sandbox before deployment.
4. OSC UI / OSC transport / NvContainer integration is still **out of scope**. Notify the user before starting or editing these systems.

All unrelated project modifications, worktrees and branches were left untouched.
