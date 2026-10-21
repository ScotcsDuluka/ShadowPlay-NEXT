# Latest checkpoint — Game Filter / Off persistence

Read this checkpoint first. The previous document below is preserved as historical context; its claims about the current task/layout have not been revalidated.

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

## Compact Overlay Sidebar — 2026-10-20
- User selected visible 4x60px sidebar over 4x80px scrolling. Source/live `osc/mods/gpu-profiles.js` modified and matched by SHA256; only CEF reloaded. Live CDP smoke: 4 native button rows fit in 240px; scrolling disabled; existing Game Profiles click opens `main.gpu-profiles`; center/right tiles remain fixed; no JS exceptions. Node tests 26/26 pass. Backups, screenshot, logs and full details: `build_test/NVIDIA LeftSidebar_Test/HANDOFF.md`. No game or backend restarts and no native OSC changes.


---

# Previous checkpoint — historical context

# CODEX HANDOFF — NVIDIA ShadowPlay / GeForce Experience OSC
**As of:** 2026-10-20 (Asia/Bangkok)  
**PC:** AORUS-DESKTOP (Windows)  
**Repository:** `C:\My Project\NVIDIA-Shadowplay`  
**Authoring purpose:** User is moving ongoing coding work to Codex. READ THIS FILE FIRST, then inspect current files. This checkpoint is documentation ONLY: no UI code changes are requested as part of writing this handoff.

## 0. What the user wants (สำคัญมาก)

- **Game Profiles must be visible and clickable directly in the REAL NVIDIA Overlay Main-menu (OSC)**, not inside a submenu.
- **Keep the ORIGINAL NVIDIA visual language**: black/translucent square tiles, existing native icons, fonts, layout behavior, selection/focus green outline. **No redesigned neon/glassmorphism/rounded-corner image mockups.**
- Changes must appear **in the running OSC itself**, not merely as a generated image.
- **Responsive, visually balanced and not cramped on the user's actual screen**. User repeatedly rejected layouts that looked distorted or too wide.
- Do not touch game launch/restarts, native OSC being edited by another chat, or unrelated Game Filter logic without coordinating.
- User approved experimenting in real OSC, but **has NOT approved the current 5x240 layout visually**.

## 1. CURRENT PROBLEM — next Codex task

**The current live experiment is still UNACCEPTABLE VISUALLY** according to the latest user screenshot. We added Game Profiles as a fifth 240x240 center tile; total width became excessive and pushes the entire Main-menu toward screen edges / right-side controls on the user's actual Windows/browser view. The earlier 320px-high experiment also looked ugly/distorted.

**Do NOT claim success from CEF smoke tests alone.** Those tested the DOM at a 1920x1200 CEF viewport, while the user's actual screenshot shows the Overlay significantly more crowded. Need inspect effective real viewport / scaling / game-window size and test at that width, not assume 1920px everywhere.

**Suggested NEXT experiment (NOT YET IMPLEMENTED OR TESTED):**
- Keep original OSC Main-menu total height of **240px**.
- Keep left **Screenshot / Photo mode / Game filter**, native 3 x 80px.
- Keep right **Performance / Gallery / Mic / Camera / Settings / QuietMode** recognizable and usable.
- Explore **five center tiles at ~192px width** instead of 240px, so 5 x 192 = 960px = original four-center-tile total width (4 x 240). May require proportional/native-font/icon sizing and careful focus/submenu placement. This is only an idea; inspect responsive/native CSS constraints before implementing.
- If 5 x 192 looks squashed, propose an **alternative original-NVIDIA-looking composition** that keeps Game Profiles visible in main-menu, not a hidden submenu.
- Avoid hand-coded assumptions tied to the 1920x1200 CEF screenshot; test at the actual user's narrower visible window as well as 1920.

**Highest-priority acceptance criteria:** user visually approves; Game Profiles clickable; 3 original left controls, center recording controls, Performance/Gallery/utility controls and keyboard shortcuts still work; no overlapping or clipped panels at user's actual resolution.

## 2. Current deployed files (DO NOT confuse source with live runtime)

Source code:
`Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/gpu-profiles.js`

Live build/runtime JS:
`build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/gpu-profiles.js`

On handoff these **two files have identical SHA256**:
`05731B6ACC515FCF96F150F7F5DB1DF3560C4FAD07EAC32A2CD2BA7CA8613C40`

**Current design logic** in `gpu-profiles.js`:
- `ensureMainMenuTileStyle()`: injects CSS scoped to `.main-menu-container`, widens Main-menu, sets 5th `md-menu` to 240x240, keeps right utilities 80x80, adjusts menu horizontal offset. **This is where visual/layout defect lives.**
- `updateMainMenuPerformance()`: creates `tiles.GameProfiles` with the fifth native `nv-osc-tile` layout and retains Performance/Gallery placement.
- `name: 'nv-gpu-profiles-center-tile'`, `currentState: 'Ready'`, `state.Ready.icon = 'icon-edit_tune'`, `state.Ready.status = 'Open'` are needed because NVIDIA's center tile template uses state-sourced icon/status. First version lacked the icon until this was fixed.
- `openGameProfilesFromMainMenu()`: existing route `main.gpu-profiles`; actual browser hash `#/base/preferences/game-profiles`.
- These native Angular `md-menu` buttons retain their existing `ng-click` handling; do not replace them with stand-alone unbound HTML.
- The current fifth tile is placed AFTER Broadcast LIVE; center tiles have 240x240 original NVIDIA styling.

**Git as of handoff:** `gpu-profiles.js` source is MODIFIED (not committed). This handoff and multiple `build_test` experiment folders are not committed. Source/runtime build copies exist on disk. Check `git status --short` before modifying anything because another assistant may change files.

## 3. Timeline of UI experiments (and user feedback)

1. **Original baseline (before UI placement experiments):** 3 native left 240x80 buttons; 4 center 240x240 tiles; right Performance/Gallery and 80x80 utilities. Game Profiles existed as an **icon-only 80x80 tile at far left of the Game filter row**. Code worked but looked visually disconnected. Original SHA256 `75FD81DC95B42163E01E547A7E56FECA98C3C544C24331AC0D730D9DD457CF2F`.
2. **Left scroll experiment:** reparent four left tiles into a 240px tall scrolling sidebar; fourth Game Profiles was below fold. User rejected: odd proportions and unnecessary scroll for four buttons.
3. **Compact left 4x60px experiment:** all four fit into 240px. Technically passed mouse-wheel and routing tests. User rejected: icons/text too squeezed.
4. **320px-high experiment:** left four 240x80 buttons; center four 240x320; right Performance/Gallery 120px-high rows. Technically passed, but user screenshot showed awkwardly stretched central tiles and excess vertical space; **visually rejected**. SHA256 `5BBA94C293BADAF352982CFB4EF2E5B6E25EE280E0F9260D73E480E54E7E7FFA`.
5. **Current native five-tile 240x240 experiment:** restores original height, adds fifth center Game Profiles tile. CEF geometry/tests pass, icon/title/Open render, but **real user's screen screenshot shows crowded, over-wide overlay**. **This is current live design and NOT visually approved.**
6. Image mockups with glossy green neon/rounded cards were rejected. **Do not generate mockups as the solution**; user wants actual NVIDIA OSC and original aesthetic.

## 4. Existing backups, detailed notes and test evidence

All paths relative to repo root.

**Current five-tile experiment:**
`build_test/NVIDIA NativeFiveTile_Test/`
- `HANDOFF.md` — detailed five-tile technical checkpoint.
- `native-five-smoke.cjs` — real OSC CEF DevTools smoke test (test currently asserts positions at 1920x1200 only; update for new layouts).
- `native-five-smoke.json` — passing DOM geometry/route result for 5 x 240 (NOT a visual acceptance test).
- `native-five-live.png` — CEF screenshot at 1920x1200; NOT user's rejected screenshot.
- `automated-tests-full.txt` — 26 passing Node tests.
- `gpu-profiles.before-five.source.js` and `gpu-profiles.before-five.runtime.js` — backup of rejected **320px** layout.

**Original baseline and two left-panel experiments:**
`build_test/NVIDIA LeftSidebar_Test/`
- **True pre-sidebar baseline backup:** `gpu-profiles.source.before.js` and `gpu-profiles.runtime.before.js` (SHA256 `75FD81DC95B42163E01E547A7E56FECA98C3C544C24331AC0D730D9DD457CF2F`). Restore these if reverting ALL layout experiments to original icon-only Game Profiles placement.
- `gpu-profiles.before-compact.source.js` / `gpu-profiles.before-compact.runtime.js` — 4x80 scrolling variant.
- `gpu-profiles.compact.saved.js` — 4x60 compact variant.
- `HANDOFF.md` — backup/rollback history.
- `inspect-menu.cjs` — read-only DOM inspect while navigating CEF temporarily to Main-menu then back to base.
- `reload-frontend.cjs` — CDP `Page.reload` **only if idle at #/base**; no backend/game restart.
- CEF screenshots `sidebar-top.png`, `sidebar-scrolled.png`, `sidebar-compact-live.png`.

**320px experiment:**
`build_test/NVIDIA MainMenu320_Test/`
- `HANDOFF.md`, `mainmenu320-live.png`, `mainmenu320-smoke.cjs`, `mainmenu320-smoke.json`, backup and regression output.

**Screenshot from user's current actual overlay:** visible in this conversation (narrower than 1920 CEF screenshot), **not saved into the above PC experiment folder**. Ask user / inspect actual viewport, do not replace it with a clean 1920px CEF screenshot as evidence of visual acceptability.

## 5. Tests / running OSC tooling

Last full Node regression rerun as part of writing this handoff on 2026-10-20: **26/26 PASS; zero failures; exit code 0**. This confirms existing regression tests, NOT screenshot aesthetics or all in-game flows.

Node v24 runtime:
`C:\Users\ScotcsDuluka\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`

PowerShell full test:
```powershell
$node = 'C:\Users\ScotcsDuluka\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$tests = Get-ChildItem -LiteralPath 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests' -File -Filter '*.test.js' |
  Select-Object -ExpandProperty FullName
& $node --test $tests
```

CEF DevTools metadata:
- `http://127.0.0.1:59099/json/list` gives current target URL and WebSocket debugger URL for OSC page `http://localhost:59011/index.html#/base`.
- At last handoff check, CEF route was **`#/base` (idle)**.
- Can inspect actual Angular UI state through CDP Runtime.evaluate. For native menu, visit `main.main-menu`, inspect, then restore `base`. Avoid navigation if some other chat is actively testing.
- Screenshot through CDP `Page.captureScreenshot`. Keep separate screenshots and dimensions for actual target window.
- CEF's JS version lacks optional chaining; use CEF-compatible JavaScript in deployed JS. Node test scripts can use modern Node syntax.
- Source changes must be deployed to the corresponding `build` path (verify SHA256) and front-end CEF reload while idle; merely changing `Project/` does not guarantee OSC refresh.
- **Never restart SMASH LEGENDS, NVIDIA Web Helper, NvNode, other game/backend services or Native OSC without explicit approval.** Prior backend changes may require a later *normal user-approved* restart; not part of this UI task.

## 6. Separate Game Filter work — do not lose / overwrite

Existing Game Filter work is a SEPARATE feature; UI redesign must not regress it.

Read:
- `build_test/NVIDIA FilterEntry_Test/CURRENT-STATUS.md` (quick status, diagnostic snapshot might be stale)
- `build_test/NVIDIA FilterEntry_Test/HANDOFF.md` (detailed development and tests)
- `build_test/NVIDIA FilterEntry_Test/diagnose-game-filter.cjs` (read-only live status tool)

Related code:
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/ansel-game-filter.js`
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/gameFilterUi.test.js`
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/game-filter-route.js`
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/ansel-client.js`

Implemented earlier: Game Filter verified entry/readiness, native FILTERS listener rearm, Off (-1) persistence/close/reopen, race guards, safe deep-link startup through native initialization, automated regressions.

Historical real SMASH LEGENDS + D3D11 ReShade runtime smoke successfully tested basic preset Off/reopen, but **current cold startup/Alt+F3/cold deep-link and external ReShade mutation synchronization were not verified in a fresh live session**. When no game runtime is connected, `/Duluka/v.1.0/GameFilter/State` may correctly return HTTP 503 `ANSEL_UNAVAILABLE`; this is **not** proof of a broken server. Do not fake HTTP 200 or restart backend as a guessed fix. Preserve native OSC styling. ReShade installer UI-only.

Another GLM chat may own Native OSC changes: avoid competing writes.

## 7. Recommended Codex work order

1. Read THIS file and `build_test/NVIDIA NativeFiveTile_Test/HANDOFF.md`, check current `git status --short` and verify current source + runtime hashes. Treat latest user screenshot as visual bug evidence.
2. Analyze current OSC `app.js` native main-menu CSS/template constraints and `gpu-profiles.js` injected CSS; identify cause of width overflow at **actual viewport**.
3. Before making destructive edits, back up both current source and runtime to a new, uniquely named experiment folder. Preserve original working Game Profiles route and all native button handlers.
4. Implement a responsive layout **that resembles original NVIDIA**. Consider 5x192 center tiles ONLY if icon sizes, focus outlines, performance controls, target width, and submenu positioning remain usable; otherwise propose an alternative first-class Game Profiles placement.
5. Deploy to actual OSC when idle and visually inspect CEF **at the real constrained width**, not only 1920x1200. Test hover/focus and opening Game Profiles; also test return navigation.
6. Run 26/26 source tests (or current full suite), record screenshots and measurements, then ask user for approval. **No Git commit unless user explicitly requests it.**
7. If the user dislikes the next layout, restore previous known version cleanly rather than stacking CSS patches.

## 8. Resume prompt for Codex (paste verbatim if useful)

> Continue work in `C:\My Project\NVIDIA-Shadowplay`. Read `CODEX_HANDOFF.md` first. User needs Game Profiles visible in the real NVIDIA OSC Main-menu while preserving the original NVIDIA square-tile visual design. Current 5x240 center tiles work functionally but OVERFLOW / look cramped in the user's narrower real screenshot. Fix responsive proportions, likely investigate fitting 5 into the original 960px center span (e.g., 5x192 with appropriate icon sizing), but choose based on actual DOM/CSS constraints and screenshot evidence. Back up the source + built runtime before edits. Avoid Native OSC changes, game/backend restarts, and Git commits. Verify in real OSC, test at actual viewport, preserve click/hotkeys, rerun regression, present result for user feedback.

---
**This handoff is a checkpoint. Nothing here claims the latest design is visually finished or user-approved.**
