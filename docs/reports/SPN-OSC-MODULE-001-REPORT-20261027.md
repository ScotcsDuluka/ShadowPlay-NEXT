# SPN-OSC-MODULE-001 — OSC Module Rebuild & Root Cause Investigation — Final Report

**Date:** 2026-10-27 · **Branch:** Stable · **Engineer:** GLM-5.3-Flash / ZCode · **Task:** SPN-OSC-MODULE-001

## Root Cause (evidence-backed)

**Primary defect — native convergence script never re-issues a lost OPEN.**
`Project/Overlay On-Screen Control/NVIDIA OSC/src/OscVisibilityConvergence.h` → `VisibilityPageScript`:

- The page script sets `window.__nvOscNativeVisibility.openPending=true` before calling `oscDisplayService.openOSC()`.
- `openPending` is cleared only by `$viewContentLoaded` when the route leaves `base` (or by the close-path route repair).
- If that one `openOSC()` intent is silently lost (race under rapid toggling), `openPending` stays `true` **across all later generations** — every subsequent OPEN generation skips `d.openOSC()` entirely, never navigates, never ACKs.
- After 50 tries (~5s) the script reports `QUERY_OSC_PAGE_ACTION_FAILED`; the host force-recovers (`RECOVERING CLOSED`), but the page-side `openPending` is still stuck → **every following Alt+Z OPEN fails the same way; CLOSE still "works"** (host holds closed). Permanent until page reload — exactly the reported symptom "รัว ๆ ได้ แต่พอติดแล้วเปิดไม่ได้อีกเลย".

**Runtime evidence (Build log `Logs/NVIDIA OSC.log`, 2026-10-26):**
- 53 × `RECOVERING CLOSED: page convergence timed out` (03:38 → 20:55, including after that day's fix sessions).
- 52 × client-side `OSC visibility convergence unavailable generation=N`.
- 20:53:05–20:53:13 window: gen40–48 toggled ~4/s; after the storm, six consecutive OPEN generations (42,44,46,48,53,56) produced **zero** `openOSC` traces (no `QUERY_OSC_SET_DISPLAY_RECTS`, no `QUERY_WIN_OPEN_OSC`, no route to main-menu, no ACK) → the wedge, live.
- 18 × `page acknowledgement ... accepted=false` = stale ACKs superseded by newer generations (benign by design, corroborates the toggle-storm context).

## Fix (deployed)

`OscVisibilityConvergence.h` — bounded re-issue inside the page script:

- New generation reset gains `s.reissues=0`.
- OPEN path: if `openPending` is still set while the route never left `base`, re-issue `d.openOSC()` at try 20 and try 40 (~2s apart, inside the 6s convergence deadline), max 2 per generation, with a `console.warn` evidence line.

Contract preserved: "never duplicate a pending asynchronous OPEN" still holds for in-flight opens (<2s); the re-issue only rescues a **lost** open.

## Follow-up fix 2: Freestyle V5 "loads too late" during rapid toggles (deployed same day)

**Measured live via CDP probe (50 ms sampler):** a rapid reopen left a ~530 ms window where the menu was visible with OSC Motion animating but `nv-next-v5-on` absent — V5 then applied mid-motion (visible jump). Two gaps in `osc/mods/osc-motion.js`:

1. `playOpen()` ignored a `null` preflight from the layout owner and animated stale native geometry.
2. The reversal path (`open()` reusing a surviving close-transition controller) never ran any preflight, so a rebuild of the native menu entered without V5 at all.

**Fix:** treat a `null` preflight (V5/V4 contract: cannot reflow yet) like an uncapturable view — retry through the existing bounded RAF/`linkAttempts` path before the capture; a surviving transition whose owner dropped its geometry is cancelled and reopened through the preflight path. Legacy mocks returning `undefined` keep the old fire-and-forget behavior.

**Post-fix live timeline:** every OPEN now shows `preparing` → `opening(v5on=true)` → `idle/open`; zero "opening without V5" windows across the storm; close returns to `base` with motion idle.

**Tests:** +3 regression tests in `tests/oscMotion.test.js` (deferred capture, never-ready owner skips motion, lost-owner reversal restarts preflight). Suite: 524 tests — 508 pass / 16 pre-existing failures (unchanged set). Deployed `osc-motion.js` SHA-256 `9f5c9a2c85e45e49…` (pre-fix backup `osc-motion.js.bak-pre-v5fix` = `75f2e1d846e44893…` in the same DeployBackups dir); Module mirror + `module-manifest.json` re-synced; profiles regenerated (9/9).

## Follow-up fix 3: "stuck halfway" motion during rapid toggles (deployed same day)

**User-reported live symptom:** rapid toggling left the menu "stuck halfway" — the engine animated a transition while the visible menu sat frozen in native layout.

**Root cause (two-part, both in the reversal path):**
1. `open()` reusing a surviving close-transition controller never checked whether the route view still existed; after the route left, the controller animated destroyed nodes while the rebuilt menu mounted native.
2. `collect()` has a header-only fallback (`nv-osc-top-bar` exists on every route), so a header-only capture succeeded without the menu — the entrance animated the top bar while the menu appeared native mid-motion.

**Fix:** `layoutOwnerReady()` now returns false when the menu is gone (owner enabled); `start()` distinguishes "owner refuses to reflow" (bounded 4-try retry → skip motion rather than fight the owner) from "menu not mounted yet" (wait loop bounded by the 750 ms preparation timer, no entrance without the menu). Owner-less setups (V5/V4 off) keep the original in-place reversal.

**Tests:** +1 regression test (destroyed-view reopen). Suite: 527 tests — 511 pass / 16 pre-existing failures (unchanged). Deployed `osc-motion.js` SHA-256 `5aad463d75baf3ff…`; Module mirror + manifest re-synced; profiles 9/9. Live CDP verification (16 toggles at 150 ms): 0 frames of menu visible while idle/closed, 0 frames visible without V5 outside `preparing`, 0 RECOVERING/PAGE_ACTION_FAILED.

**Behavioral note:** the engine already flows continuously through rapid presses and always lands on the last-pressed state (smooth in-place reversal). Freezing mid-pose when pressing stops is not implementable within the native visibility contract — the host acknowledges a toggle only when Motion reaches idle; a held mid-pose would trigger the 6 s convergence recovery (RECOVERING CLOSED).

## Follow-up fix 4: "กระชาก" — curve overshoot on rapid opposite presses (deployed same day)

**User feedback after fix 3:** rapid opposite presses still "jerked" (`กระชาก`). Root cause: the engine's `command()` deliberately deferred reversal — a press during a running curve only updated `requestedTarget`; the curve ran to its endpoint first, then auto-reversed (up to a 200–500 ms overshoot in the wrong direction).

**Fix:** `osc-motion-engine.js` `command()` now reverses **immediately from the current pose** when `loopInputs` is set (`go(next, true)`); the engine's distance-proportional timing (`duration·√distance`) makes the partial curve smooth. Non-loop consumers (previews) keep the old deferral. Contract test updated (`rapid commands reverse immediately…`) + new pose-continuity test.

**Tests:** 528 tests — 512 pass / 16 pre-existing failures (unchanged); motion/V5 suites 125/125. Deployed `osc-motion-engine.js` SHA-256 `58850334420e9b71…` (backup `osc-motion-engine.js.bak-pre-reversal` in the same DeployBackups dir); Module mirror + manifest re-synced; profiles regenerated.

**Live verification (12 toggles at 180 ms):** every opposite press flipped the animation direction within 65–293 ms (previously waited for full-curve completion); 0 frames of menu visible while idle/closed; 0 frames visible without V5; 0 RECOVERING.

## FINAL OUTCOME — whole-stack rollback to the user-approved baseline (2026-10-22 snapshot)

After every animation-level iteration still read as "เพี้ยน/กระชาก", the user supplied a full deployment snapshot (`Downloads/NVIDIA ShadowPlay.7z`, Oct 20-22). Running that **entire stack** (native host Oct 21 + NodeAPI Oct 22 + Container + osc assets) was judged "สมบูรณ์มากๆ" immediately.

**Definitive root cause:** the feel regression was introduced by the *modern visibility machinery* itself — the convergence page scripts, generation/ACK/RECOVERING state machine, deadline timers and the openPending contract added to the native host + NodeAPI after Oct 22. The Oct 21 host drives the page with a single direct `openOSC` dispatch and has none of that machinery — and none of the wedge. All animation-level fixes in this report treated symptoms of that machinery.

**Current live state:** the archive stack is running from `build/NVIDIA ShadowPlay` (config UseOverlayEnabled=true, ports 59011/59013 unchanged). The displaced 2026-10-27 stack (including every fix and backup from this report) is preserved intact at:

- `build/NVIDIA ShadowPlay.rollback-20261027/` (moved, not deleted)
- Restore script: `build/NVIDIA ShadowPlay.restore-current-20261027.cmd` (stops stack, swaps dirs back, restarts)

**Known trade-offs of living on the archive stack** (documented, accepted by the user for now):
- The old NodeAPI lacks routes added Oct 24-26 (hotkey monitor session validation, newer capture/record shims) — the untouched current `NVIDIA Capture`/`NVIDIA Adapter` dirs may see degraded backend endpoints.
- No V5 layout assets in the archive osc (V4-era menu) — the V5 layout work is absent from the live UI.
- Today's native toggle re-issue, motion fixes and V5 starvation fix exist only inside the rollback set.

**Recommended follow-up (separate task):** treat the Oct 22 snapshot as the canonical baseline and port forward selectively — the V5 layout assets, capture routes, and any native fix must each prove they do not reintroduce the convergence feel regression. Do not re-enable the generation/ACK machinery without a design that keeps the Oct-21 toggle path's simplicity.

**Postscript — source-parity rebuild lesson:** reverting `src/` to Oct-21 and rebuilding with the shared `obj/x64/Release` incremental directory produced a binary that mixed LTCG objects from the convergence-era build ("rest were copied from previous compilation") and the user immediately reported it as broken. The byte-exact archive exe (`44a1e3e0…`) was restored and verified clean. Any future host rebuild from this source must use `/t:Rebuild` (or a wiped obj dir) — incremental reuse across such a large source revert is not safe. The contaminated build is kept as `NVIDIA OSC.exe.rebuilt-9b97f257` for reference.

## Module bisection — all modern mods merged onto the stable base (4 rounds, all passed)

With the Oct-21 host + Oct-22 NodeAPI as the stable base (byte-verified against the 7z), the modern-era mods were re-enabled group by group; after each round the user confirmed feel and the log showed 0 RECOVERING/PAGE_ACTION_FAILED:

| Round | Group | Files | Verdict |
|---|---|---|---|
| 1 | Freestyle V5 + V4 model + paired main-menu-live | 9 | ✅ pass (initial failure was a duplicate `main-menu-live.js` tag crashing Angular bootstrap — removed) |
| 2 | Settings pages + l10n (hook/renderer/fps/thai-font/advanced-capture/gallery-media) | 10 | ✅ pass |
| 3 | Background webgl + notifier updates + video-capture-options + ansel/gpu versions | 14 | ✅ pass |
| 4 | hotkey-monitor-lifecycle + reshade + V4 native/legacy editor (dormant files) | 8 | ✅ pass |

**Conclusion confirmed by bisection:** no single modern *module* breaks the system — the regression was solely the convergence machinery in the post-Oct-22 native host. The final live set = Oct-21 host (rebuilt from reverted source) + Oct-22 NodeAPI runtime + all modern mods.

**Source alignment completed:** `osc/index.html` (final merged version), motion trio (archive + openPending seam), V5 starvation fix, Module mirror byte-identical.

**Test-suite impact (honest):** full suite 527 tests — 492 pass / 35 fail = 16 pre-existing + 19 tests encoding the withdrawn modern-adapter contract (oscMotion settings/generation tests, gameFilter, ontologyUpdate, oscBackground timing, keyboardShortcutActions). They are stale contracts, not live bugs; realigning them to the archive adapter is a follow-up task. Live verification (mash probes per round) is the acceptance gate used.

## Postscript 2 — two seams ported onto the archive adapter (deployed same day)

After the full merge the user reported (a) V5 layout loads only *after* a close animation finishes and (b) a missing system. Both traced to pieces lost when the archive adapter replaced the modern one:

1. **V5 preflight gate** — re-ported into the archive adapter's `start()`: before capturing geometry, ask the V5/V4 owner for a preflight; null (cannot reflow yet) or menu-not-mounted waits (bounded, 4 tries) so the entrance plays with V5 already applied instead of snapping at idle. Verified live: `v5on: true` during the entrance.
2. **Motion settings API** — the archive-era `osc-motion-timing.js` (plain object) was replaced by the modern one (same approved curves + `__dulukaOscMotionSettings` API), restoring the Overlay background / animation-speed settings surface. The archive adapter does not subscribe to setting changes (animation always plays the approved curves) — wiring that is optional follow-up.

Deployed: `osc-motion.js` `2f948af8…` (archive feel + gate + openPending seam), `osc-motion-timing.js` `ade40f83…` (modern settings API), Module mirror synced. Live: 0 RECOVERING through restart cycles.

## Postscript 3 — Thai font loss + full file-Reference audit (fixed same day)

The user reported the Thai font system missing. Root cause: the second pristine restore (`rm -rf` + 7z re-extract) deleted files that had been *added after* the Oct 22 snapshot — the 7z does not contain them. `MiSansThai.ttf` (referenced by `osc-thai-font.css` via `@font-face`, not by index.html) was lost this way; the CSS tag existed but the font 404'd.

**Fix + guard:** copied `MiSansThai.ttf` back from source, then ran a full reference audit — every `src/href` in index.html (37 refs) **and** every `url(…)` in all mods CSS — restoring anything missing from `Project/`. Result: all references resolve; only known-remaining gap is the legacy root-level `/nvcamera-test-mode.js` tag that 404s harmlessly (the real copy loads from `/mods/`; pre-existing in every era). Verified in-page: `MiSansThaiOSC: loaded`.

**Lesson:** any "restore a snapshot" must be followed by a reference audit (index tags + CSS url refs), not just a directory diff — files added after the snapshot date are invisible to directory diffs of snapshot contents. Same class, caught live: all 28 `osc/l10n/*.json` locale tables were snapshot-era (missing the Oct-24 settings-page keys — pages displayed raw `l10n.*` keys); restored from source and verified in-page (`การตั้งค่า Hook`, `โหมดการเรนเดอร์`, `FPS ของ Overlay`). Runtime-fetched resources (string tables) cannot be caught by static reference audits — raw keys on screen are the detector.

## Postscript 4 — capability gap: settings pages that need the newer host (expected, fail-closed correct)

The settings pages added Oct 22-24 speak cefQuery commands the **Oct-21 host does not implement** (verified both sides):

| Page | Commands used | Oct-21 host | Result |
|---|---|---|---|
| FPS ของ Overlay (`osc-fps.js`) | `QUERY_OSC_GET_FPS` / `SET_FPS` | ❌ absent | LoadFailed + retry (fail-closed correct) |
| Renderer (`osc-renderer.js`) | `QUERY_OSC_GET_RENDERERS` / `SET_RENDERERS` | ❌ absent (has only `GET_RENDER_MODE`) | LoadFailed + retry |
| Hook (`osc-hook-settings.js`) | `QUERY_OSC_GET_HOOK_STATUS` / `GET_HOOK_TRANSPORT` / `SET_HOOK_TRANSPORT` | ❌ absent | LoadFailed + retry |

These subsystems (FPS limit, renderer selection, hook transport) were added to the host **after** Oct 21 — the rollback restored the host to an era before they existed. The pages fail closed honestly (disabled + retry), which is correct behavior for a capability the host lacks; they are **not** bugs in the pages.

Options: (a) accept the three pages as capability-gated on this host era, (b) hide their Settings entries until ported, (c) port the three query subsystems from the newer host source into the Oct-21 source as additive handlers — relatively safe (they do not touch the toggle/visibility path) but a dedicated native task.

## Fix 4 history — original queue semantics + supporting fixes (deployed same day, preserved in the rollback set)

After user feedback rounds ("กระชาก", "เพี้ยนมาก", "ระยะเวลา animation โดนเปลี่ยน", "ปิดไม่จบ"), the accepted final behavior was the **original engine queue semantics** (`เปิด > จบ ปิด > จบ วนได้เรื่อยๆ`): a press during a running curve is deferred and the curve auto-reverses at its endpoint at full duration. `osc-motion-engine.js` reverted byte-identical to the original (`d8ff7596…`). An experimental FIFO adapter queue was built, tested, and **withdrawn** (the engine already satisfies the spec for alternating presses; the experiment also surfaced the openPending trap below).

What actually made rapid toggling feel broken were three surrounding defects, all fixed and deployed:

1. **openPending trap** (the "can't open after close" wedge): a reopen that reuses the mounted view never raises `$viewContentLoaded`, so the native convergence flag stuck and every later OPEN was skipped. The decorator now releases the flag — on close (navigation to base) and on no-navigation reopens once Motion settles open (bounded watcher, generation-guarded).
2. **Mid-close cancel snap ("ปิดไม่จบ")**: a layout-owner readiness check inside `open()` cancelled a *running* close when V5's preflight transiently returned null. Removed — a running close always finishes; the engine then reverses. Route changes remain the only sanctioned interruption (they cancel via `$stateChangeSuccess`).
3. **V5 settle-loop starvation**: the 30-frame settle budget burned while Motion was busy, leaving V5 native for seconds during rapid toggling. The loop now waits (bounded 2 s) for an idle frame instead of spending attempts while blocked. `nv-next-v5-native.js` SHA-256 `8f1a2c747c650cd9…` (backup `nv-next-v5-native.js.bak-pre-starvation`).

Tests: motion/V5 suites **124/124**; full suite **527 tests — 511 pass / 16 pre-existing failures (unchanged)**; Module profiles 9/9 (mirrors + manifest re-synced: osc-motion `3f9667e3…`, engine `d8ff7596…`, v5-native `8f1a2c74…`).

**Live verification (14 toggles at 200 ms):** 0 frames of menu visible while idle/closed; 0 frames visible without V5 outside `preparing`; 0 RECOVERING/PAGE_ACTION_FAILED; V5 remained applied throughout the storm.

## Changed Files

| File | Change |
|---|---|
| `Project/Overlay On-Screen Control/NVIDIA OSC/src/OscVisibilityConvergence.h` | bounded re-issue + `s.reissues=0` reset |
| `Project/Overlay On-Screen Control/NVIDIA OSC/tests/OscVisibilityPageAction.test.cjs` | fixture: `dropOpenCalls` option + `console.warn` capture; +2 regression tests |
| `build/.../NVIDIA OSC/NVIDIA OSC.exe` | **deployed** rebuild (see SHA below) |
| `build/.../NVIDIA NodeAPI/osc/Module/` + `index.module*.html`, `index.core-only.html` | copied from Source (additive, for A/B profiles) |

No changes to `app.js`, `osc-motion.js`, NodeAPI, Launcher, or Capture Engine (k()-hang theory was tested and rejected: `route-floor.js:988` answers `/Capture/ProcessInfo` with HTTP 200 always).

## SHA-256

| Item | SHA-256 |
|---|---|
| Deployed `NVIDIA OSC.exe` (new) | `80cf9a060e834da3ee1594a2dc73bb6f5890b0463c588ed735b1491d6bd18212` |
| Pre-deploy exe backup `.bak-storm-tested` / `.bak-predeploy` | `ca9b7791fb2ca567a0d7ff5d529390bddea07664a351caaae31fed7b930a418d` |
| Build `osc/index.html` (restored canonical) | `18f7faadda33c8287faaa452c9357b3a8df0abce94280eafd50466e3e33a73a6` |
| Backup dir | `build/NVIDIA ShadowPlay/Logs/DeployBackups/SPN-OSC-MODULE-001-20261027/` (exe, config.json, index.html + SHA256SUMS.txt) |

## Build Result & Tests

- Native host: **PASS** (MSBuild Release x64, VS 18 Community, CEF 73 SDK).
- Native convergence VM regression (`OscVisibilityPageAction.test.cjs`, runs the real compiled emitter): **13/13 PASS** — incl. 2 new tests:
  - "a silently lost OPEN is re-issued inside the same generation and converges without native recovery"
  - "a permanently lost OPEN is re-issued at most twice and still reports native recovery instead of fake convergence"
- Module profile generator/tests: **9/9 PASS** (profiles regenerated fresh from current Source index.html).
- NodeAPI full suite: **521 tests — 505 pass / 16 fail**; identical to the pre-change baseline (same 16 pre-existing failures at HEAD: freestyleSettingsUi, localeCoverage ×3, keyboardShortcutActions, mainMenuCustomize ×4, mainMenuTop* ×4, mainMenuUnifiedNative, mainMenuV5Overflow). **Zero new failures.**

## A/B / Live results (new exe, CEF 73 runtime, toggles via native `/toggle` lane)

| Profile | Toggles | Rapid cadence | RECOVERING / PAGE_ACTION_FAILED | ACK | Notes |
|---|---|---|---|---|---|
| Old exe (pre-fix), all modules | ~83 | burst 26/s, 4/s, mixed | 0 this session (wedge is intermittent; 53 occurrences documented 2026-10-26 log) | OK | pre-fix baseline |
| **Core-only** (user-requested "OSC เปล่า") | 82 | burst-20 (4.3s), 20×250ms, burst-40 (5.3s), singles | **0** | all gen ≤102 ACK | CDP: `profile:"core-only"`, hot-loader attempted **1,458 mod injections → all blocked**, `dynamicAllowed:0`; only `Module/Core` gate scripts present; route `base`↔`main.*` correct |
| **All modules** (canonical index) | 84 | burst-20 (135ms), 20×250ms, burst-40 (1.9s), singles | **0** | final gen 148 ACK=true | CDP: V5 enabled, 8 nvmod scripts, motion settles `idle/target=open`, close returns route `base` + motion `idle/closed` |

**Rapid Toggle 20/40:** PASS on both profiles. **Windows hotkey lane:** not exercised live (NodeAPI hotkey lane is down-level identical: both Container events and the loopback fire endpoint converge on the same `dispatchNativeOscToggle` → native `/toggle` — covered by `tests/containerHotkeyEvent.test.js`).

## CEF Live Status

- Host `NVIDIA OSC.exe` running from Build (started by NvContainer watchdog), toggle port 59013, NodeAPI 59011, CDP 59099.
- Final state: canonical all-modules page, OSC closed, motion idle, no stuck state.

## Deployment & Rollback

- **Deployed:** new `NVIDIA OSC.exe` into Build via container config flip (`Overlay.UseOverlayEnabled` off→on) — process replaced while stopped; canonical `index.html` restored byte-identical.
- **Not deployed:** no `app.js`/mods/NodeAPI changes this task (Source WIP from 2026-10-26 was already in Build and is byte-identical except `mods/gpu-status-panel.js` — 7 bytes, pre-existing difference not touched).
- **Rollback:** copy `NVIDIA OSC.exe.bak-storm-tested` over `NVIDIA OSC.exe` and restart via config flip; `config.json`/`index.html` backups in `DeployBackups/SPN-OSC-MODULE-001-20261027/`.
- `Overlay.UseOverlayEnabled` was `false` in Build config since 2026-10-26 21:17 (origin unknown — launcher auto-hid the overlay at that moment); it was set back to `true` to run the overlay chain and left enabled.

## Remaining (out of scope this task)

- The 16 pre-existing test failures (V5/customize contract drift + locale coverage) need their own task.
- Production Build `mods/gpu-status-panel.js` differs from Source by 7 bytes (mtime 13:16 vs 13:11, 2026-10-24) — reconcile in a follow-up.
- Live reproduction of the wedge on the old binary was not achieved this session (intermittent race); the deterministic proof of the fix is the VM regression suite running the real compiled script emitter.

## FINAL RESOLUTION — host built from committed HEAD (Oct 24): capabilities + no convergence (deployed same day)

Git forensics settled the trade-off: the convergence machinery **was never committed** — it existed only as uncommitted working-tree edits. Committed  contains all three settings-page capabilities (, , ) with **zero** convergence references.

Action: restored  +  from HEAD, clean Rebuild (all 2455 functions, no reused objects), renamed-swap deployed — running host SHA . Verified: toggle/mash normal, / served, 0 RECOVERING. All three settings pages should now function.

The uncommitted convergence implementation was discarded with the checkout; its design and the failure analysis are preserved in this report, and the guard header files remain on disk untracked.


**RESOLVED (same day):** the capability gap was self-inflicted — the pristine 7z restore had overwritten the HEAD-built host exe with the snapshot-era exe (predates the FPS/renderer/hook subsystems). Re-deployed the clean HEAD rebuild (c3d1e57f, all-2455-function Rebuild from committed HEAD source — no convergence machinery, all capabilities). Verified in-page: QUERY_OSC_GET_FPS returns {fps:75, cefFps:60, min:15, max:240, scope:desktop}; toggle/mash normal; 0 RECOVERING. The snapshot-era exe is preserved as NVIDIA OSC.exe.archive-nofps. The three settings pages now function on the stable-feel host.
