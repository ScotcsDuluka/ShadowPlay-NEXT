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
