# NVIDIA OSC — Customize Main-menu clean rebuild

**Isolation:** `sandbox/customize-v5-redesign-20261023` in `C:\My Project\_ShadowPlay-Customize-V5-Redesign`.
**Starting point:** `Stable` commit `8211761695`; intentionally *not* a merge of the experimental branches.
**Relevant checkout:** `Project/Overlay On-Screen Control/NVIDIA NodeAPI` and `devtools/osc-live` (Git sparse worktree).

## Confirmed user requirements

1. Keep the **real NVIDIA Main-menu** preview, including native `nv-osc-top-bar` (NVIDIA logo, GeForce Experience, close X), all three panels and their original native controls/events. No cloned or placeholder menu.
2. Preserve the **40px Settings frame inset on every side** and NVIDIA-like styling, including OSC Motion behavior.
3. Customize structure: `Panel -> Group (240x240 base) -> Block -> Slot -> Control`. Small has 3 slots / 4 layouts; Side has 2 slots / 3 layouts; Big has 1 slot / 1 layout. Controls are unique and Settings cannot be removed.
4. Ensure the Customize screen **never clips Footer, Group controls, Background Settings or native Top Bar**, including smaller displays. Separate scrolling areas must be usable, not simply cropped.
5. Control selection/movement must be interactive. Native buttons must not be obscured by CSS hit boxes; avoid invoking real capture actions as an unintended side effect of layout editing.

## Baseline / known regression risk

- Clean sandbox baseline: 96/96 selected JS tests pass on `Stable`, but visual overflow and layout balance were reported by the user and remain open design issues.
- Live CSS currently layers multiple competing rules for `.nv-live-editor`, the upper viewing band, grid/panel heights, and topbar visibility. Clean this up in one coherent scoped layout rather than adding another cascade.
- Existing V5 model and editor are useful references but must not be treated as visually complete.
- This branch is **not connected to running OSC** and must not write to `build/NVIDIA ShadowPlay` without separate deployment approval.

## References — inspect and compare, do not blindly merge

- `feature/group-layout-a-integration` — opt-in editor/model.
- `review/group-v5-correctness` — captured geometry, undo, topbar and overflow bugfixes.
- `design/v4-compact-settings` — alternate compact design experiments.
- `fix/v4-cef-reconnect` and `feature/flexible-layout-v4` — native menu timing, route/motion, drag behavior.
- `Stable` — unchanged production reference.

## Rebuild acceptance checklist

- [ ] One explicit layout contract (CSS variables and regions) for Top Bar, native Preview, Settings editor and Footer.
- [ ] No duplicate/contradictory scoped `nv-live-layout-editing` rules.
- [ ] Check screenshots + DOM rectangles + hit-tests at 1920x1200, 1366x768, 2560x1440 and other practical sizes.
- [ ] Native `nv-osc-top-bar` and X both visible and hit-testable during Customize.
- [ ] Footer and Background Settings reachable; horizontal overflow = 0.
- [ ] Large Group lists independently scroll; `+ New Group` remains accessible.
- [ ] Group model invariants, editor save/undo, navigation back from Settings and OSC Motion regression tests pass.
- [ ] Only after visual acceptance, package a deliberate Source/Runtime deployment with rollback backup.

_No changes to Stable, Capture Engine, or active game/runtime are part of this sandbox setup._
