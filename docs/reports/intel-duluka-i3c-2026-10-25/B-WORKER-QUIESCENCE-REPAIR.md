# SPN-I3C-004 / Agent B — DXGI Worker Quiescence B1 Repair

**Executed:** 2026-10-25 · **Machine:** HUAWEI-MateBook · **branch:** `intel/duluka-capture`
**Task status:** IMPLEMENTED + SCOPED SYNTHETIC TESTED, **MAIN ACCEPTANCE PENDING**.
**Live Intel authorization:** **NOT AUTHORIZED**. `intel_native_live_input_not_verified` remains CLOSED.
**B1 conclusion:** Native cleanup on the *known still-alive worker* timeout path is removed. The fix is source-level and fake-worker verified; actual driver-hang behavior remains unverified, and B2–B8 are independently open.

## Authorization, ownership and isolation

- Read SPN-I3C-004 original task, SPN-I3C-002 full `.done`/audit, `AGENTS.md`, the canonical Session-Centric Recording Contract and Thai companion, I3-B handoff/report and actual backend source. Checked `git status` (already dirty with pre-existing Intel and unrelated work), mailbox and Agent A/C ownership before Claim. No other B `.working` existed; Claim performed on MateBook.
- Agent B changed **only** `Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend.vb`, added isolated fake worker tests below and this B-only report/evidence. Agent A's Recording, Transport and QsvNative.Tests untouched.
- NO Git add/reset/clean/checkout/commit/push, runtime deployment/restart, configuration/display change, AORUS edit, live Desktop/game/audio/mic capture, screenshot, or production Ddagrab Initialize/Start/AcquireNextFrame. The general offline runner was invoked with `--offline` after confirming `HardwareGate.UseOfflineMode()` skips live Ddagrab probe and all `DDAGRAB` tests.

## Source change and reasoning (1-based current lines)

| Area | Source evidence | B1 fix |
| --- | --- | --- |
| Cleanup ownership | `DdagrabBackend.vb:70, 753–756` | New Interlocked one-shot cleanup claim; both joined Dispose and worker exit use `CleanupPersistentResourcesOnce`. |
| Dispose wait / quarantine | `695–751` | Set stop request, join worker at most 2s+3s outside `_sync`. On still-alive worker, state becomes `Quarantined`, no COM cleanup, bounded return and explicit error log. |
| Normal exit | `695–748` | When worker already exited/joined, state `Disposed` and cleanup once, preserving normal release flow. |
| Worker exit handoff | `881–885, 1159–1195` | WorkerLoop now has an outer `Finally` calling `CompleteWorkerExit`, even if worker fault callback/logging throws. It calls cleanup only if Dispose was requested and publishes final `Disposed` after cleanup. |
| Retry / idempotence | `695–700, 753–756, 1289` | Repeat Dispose cannot double-clean; `ThrowIfDisposed` blocks all new Start, while Quarantined and after exit. New enum value is appended, retaining prior enum ordinals. |
| Initialize partial cleanup | `549–558` | Failure paths use same once-only cleanup guard, avoiding a later Dispose double-clean attempt. |

**Invariant:** While Worker.IsAlive in a Dispose timeout scenario, `CleanupPersistentResources` is not called by Dispose. All COM/driver objects remain retained until the worker exits and its finalization path runs. No unbounded joining and no Thread.Abort are introduced. Permanent driver hang intentionally leaves a quarantined backend/resources for process-lifetime recovery: this is safer than freeing in-use COM resources, but is *not* a live driver-hang recovery solution.

**Unverified B2 limit:** Start's setup/recreation and Initialize run outside the full lifecycle lock; Dispose racing before worker creation is still unsafe to certify. This B1 patch does not claim to resolve these independent Start/Initialize races or all resource lifetime edges. Need a separate Main-owned B2 authorization for active initialization/start leases, and separate physical-hang/restart safety approval.

## Files changed / added (exclusive B scope)

1. Modified: `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend.vb`.
2. Added: `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Tests/Video/B1WorkerQuiescence.Tests/B1WorkerQuiescence.Tests.vbproj`.
3. Added: `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Tests/Video/B1WorkerQuiescence.Tests/Program.vb`.
4. Added: `docs/reports/intel-duluka-i3c-2026-10-25/B-WORKER-QUIESCENCE-REPAIR.md`.
5. Added: `docs/reports/intel-duluka-i3c-2026-10-25/B-evidence/{b1-build-release.log,b1-fake-worker.log,video-offline-regression.log}`.

### SHA-256 source evidence

| File | SHA-256 after B1 |
|---|---|
| `DdagrabBackend.vb` | `78F42FC2E448398DE68001D627EC001514229EDDE807EE3E70E395A433BF38D8` |
| `B1WorkerQuiescence.Tests/Program.vb` | `6E845033B5B87933FF9A862709B19F240F2C8311A28E67CEEAF41C32565EFA7C` |
| `B1WorkerQuiescence.Tests/B1WorkerQuiescence.Tests.vbproj` | `82BDF5AB812CBFFAA76DE270A22A701059011C395830C2E626CE93601542BC54` |

B's preceding SPN-I3C-002 audit recorded the then-current `DdagrabBackend.vb` hash `3905D2F8A6B3A6291478551215CF2FAFB4994C7558936BE05217353A5EDECF8C`, before this authorized B1 change. Git diff includes pre-existing uncommitted code, so a full diff against repository HEAD must **not** be attributed to B alone. No upstream sources were overwritten/reset.

## Builds and actual test results

| Actual execution | Passed | Failed | Skipped | Exit | Evidence |
|---|---:|---:|---:|---:|---|
| DXGI backend Release build | N/A | 0 build errors | N/A | **0** | `B-evidence/b1-build-release.log`; **0 warnings / 0 errors** |
| Isolated B1 fake-worker harness Release build | N/A | 0 build errors | N/A | **0** | `B-evidence/b1-build-release.log`; **0 warnings / 0 errors** |
| B1 fake-worker tests (3 explicit scenarios) | **3** | **0** | **0** | **0** | `B-evidence/b1-fake-worker.log` |
| Existing Video Tests **--offline** | **38** | **0** | **28** | **0** | `B-evidence/video-offline-regression.log`; 28 live Ddagrab cases skipped |
| `git diff --check` on owned DXGI source | check passes | N/A | N/A | **0** | read-only CLI inspection |
| Full Intel I3-B regression / native media suite | **Not Run** | **Not Run** | **Not Run** | N/A | Existing I3-B evidence intentionally NOT rewritten while Agent A owns concurrent source; historic 2,056/0/33 is not a new B run |
| Synthetic audio mux 13 tests | **Not Run** | **Not Run** | **Not Run** | N/A | Explicitly outside B1 scope |
| Real worker/driver hang, Desktop/Exclusive, mic/system audio, 1080p throughput | **Not Run** | **Not Run** | **Not Run** | N/A | Prohibited without separate authorization |

**B1 scenarios actually run:** (1) blocked fake worker, five-second Dispose budget, Quarantined, zero pre-exit cleanup, Start rejected, exactly one post-exit cleanup; (2) normal worker exit, once-only cleanup; (3) overlapping Stop/Dispose with blocked fake worker, no premature cleanup and eventual Disposed. The harness uses a plain .NET fake thread and calls the same private finalization method as WorkerLoop via reflection. It **never creates a DXGI device or COM resource**. It verifies cleanup *dispatch/ownership*, not physical COM/driver survival under a real hang.

### Reproduction commands (PowerShell on HUAWEI-MateBook)

```powershell
$root = 'C:\My Project\NVIDIA-Shadowplay'
$cap = Join-Path $root 'Project\NVIDIA Capture\EngineCapture\Duluka Capture'
$dxgi = Join-Path $cap 'Video\DXGI Desktop Duplication\DulukaEngine.Video.DXGI\DulukaEngine.Video.DXGI.vbproj'
$b1 = Join-Path $cap 'Tests\Video\B1WorkerQuiescence.Tests\B1WorkerQuiescence.Tests.vbproj'
$video = Join-Path $cap 'Tests\Video\DulukaEngine.Video.Tests\DulukaEngine.Video.Tests.vbproj'
dotnet build $dxgi -c Release --no-restore -v:q
dotnet build $b1 -c Release --no-restore -v:q
dotnet run --project $b1 -c Release --no-build --no-restore
dotnet run --project $video -c Release -- --offline
```

No run of the full I3-B script: its existing evidence paths are outside B's exclusive ownership, and Agent A has concurrent uncommitted I3-C integration work. The synthetic B1/Video regression evidence above is separately reproducible without changing A's files.

## Residual risks / escalation (not claimed fixed)

- **B2 HIGH:** Concurrent Initialize/Start/Stop/Dispose active-operation races, especially Dispose-before-worker-creation; **NEEDS NEW TASK**.
- **B3 HIGH:** AcquireNextFrame success vs ReleaseFrame try/finally window; **NEEDS NEW TASK**.
- **B4 HIGH:** Shared NT handle and staging texture exception ownership/double release; **NEEDS NEW TASK**.
- **B5 HIGH:** Stale disposed duplication wrapper / temporary IDXGIOutput1 leaks after failed recreation; **NEEDS NEW TASK**.
- **B6 HIGH:** Untagged old-generation fault callbacks / stale QPC; **NEEDS NEW TASK**.
- **B7 HIGH:** Shared NT/keyed mutex not supported by Intel BgraFrameAdapter, same-LUID foreign-device copy forbidden; **NO LIVE WIRING**.
- **B8 MEDIUM:** Sink exceptions / crash-restart/queue ownership edge coverage; **NEEDS FOLLOWUP**.
- **B1 live-only verification:** synthetic fake worker proves safety guard only; real driver lock/unrecoverable GPU hang and true COM lifetime behavior remain **NOT VERIFIED**. A permanently hung worker can intentionally hold COM until process exit, requiring process-level lifecycle strategy.

## Recommendation to Main and Agent A/C

- Agent B recommends **B1 SYNTHETIC FIX ACCEPTANCE SUBJECT TO MAIN/C INDEPENDENT REVIEW**, but **Intel LIVE remains BLOCKED**.
- Agent A should not interpret this as permission to start production DXGI. It must preserve Session/encoder/mux ownership, exact target/generation, direct same-device frame restrictions, and the closed live gate.
- Main should issue separate authorization for B2 and, when ready, B3–B8. Only after those gates, independent reviews and privacy-cleared live acceptance can physical driver behavior be evaluated.
- Agent B will stop after mailing completion, pending Main acceptance/revision. No other task is self-assigned.
