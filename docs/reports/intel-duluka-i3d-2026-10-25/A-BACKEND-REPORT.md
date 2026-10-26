# SPN-I3D-002 — Agent A Backend/Worker Continuation Report

Date: 2026-10-26 (Asia/Bangkok)
Machine: HUAWEI-MateBook
Branch: intel/duluka-capture
Disposition: IMPLEMENTED; scoped synthetic/offline regression PASS, submitted for independent Agent C/Main QA.
Intel live readiness: BLOCKED / NOT AUTHORIZED — intel_native_live_input_not_verified remains CLOSED.

## Authorization, routing, and ownership

Received SPN-I3D-002_A.task on the HUAWEI-MateBook authority bus C:\My Project\ShadowPlay-NEXT-AgentBus.
Read full task, I3D-COORDINATOR-HANDOFF.md, Agent B/C task ownership, Mailbox README, AGENTS.md, accepted Session-Centric Recording Contract and Thai companion, Intel I2/I3-B handoffs, I3-C B DXGI audit/B1 repair/C independent QA, current owned code and read-only shared dependencies.
Verified branch intel/duluka-capture and no active overlapping Astra source editor, then atomically claimed SPN-I3D-002_A.working via AgentMailbox.ps1 with explicit -BusRoot.
SPN-I3D-001_Astra.working remains PAUSED and UNTOUCHED. Agent B independently owns D3D11VideoFrame.vb, FrameTransaction.vb, I3DFrame.Tests; B completed SPN-I3D-003_B.done before A's final integrated build. Agent C must wait for both .done states.
Preserved existing uncommitted Intel worktree. No Git reset/checkout/clean/add/commit/push; no runtime Deploy/Restart; no AORUS/NVENC/Intel native QSV ABI edits, no display/GPU config changes.

## Implementation — own source only

Relative root: Project/NVIDIA Capture/EngineCapture/Duluka Capture/

Modified: Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend.Lifecycle.vb.
- Continued and preserved Astra's B1/B2 operation-lease design and terminal Disposed/Quarantined logic instead of rewriting source.
- External lifecycle fault callbacks now acquire a counted callback lease while admitted under _sync. FinishLifecycle cannot claim native cleanup until initialization/start operation count is zero, worker has relinquished its native-use lease, AND admitted callbacks have unwound.
- Same _quiescent event is reset on callback admission; Stop and Dispose await quiescence within existing 2s and 5s budgets, respectively, without joining or waiting under _sync.
- Callback-thread self-wait avoidance is tracked PER backend with a counted Dictionary(threadId -> depth), rather than a global ThreadStatic flag which could incorrectly skip waits on unrelated backend instances. Nested callbacks unwind their own depth.
- Dispose executes the final quiescence check/state publication inside Finally even if the offline BeforeDisposeCompletion test hook throws. Disposed cannot regress to Quarantined: publication is guarded by the same _sync state lock, and exactly-once cleanup is preserved.
- Existing callbacks are still rejected before admission for _disposed/_stopSignal/disabled, wrong _runId or wrong _sourceGeneration. Throwing consumer callbacks fail the worker closed as Faulted and prevent further callbacks.
- This does not claim a live driver hang is safely recoverable: a permanently blocked worker remains quarantined and retains native resources, intentionally avoiding use-after-release.

Modified: Tests/Video/B1WorkerQuiescence.Tests/Program.vb.
- Corrected the inherited fake-worker fixture to declare _workerQuiescent=False and reset _quiescent when a synthetic fake worker is active. The Astra lease is independent of Thread.IsAlive; without this fixture setup the fake represented a nonexistent active-worker state. The three B1 original cases remain intact.

Added: Tests/Video/I3DWorker.Tests/I3DWorker.Tests.vbproj and Program.vb.
- Ten deterministic no-DXGI tests: stale (run,generation) rejection; reentrant Stop callback; blocked callback cleanup lease; Dispose-vs-worker-completion publication barrier; Initialize/Dispose; Start/Dispose; worker crash requiring rebuild; zero/equal/backward QPC and bounded 120-attempt synthetic retry budget; Stop during Initialize; successful fake Stop/Start with new run/generation and old-callback rejection; throwing lifecycle callback fails closed.
- Tests invoke injected internal lifecycle routines via reflection and use only in-memory .NET threads, events, fake sinks and exception generation. NEVER invoke production Ddagrab Initialize/Start/WorkerLoop/AcquireNextFrame/DuplicateOutput or open a D3D11 device.

Owned source with NO A modifications: DdagrabBackend.vb, DdagrabBackend.Worker.vb, SourceLifecycle.vb. Worker already calls B-owned FrameTransaction.Execute with acquire, BuildOwnedFrame, release, then PushOwnedFrame; B independently inspected and modified only its own helper/interface internals. No DXGI project file, shared Video Program.vb/HardwareGate.vb, B-owned FrameTransaction.vb or D3D11VideoFrame.vb edited by A.

## Source-level B1–B8 disposition

B1: OFFLINE-VERIFIED for once-only cleanup and Dispose final state, including forced interleaving after worker quiescence; physical COM/driver hangs NOT RUN. Disposed publication protected against Quarantined regression.
B2: OFFLINE-VERIFIED in controlled tests for Initialize/Dispose, Start/Dispose, Stop/Initialize, callback Stop and overlapping B1 Stop/Dispose; operation lease and _sync separation preserved. All possible native GPU timings remain unverified.
B3: SOURCE-MITIGATED by B-owned FrameTransaction.Execute (acquire success arms unconditional release guard; sink called after release); A reviewed Worker call-site compatibility; B 27 test cases are separate B evidence, not A's newly run counts.
B4: SOURCE-MITIGATED by B-owned ResourceOwner/HandleOwner/D3D11VideoFrame changes. Actual COM/NT handle and GPU driver behavior NOT RUN.
B5: SOURCE-MITIGATED: Worker RecreateDuplication invokes B-owned DuplicationReplacement.Replace; old wrapper cleared, temporary scope disposed by helper, ValidateRecoveryTarget revalidates original exact display, bounded retry capped at 120 or 30s. A offline-tested budget; fake scope exceptions tested separately by B. No silent LUID/output migration.
B6: OFFLINE-VERIFIED: explicit QPC invalid/equal/backward rejection (not synthetic timestamp clamping), run/source-generation callback admission gating, stale callbacks rejected after fake restart. An existing legacy ISourceLifecycleSink receiver, e.g. BgraSessionSource.cs, does not itself implement the optional ISourceGenerationLifecycleSink: backend serializes its own worker callback before recreation and now leases callbacks, but independent receiver-side generation enforcement remains recommended before live routing.
B7: OPEN / NOT IMPLEMENTED as designed. Intel current transport requires same native D3D11 device with no shared handle/keyed mutex/foreign-device import. No changes allowing unsupported paths.
B8: OFFLINE-VERIFIED for injected worker crash -> Faulted/rebuild required; safe fake Stop/Restart with fresh stamp; callback throw and reentrant Stop; B owns sink PushOutcome and frame ownership cases. An external sink that accepts a frame then throws without returning a PushOutcome remains contract-ambiguous and is NOT claimed solved.

## Build, test, and provenance — ACTUALLY EXECUTED

All commands below executed on HUAWEI-MateBook. Dotnet: C:\Program Files\dotnet\dotnet.exe, SDK 10.0.402.
A first isolated source snapshot was made at an ephemeral path recorded in A-evidence/snapshot-path.txt using robocopy; its outputs were NOT written into B's shared bin/obj while B was .working. Its build succeeded, 0 warnings/0 errors. Isolated final at that time: I3D worker 7/0, B1 3/0, Video --offline 38/0/28. An earlier 6 PASS / 1 FAIL I3D run was a fake-fixture setup error (workerQuiescent incorrectly left True after manually declaring fake Running); corrected the test lease model, reran and passed. Isolated results are not added to integrated totals.

After Agent B .done, compiled and ran directly in the actual uncommitted worktree, independently of the isolated copy:

1. dotnet build "Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DulukaEngine.Video.DXGI.vbproj" -c Release --no-restore -p:NuGetAudit=false -v:q
   Final EXIT 0; Build succeeded: 0 warnings, 0 errors.
   Evidence: A-evidence/a-final-dxgi-build-diff.log.
2. dotnet build "Tests/Video/I3DWorker.Tests/I3DWorker.Tests.vbproj" -c Release -p:NuGetAudit=false -v:q
   EXIT 0; 0 warnings, 0 errors. Evidence: A-evidence/a-integrated-build-and-worker.log.
3. dotnet run --project "Tests/Video/I3DWorker.Tests/I3DWorker.Tests.vbproj" -c Release --no-restore -p:NuGetAudit=false
   FINAL EXIT 0; I3D_WORKER_SUMMARY Passed=10 Failed=0 Skipped=0.
   Evidence: A-evidence/a-integrated-final.log.
4. dotnet run --project "Tests/Video/B1WorkerQuiescence.Tests/B1WorkerQuiescence.Tests.vbproj" -c Release --no-restore -p:NuGetAudit=false
   FINAL EXIT 0; B1 SUMMARY Passed=3 Failed=0 Skipped=0.
   Evidence: A-evidence/a-integrated-final.log and a-integrated-b1-video.log; B1 Release build 0 warnings/0 errors.
5. Audited HardwareGate.UseOfflineMode and Program.vb before running:
   dotnet run --project "Tests/Video/DulukaEngine.Video.Tests/DulukaEngine.Video.Tests.vbproj" -c Release -- --offline
   FINAL EXIT 0; Result: 38 passed, 0 failed, 28 skipped; all 28 live Ddagrab tests explicitly SKIPPED, never executed.
   Evidence: A-evidence/a-integrated-final.log.
6. git diff --check on dirty repository: EXIT 0. Git printed two pre-existing LF->CRLF warnings on unrelated CaptureSession.vb and DxgiInventory.cs; neither A-owned nor new diff errors.

NEW A-integrated test total (no double counting): 10 I3D + 3 B1 + 38 Video = 51 PASS, 0 FAIL, 28 SKIP. Release 0 warnings/0 errors. All final process exit codes 0.
Agent B reported 27/0 separate B I3DFrame tests; not rerun/countable as A runs. Historical I3-B 2056/0/33 and I3-C 1938/0 are NOT new A results. Prior synthetic audio mux 13 NOT RUN. Main Release / full Intel QSV media rerun NOT RUN. Live Desktop/game/Fullscreen Exclusive/audio/mic/screenshot/device-fault/1080p tests NOT RUN and NOT AUTHORIZED.

## SHA-256 final worktree (Windows Get-FileHash)

A-modified DdagrabBackend.Lifecycle.vb:
D5B669E6277E95B30BA3A0F7B38BFABB7D71E86A45AD1ABEFCF8BB5AA20B377F

A-modified B1WorkerQuiescence.Tests/Program.vb:
4E7F8F4DA765737E263D19187B7F6DF2DB4AEFD835D5FD1926D0CC2BCBDABACE

A-added I3DWorker.Tests/Program.vb:
7B812DCBE7F0B48A7D3B89ABA2EDCA6107AD28FA42B6E227E3E196B4A4B03AFF

A-added I3DWorker.Tests/I3DWorker.Tests.vbproj:
60D4A10FAE5FA0F52CE444AB053CB0E7B6D1D28B4A964C13E0E411C05F8DC978

Preserved Astra Backend.vb (not modified by A):
040B33458BABB574DD1807BECE461BE10B97C653C1EF58ADE2B30E24C265F44E

Preserved Astra Worker.vb (not modified by A):
ACA5C0F2597ADC3A30AB9F97DDC35C5A483C439FD6B9DAE412B7A507EB0F0062

Preserved SourceLifecycle.vb (not modified by A):
BF743E856DA67964B0E407FA91A1F164A2EC0025019DA5B05E891CCE0406420B

B-owned FrameTransaction.vb, checked after B .done:
C5D02FF64B79E53FE573651B54CB1D0113AA8C39587DD07D728CA75AA194FBB4

B-owned D3D11VideoFrame.vb, checked after B .done:
1CD850120A4B90396890FAF38CAF6F9DF25BD015C89A892B0C29E7AEA0CCBB63

Both B-owned hashes match SPN-I3D-003 B report at A's final check. All work left uncommitted.

## Remaining gates / request to Main

- Request Agent C's independent read-only I3D integrated review after both A and B are .done, especially callback admission/linearization and the BgraSessionSource legacy generation-stamp gap.
- Synthetic acceptance recommendation ONLY for scoped worker lifecycle and helper compatibility; this is not a claim that real COM/driver hangs, NT handle faults, physical device changes or real capture pass.
- Actual persistent GPU worker hang deliberately retains quarantine resources until the worker exits. No unsafe forced driver cleanup; process-level recovery plan remains OPEN.
- Never start a real Intel Desktop Duplication worker or enable the Intel live data path merely because these synthetic tests pass.
- No deployment/restart, Git commit/push or real screen/audio capture performed.
- Status .done represents SUBMITTED to Main, NOT ACCEPTED.

Agent A checkpoint: stop after Mailbox V2 Done, await Main acceptance or new Task-ID.
