# CODEX HANDOFF — ShadowPlay NEXT / CEF 73 → Overlay OPEN/CLOSE End-to-End
Date: 2026-10-26 (Asia/Bangkok)
Priority: P0 / RELEASE BLOCKING
Owner: CODEX (single coordinator for this handoff)
Status: TASK READY, NOT IMPLEMENTED/NOT ACCEPTED
Source: C:\My Project\NVIDIA-Shadowplay
Build/product root: C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay

## 0. MISSION / STOP CONDITION

Work autonomously from investigation through source fix, Build → Test → isolated stage → guarded Deploy → coordinated Restart → real-live verification, UNTIL ShadowPlay NEXT Overlay can **OPEN and CLOSE normally**, not merely compile. User says Codex is ready and wants the COMPLETE remaining work. Do not mark COMPLETE just because CEF libcef.dll loads or a benchmark passes.

User-visible definition of COMPLETE:
- After a clean product start (Launcher and support chain ready), ONE Alt+Z opens the actual original NVIDIA-style OSC overlay; ONE Alt+Z closes it.
- Repeat Alt+Z 20 times (10 full open/close cycles or more) without missing toggles, duplicate actions, route loops, browser hangs or process crashes. Test both slow and rapid input, with debouncing.
- Closing releases keyboard/mouse focus, restores prior foreground and click-through; user can type/click normally. Reopening restores interactive page including Settings and main menu.
- Run separate Desktop test and controlled fullscreen/Hook test where a safe known game is available; if no suitable game runtime exists, do not claim Hook PASS. Hidden/notifier frames must not linger in game.
- OSC UI/native visibility, NodeAPI page state and hotkey registration do NOT contradict; UI page route #/base must not bounce open/closed.
- No duplicate NVIDIA OSC instances, leftover CEF subprocesses, stale shared textures/CPU buffer frames, blank overlay, upside-down frame or OPEN↔CLOSED flicker.
- User's NVIDIA original UI style, md-*, l10n, Freestyle V5, ReShade modules and custom configurations remain intact.
- Backups and rollback tested. Collect evidence for each gate; only then report final PASS.

If a blocker requires unavailable hardware, game, permission or user interaction, make maximal isolated progress, report **BLOCKED** with evidence and precise next step. Do NOT claim a live test that was never run.

## 1. NON-NEGOTIABLE READING / OWNERSHIP

Read these before editing:
1. C:\My Project\NVIDIA-Shadowplay\docs\BUILD-DEPLOY-POLICY.md
2. C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc\mods\อ่านก่อนทำ UI.md — MANDATORY before touching OSC HTML/CSS/JS.
3. D:\cef73-build\evidence\CEF73-INTEGRATION-PROGRESS-20261026.md (latest append ~01:59)
4. D:\cef73-build\evidence\CEF73-OSR-DUAL-RUNTIME-ACCEPTANCE.md
5. D:\cef73-build\evidence\CEF73-240FPS-DEADLINE-FINDING.md
6. C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Container\NVIDIA ShadowPlay Helper\README.md
7. C:\My Project\ShadowPlay-NEXT-AgentBus\README.md for task ownership; this bus is LOCAL to AORUS-DESKTOP and is NOT synced with MateBook.
8. Existing source tests, manifests and project build scripts before running them.

Check git status, current AgentBus .task/.working ownership and active Codex/Agent file locks; don't collide with Agent A/B/C. This document grants Codex coordination responsibility, not permission to overwrite sources held by other Agents. Don't create ad-hoc duplicate consumers or an alternate Overlay.

Make a numbered checklist of all gates and keep progress appended to a NEW report, not by overwriting historical evidence. **Never put pairing secrets, tokens, or user settings values in reports.** Do not push/commit unless separately authorized.

## 2. VERIFIED CEF GROUND TRUTH — DO NOT GUESS

Original production CEF runtime directory:
C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA OSC

Production legacy libcef.dll: original installed DLL; observed size 109914112 bytes; old CEF version 73.1.12.1933.
Patched CEF 73 original candidate (PREFER INITIAL INTEGRATION):
D:\cef73-build\candidate\frame-probe-runtime
  libcef.dll SHA256 FC21F54CE6C628CCE0A56B652C6FC62E916AC63EF4E1B19D2ADA0DEE4F927C9D
  observed 240 target at ~231–239 GPU-unique frames/sec on good controlled runs; BUT on other runs ~80–107, so pacing is not yet stable. This is **not** live OSC/Hook FPS.
Deadline-clamp experimental candidate (DO NOT CHOOSE BY DEFAULT):
D:\cef73-build\candidate\deadline-fix-attempt1-runtime
  libcef.dll SHA256 876E0C2626A280634992F885062CFC62C817661420274EC942920DCD38D93A9A
  fixed source arithmetic producing a past BeginFrame deadline at 240Hz, but controlled A/B was WORSE: baseline 240 228.44 and boost236.87, experimental 240 224.95 and boost226.23. Do not ship the experimental version simply because formula seems cleaner.
Source/build/patch evidence and rollback:
D:\cef73-build\evidence\deadline-fix-attempt1
D:\cef73-build\chromium\src\out\Release_GN_x64
D:\cef73-build\candidate\shadowplay-compat-lab1 (STATIC staging; DO_NOT_LAUNCH_UNTIL_ISOLATED.md)
CEF candidate original and legacy both Chromium 73.0.3683.75, same CEF commit and matching Windows/universal API hashes. 44 named CEF imports used by NVIDIA OSC.exe and 126 by Launcher.dll ALL present in original candidate; Launcher.dll LoadLibrary smoke succeeded from isolated candidate. CEF VERSION metadata is different: not a blanket drop-in runtime acceptance.
Launcher.exe bootstrap sets the DLL directory to Overlay On-Screen Control\NVIDIA OSC and loads NVIDIA Launcher\Cef\Launcher.dll. **Keep matched libcef.dll, chrome_elf.dll, snapshot/ICU resources, locales and matching CEF subprocess runtime together. Never swap only libcef.dll with leftover old resources.**
GetWindowlessFrameRate() on this old API may return 60 even when accelerated GPU-unique frames exceed 60; **don't gate GPU FPS on this getter**.
Uncapped --disable-frame-rate-limit produced ~581.78 GPU-unique FPS (also effectively disables GPU VSync); diagnostic ONLY. Do not deploy unlimited mode as a fix. ExternalBeginFrame experimental fixture gave CPU OnPaint at ~239/sec and ZERO GPU OnAcceleratedPaint; not a valid GPU path. Never treat callbacks as unique pixel frames.
Acceptance for main Overlay toggle prioritizes correctness, no crash/flicker/focus loss. For 240 FPS exploratory validation accept average 216–264 as a working engineering tolerance, but annotate long stalls, repeatability and 1% lows. Cannot use an average to excuse persistent 80–100 FPS. Real DX9 Hook 60 FPS stability is a separate gate.

## 3. RELEVANT SOURCE FILES / REAL TOGGLE PATH

- Native OSC: Project\Overlay On-Screen Control\NVIDIA OSC\src\osc_main.cpp
- CEF OSC project: Project\Overlay On-Screen Control\NVIDIA OSC\NVIDIA OSC.vcxproj
- OSC static frontend and mods: Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc\
- NodeAPI server: Project\Overlay On-Screen Control\NVIDIA NodeAPI\index.js and its shims/routes
- Global hotkey Helper: Project\NVIDIA Container\NVIDIA ShadowPlay Helper\NvHotkeyHelper.cs
- Helper project: Project\NVIDIA Container\NVIDIA ShadowPlay Helper\nvsphelper.csproj
- Container: Project\NVIDIA Container\NvContainer.cs and adjacent API/health infrastructure
- Launcher bootstrap: Project\Launcher.Cef\src\bootstrap.cpp
- Launcher consumer/lifecycle: Project\Launcher.Cef\src\launcher_main.cpp, launcher_supervisor.cpp, launcher_client.cpp, launcher_util.cpp
- OSC Adapter/Host and Duluka Hook: Project\NVIDIA Adapter\OSC Adapter\ (inspect own source before change)
- Native test: Project\Overlay On-Screen Control\NVIDIA OSC\tests\CefFrameDeliveryTest.cpp and Analyze-CefFrameDelivery.ps1

Verified hotkey route:
NvHotkeyHelper Defaults['openshare'] = VK_ALT(18)+VK_Z(90). Registers global hotkey, sends route to http://127.0.0.1:59012/v1/hotkey/{name} (inspect current runtime config). Helper's editor monitor URL is http://127.0.0.1:59011/ShadowPlay/v.1.0/Hotkey/Monitor.
Other historical OSC local ports: page/server :3000, toggle :59003, NodeAPI :59011; confirm exact current config/routes before invoking; NEVER blindly expose LAN ports.
OSR native WM_OSC_VIS in osc_main.cpp: wParam 0=CLOSE,1=OPEN,2=TOGGLE; g_ui_open owned by Native HWND/UI thread; g_ui_open_snapshot for other threads. lParam nonzero indicates external request, and QueueOscPageAction calls the actual Angular oscDisplayService.openOSC/closeOSC on CEF TID_UI. It queues pending action before browser exists.
The page #/base route drives WM_OSC_ROUTE; stale route/open flags can cause #/base ↔ main-menu ping-pong, input click-through flicker and OPEN↔CLOSED. Native OSC already attempts route reconciliation; test/repair that path instead of creating another toggle owner.
Native OSC focus mode and Hook path via WM_OSC_FOCUS_MODE / g_game_focused, WasHidden(false), Invalidate(PET_VIEW), GPU frame metadata/adapter frame bursts.
Native OSC's ModeFrameBoostPaintTask uses CEF UI task pacing, an OSR/Host publisher, and 5sec boost. Ensure no duplicates or continuous repaint while invisible.
Native OSC uses Local\\NVIDIA.ShadowPlay.OSC singleton mutex, native hooks and absolute logs/config, while Launcher can manage production processes. DO NOT directly launch the staged Lab executables until the collision risks are isolated.

## 4. EXECUTION PLAN — DO IN ORDER; NO REFLEXIVE REWRITES

### G0 — Runtime forensic baseline and ownership (NO WRITES)
1. Read authoritative config/health/status, process list, ports, startup chain, NodeAPI logs, helper hotkey logs, launcher logs, OSC crash/event logs; redact secrets.
2. Record which processes truly run now; machine observed NvContainer product PID34584 on 2026-10-26 ~02:10, while matching OSC/Launcher process was NOT seen in targeted process list. Verify fresh; never assume currently live.
3. Record hashes and timestamps for production Launcher.exe, Launcher.dll, OSC.exe, libcef.dll, ICU/V8 data, locales, JS assets, NodeAPI server/helper, and config. Copy full matched build/runtime to rollback staging (not only DLL).
4. Reproduce actual user symptom with minimal safe route first: explicit toggle API and physical Alt+Z (where input allowed). Distinguish helper never sending event, Container 4xx/5xx, NodeAPI route error, JS injector unavailable, Native OPEN but no GPU frame, input/focus click-through, CEF browser not initialized and second instance.
5. Evidence: baseline-processes.json, baseline-hashes.json, baseline-endpoints.json, chronological toggle IDs/correlation and screenshots/recordings only as locally authorized. Label UNKNOWN rather than guessing.

### G1 — CEF Runtime exact-match isolated consumer test
1. Inspect how OSC/Launcher locate libcef.dll, chrome_elf.dll, resources/locales/subprocess. Compare legacy/candidate architectures and ABI; existing static check PASS is not sufficient.
2. Produce a NEW isolated copy of complete product layout with original UI and paired candidate runtime. Preserve all original resources, backup staging and revisioned candidate manifest.
3. First use safe loader and standalone OSC CEF browser mock/smoke to prove CefInitialize, browser creation, rendering to GPU Shared Texture, navigation and clean CefShutdown. Do not start Supervisor or hook global keyboard/mouse in Lab.
4. Handle unsafe prod singleton/mutex/IPC/shared-memory IDs, absolute config/log/cache path and child-launch behavior before standalone consumer launch. Any missing isolation means BLOCKED for Lab actual launch; use mocks instead.
5. If loader/version assertion/API hash fails, build consumer OSC/Launcher wrapper against matching CEF73 SDK. Preserve original UI and Launcher bootstrap; no unnecessary Chromium rebuild.
6. Prefer FC21 candidate initially. Deadline-clamp 876E remains comparison experimental, NOT approved.
7. Evidence: candidate version/hash/resource manifest, safe CEF host startup and shutdown logs, exceptions, screenshots.

### G2 — Helper + Container + NodeAPI Alt+Z contract
1. Trace OpenShare from RegisterHotKey (id, modifiers, hotkey conflict/retry), dispatch queue, Container HTTP 59012 response, NodeAPI hotkey and oscDisplayService, Native OSC WM_OSC_VIS. Create a correlation ID per request; do not log secret values.
2. Prevent duplicate opens from auto-repeat, queued stale toggles, recursive route->native->route loops and delayed requests crossing state generations. Serialize toggles through ONE owner.
3. Differentiate SET_OPEN/SET_CLOSED (idempotent) from TOGGLE intent. Guarantee sequential rapid keypresses use latest authoritative state.
4. Hotkey shortcut editor: suspend global hotkeys only while editing/capturing; resume after leaving editor, even after error/route reload. Do not fire Alt+Z while assigning keys; outside editor Alt+Z must work. This is a known regression.
5. Verify Helper watcher/re-registration, retry after conflict, config changes, backend restarts and no phantom Alt+Z on key release.
6. Include tests mocking HTTP delays, repeated Alt+Z, helper unavailable / node unavailable, state race, editor enter/exit.

### G3 — Native OSC / Angular route and visibility convergence
1. Inspect WM_OSC_VIS, WM_OSC_ROUTE, g_ui_open, g_ui_open_snapshot, g_pending_page_action, QueueOscPageAction, OSC page oscDisplayService.openOSC/closeOSC and #/base route. Draw a single state model: CLOSED, OPENING, OPEN, CLOSING, RECOVERING (names can differ).
2. Guarantee pre-browser toggles queue desired state and flush ONCE after browser+Angular injector are ready. No stale previous generation callback may reverse current user intent.
3. OPEN: awake CEF browser; WasHidden(false)/Invalidate(PET_VIEW), genuine native menu route; show overlay according Desktop/Hook, update hit-test/region/focus; publish new frame only when real pixels changed. CLOSED: page goes #/base, native state CLOSED, restore focus, stop active interactions, publish hidden metadata or clear retained host frame, preserve notifier contract.
4. Eliminate route ping-pong, ghost reopen after close, delayed JS reopen, blank first paint, duplicated hwnd and stale g_ui_open. Preserve NVIDIA transitions/animations and frontend state across close/reopen.
5. Never call CEF APIs on wrong thread; use CefPostTask(TID_UI). Handle teardown while callbacks pending, no UAF; one browser/process lifecycle per active runtime.

### G4 — Desktop windowing/input
1. Validate Alt+Z OPEN: window shown topmost on selected display, correct size/monitor, original NVIDIA UI visible and clickable, keyboard navigates, dropdowns and md-* controls work. Don't introduce page overflow/forced-size CSS or rewrite Freestyle V5.
2. Validate CLOSE: returns input to previous foreground app, keyboard modifiers released, no stuck capture/no phantom click-through overlay, no half-transparent window or mouse capture; ensure the notifier can be visible without opening main menu.
3. Check Settings → key editor → return → Alt+Z, main-menu → settings → back, select/dropdown dialogs, minimized/hidden state, 100% zoom and multi-monitor, repeated 20 toggles.
4. For UI changes read 'อ่านก่อนทำ UI.md' and preserve original NVIDIA AngularJS Material 1.1.3 / md-* components; use English default + existing Thai localization, no new design or CSS global overrides.

### G5 — Hook/Host/Adapter visibility on controlled games
1. Only after Desktop OPEN/CLOSE PASS, verify Hook Mode/DX9 controlled allowlisted game where installed and authorized (Floating Point or 100 hidden frogs are historical test candidates; confirm current installed exe/API before running). Do not force-close unrelated user games.
2. Validate Host attach/API routing and CPU/GPU mode selection per actual supported graphics API. Respect x86/x64, DirectX9 vs DX9Ex, D3D11 transport contracts. Do not impose CPU fallback when GPU mode selected without clear diagnostic/report.
3. OPEN: CEF GPU OnAcceleratedPaint → host producer → shared texture/frame metadata → hook compositor. Fresh visible frame arrives; text/icons correctly oriented, no black/old frames; clicks and keys route safely. CLOSE: immediate hidden metadata clear/ACK and retained frame eviction; no ghost overlay on screen, game focus restored. Invisible -> zero active publish; unchanged pixels -> frameNumber unchanged; advance only after copy success.
4. Verify OPEN↔CLOSED switch with game focus changed, alt-tab, windowed/fullscreen where possible; don't restart game for every UI change; no uncontrolled inject/overrides into non-allowlisted process.
5. Do not claim 240 FPS in real DX9 Hook from CEF isolated benchmark. Aim stable 60 FPS Hook and correct visibility first. Collect frame timing real host/publish/copy/ACK without frame serial fabrication.

### G6 — Build / Test / Deploy / Restart and ROLLBACK
1. Implement smallest verified SOURCE fixes, keep owners informed, use product's existing Build/Deploy policy. For every actual code revision: Build → targeted regression → isolated staging → verify exact binaries/assets hashes.
2. Before touching product: atomic backup snapshot of full compatible Runtime/Launcher/OSC/NodeAPI/Helper/Config required for rollback; record process IDs and hashes. Never overwrite user's secret config; redact secrets. Do not mix old/new CEF resource DLLs.
3. Deploy only if G1–G4 isolated/desktop smoke gates PASS. Install matched CEF runtime + consumer binaries and assets in a controlled maintenance window; coordinated stop and restart both sides (OSC and NodeAPI/Container/Helper/Launcher as needed, not indiscriminate taskkill).
4. Read actual API/status, process paths and JS version after restart: prove deployed binaries are live, not old processes. Do not just say 'Restarted'. Verify fresh Alt+Z and closed state, then Hook tests.
5. If live smoke fails: automatic revert to original matched runtime + prior executable/assets; restart the prior chain coherently, log rollback completion and data preserved. No blind repeated deploy on failure.
6. No git push/commit unless separately authorized. Deployment and its permission requirements must be made explicit in report. Do not perform destructive cleanup of original.

### G7 — Mandatory tests and final evidence
Automated:
- Hotkey editor suppression/re-enable, no duplicate registration, queued order/stale-drop; 20 repeat toggles with jitter.
- WM_OSC_VIS idempotent and toggle intents, route back-and-forth, delayed browser init, UI injector not-ready, no orphan browser, no race on close.
- Startup/shutdown/resume with CEF candidate and Launcher; UI navigation + input in fake/isolate harness.
- GPU OSR unique frame smoke 75/120/240 and restore; record bad/low frames, NEVER count callbacks as new unique pixels.
- Host hidden metadata/unchanged-frame rule/ACK; x86/x64 if relevant.

Manual live on AORUS-DESKTOP:
- Clean boot CLOSED; Alt+Z opens native overlay within reasonable perceived latency; Alt+Z closes. 20 toggles; no focus loss, no crash, no stuck keys.
- Open→Settings→Back→Close; enter shortcut editor and exit; Alt+Z resumes.
- Desktop multi-monitor, display change, original NVIDIA menu and language switch.
- Hook Mode: open, close, reopen while game focused; game continues rendering, no ghost overlay.
- Restart all controlled components once and repeat OPEN/CLOSE.
- Negative: NodeAPI temporarily absent, CEF unready, Helper hotkey conflict, rapid double presses, foreground game changes; recover gracefully.

Final report required:
A. changed-files summary + exact runtime/version/hash/asset set and owners
B. before/after startup/endpoints and hotkey correlation logs
C. tests: PASS/FAIL/SKIP plus evidence paths and known limitations
D. screenshots (OPEN, CLOSED, Settings) or authorized real-runtime evidence
E. deployment and exact restart process IDs/time, rollback snapshot, rollback execution result
F. final label: ACCEPTED only when real Desktop toggle PASS and controlled Hook PASS where available; otherwise PARTIAL / BLOCKED, no invented proof.

## 5. NOT IN SCOPE / DO NOT BREAK
- Never redesign native NVIDIA OSC UI, replace md-* or Freestyle V5 Group/Block controls.
- Do not 'fix' toggle by killing/restarting OSC on each Alt+Z; process should remain ready.
- Don't enable always-unlimited CEF 581.78 FPS mode in production; don't promise 240fps jitter-free.
- Don't overwrite original CEF DLL or matching resources without validated staging+rollback.
- No rewriting Ansel/ReShade, Capture or GPU policy; only necessary integration regressions.
- Do not blindly disable hotkey editor guard, SHA-256 safety gate, PID/executable identity checks, or production security controls.
- Do not restart another agent's active game, modify unrelated repo, expose API beyond localhost or leak pairing secret.

## 6. CODEX START NOW
1. Read this entire task + mandatory references.
2. Check Agent ownership and runtime state fresh, print a concise G0 result.
3. Work continuously through G1→G7 within this assignment with concrete source edits/tests and safe verified deployment.
4. Append progress and evidence to C:\My Project\NVIDIA-Shadowplay\CODEX-CEF73-OVERLAY-EXECUTION-REPORT-20261026.md, separate from this handoff.
5. When end-to-end open/close is actually working, report reproduction steps and proof to user. If blocked, report exact failed gate and evidence, not an empty success.

### THE ONLY SUCCESS CLAIM THAT COUNTS
“Alt+Z opened and closed the real ShadowPlay NEXT NVIDIA OSC repeatedly; Native state, frontend route, hotkey Helper, Container/NodeAPI, focus and GPU/Host visibility converged; Build/Deploy/Restart proof matches live files; no ghost overlay or crashes; rollback available.”

END OF CODEX HANDOFF.
