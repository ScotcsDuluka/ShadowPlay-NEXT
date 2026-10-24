# NVIDIA Container — Legacy NvCapture State Compatibility
Date: 2026-10-24
Project: C:\My Project\NVIDIA-Shadowplay

## Verified issue
- NvCapture :59077/health and /state responded HTTP 200 and ready=true, recording=false.
- The installed /state JSON contains ready, failReason, recording, enabled, elapsedSec, outputPath, lastFile; it does not have actualSessionBackend.
- NvCapture /engines and /displays still respond {"error":"not_found"}.
- Previous Container Gateway required modern actualSessionBackend in /state and incorrectly reported the entire worker unavailable.

## Fix (NVIDIA Container only)
Components/Runtime/CaptureWorkerGateway.cs now validates either modern /state (actualSessionBackend present) or complete legacy /state (ready, recording, enabled, elapsedSec, failReason, outputPath and lastFile). Partial/malformed JSON fails validation.

Status fields:
- availability: worker /health and /state are readable
- workerReady: actual worker ready, regardless of engine selection
- engineEvidenceAvailable: successfully read and validated worker /engines v2
- runtimeReady: engine evidence or known real active session; never just transport
- stateContract: current, legacy or unavailable
- actualSessionBackend: null when legacy worker cannot verify owner
- recording: actual worker state, nullable on errors

Existing API default-deny write settings and identity policies remain unchanged. Never infer GPU codecs, screenshot support, or capture route.

## Regression and deployment
- Capture Worker Gateway regression: 42/42 PASS, including legacy/current, invalid JSON, unknown owner and FFmpeg evidence handling.
- Capture Display Relay regression: 26/26 PASS (stub HTTP; no capture operations).
- Isolated NvContainer Release x64 build: 0 errors and 0 warnings.
- Full four-file NvContainer bundle staged and installed; SHA-256 of installed files matched manifest.
- Controlled Launcher/Container restart with rollback backup under LocalAppData/ShadowPlay NEXT/DeploymentBackups/ContainerRelease-20261024-120003.
- Runtime NvContainer PID changed after Launcher-managed lifecycle; latest verified PID was 37376.
- No game or Hook killed; preflight confirmed zero active adapter instances and Capture idle.

## Real post-deploy result
GET http://127.0.0.1:59012/v1/capture/status
- availability=Available
- workerReady=true
- engineEvidenceAvailable=false
- runtimeReady=false
- recording=false
- stateContract=legacy
- actualSessionBackend=null
- stateError=null; healthError=null

GET http://127.0.0.1:59012/v1/capture/engines
- policyValid=true
- workerTruthAvailable=false
- workerError=endpoint_not_found

GET /v1/osc-adapter/status: dx9AutoHookEnabled=true, dx9HostContractReady=true, dx9TestTargetsConfigured=2; activeCount=0.
GET /v1/game-watcher/status: enabled=true, running=true, ApplicationOntology v1.2.8.
NodeAPI, NvCapture and Graphics-through-Container health checks HTTP 200.

The first concurrent probe just after restart returned a transient unavailable worker/timeout. Follow-up probes succeeded. Treat startup transient separately from persistent failure.

## Additional investigation
At one sampling moment Windows showed OSC Adapter x64/x86 processes while API activeCount was zero. A second sample showed the Floating Point test game still running and activeCount=1 with both Hosts attached; this was not verified as an orphan. Never kill these processes based on one sample.

## Remaining follow-ups
1. NvCapture team: deploy v2 /engines and display/screenshot worker endpoints; not deployed yet.
2. NodeAPI team: increase Screenshot POST timeout (currently 5.5s, worker may need >10s).
3. Container: consider health warmup telemetry and sustained untracked Host reconciliation without killing authorized game processes.

No changes to game files, NvCapture, NodeAPI, OSC UI, or native Hook Engine. No Git commit/push.
