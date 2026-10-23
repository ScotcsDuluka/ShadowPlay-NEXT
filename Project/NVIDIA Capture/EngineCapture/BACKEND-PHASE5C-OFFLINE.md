# Phase 5C — Non-invasive audio timeline regression (2026-10-23)

## Safety and execution scope

This run deliberately **did not start NvCapture recording**, access microphone or speaker devices, play sound, capture the desktop, use WASAPI, contact OSC/NvContainer, deploy binaries, or restart existing services. All synthetic PCM data was generated in memory and written only under an ignored test build directory. Tests/builds used below-normal PowerShell process priority. The production NvCapture REST health endpoint remained `ready=true, recording=false`.

## Actual bug detected and fixed

`EngineCapture/Duluka Capture/Audio/DulukaEngine.Audio/AudioTimelineWavSink.cs`, `WriteSilenceLocked`:

**Before:** `n` contained the precise remaining silence bytes (sometimes fewer than 65,536), but the code passed the full 65,536-byte `zeros` array to `AudioPacket`. `AudioWavSink.Write` writes and counts all bytes in `packet.Data`, not only `AudioPacket.Frames`, so an undersized chunk was written as 65,536 bytes, artificially extending the resulting WAV timeline.

**Measured before fix:** A synthetic recording with a 0.5-second target produced a **0.882667-second** WAV, verified by `ffprobe`. This is an audio-timeline bug, not just a misleading diagnostic.

**After:** For a partial silence chunk, pass a zero-filled buffer of **exactly `n` bytes**; reuse the full 65,536-byte block for complete chunks. This avoids overshooting WAV duration while preserving bounded chunking.

## Non-invasive permanent regression suite

Run:

```powershell
dotnet run --project "C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Capture\EngineCapture\AudioOffline.Tests\AudioOffline.Tests.csproj" -c Release
```

The suite references the **real `DulukaEngine.Audio` project** and uses only `AudioTimelineWavSink` with synthetic, deterministic `AudioPacket`s. It tests:

- Stereo initial silence, real samples, and trailing silence to a 0.500-second WAV.
- Microphone-style mono packets arriving **before video alignment** and **out of timestamp order**, proving they sort and preserve internal gaps and end silence in a 0.400-second WAV.
- Audio started before the video anchor, clipped to the video timeline and filled to 0.200 seconds.
- Correct PCM WAV RIFF headers, sample rate, channel count, 16-bit format, data-size fields, and accurate media duration within one sample.
- No packets/no source → no fictional audio WAV.
- Completion called twice without duplicating output.

**Results:** **27/27 offline checks PASS**. `ffprobe` independently verified PCM durations of **0.500000, 0.400000, and 0.200000 seconds** for the three files.

`DulukaEngine.Audio` Release build **0 errors/0 warnings**; overall `NvCapture` Release build **0 errors/3 pre-existing warnings**.

## Explicit limitations

- No microphone, loopback/system audio, sound-card driver or WASAPI device is exercised; therefore none is claimed working.
- Container timestamp and synthetic sample length evidence do not certify audible lipsync, real device synchronization, long-run drift, recovery from device disconnection, or gameplay performance.
- Tests are limited to tiny audio buffers and run without opening production device handles.
- Source changes are **not deployed into the running capture worker**. Local application build output was rebuilt but the existing production executable/process was not restarted.
- The user's other local Stable worktrees and in-progress OSC, NvContainer, DX9 files are outside the test scope.
