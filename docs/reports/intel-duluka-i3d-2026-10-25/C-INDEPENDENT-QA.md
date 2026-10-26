# SPN-I3D-004 — Agent C Independent QA

**Date:** 2026-10-26 · **Machine:** HUAWEI-MateBook · **Branch:** `intel/duluka-capture`  
**Agent:** C · **Task:** SPN-I3D-004 · **Disposition:** INDEPENDENT QA COMPLETED — Synthetic/Offline ACCEPT recommendation, Intel LIVE BLOCKED.  
**Final authority:** Main Coordinator; `.done` is a submitted report, not acceptance.

## 1. Authorization / safety
- Verified `SPN-I3D-002_A.done` and `SPN-I3D-003_B.done` in the authoritative MateBook mailbox, then claimed `SPN-I3D-004_C.task` atomically with explicit `-BusRoot`. Read both complete A/B .done reports and I3D Coordinator Handoff; `SPN-I3D-001_Astra.working` remains PAUSED/untouched.
- Read `AGENTS.md`, accepted Session-Centric Recording Contract plus Thai companion, I3-B handoff, I3-C Independent QA, DXGI Worker Audit and B1 Worker report. Reviewed A lifecycle and worker source, B FrameTransaction and D3D11VideoFrame, offline test harnesses and no-live test selection.
- C made **no production source code edits**, changed no A/B reports or Astra mailbox files, and performed no Git add/commit/reset/clean/checkout/push, deployment or runtime restart. No real Desktop Duplication, real display/game pixels, screenshot, fullscreen game, microphone/system audio, driver-fault injection or GPU/display setting changes.
- `intel_native_live_input_not_verified` **remains CLOSED**; SourceCompositionDriver still rejects non-test synthetic producers. Native Capture/Live is **NOT AUTHORIZED**. Only read-only target enumeration and test-owned synthetic QSV were exercised.

## 2. Working-tree and source evidence
- `git branch --show-current`: `intel/duluka-capture`. `git status --porcelain` showed **79 dirty entries**, including existing unrelated Intel/source/docs edits. C did not modify or resolve concurrent changes; attribution of the entire Git diff to A/B would be false.
- `git diff --check`: **exit 0** (two existing LF-to-CRLF notices for unrelated `CaptureSession.vb`, `DxgiInventory.cs`). This check does not validate untracked-file diffs.
- Astra's `before/DdagrabBackend.vb` SHA was `78F42FC2E448398DE68001D627EC001514229EDDE807EE3E70E395A433BF38D8`; current `DdagrabBackend.vb` SHA `040B33458BABB574DD1807BECE461BE10B97C653C1EF58ADE2B30E24C265F44E` corresponds to retained Astra partial restructuring per A report, NOT an A-only change. Astra before `D3D11VideoFrame.vb` SHA `3C247619A8A1B37E10D9A4C1B647CC0BAFB97EDFBADDE03443BB16050B78AB23`; current is B-final SHA below.
- Independently recomputed and compared the following final **SHA-256** values on the actual MateBook worktree. All match A/B final reports. Root for video entries: `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/`.

| Source | SHA-256 |
| --- | --- |
| `DdagrabBackend.vb` (retained Astra, A read-only) | `040B33458BABB574DD1807BECE461BE10B97C653C1EF58ADE2B30E24C265F44E` |
| `DdagrabBackend.Lifecycle.vb` (A) | `D5B669E6277E95B30BA3A0F7B38BFABB7D71E86A45AD1ABEFCF8BB5AA20B377F` |
| `DdagrabBackend.Worker.vb` (A read-only) | `ACA5C0F2597ADC3A30AB9F97DDC35C5A483C439FD6B9DAE412B7A507EB0F0062` |
| `FrameTransaction.vb` (B) | `C5D02FF64B79E53FE573651B54CB1D0113AA8C39587DD07D728CA75AA194FBB4` |
| `D3D11VideoFrame.vb` (B) | `1CD850120A4B90396890FAF38CAF6F9DF25BD015C89A892B0C29E7AEA0CCBB63` |
| `Video/Contracts/DulukaEngine.Video/Contract/SourceLifecycle.vb` (unchanged A scope) | `BF743E856DA67964B0E407FA91A1F164A2EC0025019DA5B05E891CCE0406420B` |
| `Tests/Video/B1WorkerQuiescence.Tests/Program.vb` (A) | `4E7F8F4DA765737E263D19187B7F6DF2DB4AEFD835D5FD1926D0CC2BCBDABACE` |
| `Tests/Video/I3DWorker.Tests/Program.vb` (A) | `7B812DCBE7F0B48A7D3B89ABA2EDCA6107AD28FA42B6E227E3E196B4A4B03AFF` |
| `Tests/Video/I3DWorker.Tests/I3DWorker.Tests.vbproj` (A) | `60D4A10FAE5FA0F52CE444AB053CB0E7B6D1D28B4A964C13E0E411C05F8DC978` |
| `Tests/Video/I3DFrame.Tests/Program.cs` (B) | `1B604A25550C7716719D459FE1BC96527BBECE96DC0FEFF11C18075FB6A54348` |
| `Tests/Video/I3DFrame.Tests/I3DFrame.Tests.csproj` (B) | `8F9CA1FB2914B187EFF80A27175D97FCC3AAE6593869634D5D68AD28ECE3E0FF` |

This confirms report-to-current-file consistency, not that all dirty/untracked source history was independently attributable.

## 3. B1–B8 independent disposition

| Risk | Scoped result | QA reasoning / remaining gate |
| --- | --- | --- |
| **B1** Dispose vs worker exit cleanup and terminal state | **FIXED / OFFLINE-VERIFIED** | State publication uses shared `_sync`; `FinishLifecycle` requires zero active operations/callbacks and worker quiescence; fake interleaving recheck confirms Disposed never regresses to Quarantined and cleanup claimed once. Real COM/driver hangs **NOT RUN**. |
| **B2** Start/Initialize/Stop/Dispose races | **OFFLINE-VERIFIED** | Operation and callback leases and per-instance reentrant Stop safeguard examined; fake tests cover major lifecycle interleavings. Exhaustive native thread/driver timing **NOT RUN**. |
| **B3** Acquire-success ReleaseFrame guard | **FIXED / OFFLINE-VERIFIED** | `FrameTransaction.Execute` arms release immediately after successful acquire, builds owned copy inside guard, invokes sink after release; B's 27-case harness independently rerun. Actual DXGI failure behavior **NOT RUN**. |
| **B4** Texture / NT handle ownership | **SOURCE-MITIGATED + OFFLINE-VERIFIED** | `ResourceOwner`, `HandleOwner`, and one-shot `D3D11VideoFrame.Dispose` retain exactly-once fake-object transfer/disposal; true driver COM/CloseHandle failures **NOT RUN**. |
| **B5** Duplication recreation and target | **SOURCE-MITIGATED / OFFLINE-VERIFIED** | `DuplicationReplacement.Replace` clears old reference and disposes temporary factory scope, while Worker validates original physical output and bounds retries (120 attempts/30 sec); real hotplug/faults **NOT RUN**. |
| **B6** Old generation callback and QPC | **OFFLINE-VERIFIED, LIVE GAP** | Worker rejects equal/backward QPC; lifecycle callback gates on run+generation. Existing `BgraSessionSource` is a legacy sink and does not implement optional `ISourceGenerationLifecycleSink`; receiver-side independently stamped fault handling before live wiring needs followup. |
| **B7** Shared NT/keyed mutex / foreign device | **OPEN / INTENTIONALLY UNSUPPORTED** | Existing Intel BGRA adapter requires exact native device and no shared handle; no new import/sync capability is claimed or authorized. |
| **B8** Sink exception / crash/restart | **OFFLINE-VERIFIED with OPEN contract edge** | Throwing fault callback faults producer; fake worker crash requires rebuild, sink Drop/Replace/unknown outcomes tested. A sink that internally accepts a frame and *then throws before returning its PushOutcome* has ambiguous ownership; requires contract clarification and negative-case test before live use. |

Additional caution: production native cleanup may encounter exceptions not represented by fake disposables or the successful cleanup-claim assertions. Long-running driver hangs intentionally remain Quarantined; process-level recovery policy is **NOT VERIFIED**. Source improvements are not live-hardware certification.

## 4. C-only independent build/test results

Execution host HUAWEI-MateBook; runtime `C:\Program Files\dotnet\dotnet.exe`. All results below were **actually rerun by C after A/B .done**, logs in `docs/reports/intel-duluka-i3d-2026-10-25/C-evidence/`. No prior agent scores are included in these C totals.

| Command (project location abbreviated under Duluka Capture) | Passed | Failed | Skipped | Exit | C-evidence |
| --- | ---: | ---: | ---: | ---: | --- |
| `dotnet build Video/DXGI Desktop Duplication/.../DulukaEngine.Video.DXGI.vbproj -c Release --no-restore -p:NuGetAudit=false -v:q` | N/A | N/A | N/A | **0** | `build-dxgi.log`; 0 warnings/0 errors |
| `dotnet build Tests/Video/I3DWorker.Tests/I3DWorker.Tests.vbproj -c Release --no-restore -p:NuGetAudit=false -v:q` | N/A | N/A | N/A | **0** | `build-i3dworker.log`; 0 warnings/0 errors |
| `dotnet build Tests/Video/I3DFrame.Tests/I3DFrame.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -v:q` | N/A | N/A | N/A | **0** | `build-i3dframe.log`; 0 warnings/0 errors |
| `dotnet build EngineCapture/QsvNative.Tests/QsvNative.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -v:q` | N/A | N/A | N/A | **0** | `build-qsv.log`; 0 warnings/0 errors |
| `dotnet run --project I3DWorker.Tests.vbproj -c Release --no-build --no-restore` | **10** | 0 | 0 | **0** | `i3dworker.log` |
| `dotnet run --project I3DFrame.Tests.csproj -c Release --no-build --no-restore` | **27** | 0 | 0 | **0** | `i3dframe.log` |
| `dotnet run --project B1WorkerQuiescence.Tests.vbproj -c Release --no-build --no-restore` | **3** | 0 | 0 | **0** | `b1worker.log` |
| `dotnet run --project DulukaEngine.Video.Tests.vbproj -c Release --no-build --no-restore -- --offline` | **38** | 0 | **28** | **0** | `video-offline.log`; DDAGRAB real tests skipped |
| `dotnet run --project QsvNative.Tests.csproj -c Release --no-build --no-restore -- --evidence-dir C-evidence/media-qsv-i2` | **659** | 0 | 0 | **0** | `qsv-i2.log` |
| Same QSV with `--i3-transport --evidence-dir C-evidence/media-qsv-i3transport` | **660** | 0 | 0 | **0** | `qsv-i3transport.log` |
| Same QSV with `--i3b --evidence-dir C-evidence/media-qsv-i3b` | **331** | 0 | 0 | **0** | `qsv-i3b.log` |
| Same QSV with `--i3c --evidence-dir C-evidence/media-qsv-i3c` | **285** | 0 | 0 | **0** | `qsv-i3c.log` |

**Fresh C execution total: 2,013 Passed / 0 Failed / 28 Skipped**; 8 test suites, all exited 0. Four scoped Release builds also exited 0, each 0 warnings and 0 errors. Historical I3-B `2,056 / 0 / 33` and I3-C `1,938 / 0` are **historical reference** only, not added to the new count.
**NOT RUN:** 13 synthetic audio mux assertions, I1 baseline full rerun, historical six separate media negative controls, full Main Release solution, real DXGI acquisition/driver hang, screenshot/audio/mic/Exclusive capture, shared resource import, HDR, real throughput and 1080p tests.

## 5. Synthetic MP4 / timeline / color validation

C freshly ran I3-C synthetic session tests and emitted four new `C-evidence/media-qsv-i3c/composition-{vbr,cqp}-{limited,full}.mp4` files. All four report **60 submitted / 60 encoded / 60 muxed / 60 fully decoded** with verified `Saved`, 30 fps × 2 seconds, no real Desktop frames and zero audio samples. These are *test-owned synthetic frame textures* only; QSV work does not prove physical source acquisition or realtime throughput.
C independently inspected each `composition-*.json`, `composition-*.mp4-color.json`, and `lifecycle-i3c-*.h264-color.json`; the four VBR/CQP × Limited/Full combinations have BT.709 primaries/matrix/transfer and correct `tv`/ `pc` range in **both** MP4 and H.264 elementary metadata. Frame tests and session verifier exercise source QPC-to-session PTS, count, moving markers and delayed flush. C's separate four-profile audit `c-i3d-media-audit.log` exits 0; these profile checks are supplementary and **not added to the 2,013 test total**.

## 6. Main disposition / future work

**Recommended:** `I3-D SYNTHETIC/OFFLINE: ACCEPTED` for exactly the tested scope and maintained gated code, subject to Main verification of hashes and logs.  
**Required followup before LIVE:** B6 receiver-side stamped-fault contract, B8 ambiguous sink ownership on accept-then-throw, driver COM/NT handle and quiescence evidence, process-level hang/recovery policy, strict same-native-device/unshared path, and explicit privacy-cleared real-device acceptance approval.  
**Intel LIVE: BLOCKED / NOT AUTHORIZED.** Never open `intel_native_live_input_not_verified` or deploy/restart based on these results.  

**Checkpoint:** Agent C submits this report in `SPN-I3D-004_C.done` and ceases work until a new Main-authored Task-ID.