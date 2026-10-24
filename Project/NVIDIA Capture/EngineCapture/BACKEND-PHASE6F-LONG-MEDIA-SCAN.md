# Phase 6F — Bounded MP4 packet-scan performance, no capture devices

Date: 2026-10-24. Branch: `Stable`. Scope: `Project/NVIDIA Capture`.

## Safety

The user's desktop/music/microphone/speakers are never read or recorded. Only synthetic FFmpeg `lavfi:testsrc2` H.264 video is used. No D3D11, DXGI, NVENC, NvCapture production worker, OSC, NvContainer, or game is touched. A unique per-run `bin/Release/ScanPerf_<GUID>` folder is created and always deleted. The script refuses to generate more than its configured disk budget (default 128 MiB), and test tools are run with below-normal PowerShell process priority.

## Observation

The Phase 6E `CaptureMediaInspector.Inspect` now asks ffprobe to count packets rather than trusting the MP4 `moov` table alone. This is a bounded full-file scan with an 8s timeout. The question was whether the extra scanning cost was prohibitive on moderately long recordings.

Manual initial reference measurements with a complex 640x360 repeated synthetic video (external ffprobe command alone, not including .NET or inspector):
- 1 minute, 14,656,250 bytes: ffprobe 64 ms, 1,800 packets.
- 5 minutes, 73,277,770 bytes: ffprobe 110 ms, 9,000 packets.
- 10 minutes, 146,554,655 bytes: ffprobe 171 ms, 18,000 packets.
A separate 1-hour virtual black-video test was only about 2 MiB; its 251 ms packet scan **does not** represent a 1-hour real capture.

## Reproducible benchmark script

`EngineCapture/MediaEvidence.Tests/Invoke-LongMediaScan.ps1`:

- Builds a 4s 320x180 testsrc2 H.264 seed (`libx264`, CPU only).
- Loops the source with `-c:v copy` into 1-, 5-, and 10-minute **virtual-duration MP4 files**; the source is not encoded again at live rate.
- Invokes the **real production inspector linked into the test assembly** using its new `--perf-only` entry. The measured `scan_ms` is clocked around `CaptureMediaInspector.Inspect` *inside the .NET test process*; the .NET launcher/build overhead is explicitly excluded.
- Independently verifies declared vs actually read packet counts.
- Limits generated disk consumption and deletes the test-only sandbox in a `finally` block.

Measured actual inspector results (first repeat of the bounded harness):

| Virtual video duration | MP4 size | Counted video packets | Measured Inspect() |
| --- | ---: | ---: | ---: |
| 1 minute | 5,597,787 bytes | 1,800 | 222 ms |
| 5 minutes | 27,985,539 bytes | 9,000 | 152 ms |
| 10 minutes | 55,970,235 bytes | 18,000 | 412 ms |

All three cases passed and remained under the inspector's existing **8,000 ms** timeout. Variation in timing is expected from OS file caching/scheduling. The benchmark is not a guarantee for multi-GB, high-bitrate, game-encoded recordings on a busy disk.

The script also exercised its 16 MiB budget guard against a projected oversized 20-minute fixture: **guard rejected correctly**. Checked that **no `ScanPerf_*` directories remained** after successful and rejected runs. The initial manual probe sandbox was removed separately (9 synthetic files, 238,790,477 bytes).

`EngineCapture/MediaEvidence.Tests/Program.vb` was extended with a standalone `--perf-only` mode. All existing media-evidence checks remain in the default code path: **18/18 pure/parser checks passed** (the 7 physical synthetic media checks are independently covered by the existing 25/25 suite).

## Run

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture'
& "$r\EngineCapture\MediaEvidence.Tests\Invoke-LongMediaScan.ps1" -Minutes 1,5,10 -MaxGeneratedMiB 128
```

## Unresolved limits

1. Full packet scans on multi-GB files or slow/highly loaded storage could exceed 8s. The inspector intentionally fails closed with `ffprobe_timeout` instead of pretending full verification.
2. FFprobe packet counts do not decode every frame. Corrupted bitstreams with all container packets present may still appear structurally complete; this needs a separate controlled corruption test (Phase 6G).
3. Real WASAPI, game capture, GPU device loss and physical A/V sync remain unverified. Do not capture the user's music or screen without permission.
