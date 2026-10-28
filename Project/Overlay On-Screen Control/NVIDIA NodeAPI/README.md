# NVIDIA NodeAPI — local service and OSC web assets

**Status: active development, 24 October 2026.** NodeAPI serves the CEF/Angular OSC UI, backend routes and selected local bridges. It **does not** perform native game injection or capture/encode frames itself.

## Ownership map

- `index.js`, `package.json` — Node service and route composition.
- `osc/` — [OSC UI source map](osc/README.md), translations (`l10n/`) and project mods.
- `shims/` — bounded proxies for Capture, Gallery, Game Filter and other module APIs.
- `NVIDIA Web Helper/` — managed host of the active Node runtime; its separate `Backend/` subtree is not the active product service.
- `tests/` — Node regressions, security checks and UI contracts.

## Latest architecture audit — 25 October 2026

The read-only [OSC / Backend architecture audit](../../../docs/osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md) separates **CEF native query/frame publishing**, **NodeAPI HTTP/Socket.IO**, **NvContainer policy/supervisor**, **NvCapture media**, **Graphics**, **Gallery**, and **OSC Adapter/Hook**. Its [route inventory](../../../docs/osc/OSC-BACKEND-ROUTE-INVENTORY-20261025.md) counts 336 explicitly declared Express route literals in the inspected JS set: this is a **source inventory, not 336 live and authenticated operations**. Confirm installed Build, worker truth, origin authorization and CEF/game pixels separately.

For Video/Advanced Capture styling and active UI evidence, see [Capture UI v77](osc/mods/CAPTURE-UI-REDESIGN-20261025.md); the six Pipeline Design fields are a **non-persisted preview**, not actual recording backend activation.

## Integration and verification

The typical control path is `NVIDIA Capture ↔ NVIDIA Container ↔ NVIDIA NodeAPI ↔ OSC UI`; native OSC Host and Duluka Hook separately own Overlay frame rendering.

- **Capture:** status, capabilities and display/screenshot proxies must preserve upstream unavailable/denied/unknown states. Runtime may lack endpoints present only in newer source.
- **Gallery:** recent scanner and guarded CopyFile have regression evidence; browser MSE/seek bridge exists, but *real CEF playback* and A/V sync remain acceptance gates. Delete deliberately returns HTTP 501.
- **Security:** loopback same-origin/Host guards mitigate browser-origin requests, **not** fully authenticate a local process. Some patches were source-only at report time—verify deployed hashes.
- **ReShade Manager (guided):** OSC Settings can open an evidence-based inspection screen. Installer/Info must confirm `schemaVersion=1`, `mode=guided`, `nativeInstall=false`, `nativeUninstall=false` and the official Setup URL before the site action is enabled; stale responses are rejected. This is **read-only game-folder inspection plus a handoff to official Setup**, not native DLL installation. Source/Build guards and focused tests were recorded, but a live CEF click-through remains unverified. See [ReShade Manager acceptance report](NODEAPI-RESHADE-MANAGER-20261024.md).
- **Ansel/ReShade effects:** a menu can render while its native named pipe/effect runtime is absent. Report actual connection state, not menu capability.

[Web Helper host](NVIDIA%20Web%20Helper/README.md) · [Notifier map](osc/mods/notifier/README.md) · [Gallery verification](NODEAPI-GALLERY-PLAYER-SEEK-20261024.md) · [Local API security](NODEAPI-LOCAL-API-SECURITY-20261024.md).
