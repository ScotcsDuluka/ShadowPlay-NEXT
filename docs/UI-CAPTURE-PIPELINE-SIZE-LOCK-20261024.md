# ShadowPlay NEXT — Pipeline Design UI Size Lock (2026-10-24)

**Owner:** OSC / NVIDIA NodeAPI UI, AORUS-DESKTOP  
**Status:** Implemented as a UI-only local design preview. Live capture API/encoder selection is NOT wired by these changes.  
**Canonical engine design:** [Session-Centric Architecture](architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md).

## Target and rationale

Match the NVIDIA OSC Settings look, with deterministic field sizes and no nested scrolling at the normal desktop overlay width. Keep existing `Live Settings` behavior untouched. `Pipeline Design` is a **non-persisted preview**; it never POSTs a capture setting or starts a recording.

## Pixel sizing / layout contract

| Element | Desktop lock | Behaviour |
| --- | --- | --- |
| Tab group | 36 px high | `Live Settings` and `Pipeline Design` |
| Each tab | 128 × 28 px | Text truncates instead of resizing |
| Pipeline panel | 100% parent width × 270 px high | `box-sizing: border-box`; no nested scrollbar |
| Input grid | 3 equal columns × 2 fixed rows | Responsive equal-width tracks; 12 px horizontal and 8 px vertical gaps |
| Each field | 100% assigned track × 66 px | Min/max height 66 px, no wrapping |
| Label | 18 px high | Ellipsis if needed |
| NVIDIA `md-select` | 34 px high | Same for all six, preserve native Material popup |
| Summary bar | 39 px high | Maximum 2 visible lines; full text in tooltip |
| Explanatory note | 16 px high | Ellipsis, no influence on panel sizing |
| Footer | 3 columns × 36 px high | Left Capture cursor / center runtime status / right settings status |
| Narrow viewport ≤850 px | 2 columns × 3 fixed rows, 340 px panel | Still no nested scroll within Pipeline Design |

Design fields: `Desktop Capture API`, `Exclusive Capture API`, `Video Encoder`, `System Audio`, `Microphone`, `Recovery Policy (planned)`. The sixth field remains **disabled** until its source-switching backend has real evidence. Unavailable WGC/QSV/FFmpeg Audio backend options remain disabled; advertised codec lists do not prove hardware capability.

Track **width is equal and relative to the available Settings container**, not a hardcoded absolute screen coordinate. This avoids overflow at different desktop resolutions. Heights and spacing are locked so text cannot cause vertical jumps.

## Implementation and regression

- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.js`
  - Version 72, local draft state and sixth disabled Recovery slot.
  - Compact read-only status summary; native Angular Material selects retained.
  - Does **not** change the existing persisted settings POST path.
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.css`
  - Size-lock overrides scoped strictly to the Pipeline Design panel and shared footer.
  - No additional nested scrollbars.
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/capturePipelinePlanUi.test.js`
  - Asserts six field model names, Recovery disabled, no network POST, CSS sizing invariants.
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/videoCaptureOptionsUi.test.js`
  - Legacy Live Settings contract unchanged.

### Runtime deployment

For desktop OSC runtime, deploy only the two verified UI assets from `Project/.../osc/mods/` into `Build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/`. Verify SHA-256 and HTTP `GET /mods/list` and `GET /mods/<asset>`. Static assets can reload by reopening OSC; no Capture/Container restart is necessary and no screen recording is authorized. Back up existing runtime assets before overwriting. **No Git commit/push**.

## v73 — Screenshot review and follow-up (2026-10-24)

User-supplied screenshot confirmed the three-column design is materially aligned but revealed unused space below the footer and potentially misleading live controls on the design-only tab. Scope-limited refinements:

- Keep all six cells and 270px Pipeline Design panel unchanged.
- When `Pipeline Design` is active, assign a fixed 400px content footprint (468px at <=850px); use flex layout to anchor the 36px footer at the bottom. Live Settings layout is unchanged.
- In design-only tab replace the live `Capture cursor` toggle with a noninteractive `DESIGN PREVIEW` label and the live `Ready` status with `NOT APPLIED`. Runtime status remains factual.
- Give the bounded two-line summary 11px text (the actual full selection remains available in its title attribute).
- `md-select` size and native NVIDIA focus/popup treatment stay unchanged.
- Node syntax check and both UI test suites: **26 passed / 0 failed**. Git whitespace diff check passed.
- Static JS/CSS deployed to AORUS Runtime Build; source/deployed SHA-256 matched and HTTP `/mods/list` and asset responses were verified.
- Backup before deploy: `C:\My Project\NVIDIA-Shadowplay\build_test\ui-deploy-backups\capture-pipeline-v73-20261024`.
- **CEF visual post-deploy screenshot not yet inspected.** This is source/test/runtime HTTP evidence, not final visual acceptance. No Capture/Container restart; no Commit/Push by this action.

## v74 — OWNER CORRECTION: native NVIDIA md- sizes are authoritative (2026-10-24)

**This section supersedes every earlier 'fixed md-select size', 'fixed label', 'fixed panel height', and 'all-in-one settings' recommendation in this document.** The owner explicitly directed: **Do not force the size of NVIDIA md- components; arrange the layout to fit them.**

- Preserve NVIDIA native `md-select`, `md-input-container`, `md-option` sizing and focus/menu behavior. No forced 34px select height or 66px field height. Adapt the surrounding grid, not the control.
- Main `Video capture` page keeps the native quality/replay/resolution/FPS/bitrate controls plus engine/API, capture display, cursor and status. One `osc-button osc-button-gray` **Advanced Capture** navigates to a separate subpage inside NVIDIA Video Settings.
- `Advanced Capture` page owns separate Desktop API / Exclusive API / Encoder / System Audio / Microphone / Recovery design selects; real Codec Encoder/Preset and Preview Build Arguments are moved there. A second native `osc-button` returns to Video capture.
- Navigation hides native main `#topLevel` children temporarily through a scope class instead of altering Angular-compiled native control ownership or moving those controls in the DOM. Back restores them.
- Grid columns are equal responsive tracks with `minmax(0,1fr)`. Only spacing, grid positions and surrounding panels change; source input components keep natural height.
- Legacy v72/73 size-lock CSS was removed. Only previously injected 190px inline width/margin is cleared when it matches the old injected literal value, without modifying intrinsic NVIDIA Material dimensions.
- Advanced Pipeline Design is still a nonpersistent draft and does not POST or activate unverified backends. Native Live Settings Save route remains unchanged.
- JavaScript syntax, Git diff check and combined UI unit tests: **26 Passed / 0 Failed**.
- Static UI JS/CSS source copied to the AORUS OSC runtime Build; file hashes and actual HTTP `/mods/list` + asset responses verified, with backup at `C:\My Project\NVIDIA-Shadowplay\build_test\ui-deploy-backups\capture-native-md-advanced-v74-20261024`. No Capture/Container restart or Git push/commit.
- **Real post-v74 CEF layout screenshot is pending.** Static/runtime-HTTP verification does not establish final visual fit; validate main and advanced screens in the user's OSC next.

## v75 — Real NVIDIA Angular Settings Route (24 October 2026)

This section supersedes the v72/v73 fixed-size UI and the v74 pseudo-subpage. Read the full mandatory 195-line osc/mods/อ่านก่อนทำ UI.md, including section 8 on native Settings routes, before future changes.

**Native route:** main.osc-capture-advanced at /preferences/video-capture/advanced, registered in osc/index.html HEAD before Angular bootstrap. Uses NVIDIA osc-content, nv-osc-tile, osc-central-div, preferences-root-panel, osc-side-buttons, native md-input-container, md-select, md-option, and osc-button with hover-focus. Dedicated scoped sources: osc/mods/advanced-capture-route.js and advanced-capture-route.css.

**Main Video capture:** keep native NVIDIA replay/quality/resolution/FPS/bitrate and existing Capture Engine/API. Keep the custom display selector, capture cursor, and true runtime status. Remove the fake embedded advanced panel and codec/preset editor. Use Advanced Capture osc-button to navigate to the separate State; Back returns to main.preferences.video. A bounded initialization retry handles late ui-router DOM creation after Back. Clear runtime polling timers on route leave.

**Native control sizing:** never impose custom md-select or md-input-container width/height; only arrange grid placement, margins, and spacing. No full-OSC scroll workaround. Pipeline Design has six dropdowns but remains unsaved/unapplied, with unwired backends disabled. Live Encoder/Preset editor reads settings and Container catalog; only catalog-supported routes can be POSTed; all-GPU-failed FFmpeg NVENC synthetic results stay disabled. Encoder-specific presets retain native indices. A Save must preserve unrelated current Capture settings and be acknowledged by the service.

**Locales:** new oscCapture keys for en-US and th-TH, with existing native Back/Save/Reload keys. Other locale review is pending.

**Evidence:** JavaScript syntax, 31/31 Node UI tests, Git diff whitespace validation; runtime index, JS, CSS, and l10n were deployed with matching SHA-256 and HTTP asset verification. AORUS Runtime backup: build_test/ui-deploy-backups/capture-v75-runtime-predeploy-20261024. Real NVIDIA CEF visual and interaction verification remains pending; source/unit/HTTP tests are NOT the same as a real CEF screenshot. No Capture Engine changes, no capture/audio tests, no commit/push.

## v75 CEF Runtime Correction — 24 October 2026, 23:35 ICT

Screenshot after source deployment displayed the old v74 embedded Advanced Capture pane. Root cause: managed OSC parent PID 15428 started at 21:56:54, before the v75 UI assets were deployed. The live NvNode server was already serving v75, including the new Angular State and no old embedded Advanced markup. Static asset deployment alone did not reload the long-lived CEF browser.

Fix: checked Capture (ready=true, recording=false) and OSC parent ownership. Graceful CloseMainWindow returned true but the process stayed alive; stopped only our OSC parent PID 15428. NvContainer watchdog PID 34584 automatically respawned OSC PID 29832 at 23:35:02 with new CEF renderer PID 21640. Capture remained Ready/Idle. HTTP checks for index.html, video-capture-options.js and advanced-capture-route.js passed, as did the 31/31 UI regression tests.

To prevent stale CEF in subsequent UI deployments, use Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/restart-osc-ui-if-stale.ps1. Default is read-only: compares deployed asset timestamps to active OSC process start and validates Source/Build SHA-256. -Apply authorizes an OSC-only restart when stale, but only when the expected NvContainer owns OSC and Capture is Ready+Idle. Never restart unrelated NVIDIA App, NvNode, Container, or Capture for this.

Post-fix script dry-run: OSC_UI_STALE=False, UI_FRESH_NO_RESTART. Fresh process and HTTP checks are proven. Live CEF pixel/layout/button interaction remains unverified until observed in the OSC UI. No Capture/Audio recording or Git commit/push was performed.
