# Game Filter handoff — 2026-10-20

## User constraints
- Do not restart SMASH LEGENDS. Another GLM chat may own native OSC changes.
- Retain NVIDIA native CSS and controls. ReShade installer remains UI-only.
- Explicit reset requested and attempted: noCredit; available reset credits 0.

## Changed source and deployment
- Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/ansel-game-filter.js
- Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/gameFilterUi.test.js
- Runtime JS copied to build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/ansel-game-filter.js; frontend reloaded only. No game/backend process restart.
- Original runtime JS backup: ansel-game-filter.before.js in this folder.
- Previous dropdown fix: gpu-status-panel.css adds scoped md-select-value padding 0 10px and border-box; copied/live verified.

## Implemented
- Fast editor entry requires successful preparation for current pid/session/generation, enabled mode and real per-game slots; default empty slots are not readiness.
- Re-arm native FILTERS listener false/true before catalog request; native one-shot listener otherwise silently removes itself while retaining its internal enabled flag.
- Coalesce concurrent native enable preparation; old session completion cannot prepare a new session.
- Preserve current active slot, including Off, on reopen. Entry sentinel skips native internal clear; explicit style click clears sentinel before Angular so Off is not swallowed.
- DulukaModsSelectionStorage maps executable to zero-based preset index or -1 for Off. Capture records explicit UI choice. Native ModsDataStorage writes merge this selection so delayed shutdown cannot re-enable old preset.
- Snapshot current per-game ModsSlotStorage on shutdownModsUI, preserving other games; no full serialization during slider dragging.
- Malformed per-game native data does not throw in the merge.
- Direct editor route goes through verified native entry instead of treating a connected pipe as full initialization.

## Confirmed tests
- 23 automated tests passed across gameFilterUi, gameFilter, anselClient, gameFilterSettings, freestyleSettingsUi. See automated-tests.txt.
- Real running SMASH LEGENDS, PID 18652, D3D11; session 18652:7840640:4, generation 2; actual catalog RadiantGI, BaBa: PHDR, FXAA.
- live-smoke.cjs passed opening from main menu, catalog options, choosing empty preset 2, Off reset with runtime stack [], closing and reopening with Off retained. No captured JS exceptions.
- Evidence: live-before-reload.json, catalog-populated.png, catalog-options.png, off-reopened.png.

## Unfinished — verify this first
- Cold reload directly at #/base/mods initially deadlocked: route guard prevented base.onEnter, which starts socket and later nvCameraService.init.
- Latest additional source/runtime patch wraps nvCameraService.init with __dulukaNativeInitialization promise. Initial route guard first enters main.main-menu, then waits for native init before verified editor launch; pending entry is coalesced.
- That final bootstrap addition was copied/reloaded but is NOT fully validated. Latest read-only final-live-check.json: hash #/base, modsSupported true, modsIsEnabled false, UIRunning false, all four slot catalogs have 3 effects, saved choice -1, native enabled false. Editor was closed; this does not prove deep reload automatically reopened it.
- Earlier direct-reload smoke failed; do not claim all reload cases fixed. Reproduce after quota resets, validate normal menu entry, Alt+F3, Off close/reopen, then direct reload. Do not restart game.
- Last passing 23-test suite preceded this final bootstrap addition. Rerun appropriate UI regression after checking the interrupted test-agent edits; no further agents needed.
- Consider external ReShade edits after saved Off: stored choice currently takes precedence on cold entry. Do not silently claim two-way live synchronization is fully verified.

## Prior backend limitation
- Earlier game-filter-route.js/ansel-client.js permission-race changes passed source tests and were copied to runtime, but existing Web Helper process was not restarted because automatic approval rejected stop/start. Those backend changes await normal application restart. This task did not retry that action.

## Useful runtime access
- CDP http://127.0.0.1:59099/json/list (select localhost:59011 page; fetch current websocket URL each time).
- HTTP http://127.0.0.1:59011/Duluka/v.1.0/GameFilter/State and /Settings.
- Node C:/Users/ScotcsDuluka/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe
- CEF JavaScript has no optional chaining. No need to rebuild native C++.

## Continuation — 2026-10-20 (Desktop Commander)
- Confirmed AORUS-DESKTOP connection and inspected source, runtime notes, tests and CDP target.
- Reran the five selected regression files on current source: 23/23 passed.
- Added a cold-start deferred `nvCameraService.init` regression to `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/gameFilterUi.test.js`: initial direct mods route bootstraps main menu, coalesces duplicate route attempts, does not invoke native entry before initialization, and enters exactly once afterward. All 23 tests still pass.
- Backup of pre-change UI regression test: `build_test/NVIDIA FilterEntry_Test/gameFilterUi.before-bootstrap.test.js`.
- Live game confirmation remains BLOCKED: GameFilter State HTTP returns 503; CDP page is alive at `http://localhost:59011/index.html#/base`; SMASH LEGENDS process was not seen in the targeted process check. Did not start/restart game or Web Helper or change runtime JS.
- Next: when real runtime becomes available, verify menu entry, Alt+F3, Off close/reopen, and direct `#/base/mods` reload without restarting the game for this task. Maintain Native OSC coordination.

## Autonomous continuation — 2026-10-20 (~16:18 ICT)
- Investigated 503 without restarting game or services. Web Helper process and OSC alive; HTTP /GameFilter/Settings=200, /OSC/Interface=200, /GameFilter/State=503 with JSON {"connected":false,"code":"ANSEL_UNAVAILABLE","message":"No responding D3D11 ReShade runtime is connected"}.
- ReShade named-pipe enumeration showed zero NVIDIAAnsel.* matches. Game Watcher HTTP 59012/v1/game-watcher/status reported running/catalogReady true, 0 candidates, 0 matched games, no lastError. A 503 while no runtime exists is EXPECTED, not evidence of failed backend service. Avoid changing it to 200 or restarting the backend to 'fix' this.
- Verified SHA256 source/build match for shims/game-filter-route.js, shims/ansel-client.js, and osc/mods/ansel-game-filter.js. No native processes or build JS were modified during this continuation.
- Expanded gameFilterUi.test.js cold route simulation to check successful navigation back to "mods" after deferred initialization, duplicate route request coalescing, and rejection/failure behavior (must not enter editor and must show required-game warning). No production source changes were needed. Old UI test backup remains gameFilterUi.before-bootstrap.test.js.
- Ran ALL eight tests/*.test.js suites (26 Node tests), 26 passed, 0 failed, exit 0. Full output is automated-tests-full.txt.
- Added read-only diagnose-game-filter.cjs (works with Node v24): GETs GameFilter State/Settings and game watcher, lists NVIDIAAnsel pipes, and inspects CEF route/prepared/catalog via CDP Runtime.evaluate without navigation or mutation. Snapshot in diagnostic-snapshot.json; rerun from repo root or anywhere with:
  & "C:/Users/ScotcsDuluka/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe" "C:/My Project/NVIDIA-Shadowplay/build_test/NVIDIA FilterEntry_Test/diagnose-game-filter.cjs"
- During read-only CEF check page was #/base, native initialization promise existed, no connected runtime/prepared token, UIRunning=false, 4 empty slot catalogs. This is correct while the target game is not running.
- Live smoke remains UNVERIFIED: only once an actual compatible game/ReShade runtime is connected, validate normal menu open, Alt+F3, Off select/close/reopen, and direct #/base/mods cold reload (safely, not restarting the game). Do not run live-smoke.cjs while runtime absent. Do not launch/restart SMASH LEGENDS or modify Native OSC managed elsewhere.
- Limitations: saved Off versus external ReShade edits remains intentionally unverified; historical backend changes still need the *user's normal application restart* to take effect. No service restart was performed here.

## Status checkpoint — 2026-10-20 (Desktop Commander, user requested save)
- Reran read-only diagnostics on AORUS-DESKTOP: State=503 ANSEL_UNAVAILABLE, Settings=200 bridge enabled, Game Watcher healthy with zero candidates/matches, NVIDIAAnsel pipes absent, CEF #/base. Refreshed diagnostic-snapshot.json.
- Reran every Node tests/*.test.js regression: **26/26 PASS, 0 fail**. Refreshed automated-tests-full.txt.
- Saved quick status and safe next steps in CURRENT-STATUS.md.
- No game or service restart, no Native OSC change, no production code modifications, no Git commit. Working files are saved on disk; relevant project files are untracked.
- NEXT: only when real game/adapter is connected, re-run diagnostics then test menu entry, Alt+F3, Off close/reopen and direct #/base/mods cold reload. External ReShade/Off sync remains unverified.
