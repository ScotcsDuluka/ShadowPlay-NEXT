# CODEX HANDOFF — NVIDIA ShadowPlay NEXT / Freestyle Main-menu V5
**Created 2026-10-23 (Asia/Bangkok).** This is an exact-state handoff from the ChatGPT Customize conversation. **DO NOT assume the feature is complete.** You are asked to **take over and finish the implementation on the real source**, but preserve the working NVIDIA runtime and other concurrent projects.

## 0. First, protect the current state and coordinate ownership

- **Repository:** `C:\My Project\NVIDIA-Shadowplay`
- **REQUIRED Git branch:** `Stable`. HEAD at handoff inspection: `ec840a8df23668600996568c8661ccd4445366ea` (concurrent Codex sessions may move it; re-read status).
- **Canonical development source:** `C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc`
- **NodeAPI tests:** `C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests`
- **Working runtime:** `C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay`; **never use Build as source** or rebuild/replace the entire Build.
- **Verified original UI/motion reference:** `C:\Users\ScotcsDuluka\Downloads\NVIDIA ShadowPlay.7z`; read-only unpack `C:\My Project\_ShadowPlay-Archive-Clean-Audit-20261023`. UI, visual appearance, NVIDIA native buttons and original behavior from this archive are the design reference. The original archive is a runtime snapshot, not necessarily the most current source for Language Sync, Capture, Hook, etc.
- **Actual OSC CEF:** NodeAPI serves its local `osc` folder. HTTP `59011`, native host `59013`, CEF remote debug `59099`. A browser mockup does not prove CEF runtime behavior.
- **Crucial:** Other concurrent assistants are working in this SAME Stable tree. At inspection, numerous dirty/untracked Capture Audio, D3D9 Hook, NVIDIA Container, GameWatcher, and build scripts files exist. **Do not git add/reset/checkout/commit/merge these**. Stage only known OSC V5 files after reviewing each diff. Do not delete old branches or other worktrees.

### 0A. Uncommitted work to inherit, not overwrite

At inspection, the V5-specific pending changes in working tree were:
1. `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/main-menu-live.js`: added `nextOwnsInput()` guards on V2 capture click, down, move, up, keydown, so old V2 drag/click does not compete when NEXT V5 owns input.
2. `.../osc/mods/nv-next-v5-native.js`: new **temporary edit ownership** mechanism, snapshot/restore of inline styles/classes/attributes on the original NVIDIA md-menu nodes; positions native `md-menu-bar` relative before coordinates to prevent offset-parent drift.
3. `.../osc/mods/nv-freestyle-editor.js`: `beginEdit` uses transient `A.editOwnership(true)` rather than immediately persisting feature flag; Cancel/route exit undo & restore; Done persists flag.
4. `.../tests/nvNextV5Native.test.js`: two additional tests for temporary ownership and restored native nodes.

These changes are **uncommitted and NOT deployed to Build** at handoff. Node Syntax and targeted tests passed 66/66 after these edits. Review the diffs and test them under real CEF before committing/deploying. Do not override them by blindly checking out Stable or copying the archive. Other non-V5 dirty files in NodeAPI (`gameWatcherValidation.js`, `gameWatcherValidation.test.js`) belong to a **different workstream**.

## 1. Current V5 implementation and prior commits

Existing V5 model + adapter + editor in Project (not 100% production ready):
- `osc/mods/nv-layout-v5-core.js`: Pure six-Group/Block/Slot domain model, geometry, Auto-Fill, Settings invariant, swaps, Insert Preview, staged edits (Done/Cancel/Undo). Initial commit `8b5806d5ae`.
- `osc/mods/nv-next-v5-native.js`: Uses **real** NVIDIA native `md-menu`, `menu-button`, icon assets and event handlers, positions native tiles. Initial opt-in commit `4a104bacc9`.
- `osc/mods/nv-next-v5-native.css`: Native-only styles.
- `osc/mods/main-menu-live.js`, `main-menu-live.css`: restored from `.7z` in commit `afcc0be4b2` (JS subsequently modified to integrate V5 and suppress V2 competing input; do NOT restore it again wholesale).
- `osc/mods/nv-freestyle-settings.js`, `.css`: Retain NVIDIA existing Settings panel, rearranged into **floating Settings** using 80% Background and 20% Controls; preserve Angular-owned nodes, native md-select; commit `e5c3821afe`.
- `osc/mods/nv-freestyle-editor.js`, `.css`: Experimental user-facing V5 editing of native 6 Groups + slots, drag Group and Control, right-click layout context menu, Auto-Fill toggle, Undo, Cancel, Done, button event interception. Initial commit `16f95c16e4`, fixes `beb6244c1d`, `a048cdca5c`. **CEF behavior still buggy.**
- `osc/mods/osc-motion.js`: Calls NEXT preflight when enabled, otherwise existing V4 if enabled; Motion must not be compromised.
- `osc/index.html`: Loads relevant source modules, preserved Launcher Thai Language Sync; V4/old Group UI imports were deliberately unmounted earlier (do not revive).
- `tests/nvLayoutV5Core.test.js`, `nvNextV5Native.test.js`, `nvFreestyleSettings.test.js`, `nvFreestyleV5EndToEnd.test.js`, `nvArchiveCustomizeBaseline.test.js`.

**Last successfully synced runtime commit:** `a048cdca5c` (session `20261023-084817-db75130f`, backed up 2 changed OSC files). Git HEAD has since moved due to independent work. Last **pending V5 fixes are not in Runtime**.

## 2. Critical open bug — two layout/input owners compete

User screenshot 2026-10-23 ~16:09 shows Settings panel in desired floating footprint (roughly 80/20) but **NVIDIA Controls are missing/misaligned** above it; edit outline Group boxes floating in unexpected positions. Multiple systems hold different state:
- V2 NVIDIA Customize directive in `main-menu-live.js` still has its own `captureClick`, `mousedown`, `mousemove`, `mouseup`, `keydown` handlers and owns V2 layout data.
- V5 `nv-freestyle-editor.js` adds document capture-phase input listeners, V5 draft data and guides; before pending fix it permanently sets `DulukaNextNativeLayoutV5Enabled=1` on entering Edit, before Done.
- `nv-next-v5-native.js` positions `md-menu` using important inline `left/top/width/height` and transforms `md-menu-bar`. Earlier offset calculation assumed wrong positioned parent, causing tiles to drift.
- `main-menu-live.css` still carries NVIDIA/V2 flex styles and `.nv-live-layout-editing` transition selectors.
- Old V4 `main-menu-unified-native.js` and Group Layout A still exist on disk (old flags in localStorage) but their source imports were unmounted; verify actual CEF script list before assuming they are active.
- OSC Motion can change parent transform / opacity during entrance. Avoid geometry capture before native tiles are settled.

**Next task:** Make a single explicit ownership decision for **native geometry AND native input** per phase, with capture guards preventing Screenshot/Record while editing; V5 edit lease must not persist into Normal Mode except on Done, and Cancel must restore exact native layout without page-reload races. Ensure CSS absolute positioning resolves against actual `md-menu-bar`. Remove stale owner classes/inline important styles on transition and after route leave.

**Do not assume saved V5 layout consists of just 2 Groups.** There were multiple evolving CEF snapshots while debugging. Backup of exact key values was collected here:
`build_test\OSC-Live-Preview\freestyle-localstorage-before-owner-fix.json`
This contains the V5 flag, serialized Layout, and V2/V4/Group Layout flags/values; don't overwrite it. Confirm current state from CEF and back it up again before any migration. An old screenshot previously showed only `g3,g4` during partial edit, whereas newer saved snapshot contained center 4 groups. Preserve user data; default layout must never be auto-restored silently.

## 3. Full user specification — must implement all of it

### Source and look
- Original `.7z` NVIDIA style; native buttons, functional handlers, genuine material `md-*`, ICO icons, original OSC Motion, Host scaling.
- **Do NOT create a replacement OSC UI or generic Group Editor dashboard.**
- **Normal Mode visually identical to native NVIDIA baseline:** no Group borders, extra buttons, edit guides.
- **Customize opens Edit Mode on the ACTUAL Main-menu**, beneath original GeForce Experience heading.
- **Floating Settings panel** like screenshot, starts around 80% viewport width centered and under Main-menu, NVIDIA dark styling and green header edge; **left 80% Background Settings, right 20% Controls**. Existing NVIDIA Settings controls / Angular elements preserved; right Controls list scrollable. Right-click NVIDIA-style context menu to choose Block Type and Layout.

### Data model
- Maximum **6 Groups TOTAL** across left, center, right, no per-Panel quotas. Base **240×240** per Group.
- Default: left `g1` Side×3; center `g2/g3/g4/g5` Big×1 each; right `g6` Small×1+Side×2.
- Each Group maximum three 80px Small/Side blocks OR single 240px Big.
- Small slot presets: `[C][C][C]` (3), `[CC][C]` (2), `[C][CC]` (2), `[CCC]` (1).
- Side: `[C Text SubText]` (1), `[C Text SubText][C]` (2), `[C][C Text SubText]` (2).
- Big: `[C Text SubText]` (1).
- One Control per slot and no duplicates. Controls may be placed anywhere in any Panel/Group, no type-based restrictions.
- **Auto-Fill ON**: Group remains 240px high; Small/Side row heights: one block=240px, two=120px, three=80px. **OFF:** Small/Side rows stay 80px, Big stays 240px. Persist toggle only on Done; NVIDIA icons centered, never stretched.
- **No-Block empty Groups** hidden and compacted in Normal Mode; appear as 240×240 guides in Edit, still count toward six Groups. A block with empty slots is not necessarily an empty Group.

### Drag, Context and lifecycle
- Control→Control **swap**; Control→empty Slot fills; Control→empty Group and blank Group space opens Type/Layout selector. When selected layout has fewer slots, displaced Controls return to palette keeping previous order. Big replaces other Blocks, displaced Controls also returned.
- Group→Group swap; Group→between Groups delay then **Insert Preview**; move any Group across Panels; draggable `+ Group` with ghost preview; delete Group returns its Controls to palette.
- Only **Settings** has invariant: must remain in Main-menu; cannot delete Group containing it; no layout or block changes may silently eject Settings.
- Edit changes visible immediately, **Done saves**, **Cancel/Escape restores exact pre-edit state**, **Undo** one prior edit.
- Native button commands (Screenshot/Record, etc.) must never fire on drag or click in Edit Mode.
- Transitions/animations for swap, insert, block change, Auto-Fill, add/remove; but original OSC Motion and timing take priority, no UI lag.

## 4. Actual last verified test state (not a release)

The following selected test list passed **66/66** AFTER pending ownership edits (no runtime deploy):
`nvNextV5Native.test.js`,
`nvLayoutV5Core.test.js`,
`nvFreestyleSettings.test.js`,
`nvFreestyleV5EndToEnd.test.js`,
`nvArchiveCustomizeBaseline.test.js`,
`launcherLanguageSync.test.js`,
`oscMotion.test.js`.
Command in PowerShell:
```powershell
$n = "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI"
$names = @("nvNextV5Native.test.js","nvLayoutV5Core.test.js","nvFreestyleSettings.test.js","nvFreestyleV5EndToEnd.test.js","nvArchiveCustomizeBaseline.test.js","launcherLanguageSync.test.js","oscMotion.test.js")
$files = $names | ForEach-Object { Join-Path "$n\tests" $_ }
node --test @files
```
**Important:** Unit test success does NOT imply CEF visuals pass. Earlier native CEF smoke tester:
`build_test\OSC-Live-Preview\freestyle-v5-cef-smoke.cjs`
**FAILED** at visual condition (rendered Group count/positions), and was not successfully completed. Scripts `probe-freestyle-cef.cjs`, `forensic-no-touch.cjs`, `freestyle-debug.cjs`, `inspect-customize.cjs`, `next-v5-cef-check.cjs` are in the same build_test folder. Use read-only first and avoid running the old probes that toggle flags without saving session state.
Other comprehensive NodeAPI suites had unrelated failures from `gameFilter.test.js` (STALE_SESSION) and `keyboardShortcutActions.test.js` in a previous run. They are not evidence that V5 is correct or broken. Report separately.

## 5. Safe backup and Preview/Deploy flow

**Backup at handoff:**
`C:\My Project\NVIDIA-Shadowplay\build_test\OSC-Live-Preview\freestyle-localstorage-before-owner-fix.json`
**Old Runtime deployment snapshot/session backups:**
`C:\My Project\NVIDIA-Shadowplay\build_test\OSC-Live-Preview\sessions`
Latest applied session:
`20261023-084817-db75130f` (use manifest.json for exact source commit and SHA-256).
An earlier session `20261023-083900-35cbc558` is marked **interrupted** due to Windows EPERM replacing manifest, but later apply succeeded. Don't casually execute non-LIFO rollbacks across subsequent sessions.

**Project → Build tool:**
`Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-live-preview.cjs`
```powershell
$tool = "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-live-preview.cjs"
node $tool --plan
node $tool --apply
node $tool --status
node $tool --rollback SESSION_ID
```
The tool **REFUSES plan/apply while any frontend source changes are uncommitted**. At handoff this is expected and deliberate. Only after reviewing diff, protecting other concurrent modifications, committing SELECTED tested OSC files on Stable may you use `--plan` and `--apply`. Backup first; never hand-copy mods into Build; do not overwrite Launcher-generated `osc/mods/launcher-language.json`. Do not rebuild whole Build.

Read-only HTTP/CEF provenance:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-preview-audit.ps1"
```
CEF inspection via port 59099; actual native Host show may be `POST http://127.0.0.1:59013/show`. Avoid needless restarts. Do not touch Capture, Hook/Renderer, Container, game watcher workstreams; no branch/worktree merge.

## 6. Priority execution plan for Codex

1. **Preflight:** Check Stable HEAD, all dirty/staged files and CEF live state; preserve user localStorage JSON backup and last Runtime session manifests. Do not overwrite incomplete V5 edits; communicate ownership to other active tasks if editing overlapping files.
2. **Consolidate owner control:** verify the new `temporaryOwner` and style snapshot/restore paths work in CEF, close all V2 input event paths when NEXT owns UI, and remove native geometry conflicts (bar offsetParent must be correct; OSC Motion cannot override final positions). Ensure feature flag does not change before Done; Cancel works with no forced reload.
3. **Correct saved Layout / geometry** without silently resetting user state. Default must be six Groups with 12 NVIDIA Controls. Diagnose why screenshot shows empty Groups and floating few controls, and why V5 `redraw` got fewer visible boxes than model.
4. **Finish Group/Control drag and Context Menu in native CEF**: working drops and swaps, stable slot targets, palette keys, cross-Panel inserts with delay, `+ Group` ghost, correct Settings invariants and immediate draft render.
5. **Complete floating 80/20 NVIDIA Settings** per attached screenshot; check actual 1920×1080, 1920×1200 and scaled CEF viewport, no overflow; keep NVIDIA native styles/fonts/ICO.
6. **Wire Done / Cancel / Undo / Esc** end-to-end without touching V2 persisted state until explicit migration. Test repeated reopen Settings → Main-menu and Motion transitions, no control jumps.
7. **Native regression tests and real CEF screenshots**, verify exact positions of all 12 controls and absence of inadvertent Capture/Record actions, Language Sync th-TH, OSC Motion entrance/exit.
8. **Review & commit only V5 scope to Stable**, then explicitly backed-up Sync using tools; verify SHA and CEF. Write session/commit ID and screenshots to handoff status. Do not call it DONE until real CEF passes.
9. **No destructive cleanup** until reviewed. Keep other branches/worktrees, Runtime binaries, and .7z intact.

## 7. Source of truth on completion

- User wants **original NVIDIA ShadowPlay UI from .7z PLUS fully functioning Freestyle Layout V5**, not a new generic OSC, not a broken older experimental V4/Group editor, not just standalone tests.
- Stable Project is where Codex edits source. Build is a protected **working Runtime** whose OSC frontend can be synced with explicit backup.
- Even while other agents modify Stable, **never mix Build binaries, tree/branch outputs, or commit someone else's files**.
- The user's screenshots (in the originating ChatGPT conversation) demonstrate the design reference and current V5 collision symptom. If visual references are inaccessible in Codex, inspect the actual CEF from 59099 and the archived Customize UI, and request screenshots only if strictly necessary.

**Do real work on source now; do not respond with just a plan. Preserve current state and finish the missing integration.**
