# SPN-I3C-003 — Agent C Independent Integration QA

**Date:** 2026-10-25 (Asia/Bangkok)  
**Machine / branch:** HUAWEI-MateBook / `intel/duluka-capture`  
**Task / Owner:** SPN-I3C-003 / C  
**Result status:** QA COMPLETED — synthetic composition ACCEPTED recommendation; B1 worker lifecycle NEEDS FOLLOWUP; INTEL LIVE BLOCKED / NOT AUTHORIZED.  
**Acceptance authority:** Main Coordinator; this submission is not Main ACCEPTED.

## Scope, authorization and evidence
- Checked the local MateBook mailbox: both `SPN-I3C-001_A.done` and `SPN-I3C-002_B.done` existed before atomic Claim of `SPN-I3C-003_C.task`. Also reviewed separate `SPN-I3C-004_B.done` (B1 repair).
- Read A/B completion reports, B DXGI audit and B1 repair, `AGENTS.md`, accepted Session-Centric contract and Thai companion, I3-B handoff/report/source manifest. Independently examined the A composition driver/tests and B1 backend cleanup/test code, plus existing source lifecycle/transport requirements.
- Repo `git status --short` showed extensive pre-existing tracked/untracked I1–I3 and unrelated work. Do not attribute its whole diff to A/B/C. Canonical contract/Thai were already untracked (no modifications by C). `git diff --check` exit **0**, with two LF-to-CRLF notices on pre-existing `CaptureSession.vb` and `DxgiInventory.cs`.
- **No implementation source edited by C.** C wrote only this report and C-only logs/media in `docs/reports/intel-duluka-i3c-2026-10-25/C-evidence/`. Compilation regenerated ordinary scoped `bin/obj` outputs; no Git mutations, runtime deploy/restart, screenshot, microphone, system-audio, real Desktop/game acquisition, or display/GPU changes.

## Independent source/hash verification
Paths below are relative to `Project/NVIDIA Capture/EngineCapture/`. All current SHA-256 matched the respective final A or B1 completion report.

| Source | Current SHA-256 |
| --- | --- |
| `Duluka Capture/Recording/DulukaEngine.Recording/SourceCompositionDriver.vb` | `50F58328EF41EF46F24BCF9CEAC964833D0279D64A719986EB425B53FDBBF5FC` |
| `QsvNative.Tests/I3C.cs` | `8311405283003A13A941B66C963D396F2D414D9B73D144F79319040ECC33AFCF` |
| `QsvNative.Tests/I3B.cs` | `74203A5AEDD8BF864C420C934E5C748EBF847623EF423E4903A8661472AFE2A0` |
| `QsvNative.Tests/Program.cs` | `7EBEDA44FB3F899D241C5ED5E0D0A6D685F49D08706516188DC0C47E301E4839` |
| `Duluka Capture/Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend.vb` | `78F42FC2E448398DE68001D627EC001514229EDDE807EE3E70E395A433BF38D8` |
| `Duluka Capture/Tests/Video/B1WorkerQuiescence.Tests/Program.vb` | `6E845033B5B87933FF9A862709B19F240F2C8311A28E67CEEAF41C32565EFA7C` |
| `Duluka Capture/Tests/Video/B1WorkerQuiescence.Tests/B1WorkerQuiescence.Tests.vbproj` | `82BDF5AB812CBFFAA76DE270A22A701059011C395830C2E626CE93601542BC54` |

I3-B historical `source-manifest.json` contained **27 entries**. C's fresh comparison found exactly **3 changed entries**: `DdagrabBackend.vb` (B1 patch), `I3B.cs` and `Program.cs` (A additive test changes). The other **24 matched**. Evidence: `C-evidence/c-i3b-manifest-compare.log`. No unaccounted source-manifest drift was found at inspection time.

## Architecture and safety review
- `SourceCompositionDriver.Prepare` explicitly rejects producers where `IsTestOwnedSynthetic=False` with `intel_native_live_input_not_verified` and never initializes/calls a production Ddagrab worker. `PrepareSourceGeneration()` is a producer reservation interface, not `DuplicateOutput` or `AcquireNextFrame`. No production implementation/factory binding is provided in this phase. No C evidence shows actual Desktop/game pixels.
- Selected and producer targets must match exactly including the current validated output identity, device path, and source generation. The driver rechecks a fresh read-only DXGI inventory and distinguishes the snapshot-only `TopologyId` GUID from stable identity; its original producer epoch stays bound to subsequent frames.
- Test-owned producer pushes stamped D3D11 frames into existing bounded `BgraSessionSource -> BgraFrameAdapter -> owned NV12 -> SessionFrameRouter -> native QSV -> original SessionPacketSink/Mux -> verified MP4`. The original Session owns timestamps/PTS, encoder and mux; source owns neither MP4 nor audio. Tested failures reject wrong output, zero generation, stale topology, stale callbacks, and fatal removal without hidden source migration.
- `Run` and `Stop` cancel source/producer and terminate original engine; Dispose has a bounded session join and does not force native release on a wedged synthetic session. This is **NOT proof of production worker quiescence**. The `IsTestOwnedSynthetic` property is a construction convention, not a non-spoofable production security boundary; no live route may be exposed based on it.
- B1 source change prevents the known pre-exit forced cleanup on a still-alive worker at timeout; observed isolated fake-worker evidence rechecks no cleanup before worker release and cleanup-once after worker exit. This does not reproduce native COM/device-driver hangs.
- **Potential B1 final-state race requiring focused test/fix:** `DdagrabBackend.Dispose` can decide `workerExited=False` while `Thread.IsAlive=True` and then write `Quarantined`, even if `CompleteWorkerExit` has already completed cleanup and published `Disposed` while the worker thread is just returning. Existing three fake-worker tests do not force this precise interleaving. Source-level concern; **not experimentally reproduced**. It may leave stale status after clean exit, rather than prove premature cleanup.
- **Open LIVE blockers:** B2 lifecycle Start/Initialize/Stop/Dispose races; B3 acquired-frame ReleaseFrame/QI window; B4 shared texture/handle exception ownership; B5 duplication recreation stale wrapper/leak; B6 untagged old-generation fault/QPC; B7 NT/keyed mutex/foreign device unimplemented; B8 sink/crash/restart edges. Shared handles, cross-native-device CopyResource, arbitrary output switching and automatic hardware fallback remain forbidden. Main should assign separate ownership-controlled repairs and re-QA.

## C-run builds/tests — actual commands, results and evidence
All executed on HUAWEI-MateBook using `C:\Program Files\dotnet\dotnet.exe`; all test arguments were reviewed to ensure test-owned synthetic inputs and C-only evidence destinations. `QsvNative.Tests` is at `Project/NVIDIA Capture/EngineCapture/QsvNative.Tests/QsvNative.Tests.csproj`.

| Stage / actual CLI form | Passed | Failed | Skipped | Exit | Log |
| --- | ---: | ---: | ---: | ---: | --- |
| `dotnet build QsvNative.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -v:q` | N/A | N/A | N/A | **0** | `c-scoped-build.log` (0 warnings/0 errors) |
| `dotnet run --project QsvNative.Tests.csproj -c Release --no-build --no-restore -- --i3c --evidence-dir C-evidence` | **285** | **0** | **0** | **0** | `c-i3c-rerun.log` |
| Same command `--i3b --evidence-dir C-evidence/i3b-rerun` | **331** | **0** | **0** | **0** | `c-i3b-rerun.log` |
| Same command `--i3-transport --evidence-dir C-evidence/i3-transport` | **660** | **0** | **0** | **0** | `c-i3-transport-rerun.log` |
| Same command `--evidence-dir C-evidence/i2-native` (default native mode) | **659** | **0** | **0** | **0** | `c-i2-native-rerun.log` |
| `dotnet run --project B1WorkerQuiescence.Tests.vbproj -c Release --no-build --no-restore` | **3** | **0** | **0** | **0** | `c-b1-fake-worker-rerun.log` |
| C-owned JSON result + MP4 and elementary H.264 BT.709 tag audit, 4 profiles | **4 profiles verified** | **0** | **0** | **0** | `c-media-color-audit.log` |

**Independent totals newly executed by C:** **1,938 Passed / 0 Failed / 0 Skipped** across 1,935 QSV tests plus 3 B1 fake-worker scenarios; four media checks are additional checks on those runs and **not double-counted**. 
**Historical baseline only:** I3-B **2,056 Passed / 0 Failed / 33 Skipped** (retained original evidence); 1,650 of those checks independently rerun by C; 400 I1 and 6 media negative controls **Not Run by C**. Synthetic audio mux **13 Not Run** (not Passed and not Skipped). Full I3-B orchestrator/Main Release and live Desktop/Exclusive/physical driver-fault, audio, 1080p throughput tests **Not Run**.

## MP4/PTS/color evidence (C's fresh synthetic outputs)
Each of four new `C-evidence/composition-{vbr,cqp}-{limited,full}.mp4` clips has **60 submitted / 60 encoded / 60 muxed / 60 fully decoded**, 2-second duration, 30 fps MP4, original session state `Saved`, 60 test-owned D3D11 frames consumed, **0 screen frames / 0 audio samples**. Output JSON attests FullDecodeVerified and packet accounting; test independently checks moving-marker frames, source ownership, drained outstanding packets and native locks, cancellation and device-fault outcomes. No source callback generated a second session/encoder/mux.
The four `*.mp4-color.json` container probes AND `lifecycle-i3c-*.h264-color.json` elementary probes independently report BT.709 primaries/transfer/matrix with **tv** range for Limited, **pc** range for Full. C rechecked each pair and MP4 `nb_frames=60` from the emitted JSON; see `c-media-color-audit.log`. These are real fresh C reruns on synthetic textures and **not** physical capture or measured realtime performance. QPC/session PTS checks are exercised by the recorded test suites; C did not independently measure physical-display QPC timestamps.

## Final recommendations to Main
1. **I3-C synthetic producer/session composition: ACCEPTED recommendation** for exact synthetic scope, conditioned on Main's review of this report and C evidence; keep the live gate closed.
2. **B1 worker quiescence: NEEDS_FOLLOWUP / independent LIVE BLOCKED.** Three fake-worker scenarios pass, but test the possible Dispose-vs-worker-finalization state race and complete B2–B8 before considering production wiring. Do not interpret B1 synthetic checks as native COM-driver-hang verification.
3. **Intel LIVE CAPTURE: BLOCKED / NOT AUTHORIZED**. `intel_native_live_input_not_verified` remains closed; do not deploy/restart, capture real Desktop/game/audio or enable production DXGI without new Main authorization and privacy-cleared explicit scope.
4. No source modifications or runtime deployment by Agent C; stop at V2 `.done` checkpoint and await Main decision.

**Report completeness:** build/test result codes, source hashes, evidence paths, Not Run baseline, known blockers and acceptance dimensions are deliberately separated.