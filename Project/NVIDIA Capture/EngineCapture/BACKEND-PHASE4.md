# Phase 4 — Isolated NvCapture REST capture smoke (2026-10-23)

Branch: `feature/capture-engine-router-20261022`.
Worktree: `C:\My Project\_ShadowPlay-Capture-Engine-Sandbox`.

**Executed on AORUS-DESKTOP, not on the Stable deployment.** The original Stable source checkout, deployed executable, NodeAPI and OSC UI were not modified or restarted. The testing process and its temporary listener were stopped by the test harness. No files below `NVIDIA NodeAPI/osc/` were touched.

## What changed

- **Opt-in worker isolation:** `NvCapture.exe --rest --capture-sandbox`, environment variables `NVIDIA_SHADOWPLAY_APP_ROOT` and `NV_CAPTURE_ISOLATED_ROOT` point to the SAME isolated root; `NV_CAPTURE_ISOLATED_REST_PORT` must be 59100–59999. Requires `Config/config.json`, `Config/engine.json`, and marker `.shadowplay-capture-isolated` with `shadowplay-next-isolated-capture-v1`. Preflight rejects invalid roots/ports. Production continues to use 59077.
- In sandbox mode the NodeAPI event-bus poller, TCP Hub (because `--rest` is mandatory), auxiliary Audio UI, and optional background encoder-discovery are not started. For an FFmpeg-only sandbox, persistent native DXGI/NVENC initialization is skipped; for Duluka it still initializes.
- Isolated REST recording output is forced to `<sandbox-root>\Records`. The worker never fetches the Stable `59001/RecordPaths` or writes ordinary MyVideos for isolated Direct REST.
- The WinForms app's single-instance check becomes **false only** for explicit sandbox mode, preventing the sandbox launch from forwarding its command line to the already-running Stable NvCapture. This conditional currently lives in the generated `My Project/Application.Designer.vb`: **verify it is retained if Visual Studio regenerates the designer**.
- `NVIDIA Capture.vbproj` adds `Vortice.Direct3D11 3.6.2` to propagate Native NVENC's Vortice/SharpGen transitive runtime dependencies into NvCapture output/`.deps.json`; plain Duluka DLL HintPaths alone did NOT bring these runtime assemblies. Exact version matches DulukaEngine.Encoder.Nvenc.vbproj.
- **Native Direct REST stop truth fix:** `HandleRecordingStop` intentionally clears `_recordingTask` after completion, so a caller that examines only that cleared field reported `stopped:false` even though `SessionResult.Pass=True` and MP4 finalized. `StopRestOnUi` now keeps the actual session task before stop and checks its completion/result and file existence.

## Real tests — actual desktop capture through isolated NvCapture.exe

Test harness (creates isolated config, starts its own child NvCapture, sends loopback REST, validates MP4 via ffprobe, then kills ONLY that sandbox child in a finally block):

```powershell
$r = 'C:\My Project\_ShadowPlay-Capture-Engine-Sandbox\Project\NVIDIA Capture'
& "$r\EngineCapture\IsolatedTest\Invoke-CaptureSmoke.ps1" -RunSmoke -Engine FFmpeg -Seconds 3
& "$r\EngineCapture\IsolatedTest\Invoke-CaptureSmoke.ps1" -RunSmoke -Engine Duluka -Seconds 3
```

| Evidence | FFmpeg | Duluka |
| --- | --- | --- |
| Actual route | FFmpeg/gdigrab/libx264 | Duluka/DXGI/NVENC_H264 |
| Dedicated loopback port | 59477 | 59477 |
| Direct REST start | started=true | started=true |
| Direct REST state | actualSessionBackend=FFmpeg, recording=true | actualSessionBackend=Duluka, recording=true |
| Stop/MP4 finalize | stopped=true | stopped=true after fix |
| ffprobe H.264 | yes | yes |
| Duration | 3.20 s | 2.733388 s |
| Resolution at run | 2960 x 1082 | 1680 x 1050 |
| Bytes | 481141 | 17086136 |
| Verdict | SMOKE_PASS | SMOKE_PASS |

File evidence lives **only** in ignored build output: `NVIDIA Capture/bin/Release/IsolatedCaptureRoot/Records/`. Do not publish desktop-capture footage with the Git changes.

Native debugging sequence:
1. First attempt: `Vortice.Direct3D11, Version=3.6.2.0` missing in the assembled NvCapture runtime; fixed package propagation.
2. Second attempt: native DXGI capture, NVENC encode and mux **succeeded** with `SessionResult.Pass=True` but Direct REST incorrectly returned stop_failed due to checking the cleared `_recordingTask`.
3. Third attempt: Direct REST stop return corrected; `SMOKE_PASS` with finalized H.264 MP4.

The user-facing Stable recording worker remained alive and `GET http://127.0.0.1:59077/health` reported `ok:true,ready:true,recording:false` when checked between tests. The isolated port 59477 and its process were not left running after the harness completed.

## Validation / remaining gates

- Unit tests validate capture isolation root/port constraints, binary capability probe, HTTP fragmented requests, route behavior. The existing Container and NodeAPI test suites should remain green.
- Smoke is intentionally **video-only** (system audio and microphone disabled) to avoid capturing unrelated audio. Audio mux correctness and longer runs need follow-up testing.
- Real display sizes differ between the two captures because Windows display configuration/current desktop geometry varied during runs; this is not evidence of an encoder resolution limit.
- Only one short run per backend passed. Not a longevity, performance, thermal, load-switch or game-capture certification. Never advertise OBS bridge or NVIDIA NvFBC as implemented.
- Do **not merge or deploy** without integrating newer Stable commits, resolving any conflicts and validating that the isolated WinForms single-instance change survives generated-file updates.
