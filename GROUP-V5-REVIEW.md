# Group Layout A V5 — isolated correctness pass

This branch is an isolated review of the in-progress `main-menu-group-model.js`, copied from the Group Layout A sandbox. It does not enable or deploy V5 in a running NVIDIA OSC.

## Corrections

- Side `detailLeft` / `detailRight` preset widths now render at 180/60 and 60/180 px within the 240px Group, as recorded in `GROUP-LAYOUT-A.md`.
- `moveGroup()` now appends to the end when no index is supplied for the same Panel. An explicit index means a position in the original Panel before removing the source Group, including an explicit end position.

## Verification

- Added `tests/mainMenuGroupModelV5.test.js` with 6 regression cases for migration, geometry, same/different Panel reorder, immutability, and Settings protection.
- Ran V5 model syntax check and regression: 6/6 passed.
- Ran combined Main-menu, Customize, Layout A, Motion tests: 114/114 passed (requires copied `build_test/NVIDIA Background_Test/selected-motion-timing.json` fixture).
- Original source worktree, running OSC, and separate Group Layout A sandbox were not edited by this pass.

## Integration note

Do not auto-deploy. Review against the other session's live V5 editor changes, then merge only the intended model and tests. This does not verify CEF visuals or pixel alignment.

## Follow-up: V5 Editor correctness and lifecycle

- Imported the current native Group Editor JS and CSS into the isolated review branch for diagnosis (without changing the main project).
- Fixed atomic layout saving: localStorage failure leaves the original in-memory model intact.
- Fixed V5 off/on toggling: the editor is hidden while disabled and the Settings host regains the V5 CSS class when re-enabled.
- Fixed document mouseup listener cleanup after the editor host is detached to prevent handlers accumulating during Settings navigation.
- Fixed native Sync failure after a successful save: editor reports pending refresh but keeps Undo history intact.
- Added `tests/mainMenuGroupEditorV5.test.js` covering Reset/Undo, failed save, editor toggle, repeated mount, and transient native Sync errors.
- Combined Main-menu/Customize/Motion/V5 model/editor tests: 119/119 passed in isolation.
- Checked that `gpu-profiles.js` already maps unified visibility into native `tiles[name].visible`; did not change native geometry or claim to verify live CEF visuals.
- Runtime flags, active OSC windows, and the main project remained untouched by this review.

## Follow-up: Native Top View and missed clicks (2026-10-22)

- User reported that the top viewing area vanished and some native buttons were not clickable.
- The current V5 geometry can grow beyond the transparent top band of the fullscreen Settings shell. Regression fixture reproduced native tiles ending at viewport y≈548 while the opaque panel began at y=420 for 1920×1200.
- Shared `--nv-top-preview-height` (380px; 272px for shorter viewports) between CSS and native layout sizing. While V5 Customize is active, the renderer measures the real rail anchor and caps scaled geometry eight pixels inside the transparent viewing strip. Normal non-editing geometry is unchanged.
- Added `tests/mainMenuTopPreviewV5.test.js`. Isolated review suite: 122/122 passed; tests run against the deployed main project: 116/116 passed.
- Deployed review versions of five targeted JS/CSS files into main Source and runtime. Backup: `C:\My Project\NVIDIA-Shadowplay\build_test\NVIDIA GroupLayout TopPreview Fix 20261022\source` and `...\runtime`.
- Reloaded **CEF page only**, `Page.reload(ignoreCache=true)`. CDP confirmed route remained `#/base`, V5 enabled, updated renderer contains `--nv-top-preview-height`, and Side ratio is `[3,1]`.
- Actual click-target hit testing on the Customize route is still outstanding: CEF was on `#/base` during verification. Do not claim that a live visual/click test has passed yet. Do not restart the game to verify.
- The main worktree already contained unrelated changes; do not cherry-pick this review branch indiscriminately over those uncommitted changes.
