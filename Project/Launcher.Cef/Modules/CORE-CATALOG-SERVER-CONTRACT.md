# ShadowPlay NEXT — Core Module Catalog, Server-owned

Status: CONTRACT PROPOSAL / NOT YET IMPLEMENTED (2026-10-24).

Core Modules are integrated components of ShadowPlay NEXT, not separately operable extensions.
Duluka.Server currently has an account/auth focus; the Launcher Core catalog API has not been verified.
Do not assume an endpoint, login flow, response or catalog version that does not exist yet.

## Ownership
- SERVER owns published Core metadata: name, official version, description, official background/branding, release channel, compatibility, and supported features. Launcher users may only view it.
- LAUNCHER owns local read-only telemetry: installed, missing, running, stopped, unknown. Local status MUST NOT be presented as Server metadata.
- SHADOWPLAY SUITE/SUPERVISOR owns startup, shutdown and updates of all Core components together. No user Core enable, disable, remove, update, per-module preferences, layout editing or background editing UI.
- ADD-ONS are independent optional modules, with their own install/update/enable/disable/remove system after verified backend support.
- Existing data-only .spmod appearance installer stays archived on disk; it is not loaded by the current quick-mode Core UI.

## Proposed read-only catalog structure (illustrative only)
schemaVersion: 1
catalogRevision: string
publishedAt: ISO 8601 UTC
modules[]:
  id: one of Container, Overlay, NodeApi, Capture, Graphics, Helper, Adapter, Hook, Ansel, Ffmpeg, Config
  displayName: string
  version: string or null
  description: string
  kind: core
  lifecycle: suite-managed
  userEditable: false
  userInstallable: false
  userRemovable: false

## Server integration acceptance criteria
1. Build an authenticated, versioned, documented read-only HTTPS catalog contract; administrative publication has a separate access scope. Do not invent an active URL.
2. Validate response schema, IDs, bounded strings, safe asset URLs and release provenance; reject executable code/HTML/commands.
3. Browser/native bridge must not expose sensitive account session tokens in URLs, logs or DOM. Use vetted allowed origins and TLS.
4. Show three distinct facts: remote catalog available or unavailable, catalog revision (or clearly labelled bundled fallback), and local runtime status.
5. If offline, keep read-only bundled names/descriptions without invented versions, update success or fabricated Server data.
6. Tests cover offline, invalid/malformed data, stale cache, unauthorized requests, and zero Core mutation actions.

## Implemented in Launcher now
- 11 Core cards, all read-only; status still from local Launcher.
- Local Theme/Background/Settings actions removed from the visible Core flow.
- DOM marks data-catalog-source=bundled-fallback and data-runtime-source=local-launcher.
- UI notice states that the Server catalog is not connected yet.
- Suite-level ShadowPlay power control remains; it is not a per-Core control.
- No server code has been changed. Do not equate UI compliance with completed server integration.

Launcher files: Project/Launcher.Cef/ui/index.html, quick-ui.js, quick-ui.css, i18n.js.
Preflight and deploy: Project/Launcher.Cef/Modules/deploy-module-layout.cjs.
Runtime verification: build_test/Launcher ModuleLayout_Test/verify-runtime.cjs.
