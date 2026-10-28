# Capture control plane implementation evidence

Date: 2026-10-27, Asia/Bangkok. Scope: `Project/NVIDIA Capture/src/[API]` and the existing device-free `EngineCapture/RouteGuard.Tests` runner. Shared Stable working tree and concurrent changes were preserved. No staging, commit, checkout, reset, restart or capture was performed by this agent. Root owns deployment and live acceptance.

## Source findings and fixes

| Symbol / source | Previous behavior | Implemented behavior / evidence boundary |
| --- | --- | --- |
| `CaptureRestApiServer.RestTruth`, `RestLifecycleOwner` | A queued native Task counted as an actual Duluka session; an optimistic external NVIDIA flag counted as actual external capture | Lifecycle busy/owner, allocated session and actual progressing engine are separate. Actual Duluka running requires a session ID plus positive real-frame and encoded-frame progress and the native `Recording` lifecycle state. Synthetic owned frames have real-frame count zero. External NVIDIA remains `ExternalStatusUnverified`, with no actual owner claim. |
| `CaptureRuntimeTruth.Observe` | No pure observation seam | Tests distinguish idle, preparation, awaiting first real frame, synthetic-only input, real capture, finalizing, FFmpeg progress, external request and conflicting owners. It never selects or starts an engine. |
| `GET /engines`, `/capabilities`, `/state`, `/health` | Saved selector and actual owner were the principal fields | Additive `truthSchemaVersion=1` under existing `/engines` schema 2. Exposes requested direct-command snapshot, fresh saved configuration including explicit REST overrides, selected engine, available implementation/runtime metadata, actual session, actual progressing engine and verification scope. Existing `recording` retains lifecycle busy semantics for compatibility and restart protection; `isCapturing` is the actual-progress field. |
| `ServeRestClient`, `CaptureRestAuthorization` | Loopback POSTs had no credential check | All POST routes require `X-ShadowPlay-Capture-Token`, matched in fixed time against the existing same-user DPAPI pairing vault. Missing/unreadable vault returns HTTP 503, missing/mismatched token HTTP 403. No automatic enrollment, rotation or plaintext fallback. Host must match this loopback listener; foreign Origin is denied; unsupported verbs return 405. Reads need no token. |
| `CaptureHttpRequestReader.Read` | Discarded headers; accepted duplicate Content-Length and transfer encoding | Retains normalized headers for authorization, rejects duplicate headers, transfer encoding, malformed/absolute request targets and malformed UTF-8 while preserving bounded split-packet parsing. |
| `StartRestOnUi` | Native start succeeded on allocated SessionId; FFmpeg succeeded merely on process start | Native waits for real capture plus encoded progress. FFmpeg observes its own `FrameCaptured` output statistics before reporting started. Configuration is rechecked on the UI thread. Start timeout remains an unresolved/pending operation, requiring state inspection. |
| `LoadOscSettings`, `LoadOscSnapshot`, `CaptureRestSettingsPolicy.Valid` | Absent `capture-osc.json` silently replaced native canonical settings with 60 fps/50 Mbps; FFmpeg did not apply the same overrides | Absent override store uses canonical configuration. An explicit store applies to either engine. Snapshot values reject malformed/out-of-range FPS/bitrate and unknown resolution; custom resolution retains configured geometry. Invalid explicit settings are rejected instead of silently selecting native resolution. |
| `POST /record/stop` | Stop success and output file existence could be mistaken for saved media | Operation `stopped` is separate from `saved` and `finalizationState`. Saved requires owner success, full video decode and exact requested audio-stream count. Short outputs (at most 30 seconds) use bounded full decode; longer standalone FFmpeg outputs remain Partial with `full_decode_pending`. Native session result can provide its own full-decode proof. Failed/cancelled/partial evidence and existing output path remain visible. `/state.lastRecordingResult` caches the completed REST result. |

## Exact shared authorization contract

- Protected file: `%LOCALAPPDATA%\ShadowPlay NEXT\Secrets\pairing-current-user.dpapi`.
- Existing Container `PairingSecretStore` format: CurrentUser DPAPI, no entropy, UI forbidden, decrypted value is exactly 32 ASCII hex characters.
- Worker header: `X-ShadowPlay-Capture-Token`.
- Gateway must read the same protected vault and attach the header only to mutating requests. Root implemented the Container integration separately. Same-user token availability does not prove media or GPU capability.
- Secrets are never included in reports, API responses or test output. A read-only test confirmed the existing local vault is readable without printing its value.

## Validation

Command (repository root):

```powershell
dotnet run --project 'Project/NVIDIA Capture/EngineCapture/RouteGuard.Tests/CaptureRouteGuard.Tests.vbproj' -c Release --no-restore
```

Result: exit 0; **34 control-plane authorization/runtime/settings checks + 33 pre-existing route/capability/HTTP/isolation checks = 67 passed**. No capture command was dispatched; no GPU/desktop/audio device was opened. The DPAPI check is read-only. Initial intermediate runs passed 59 and then 60 checks; the final 67-check run includes settings and existing-vault validation. `git diff --check` on owned API/test paths passed.

Main Release build, staging, process hashes and live endpoint verification are coordinated by the root/unified-build agents and are not claimed by these device-free tests.

## Open boundaries and concrete external findings

- Native QSV REST selection remains fail-closed under `CaptureBackendRoute.Decide`; this change does not open the Intel live gate or certify native Intel recording.
- OBS stays unavailable without a recording integration. External installed NVIDIA, native NVENC and NvFBC are different capabilities; no NvFBC readiness is claimed.
- `src/[Capture]/NvShadowPlayRecorder.Start` only sets an optimistic flag in the observed source; `StopRecording` invokes the hotkey. The flag is not recorder-runtime attestation and cannot establish an actual running NVIDIA engine. This file was not edited by this agent.
- FFmpeg frame statistics prove encoder output progress but do not attest the child's physical encoder-device LUID. Hardware verification remains explicitly false at this API scope.
- Long standalone FFmpeg media requires a separate complete verification path before Saved; packet scans are insufficient. Audio stream counts prove requested mux policy, not real microphone/loopback source content or acoustic sync.
- Existing event-bus commands are a different authorization surface. The direct REST protection does not certify that transport; requested direct-command/audio intent is marked unknown for a recording started elsewhere.
- Display/exclusive capture, live NVIDIA/Intel, source recovery and audio synchronization acceptance remain root-owned runtime work. No per-game exclusive support is inferred from source presence.

## Modified paths

- `Project/NVIDIA Capture/src/[API]/CaptureRestApiServer.vb`
- `Project/NVIDIA Capture/src/[API]/CaptureHttpRequestReader.vb`
- `Project/NVIDIA Capture/src/[API]/CaptureRestAuthorization.vb` (new)
- `Project/NVIDIA Capture/src/[API]/CaptureRuntimeTruth.vb` (new)
- `Project/NVIDIA Capture/src/[API]/CaptureRestSettingsPolicy.vb` (new)
- `Project/NVIDIA Capture/EngineCapture/RouteGuard.Tests/CaptureRouteGuard.Tests.vbproj`
- `Project/NVIDIA Capture/EngineCapture/RouteGuard.Tests/Program.vb`
- `Project/NVIDIA Capture/EngineCapture/RouteGuard.Tests/ControlPlaneTests.vb` (new)
- This new evidence fragment.
