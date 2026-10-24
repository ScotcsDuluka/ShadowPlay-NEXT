# NVIDIA Container — Capture Communication Hub / API Contract
Date: 2026-10-24
Project: C:\My Project\NVIDIA-Shadowplay
Owner: NVIDIA Container only. NvCapture remains the sole capture and recording engine.

## Architecture
NvCapture (REST :59077) ↔ NvContainer (loopback API :59012) ↔ NVIDIA NodeAPI ↔ OSC UI

## NvCapture worker contract
- GET /health: ready:boolean, recording:boolean.
- GET /state: ready:boolean, recording:boolean, actualSessionBackend:string, elapsedSec:int; actual recording owner is authoritative.
- GET /engines: schemaVersion=2, readOnly=true; configured: {backend,captureApi,encoder,allowed}; actualSessionBackend, nativeRuntimeReady, nativeRuntimeCodec, recording; ffmpegBinary: {binaryRunnable,captureApis:[],encoders:[],hardwareEncoderVerified}.
- POST /record/start: started:boolean, error if rejected.
- POST /record/stop: stopped:boolean, error if rejected.
Older deployed NvCapture can return HTTP 200 with {"error":"not_found"} for /engines; the Container classifies this as endpoint_not_found.

## NVIDIA Container routes
- GET /v1/capture/engines
  schemaVersion:1, policyValid, policyError, availability, workerTruthAvailable, workerError, workerRuntime (v2 JSON or null), providers, workflows, recording:boolean|null.
- GET /v1/capture/status
  schemaVersion:1, source:"NvCapture", runtimeEnabled, availability:"Available"|"Unavailable", runtimeReady:boolean, recording:boolean|null, actualSessionBackend:string|null, health, state, healthError, stateError, replay:{availability:"Unavailable",commandSupported:false}, screenshot:{availability:"Unavailable",commandSupported:false}.
- GET /v1/capture/capabilities
  schemaVersion:1, source:"NvCapture", availability, workerError, policyValid, providers, workflows.
- POST /v1/capture/record/start|stop
  Disabled by default with 403 record_gateway_disabled. Opt-in captureRuntime.allowRecordCommands=true and testMode.enabled=false in config, plus JSON body exactly {"confirmed":true}. Start checks live state/health/engines, Stop checks a live active state/owner. Container forwards {} only. Does not capture itself.
- POST /v1/capture/replay/start|stop|save and POST /v1/capture/screenshot
  HTTP 501, not_supported_by_worker_contract, availability:"Unavailable"; never pretend worker supports these until it does.

Read API HTTP 200 means the diagnostic endpoint responded, NOT that the worker is available.
Provider fields:
- routeWired = source path exists.
- capabilityVerified = worker-verified capability metadata; never from a policy-only route.
- hardwareVerified = worker-verified hardware status (false when unconfirmed).
- runtimeReady = worker runtime readiness; not inferred from routeWired.
- recording = actual worker state, nullable when unknown.
- captureApis, encoders, codecs = empty when the worker has not supplied trustworthy evidence.
- selected.policyOnly = baseline routing policy, NOT the live configured backend. selected=null when policy invalid/missing. workerRuntime.configured = selected live backend when available.
FFmpeg binary introspection is not hardware/GPU initialization proof.

## Safety and errors
- GET timeout 3 sec, max JSON 128 KiB, local loopback only.
- Error codes: runtime_disabled, endpoint_not_found, unavailable, timeout, invalid_response, upstream_http_error, invalid_endpoint.
- A worker 404, HTTP 200 not_found, timeout or missing v2 fields makes workerTruthAvailable=false, workerRuntime=null, availability=Unavailable. Unknown recording state remains null.
- Record command requires explicit config enable + per-request confirmation, serialized commands, and native/FFmpeg owner preflight; never sends arbitrary caller JSON to NvCapture.
- Start POST timeout 19 sec; Stop POST timeout 35 sec. HTTP timeout or worker start_status_pending_timeout / stop_status_pending_timeout returns 504 outcomeUnknown:true, retryRequiresFreshState:true. The gateway blocks blind repeat commands until state reconciliation (status exposes startOutcomeUnresolved / stopOutcomeUnresolved). Never blindly retry a timed-out command.
- Existing ControlRequestSecurity guards Host/Origin/fetch-site but is NOT authenticated IPC against an untrusted local same-user process. Keep write routing disabled until coordinated authentication if untrusted local clients are possible.

## Policy installation and state
Source policy exists: Project\NVIDIA Config\capture-engines.json
Runtime Build policy missing when inspected: build\NVIDIA ShadowPlay\NVIDIA Config\capture-engines.json
Missing/invalid policy is reported independently of NvCapture health/readiness.
Scripts\Install-NvContainerBuild.ps1 now validates source policy, stages it on deferred install, and provisions only when the target policy is missing. It preserves existing policy. This installer was NOT executed. Running Build unchanged.

## NodeAPI / OSC handoff (DO NOT IMPLEMENT IN THIS MODULE)
- NodeAPI GET /DulukaCapture/v.1.0/engines → Container GET /v1/capture/engines
- NodeAPI GET /DulukaCapture/v.1.0/status → Container GET /v1/capture/status
- NodeAPI GET /DulukaCapture/v.1.0/capabilities → Container GET /v1/capture/capabilities
- Display workerRuntime.configured instead of selected.policyOnly.
- Display Unavailable until source has real verified worker evidence. Do not expose record/replay/screenshot write routes to UI until reviewed and end-to-end tested.
- NodeAPI and OSC UI source were not modified, per component ownership.

## Tests and limitations
- Capture Catalog: 21/21 PASS.
- Capture Worker Gateway: 28/28 PASS using stub HTTP handlers (no real worker mutations).
- NvContainer Release x64 isolated build: 0 Errors, 0 Warnings.
- Installer script syntax check: PASS. No installer run, restart, deployment, recording or audio/screen capture done.
- Compatible NvCapture v2 worker has NOT been deployed/tested end-to-end; older live worker /engines remains not_found.
