# ShadowPlay NEXT — Agent A / I3-C1 Composition Report

- Task: **SPN-I3C-001**, Agent **A**, Machine **HUAWEI-MateBook**
- Branch: `intel/duluka-capture`
- Result: **IMPLEMENTED — SYNTHETIC ONLY; ready for Main/C independent review**
- Production readiness: **BLOCKED** by `intel_native_live_input_not_verified`; **LIVE NOT AUTHORIZED**
- Scope: producer/controller composition seam; no production DXGI start, no real display/game/audio/mic capture.
- Review input: I3-B 2,056 Passed / 0 Failed / 33 Skipped (historical baseline), synthetic audio mux 13 Not Run.

## Implemented

1. Added `Recording/DulukaEngine.Recording/SourceCompositionDriver.vb` with `ISourceCompositionProducer` and a one-shot controller. Accepts **only test-owned synthetic producer**; no production `DdagrabBackend` implementation or factory route is connected.
2. `Prepare` checks exact selected producer Target: adapter LUID/vendor/device, PCI path, output index, display device name, monitor handle/path, attached state, output technology, geometry, rotation and producer source epoch. It independently revalidates stable identity against read-only current `DxgiDisplayInventory.Collect()` before the metadata-only `PrepareSourceGeneration()` reservation; rejects zero/missing generation and unverified targets.
3. The producer's TopologyId is a **per-inventory-snapshot nonce**, because `DxgiDisplayInventory.Collect` assigns `Guid.NewGuid()` each call. Comparing it across independent Enumerations was a false mismatch; preserve the producer-selected epoch for frames, while rechecking all stable identity fields against fresh current DXGI topology. A topology migration requires new attestation/epoch, not arbitrary target switching.
4. Controller starts existing `RecordingEngine.StartSourceSession` with its bounded `BgraSessionSource`; injected producer starts only after reservation. Existing `BgraFrameAdapter -> owned NV12 -> SessionFrameRouter -> native QSV -> session packet sink -> MP4` remains the single output pipeline. The source never owns mux/encoder/audio.
5. Stop cancels queued/in-flight reads, stops test producer and notifies original engine; Dispose waits boundedly for the session to exit before freeing source/producer. A stuck session fails closed without forced COM release. No native ABI 3 or color descriptor changes.
6. Added `QsvNative.Tests/I3C.cs` with four end-to-end synthetic MP4 cases, cancellation/fault/identity/generation tests; registered `--i3c` in `Program.cs`. Extended existing I3B `VerifyColor` helper with an optional evidence directory, preserving its original default behavior. New runs are isolated under **A-evidence/**, not I3-B historical evidence.

## Changes / SHA-256 (final on this worktree)

All paths relative to `Project/NVIDIA Capture/EngineCapture/`.

| File | Status | SHA-256 |
| --- | --- | --- |
| `Duluka Capture/Recording/DulukaEngine.Recording/SourceCompositionDriver.vb` | Added | `50F58328EF41EF46F24BCF9CEAC964833D0279D64A719986EB425B53FDBBF5FC` |
| `QsvNative.Tests/I3C.cs` | Added | `8311405283003A13A941B66C963D396F2D414D9B73D144F79319040ECC33AFCF` |
| `QsvNative.Tests/I3B.cs` | Additive helper overload; baseline call unchanged | `74203A5AEDD8BF864C420C934E5C748EBF847623EF423E4903A8661472AFE2A0` |
| `QsvNative.Tests/Program.cs` | Additive test selector | `7EBEDA44FB3F899D241C5ED5E0D0A6D685F49D08706516188DC0C47E301E4839` |

No Ddagrab worker, native bridge, Session contract, NVENC or AORUS source changed by Agent A. Existing pre-task uncommitted changes preserved. Agent B exclusively owns DXGI worker audit report; Agent C QA task was pending during implementation.

## Build / test evidence (real execution)

Commands were run on MateBook in Release with installed .NET 10 and pinned existing dependencies. Exit code **0** for all final commands below. Reports and media are in `docs/reports/intel-duluka-i3c-2026-10-25/A-evidence/`.

| Stage | Command summary | Passed | Failed | Skipped | Exit |
| --- | --- | ---: | ---: | ---: | ---: |
| Scoped Release | `dotnet build QsvNative.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -v:q` (builds Recording dependencies) | n/a | n/a | n/a | 0; 0 warnings, 0 errors |
| New I3-C1 | `dotnet run --no-build -c Release -- --i3c --evidence-dir A-evidence` | **285** | **0** | 0 | **0** |
| I3-B source/lifecycle | `--i3b --evidence-dir A-evidence/i3b-rerun` | **331** | **0** | 0 | **0** |
| I3 transport/adapter | `--i3-transport --evidence-dir A-evidence/i3-transport` | **660** | **0** | 0 | **0** |
| I2 native QSV/session | default run with `--evidence-dir A-evidence/i2-native` | **659** | **0** | 0 | **0** |
| **Total actually rerun** | 4 executed test modes | **1,935** | **0** | **0** | all 0 |

- I3-C1 VBR and CQP × BT.709 Limited/Full: every case **60 submitted / 60 encoded / 60 muxed / 60 fully decoded**, session Saved, no screen frames or audio captured, H.264 elementary VUI and MP4 color tags verified, moving-marker decode verified. The test-owned GPU frame texture is disposed once; QSV delayed packets drain with zero locked/outstanding native surfaces.
- Negative cases: bad physical output before reservation, live producer refused, zero generation refused, stale monitor-path identity refused, stale callbacks remain owned by producer on drop, idle Stop -> Cancelled, device removal -> Failed and denies same-session recovery. Retained I3-B tests cover QPC-to-session PTS, generation change, queued frames, Stop during GPU conversion, lost-device callback, double disposal, encoder/session recovery.
- `git diff --check`: **exit 0**, two pre-existing LF->CRLF warnings for `CaptureSession.vb` and `DxgiInventory.cs`; no edits to those paths from this task.
- Initial intermediate compile failed with 3 VB `Stop()` keyword parsing errors; fixed to `Me.[Stop]()`. First intermediate synthetic run failed on cross-enumeration `TopologyId` GUID mismatch; corrected with stable-field revalidation as above. **Final build and all final listed tests pass.** The final I3-C mode was rerun after the last Stop/finally adjustment.

### Not Run / historical evidence (do not aggregate as new passes)

- Full I3-B multi-stage runner and the retained **I1 400 checks / 33 framework skips**: **Not Run** in this work turn, to avoid modifying shared historical I3-B/I1 evidence under concurrent uncommitted work.
- Retained **6 media negative-controls** as a separate historical stage: **Not Run** here.
- Synthetic audio mux **13 checks: Not Run** (not included in Passed or Skipped).
- Main Release solution: **Not Run** this turn; scoped Release verified.
- Real DXGI `DuplicateOutput/AcquireNextFrame`, physical driver removal/hang, GPU setting changes, real desktop/game/audio/mic, Fullscreen Exclusive, shared NT/keyed-mutex, HDR/scaling, long-run and 1080p throughput: **Not Run / not authorized**.
- Baseline **2,056 Passed / 0 Failed / 33 Skipped** is historical I3-B acceptance, NOT a rerun. Of that baseline, **1,650** assertions were rerun here; **406** historical baseline assertions were not rerun. Adding the **285** new I3-C checks gives **1,935** *actually executed* checks this turn; it is not a replacement for independent baseline/QA validation.

## Important limits and remaining gates

- `IsTestOwnedSynthetic` is only a temporary construction gate for this unconnected seam, **not** a trusted production security boundary. No production bridge is exposed or activated. Live enablement needs a new Main-authorized task and a non-spoofable route gate.
- The current DXGI worker still needs Agent B's independent shutdown/join/COM fault audit. The synthetic producer does not reproduce real driver hangs, device removal, display modes, copied texture contention or sharing of foreign native D3D11 devices.
- TopologyId in current inventory is an ephemeral snapshot GUID, not persistent CCD monitor identity. Future authorized production wiring must use actual initialized producer target/epoch, revalidate stable CCD identity and reject stale callbacks; never compare snapshot GUIDs as physical identity.
- The synthetic controller assumes producer promises correct frame ownership and cooperative Stop. Unsafe production Stop/worker hangs must never be bypassed by freeing resources prematurely.
- Encoder and packet-mux compatibility remains private ABI 3; no new native build required for this code. No implicit ABI migration or encoder fallback.
- Live acceptance gate `intel_native_live_input_not_verified` **remains closed**.

## Main handoff / next recommended step

1. Review Source Hashes, four MP4 results, H.264 probes, A logs and the original Session-Centric contract. Verify source diff against concurrent worktree.
2. Wait for **SPN-I3C-002_B.done** and review worker-hang risks and exact producer generation/target lifetime before any live work.
3. Permit Agent C to run independent QA only after both A/B .done exist, as specified in C's task. Separate decisions: *synthetic composition* vs *Intel live readiness*.
4. Do **not** authorize live Intel producer, deployment, audio, game capture or automatic Desktop/Exclusive fallback on this report alone.

**Agent A submits this report for Main ACCEPTED / NEEDS FIX / BLOCKED decision; completion does not imply acceptance.**
