# NVIDIA Container — Capture Display / Screenshot Relay
Date: 2026-10-24
Project: C:\My Project\NVIDIA-Shadowplay

## Owner and topology
NvCapture :59077 -> NvContainer :59012 -> NodeAPI :59011 -> OSC UI.
NvCapture is the ONLY display selector and screenshot producer. Container relays validated JSON. No pixels, audio or screenshots are created by Container.

## Container -> NvCapture
GET /v1/capture/displays -> GET /displays
  Upstream JSON schemaVersion 1, readOnly=true, source NvCapture, displays array, selected, selectionValid, recording, appliesOnEngineRebuild. Requires outputIndex (OutputIndex), exact adapterLuidLow/High identity in upstream display objects. Preserves genuine empty/invalid inventory; no fake screens.
POST /v1/capture/display/select -> POST /display/select
  Body exactly {confirmed:true,outputIndex:int>=0,adapterLuidLow:uint32,adapterLuidHigh:int32}; revalidates exact display identity with fresh upstream GET /displays; requires idle /health and /state. Returns normalized saved, error, selected, effectiveNow, appliesOnEngineRebuild, source. Selection applies on engine rebuild, not immediately.
POST /v1/capture/screenshot -> POST /screenshot
  Body exactly {confirmed:true}; requires idle /health and /state and valid selected display. Normalizes upstream PascalCase Saved/ ErrorCode/ File to lowercase saved/error/file fields. A 200 response with saved=false remains a failure; no synthetic file is generated.

Default-deny on writes:
  captureRuntime.allowDisplayCommands=false
  captureRuntime.allowScreenshotCommands=false
  testMode.enabled=true also blocks writes.
  Existing browser loopback request filter is NOT local-process authentication. Only enable write routes after compatible NvCapture runtime plus coordinated trusted IPC authorization.
No changes to Record Start/Stop or Replay (Replay remains 501).

Error contract: missing endpoint 404 or 200 not_found -> 501 Unavailable, timeout -> 504, bad JSON/unreachable -> 503, invalid caller body -> 400, denied writes -> 403, busy -> 409. GET timeout 3 s; select 4 s; screenshot 15 s; bounded response 128 KiB. A timed-out screenshot may have an unknown outcome; do not automatically retry.

## NodeAPI team handoff
The existing shims/capture-display-routing.js proxies these three endpoints:
  /DulukaCapture/v.1.0/displays
  /DulukaCapture/v.1.0/display/select
  /DulukaCapture/v.1.0/screenshot
NodeAPI currently has 5.5-second POST timeout. Worker screenshot may take around 10 seconds; change screenshot-specific NodeAPI timeout to over 15 seconds before real release. Keep saved=false distinct from HTTP 200.
NodeAPI/OSC source was not modified by Container agent.

## Tested
New Project/NVIDIA Container/Tests/CaptureDisplayRelay.Tests.csproj and CaptureDisplayRelayTests.cs (26/26 mock tests), plus existing CaptureWorkerGateway.Tests.csproj (28/28), and isolated NvContainer Release x64 Build (0 errors, 0 warnings).
No POST sent to actual NvCapture, no screen captured, no monitor selected.
Compatibility with deployed NvCapture's /displays endpoint is not yet verified. NvCapture team's Phase 8 notes describe these endpoints as Source-only.

## Concurrent identity request
Following user's updated request, development override allowUnverifiedIdentity=true applies to DX9 as well as D3D11; production strict SHA-256 returns when disabled. Other guards (Ontology match, Host/PID/path, API contract) are retained. enableDx9AutoHook still defaults false; no auto-hook/game injection was run. DX9 Routing tests 23/23 pass.

## Deployment
Stage verified binaries first. Do not overwrite active runtime without compatible NvCapture deployment, rollback backups and a coordinated restart. Capture/OSC/Hook owners must review respective integration.
