# ShadowPlay NEXT — ReShade Manager UI + Installer Preflight

**Date:** 2026-10-24
**Owner:** Overlay On-Screen Control / NVIDIA NodeAPI
**Scope:** Guided UI and read-only installer preflight only. Game Filter/Presets, Duluka Hook and Anti-cheat integrations remain untouched.

## Completed

1. Enabled **Open ReShade Manager** button in `osc/mods/gpu-status-panel.js` under OSC Settings > Game filter, ReShade Freestyle mode. Retained existing NVIDIA AngularJS controls and saved Game Filter settings.
2. Added `osc/mods/reshade-manager.js` and `osc/mods/reshade-manager.css` with NVIDIA-style UI: executable path, user-selected Graphics API, read-only inspection, Installed / Not Installed / Unknown with evidence, official guided Install / Update / Uninstall, warning and confirmation, progress/error, verify-after-setup and Back.
3. Wired pre-existing `shims/reshade-installer-route.js` to `index.js` after local API-origin security middleware. Added both new assets to the explicit `/mods/list` allowlist.
4. Extended inspection to return `installationStatus`, `installationScope: game-directory-only`, `graphicsApiHints`, `graphicsApiVerified:false`, and `gameRunning` status by read-only Windows tasklist. A process **name** match is only `possible` running, not proof of executable path; `not-seen` is not proof the game is closed.
5. Hardened the preflight against UNC/network paths: Windows absolute **local drive** paths ending with .exe only.
6. User-confirmed handoff opens **https://reshade.me/**, not a downloaded binary. User must run official Setup manually. No NodeAPI Install/Update/Remove write routes, DLL replacement, preset editing, injection or Anti-cheat bypass were added.

## Installer evidence rules

- **Installed:** ReShade-identifying log plus an API proxy or ReShade.ini, based on directory evidence. No cryptographic verification of DLL ownership.
- **Unknown:** Unidentified proxy DLL, ReShade.ini, or ReShade.log without sufficient corroboration. Never overwrite an unknown DLL.
- **Not Installed:** No signs in inspected local directory **only**. Does not exclude global/alternate-path installations.
- API hints from proxy filenames (DX9, DX10/11/12, OpenGL) are suggestions only, not verified game render APIs. Vulkan can be selected manually. Official Setup owns the final API/installer decisions.

## Tests and deployment

- Node --check on modified source/Build JS: PASS.
- Related Gallery, GPU, Capture, NodeAPI Security and ReShade focused tests: **75/75 pass**.
- Deployed `shims/reshade-installer-route.js`, `osc/mods/reshade-manager.js`, `osc/mods/reshade-manager.css` with SHA-256 source/Build verification.
- Patched **only ReShade-related changes** into deployed `index.js` and `osc/mods/gpu-status-panel.js`, instead of overwriting unrelated pending work.
- Backup: `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-ReShadeManagerBackup-20261024-131643`.
- Restarted only managed NodeAPI (Web Helper and child NvNode). At restart verification: Web Helper PID 31284 / NvNode PID 3848 (PIDs may change).

## Live Runtime

- `GET /Backend/v.1.0/health`: 200.
- `GET /Duluka/v.1.0/ReShade/Installer/Info`: 200 (`mode:guided`, `nativeInstall:false`, `nativeUninstall:false`).
- `POST /Duluka/v.1.0/ReShade/Installer/Inspect` with **an installed Steam game**: 200, `installationStatus:Not Installed`, `installationScope:game-directory-only`, `graphicsApiVerified:false`, `gameRunning:not-seen`, `mutationSupported:false`, no local ReShade DLL/config/log/shaders evidence.
- `GET /mods/list`: 200 and lists both new ReShade assets.
- `GET /mods/reshade-manager.js` and CSS: 200.
- Original Game Filter Settings remains HTTP 200.
- Cross-origin request against installer Info: HTTP 403.
- `POST /Duluka/v.1.0/ReShade/Installer/Install`: HTTP 404 (no native file-changing endpoint).

## Acceptance limitations / next checks

- Actual interactive CEF UI appearance, tab focus, manual executable selection, confirmation and recovery after official Setup still need a live visual click-through. HTTP and unit testing are not equivalent to CEF acceptance.
- Official ReShade Setup was **not launched or run**: user must explicitly confirm and run it; no game files were changed by the assistant.
- UI uses manual absolute game executable path entry; no native file picker or Game Watcher chooser is included yet.
- Does not modify `NVIDIA Adapter\Ansel Adapter\ReShade Ansel`; coordinate only engine compatibility in later scoped work. Game Filter engine/presets are intentionally paused.

## Follow-up 2026-10-24 21:37 (OSC Manager safety / CEF acceptance)

- Found a Setup safety race: successful game inspection previously enabled the official-website action before Installer/Info verified the guided contract.
- osc/mods/reshade-manager.js now requires schemaVersion=1, mode=guided, nativeInstall=false, nativeUninstall=false, and officialUrl=https://reshade.me/ before enabling Setup. It starts fail-closed on every opening.
- infoVersion invalidates stale Info responses after Back / Reopen, preventing a delayed previous-open response from authorizing the current opening.
- New DOM/Angular stub tests in tests/reshadeManagerContract.test.js verify: safe contract pending, contract rejected, new opening reauthorization, stale response, and API failure.
- Focused regression: **10/10 PASS** (reshadeManager.test.js + reshadeManagerContract.test.js). node --check PASS.
- Deployed only the new OSC ReShade Manager JS to the running Build folder (no game DLL changes, no Hook/Container/NodeAPI process restart).
- Runtime JS SHA-256: 835BDA0FA9563BBB172B395FAA440F5353D021F6D5253B8A264E60E7C1760D35.
- Backup: C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-ReShadeManagerBackup-20261024-213728\reshade-manager.js.
- Live Health, Installer/Info, and updated JS: HTTP 200.
- **CEF visual/click acceptance remains NOT VERIFIED**: OSC process exists, but Windows desktop screenshots did not display the OSC overlay; an isolated headless Chrome snapshot could not be created. No claim of working real CEF click-through is made.
- The existing CEF renderer may retain the old JS until OSC page reload; avoid restarting the active OSC without coordination. Test Settings > Game filter > Open ReShade Manager and return/verify interactions after a normal reopen.
- Official ReShade Setup was never launched; no installation/update/uninstall performed and no game files modified.