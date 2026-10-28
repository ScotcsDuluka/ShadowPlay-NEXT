# NEW CHAT HANDOFF — P0 ShadowPlay NEXT Overlay UI / Mods close bug

Date: 2026-10-26 Asia/Bangkok. USER REQUESTED HANDOFF IMMEDIATELY BECAUSE QUOTA IS 1%. Stop active investigation here; continue in a new chat. NOT ACCEPTED / NOT DEPLOYED.

## User's latest direction

LATEST USER OBSERVATION AFTER HANDOFF: **Alt+Z works, but other hotkeys appear not to work.** Do not describe all global hotkeys as broken. Investigate the non-OpenShare path separately: Helper registration/dispatch -> Container generic Hotkey event -> NodeAPI Socket.IO -> original hotkeyService/eventAggregator -> action service. OpenShare uses native /toggle whereas other shortcuts still rely on frontend service initialization; the observed CaptureState503 aborting appService.initializeUI may also skip hotkeyService.init. This is a hypothesis requiring real per-shortcut tests/correlation, not a confirmed cause. Preserve editor suppression; do not disable guards to make keys fire. No new live shortcut tests were performed after this user report.

CEF is working in the tested scopes; investigate UI or Mods at:
`C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc`

Actual user symptom: opening Overlay succeeds, closing/reopening becomes stuck. Do not assume a CEF library failure or rebuild Chromium reflexively. CEF full product acceptance and stable 240 FPS are still not proven.

## Read first

1. `CODEX-HANDOFF-CEF73-OVERLAY-ENDTOEND-20261026.md` (original complete G0-G7 assignment).
2. `CODEX-CEF73-OVERLAY-EXECUTION-REPORT-20261026.md` (this run's execution evidence).
3. `docs/BUILD-DEPLOY-POLICY.md`.
4. `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/AGENTS.md`, `mods/อ่านก่อนทำ UI.md`, full `mods/NVIDIA-OSC-UI-REVERSE-ENGINEERING.md` BEFORE UI work. Preserve NVIDIA UI, md-/nv- controls, Motion, V5, settings, localization. No redesign.
5. Evidence notes under `build_test/NVIDIA CEF73_Overlay_Test/20261026/contract/CONTRACT-VERIFICATION.md` and `native/NATIVE-G3-EXECUTION-NOTE.md`.

## Concrete live bug — strongest evidence

The coordinator started OLD production Launcher and enabled its existing power button once. No new binaries/assets/runtime were deployed. Old Container PID34584 was already running. Initial config power was false. User provided an idle window for actual key testing.

- Physical Alt+Z at 02:33:37: Helper sends OpenShare, HTTP200; Native OPEN; real CEF `#/base/main-menu`.
- Physical Alt+Z at 02:35:25: Helper sends OpenShare, HTTP200; Native logs CLOSED.
- At 02:35:26 AND 02:35:46: Native OPEN getter=false, but Angular `$state.current.name`=`main.main-menu`, menuCount1, screenshot still shows original NVIDIA menu. Motion phase=idle, target=requestedTarget=closed; hotkey monitor=true. This is real Native/UI/Motion divergence, not a missing hotkey event.
- Evidence `build_test/NVIDIA CEF73_Overlay_Test/20261026/live-baseline/{open-before-close,after-close-1,after-close-settled}.{json,png}` plus native/helper timelines.
- Fixed read-only diagnostic script: `build_test/NVIDIA CEF73_Overlay_Test/20261026/Capture-LiveState.cjs LABEL`. It uses CDP59099, Native read-only getter59013, monitor59011 and screenshot; NO reload. Requires running product. Never overwrite existing labels. No secret settings captured.
- At 02:38:34 coordinator used original Launcher power OFF to release stuck input and restore initially disabled state. Container34584/Graphics22620 survived; OSC/NodeAPI/Helper stopped. Last confirmed product state: Overlay disabled. Launcher may still be running (Computer Use window1115732; never reuse stale handle without fresh selection). Refresh current processes/config before action. No supervisor was force-killed.

## Derived root cause worth pursuing in osc/UI/Mods

Original `osc/app.js` oscDisplayService has private `P` (open) and `z` (transition).
OPEN sets z=true. Only its `$viewContentLoaded` listener clears z. Original closeOSC runs only if `P && !z`.

Real OPEN logs TypeError `getBroadcastPortalFromName` reads undefined[0] from main-menu controller (app.js20:21907 /34:28921). If controller/linking fails before viewContentLoaded, original z can stay true and CLOSE becomes a permanent no-op even when route/current says main-menu.

Contract audit confirmed sanitized live WebHelper log lines5778278-5778279 at02:33:03: GET `/ShadowPlay/v.1.0/Capture/State` status503 (also02:31:48). Original shadowPlayService.init awaits CaptureState without catch. appService.initializeUI chains all dependencies and initializes broadcastService/display/hotkey much later; a rejection skips late initialization. broadcastPortalPreferences may remain undefined. This causal chain is strongly derived; don't present every private flag as directly observed.

Capture worker is unavailable/disabled in this baseline. DO NOT change truthful503 to fake idle200, claim capture ready, or alter unrelated Capture engine. Prefer minimal fault-tolerant shell initialization or safe guard respecting unavailable features; avoid duplicate service listeners if full initialization later resumes. This design was still under evaluation, NOT implemented as an approved broad init rewrite.

Motion source and deployed `osc/mods/osc-motion.js` were hash-identical `054A314BD037539980CB98546AC11EB6773EDB69F02DA07ADE1180CB2A22EA22`. Its decorator drops pendingClose and releases visual even if originalClose no-ops. Therefore Motion closed does not prove actual route closed.

## Source changes already made, not deployed

Native OSC agent modified ONLY native convergence/request logic and focused tests, preserving pre-existing dirty FPS work:
- `src/osc_main.cpp`
- NEW `src/OscVisibilityConvergence.h`
- NEW `src/OscToggleRequestGuard.h`
- NEW `tests/OscVisibilityConvergenceTest.cpp`, `.vcxproj`, `OscVisibilityPageAction.test.cjs`.

Latest desired state/generation, cancellable single JS retry chain, page ACK/failure, guarded route observations, native6s convergence recovery to CLOSED/input release, immediate native-CLOSED click-through, stale/duplicate HTTP guards and correlation. Latest bounded repair uses existing `$state.go('base')` only after original CLOSE/Motion completes but route is stale; restores public Motion on reopen. Tests include real osc-motion engine/decorator and original-close-no-op simulation. Native route handler compares observation to actual current main-frame URL to reject queued stale route messages. Header parser rejects empty-first duplicate headers.

FINAL actual Release OSC binary (all current edits):
`build_test/NVIDIA CEF73_Overlay_Test/20261026/native/osc-build/NVIDIA OSC.exe`
1,400,320 bytes; SHA256 `CA9B7791FB2CA567A0D7FF5D529390BDDEA07664A351CAAAE31FED7B930A418D`.
`build-osc-final.exit.txt`=0; `native-source-build-manifest-final.json` records exact source/output. Earlier build2 manifest/binary hash7A406... is SUPERSEDED. x64+Win32 native regressions PASS, 11/11 actual emitted JS VM tests PASS, diff-check PASS. Unit tests do not prove live rapid delayed navigation or total UI deadlock recovery.

Contract agent changes:
- Helper `NvHotkeyHelper.cs` + NEW `HotkeyDispatchState.cs`: remove150ms quiet latch, retain OS MOD_NOREPEAT, serialize correlated requests, expire/drop editor generations.
- Container `NvContainer.cs`, `Components/Diagnostics/ContainerTestApi.cs` + NEW `HotkeyRequest.cs`: exact hotkey route reports actual downstream failure; generic publishers preserved.
- NodeAPI `index.js`, `shims/container-hotkey-event.js`, NEW `hotkey-monitor-state.js`, `osc-runtime-state.js`, `route-floor.js`, `NvShadowPlayAPINode.js`.
- NEW UI behavior-only `osc/mods/hotkey-monitor-lifecycle.js`, registered in `osc/index.html`: uses existing NVIDIA monitor service/router, explicit session/sequence/reload predecessor; no unsafe monitor expiry, no style/geometry changes.
- Focused Node/C# tests in source; source/build manifests and exact commands in contract note.

Contract verification: 32/32 Node regressions PASS, 59 C# assertions PASS. Isolated Release Container and Helper built; helper has existing WebRequest deprecation warning. Unrelated existing keyboardShortcutActions test fails CSS selector absent in unchanged base.css and HEAD; left unchanged. Source manifests must be rechecked for late edits before staging.

## CEF evidence / consumer integration

Selected candidate FC21, NEVER default to experimental876E:
`FC21F54CE6C628CCE0A56B652C6FC62E916AC63EF4E1B19D2ADA0DEE4F927C9D`.
Legacy production CEF remains `1B547411E093468639C98D8E9D83A2F57A7F19A9A6895A3A966A9F7269E7E73A`.

- Fresh isolated matched fixture candidate75/120/240/boost/restore=74.986/118.178/226.664/195.328/74.992 uniqueGPUFPS. Capture/shutdown PASS, performance strict3/5; boost fails handoff216-264 exploratory tolerance. Do NOT claim stable240. Legacy0/5 ~55-60. Evidence cef-runtime/attempt1.
- Actual isolated Launcher candidate startup/bridge/shutdown PASS once: original UI/body/bootstrap, no-supervise/hidden/self-exit, no childservice executables in lab, no product process/hash changes within its testwindow. Evidence cef-runtime/launcher-consumer-attempt1. Existing Launcher JS32 tests, shutdown7, HTTP11, module19scenarios/5204assertions PASS. No live Launcher interaction acceptance for candidate.
- Original OSC UI standalone CEF mock attempt1 FAILED close and had inadequate snapshot timing/orientation; retain failed evidence.
- Corrected justified mock attempt2 PASS scoped UI: original unchanged NVIDIA assets+realAngularservices, truthful SYNTHETIC fixture envelopes, all requests intercepted/cancelled in-process, NO production contact/globalhooks/IPC. Base→menu→Settings→keyboardeditor→back→base→native-scriptreopen→base. Native ACK1closed/2open/3closed; no TypeErrors,822GPUcallbacks/0CPU,6correctlyorientedreadbacks; closedimageempty; cleanCefShutdown/noorphans. Evidence cef-runtime/osc-ui-mock-attempt2. These are mock responses, NOT live Capture-ready proof.
- Isolation agent was adding separate attempt3 specifically actual CefBrowserHost mouseclick/Tab input (because route calls alone do not prove input). USER STOPPED FOR QUOTA BEFORE RESULT WAS COLLECTED. Inspect current agent note/files/processes; do not claim attempt3 PASS unless exact receipt confirms. No new retries/builds needed just to fill quota. Existing bounded fixture may finish safely in background; never kill production processes to clean it.

## Backup / static stage / deployment blocker

Full compatible rollback snapshot2361files:
`D:\cef73-build\rollback\overlay-endtoend-25691026-022611\product`
Thai Buddhist year appears in foldername; actualdate2026-10-26. Adjacent manifest; full offline restore-drill2361SHA256 matched0differences. Includes runtime/Launcher/OSC/NodeAPI/Container/Helper/Adapter/config/private data. Never print private config. This is OFFLINE rollback drill, NOT live rollback.

Static complete compatible product layout prepared:
`build_test/NVIDIA CEF73_Overlay_Test/20261026/stage/product`
Derived from rollback with all70matchedcandidateCEFresources copied/hashed. `stage/preparation.json`, `cef-runtime-manifest.json`, `DO_NOT_LAUNCH.md`.
IMPORTANT: consumer binaries/modifiedJS HAVE NOT BEEN REFRESHED INTO THIS STAGE. It still has OLD consumer binaries and backup assets; candidate resources alone do not make a deployable accepted bundle. DO NOT LAUNCH this staticstage: globalhooks/mutex/IPC/config may collide.

No production files were deployed. No installer staging into PendingUpdates was done yet by coordinator. Production legacy runtime remains. Do not report source Build/Test as live fix.

Live Container normal shutdown/activation blocker: old NvContainer is a WinExe with infinite watchdog and no shutdown/cancellation/WM_CLOSE route. BUILD-DEPLOY-POLICY forbids force-killing it/partial bundle replacement. Launcher EXIT_ALL falls back TerminateProcess; do not use as a normal safe exit. Official `Scripts/Install-NvContainerBuild.ps1` will stage full fourfilebundle and exit2 while oldContainer lives. Source Release is contract/container-release. Exact old ContainerDLLhash `CBFC84C5636C8689F0C4B3621F38EECA229393B20DA50F8658695E4B9A9DD1B4`; newbuild hash manifest records D17E... . Do not claim newContaineractive.

Potential next rollout must explicitly address this blocker and existing supervised process identities, complete G1-G4 isolated/input gates, refresh compatible stage+hashes, close only Launcher gracefully while powerOFF, preserve settings, guard deploy and automatic rollback on any live smoke failure. Existing oldContainer compatibility does NOT establish its new acknowledged contract is active. Do not reflexively deploy an incomplete mixed runtime.

## Hook / acceptance still outstanding

No Hook game launched this run. Desktop live after-fix20toggles/input/focus/Settings/editor NOT TESTED, G5 NOT TESTED, G6 NOT DEPLOYED, finalG7 NOT ACCEPTED.
Controlled installed candidates freshfingerprinted:
- `D:\SteamLibrary\steamapps\common\Floating Point\Floating Point.exe`,x86,SHA256851740503CBC607B635357D45F51462430B0D8B3C4EEF2B80ACEE6CBDE1EA240.
- `D:\SteamLibrary\steamapps\common\100 hidden frogs\100_hidden_frogs.exe`,x86,SHA256D6192DD63743A14387A3FEC91593237EC34AAD319F48DC8D3005646EA4F280B4.
Confirm actual graphicsAPI at runtime. No uncontrolled injection or closing unrelated games. G5 only afterDesktopPASS.

Root pure HostPublishCadence x64/Win32 tests PASS (hiddenmetadata/unchangedserial/copyfailure/transportpolicy); host-visibility/receipts.json. Not liveHookproof.

## Working tree / next-chat workflow

Repo Stable, extensive PRE-EXISTING dirty Capture/Hook/UI/FPS changes. Preserve all. No commit/push/reset/checkout/fullrebuild. Local AgentBus had no .task/.working owners at initial check; recheck. Source owners were native/contract/isolation subagents in this chat, asked to stop newwork at user's quota request; their disk evidence persists but newchat must recheck backgroundprocesses.

Recommended next sequence: read evidence→inspect actualUI/Mods bootfailure/Capture503 consequence without fakingready→review preparednative/contractpatches→collect pendinginputmockreceipt→resolve compliantactivation/staging→guardeddeploy+rollback→realphysicalAltZ20+Settings/editor/focus→controlledHook. Never call COMPLETE until actual live toggles pass, or report exact remaining blocker with evidence.
