# ShadowPlay NEXT / CEF73 Overlay execution report

Latest user observation at quota handoff: **Alt+Z works; other hotkeys appear unavailable.** Added to the new-chat handoff as a separate non-OpenShare event/init investigation. Not yet tested live, no blanket claim that every hotkey is broken.

> USER REQUESTED QUOTA HANDOFF. Final continuation state is in `CODEX-HANDOFF-CEF73-UI-MODS-20261026-NEXT-CHAT.md`. Production patch NOT DEPLOYED; actual legacy close failure reproduced. Latest native Release artifact CA9B7791FB2CA567A0D7FF5D529390BDDEA07664A351CAAAE31FED7B930A418D. Original UI CEF mock attempt2 passed scoped navigation/render/shutdown; actual input attempt3 was pending when user stopped. All final acceptance remains OPEN. Last confirmed live Overlay power was turned OFF normally at02:38:34 to release stuck input; Container/Graphics preserved.

Date: 2026-10-26 (Asia/Bangkok). Coordinator: this Codex chat. Status: IN PROGRESS, NOT ACCEPTED.

User authorizes G0-G7 source investigation, fixes, build, tests, guarded deployment, coordinated restart, live verification and rollback. No commit or push authorized. Historical handoff and CEF evidence remain unchanged. No secrets or setting values are included here.

## Gate checklist

0. G0 baseline and ownership: IN PROGRESS.
1. G1 exact matched isolated CEF consumer: IN PROGRESS; actual staged OSC launch prohibited until hooks, singleton, IPC and paths are isolated.
2. G2 Helper / Container / NodeAPI contract: source audit, focused fixes and tests IN PROGRESS.
3. G3 native/page convergence: source audit, focused fixes and tests IN PROGRESS.
4. G4 Desktop input / visibility: NOT TESTED.
5. G5 controlled Hook game: NOT TESTED; requires Desktop PASS first.
6. G6 build / stage / guarded deploy / restart / rollback: NOT DEPLOYED.
7. G7 automated and live acceptance: NOT ACCEPTED.

## Initial observations (02:21-02:24 Bangkok)

- Read the complete handoff and mandatory build policy, UI guide/AGENTS, Helper README, AgentBus README and the three CEF evidence reports. UI Atlas will be read before any UI edit.
- AgentBus contains README/script/template only, no `.task` or `.working` ownership. Root/parent AGENTS absent. Existing source tree has extensive pending changes, including FPS work in `osc_main.cpp`; preserve them.
- Product Container PID 34584 is running from the exact product root (started 2026-10-24 19:46 Bangkok); product NvGraphics PID 22620 also running. No matching product OSC/Launcher/Helper process observed.
- Official NVIDIA App Overlay and nvsphelper64 also run from Program Files. They are separate from this product; possible Alt+Z conflict is unproven pending Helper registration evidence.
- Product control `127.0.0.1:59012` responds HTTP 200 to status and adapter status. NodeAPI :59011 and native OSC :59013 do not connect. Port :3000 is owned by PID 4 and does not establish NodeAPI readiness.
- Product overlay enabled flag is FALSE. This explains why the supervisor currently keeps its overlay children stopped; it does not establish the root cause of the user's prior failure.
- Production legacy CEF/resources are still present. Candidate FC21 and experimental 876E remain separate; experiment is not selected.

## Work allocation

- Coordinator: baseline evidence, report, backup/staging/review, live actions and final acceptance decision.
- Native subagent: only native OSC convergence fix and regression tests; preserve existing FPS changes; isolated output only.
- Contract subagent: Helper/Container hotkey acknowledgement/order/editor lifecycle fixes and tests; no runtime mutation.
- Isolation subagent: matched candidate/legacy fixture evidence and isolated Launcher build/tests; no real consumer launch or deployment.

Evidence root: `build_test/NVIDIA CEF73_Overlay_Test/20261026/`. Baseline script records sanitized process/endpoints plus hashes, not raw configuration.

## Progress log

- Initial source audit found native independent JS retry closures without cancellation/generation and route events that can reverse a newer external intent. Fix/test underway; not yet runtime-proven.
- Initial contract audit found HTTP success reported despite failed downstream delivery, a 150ms rearm rule dropping distinct rapid presses, and editor suppression that can outlive page reload. Fix/test underway; no guard bypass allowed.

### 02:27-02:28 Bangkok - baseline and rollback evidence

- G0 sanitized evidence captured: `baseline/baseline-processes.json`, `baseline/baseline-endpoints.json`, `baseline/baseline-hashes.json`, `baseline/baseline-summary.json` (2,361 files). Explicit POST to :59013/toggle could not connect/complete while product OSC is absent; `baseline/explicit-toggle-baseline.json`. No physical Alt+Z result claimed at this point.
- Full compatible rollback snapshot: `D:\cef73-build\rollback\overlay-endtoend-25691026-022611\product`. Folder year uses the host Thai Buddhist calendar; actual session date is 2026-10-26 Gregorian. Manifest is beside product. Runtime, Launcher, NodeAPI, Container/Helper, Adapter, configuration and private data are preserved. Volatile logs/cache/catalog are marked separately in manifest.
- Full offline restore drill copied all 2,361 files into a separate inert `restore-drill` directory and verified SHA-256 with zero mismatches. This is an OFFLINE drill, not a live rollback/restart. Receipt: `baseline/rollback-preparation.json`. No production replacement.
- Confirmed production legacy libcef SHA256 `1B547411E093468639C98D8E9D83A2F57A7F19A9A6895A3A966A9F7269E7E73A`; OSC EXE `A2A778C40FD84987101637A43046F291FB718301E5BF42E9894AEFFF4C0EA136`; Container DLL `CBFC84C5636C8689F0C4B3621F38EECA229393B20DA50F8658695E4B9A9DD1B4`. Exact complete baseline is in JSON.
- Fresh candidate/legacy matched fixture attempt1: both exit 0, GPU pixel evidence complete, no leftover fixture process. Candidate fresh FPS at 75/120/240/boost/restore = 74.986 / 118.178 / 226.664 / 195.328 / 74.992. Strict +/-5% passes 3/5; exploratory handoff 216-264 tolerance accepts 240 phase but rejects boost. Legacy = 59.987 / 54.681 / 55.687 / 59.167 / 57.989, strict 0/5. Data: `cef-runtime/attempt1/candidate-legacy-comparison.json`. These are fixture results only, not OSC or Hook acceptance.

### 02:30-02:32 Bangkok - focused source verification

- HostPublishCadenceTest built and ran independently for x64 and Win32, both PASS. Tests verify native publish counter advances only after successful new-source copy, unchanged/old frames stay unchanged, hidden clears retained frame number, and existing transport policy remains intact. Evidence: `host-visibility/receipts.json` and separate build/test logs. This is a pure regression test, not actual Hook or game rendering proof.
- G2 first focused test run reports 20/20 Node tests (ephemeral HTTP, jittered 20-toggle sequence, editor/session ordering and backend failure) and 59 C# assertions. Build and coordinator review continue; final counts will be recorded after all edits.
- Read the complete mandatory UI Atlas and current native service behavior before the behavior-only hotkey lifecycle mod. Native NVIDIA service and UI-Router are reused; no CSS, new menu or component replacement is part of this fix.
- Identified an activation constraint: the live legacy NvContainer WinExe uses infinite watchdog loops without cancellation/normal shutdown route. Existing Launcher EXIT ALL falls back to TerminateProcess; this is not a compliant normal supervisor exit under BUILD-DEPLOY-POLICY. No force-stop performed. A running Container update can only be staged pending a safe exit; isolated work continues.

### 02:31-02:36 Bangkok - real legacy runtime symptom REPRODUCED

- Started the existing product Launcher once via Computer Use. Clicked its original power control at 02:31:41 to enable the existing overlay chain. This is baseline operation, not candidate deployment. Old Container PID34584 started WebHelper23028, Helper32032, NvNode34264, OSC36264 with legacy libcef actually loaded from the canonical path.
- Computer Use initially rejected input because concurrent user input was detected; no key was delivered. User subsequently explicitly provided an idle input window. Meanwhile product log records a power OFF/ON sequence at 02:32:55/02:32:58 without coordinator input; do not attribute it to our sent actions. Subsequent baseline PIDs: WebHelper8432, Helper3252, NvNode12108, OSC2232 (children20904/28600), Container34584 unchanged.
- Physical Alt+Z sent successfully at 02:33:37. Helper logs OpenShare queued and HTTP200; native logs OPEN and actual CEF route changes to `#/base/main-menu`. Read-only CDP capture `live-baseline/open-before-close.json/.png` shows original NVIDIA menu, Angular ready, Native OPEN, motion idle/open.
- Physical Alt+Z sent once at 02:35:25. Helper logs delivery200 and native logs `CLOSED (was OPEN, intent2)`. At 02:35:26 and again 02:35:46 the CEF route remains `main.main-menu`, menuCount1; motion is idle/target CLOSED; Native OPEN getter is false; hotkey monitor true. The screenshot still contains the original NVIDIA menu. This is a concrete G3/G4 FAIL, matching the user's report that close/reopen gets stuck.
- Evidence: `live-baseline/after-close-1.json/.png`, `after-close-settled.json/.png`. Capture script uses fixed read-only diagnostics and Page.captureScreenshot only; no reload/navigation/setting values are captured. Snapshotting CDP content does not independently establish desktop compositor/input correctness.
- Native/source subagent notified to extend the actual generated-script regression with this live route/native/Motion divergence before any deployment. No repeated twenty-toggle test while this basic close gate fails.

### 02:51–03:02 Bangkok — ChatGPT takeover after Codex usage limit

- Checked AORUS-DESKTOP online; read full report, task handoff, source/test state and build policy. No new AgentBus .task/.working ownership. Existing Codex G2/G3 source changes, native builds and earlier live screenshot proofs preserved; no repo reset, git commit/push, process killing, game action or UI key input.
- Confirmed Codex live legacy bug: Alt+Z OPEN works physically, and one Alt+Z CLOSE sets Native CLOSED but Angular route remains main.main-menu / Motion visually CLOSED; CEF screenshot still shows menu. This is the precise G3 failure, NOT a 240 FPS failure. Native fix latest source hash 851674FD213F6B08DBB605357ACEC946D88CF848BCBF574DAEECDD0A0928D95F; isolated built NVIDIA OSC.exe SHA CA9B7791FB2CA567A0D7FF5D529390BDDEA07664A351CAAAE31FED7B930A418D (native/build-osc-final.exit.txt=0). Preserve root UI NVIDIA design.
- New independent takeover regression evidence in build_test/NVIDIA CEF73_Overlay_Test/20261026/takeover-validation:
  - native-x64.log: PASS 2000 alternating intents, 20 jitter toggles and 8-concurrent duplicate request handling;
  - native-page-motion.log: PASS 11/11 with actual generated C++ page script and original Motion decorator;
  - node-g2-tests.log: PASS 32/32 NodeAPI/Helper lifecycle and security contracts.
  - First C# invocation with --no-restore in a brand-new artifacts path failed NETSDK1004 due missing project.assets.json; this is harness setup, NOT production source failure. Reran with normal restore under Run-Dotnet-Restore-Retest.ps1, exit0; container-hotkey-contract-restored.log: PASS all 59 assertions.
- Continued existing isolated integration staging WITHOUT deploying: Stage-Integration-Binaries.ps1 validated approved native exe, G2 container/helper release bundles and 8 NodeAPI/OSC web files; copied 17 files into stage/product with SHA checks, preserving existing 70 matching CEF candidate resources. stage/integration-build-manifest.json and stage/preparation.json show consumerBinariesRefreshed=true, deployed=false. Candidate CEF SHA FC21F54CE6C628CCE0A56B652C6FC62E916AC63EF4E1B19D2ADA0DEE4F927C9D. Stage is static only; do not launch before singleton/mutex/IPC isolation.
- Verified G1 static ABI on these newly staged binaries: staged NVIDIA OSC.exe imports 44 cef_ symbols and staged Launcher.dll 126, candidate libcef exports 197, missing=0 for both; takeover-validation/staged-import-audit.json PASS. This does not establish actual CefInitialize/UX in live OSC.
- Product NvContainer.exe PID34584 remains running (since Oct24), and BUILD-DEPLOY-POLICY forbids replacing locked EXE/DLL or forcing its stop. Existing pending version from Oct24 was fully SHA-backed-up (6 files) into takeover-validation/previous-pending-backup. Installer StageOnly invoked for NEW NvContainer G2 release, returned expected exit2; entire 4-file verified replacement staged in product NVIDIA Container/PendingUpdates/NextInstall with associated policy, not active. takeover-validation/container-pending-v2.json proves backup and pending. No old data discarded.
- At 03:01 checked active product NvContainer.dll still SHA CBFC84C5636C8689F0C4B3621F38EECA229393B20DA50F8658695E4B9A9DD1B4 and legacy product libcef SHA 1B547411E093468639C98D8E9D83A2F57A7F19A9A6895A3A966A9F7269E7E73A; new pending NvContainer.dll SHA D17E2A56A815E83F64F18044C42CC3FF1C57C285768CF3EBC5305A219B962DF0. Product Container running; OSC/NvNode children currently absent as product power/overlay is off. Product originals unchanged and rollback snapshot still available.
- BLOCKER: no normal graceful shutdown path for the running old WinExe NvContainer; Launcher EXIT ALL uses termination fallback. Policy prohibits force kill. Therefore DO NOT yet activate Container pending, swap complete CEF runtime or claim Desktop/Hook real behavior fixed. A safe maintenance window with verified normal shutdown/reboot and consumer startup needs coordination; do not assume the user's session can be interrupted. Separate staged Launcher/OSC processes must not start while global mutex/hook/config/IPC collide. Real G4/G5/G6/G7 acceptance remains OPEN.
- If work resumes: first verify current live product and pending SHA; then finish isolated consumer initialization and safe activation design; during authorized maintenance install matched CEF runtime and native/NodeAPI/Helper/Container release with full rollback, coordinated restart, physically verify Alt+Z open/close 20 toggles and real Desktop/Hook state. Do not use uncapped GPU 581fps mode or unaccepted deadline experiment.
### 2026-10-26 03:24–03:26 Bangkok — User clarified ShadowPlay NEXT not actively in use
- Interpreted as allowing a normal application shutdown for maintenance, NOT permission to force kill unrelated services or reboot the Windows desktop.
- Product Launcher PID28524 main window hwnd1115732 exited cleanly through user32.PostMessage(WM_CLOSE); no force. Its two CEF subprocesses exited. Product NvContainer.exe PID34584 (no MainWindowHandle), NvGraphics, OSC, NvNode and helpers remain running/owned by NvContainer. Capturing status GET 127.0.0.1:59012/v1/capture/status confirmed recording=false and no outstanding start/stop. Do not change actual recording.
- Examined actual source: Launcher LAUNCHER_EXIT_ALL calls LauncherSupervisor::InstallerExit -> launcher_shutdown::StopExactPath, which posts WM_CLOSE then calls ForceTerminate if processes persist after 1500ms; NvContainer is a headless while(true) watchdog, no built-in graceful shutdown endpoint in old running binary. Repo docs/BUILD-DEPLOY-POLICY.md prohibit force killing active supervisor. Plain Launcher WM_CLOSE does not end NvContainer.
- Windows Restart Manager preview safely identified exactly ONE target process PID34584 for installed NvContainer.dll, excluding genuine NVIDIA services; evidence takeover-validation/restartmanager-preview-diagnostic.log. Attempted RmShutdown(flags=0) non-force ONLY after verifying launcher exited, expected exactly one target, capture idle. Caller PowerShell exited abnormally (code -1073740940) with empty child log; no production process stopped. DO NOT REPEAT failed RestartManager attempt; marked both invoking scripts *.FAILED.disabled.txt. Exact source cause of crash not proven, do not ascribe to Container failure.
- Rechecked production process and SHA: NvContainer34584 still running, product NvContainer.dll hash CBFC84C5636C8689F0C4B3621F38EECA229393B20DA50F8658695E4B9A9DD1B4 (old); product libcef.dll hash 1B547411E093468639C98D8E9D83A2F57A7F19A9A6895A3A966A9F7269E7E73A (legacy). Staged pending new NvContainer.dll D17E2A56A815E83F64F18044C42CC3FF1C57C285768CF3EBC5305A219B962DF0 preserved. Full staging preflight after Launcher close: 87 files valid, 25 changed, 8 owned active runtime processes, apply=false, NO product overwrite. Guarded installer remains at build_test/NVIDIA CEF73_Overlay_Test/20261026/Promote-StagedOverlay-Guarded.ps1; requires all listed runtime owners absent; must not run -Apply while running.
- Status remains PREPARED / BLOCKED ON GRACEFUL SHUTDOWN. Do not silently force exit despite user saying 'not using'; request explicit opt-in to official Launcher Exit All's forced fallback (including potential NvContainer and other owned workers) or use planned normal Windows shutdown/reboot when user controls their machine. Do not claim Alt+Z newly fixed until actual new runtime installed and proven.
