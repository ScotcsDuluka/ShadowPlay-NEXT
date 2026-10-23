# NVIDIA ShadowPlay — Classic UI + New Group V5 Prototype

**Source of truth:** `%USERPROFILE%\Downloads\NVIDIA ShadowPlay.7z` (about 77 MiB).
This archive contains a known-good **deployed runtime** and original OSC UI assets. It does **not** contain the native C++ `NVIDIA OSC` project sources.

## Safety / provenance

- `legacy-original/Overlay On-Screen Control/NVIDIA NodeAPI/osc` is a READ-ONLY REFERENCE copy extracted from the archive. Do not modify it.
- `working/NVIDIA NodeAPI/osc` contains a separate copy of the old OSC UI with three new V5 scripts/styles loaded from `index.html`. The old `main-menu-live.js`, `main-menu-live.css`, and OSC Motion scripts are byte-identical to those extracted from the archive.
- `preview.html` is a **simulated QA harness**, not the actual CEF/native NVIDIA menu. It tests interactive Group UI without executing capture actions.
- Nothing was written to the user's active `C:\My Project\NVIDIA-Shadowplay\build` runtime, `Stable` branch, or the archived `.7z` file. Do not copy the prototype to Runtime without native integration and regression validation.

## Layout contract (requested)

Panel > Group (always 240×240 design pixels) > Block > Slot > Control.

| Block | Height | Slots | Layouts |
|---|---:|---:|---|
| Small | 80px | 1–3 | `thirds` = [C][C][C], `twoLeft` = [2C][C], `twoRight` = [C][2C], `full` = [C] |
| Side | 80px | 1–2 | `full` = [C + Text + SubText], `detailLeft` = [C Text/SubText][C], `detailRight` = [C][C Text/SubText] |
| Big | 240px | 1 | `full` = [C + Text + SubText] |

A Group can contain Small/Side Blocks with a total of **up to three rows**, or one Big Block. A Group can be empty while editing. Each panel can have any number of Groups, though the native preview for unlimited Groups still needs a paging/scrolling design.

**Controls:** 12 original NVIDIA OSC controls, unique across the three panels, with Settings mandatory. Invalid edits, duplicate assignments, overflow and deleting Settings are rejected transactionally.

## Implemented

- `working/NVIDIA NodeAPI/osc/mods/classic-group-v5-model.js`: pure, testable V5 data model. Immutable edits, geometry, Block types/layouts, moving Groups/Controls, Settings protection, V2 data migration, separate safe persistence key `DulukaClassicGroupV5Layout`.
- `.../classic-group-v5-editor.js`: interactive Customize UI for creating/reordering/removing Groups, adding/removing Blocks, switching Block Type/Layout, choosing Controls then clicking Slots, Undo, reset and saved state. When editing, native capture clicks are blocked. Legacy `main.customize-main-menu` Angular route is intercepted so the old Customize page is not opened underneath V5.
- `.../classic-group-v5.css`: single, scoped Stylesheet for native-style dark panel, fixed 40px shell inset, full 240×240 group geometry, independently scrolling panel lists and whole-shell scrolling on shorter screens. It does not modify V2 CSS rules.
- `working/NVIDIA NodeAPI/osc/index.html`: loads model and editor after the old `main-menu-live.js` while preserving OSC Motion scripts. Original `__dulukaStartMainMenuEditor` entry point is intercepted **only inside this isolated working tree**.

## Test evidence

- Node model/integration tests: `node --test tests/classic-group-v5.test.cjs` → 24/24 pass.
- Interactive browser test: `node tests/browser-qa.cjs` → passed at 1920×1200, 1366×768 and 900×700; 40px shell inset, exact Group and Block sizes, add Group, add Block, change Type/Layout, assign Control, Undo, Done; no native mock capture event escaped. Results and screenshots in `qa/`.
- **Not yet tested in native CEF.** Headless Chrome QA is not equivalent to NVIDIA CEF 73, OSC Motion transitions, or the game's overlay. These are separate acceptance gates.

## Critical remaining work before installation

1. **Native bridge:** newly saved V5 Group positions do not yet rearrange the real NVIDIA `md-menu` tiles. The old working Main-menu is deliberately preserved until a native renderer bridge can be tested without calling recording/capture actions.
2. Confirm how to scroll or paginate native Preview when Group count exceeds the available horizontal space; Editor lists already support unlimited Groups.
3. Verify editing launch, Settings route navigation, transition animations, `nv-osc-top-bar`, language/fonts, and UI scale in real OSC CEF 73.
4. Only then stage a separate test Runtime with explicit rollback. Do not overwrite the user's currently running Runtime or `Stable` automatically.

## Resume work

Use this working folder as the foundation rather than the previous experimental V4/V5 CSS pile. If using Git, commit only the authored V5 modules/docs to a branch; **do not upload the proprietary runtime archive, executable/DLL files, caches or bundled font files**.
## Git branch note

This Git branch is a source-only snapshot of the authored Classic Group V5 prototype. It intentionally excludes the proprietary NVIDIA archive and bundled runtime. Do not expect existing Stable `osc/index.html` to launch the old-based editor by itself: the fully isolated prototype resides at `C:\My Project\_ShadowPlay-Classic-V5-Sandbox\working\NVIDIA NodeAPI\osc`.

To integrate into the classic archive UI only, after its existing `main-menu-live.js` script add the following scripts IN ORDER and add the stylesheet after the old `main-menu-live.css`:

- `/mods/classic-group-v5-model.js`
- `/mods/classic-group-v5-editor.js`
- `/mods/classic-group-v5.css` (stylesheet)

Native V5-to-CEF button position sync is deliberately not done yet. First complete real CEF proof in a test deployment, not in active Runtime.
