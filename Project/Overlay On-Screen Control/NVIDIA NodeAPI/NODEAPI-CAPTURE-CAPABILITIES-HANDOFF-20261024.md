# NVIDIA NodeAPI — Capture Capabilities Read-Only Gateway (2026-10-24)

Owner: NVIDIA NodeAPI. Scope: NodeAPI source + its Build deployment only.

## Implemented
- `GET /DulukaCapture/v.1.0/capabilities` from NodeAPI forwards **GET only** to NvContainer `/v1/capture/capabilities` on configured loopback control port.
- New source module: `shims/capture-capabilities.js`. Mounted in `index.js` after the local-origin security middleware.
- Preserves valid Container payloads, including truthful unavailable/invalid policy. Validates schema, source, availability, policyValid, provider and workflow shapes; bounded response (128 KiB), timeout, no codec guessing.
- On missing upstream route, HTTP error, malformed response or timeout, returns HTTP 503, `availability: "Unavailable"`, `policyValid: false` and error code. Does **not** fabricate worker readiness or capabilities.
- Tests: `tests/captureCapabilities.test.js` (6 cases).
- Combined focused Capture/Security/OSC UI tests: **35/35 pass**.
- Runtime Build: staged `index.js` and `shims/capture-capabilities.js`, restarted only NVIDIA Web Helper / NvNode.
- Runtime verified: `/Backend/v.1.0/health` HTTP 200, capabilities HTTP 503 as expected (Container route not deployed), `/DulukaCapture/v.1.0/engines` HTTP 200 carrying `policyValid:false`, `/DulukaCapture/v.1.0/status` HTTP 200 for worker health, cross-origin blocked HTTP 403.
- Restart PIDs at verification: Web Helper 25348; NvNode 3160.
- Rollback backup: `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-CapabilitiesBackup-20261024-102740`.

## Precisely verified external blockers (outside NodeAPI owner)
1. `Project\NVIDIA Config\capture-engines.json` **exists**, but `Build\NVIDIA ShadowPlay\NVIDIA Config\capture-engines.json` **does not exist**.
2. Both NodeAPI /engines (:59011) and Container /v1/capture/engines (:59012) report `policyValid:false`, `Capture routing policy unavailable: FileNotFoundException`. NodeAPI faithfully forwards upstream.
3. Current deployed Container `GET /v1/capture/status` and `GET /v1/capture/capabilities` return HTTP 404. The new source contract exists under `Project\NVIDIA Container\CAPTURE-COMMUNICATION-HUB-CONTRACT-20261024.md` but runtime Build lags.
4. Deployed NvCapture `GET /health` returns ready true and recording false, BUT `GET /state` lacks `actualSessionBackend` and `GET /engines` returns `{"error":"not_found"}`. Thus worker service health **does not verify current capture codec, routes or actual session backend**.
5. Do not claim an actual capture engine is recording-capable based solely on worker health, routeWired or baseline policy.

## Handoff — NVIDIA Container / NVIDIA Capture owners
- Container owner: stage the existing validated policy via the dedicated installer, deploy its new Capture Communication Hub routes and verify API contracts. Avoid silently overwriting an existing user policy. Confirm Container health before/after its own restart.
- Capture owner: deploy NvCapture v2 `/engines` and contract-compliant `/state`, verifying real runtime worker capabilities, not invented provider readiness.
- NodeAPI owner: when those runtimes are deployed, rerun read-only `/DulukaCapture/v.1.0/capabilities`, `/engines` and integrate the Container authoritative `/status` contract without breaking the existing OSC Video Settings response shape.
- This task did not start/stop recording, change capture config, change Container binaries or restart NvCapture.
