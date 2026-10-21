# ShadowPlay NEXT — Native Flexible Main-menu V4 (experimental)

**Branch:** `feature/flexible-layout-v4`
**Date:** 2026-10-22 (Asia/Bangkok)
**Status:** Prototype validated against live NVIDIA OSC CEF, **not merged or enabled in the regular Build**.

## User-approved design direction

- The **left, center and right panels** must all accept **Block Big**, **Block Side** and **Block Side Slot**.
- One Block can contain up to **9 Controls**; a group of 9 renders as a **3 × 3 icon grid**.
- Any of the existing 12 native NVIDIA Controls may move between panels.
- **Settings is movable and restylable but cannot be removed.**
- Do not stretch individual tiles or extend beyond the display. Center the complete native menu.
- Reuse original NVIDIA `md-menu`, `menu-button`, icons, Angular status bindings, CEF adapter and native action handlers.
- Preserve original Background settings and OSC Motion behavior; edits get only small 180 ms layout transitions.
- The Customize UI itself is **still experimental** and requires visual approval.

## Implementation in this Git worktree

- `osc/mods/main-menu-unified-model.js`: versioned V4 model, grouping, migration, serialization and layout packing.
- `osc/mods/main-menu-unified-native.js`: separate native UI adapter and interactive editing controls. Uses **existing** NVIDIA native nodes.
- `osc/mods/main-menu-unified-native.css`: NVIDIA-like styling, responsive editor, 3 × 3 icon groups and subtle editing animations.
- `osc/mods/gpu-profiles.js`: when V4 is enabled, delegates the existing native tile position and visibility updates to V4.
- `osc/mods/main-menu-live.js`: when V4 is enabled, maps existing Angular native tile faces to V4 variants.
- `osc/index.html`: loads V4 scripts and CSS additively.
- `tests/mainMenuUnifiedModel.test.js`, `tests/mainMenuUnifiedNative.test.js`: model/native regression tests.

## Compatibility and data safety

V4 uses **separate** localStorage keys:
- `DulukaMainMenuUnifiedV4Enabled` = `1` opts into V4.
- `DulukaMainMenuUnifiedV4` stores V4 grouped layout.

On first enable, V4 migrates the current V2 native positions, left/right Side rows, and saved Center Big/Side choice in-memory. The old V2 layout and Side row storage keys are not changed. Optional Controls intentionally removed in V2 are not resurrected. Settings is always retained.

V4 is disabled by default. When disabled, the **entire original V2 native editor and geometry path remains active**.

Do not turn on V4 for an active recording, game session or injection workload until UI is visually approved.

## Tests completed

- **60/60 Node tests passed** (44 original Customize tests + 16 new unified tests).
- Confirmed against the running native OSC CEF at 1920 × 1200, using **temporary, in-memory CDP injection** (no permanent Build edits):
  - 12/12 original NVIDIA `md-menu` nodes retained.
  - Migrated default 3-panel menu fit: x=204, width=1512, no overflow.
  - 9 Controls grouped into one Big Block in Center, while other Controls remained in the Right panel: fit true, width 516, centered.
  - 3-panel Customize editor, 12 native buttons, original Overlay Background settings present and Settings deletion prohibited.
  - Screenshot evidence, stored only locally: `smoke/v4-native-editor-cef.png`.
  - Original OSC localStorage V4 keys restored, OSC returned to V2 via CEF reload, leaving the running Build unchanged.
- **Important native geometry discovery:** NVIDIA auto-centers `.main-menu-container` and changes its width when `md-menu-bar` grows. The bar origin must be measured **after setting bar width**, or centering is applied twice. Also delay V4 geometry while OSC Motion phase is `opening` / `preparing`, until `idle`.
- No physical game/injected-filter session was tested; verified against the actual running OSC shell and CEF native nodes instead.

## Next integration gates

1. Finish interactive UI design feedback on Big/Side/Slot, drag/drop, panel spacing and Controls tray.
2. Test the **complete branch integration in a controlled Build copy**, not only temporary CEF injection. The CEF smoke temporarily emulated the patched legacy Angular/GPU hooks because the regular Build runs V2.
3. Verify game-overlay injection, status updates (Replay/Recording), Settings submenu, OSC Motion open/close, multiple monitors and rollback.
4. Only after these pass and the user approves the Customize appearance, merge into Stable and deploy as the new default.

## Useful commands

PowerShell:
```powershell
git -C 'C:\My Project\_ShadowPlay-Flexible-Layout-V4' status --short
node --test 'C:\My Project\_ShadowPlay-Flexible-Layout-V4\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuUnifiedModel.test.js' 'C:\My Project\_ShadowPlay-Flexible-Layout-V4\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests\mainMenuUnifiedNative.test.js'
```

**Do not `git reset --hard`, `git clean` or blindly pull inside the user's active Stable working tree.** The active project may contain concurrent Codex edits unrelated to this experiment.
