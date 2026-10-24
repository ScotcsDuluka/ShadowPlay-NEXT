# NVIDIA NodeAPI — Capture Status GET Fix (2026-10-24)

## Scope
NodeAPI only. Source: `Project/Overlay On-Screen Control/NVIDIA NodeAPI`.
The user has authorized direct NodeAPI Build / Restart without per-run confirmation.

## Confirmed issue
OSC Video Settings `osc/mods/video-capture-options.js` calls `GET /DulukaCapture/v.1.0/status`, but the old handler was mistakenly located inside `m === 'POST'` in `shims/route-floor.js`. The live API returned 404 despite NvCapture `/health` being 200. In the disabled POST branch the legacy handler also referenced `G`, which was only scoped inside GET routes.

## Resolution
- Added `shims/capture-runtime-status.js` with read-only GET and backwards-compatible POST.
- Mounted it in `index.js` after the local-origin security middleware and before the route floor.
- Removed dead/misplaced legacy POST handler from `shims/route-floor.js`.
- Strictly validate NvCapture `/health`: HTTP 200, JSON object, `ok === true`, boolean `ready` and `recording`; fail closed on invalid/oversized/error/timeout response, and never display recording when worker is not ready.
- Added `tests/captureRuntimeStatus.test.js` with 6 cases.
- No capture start/stop, policy write, or OSC UI edit was performed.

## Verification
- Source JS syntax: PASS.
- Focused Catalog, Capture Status, UI and Security regression: **29/29 PASS**.
- Deployed exactly 3 NodeAPI files to existing Build and restarted managed NVIDIA Web Helper/NvNode.
- Live `GET http://127.0.0.1:59011/DulukaCapture/v.1.0/status`: **HTTP 200** `{"enabled":true,"ready":true,"recording":false}`.
- Legacy POST read-only endpoint: HTTP 200 with matching values.
- `/Backend/v.1.0/health` and `/DulukaCapture/v.1.0/engines`: HTTP 200.
- External Origin blocked: HTTP 403; same origin allowed: HTTP 200; hotkey GET blocked: HTTP 405.
- New Web Helper PID 1672; NvNode PID 15364 at verification time.
- Backup: `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-StatusFixBackup-20261024-102323`.

## Still unresolved / next work
NvContainer Capture Catalog still reports `policyValid=false` due to `Capture routing policy unavailable: FileNotFoundException`; this is a Container/Capture policy issue, **not** a NodeAPI GET status issue. Need separate owner investigation. UI/Customize regression failures from other parallel work were not modified in this fix.
