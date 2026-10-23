# Freestyle V5 continuation — 2026-10-23, approximately 17:33 Bangkok

## Start here

User requested a New Chat handoff because account usage showed 6% remaining. This is a continuation of the implementation, **not a completed-release claim**. Read this file AND `CODEX-HANDOFF-FREESTYLE-V5.md` fully. Continue development from the current code; do not redesign or reset anything.

Repository: `C:\My Project\NVIDIA-Shadowplay`. Required branch: **Stable**. Canonical source: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc`; tests: neighboring `tests/`. Runtime: `build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc`. Original NVIDIA reference: `C:\Users\ScotcsDuluka\Downloads\NVIDIA ShadowPlay.7z`, read-only unpack `C:\My Project\_ShadowPlay-Archive-Clean-Audit-20261023`.

User authorizes real CEF testing. Do not restart a game, rebuild the whole Build, or touch Capture, Hook, Container, GameWatcher, osc-renderer, unrelated localization/index or other chats' files. Never reset/delete dirty work. Many unrelated files remain dirty/untracked. Stage explicit owned files only; inspect status and staged files before every commit. Other chats can move HEAD and restart Host/NodeAPI.

## Latest user symptoms and resolved drag regression

User reported clipped native controls, white context menus, Settings content overflowing its panel, old V2 editor flashing on entry and exit, and then real mouse dragging stopped working.

**Actual drag cause confirmed:** an earlier fix rejected `mousemove` when `ev.buttons === 0`. Both native Host forwarding paths build `CefMouseEvent` with Shift/Ctrl/Alt but omit mouse-button modifier flags (`osc_main.cpp` MkMouse/MkMousePt ~3462–3521; WM_MOUSEMOVE ~3701/4001). Real forwarded movement therefore has a zero DOM button mask. CDP drags supplying buttons=1 had passed and hid this regression. The frontend now trusts its captured down/up drag state; **do not restore the zero-buttons gate**. Host was inspected read-only, never edited by this workstream.

After the latest backed-up Runtime sync, the human explicitly replied **“ลากได้แล้ว”** to a request to try actual mouse dragging. This is the strongest mouse-path validation in this session. Native action guards remain active in Edit.

Latest code commit: **`a5bb83e40f5d5db7ed5dc30a388b7d348835a1a8`**, `Accept native Host drag movement and hide closing editor before release`.

Latest Runtime deployment: **`20261023-102555-5f3b0f05`**, 2 files (editor JS/CSS), backed up and SHA verified. Manifest under `build_test/OSC-Live-Preview/sessions/<session>/manifest.json`.

## Completed commits and Runtime sessions

| Code commit | Change | Backed-up Runtime session |
| --- | --- | --- |
| `f5874d39b1ce8fdb5b6c68ea6c04de27bbee50b6` | Single native owner, contained Settings/editor lifecycle, model, ownership and deployment tests | `20261023-094858-03a8c373` (9 files) |
| `dc7221083f6ea99d65646543cda8f744766eef36` | Earlier CDP-hover workaround — its zero-buttons gate was later corrected in a5bb | `20261023-095348-11330a7c` |
| `5853504f5c7a11c27bba1898614ac4130ab58694` | Angular palette ordering, native Big/Small/Side faces | `20261023-100018-5214cf63` |
| `6d8463e1b9f2541e30cebe3c7edfbe4111640310` | Before-paint editor mounting, Settings fit, stable rail coordinates | `20261023-101246-6fee7670` |
| `89c4fc43e3dd20ddcfbc05dfcc04c9cc622379e4` | Slot guides below Group editing buttons | `20261023-101503-38490927` |
| `a5bb83e40f5d5db7ed5dc30a388b7d348835a1a8` | Real Host zero-mask drag + closing editor hidden before Angular release | `20261023-102555-5f3b0f05` |

All inherited V5 dirty source was preserved and committed. At handoff the owned V5 source/test/tool scope is clean. Other workstreams remain dirty; do not include them.

## Important implementation details

- `gpu-profiles.js::syncNativeMainMenuSlots` was the remaining V2 geometry writer every 500 ms. It now returns through V5 when V5 owns the layout, cancels queued V4 work, and keeps all 12 Angular tiles alive (`visible=true`). V5 controls visibility/geometry; V2 must not unlink controls.
- `main-menu-live.js` has V5 guards on old V2 input paths and routes assignment/variant queries through the V5 model.
- `nv-next-v5-native.js` owns existing native md-menu nodes, never clones them. Temporary edit ownership does not persist the feature flag. Done commits ownership; Cancel restores original styles, priorities, classes and attributes. Bar is a positioned coordinate parent. Route exit releases the lease.
- Runtime index did not import native V5 CSS. Adapter appends a single stylesheet link `nv-next-v5-native-style`; index belongs partly to another chat and was not modified here.
- Rail/bar recentering is atomic (`transition:none!important` on the V5 rail). Tweening its width/transform made coordinate origins and guides move while dragging. Native tile transitions remain for edits and yield to OSC Motion.
- CEF 73 needs explicit left/top/right/bottom instead of CSS inset. Native md-menu-content has an explicit dark background. Big variants for arbitrary controls use native assets with a centered 120px icon; text appears for Big/Side and hides for Small.
- Palette reorder changes `scope.flexMenu.items` inside `$evalAsync`. Never manually move the ng-repeat button without Angular anchors: that broke search and neighboring controls. Camera displacement/search/clear was checked in actual CEF.
- Delayed insertion uses transient adapter preview state; draft `get()` stays unchanged until drop. Preview is cleared on blur/cancel/drop. `+ Group` supports cross-panel insertion/empty Group creation. Release click is suppressed.
- Domain core retains optional backwards-compatible `controlOrder`; displaced controls preserve prior order. Palette-to-occupied replaces the prior control to palette; Settings may never be ejected. Six Groups is a global maximum.
- Settings moves original Angular background controls with comment anchors, uses header + flex remaining 80/20 grid, and scrolls the palette internally. Native md-select nodes remain native. Monitor footer is at the bottom. No outer clipping/scrollbar at the verified 1920x1200 viewport.
- Body MutationObservers organize Settings and mount editor before paint, with interval fallback. Legacy details are hidden immediately. Panel entrance animation is disabled to keep buttons stationary.
- Closing fix: unready V2 editor shells remain hidden even after `nv-live-layout-editing` disappears. `hideShell()` sets visibility hidden/pointer-events none **before** original `flexMenu.done()` changes Angular editing state. Cleanup also hides before removing V5 class. Start/scan refuse `.ng-leave`, destroyed scope, or `flexMenu.editing===false`; closing DOM cannot reacquire ownership.
- `closeShell()` invokes original Angular done directly. A synthetic click was unreliable during Motion close. Motion target closed cancels a live editor.

## Verified tests and honest limits

Latest selected Node suite: **103/103 pass**, log:
`build_test/OSC-Live-Preview/v5-session-20261023/final-exit-drag-tests.log`.

Selected tests: nvNextV5Native, nvLayoutV5Core, nvFreestyleSettings, nvFreestyleV5EndToEnd, nvFreestyleEditorLifecycle, nvArchiveCustomizeBaseline, nvV5Ownership, launcherLanguageSync, oscMotion, oscLivePreview.

Lifecycle tests now 13/13, including Host-shaped zero-mask drag/drop and delayed insert, native action capture, lingering Angular leave DOM, destroyed/false-editing guards, and hiding before Angular/V5 state release.

Actual CEF observations (not just unit tests):

- Default draft has 6 guides and all 12 native controls; layout/positions validated.
- Control swap + UI Undo; Group swap + UI Undo; delayed insert; cross-panel move; delete Group returns Stream; draggable +Group creates an empty Group on another panel; palette Stream drop opens 8 native-style presets and fills a Side block.
- Small wideLeft / Side detailRight displacements and ordering; Auto-Fill native heights 240/120/80; Settings cannot be dropped to palette. Capture/Record action spies stayed empty during Edit.
- CEF trusted input with `mousemove buttons=0` now starts and completes a Group swap. Human then confirmed physical dragging works after Build deploy.
- Source preview Cancel closure probe saw visibility=hidden and pointer-events=none before original Angular done; no guides/panel after closure. Same source then committed and deployed.
- Build-only lifecycle run `build-regression-2026-10-23T10-26-25-077Z-lifecycle`: HTTP provenance, initialized main menu, Esc, Done persistence/close, and reopen+Cancel checks **all passed**. However **overall run is false** because restoration of route/Motion timed out after raw V5 data was already restored and asserted. Do not relabel this whole run as passing.
- Existing legacy `mainMenuCustomize.test.js` had 4 pre-existing failures against archive-restored baseline; it is separate from the passing V5 selection. Do not change unrelated tests to conceal this. Broader Game Filter/keyboard shortcut suites are outside this validation.

Still required before calling V5 complete:

1. Run real CEF Settings viewport checks at **1920x1080 and 1280x800**; only 1920x1200 was visually measured so far. Inspect both whole panel and inner palette bounds, native md-select popup, compact controls, and Host scaling.
2. Finish the Build-only interaction regression including final Cancel, and rerun lifecycle with reliable route/Motion restoration. Earlier interaction checks through native action guards passed, but earlier final Cancel failed because runner selected English text while localization changed it to Thai. Runner now uses a locale-independent Cancel selector.
3. Recheck repeated opening/closing via user-visible Settings flow, no V2 flash, no guide remnants, actual Motion entry/exit and native commands after Edit.
4. Check temporary ownership with V5 flag initially disabled in actual CEF, preserving exact user storage. VM ownership restoration is covered; this specific fresh-disabled CEF path still needs final evidence.
5. Verify add/remove/block-change animation quality, rather than claiming every animation is visually reviewed. Native rail itself intentionally does not tween. CSS-scaled ancestors remain a review concern; current Host dpr 0.875 geometry worked.
6. Invalid saved JSON currently renders defaults with `lastError` rather than surfacing an editor warning. Never persist/reset invalid user data silently. Current saved JSON is valid.

## Data preservation / current state

Latest untouched CEF storage/state backup:
`build_test/OSC-Live-Preview/v5-session-20261023/before-new-chat-handoff-1032.json`.

At that read: route `#/base/main-menu`, Edit false, V5 enabled, viewport **1920x1200**, dpr **0.875**, **5 saved Groups**, 12 Angular native nodes, no edit guides. The user had removed g2/Stream earlier; do not silently replace their saved five-Group layout with default six. Use `E.apply(M.defaultLayout)` only as a transient test draft then Cancel. Current palette contains Stream. Original handoff backup and all subsequent backup files remain untouched.

The last screenshot `new-chat-handoff-runtime.png` is effectively hidden/black with particle lines (closed/offscreen composite). **Do not cite it as a successful normal-UI screenshot.** Earlier verified screenshots listed below contain visible native UI. Native Host/NodeAPI restarted externally several times; target IDs and sockets can go stale. Do not diagnose user data changes as corruption or another chat without tracing writes.

Raw storage changed during some runs without test mouse input (possibly external/user actions). Before any restore, compare fresh current values to your test's expected ones. Never restore an old backup over newer user edits. A Storage.prototype.setItem trace can identify writers; any such trace in this run was only in-memory and lost on reload.

## Evidence files

Everything below is under `C:\My Project\NVIDIA-Shadowplay\build_test\OSC-Live-Preview\v5-session-20261023\` (ignored artifacts, preserved locally).

- `final-owned-provenance.json`: all **9 owned frontend files** have equal Project/Build SHA256 (gpu-profiles, main-menu-live, core, native JS/CSS, editor JS/CSS, settings JS/CSS).
- `source-six-groups.png/json`: visible default six-Group native layout.
- `source-context-dark-confirmed.png`: dark context menu.
- `source-panel-fit.png`: verified 1920x1200 80/20 panel; panel x199.68 y440 w1520.64 h504, columns ~1170/292.5, inner bottom926, palette152px client/528px scroll.
- `source-any-control-big-small.png`: Performance Big / Screenshot Small.
- `source-flash-free-new-group.png`: new empty Group and palette placement.
- `source-host-zero-mask-drag.png/json`: trusted CEF zero-mask Group swap.
- `source-exit-no-legacy-flash.png/json`: visible normal UI after Cancel of the latest source patch (then deployed unchanged).
- `build-native-normal.png/json`: earlier Build normal native UI.
- `build-regression-2026-10-23T10-26-25-077Z-lifecycle/`: Build functional lifecycle passes, failed final route restore (see above).
- Other `build-regression-*` folders retain failed attempts and screenshots; inspect summary.json rather than assuming every image is a pass.
- `source-user-layout-fixed.png` is black and `source-normal-confirmed.png` still had a panel; these are not successful proof.

## Tools and reliable CEF access

HTTP **59011**, Host **59013**, CEF debug **59099**. Actual engine Chromium 73. No browser mockup fallback.

Helper: `build_test/OSC-Live-Preview/v5-session-20261023/cef-harness.cjs`.
Runner: neighboring `build-regression.cjs` (ignored local helper). Latest agent update is syntax checked but **not run**: restoration now records `rawDataRestored`, `routeRestored`, `routeSettled`, `checksPassed` separately; saves `restored-v5-raw-before-route.json`, `restoration.json`, bounded Motion samples. It waits for the linked 12-menu Angular toolbar before original Motion open, then actual CEF paints within a 6.5s bound. Overall failure remains a failure. Optional `--host-mask-drag` sends runner mousemoves with buttons=0. Inspect source before using; final integration remains pending.

```js
var nr = await import('node:module');
var req = nr.createRequire('C:/My Project/NVIDIA-Shadowplay/package.json');
var h = req('./build_test/OSC-Live-Preview/v5-session-20261023/cef-harness.cjs');
var cef = await h.CefHarness.connect({inputScale: .875});
await cef.call('Page.enable'); await cef.call('Runtime.enable');
console.log(await cef.state());
```

Use node_repl tool (discover tool by name). Close/reconnect after process restart or standalone runner use; simultaneous CDP sessions caused timeouts. CDP coordinates must be multiplied by dpr .875 **once**: helper point() returns CSS coords and mouse()/drag() apply configured scale. Do not double-scale.

Native Host does not expose a targetable app/window to the available computer-use tool. We used CDP for CEF, and the human confirmed physical drag. Do not substitute arbitrary PowerShell native-input injection.

Start Edit with `window.__dulukaStartMainMenuEditor()`; do not call flexMenu on the wrong mainMenu scope. Route via Angular `$state.go('main.main-menu')`. Initial Motion can be closed even with a main-menu URL. Call original `__dulukaOscMotion.open()` only after actual main-menu nodes exist; a real Page.captureScreenshot can flush deferred offscreen rAF before waiting idle. Do not replace Motion or fake its state. POST `/show` 59013 shows Host but can race route lifecycle; no need to restart services.

Helper can intercept selected Project files without writing Build: `enableSourcePreview({backend:'network',relativePaths:[...]})`. CEF Fetch interception unsupported. Always back up first, enable Page/Runtime, use Network backend, disable and close before Build-only validation. Handoff has **no active source preview or CDP connection**.

Native Host stationary zero-mask moves can interfere with synthetic CDP drags because real OS cursor remains elsewhere. **Do not fix a test-only cursor mismatch by discarding real zero-mask moves in production.** Human mouse operation is valid after a5bb.

Runner commands (back up data automatically; run only while no other tester clicks Overlay):

```powershell
node 'build_test/OSC-Live-Preview/v5-session-20261023/build-regression.cjs' --phase lifecycle --keep-page
node 'build_test/OSC-Live-Preview/v5-session-20261023/build-regression.cjs' --phase viewport --keep-page
node 'build_test/OSC-Live-Preview/v5-session-20261023/build-regression.cjs' --phase interaction --host-mask-drag
```

## Safe targeted Runtime sync

Tool: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/osc-live-preview.cjs`.
It now supports `--files` / `--files-json`, so unrelated dirty frontend files do not block syncing **clean, tracked, committed selected files**. Default whole-tree behavior is still strict. Git blob normalization handles CRLF; raw SHA/races checked; all changed Runtime files backed up before first write. Never skipGit in real Runtime, hand-copy files, or roll back non-LIFO sessions casually.

```powershell
node 'Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/osc-live-preview.cjs' --plan --files mods/nv-freestyle-editor.js mods/nv-freestyle-editor.css
node 'Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/osc-live-preview.cjs' --apply --files mods/nv-freestyle-editor.js mods/nv-freestyle-editor.css
```

## Next action

Start read-only: fresh git status/HEAD and CEF state/storage backup; inspect runner restoration update. Finish viewport and final Build CEF checks, fix only evidenced problems, preserve current user layout, commit selected changes, sync with backups, report actual screenshots/results and any remaining limitations. The drag regression is resolved and human-confirmed; do not regress it again.
