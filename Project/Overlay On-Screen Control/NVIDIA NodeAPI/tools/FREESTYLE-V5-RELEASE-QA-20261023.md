# Freestyle V5 — Release QA gate (2026-10-23)

**Status: Automated regression green. NOT a signed-off Stable release.**
This report is a nonintrusive, read-only/offline QA pass while the user is away.
Canonical source is Project/Overlay On-Screen Control/NVIDIA NodeAPI on Stable.
Working NVIDIA ShadowPlay runtime and user layout must remain untouched.

## Tests added

tests/nvLayoutV5Stress.test.js adds repeatable seeded model property tests:

- 20 fixed PRNG seeds * 550 mixed operations = 11,000 attempted transitions.
- Random cross-panel Group moves and swaps, Group add/delete, Block add/remove/
  type/layout changes, Control placements/removals, and Auto-Fill toggles.
- Every successful step checks both live and JSON-roundtripped validation,
  uniqueness of Group IDs and Controls, exactly one Settings, total controls
  equal to occupied + unassigned catalog, finite and in-bounds geometry,
  240x240 Group bounds, 6-Group global limit and empty-Group visibility.
- Rejected operations must leave their input object unmodified.
- A further 100 multi-action Edit sessions exercise Undo, Done (exactly one
  persisted write), Cancel (zero persisted writes), and round-trip validation.
- Deterministic failures reproduce with seed and step number.

**Run:**

    node --test "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\nvLayoutV5Stress.test.js"

**Combined suite, 2026-10-23:** 133 tests, 133 pass, 0 fail. Includes
nvLayoutV5Core, nvLayoutV5Stress, nvNextV5Native,
nvFreestyleEditorLifecycle, nvFreestyleV5EndToEnd,
nvFreestyleSettings, nvV5Ownership, nvArchiveCustomizeBaseline,
launcherLanguageSync, oscUiLocalization, oscMotion, oscLivePreview.

Log: build_test/OSC-Live-Preview/freestyle-v5-offline-release-qa-20261023.log

An earlier **real CEF combined pass** from the Customize thread covered 18
automated interaction/viewport/lifecycle checks. Its artifacts are under
build_test/OSC-Live-Preview/v5-session-20261023/
build-regression-2026-10-23T11-43-40-667Z-all/.
This is historical evidence, not a new CEF run for the current audit.

## Before calling it release-ready

1. **Actual mouse**: verify Group drag and delayed Insert Preview; cross-Panel
   drop; palette Control-to-Control swap. Synthetic CEF events sometimes
   disagree with the native OS pointer. Require a real input confirmation.
2. **Native actions**: in Edit Mode, physically drag/click Record/Screenshot
   and confirm neither command runs. Normal Mode must continue to work.
3. **Repeated lifecycle**: 10-20 cycles of open/close, Settings Customize,
   Done/Cancel/Escape, native Material selects; no stale groups or hotkeys.
4. **Visual parity with NVIDIA .7z**: check 1920x1200 with actual OS scaling,
   1920x1080 and 1280x800; correct genuine md-menu/ICO, selected controls,
   original NVIDIA Motion and floating 80/20 Settings panel.
5. **Persistence**: backup the user's exact localStorage; Save, restart OSC,
   verify positions, no duplicate/missing Settings, no extra Controls,
   Auto-Fill persists; Cancel must restore byte-for-byte layout data.
6. **Source/runtime provenance**: check Stable, inspect ALL dirty source files;
   do not copy external OSC renderer/language edits without coordination.
   Back up Build via tools/osc-live-preview.cjs with explicit file allowlist
   and verify SHA-256 after syncing. No full rebuild, no Capture/Hook changes.
7. **Owner arbitration**: inspect existing V2 / V4 / V5 runtime flags and
   class/style cleanup around transitions; exactly one native positioning
   owner when V5 enabled. Avoid treating a DOM-only Unit pass as proof.
8. **Release decision**: human visual/functional approval and rollback ID.

## Scope / cross-project safety

- This QA work adds only an offline model regression test and this report.
- It does not modify V5 runtime model, native CEF layout, user Settings,
  NVIDIA Capture, Hook/Renderer, Container or any non-V5 worktree.
- The Stable worktree has unrelated concurrent dirty files in
  Capture/Container/Hook, osc/index.html, locales, and osc-renderer.
  Never run git add ., reset/clean, or stage those files from this QA task.

## 2026-10-23 — V5 hidden-Host RAF guard

An enabled NEXT native owner could schedule a 30-frame geometry settle
repeatedly from its 360ms poll while the native Main-menu root existed but
OSC Motion was closing/closed. Geometry correctly refused the writes, but
the frames were needless.

osc/mods/nv-next-v5-native.js now suppresses starting or continuing RAF
during blocked Motion, then resumes on a blocked -> idle/open transition.
prepareForMotion remains exempt to pre-position native NVIDIA tiles.

tests/nvNextV5Native.test.js adds deterministic queued-RAF/timer coverage:
- Closed Host: zero RAF requests after 40 ticks, no native style writes.
- Opening/closing: no RAF reflows; preflight still allowed.
- Idle/open: geometry resumes; subsequent Close ends a queued RAF loop.
- Temporary edit owner: no RAF churn and no persistent feature flag.

Combined offline suite: 135/135 pass. Test log:
build_test/OSC-Live-Preview/freestyle-v5-motion-idle-gate-20261023.log

Release gate unchanged. Source-only logic optimization; no direct CPU/GPU
profiling or new real CEF verification. Do not auto-deploy during parallel
OSC index/renderer/host work.
