# Capture Backend Phase 3 — Direct REST FFmpeg + Binary Capabilities (Sandbox)

## Scope / deployment

Source branch: `feature/capture-engine-router-20261022`.
Worktree: `C:\My Project\_ShadowPlay-Capture-Engine-Sandbox`.

Changes are source-only. The original `Stable` checkout and deployed processes remain untouched. No `NVIDIA NodeAPI/osc/` files were changed. No live recording command was sent to the already-running Stable NvCapture worker (port 59077) or NvContainer (59012).

## Behavior

- `FFmpegRuntimeProbe.vb` safely runs `ffmpeg.exe -version/-filters/-devices/-encoders` and parses *advertised binary features*. Only enumerated, whitelisted names are used to gate Direct REST start. This **does not verify NVIDIA/AMD/Intel GPU-driver availability**. It does not start screen capture. GET `/engines` triggers lazy/background binary scan and exposes `ffmpegBinary` with `hardwareEncoderVerified:false`. First query can return `ffmpeg_probe_pending`.
- `POST /record/start` supports the selected `Duluka` or `FFmpeg` backend. It rejects OBS, installed NVIDIA and mismatched/unsupported capture-API/encoder combinations; it probes the FFmpeg binary before admitting an FFmpeg start. The HTTP thread marshals recording work onto the pre-existing WinForms/Engine session thread, not a second independent capture instance.
- `POST /record/stop` stops the **actual current session owner** (Duluka/FFmpeg), independent of selected mode changed since start. The installed NVIDIA ShadowPlay hotkey route remains event-bus only.
- Direct HTTP commands serialize with a non-blocking SemaphoreSlim gate; concurrent commands receive `direct_rest_command_busy`. Starts return `started:bool,backend,outputPath,error`; stops return `stopped:bool,backend,file,error`. A reported `started:true` requires observing a real active task or FFmpeg Recording state; a reported `stopped:true` requires successful completion or an FFmpeg output file.
- `LegacyCaptureEngine.IsRecordingLifecycleBusy` includes the **Detecting** reservation as well as Recording/Stopping/Muxing, preventing an overlapping start from destroying a spawning FFmpeg child. `IsRecordingLifecycleActive` remains separate so telemetry does not call Detecting a recording.
- `CaptureHttpRequestReader.vb` uses bounded incremental HTTP parsing (64 KiB maximum) and preserves fragmented UTF-8 request bodies. Tests cover split reads, oversized and truncated bodies and body-less GETs.

## Known limitations — do not mistake Phase 3 for production certification

1. REST Direct Start/Stop handlers compiled; **not end-to-end tested against a launched Phase 3 NvCapture instance**. The live Stable NvCapture occupies 59077, so no second worker was launched and no live game/desktop recording was attempted.
2. API `/health.ready` is the historical Native readiness flag. `/engines.ffmpegBinary` is metadata about a binary, not proof that NVENC or DXGI can initialize at session start. Hardware/driver probe and capture video output acceptance still require a controlled runtime test.
3. Direct REST start/status may return `start_status_pending_timeout` after 15 seconds (or `stop_status_pending_timeout` after 30 seconds). Such a response means the result is **unknown**, not that the operation was cancelled. Check `GET /state` and never blindly start again.
4. Event-bus and Direct HTTP share legacy engine objects. The new Detecting guard reduces double-start hazards, but concurrent API/event-bus stress must still be explicitly tested before rollout.
5. The loopback REST API is local-only; it does not add authentication. Do not bind it to a public network interface.
6. Do not merge/deploy the branch until FFmpeg direct recording has been verified in a dedicated isolated instance/config with unique ports and session lifecycle tests; the current NvCapture REST port is constant 59077.
7. The `NVIDIA Config/capture-engines.json` file remains a **locked diagnostic baseline**, not the selected effective mode. The actual mode is still read from unified `config.json`; `NvContainer.workerRuntime.configured` carries the worker's current selection.

## Verified here

- `ffmpeg.exe` version `N-123522-gac4d50cb26-20260317`, advertises `ddagrab`, `gfxcapture`, `gdigrab`; enumerates libx264, NVENC, QSV and AMF encoders.
- Read-only probe/route/HTTP regression runner: **30/30 passed**, includes 3 checks against that local FFmpeg executable. With existing Routing (13), Container Catalog (15) and NodeAPI (5) suites: **63/63 tests passed**.
- Synthetic **video-only** smoke (no screen capture, no audio): one second `testsrc2` encoded by `libx264` to MP4; `ffprobe` confirmed H.264 320x180 with 30 frames, resulting file length 32719 bytes, stored in ignored build output `NVIDIA Capture/bin/Release/phase3-synthetic-smoke.mp4`.
- NvCapture builds: 0 errors, 3 pre-existing warnings. NvContainer builds: 0 errors/warnings when reusing previously verified Graphics references. A separate full build attempt encountered transient CSC CS2012 (NvGraphics.Contracts.dll file locked by another compiler/process); do not stop unrelated programs to work around it.

## Testing

```powershell
$r = 'C:\My Project\_ShadowPlay-Capture-Engine-Sandbox\Project'
dotnet run --project "$r\NVIDIA Capture\EngineCapture\RouteGuard.Tests\CaptureRouteGuard.Tests.vbproj" -- "$r\FFmpeg\ffmpeg.exe"
dotnet run --project "$r\NVIDIA Capture\EngineCapture\Routing.Tests\ShadowPlay.Capture.Routing.Tests.csproj"
dotnet run --project "$r\NVIDIA Container\Tests\CaptureEngineCatalog.Tests.csproj" -- "$r\NVIDIA Config\capture-engines.json"
node --test "$r\Overlay On-Screen Control\NVIDIA NodeAPI\tests\captureEngineStatus.test.js"
dotnet build "$r\NVIDIA Capture\NVIDIA Capture.vbproj" -c Release
dotnet build "$r\NVIDIA Container\NvContainer.csproj" -c Release -p:BuildProjectReferences=false -p:UseSharedCompilation=false -m:1
```

Note: compiling NvCapture in a clean clone requires building nested Duluka Recording and provisioning the nine prebuilt DulukaEngine DLLs into the app HintPath described in `BACKEND-PHASE1.md`. A fully reproducible one-command build remains a future task.
