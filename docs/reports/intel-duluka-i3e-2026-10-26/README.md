# ShadowPlay NEXT — I3-E Main-Only Receiver Fault Hardening

**Date:** 2026-10-26 (Asia/Bangkok)  
**Owner:** Main Coordinator ONLY — No Agent A/B/C/Astra.  
**Machine/branch:** HUAWEI-MateBook / intel/duluka-capture  
**Status:** B6 receiver-side synthetic/offline fix **PASS**; B8 contract documented but **NOT completely solved**; **INTEL LIVE BLOCKED**.

## Reason for this work
I3-D Main accepted the synthetic-only evidence of independent QA (2,013 Passed/0 Failed/28 Skipped, historical to this phase). Outstanding B6: DXGI backend can emit ISourceGenerationLifecycleSink.SourceFault(kind, sourceGeneration), but BgraSessionSource only implemented old one-argument ISourceLifecycleSink.SourceFault(kind). An old producer's delayed fatal fault after an authorized generation recovery could poison the current BgraSessionSource. B8: frame ownership is ambiguous if an untrusted IVideoFrameSink accepts a frame and throws before returning PushOutcome.

## Main changes — preserve old public contract
1. `Video/Transport/DulukaEngine.Video.Transport.D3D11/BgraSessionSource.cs`: implements existing opt-in ISourceGenerationLifecycleSink; adds `SourceFault(kind, sourceGeneration)` comparing a positive incoming generation to the session's current generation under the exact same `gate` used by AuthorizeRecovery. Stale, zero, and future generation notifications are ignored without mutating fault or queue. Current generation enters same ApplyFaultLocked logic as old untagged SourceFault(kind). Old one-argument API remains for existing synthetic callers. This is a receiver-side logical check; real DXGI delivery has NOT been tested.
2. `QsvNative.Tests/I3B.cs`: 6 new synthetic checks: invalid zero/future faults rejected; current AccessLost accepted; stale DeviceRemoved rejected after recovery to generation 2; matching current terminal fault accepted; terminal fault prohibits second recovery; stopped session ignores stamped callback. Does not invoke real DXGI worker.
3. `Video/Contracts/DulukaEngine.Video/Contract/IVideoFrameSink.vb`: added explicit ownership contract documentation. Sink that returns Pushed/Replaced owns the frame; a sink MUST NOT accept and then throw without returning its PushOutcome. Throw before acceptance leaves frame producer-owned. **This is documentation, not implementation of an acknowledgment protocol and not proof all sinks obey it.**

All modified sources were snapshotted before change under `docs/reports/intel-duluka-i3e-2026-10-26/main-before/` with `main-edit-manifest.txt`. No existing uncommitted code was reset; no agent task file was touched.

## Source SHA-256 after Main edits
- BgraSessionSource.cs: `2AFE443D38DF788FB64E680BB67924F9141EFD370413CAC72DE132A846CD5D3F`
- QsvNative.Tests/I3B.cs: `E764D62D66C352F4CA705FC4C16F527296AA613294A48F502184C69423F0D42A`
- IVideoFrameSink.vb: `3D1075C98749DA04398E09743696E36A1E9B9509D8AB2141455AFCC85743CA44`

## Actual scoped build/test reruns — Main-only, 2026-10-26
All executed using dotnet Release on HUAWEI-MateBook; separate evidence under `main-evidence/`, exit code **0** for every executed stage:

| Stage | Result | Notes |
|---|---|---|
| QsvNative.Tests Release build | 0 warnings / 0 errors | `main-evidence/build-qsv.log` |
| QSV `--i3b` | **337 Passed, 0 Failed** | +6 new stamped lifecycle assertions; `qsv-i3b.log`, synthetic test-owned frames |
| QSV `--i3c` | **285 Passed, 0 Failed** | No real desktop/audio capture; `qsv-i3c.log`, new synthetic 60-frame VBR/CQP media under `media-i3c/` |
| I3DFrame.Tests Release build | 0 warnings / 0 errors | `build-frame.log` |
| I3DFrame.Tests fake-only | **27 Passed, 0 Failed** | `frame-tests.log`; validates throw-before-accept case but **not** unsafe accept-then-throw |
| DXGI Release build | 0 warnings / 0 errors | `build-dxgi.log` |
| Video Tests `--offline` | **38 Passed, 0 Failed, 28 Skipped** | `video-offline.log`; 28 real DDAGRAB hardware tests explicitly skipped |

**New Main-only verified total:** 687 Passed / 0 Failed / 28 Skipped across 4 test suites; not additive with I3-D historical 2,013 checks. I3-D Worker/B1 fake suites and I2/I3 transport were **not rerun** by Main in this phase; their prior C QA evidence remains a separate historical result. The 13 synthetic audio mux checks were **not run**. Main Release solution, physical driver hang, Exclusive capture, real screen/audio/mic, 1080p performance and real GPU shared NT handles are **not verified**.

**Git:** Branch intel/duluka-capture; `git diff --check` exit 0 with existing line-ending notices. Worktree had 81 dirty entries at inspection, including pre-existing uncommitted work. No commit, push, deploy, restart, OS settings change, or actual capture performed.

## Residual risks and policy
- **B6 receiver-side:** supported for producers using the stamped interface. The **legacy untagged method intentionally remains** for compatibility; without a producer generation it cannot independently reject an old callback. Future production wiring must use the stamped interface and forbid unstamped old-generation notifications.
- **B8:** still **OPEN** for malicious/buggy sinks that take ownership then throw without reporting PushOutcome. Blind double-dispose is unsafe; a negotiated ownership/acknowledgment contract or strict validated nonthrowing sink requirement is needed before allowing untrusted production sinks. FrameTransaction's prior tests cover throw-before-accept, not accept-then-throw.
- **B7:** keyed mutex/shared NT imported frames and foreign D3D11 devices are intentionally unsupported; exact native-device gate remains.
- **Driver hangs and process-level recovery:** still hardware-unverified. Quarantine deliberately retains native resources while stuck.
- **No live authorization:** `intel_native_live_input_not_verified` must remain CLOSED. No production DuplicateOutput/AcquireNextFrame, recording real Desktop/game pixels, microphone/system audio, screenshot, Fullscreen Exclusive, GPU setting change, or deployment.

## Final scope
**I3-E B6 synthetic/offline: MAIN ACCEPTED.**  
**I3-E B8: source contract clarified, unverified ambiguous-ownership case remains OPEN.**  
**Intel LIVE: BLOCKED / NOT AUTHORIZED.**  
All further engineering belongs to Main only unless the user explicitly changes ownership.
