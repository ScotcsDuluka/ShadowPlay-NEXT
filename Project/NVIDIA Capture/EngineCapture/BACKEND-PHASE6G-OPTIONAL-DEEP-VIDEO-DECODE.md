# Phase 6G — Optional full bitstream decode of finalized video

Date: 2026-10-24. Branch: `Stable`. Scope: `Project/NVIDIA Capture` only.

## Safety boundary

No real audio or video was accessed: all test input video was FFmpeg's internal `testsrc2` generator. The controlled FFmpeg decoder outputs **only to the null muxer** (no playback, user media files, desktop capture, WASAPI, microphone, speaker, D3D11, or NVENC). Test tools use below-normal process priority. The running production NvCapture worker was never restarted, modified, or used to record.

## Newly demonstrated limitation in Phase 6E

On a generated 5-second H.264 faststart MP4 of 1,217,761 bytes, flip 256 bytes within its video payload at 30%, 50%, and 70% of the file. The MP4 track header remains intact and ffprobe `-count_packets` reports **150/150 video packets**, exiting zero without stderr errors **on all three damaged files**. Thus the Phase 6E structural `CaptureMediaInspector.Inspect` rightly checks file completeness, but **cannot** establish video bitstream correctness. A healthy file with complete packets is not necessarily decodable.

The same three damaged files, tested with `ffmpeg -v error -xerror -i ... -an -f null -`, failed immediately with H.264 decoder errors (e.g. invalid syntax and macroblock decoding errors). Healthy synthetic video decoded with exit 0. This verifies a distinct integrity layer, not an extension of packet counting.

## Production API addition

`src/[API]/CaptureMediaInspector.vb` adds a **separate, opt-in** method:

```vb
CaptureMediaInspector.InspectWithVideoDecode(filePath, ffprobePath, ffmpegPath)
```

It first runs the existing strict packet scan. If and only if structurally verified, it starts the explicitly supplied FFmpeg binary with `-nostdin -loglevel error -xerror`, maps the first video stream, ignores audio, and sends decoded frames to the **null output**. The subprocess has a **15-second timeout** and is reaped on timeout. FFmpeg exit/error evidence determines `Evidence.Verified`, `VideoDecodeChecked`, `VideoDecodeVerified` and a reason code (`video_decode_failed`, `ffmpeg_decode_timeout`, `ffmpeg_decode_tool_missing`, etc.). Media inputs are read-only.

**Important:** The regular `Inspect()` path and REST `/record/stop` are **unchanged**. Doing a full decode on each stop would add substantial time and CPU load for long captures. This optional method is not automatically invoked during normal recording. It checks **video only**, not system/microphone audio correctness or acoustic A/V sync. A decoder timeout returns **unverified** rather than a false success.

## Permanent tests and results

`EngineCapture/MediaEvidence.Tests/Invoke-SyntheticDecodeIntegrity.ps1` generates the controlled 5-second H.264 fixture, makes three 256-byte corruption variants, and uses the real linked `CaptureMediaInspector` with `--decode-only` mode:

- Healthy file: structural + deep video decode **2/2**.
- Missing decoder path: structural passes, optional decode fails closed **2/2**.
- Three corrupt files: structural packet scan succeeds on intact counts but full decode fails, **2/2 each**.

**10/10 checks, all 5 controlled inputs passed.** Source and outputs are deleted from a uniquely generated test folder in `finally`.

Regression on modified source:
- Default MediaEvidence parser: **18/18**.
- Synthetic FFmpeg Session Recovery / packet-truncation guard: **29/29**.
- Existing audio/video mux regression: **25/25**.
- Deep bitstream tests: **10/10**.
- **82/82 checks across these runs**.
- `NvCapture` Release build: **0 errors**, 3 existing warnings.
- No real mic/system audio or real video capture was performed.

## Reproduction

```powershell
$r = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture'
& "$r\EngineCapture\MediaEvidence.Tests\Invoke-SyntheticDecodeIntegrity.ps1"
dotnet build "$r\NVIDIA Capture.vbproj" -c Release -p:Platform=AnyCPU
```

## Open acceptance gates

1. Deep decode is intentionally optional and may be too slow on long recordings; do not invoke synchronously from the live UI stop path without asynchronous/background design and resource controls.
2. Audio decoding, true audio source identity, acoustic A/V sync, real DXGI/NVENC device loss, and long-running gameplay stutter remain untested.
3. No OSC UI, OSC transport or NvContainer edits were made. Notify the user before any such work.
