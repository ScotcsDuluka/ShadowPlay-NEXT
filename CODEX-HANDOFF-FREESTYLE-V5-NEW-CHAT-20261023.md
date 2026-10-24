# NVIDIA ShadowPlay NEXT — Freestyle V5 / NEW CHAT HANDOFF
**Date:** 2026-10-23 (Asia/Bangkok)  
**Status:** Source work committed; automated tests passed; **NOT signed off as a production release**.  
**Purpose:** Continue development immediately in a fresh ChatGPT/Codex chat after message delivery timeout. DO NOT reset/rebuild or redesign the existing OSC.

## 1. Absolute rules and locations

- Actual repository: `C:\My Project\NVIDIA-Shadowplay`
- **Required active Git branch: `Stable`**. Verified last head: `764809dca8` — `perf(osc): avoid V5 geometry RAF churn while native Host is hidden` (2026-10-23 19:13 Bangkok). HEAD can move if other chats work concurrently: **read it again first**.
- Canonical source: `Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc`
- Tests: `Project\Overlay On-Screen Control\NVIDIA NodeAPI\tests`
- Real runtime to preserve: `build\NVIDIA ShadowPlay` — never treat Build as source; no blanket copy, clean, reset or full rebuild.
- Native NVIDIA visual reference: `C:\Users\ScotcsDuluka\Downloads\NVIDIA ShadowPlay.7z`; read-only audit/extraction: `C:\My Project\_ShadowPlay-Archive-Clean-Audit-20261023`. Keep native NVIDIA `md-*`, ICO, real 12 buttons, animation, Host scaling and launcher language support.
- **Current worktree is shared across other conversations**. Significant dirty work exists in D3D9 Hook, NVIDIA Container, NodeAPI GameWatcher, `osc/index.html`, `osc-renderer.js`, localization, OSC Host `osc_main.cpp`, `Scripts/build-dev.ps1`. Do **NOT** reset/checkout/clean/mix/stage/commit any of these from the Freestyle workstream.
- At handoff, `git diff --cached --name-only` is empty; this has been verified. Do not use `git add .`.
- Handoff files already available:
  - `CODEX-HANDOFF-FREESTYLE-V5.md` — full original V5 spec and historical warnings.
  - `CODEX-HANDOFF-FREESTYLE-V5-CONTINUE-20261023.md` — deep technical continuation and prior CEF diagnosis (written around 17:33).
  - `Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\FREESTYLE-V5-RELEASE-QA-20261023.md` — current Release QA gate and stress/performance test documentation.
- **Read all three files in full before changing code**. This file is the newest delta, not a substitute.

## 2. Current implementation and verification

Freestyle V5 exists in the original OSC; it is **not** a separate overlay clone:
- 6 global Groups at most across LEFT/CENTER/RIGHT; 240×240 base Groups.
- Big/Side/Small Blocks, one Control per Slot, no duplicate Controls; Settings must stay.
- Auto-Fill on/off, empty Group behavior, Group/Control drag, swap, delayed Insert Preview, cross-panel moves and block preset context menu.
- Real NVIDIA native 12 `md-menu` buttons and icon assets; the old V2 layout/input writer yields to V5 when V5 is in control.
- Original floating NVIDIA Customize Settings: Background Settings 80% left + scrollable Controls 20% right.
- Native Material `md-select` / Esc, Done, Cancel, Undo; live draft state, persisted state changes on Done only.
- OSC Motion remains original; true native input forwarding may send **mousemove buttons=0** — **do not reintroduce a `buttons=1` requirement**. Human already confirmed native mouse drag worked after the fix from commit `a5bb83e40f5d5db7ed5dc30a388b7d348835a1a8`.

**Previously completed CEF checks (actual runtime CEF, NOT just unit mocks):**
- Combined Interaction + Lifecycle + Viewport test: **18/18 checks passed**, data/route/Motion restored in that run.
- Includes CEF Group/Control Swap, cross-panel move, delayed Insert Preview, Auto-Fill, Context Menu, no accidental Capture/Record during editing, Done/Cancel/Undo, Material Escape, and 1920×1080 + 1280×800 panel sizing.
- Evidence directory: `build_test\OSC-Live-Preview\v5-session-20261023\build-regression-2026-10-23T11-43-40-667Z-all` (inspect `summary.json` and PNGs).
- Original human input drag confirmation applies to an earlier deployed build, not to the source-only optimization below.
- User's saved Layout may contain fewer than 6 Groups by design; **do not auto-reset to default**. Default six is for a fresh layout, not a mandate to overwrite their customized saved data.

## 3. Latest additional work (2026-10-23 19:13)

Committed to `Stable`: **`764809dca8`**.

Files changed in this commit:
1. `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/nv-next-v5-native.js` — source-only performance fix: suppress repetitive native geometry `requestAnimationFrame` work while OSC Host is hidden or Motion is opening/closing; resume once Motion is idle/open. Motion preflight `prepareForMotion()` remains supported.
2. `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/nvNextV5Native.test.js` — deterministic fake RAF / timer tests for close→open→close transitions and transient edit ownership.
3. `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/FREESTYLE-V5-RELEASE-QA-20261023.md` — appended QA findings.

**Latest complete selected Node regression: 135/135 passed (0 failed).**
Log: `build_test\OSC-Live-Preview\freestyle-v5-motion-idle-gate-20261023.log`.
Selected suites: `nvLayoutV5Core`, `nvLayoutV5Stress`, `nvNextV5Native`, `nvFreestyleEditorLifecycle`, `nvFreestyleV5EndToEnd`, `nvFreestyleSettings`, `nvV5Ownership`, `nvArchiveCustomizeBaseline`, `launcherLanguageSync`, `oscUiLocalization`, `oscMotion`, `oscLivePreview`.

**Important: latest source-only fix NOT deployed to runtime.** Verified SHA-256 mismatch on native adapter:
- Source `nv-next-v5-native.js`: `E0B32ADA4CA8A70E01211D992191DCA1A65E45A5A42C09A1A1F42407C6FC8A97`
- Build `nv-next-v5-native.js`: `7732FD064FE14EAA847F42521362C12A8EA000DB5DA0A234EE602163179A1B99`

This is **expected and intentional**; user was unavailable to confirm visual behavior and other OSC threads are concurrently editing. **Do not claim Project and Build match or that the 19:13 optimization passed live CEF**. It has passed offline tests only.

Earlier real Build-backed-up Freestyle editor patch commit `964ff1be11`, backup session `20261023-114047-10598bdf`. See `build_test\OSC-Live-Preview\sessions` manifests to find exact effective runtime sessions. Do not assume commit HEAD means every frontend file was deployed.

Separate model stress regression:
- `tests\nvLayoutV5Stress.test.js`; prior commit `85ee3118b7`.
- 20 deterministic seeds × 550 steps (11,000 attempted operations) and 100 edit sessions; Settings/duplicate/layout invariants verified. A separate exploratory 22,000-action script also passed.
- Stress tests run **offline**; they do not touch saved localStorage or the running OSC.

## 4. Current known risks / what to do next

1. **First read-only audit:** Check Stable branch/HEAD/dirty/staged state and all handoffs. Do not overwrite concurrently modified `index.html`, `osc-renderer.js`, l10n, Host, D3D9 Hook or Container.
2. **Validate newest RAF optimization in native CEF**, explicitly closed→opening→idle/open→closing cycle; ensure 12 true native buttons appear, preflight doesn't jump positions, no repeated RAF churn. Do not fake Motion target or force a production restart. A source-preview test may be preferable before any Runtime Sync.
3. **Only if CEF evidence is good**, deliberately deploy **only the clean committed native adapter JS** through `osc-live-preview.cjs --plan --files mods/nv-next-v5-native.js`, `--apply --files mods/nv-next-v5-native.js`, then verify SHA. Tool makes backed-up sessions/rollback manifests. Avoid whole-tree apply: other OSC frontend files are dirty.
4. Confirm real NVIDIA appearance and interactions at 1920×1200 with OS scaling (earlier DPR 0.875), 1920×1080, 1280×800. Existing screenshots can be found in `build_test\OSC-Live-Preview\v5-session-20261023` and various `build-regression-*` directories. Check `summary.json`, not just filenames.
5. Check the **physical mouse on the actual screen** for drag/insert/Record suppression and approve visuals **only when user is available**; do not interrupt them or claim a human approval that did not occur for the latest build.
6. Keep the localStorage Layout untouched. Back up fresh real CEF state before synthetic tests. Earlier snapshots are not automatically current; concurrent user/app changes may have happened.
7. Monitor stale Motion target vs CEF route, Legacy V2 owner/flag, input/keyboard close routing and 12-menu presence. Fix only evidenced bugs; always run tests again.
8. Update handoff/report with actual CEF proof, selected commit SHA, Runtime session ID and remaining limitations. **Release is not yet approved**.

**CEF endpoints:** NodeAPI HTTP `127.0.0.1:59011`, OSC Host native control `127.0.0.1:59013`, CEF DevTools `127.0.0.1:59099`. Services/processes may restart; rediscover target each time.

**Useful test scripts:** `build_test\OSC-Live-Preview\v5-session-20261023\cef-harness.cjs`, `build-regression.cjs`. The combined runner can take `--phase all --host-mask-drag --keep-page --inspect-select`. Read its current code first; it may make a native Host show call, change route/viewport, then restore. Only run while no one else is interacting with OSC. CDP input must scale CSS coordinates exactly once.

**Safety:** No `git add .`, no `git reset --hard`, no `git clean`, no branch/worktree manipulation, no full Build replacement; no accidental screenshot/record in Edit; no capture engine modifications, no removing dirty files. The .7z reference and Build binaries must remain intact.

## 5. Copyable prompt for the new ChatGPT/Codex chat

```text
รับช่วง NVIDIA ShadowPlay NEXT — Freestyle Main-menu V5 ต่อบนเครื่องจริง

Repo: C:\My Project\NVIDIA-Shadowplay
Branch: Stable (ตรวจ HEAD สดก่อนทำ)
Source: Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc

อ่านเอกสารทั้งหมดก่อน:
1. CODEX-HANDOFF-FREESTYLE-V5-NEW-CHAT-20261023.md
2. CODEX-HANDOFF-FREESTYLE-V5-CONTINUE-20261023.md
3. CODEX-HANDOFF-FREESTYLE-V5.md
4. Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\FREESTYLE-V5-RELEASE-QA-20261023.md

สถานะล่าสุด: Stable commit 764809dca8, selected Node tests 135/135 ผ่าน; ก่อนหน้านั้น CEF integration 18/18 ผ่าน แต่ commit 764809dca8 ที่แก้ native RAF ตอน OSC ปิดยังไม่ Deploy และยังไม่ได้พิสูจน์บน CEF จริง
อย่าสับสน Source กับ Build, อย่าใช้ Build เป็นต้นฉบับ
ให้ตรวจ Native Motion/RAF closed→open ใหม่โดยใช้ CEF จริง จาก Source อย่างปลอดภัยก่อนพิจารณา Deploy; อย่าเปลี่ยน Layout/Flag ที่ผู้ใช้เซฟไว้หรือทับงาน Dirty ของอีกแชท
ต้องรักษา NVIDIA .7z look, native md-menu controls ทั้ง 12, drag mousemove buttons=0, Single Layout Owner, Auto-Fill, Context Menu, Settings 80/20 และ Done/Cancel/Undo
ห้าม Reset, Clean, git add ., Build ทับทั้งหมด, แก้ Hook/Capture/Container/OSC Renderer/index/l10n ของอีกงาน
ทดสอบ, แสดงหลักฐานจาก CEF จริง, Commit เฉพาะไฟล์ V5 ถ้าแก้, แล้ว Sync แบบ Backup + SHA เฉพาะไฟล์ที่ผ่าน
ยังไม่ประกาศ Release จนกว่าจะทดสอบเมาส์จริงและผู้ใช้อนุมัติ UI
เริ่มอ่านสถานะและลงมือทำต่อจากจุดเดิมเลย ไม่เริ่มออกแบบใหม่
```
