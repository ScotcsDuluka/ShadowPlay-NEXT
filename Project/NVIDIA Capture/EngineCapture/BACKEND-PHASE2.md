# Capture Engine Phase 2 — backend routing (no OSC changes)

Branch: `feature/capture-engine-router-20261022`.
All work is in `C:\My Project\_ShadowPlay-Capture-Engine-Sandbox`.
Source `C:\My Project\NVIDIA-Shadowplay` (Stable) and live build are not deployed or restarted.

## Implemented backend behavior

- One **pure route guard**: `NVIDIA Capture/src/[API]/CaptureBackendRoute.vb`.
  Validates requested `engine_mode` + `api_capture` + encoder; no probing, IO, or GPU side effects.
- **Native Duluka**: `ddagrab`/`dxgi_desktop_duplication` and `NVENC_H264` only. The implementation in `NvencEncoderBackend` rejects HEVC/AV1. It must not fallback to FFmpeg on initialization failure.
- **FFmpeg**: existing `LegacyCaptureEngine` with `ddagrab`, `gdigrab`, or `gfxcapture`, validates known encoder IDs; whether a particular FFmpeg binary exposes them is tested when launched.
- **Historical installed NVIDIA ShadowPlay**: retains separate `nvidia` mode, explicitly **not** the future NVIDIA NvFBC backend.
- **OBS and unknown engines**: rejected with explicit errors; never quietly dispatched to FFmpeg or Duluka.
- Engine command TCP dispatch now chooses by effective config, not `_useNewEngine`. Stop chooses the actual active session, unaffected by config changes. WinForms diagnostic buttons use the same guard.
- The NvCapture event-bus/REST state poller (`StartRestRecording` / `StopRestRecording`) now chooses and confirms FFmpeg or native by the actual active owner; FFmpeg quality snapshot overrides are applied in memory **for the session only**, not persisted back to Config.
- Direct NvCapture REST `POST /record/start` and `POST /record/stop` still drive native sessions only. They reject non-native routes with `direct_http_route_not_wired_use_capture_event_bus` rather than recording on the wrong engine. A later phase can wire full legacy session orchestration to this second transport.
- NvCapture `GET /engines` (schema 2) reports `configured` route and `actualSessionBackend` separately. `GET /state` uses the actual owner for recording, elapsed and output path (FFmpeg + Native + Installed NVIDIA).
- NvContainer `GET /v1/capture/engines` (schema 1) retains its locked **policy baseline** but includes `workerRuntime` from a bounded loopback GET to NvCapture `/engines`. When unavailable, `workerRuntime=null` rather than inventing a selected engine.
- NodeAPI `GET /DulukaCapture/v.1.0/engines` transparently returns this read-only capability snapshot. No OSC code changed.

## Existing caveats and next gates

1. This has been compiled/unit-tested **only** in an isolated worktree. **Real recording, GPU stalls, audio mux, installed NVIDIA hotkey path and mode-switching under load remain unverified.**
2. `NvCapture /health.ready` still represents native-engine initialization, not FFmpeg binary/driver readiness. `workerRuntime` distinguishes selected route. Runtime capability probing for FFmpeg inputs/encoders should be implemented before allowing UI selection.
3. The process may still initialize native DXGI/NVENC even when FFmpeg is selected; avoid refactoring lifetime/rebuild until measured and regressions verify GPU handoff.
4. Changes to `engine_mode` while a session is recording affect **next** session only; current owner remains the actual session. Do not advertise live hot-swaps.
5. Direct REST HTTP non-native start/stop is deliberately not wired. The existing event-bus poller is the current supported command path.
6. NvContainer does not auto-deploy any of this; stage through the canonical build only after actual smoke testing, and coordinate ownership with other in-progress worktrees.
7. Before a real test, verify no other capture/game process is at risk and preserve the existing config. Default `testMode.enabled` and `captureRuntime.enabled` are unchanged.

## Local tests

```powershell
$r = "C:\My Project\_ShadowPlay-Capture-Engine-Sandbox\Project"
dotnet run --project "$r\NVIDIA Capture\EngineCapture\RouteGuard.Tests\CaptureRouteGuard.Tests.vbproj"
dotnet run --project "$r\NVIDIA Capture\EngineCapture\Routing.Tests\ShadowPlay.Capture.Routing.Tests.csproj"
dotnet run --project "$r\NVIDIA Container\Tests\CaptureEngineCatalog.Tests.csproj" -- "$r\NVIDIA Config\capture-engines.json"
node --test "$r\Overlay On-Screen Control\NVIDIA NodeAPI\tests\captureEngineStatus.test.js"
dotnet build "$r\NVIDIA Container\NvContainer.csproj" -c Release
```

NvCapture app build needs its transitive `DulukaEngine*.dll` files in the expected `NVIDIA Capture/bin/Release/net10.0-windows10.0.26100.0` HintPath as documented in `BACKEND-PHASE1.md`.
