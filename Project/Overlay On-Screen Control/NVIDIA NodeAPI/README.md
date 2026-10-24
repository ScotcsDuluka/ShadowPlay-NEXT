# NVIDIA NodeAPI — local service and OSC web assets

**Status: active development, 24 October 2026.** NodeAPI serves the CEF/Angular OSC UI, backend routes and selected local bridges. It **does not** perform native game injection or capture/encode frames itself.

## Ownership map

- `index.js`, `package.json` — Node service and route composition.
- `osc/` — [OSC UI source map](osc/README.md), translations (`l10n/`) and project mods.
- `shims/` — bounded proxies for Capture, Gallery, Game Filter and other module APIs.
- `NVIDIA Web Helper/` — managed host of the active Node runtime; its separate `Backend/` subtree is not the active product service.
- `tests/` — Node regressions, security checks and UI contracts.

## Integration and verification

The typical control path is `NVIDIA Capture ↔ NVIDIA Container ↔ NVIDIA NodeAPI ↔ OSC UI`; native OSC Host and Duluka Hook separately own Overlay frame rendering.

- **Capture:** status, capabilities and display/screenshot proxies must preserve upstream unavailable/denied/unknown states. Runtime may lack endpoints present only in newer source.
- **Gallery:** recent scanner and guarded CopyFile have regression evidence; browser MSE/seek bridge exists, but *real CEF playback* and A/V sync remain acceptance gates. Delete deliberately returns HTTP 501.
- **Security:** loopback same-origin/Host guards mitigate browser-origin requests, **not** fully authenticate a local process. Some patches were source-only at report time—verify deployed hashes.
- **Ansel/ReShade:** a menu can render while its native named pipe/effect runtime is absent. Report actual connection state, not menu capability.

[Web Helper host](NVIDIA%20Web%20Helper/README.md) · [Notifier map](osc/mods/notifier/README.md) · [Gallery verification](NODEAPI-GALLERY-PLAYER-SEEK-20261024.md) · [Local API security](NODEAPI-LOCAL-API-SECURITY-20261024.md).
