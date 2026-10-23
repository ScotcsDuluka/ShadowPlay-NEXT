# Multi-engine backend foundation — Sandbox Phase 1

**Branch:** `feature/capture-engine-router-20261022`
**Source worktree:** `C:\My Project\_ShadowPlay-Capture-Engine-Sandbox`
**Production Stable source:** unchanged by this worktree. No OSC files were modified.

## Integration map

```text
NodeAPI/index.js
    GET /DulukaCapture/v.1.0/engines (read-only)
        -> shims/capture-engine-status.js
        -> NvContainer 127.0.0.1:59012/v1/capture/engines
        -> CaptureEngineCatalog Snapshot (locked policy)
        -> NvCapture health 127.0.0.1:59077/health
NvCapture 127.0.0.1:59077/engines: local worker introspection (read-only)
```

The route does not start, stop, alter, or select a recording provider. `NVIDIA Config/capture-engines.json` declares an explicitly locked default route `duluka:dxgi` + `nvidia:nvenc-h264`. Planned providers remain in the catalog solely as disabled/unwired capabilities.

**Important truth constraints:** The current RecordingEngine constructs DXGI Ddagrab and native NVENC directly. FFmpeg source/binaries exist but this routing layer cannot switch to FFmpeg yet; OBS WebSocket in Container controls external OBS rather than providing raw frames; NVIDIA NvFBC has not been validated or implemented. Health means the current process's internal encoder initialized, not that all codecs/GPUs/backends passed benchmarks.

## Updated files
- `NVIDIA Capture/src/[API]/CaptureRestApiServer.vb` — local GET /engines.
- `NVIDIA Container/NvContainer.cs` — read-only catalog delegate + existing worker health.
- `NVIDIA Container/Components/Diagnostics/ContainerTestApi.cs` — local GET /v1/capture/engines.
- `NVIDIA Container/Components/Runtime/CaptureEngineCatalog.cs` — provider status + policy verification.
- `NVIDIA Container/NvContainer.csproj` — exclude standalone test source.
- `NVIDIA Container/Tests/` — zero-package regression runner.
- `NVIDIA Config/capture-engines.json` — read-only locked policy.
- `Overlay On-Screen Control/NVIDIA NodeAPI/index.js` — mount capability route after existing security middleware.
- `Overlay On-Screen Control/NVIDIA NodeAPI/shims/capture-engine-status.js` — fail-closed proxy.
- `Overlay On-Screen Control/NVIDIA NodeAPI/tests/captureEngineStatus.test.js` — Node regression.

No file under `NVIDIA NodeAPI/osc/` was touched. No Stable deployment, service restarts, new listeners, or real recording started.

## Safe checks
```powershell
$project = "C:\My Project\_ShadowPlay-Capture-Engine-Sandbox\Project"
node --test "$project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\captureEngineStatus.test.js"
dotnet run --project "$project\NVIDIA Container\Tests\CaptureEngineCatalog.Tests.csproj" -- "$project\NVIDIA Config\capture-engines.json"
dotnet build "$project\NVIDIA Container\NvContainer.csproj" -c Release
```

The isolated NvCapture.vbproj uses prebuilt DulukaEngine library references; a clean clone needs to compile `EngineCapture/Duluka Capture/Recording/DulukaEngine.Recording.vbproj` first and copy its `DulukaEngine*.dll` outputs to the expected `NVIDIA Capture/bin/Release/net10.0-windows10.0.26100.0` directory before the app build. `Scripts/build-dev.ps1` does not currently build these nested projects automatically. This is a pre-existing build dependency, not a new runtime feature.

## Next phase (not implemented)
- Confirm source-of-truth policy vs actual effective recording configuration and codec-specific support.
- Define runtime-factory selection in RecordingEngineHost with strict no-fallback guard.
- Implement FFmpeg worker selector, OBS managed bridge and optional NvFBC after hardware/API tests.
- Expose an API-safe selection POST only after real factory and lifecycle tests; then connect OSC.
