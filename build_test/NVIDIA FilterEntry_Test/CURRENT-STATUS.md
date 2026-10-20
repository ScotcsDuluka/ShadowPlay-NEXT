# NVIDIA Game Filter — Current Status

Updated: 2026-10-20, Asia/Bangkok
Device: AORUS-DESKTOP

## Completed / verified
- Current source regression across all 8 Node test files: **26/26 passed, 0 failed** (rerun during this check).
- gameFilterUi.test.js includes simulated cold startup, deferred initialization, navigation to mods, duplicate route coalescing and failed init handling.
- Source/build SHA256 previously matched for shims/game-filter-route.js, shims/ansel-client.js and osc/mods/ansel-game-filter.js.
- Read-only diagnostics tool diagnose-game-filter.cjs refreshed snapshot in diagnostic-snapshot.json.
- Latest regression output saved in automated-tests-full.txt.

## Current runtime / blocker
- Game Filter State: **503 ANSEL_UNAVAILABLE** (no responding D3D11 ReShade runtime).
- Settings: 200, bridge enabled, mode reshade.
- Game Watcher: running, catalogReady, 0 candidate processes and 0 matched games.
- NVIDIAAnsel named pipes: 0.
- CEF: #/base, native initialization promise present but no connected runtime.
- 503 currently indicates honest unavailability; do not mask with HTTP 200 or restart services as a guessed fix.

## Next actions when game runtime is present
1. Run read-only diagnose-game-filter.cjs; check GameFilter/State=200 and Ansel pipe present.
2. Verify main-menu editor entry, effect catalog, and Alt+F3.
3. Verify Off, close, reopen, saved choice -1, and empty runtime stack.
4. Verify cold reload at #/base/mods automatically enters mods without deadlock or JS exceptions.
5. Verify saved Off versus external ReShade mutations; two-way synchronization not yet verified.
6. Record live evidence and update HANDOFF; patch only reproduced issues and rerun regression.

## Safety / save
- Do not launch/restart SMASH LEGENDS or stop/start Web Helper during unattended verification.
- Do not change other chat's Native OSC code or native NVIDIA CSS/controls.
- ReShade installer remains UI-only.
- Existing backend changes await user's normal application restart; do not force one.
- No production edits or Git commits in the latest check.
- Git reports relevant Game Filter source/test and build_test as untracked. Files exist on disk, not committed.
