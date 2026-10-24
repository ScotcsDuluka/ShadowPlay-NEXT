# NVIDIA Container — Capture Contract and Startup Warmup
2026-10-24
Project: C:\My Project\NVIDIA-Shadowplay

## Current runtime at preflight
NvCapture /health and /state are HTTP 200. NvCapture /engines is Schema v2 and /displays Schema v1 with real monitor inventory. Container capture status has workerReady=true and runtimeReady=true.
NodeAPI shims/capture-display-routing.js already uses 19000ms for screenshot POST. NodeAPI tests cover saved=false and ambiguous 504 timeout; do not overwrite this concurrent module work.

## Implemented in Container source
Components/Runtime/CaptureWorkerGateway.cs:
- GET-only contract negotiation across NvCapture /health /state /engines /displays; no recording, display switch, or screenshot triggered.
- /engines version 2 and /displays version 1 are verified only from actual validated worker payload. Health/state are unversioned and report null, not invented versions.
- Missing/invalid/legacy upstream gives null version and Unavailable, without invented codecs or screen capabilities.
- record, screenshot, display select command verification remains false where no read-only worker command advertisement exists. Write gates remain default-deny.
- Status and contract have startupPhase: Starting in first 30 seconds if never ready, Ready after success, Degraded after subsequent loss, Unavailable after initial 30 seconds, Disabled when Capture runtime disabled.
- Startup phase is diagnostic only: does not mask healthError/stateError, change workerReady, recording, runtimeReady, or allow unsafe commands.

Components/Diagnostics/ContainerTestApi.cs: GET /v1/capture/contract.
NvContainer.cs: wires CaptureHub.Contract to above endpoint.
Tests/CaptureWorkerGatewayTests.cs: 11 new cases for contract versions, fail-closed write discovery, and deterministic startup/recovery.

## API shape
GET /v1/capture/contract diagnostic JSON:
- schemaVersion=1, source=NvCapture
- workerReady, availability, startupPhase, startupGraceSeconds=30
- workerApiVersion=2 or null; displayApiVersion=1 or null
- stateContract=current/legacy/unavailable, recording boolean or null
- endpoints.health/state/engines/displays each have routeWired, schemaVerified, schemaVersion, availability, code, upstreamStatus
- commands.record/displaySelect/screenshot/replay have routeWired, workerCommandVerified, gatewayEnabled. DisplaySelect/Screenshot include displayInventoryVerified and screenshot.runtimeReady=false.
Read-only contract cannot affirm POST functionality: never display screenshot as Ready from GET inventory alone.

## Regression and Build
- Container Capture Worker Gateway 53/53 PASS (stub HTTP only)
- NodeAPI Capture Display Relay 8/8 PASS; timeout 19000ms confirmed
- NvContainer isolated Release x64: 0 warnings, 0 errors
- Staged four-file Container build: build\NVIDIA ShadowPlay\NVIDIA Container\PendingUpdates\NextInstall
- Installer StageOnly returned expected DEFERRED exit 2.

## Deployment / rollback
Live deploy and restart intentionally NOT performed: preflight showed Floating Point.exe active, OSC Adapter activeCount=1, GameWatcher matched=1. NvCapture recording=false. Do not terminate the user's active test game or unload injected Hook abruptly.

Once game exits and adapter activeCount=0, rerun preflight and existing Scripts\Deploy-NvContainerControlledDx9.ps1 with secure bundle/config backup and rollback. After installing verify installed SHA-256, NodeAPI/Capture/Graphics health, watcher and DX9 route, plus GET /v1/capture/contract (v2/v1; startupPhase Ready; screenshot workerCommandVerified=false). Current production Container has not received these newly added fields yet.

No Git commit or push. No NvCapture, Hook, game files, or OSC UI modified.
