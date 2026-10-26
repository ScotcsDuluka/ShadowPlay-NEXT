# SPN-I3D-003 — Agent B / Frame Transaction & Resource Continuation

**Machine:** HUAWEI-MateBook · **Branch:** `intel/duluka-capture` · **Date:** 2026-10-26 (Asia/Bangkok) · **Task:** `SPN-I3D-003_B`
**Disposition:** **IMPLEMENTED / B-SCOPED SYNTHETIC TESTS PASS (27/27)**; integrated I3-D acceptance **PENDING Agent C/Main**.
**Hard gate:** `intel_native_live_input_not_verified` remains **CLOSED**. **NOT LIVE-READY**.

## Ownership and prerequisite checks

- Read the full original Mailbox V2 B task, I3D Coordinator Handoff, other agents' scopes, `AGENTS.md`, accepted canonical Session-Centric Recording Contract + Thai companion, I3-B handoff, I3-C independent QA, B original DXGI audit/B1 report, and current DXGI Worker/FrameTransaction/D3D11VideoFrame/source transport code.
- Main has explicitly PAUSED historical Astra; `SPN-I3D-001_Astra.working` remains untouched. Agent A `SPN-I3D-002_A.working` owns backend/worker/lifecycle sources; Agent B touched none of them. C cannot start until A+B are `.done`.
- Confirmed no other `*_B.working` task before atomic Claim using explicit `-BusRoot`. Examined MateBook branch, dirty Git status and active process list. Did not modify, delete or reset other agents' uncommitted changes.
- Exclusive source edits confined to `D3D11VideoFrame.vb` and `FrameTransaction.vb`; isolated new `Tests/Video/I3DFrame.Tests/` tests; report and B-only evidence. No DXGI project file, Backend/Worker, SourceLifecycle, QSV, Recording, A/C reports or existing shared Program/HardwareGate edits.

## Implementation — current source 1-based line evidence

**B3 (source helper + Worker compatibility):** `FrameTransaction.vb:15–46` remains a one-frame transaction: boolean successful acquire immediately arms a `Finally` invoking ReleaseFrame exactly once. QI/source timestamp/cursor/copy/frame construction all execute in `build()` under that guard. Only when release completes can the external `push(frame)` callback execute. Added fail-fast null delegate guards (`17–20`) and documented the native acquire delegate success contract. Confirmed READ-ONLY `DdagrabBackend.Worker.vb` sends AcquireNextFrame, BuildOwnedFrame and ReleaseFrame/desktop-resource cleanup in this exact order; no Worker source modifications.

**B4 (resource/handle transfer):** `FrameTransaction.vb:54–114` now validates builder callbacks and rejects a missing owned texture before transfer (`:103`). The existing `ResourceOwner` and `HandleOwner` detach on transfer; on prepare/copy/createHandle/construct failures, the resource owner disposes each acquired object and closes any untransferred NT handle once. The existing dropped staging texture is released prior to handing off shared texture on the optional shared branch. This change does **not** enable SharedHandle transport or keyed mutex.
- `D3D11VideoFrame.vb:69–79, 109–148, 224–258`: Public constructors still demand a real `ID3D11Texture2D`; they delegate to an internal `Friend` constructor accepting `IDisposable` and an injectable handle closer solely for device-free ownership fault tests. Real public path uses `CloseHandle` via `CloseNativeHandle`. Existing `Interlocked.CompareExchange` one-shot Dispose gate remains at `:224`; both texture release and handle close go through it. New injected closer used at `:238`. Even when fake texture Dispose throws, handle closure and OnDisposed callback continue.
- `FrameTransaction.vb:5–9`: `InternalsVisibleTo("I3DFrame.Tests")` makes direct static helper invocation available to dedicated offline test assembly, including correct CLR ByRef semantics for `DuplicationReplacement.Replace`. No project file or ABI changes.

**B8 (sink ownership):** `FrameTransaction.vb:35–46` leaves frame owned by consumer only for `Pushed` or `Replaced`, disposes at producer for `Dropped`, unknown enum outcomes or throwing sink. Releases duplication before external callback. This assumes a throwing sink did **not** irrevocably accept a frame before throwing; production `IVideoFrameSink` contract promises nonthrowing bounded queue behavior. If a sink accepts then throws before returning, ownership cannot be determined by this API: requires upstream protocol/task, NOT claimed fully solved.

**B5 generic fake validation:** `FrameTransaction.vb:154–175` `DuplicationReplacement.Replace` clears current before old disposal; replacement owned only after scoped factory success; temporary candidate disposed if scope exits by exception; scope closed on failure. Added validation of null delegates. **Agent A alone owns Backend call site** and is responsible for device/fault/bounded retry integration.

**B7 read-only compatibility:** Existing Intel `BgraFrameAdapter.cs` strictly rejects non-zero `SharedHandle` and `ResourceOptionFlags.Shared` / non-None misc flags; transport requires the same native ID3D11Device, not merely same LUID. No shared/keyed-mutex implementation, foreign-device import or production-wiring expansion by B. The shared branch above is tested only with purely synthetic fake disposables/handles and is **NOT** an Intel supported data path.

## Files and SHA-256 evidence

Relative to `Project/NVIDIA Capture/EngineCapture/Duluka Capture/`:

| B-owned file | SHA-256 at submission |
|---|---|
| `Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/D3D11VideoFrame.vb` | `1CD850120A4B90396890FAF38CAF6F9DF25BD015C89A892B0C29E7AEA0CCBB63` |
| `Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/FrameTransaction.vb` | `C5D02FF64B79E53FE573651B54CB1D0113AA8C39587DD07D728CA75AA194FBB4` |
| `Tests/Video/I3DFrame.Tests/Program.cs` | `1B604A25550C7716719D459FE1BC96527BBECE96DC0FEFF11C18075FB6A54348` |
| `Tests/Video/I3DFrame.Tests/I3DFrame.Tests.csproj` | `8F9CA1FB2914B187EFF80A27175D97FCC3AAE6593869634D5D68AD28ECE3E0FF` |

`D3D11VideoFrame.vb` was already modified by prior project work, `FrameTransaction.vb` existed as untracked Astra work. All changes are additive continuation on current dirty source; **do not infer this entire Git diff represents B-only changes**. `git diff --check -- D3D11VideoFrame.vb` exit 0. Source files and tests are not staged/committed/pushed.

## Actual build and test results — MateBook, synthetic-only

| Operation | Passed | Failed | Skipped | Exit | Evidence |
|---|---:|---:|---:|---:|---|
| `dotnet build I3DFrame.Tests.csproj -c Release --no-restore -v:q` | Build succeeded | 0 errors | N/A | **0** | `B-evidence/b-scoped-build.log`: 0 warnings / 0 errors |
| `dotnet run --project I3DFrame.Tests.csproj -c Release --no-build --no-restore` | **27** | **0** | **0** | **0** | `B-evidence/b-frame-fake-test.log` |
| `git diff --check -- D3D11VideoFrame.vb` | PASS | 0 | N/A | **0** | read-only Git check |
| Independent merged DXGI Release / A Worker tests | **NOT RUN BY B** | — | — | N/A | Agent A is concurrently implementing and C performs independent integrated QA; B's scoped test project builds its DXGI project dependency |
| Historical I3-B / I3-C suites | **NOT RERUN BY B** | — | — | N/A | I3-B 2,056/0/33 and I3-C 1,938/0 are historical only |
| Synthetic audio mux 13 tests | **NOT RUN** | — | — | N/A | Out of B frame ownership |
| Real DXGI Desktop/Exclusive, system or microphone audio, screenshots, physical device faults | **NOT RUN / NOT AUTHORIZED** | — | — | N/A | Hard gate CLOSED |

The 27 final PASS cases cover: no acquired frame, immediate release after four synthetic QI/time/construct/cursor faults, release-before-sink ordering, Pushed/Replaced/Dropped outcomes, throwing and unknown PushOutcome, release failure, missing callbacks, direct/shared fake builder resource transfers, eight independent builder fault stages, concurrent `D3D11VideoFrame.Dispose` ownership, throwing fake texture Dispose, and three duplication replacement paths (success/factory fail/scope Dispose fail). The harness **uses only fake .NET objects and mock native handle values**, never calls Win32 CloseHandle with a fake handle, constructs DXGI COM devices, reads desktop pixels or drives Ddagrab Initialize/Start/AcquireNextFrame/DuplicateOutput.

**Test development note (truthfulness):** Initial harness run was **25 passed / 2 failed** because reflection Invocation did not reliably propagate CLR `ByRef` arguments back after a thrown exception; this was a test-invocation artifact. Changed the B-owned `Friend` visibility and invoked `DuplicationReplacement.Replace` directly, then rebuilt and reran: **27 passed / 0 failed / 0 skipped, exit 0**. The earlier failed run was superseded, not represented as a production bug or silently counted as a pass.

### Reproduce (PowerShell, HUAWEI-MateBook)

```powershell
$root = 'C:\My Project\NVIDIA-Shadowplay'
$proj = Join-Path $root 'Project\NVIDIA Capture\EngineCapture\Duluka Capture\Tests\Video\I3DFrame.Tests\I3DFrame.Tests.csproj'
dotnet build $proj -c Release --no-restore -v:q
dotnet run --project $proj -c Release --no-build --no-restore
```

## Unresolved cross-agent integration / safety

1. **B1/B2** backend worker quiescence, atomic Dispose state, Start/Initialize lease and shutdown races: Agent A-owned, not edited/validated by B.
2. **B3** mock `Acquire(True)` release coverage is verified, but physical DXGI failure semantics and exact live GPU lifetime are unverified.
3. **B4** synthetic texture/handle ownership verified, actual NT CloseHandle / COM release with GPU driver and keyed mutex unverified; callback-after-sink-accept-then-throw edge remains ambiguous.
4. **B5/B6** Backend recreation call site, generation-stamped fault callbacks / QPC bound are Agent A-owned and need C's independent verification; B only fake-tested generic replacement.
5. **B7** Intel shared handle/keyed mutex and foreign-device routes intentionally unsupported; do not enable.
6. **B8** complete crash/restart/callback delivery requires Agent A and independent C QA.
7. Build outputs are shared artifact paths; the B scoped build passed at recorded run while A was editing disjoint source. Independent C should rebuild after both A/B `.done`, not infer A's pending work already passes.
8. No changes to Intel native QSV ABI 3, canonical session contract, other agents' files or OS/runtime. `intel_native_live_input_not_verified` remains CLOSED.

**Requested checkpoint:** Agent B submits `SPN-I3D-003_B.done` with this report appended. `.done` means completed submission only, **NOT** Main ACCEPTED. Agent C may independently QA only after both A and B are `.done`. No follow-up task self-created.
