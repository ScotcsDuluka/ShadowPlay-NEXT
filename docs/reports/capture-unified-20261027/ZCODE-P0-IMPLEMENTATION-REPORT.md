# ShadowPlay NEXT / NVIDIA Capture — ZCODE P0 Implementation & Verification Report

Date: 2026-10-27 (Asia/Bangkok). Host: AORUS-DESKTOP (NVIDIA/NVENC). Branch: `Stable` at `e63b52544a` (unchanged).
Scope: P0-A Control Plane audit, P0-B Runtime Integration, P0-C Recording Session gates, P0-D Unified Build + synthetic tests, Deploy policy.
**No commit, push, reset, clean, production deploy, process kill, restart, screen/audio recording, or config write was performed.**
Companion evidence file: `ZCODE-P0-RUNTIME-EVIDENCE-20261027.json` (same directory).

## 1. Files Changed

**None in tracked source.** The working tree stayed byte-identical to its pre-task state (all 146+ pre-existing modified/untracked files from concurrent Codex/GPT-6 work preserved untouched; verified via `git status` before/after). Generated artifacts only:

| Artifact | Path | Purpose |
| --- | --- | --- |
| Unified Release staging | `build_test\NVIDIA Capture Unified_Test\NVIDIA Capture` + `capture-build-manifest.json` | Isolated staging of current source (default `-Dest` of `Build-CaptureUnified.ps1`; does not touch deployed runtime) |
| This report + evidence | `docs/reports/capture-unified-20261027/ZCODE-P0-*.md/.json` | Required P0 report |
| Test logs | `%TEMP%` (`routeguard.log`, `legacy.log`, `mux.log`, `sessrec.log`, `catalog.log`, `container-*.log`, `node-*.log`) | Ephemeral; not in repo |

## 2. Problems Found

| # | Finding | Severity | Layer |
| --- | --- | --- | --- |
| F1 | **Deployed NvCapture.dll (`DE04FDBB…`) ≠ current-source build (`5B687DEA…`); deployed `/engines` lacks `runtimeTruth`, `actualRunningEngine`, `requestedConfiguration`, `savedConfiguration`, `verification`, `availableCapabilities` fields that current source emits** | P0 | Runtime |
| F2 | **Deployed NodeAPI `/DulukaCapture/v.1.0/status` returns only `{enabled,ready,recording}`; current shim adds `stateKnown`, `workerReady`, `actualSessionBackend`, `actualRunningEngine`, `lifecycleState`, `busy`, `isCapturing`, `runtimeTruth`** | P0 | NodeAPI |
| F3 | **Deployed `nvcontainer.json` declares `captureRuntime` twice** (block 1 carries `allowRecordCommands/displayCommands/screenshotCommands=false`; trailing block has only `enabled+apiPort`). System.Text.Json is last-wins, so the first block's flags are dead text. Source template `Project/NVIDIA Config/nvcontainer.json` is clean (single block) — the duplication exists only in the deployed copy. | P0 (config trap) | Deployed config |
| F4 | `captureRuntime.allowRecordCommands=false` (deployed + config default) → Container gateway refuses all record commands (403 `record_gateway_disabled`); Control Plane is read-only end-to-end right now | By design (default-deny); requires explicit operator decision after F1–F3 resolution | Container |
| F5 | Capture policy `capture-engines.json` (`selectionLocked`, `activeCaptureId: duluka:dxgi`) diverges from worker truth (`Config/config.json` → `EngineMode=FFmpeg/ddagrab/NVENC_H264`); the policy file self-describes as "does not select or start a recording engine" and the worker `/engines` response labels selection data `readOnly=true`, so no code path conflates them — but any UI that renders the policy IDs as "current engine" would be lying | Contract risk (UI discipline) | Policy vs truth |
| F6 | `Scripts/deploy-capture-unified.ps1` refuses deployment unless manifest has `nativeQsvPackaged=true`; current working-tree build reports `native_qsv_packaged=false` (QSV native package not present in this working tree's `bin\package`) | Deployment gate | Deploy |
| F7 | `QsvNative.Tests` cannot run on AORUS-DESKTOP: `DxgiDisplayInventory.Collect().Outputs.Single(t => … Vendor==Intel)` throws "Sequence contains no matching element" (no Intel GPU). Not a regression; 666/666 evidence belongs to MateBook. | Expected / N/A | Intel QSV |
| F8 | `OntologyIdentity.Tests` cannot run: requires `public/catalog.json` from the separate ontology repo; absent in this tree. | Skipped (input missing) | Container |
| F9 | `NvContainer` watch-loop logs `[config] OSC toggle port unavailable` style warnings when optional config sections are missing — observed in code paths, not as a failure here. | Informational | Container |

## 3. Root Causes

- **F1/F2:** The production worker, Container and NodeAPI on AORUS were last deployed 2026-10-26; the DPAPI write-header contract, `CaptureRuntimeTruth`, `requestedConfiguration` echo and runtime-truth fields were added in the current uncommitted working tree afterward (GPT-6 audit already flagged "latest fixes not deployed"). This is the normal drift state the audit's P0-B/P0-C gate was written for — not a code defect.
- **F3:** The deployed copy was hand-edited (an extra `captureRuntime` block appended at the end) instead of editing the existing block. Last-wins parsing silently demoted the first block's flags to documentation.
- **F4:** Intentional fail-closed posture: no record command is possible until an operator flips `allowRecordCommands` in config explicitly. This prevented any accidental live capture during P0.
- **F6:** `Build-NativeQsv.ps1` output package is not present in this working tree (`Encoding/Intel/Native/bin/package` lacks `qsv-native-package.json`); the canonical staging snapshot from the GPT-6 resume run contains it, but the unified build run here did not rebuild the native package.

## 4. Implementation Details (audit results, per control-plane layer)

### P0-A Control Plane — verified against source

- **NvCapture worker `:59077`** (`CaptureRestApiServer.vb`): loopback-only TcpListener; `CaptureRestAuthorization` enforces Host-pinning (`127.0.0.1:59077`/`localhost:59077`/`[::1]`), strict Origin echo, GET read-only, non-POST→405, **fail-closed 503 when the DPAPI vault is unreadable**, 32-hex `x-shadowplay-capture-token` compared with `CryptographicOperations.FixedTimeEquals` and zeroized. HTTP reader bounded at 64 KiB, rejects chunked/duplicate/negative bodies.
- **Engine-state semantics (the "ห้ามนำ Policy มาแสดงว่าเป็น Engine ที่กำลังอัดจริง" requirement):** `CaptureRuntimeTruth.Observe` separates `lifecycleOwner`, `actualSessionBackend`, `actualRunningEngine`, `lifecycleState`, frame counters; `actualRunningEngine` is only set when `isCapturing` is true; multi-owner yields `ConflictingOwners`; InstalledNvidia yields `ExternalStatusUnverified` and never a backend claim. `/engines` exposes `configured` vs `requestedConfiguration` (snapshot at last REST start) vs `savedConfiguration` vs `actualSessionBackend` vs `verification {hardwareVerified=false, mediaVerified=false, scope=current_session_unverified}` — hardware/media verification is never asserted by a GET.
- **Route policy** (`CaptureBackendRoute.Decide`): pure function; Duluka = `ddagrab`/`dxgi_desktop_duplication` + `NVENC_H264` only; FFmpeg whitelist enforced; `obs_capture_not_implemented`; `nvidia` → distinct `InstalledNvidia` kind; unknown → rejected.
- **Hardware verification** (`FFmpegNvencVerification`): synthetic-only, explicit `POST /encoders/verify`, rate-limited 60 s, `nvidia-smi` bounded 6 s, per-GPU×encoder one-frame null-mux bounded 10 s; `GET /engines` is a pure snapshot reader (never triggers GPU work).
- **Container `:59012`** (`ContainerTestApi.cs` + `CaptureWorkerGateway.cs`): loopback-only + `ControlRequestSecurity.Validate`; reads relayed GET `/health|state|engines|displays` with schema validation (128 KiB cap, per-endpoint timeouts 3 s); record commands default-deny (`allowRecordCommands` config + `{"confirmed":true}` only), start timeout 19 s / stop 35 s, `start/stopOutcomeUnresolved` sticky flags, `RelayErrorStatus` maps `endpoint_not_found→501`, `timeout→504`, else 503. `Contract()` explicitly reports `workerCommandVerified=false` (read-only negotiation never certifies writes).
- **NodeAPI `:59011`** (`index.js` + shims): `/DulukaCapture/v.1.0/status` → Container `/v1/capture/status` (2 s timeout, 128 KiB cap, strict schema); `/Backend/v.1.0/ContainerEvents` loopback-only with event whitelists (GPU runtime state, GameWatcher status, OBS notifications, hotkey) and 32-event cap; `localApiSecurity.httpMiddleware` covers static+API; hotkey lane and `/cefquery` remain same-origin; no capture write path exists in NodeAPI (writes belong to Container → worker only).
- **DPAPI trust set** (`PairingSecretStore` + `CaptureRestAuthorization`): both resolve `%LOCALAPPDATA%\ShadowPlay NEXT\Secrets\pairing-current-user.dpapi`; Container's gateway `workerWriteToken` reads the same store; `Resolve()` prefers DPAPI and never falls back to legacy JSON while the vault exists; enrollment is explicit-only, rotation offline-only, no secret bytes ever logged.

### P0-B Runtime Integration

- Verified running processes: `NvCapture` PID 30016 (started 2026-10-26 17:12), `NvContainer` PID 26152, `NvNode` PID 36336 — all loopback, all `recording=false` throughout the task.
- Worker deployed truth: `Config/config.json` → `EngineMode=FFmpeg, APICapture=ddagrab, Encoder=NVENC_H264` — matches `/engines` `.configured` (policy-vs-worker divergence F5 is therefore *visible*, not hidden).
- REST + event wiring between the three layers is **live**: `:59011/status` ← `:59012/v1/capture/status` ← `:59077/state|engines` all returned 200 during evidence capture.
- The gateway accepts the legacy worker `/state` signature only when its complete legacy shape is present and **never infers a recording backend from `enabled`/`failReason`** (`ValidRead` in `CaptureWorkerGateway.cs:144-159`).

### P0-C Recording Session gates (source audit)

- `RecordingEngine.vb`: Initialize-once, `RequireMatchingAdapterLuid` (refuses cross-GPU texture handoff with LUID evidence in the error), per-session re-validation (`RequireIdentity` + `ValidateTarget` before every session), disk-headroom guard before any resource spawn, requested-vs-actual echo (`RequestedCaptureMethod`, pixel format truth lines, GAP logging — no silent substitution), `StartSourceSession` plan-mismatch rejection.
- `SessionMediaVerifier.vb`: `Saved` requires full decode (`CaptureMediaInspector.InspectWithVideoDecode`), exact stream counts, exact frame/packet counts, PTS/DTS/duration timeline consistency, optional colorimetry; timeouts produce `VerificationPending` (truthful pending, never optimistic success).
- REST `/record/stop` (`CaptureRestApiServer.vb`): `saved = ownerPassed && mediaVerified && audioPolicyVerified`; full decode bounded to ≤30 s files; `finalizationState` ∈ Saved/Partial/Failed/Cancelled with explicit `verificationState` and `expectedAudioStreams` policy checks.

## 5. Build Results

Command: `powershell -File Scripts\build-dev.ps1 -Module Capture` (PowerShell 7 compatible; `Build-CaptureUnified.ps1` internally).

- **`CAPTURE_UNIFIED_BUILD_PASS`** — exit 0, **0 errors, 2 warnings** (both `SYSLIB0014` WebRequest deprecation at `ShadowPlayRest.vb:83` and `CaptureRestApiServer.vb:650`; pre-existing, tracked by audit).
- Manifest: `sourceSha256 = 7AD1108BB5180E3F43EFA29A3DC96B809AD49F6E8C1B1DFAFF975997AD8D7F23` — **byte-identical to the canonical two-host digest from the GPT-6 resume run** → this staging reproduces the canonical source exactly.
- `nativeQsvPackaged=false` (F6), `hardwareVerified=false`, `mediaVerified=false`, `deployed=false` — truthful per contract; 41 staged files verified with per-file SHA-256 inside the manifest.
- Staged output is isolated at `build_test\NVIDIA Capture Unified_Test\` — the deployed production tree was not touched.

## 6. Test Results (all synthetic; no desktop pixels, microphone, system audio, or live services)

| Suite | Result | Evidence |
| --- | --- | --- |
| RouteGuard + ControlPlane (NvCapture) | **EXIT 0 — 72 checks PASS** (34 control-plane authorization/runtime-truth + 36 route/HTTP/sandbox + 2 local-FFmpeg advertise checks) | `/tmp/routeguard.log` run with deployed `ffmpeg.exe` path |
| LegacyCompletion.Tests | **EXIT 0 — 51/51** ("zero/nonzero/fatal/hang" child outcomes; fixture from lavfi color source) | staged NvCapture.dll + deployed ffmpeg |
| MediaEvidence.Tests | **EXIT 0 — 18/18** | packet-scan/decode/timeout-refusal gates |
| Recovery.Tests | **EXIT 0 — 15/15** (`RECOVERY_NO_HARDWARE_PASS`) | fault/recovery matrix, no devices |
| Mux.Tests (real engine) | **EXIT 0 — 25/25** ("25 REAL engine mux regression checks") | staged NvCapture.dll + ffmpeg/ffprobe |
| SessionRecovery.Tests | **EXIT 0 — 29 checks / 5 sessions** (`SESSION_RECOVERY_SYNTHETIC_PASS`, lavfi video-only) | synthetic MP4 payloads verified |
| CaptureWorkerGateway.Tests | **EXIT 0 — 59/59** (stub HTTP only) | outcome-unresolved, degraded/starting phases |
| CaptureDisplayRelay.Tests | **EXIT 0 — 26/26** (mock; "no real screenshots or display changes") | display select/screenshot gateway |
| CaptureEngineCatalog.Tests | **EXIT 0 — 24/24** (deployed `capture-engines.json`) | policy schema + capability honesty |
| ControlRequestSecurity.Tests | **EXIT 0 — 13/13** | loopback/header security |
| PairingSecretSecurity.Tests | **EXIT 0 — 14/14** ("isolated pairing security tests") | DPAPI store behavior |
| Dx9AutoHookRouting.Tests | **EXIT 0 — 30/30** ("no process attached") | DX9 override routing |
| HotkeyContract.Tests | **EXIT 0 — 59/59** ("no live services or Win32 registrations") | hotkey contract |
| NodeAPI JS tests (captureRuntimeStatus, captureEngineStatus, captureCapabilities, captureDisplayGateway, capturePipelinePlanUi, videoCaptureOptionsUi, oscMotion, containerHotkeyEvent) | **8× EXIT 0** | shim/UI normalization contracts |
| QsvNative.Tests | **N/A on this host** — F7 (no Intel GPU); MateBook 666/666 evidence from audit remains the Intel gate | `/tmp/qsv.log` |
| OntologyIdentity.Tests | **SKIPPED (input missing)** — F8 | requires external `public/catalog.json` |

**Total executed and passed: 210 engine-side checks + 201 container-side checks + 8 NodeAPI suites.**

## 7. Runtime Evidence

- Live read-only probes during the task (full snapshot in `ZCODE-P0-RUNTIME-EVIDENCE-20261027.json`):
  - `:59077/health` → `{ok:true, ready:true, recording:false}` (start and end of task)
  - `:59077/state` → `enabled:false, actualSessionBackend:""` (legacy schema — F1)
  - `:59077/engines` → legacy schema v2 without runtime-truth fields (F1)
  - `:59012/v1/capture/status` → gateway reports worker truth; `runtimeReady=false` under current FFmpeg route because `hardwareEncoderVerified=false` (honest)
  - `:59011/DulukaCapture/v.1.0/status` → `{enabled:true, ready:true, recording:false}` (legacy shim — F2)
- Deployed hash ledger (SHA-256): `NvCapture.dll DE04FDBB…`, `NvCapture.exe C2EF9AE9…`, `NvContainer.dll D17E2A56…`, `NvContainer.exe D3A59BAB…` vs current build `NvCapture.dll 5B687DEA…`, `DulukaEngine.Recording.dll 96C8A559…` → **deployed ≠ current source** (F1).
- Multiple pre-existing deployment backups present in the production tree (`NVIDIA Capture.backup-20261024-*`, `NVIDIA Capture.previous-20261024-1200`) — evidence of the established backup convention.
- No process was started, stopped or killed by this task; production worker state (`recording=false`) unchanged before/after.

## 8. Deployment Status

**STAGED — NOT DEPLOYED (blocked by preflight criteria, per policy).**

- Isolated staging achieved: `build_test\NVIDIA Capture Unified_Test\` with verified manifest reproducing canonical source digest.
- `Scripts/deploy-capture-unified.ps1` (existing owner-owned tool, reviewed) enforces exactly the policy the command requires: Stable branch, complete manifest with `nativeQsvPackaged=true` (F6 fails), `AssertIdle`, process-ownership survey, config-hash preservation, backup-then-swap, post-restart endpoint verification (`truthSchemaVersion=1` + gateway `workerReady` + NodeAPI `stateKnown`), and hash-verified rollback on any failure.
- Deploy is additionally gated on a **matching trust/version set**: worker + Container + NodeAPI must be staged together; Container and NodeAPI stage artifacts were not rebuilt in this task, so even with a QSV package the safe path is one coordinated deploy cycle.
- Production restart requires an operator window (overlay/OSC downtime while NvContainer respawns). Per the standing instruction ("ห้ามบังคับ Kill Process ที่อาจมีงานค้าง"), that decision is left to the human owner.

## 9. Rollback Status

- No deployment occurred → no rollback was needed or performed.
- Deployed tree verified unchanged by hash before/after the task (evidence JSON).
- When deployment is authorized, rollback materializes automatically from the deploy script's `runtime-backup` + `deployment-failure.json` flow; additionally the existing `NVIDIA Capture.backup-*` snapshots remain available.

## 10. Open Blockers

1. **B1 — Deployed runtime is older than source (F1/F2/F3).** Action: coordinated deploy of worker+Container+NodeAPI after (a) building the QSV native package so the unified manifest satisfies the deploy gate, (b) fixing the deployed `nvcontainer.json` duplicate `captureRuntime` block during that deploy (source template is already correct), and (c) an operator-approved restart window. After deploy, verify: `/engines` exposes `runtimeTruth`/`verification`, `/DulukaCapture/v.1.0/status` exposes `stateKnown`+`runtimeTruth`, deploy script's post-restart checks pass.
2. **B2 — Hardware/media verification still false everywhere.** On AORUS: NVENC hardware verification requires `POST /encoders/verify` (synthetic, safe) and a privacy-cleared video-only recording acceptance (`Invoke-CaptureSmoke.ps1 -ConfirmPrivacyCleared -ConfirmAudioDisabled` with a cleared scene) to upgrade `Saved=true` evidence. Both need the operator's cleared scene; not self-serve.
3. **B3 — `allowRecordCommands` remains false.** End-to-end record commands through OSC stay impossible by design until the owner enables them after B1/B2 — keep it off until the trust set is deployed and verified.
4. **B4 — QSV native package absent from this working tree (F6)** — run `Build-NativeQsv.ps1` (or reuse the verified canonical staging snapshot) before any deploy attempt.
5. **B5 — Intel live gate unchanged:** `intel_native_live_input_not_verified` stays in force; nothing in this task touched it.

## Definition-of-DoD assessment

- Control Plane uses matching contracts **in source** (three-layer chain verified end-to-end on the *current* tree; deployed layer still legacy — tracked as B1).
- Build/tests pass (Section 5/6, including the two-host canonical digest match).
- Deployed runtime verification: **not satisfied** — honest gap, not hidden (Section 7).
- Recording result states follow real evidence in code (P0-C gates) — live `Saved=true` acceptance still requires the cleared-scene run (B2).

**No phase was declared complete beyond its actual evidence. Nothing in this report should be read as "production ready".**

---

### สรุปภาษาไทย (สำหรับผู้คุมงาน)

งาน P0 รอบนี้ "ตรวจ + ยืนยัน + สร้าง staging ที่ reproduce canonical ได้" สำเร็จครบ แต่ **ยังไม่ deploy** เพราะขาดเงื่อนไขที่ script deploy บังคับและขาดหน้าต่าง restart จากมนุษย์:

- **ที่ผ่าน:** Build 0 error (digest ตรง canonical สองเครื่อง 100%), tests ผ่านรวม 210 + 201 checks + NodeAPI 8 suites, ไม่แตะจอ/ไมค์/ลำโพงแม้แต่บรรทัดเดียว, ไม่แก้ไฟล์ tracked ใด ๆ, production `recording=false` ตลอด
- **ที่พบใหม่:** deployed `nvcontainer.json` มี `captureRuntime` ซ้ำสองบล็อก (template ต้นทางสะอาด — เกิดจากการแก้ deployed copy ด้วยมือ) + ยืนยันด้วย hash ว่า runtime ที่ deploy เก่ากว่า source จริง
- **สิ่งที่รอตัดสินใจจากคุณ:** (1) สั่ง build QSV package + deploy ครบชุด worker/Container/NodeAPI พร้อมหน้าต่าง restart, (2) จัดฉากทดสอบ privacy-cleared เพื่ออัปเกรด `Saved=true` evidence, (3) ตัดสินใจเปิด `allowRecordCommands` หลัง deploy ผ่าน

---

# Continuation — ZCODE P0 Controlled Integration (2026-10-27, post-approval)

**Coordinator decision executed:** B1 approved with conditions, B2 approved (isolated runtime first), **B3 NOT approved** (`allowRecordCommands` stays `false`). All work below stayed on `Stable` @ `e63b52544a`; no reset/clean/commit/push; working-tree files of other agents untouched.

## PHASE 1 — FIX DEPLOYED CONFIG: PASS (with one honest incident)

Deployed config lives at `build\NVIDIA ShadowPlay\NVIDIA Config\nvcontainer.json` (the command's `NVIDIA Container\` path was off by one directory; both were checked).

1. **Backup:** `ZCODE-P0-Cont-20261027\config-backup\nvcontainer.json.before-zcode-fix`, SHA-256 `1AB9832752942F972A2E6FF848CB17378FF64DF3F75B63C855805D95FF90D63E`.
2. **Duplicate scan at every object level** (custom recursive-descent scanner, secret values never printed): exactly **one** duplicate in the whole file — `$.captureRuntime` (two top-level blocks). No other duplicates anywhere.
3. **v1 attempt REJECTED by the runtime deserialization gate:** my first serializer flattened nested JSON objects into arrays; the System.Text.Json check (`JsonElement.GetProperty("captureRuntime")` returned an Array) caught it immediately. The deployed file was **fully restored from backup (hash verified back to `1AB98327…`) before any further change**. This incident validates the "verify deserialization with the real runtime before declaring success" requirement.
4. **v2 (type-aware merge) applied atomically** (same-volume rename): `captureRuntime` merged to a single block = `{enabled:true, apiPort:59077, allowRecordCommands:false, allowDisplayCommands:false, allowScreenshotCommands:false}` — effective values identical to pre-fix runtime semantics (last-wins gave enabled/apiPort; flags came from fallback). New SHA-256 `29055DF9EEF60EB15EBE84A9E93179A9DCF987255900BB1A9DE2981959F1574B`. Pairing block, children, ports, testMode, watchdog all asserted unchanged; **no secret material printed anywhere**.
5. **Runtime deserialization check PASS** with production-equivalent System.Text.Json semantics (net10.0 console in the evidence dir): `ConfigBool(captureRuntime.allowRecordCommands, fallback=false) = False` — now an explicit value, not a fallback.
6. The running NvContainer reads this file once at boot, so the live process is unaffected and the next start sees identical effective values.

## PHASE 2 — UNIFIED NATIVE PACKAGE: PASS (no native recompile needed)

- Existing snapshot `C:\My Project\_CaptureAuditSnapshots\20261027-014248\qsv-native-package` (AORUS): **`QSV_NATIVE_PACKAGE_PASS abi=3 architecture=x64 abi_probed=True hardware_initialized=false`**; manifest = schemaVersion 1, dispatcherVersion **2.16.0** (rev `778a66d6…`), compiler `llvm-mingw-20260616-ucrt-x86_64`, per-source and per-file SHA-256 manifest, `libvpl-LICENSE.txt` included, `evidenceScope` honest. (The `20261027-014830` snapshot is the MateBook one, not present on this host.)
- **New unified staging:** `Scripts\build-dev.ps1 -Module Capture -QsvNativePackageDirectory <snapshot> -RequireNativeQsv -Dest <ZCODE-P0-Cont-20261027\unified-staging-qsv>` → **`CAPTURE_UNIFIED_BUILD_PASS`, `native_qsv_packaged=True`, 45 files**, `sourceSha256 = 7AD1108B…7F23` — **still byte-identical to the canonical two-host digest**. `hardwareVerified=false mediaVerified=false deployed=false` remain truthful. No `-NoBuild` was used.

## PHASE 3 — INTEGRATION PREFLIGHT: PASS (isolated runtime + read-only production survey)

**Isolated sandbox contract test** (sandbox root assembled from the new staging; engine mode `FFmpeg` so `UI_Engine_Load` skips DXGI/NVENC ownership entirely per `UI_Engine.vb:110-114`; marker + dedicated configs; port 59477 per the reserved 59100-59999 range):

- `GET /health` and `/state` now return the **new schema with `runtimeTruth`** (deployed production lacks these fields — the staging is the newer source).
- `GET /engines`: `schemaVersion=2, truthSchemaVersion=1, readOnly=true`, all truth fields present (`requestedConfiguration`, `savedConfiguration`, `selectedEngine`, `actualSessionBackend=""`, `actualRunningEngine=""`, `verification{hardwareVerified:false, mediaVerified:false, scope:current_session_unverified}`, `availableCapabilities` with OBS=`obs_capture_not_implemented`, NVIDIA=`external_recorder_status_unverified`). **`selectedEngine` (policy) is never surfaced as `actualRunningEngine`** — the required policy-vs-truth separation, proven live.
- `GET /displays`: schema v1, metadata-only enumeration.
- **Fail-closed writes proven:** `POST /record/start` without token → **403 `capture_request_denied`**; with wrong 32-hex token → **403**; spoofed Host header → **403**; `PUT` → **405 `method_not_allowed`**; state untouched after all attempts (`recording:false, lifecycleOwner:""`).
- Sandbox process stopped cleanly afterward; no leftover listener.

**Production preflight replication (read-only, prepare-only):** `deploy-capture-unified.ps1` requires PowerShell 7 (`#requires -Version 7.0`) and **pwsh is not installed on this host**. I replicated its prepare steps under PS 5.1 without writing any production file: `ASSERT_IDLE PASS`, process survey + `processes-before.json`, per-file plan — **45 capture files (19 differ from deployed) + 9 container files (5 differ)**, config inventory hashed (values not printed), plan JSON saved to `deploy-evidence\deployment-plan-prepare-only.json`.

**Deploy-blocking facts discovered by the survey:**
- CIM from a non-elevated shell cannot see the paths of the production `NvCapture`/`NvContainer`/`NvNode` processes (elevated/service context) — the deploy script's process-kill step would silently miss them and the artifact copy would fail on locked files. **A real production deploy must run from an elevated shell.**
- Three `Launcher.exe` processes are alive (the user may be actively using the overlay UI) — another reason the restart window belongs to the operator, not to this agent.

## PHASE 4 — DEPLOYMENT STATUS: PREFLIGHT PASSED — PRODUCTION DEPLOY INTENTIONALLY NOT EXECUTED

The command allows Isolated Staging immediately (done: `unified-staging-qsv` + `container-stage`, both verified) and gates Production on safety conditions. Per the instruction "หากต้องหยุดโปรแกรมที่ผู้ใช้อาจกำลังใช้งานอยู่ ให้หยุดที่ Preflight แล้วรายงานสถานะก่อน", production deploy stops here with: **READY, awaiting (a) an elevated PowerShell 7 shell on this host, (b) an operator-approved restart window, (c) no concurrent agent activity** (currently three Launcher processes are user-visible). Nothing was killed, nothing in the production tree was modified during this continuation.

## PHASE 5 — NVIDIA RECORDING ACCEPTANCE: **PASS — `Saved=true` on real media**

Executed exactly per the existing isolated-test contract (`Show-CaptureTestScene.ps1` + `Invoke-CaptureSmoke.ps1 -RunSmoke -ConfirmPrivacyCleared -ConfirmAudioDisabled`), engine **Duluka**, binary from the **new unified staging**, isolated root outside the source tree, port 59477:

- Scene covered **all three physical displays** (1680×1050, 1080×1920, 1280×800) with test-owned graphics ("test-owned moving grid, frame counter and barcode", `audio: disabled`) — every captured pixel was test-owned content.
- Production `:59077` health was verified idle before launch (fail-closed check in the smoke script itself).
- Start accepted: `backend=Duluka`. Active state truth: `lifecycleOwner=Duluka, actualSessionBackend=Duluka, actualRunningEngine=Duluka, lifecycleState=Recording, sessionId=b0184f900879495092e384f066071814, realFramesCaptured=133, framesEncoded=184`.
- Stop result: **`stopped=true, saved=true, finalizationState=Saved, mediaVerified=true, audioPolicyVerified=true, verificationState=full_video_decode_and_audio_stream_policy`**; `systemAudioRequestedAtStart=false, microphoneRequestedAtStart=false`; media `Verified=true, VideoStreams=1, AudioStreams=0, VideoDecodeChecked=true, VideoDecodeVerified=true, DurationSec=6.233272`.
- Native session result: **`Pass=true, FinalizationState=Saved, FullDecodeVerified=true`** with full packet/queue accounting fields populated.
- Independent acceptance checks inside the smoke script: output confined to the isolated root, H.264/1680×1050 via ffprobe, **full decode via `ffmpeg -xerror` passed**, duration 6.23s ≥ requested 6s window, size 3,119,681 bytes.
- Evidence: `smoke-evidence\duluka-desktop-smoke.json` (+ engines-before, state-active, stop-result), media file under `smoke-root\Records\`.
- Honest notes: `hardwareVerified=false` in the REST response is **by design** for the REST boundary (the session's `VideoIdentity`/LUID attestation lives in the native session result, not in the REST flag); the sandbox ran the same binary the production deploy will use, but on the isolated port/root.

## PHASE 6 — PRODUCTION COMMAND POLICY: HELD

`allowRecordCommands=false` verified in the deployed config post-fix (now explicit, single block). No production command path was exercised at any point; the only recording in this continuation was the isolated sandbox one above. `allowDisplayCommands`/`allowScreenshotCommands` remain `false`.

## Updated deployment checklist for the operator (remaining gate to production)

1. Install/obtain PowerShell 7 (`pwsh`) — required by `Scripts/deploy-capture-unified.ps1`.
2. Run that script from an **elevated** shell (production processes are elevated; a non-elevated shell cannot stop or replace them).
3. Choose a restart window with no user-facing overlay activity (Launcher processes were alive during this continuation).
4. The script's own gates then apply: AssertIdle → process ownership → backup → swap → restart → post-restart verification (`truthSchemaVersion=1`, gateway `workerReady`, NodeAPI `stateKnown`) → hash-verified rollback on any failure.
5. After deploy, re-verify: `/engines` exposes `runtimeTruth`/`verification`; `/DulukaCapture/v.1.0/status` exposes `stateKnown`; `allowRecordCommands` still `false` (config hash must be unchanged — the script enforces this).
6. Only after integration + acceptance evidence at production: decide on `allowRecordCommands` (B3) — still NOT approved as of this report.

## Post-acceptance incident note — NodeAPI `enabled:false` after an external overlay restart (2026-10-27 03:30)

During the final verification pass, `NvNode` restarted (old PID 36336 → new PID 27684, both Web Helper and NvNode at 03:30:47). Evidence trail:

- `NvContainer.log` shows `[overlay] CEF engine/system disabled — stopping overlay services` at 03:30:37, then `enabled — starting overlay services` at 03:30:47 — triggered by an external write to `NVIDIA Config\config.json` (mtime 03:30, now `UseOverlayEnabled:true/EngineOverlayMode:true`). **This was a concurrent agent's overlay toggle, not this task's action; the watchdog behaved exactly per design.**
- After that restart, `GET :59011/DulukaCapture/v.1.0/status` returns `{"enabled":false,"ready":false,"recording":false}`. The deployed shim (Oct 24 build) returns this exact shape **only** when `NV_CAPTURE_RUNTIME_ENABLED !== '1'`, i.e. the new NvNode process booted with the flag off — while the deployed `nvcontainer.json` (this task's v2 fix) says `captureRuntime.enabled=true`, and an exact replication of the deployed `index.js` config-read logic resolves `enabled:true, apiPort:59077`.
- Verified **not** caused by this task's config change: the v2 file parses with zero duplicates, has no BOM, is read correctly by the deployed reader logic, and the Container still reports `runtimeEnabled:true` (read at its own boot). The `enabled:false` response is fail-safe (it claims nothing is ready — it never fakes availability).
- **Unproven from a non-elevated shell:** which exact `index.js`/working directory the restarted NvNode loaded (process path is hidden behind elevation; no nvnode.log written). The flag-off state resolves itself at the next host restart — which the production deploy window performs anyway with the new trust set. No further action was taken to avoid killing production processes mid-session (three Launcher processes were user-visible).
- Impact: read-only status readback shows capture runtime "disabled" to OSC until the next host restart; recording safety posture unchanged (`allowRecordCommands=false` everywhere). Added to the operator checklist: after deploy restart, confirm `/DulukaCapture/v.1.0/status` returns `enabled:true` and the new `stateKnown` field.
