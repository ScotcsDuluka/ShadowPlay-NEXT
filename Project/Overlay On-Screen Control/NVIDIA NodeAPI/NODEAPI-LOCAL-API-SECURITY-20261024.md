# NVIDIA NodeAPI — Local API Security (2026-10-24)

Scope: `Project/Overlay On-Screen Control/NVIDIA NodeAPI` source only. No Build staging, runtime restart, Capture start, GPU restart, or settings changes.

## Changes

- Added `localApiSecurity.js` and mounted its Express middleware immediately after app creation in `index.js`, before static/UI/Backend routes.
- Reused existing `localGpuRequestGuard.js` for loopback remote address, loopback Host, exact same-origin Origin, and `Sec-Fetch-Site` policy. Added same-origin Referer checking.
- Native loopback callers without browser Origin / Referer remain permitted, including ContainerEvents, the legacy hotkey POST, and local host health.
- Reject browser cross-origin/opaque origin, cross-site fetch metadata, invalid Host, and non-loopback requests with HTTP 403.
- Deny root `GET /?hk=...` (HTTP 405) to prevent hotkeys being triggered by browser navigation or image embeds. `POST /?hk=...` remains available to native helpers.
- Engine.IO/Socket.IO bypasses Express middleware, so installed `io.eio.allowRequest` with the same check to reject cross-origin handshakes (Engine.IO responds HTTP 400).
- Removed wildcard `Access-Control-Allow-Origin: *` headers from the NodeAPI middleware and `/cefquery`. Same-origin OSC continues using relative HTTP requests.
- Intentionally **did not turn on NVIDIA's legacy Security Cookie**: the existing CEF/bootstrap path advertises `disableSecurity: true`. Flipping that flag alone would break legitimate UI requests. This guard is separate from that legacy mechanism.

## Tests

- `node --check index.js`, `node --check localApiSecurity.js` — PASS.
- `node --test tests/localApiSecurity.test.js tests/localApiSecuritySocket.test.js` — **5/5 PASS**. Actual Engine.IO denied-handshake tested on an ephemeral loopback server.
- Combined Security + Catalog + GPU + Capture UI tests — **25/25 PASS**.
- Wider safe source test sweep at time of audit — **341/354 PASS**, **13 UI/Customize failures** (other agents are concurrently modifying OSC UI files). This is NOT a release pass; it is not evidence that security changed those UI components.

## Deployment and limits

- The running NodeAPI service on `127.0.0.1:59011` is launched from `Build/NVIDIA ShadowPlay/...`, not `Project/...`.
- No live service was modified. The current Build does not contain this security patch yet.
- The canonical `Scripts/build-dev.ps1` stages NodeAPI top-level `.js` files and the `shims` tree, so the new source module is included in the next coordinated Build.
- This mitigates browser-origin CSRF and DNS rebinding; it **does not authenticate local processes**. Same-user local software can still call loopback APIs.
- Full CEF/browser, Native Helper, and deployed Engine.IO compatibility still need a coordinated Build and runtime acceptance check. Existing external OAuth redirect paths, if used, should be checked because intentional cross-site HTTP requests are denied.
- Do not overwrite other agents' dirty work or restart live services without coordination.
