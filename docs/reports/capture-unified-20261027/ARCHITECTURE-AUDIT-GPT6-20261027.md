# ShadowPlay NEXT / NVIDIA Capture — Architecture Audit (GPT-6)

Date: 2026-10-27 (Asia/Bangkok). Read-only source/runtime audit plus isolated Release build. Baseline Git HEAD: Stable e63b52544a. **Do not interpret the current dirty working tree as that commit's contents.** No commit, push, deployment, restart, screen/audio recording or configuration writes were performed.

## Audit conclusion

Canonical contract SPN-ADR-2026-10-24-SESSION-001 is a sound TARGET: one Duluka RecordingSession owns SessionId, QPC/PTS, selected output, frame and encoder ownership, audio, MP4 mux and verification. FFmpeg standalone, OBS external and installed NVIDIA are independent owners. Production functionality is narrower: DXGI Desktop + native NVIDIA NVENC has an isolated real video-only recording; Intel QSV is synthetic-input only; Exclusive source transitions, OBS bridge and NVIDIA-owned attestation are not production proven.

## As-is control and media graph

    OSC UI -> NodeAPI :59011 -> Container :59012 -> NvCapture :59077
                                              |
                                     RecordingBackendRoute
                                              |
                       +----------------------+-----------------------+
                       |                      |                       |
                 Duluka native          FFmpeg standalone         OBS/NVIDIA external
                       |                      |                       |
              DXGI Desktop Duplication  FFmpeg-owned process       Not production verified
                       |                      |
              NVENC H.264 (native)       Own capture/encode/mux
                       |                      |
               CaptureSession                 +--> output & evidence
                       |
           Session frame/packet path
                       |
          WASAPI audio (native session) + LiveMuxSession (FFmpeg as muxer)
                       |
           MP4 Finalize -> SessionMediaVerifier / CaptureMediaInspector
                       |
                 Saved / Partial / Failed

Separate, CLOSED Intel source path: test-owned D3D11/BGRA -> CPU NV12 staging -> native QSV ABI 3 -> same owned-frame session/mux; `intel_native_live_input_not_verified` remains in effect.

## Status by required diagram box

| Box | Source | Deployed/runtime proof | Verdict |
| --- | --- | --- | --- |
| OSC and NodeAPI | NodeAPI shims and OSC capture UI | /DulukaCapture/v.1.0/status returns HTTP 200; UI POST end-to-end not exercised | Partial |
| Recording Controller | CaptureRestApiServer, CaptureBackendRoute, RecordingEngineHost | NvCapture /health, /state, /engines HTTP 200; saved FFmpeg selection differs from Container policy-only Duluka plan | Partial |
| Duluka Engine | DdagrabBackend + RecordingEngine + NvencRuntimeAdapter | Isolated genuine desktop/video-only stream produced decoded MP4; latest fixes not deployed | Partial |
| Duluka RecordingSession | CaptureSession, SessionPacketSink | 158-frame retained media, stop returned Partial (not Saved) before verifier correction | Partial |
| FFmpeg Engine | LegacyCaptureEngine, FFmpegArgumentBuilder | Installed binary enumerates APIs/encoders; no new standalone full live capture verified by this audit | Partial |
| OBS-owned Engine/Pipeline | catalog planned and route reject | no runtime bridge | Not implemented |
| Installed NVIDIA-owned Engine | external hotkey route | optimistic flag is not recording attestation; NvFBC unavailable | Unverified |
| Video Source Manager | RecordingBackendFactory and SourceCompositionDriver | live source manager with mode switching NOT wired | Partial/design |
| Desktop Capture API | DXGI Desktop Duplication | isolated actual 1680x1050 desktop frames; source identity enforced | Verified for scoped video-only run |
| Exclusive Capture API | separate source selection target | no independent verified exclusive source/transition | Unverified |
| Frame Router | SessionFrameRouter | controlled generation/sequence/PTS checks, synthetic tests | Partial |
| Composition Surface | BgraSessionSource + conversion/staging | SourceCompositionDriver explicitly accepts test-owned producers only | Synthetic only |
| Audio Manager | AudioEngineSession / WASAPI | synthetic 5/5 audio intent; owned-frame path rejects audio; no live A/V sync proof | Partial |
| Encoder Manager | native NVENC, QSV bridge ABI3 | NVIDIA isolated video-only capture proof; QSV managed/native staging/synthetic only | Partial |
| Encoded Video Packets | SessionPacketSink | controlled packet conservation and flush; native retained video decoded | Partial |
| MP4 Muxer | LiveMuxSession | video-only actual retained MP4; synthetic video/audio tests | Partial |
| Finalize + Verify | SessionMediaVerifier, CaptureMediaInspector | retained 158-frame MP4 now re-verifies, no NEW final saved smoke | Partial |
| Saved / Partial / Failed | SessionResult and REST evidence | original stop was Partial / saved=false despite valid media, latest fix needs fresh isolated acceptance | Partial |

## Evidence and test gates

- NVIDIA isolated desktop attempt: `nvidia-desktop-attempt-01/` — 158 video frames, 1680x1050, duration 5.266614 s, actual changing decoded frame SHA256s. Original stop `saved=false`, `finalizationState=Partial`, `mediaVerified=true`, `hardwareVerified=false`. No audio requested. Independent read-only verifier rerun reports `Verified=true`, 158 decoded/packet count, pending=false; this is NOT a new end-to-end success.
- Control plane: `control-plane.md` reports 67/67 authorization/status/settings/route tests; new direct-write DPAPI header contract is source-only until deployed and verified.
- Session contract: `session-contract-run.log` reports 35/35; controlled session/media synthetic `session-media-synthetic-run.log` reports 43/43; `audio-intent-synthetic-run.log` 5/5 (synthetic sine PCM). No actual WASAPI test.
- Container/NodeAPI: 59 worker gateway stub tests, 24 capture catalog tests, 26 display relay mock tests, 13 control request tests and 61 Node capture UI tests recorded in this report directory.
- Unified native packaging: `unified-build.md` reports 12/12 fault checks on PowerShell 7 and Windows PowerShell 5.1, verified ABI3 QSV bridge on both hosts, staged native-required v3 Release pass. v3 predates latest media changes.
- Fresh isolated Release build performed by GPT-6 from AORUS current modified source: exit=0, warnings=2, errors=0. Log: `C:\My Project\_CaptureAuditSnapshots\GPT6-Architecture-Audit-20261027\current-workingtree-release-build.log`. It does not certify deployed runtime.

## Runtime and two-machine drift (verified)

- Branch/HEAD on AORUS and MateBook both `Stable` / `e63b5254` at inspection; **working trees are NOT identical**. `NVIDIA Capture.vbproj` and managed QSV `.csproj` SHA256s matched. `Build-NativeQsv.ps1`, `New-NativeQsvPackage.ps1`, and `Test-NativeQsvPackage.ps1` differed in working-tree SHA256. Do not merge or overwrite uncommitted changes automatically.
- AORUS deployed `build/NVIDIA ShadowPlay/NVIDIA Capture/NvCapture.dll` SHA256 begins `DE04FDBB1936B6A4`; prior ordinary `Project/NVIDIA Capture/bin/Release/.../NvCapture.dll` begins `B22B35B984EB77BC`. Current isolated build has independent output. Installed production binary is not the latest audited source.
- AORUS NvCapture :59077 and Container :59012 both respond; NodeAPI :59011 `/DulukaCapture/v.1.0/status` responds HTTP 200. NodeAPI `/health` responds 404 (not a reason by itself to call the service down). Current worker saved configuration reports FFmpeg/ddagrab/h264_nvenc, whereas Container selected Duluka/NVENC is explicitly `policyOnly=true` — not an active engine claim.
- The deployed worker was idle (`recording=false`) when read; no recording or mutation commands were issued.

## Critical disposition

**P0-A: Snapshot/coordinate dirty files, then reconcile the three diverged Native QSV packaging scripts on AORUS vs MateBook.** Preserve agent ownership and untracked reports. Re-run both-host unified build and validate reproducible package manifests.
**P0-B: Retest the *latest* verifier/session changes with a privacy-cleared isolated NVIDIA video-only test, verifying genuine DXGI input, SessionResult Saved=true, matching decoded frame/packet counts and fresh Runtime Truth.** No live capture without authorized cleared test scene.
**P0-C: Stage the current combined worker, Container and NodeAPI as a compatible trust/version set; validate DPAPI-authenticated write relay, read-only Runtime Truth, fail-closed writes and media result without overwriting the deployed binaries until preflight/rollback criteria pass.**
**P1-A: Prove real WASAPI system/mic capture, required audio policy, long-duration A/V sync and verification pending states; owned-frame Intel audio remains blocked.**
**P1-B: Implement and verify real source-generation continuity or explicit failure for Desktop/Exclusive; never silently migrate selected output or claim placeholders are real gameplay.**
**P1-C: Intel QSV: validate deployment package on MateBook, then real live DXGI -> CPU NV12 -> QSV/MP4 under controlled test before changing `intel_native_live_input_not_verified`.**
**P2: Screenshot/replay and OBS/NVIDIA external owner integrations remain separately gated; do not conflate native NVENC with NvFBC or installed ShadowPlay.**

## Acceptance rule

Architecture design pass != source compile != synthetic pass != current deployed runtime != real media saved. Require all applicable layers. This audit did not change implementation files, commit, push, deploy, restart or record. All reported test data is scope-limited by the evidence above.

---

## Continuation: P0 source unification and staged verification (2026-10-27)

**Real work performed on AORUS-DESKTOP and HUAWEI-MateBook.** The Git branch remains `Stable` at `e63b52544a` on both; this section covers **uncommitted working-tree changes**, not that historical commit. No `git reset/clean`, staging, commit, push, production deploy, capture restart, desktop/video recording or audio recording occurred.

### Source safety and synchronization

- Snapshot backups on each host: `C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\before-qsv-script-sync\`.
- MateBook Native QSV packaging scripts `Build-NativeQsv.ps1`, `New-NativeQsvPackage.ps1` and `Test-NativeQsvPackage.ps1` synchronized byte-for-byte to the reviewed AORUS versions. The AORUS variants include PATH-safe Git resolution, pinned dispatcher dirty-tree rejection and consistent PowerShell 5.1 utility module handling.
- Further AORUS Capture working-tree snapshot: **32 files**, of which 5 already matched MateBook and **27** were transactionally copied only after `0` dirty/untracked conflicts. Source ZIP SHA-256: `9301C5292D16FD2EC6488EB5C31D9E25D336FEF5C8CB50DE9C7E5106DC59BD5C`. MateBook destination backups: `C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\before-capture-workingtree-sync\`. The temporary allowlist-only LAN firewall rule was deleted after ZIP checksum verification.
- `Scripts/build-dev.ps1` and `Scripts/Build-CaptureUnified.ps1` were also reconciled byte-for-byte. The previous MateBook copies are backed up under `before-build-script-sync\`. The additional AORUS `Build-CaptureUnified.ps1` source selection / runtime-copy safety behavior was preserved.
- Exactly **208** Capture build input files were inventoried per host. Eight byte-hash mismatches were caused solely by CRLF on MateBook versus LF on AORUS; content normalized in memory was identical. `Build-CaptureUnified.ps1` now computes the SHA-256 from LF-normalized **text** bytes, retaining exact raw hashes for `.exe/.dll`; the original script was backed up under `before-canonical-source-hash\`. This avoids false source drift from a Git checkout's line-ending policy, not a relaxation of native-binary verification.

### Canonical native-required build — both hosts PASS

| Gate | AORUS | MateBook |
| --- | --- | --- |
| Unified Release Build | PASS: 0 errors / 2 WebRequest deprecation warnings | PASS: 0 errors / same 2 warnings |
| Native QSV Package ABI 3 | PASS | PASS |
| Native packaging fault matrix | 12/12 PASS | 12/12 PASS |
| Staged file count | 45 | 45 |
| `nativeQsvPackaged` | true | true |
| `hardwareVerified` | false | false |
| `deployed` | false | false |
| `-NoBuild` source+artifact hash re-verification | PASS | PASS |

Canonical `sourceSha256` **identical on both hosts**: `7AD1108BB5180E3F43EFA29A3DC96B809AD49F6E8C1B1DFAFF975997AD8D7F23`.

Verified native package manifest SHA-256 also identical: `7D84798F7549BEA320C034D3BC1015DA8F2C233E67CB68B6B2B9AD93A5F6CBBF`.

Staged build roots:
- `C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\AORUS-canonical-staging\`
- `C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\MateBook-canonical-staging\`

The output managed DLL byte hashes can still differ due to checkout EOL/build/debug artifact metadata. This audit certifies equivalent canonical input/source digest and validated staged dependency sets, **not byte-for-byte identical executables or hardware production readiness**.

### Synthetic and controlled hardware regression

- Both hosts: RouteGuard + ControlPlane runner exit 0 (the route runner separately prints 33 route/capability/HTTP/isolation assertions; `ControlPlaneTests.Run` executes first).
- MateBook: Session contract **35/35**; synthetic media/failure-result verification **43/43**; Intel I3 D3D11/CPU NV12 synthetic transport **660/660** (no screen or audio).
- AORUS: `LegacyCompletion.Tests` against the validated staged Capture assembly: **51/51** checks pass, exercising FFmpeg child exit/fatal/timeout with a generated color fixture. No screen/audio capture.
- MateBook: Intel QSV native synthetic hardware acceptance **666/666**, with VBR/CQP, native ABI, owned NV12 input, mux/decode, frame ownership, failure recovery and LUID checks. No screen pixels read; no audio opened.

**Important test contract fix:** the older `QsvNative.Tests/SessionTests.cs` expected a second encoder after a *source-only* stale-generation fault. The new production Session logic drains and verifies a retained Partial, leaving QSV healthy/stopped, so encoder reuse is correct. The test now separately verifies healthy source-fault reuse **and** injects a real native flush/mux failure before requiring the RecordingEngine factory to construct a different encoder. Both paths passed on Intel hardware. The old test files are backed up under `before-qsv-recovery-test-fix\`; the new test source hashes match across both machines.

### Final working-tree/runtime safety

- Git `Stable` remains at historical `e63b5254` locally on both; relevant `git diff --check` returned exit 0, no unmerged paths. **Uncommitted changes remain uncommitted** to protect concurrent work.
- Temporary source-sync firewall rule absent on both hosts.
- AORUS installed NvCapture worker still reports `recording=false`. MateBook has no running NvCapture worker. Neither staged binary has been deployed or production-restarted.
- Existing worker remains older than canonical staged Capture source; Worker/Container/NodeAPI trusted-write compatibility and actual recorded media must be checked in a controlled deployment before marking source changes production-ready.

### Outstanding work — not hidden by green synthetic tests

1. **P0 Runtime compatibility/rollout:** authorize and verify matched Capture/Container/NodeAPI Control Plane versions, DPAPI mutation-token injection, policy/status truth, rollback snapshots, then staged deployment only when process ownership and recording safety allow.
2. **P0 NVIDIA live verification:** on a privacy-cleared test scene, confirm latest Session/Media verifier reports `Saved=true` for real Desktop DXGI/NVENC media, with full decode and requested track counts. Earlier 158-frame MP4 was valid but its original operation result was Partial; read-only reinspection alone is not a new success.
3. **P1 Intel Live:** `intel_native_live_input_not_verified` remains enforced. Synthetic QSV 666/666 does not certify live Desktop source, audio, exclusive switching or driver/performance matrix.
4. **P1 Audio/Exclusive:** real system/mic source/PTS sync and source-generation continuity remain unproven; keep fail-closed behavior.
5. **P2 external pipelines:** OBS bridge, NVIDIA installed-recorder status attestation, Replay and verified Screenshot remain separate acceptance gates.

**No production Deploy/Restart was attempted in this continuation.**

---

## GPT-6 review of ZCODE P0 Controlled Integration — 2026-10-27

### Verified new milestone

Read the ZCODE smoke evidence from `C:\My Project\_CaptureAuditSnapshots\ZCODE-P0-Cont-20261027\smoke-evidence\stop-result.json` and `duluka-desktop-smoke.json` directly, rather than relying exclusively on Agent prose. The isolated, test-scene video-only Native Duluka recording reports `saved=true`, `finalizationState=Saved`, `media.Verified=true`, `VideoDecodeVerified=true`, `VideoStreams=1`, `AudioStreams=0`, duration 6.233272 sec, session `b0184f900879495092e384f066071814`, frames captured 133, frames encoded and muxed 187, duplicated frames 54. Session `Pass=true`. Correct scope: isolated 1680x1050 NVIDIA/DXGI/NVENC video-only, NOT Intel live, WASAPI, exclusive or production recording. REST `hardwareVerified=false` remains honest despite native VideoIdentity LUID evidence.

ZCODE's deployed duplicate-config repair and new Native QSV staging are documented in `ZCODE-P0-IMPLEMENTATION-REPORT.md`. The v1 config serializer corruption was rolled back and v2 is backed by System.Text.Json evidence; no assumption of source or production state solely from this agent narrative.

### Deployment blocker discovered by independent code review

`Scripts/deploy-capture-unified.ps1` currently declares success when `$capture.truthSchemaVersion -eq 1 -and $gateway.workerReady -and $node.PSObject.Properties['stateKnown']`. **The final term checks only whether a property exists**, not whether `stateKnown` is `true`, nor whether NodeAPI is `enabled` and `ready`. Likewise `gateway.workerReady` is not the same as `gateway.runtimeReady`. A worker process can respond while capture remains unavailable.

**Recommended owner patch before using `-Deploy`:** require `$gateway.runtimeEnabled -eq $true`, `$gateway.runtimeReady -eq $true`, `$gateway.workerReady -eq $true`, `$node.stateKnown -eq $true`, `$node.enabled -eq $true`, `$node.ready -eq $true`, plus worker engine truth schema v1 and idle/no-unresolved commands. If runtime is intentionally disabled by an authorized user, explicitly encode that expected mode rather than treating a disabled service as Ready. Do not turn on capture commands as part of checking readiness.

No changes were made to `Scripts/deploy-capture-unified.ps1`, which is untracked and belongs to another Agent.

### Read-only independent status guard

Created outside the repository, without modifying production:
`C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\ReadOnly-Capture-PostDeploy-Gate.ps1`.

Ran on AORUS; exit code 1 (intentional fail-closed) with 3 blocked checks:
- `workerTruthSchema`: legacy deployed worker `/engines` lacks truth schema 1.
- `gatewayWorkerReady`: `workerReady=true` but `runtimeReady=false` (worker readiness does not prove capture is available).
- `nodeSchemaAndKnownState`: old deployed NodeAPI response omits `stateKnown`.

At the last live probe, `NvCapture /state` returned `ready=true`, `enabled=false`, `recording=false`; Container returned `workerReady=true`, `runtimeReady=false`; deployed NodeAPI returned `enabled=true`, `ready=true`, `recording=false` but no `stateKnown`. Earlier NodeAPI had reported `enabled=false,ready=false`. This demonstrates **unreliable cross-layer ready truth** during overlay restarts. The old shim reads NvCapture `/health` directly, whereas new source requests Container `/v1/capture/status`.

A further source edge case exists: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/capture-runtime-status.js` returns only 3 fields from the `if(!enabled)` branch, omitting `stateKnown`. Preserve a consistent fail-closed schema for disabled state in a separate owner-scoped change; do not claim knowledge about recording state without upstream evidence.

### Disposition

- **B2 isolated video-only acceptance:** PASS, evidence verified from smoke JSON. No new live recording conducted by GPT-6.
- **B1 production deploy:** BLOCKED; latest source not deployed, deployment script uses insufficient readiness acceptance; PowerShell 7/elevated restart window still pending.
- **B3 production recording writes:** remain FALSE / NOT APPROVED.
- Explicitly coordinate with deployment-script and NodeAPI owners; do not overwrite their uncommitted changes. Production processes/configuration were not changed by this GPT-6 review.

---

## GPT-6 P0 Readiness Contract hardening — 2026-10-27

**Scope:** source-only readiness corrections and mock/isolated regression, no production deployment/restart or screen/audio capture. Current deployed NvCapture and NodeAPI are still older than the checked source.

### Files changed
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/capture-runtime-status.js`: the runtime-disabled and invalid-port/unavailable branches now use the same fail-closed unknown-state schema (recording=null, stateKnown=false, workerReady=false, no phantom actual engine), instead of claiming recording=false without consulting the worker.
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/tests/captureRuntimeStatus.test.js`: disabled branch explicitly verifies the complete unknown-state contract.
- `Scripts/deploy-capture-unified.ps1`: replaces prior optimistic gate (`gateway.workerReady && node.PSObject.Properties['stateKnown']`) with a multi-layer readiness decision that validates actual boolean values including worker health/state, gateway `runtimeReady`, no unresolved command outcomes, NodeAPI `stateKnown/ready/enabled`, and idle/actual-engine consistency.
- New `Scripts/CaptureDeployReadiness.ps1`: shared side-effect-free readiness decision function. New `Scripts/Test-CaptureDeployReadiness.ps1`: mock test matrix for missing/false stateKnown, old schemas, disabled, unknown, recording-active, gateway worker-ready-only, unresolved and mismatched actual-state cases.

Pre-change backups are stored at `C:\My Project\_CaptureAuditSnapshots\GPT6-Resume-20261027\before-capture-readiness-contract-fix\` (hash-verified). Original untracked deploy script is preserved in this backup. Ownership-sensitive edits remain uncommitted.

### Tests
- PowerShell AST parse 0 errors for `deploy-capture-unified.ps1`, `CaptureDeployReadiness.ps1`, `Test-CaptureDeployReadiness.ps1`.
- Readiness mock matrix **18/18 PASS**, no runtime connections or state writes.
- NodeAPI `captureRuntimeStatus.test.js` **8/8 PASS**.
- Full six-file NodeAPI capture regression **61/61 PASS**.
- `git diff --check` for the modified NodeAPI source/tests PASS (line-ending warnings are informational only).

### Production status / gating
- On AORUS, independent read-only `ReadOnly-Capture-PostDeploy-Gate.ps1` returned **blocked**, correctly detecting old worker truth schema, Gateway runtimeReady=false despite workerReady=true, and old NodeAPI missing stateKnown. Another status read found Worker `ready=true,enabled=false`, Container `workerReady=true,runtimeReady=false`, NodeAPI `enabled=true,ready=true` but missing stateKnown. These states must NOT be marked a successful production integration.
- A separate successful isolated Duluka smoke is documented earlier; it does not change this production truth.
- Production `allowRecordCommands=false` remains unchanged. PowerShell 7 is not installed; the full deploy script cannot be executed on this host without satisfying its runtime/elevation/operator-window prerequisites. The new readiness logic was unit-tested in PowerShell 5.1 but not yet exercised through a PowerShell 7 production restart.
- The strict post-deploy gate may intentionally reject an otherwise schema-compatible but **disabled** worker: this is a truthful `not ready` result, and the correct resolution is an explicit operator policy decision, not silently switching recording/engine flags or treating process presence as readiness.

**Next:** resolve expected disabled-runtime policy and authoritative versioned NodeAPI+Container+Capture deployment under operator-approved window, test actual post-restart readiness, and preserve rollback. Do not change production record command policy as a side effect.

---

## GPT-6 continuation: Deployment staging-path and config-guard fixes (2026-10-27)

### Observed production drift

At inspection, the product NvCapture worker was absent; HTTP :59077 was unreachable. Product NvContainer (PID 23616, started ~04:41) and NvNode (PID 26276, started ~04:41) held ports :59012 and :59011 but **capture-specific endpoints responded HTTP 404**. Deployed NvContainer executable was the Oct 18 binary, and NvCapture.exe was the Oct 24 binary. Both are older than the current checked source. **No attempt was made to stop, start or restart any production process while concurrent runtime changes were evident.**

The installed `build\NVIDIA ShadowPlay\NVIDIA Config\nvcontainer.json` at inspection had SHA-256 beginning `7F244C3D02D70D14`, modified Oct 20, with a single `captureRuntime` object holding only `enabled=true` and `apiPort=59077`. This **does not match** the ZCODE v2 deployed-config ledger hash beginning `29055DF9` from earlier the same day; exact writer/rollback source is unknown. Current missing command flags default fail-closed but violate the explicit default-deny deployment policy. Product `Config\config.json` selects Duluka/NVENC_H264, consistent with the successful isolated route but **not proof that production can run it**.

### Corrected a newly discovered API misunderstanding

`Project\NVIDIA Capture\src\[API]\CaptureRestApiServer.vb` publishes `/state.enabled = active` (true only during recording). It is **not** a service-enabled flag. The previous readiness helper incorrectly required `WorkerState.enabled=true` in an idle state, making a passing verdict impossible. `Scripts/CaptureDeployReadiness.ps1` now requires `WorkerState.ready=true` and, separately, `enabled=false,recording=false,busy!=true,actualSessionBackend empty` for an idle session. Mock test matrix **18/18 PASS** with the corrected semantics.

Gateway `runtimeReady` is stricter than worker readiness: `CaptureWorkerGateway.Status()` requires an actually supportable selected capture/encoder path; FFmpeg route with `hardwareEncoderVerified=false` must truthfully remain unavailable despite a healthy worker. That readiness policy is intentionally not weakened. Production deployment must verify selected engine readiness or remain BLOCKED. The new source of truth is native Duluka configured mode in the product's `Config\config.json`, which must be verified after deployment rather than inferred from policy-only selection.

### Fixed a hard Deploy Script staging bug

The validated Capture Manifest already includes `NVIDIA Capture\FFmpeg\ffmpeg.exe`, `ffprobe.exe` and their dependencies as nested entries. However, `Scripts/deploy-capture-unified.ps1` had an additional loop attempting to plan files under the **nonexistent** `CaptureStage\FFmpeg` sibling. This would fail the real artifact preflight even after PowerShell 7 became available. Removed the erroneous duplicate loop; the manifest now exclusively owns the Capture FFmpeg subtree.

Added `Scripts/Test-CaptureDeployPlan.ps1` (read-only) to validate files/hash/path traversal/duplicates and all planned owners in the existing ZCODE staging directory. **PASS: 55 files = 45 Capture + 9 Container + 1 NodeAPI**, includes FFmpeg and Native QSV license/manifest; does not touch deployed files. Deploy Script AST syntax check **0 errors** on PowerShell 5.1; actual `#requires -Version 7.0` invocation is still blocked by missing PowerShell 7.

### Added safe production-config preflight

`Scripts/CaptureDeployReadiness.ps1` now also implements `Assert-CaptureDeployConfig`, invoked by `Scripts/deploy-capture-unified.ps1` **before any process shutdown**. It requires one `captureRuntime` declaration, `enabled=true`, and typed explicit `false` for `allowRecordCommands`, `allowDisplayCommands`, `allowScreenshotCommands`. Current rollout policy is B3 OFF, so any auto-enable or missing key blocks the deploy; no attempt is made to repair user configuration automatically.

`Scripts/Test-CaptureDeployConfig.ps1` exercises valid config, missing flags, enabled commands, string booleans, disabled runtime, duplicate `captureRuntime` and malformed JSON using temporary fixtures. **PASS 9/9**. Current deployed config gets the expected **BLOCKED** result: `capture_deploy_write_flag_not_explicitly_disabled_allowRecordCommands`, without any modification.

### Gate summary and next action

- Source readiness matrix 18/18 PASS, config guard 9/9 PASS, artifact plan 55/55 PASS, PowerShell AST parse 0 errors.
- Native isolated NVIDIA DXGI/NVENC Saved=true evidence from ZCODE remains valid and scoped; no new screen/audio capture performed.
- **Production NOT deployed/restarted.** Blocking issues: currently running old Container/NodeAPI; NvCapture absent; old deployed config has missing explicit write-command denial keys; PowerShell 7/elevated operator window not prepared; concurrent runtime manipulation suspected and not yet coordinated.
- Correct sequence: obtain an uncontested runtime window, backup/config-merge the three explicit false flags with schema/duplicate verification and without exposing secrets, install/locate verified PowerShell 7, run staging preflight under an elevated authorized shell, deploy matched Worker/Container/NodeAPI only after all process/config safety gates pass, verify exact binary hashes and state contract. Production Record command forwarding remains disabled.
