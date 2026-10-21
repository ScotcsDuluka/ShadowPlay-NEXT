# ShadowPlay NEXT — Multi-Engine Router (Phase 0)

## Existing foundation (DO NOT DELETE)
- Main app: ../NVIDIA Capture.vbproj — VB.NET WinForms .NET 10; NvCapture assembly.
- Duluka Capture/Video/Contracts/DulukaEngine.Video: IVideoCaptureBackend, IVideoFrame and bounded sink.
- Duluka Capture/Encoding/DulukaEngine.Encoder: IEncoderBackend.
- Duluka Capture/Video/DXGI Desktop Duplication: native DXGI backend.
- Duluka Capture/Encoding/NVENC: native NVENC backend (code present, hardware state unverified).
- Duluka Capture/Recording/Backends/FFmpeg: FFmpeg process/mux.
- Duluka Capture/Audio: WASAPI.
- src/[API]/RecordingEngineHost.vb: existing production host; NOT replaced by the router.

## 4 engine families and their API/Encoder roles
| Family | Capture API | Encoder API | Ownership |
| --- | --- | --- | --- |
| Duluka | DXGI (existing); WGC game adapter planned | NVIDIA native NVENC (existing separate implementation) | In-process DLL, D3D11 textures |
| FFmpeg | ddagrab/gdigrab (command builder exists) | FFmpeg h264_nvenc/libx264 (verify ffmpeg -encoders) | One FFmpeg child owns both capture and encoding |
| OBS | OBS Studio scene/source (bridge planned) | OBS-selected encoder managed by OBS | External OBS process via obs-websocket; OBS owns recording |
| NVIDIA | NvFBC (reserved; do not assume available) | Native NVENC backend from Duluka Encoding/NVENC | Native D3D11; NVENC is encoder, not capture API |

OBS Studio bridge is NOT a raw-frame source. Capturing OBS output into our encoder would require a separately designed frame transport, OBS plugin or equivalent authorized interface. An obs-websocket control connection should only command OBS recording/streaming and query status.

The NVIDIA-family NvFBC slot is RESERVED and must pass SDK/platform/permission checks before it can be activated. NVIDIA NVENC does not itself capture frames.

## Router implemented in this phase
- `Routing/`: C# .NET 10 standalone offline capture/encoder catalog and compatibility validator.
- `Routing.Tests/`: dependency-free regression checks; no screen recording or GPU initialization.
- Routes have separate Capture ID and Encoder ID.
- A route validates transport, process ownership and source presence.
- Every valid route has `RuntimeWired=false` and `MayStart=false` until real factories, capability checks and host lifecycle are integrated. Do not use this planner to claim actual recording works.
- No changes to live Record/Enable behavior, default recorder, UI, or its configuration.

Try:
```powershell
dotnet run --project "Routing/ShadowPlay.Capture.Routing.csproj" -- --list
dotnet run --project "Routing/ShadowPlay.Capture.Routing.csproj" -- --plan duluka:dxgi nvidia:nvenc-h264
dotnet run --project "Routing.Tests/ShadowPlay.Capture.Routing.Tests.csproj"
```

## Process separation decision
Do NOT create 4 independent EXEs immediately.
1. `NvCapture.exe`: existing host/controller and recording API, remains compatible.
2. Duluka native DXGI+NVENC: use the existing contract assembly approach first; C++20 interop can be introduced behind the existing .NET contracts for hot paths where measured.
3. FFmpeg: keep one external ffmpeg.exe child, bounded lifecycle + Windows Job Object; FFmpeg owns both capture and encoder inside its filter graph.
4. OBS: control a separately running OBS Studio via its supported remote-control interface; do not embed libobs until licensing, process lifecycle, deployment and thread ownership are designed.
5. Only add `CaptureWorker.exe` for crash isolation if an actual backend needs it; IPC should carry config, commands, stats and error events, not raw 144-FPS frames. Native shared textures require explicit same-adapter handle/fence/lifetime contract.

## Required integration gates before production
1. Probe API/encoder/GPU/driver/FFmpeg/OBS support with clear failure reasons; no silent fallback to another engine.
2. Implement adapters using existing `IVideoCaptureBackend` + `IEncoderBackend` (do NOT duplicate frame contracts).
3. Formalize GPU device and texture ownership, keyed mutex/fences where appropriate, PTS monotonicity, backpressure, resize/device-lost recovery.
4. Use an explicit lifecycle: Created -> Probing -> Ready -> Running -> Stopping -> Stopped / Faulted.
5. Keep audio on WASAPI sidecar initially, then mux with per-track timestamp accounting and clean shutdown.
6. Benchmark capture FPS / drops / GPU copy count / NVENC latency / audio drift, and run regression suites.
7. Integrate selection with RecordingEngineHost and OSC config ONLY after user-visible opt-in and hardware testing; preserve existing stable behavior.

## Git/worktree safety
At introduction, pre-existing changes were present in src/[Integration]/ShadowPlayRest.vb and src/[UI]/UI_Engine.vb plus OSC files; these files were not touched.

## Confirmed OSC UI and backend ownership (2026-10-22)
- Actual user-facing UI source: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.js` and `video-capture-options.css`.
- Node runtime entry: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/index.js`; it mounts `shims/route-floor.js` (index.js line ~663).
- UI calls `/DulukaCapture/v.1.0/settings` (GET/POST) and `/DulukaCapture/v.1.0/status` (GET). Active `shims/route-floor.js` validates `engineMode`, `apiCapture`, `encoder`, `encoderPreset`, and writes shared `config.json` plus `engine.json`. It intentionally accepts only FFmpeg/Duluka.
- `NVIDIA NodeAPI/NVIDIA Web Helper/Backend/routes/duluka.js` is a **different backend implementation** with `engine` / `codeEncoder` schema; do not edit that path assuming it is the active NodeAPI index.js route. Verify deployment/entrypoint before selecting it.
- `Project/NVIDIA Capture/src/[API]/CaptureRestApiServer.vb` is the capture-worker boundary (health/record runtime, often :59077), not the UI-facing settings backend.
- `Project/NVIDIA Container/NvContainer.cs` controls NvCapture child ownership and health. Its captureRuntime enabled flag must be respected.
- Existing OSC select uses NVIDIA-like Angular Material `md-select`; preserve its appearance. UI engine choices presently FFmpeg/Duluka only; OBS/NVIDIA must remain disabled until the runtime/factory/schema is actually implemented.
- This phase created **only** offline router/catalog/test code and documentation; it did not change OSC, active Node API routes, container, or production capture host.
- `Routing.Tests` passed 13/13 local tests with .NET 10 on AORUS-DESKTOP. This verifies route metadata and incompatible combination rejection, **not** a working capture pipeline.

### Proposed integration order (no production switch yet)
1. Add a versioned **capability/status endpoint** in active `shims/route-floor.js` (or a dedicated module mounted by `index.js`), not the alternate backend. Output supported capture/encoder pairs and explain unavailable providers.
2. Keep OSC `video-capture-options.js` choices populated from capabilities while retaining md-select and current UI structure.
3. Add a recording-worker route selection boundary in NvCapture, map stable configuration IDs to the existing Video/Encoder contract factories, verify adapter frame/PTS ownership; keep FFmpeg child and OBS external bridge separate.
4. Add explicit opt-in and rollback; update NvContainer child lifecycle/health only after tests.
