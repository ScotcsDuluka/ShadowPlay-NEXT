# SPN-I3C-002 — Agent B DXGI Worker / Resource Lifetime Audit

**Machine:** HUAWEI-MateBook · **branch observed:** `intel/duluka-capture` · **date:** 2026-10-25
**Scope:** Static source-only audit. **Result:** AUDIT COMPLETED; **LIVE READINESS BLOCKED**.
**Hard gate:** `intel_native_live_input_not_verified` MUST REMAIN CLOSED.
No code edits, builds, production DXGI worker, AcquireNextFrame, screenshots, recording, audio, GPU-setting changes, deployment, restarts, Git mutations, or physical driver-fault tests were performed for this task.

## Read ownership, evidence and scope

Read `AGENTS.md`, accepted Session-Centric Recording Contract and Thai companion, `docs/INTEL-DULUKA-CAPTURE-I3B-HANDOFF.md`, I3-B report, this task and the A/C task ownership declarations. Inspected the actual DXGI worker, frame wrapper, source lifecycle and sink contracts, source/adapter/transport and I3-B test source on MateBook. Git status reports a **dirty** worktree on `intel/duluka-capture` (existing tracked/untracked I1–I3-B and unrelated edits); preserved it. Agent A owns Recording/Transport/QsvNative.Tests implementation and its A report; Agent C owns independent QA. Agent B writes only this file.

**Relative source prefixes:**
- `DXGI/` = `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/`
- `Contracts/` = `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/Contracts/DulukaEngine.Video/Contract/`
- `Transport/` = `Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/`
Source citations below use physical 1-based lines in current working tree, not historical Git revisions. Risks marked **confirmed by source** are structural control-flow/ownership hazards, not demonstrated physical failures; **hypothesis** requires fault injection or live verification.

## BLOCKERS — prioritized

### B1 CRITICAL — COM cleanup while worker may still execute (confirmed by source)

**Evidence:** `DXGI/DdagrabBackend.vb:692-750` sets `_disposed=True`, joins the worker for 2+3 seconds, explicitly logs `worker still alive after 5 s — disposing COM resources anyway` at line 736, changes state to Disposed and calls `CleanupPersistentResources()` (lines 752–754, 1247–1260) regardless of worker exit. Meanwhile WorkerLoop uses `_duplication/_device/_deviceContext/_output` for AcquireNextFrame, CopyResource, ReleaseFrame and recreation (lines 879–1153, 1198–1245). The 2-second `Stop` timeout leaves state Stopping at lines 665–677, which is safer than restarting but does **not** make Dispose safe.

**Impact:** Worker can call into released COM wrappers/driver, causing use-after-release/native process fault or deadlock. Ordinary synthetic I3-B tests never exercise driver-held worker and cannot certify this. **Minimal separate-task fix:** on failed join, quarantine backend and **retain all native resources until confirmed worker exit**; do not advertise disposed/stopped resources as reusable, do not start a second worker, surface terminal shutdown timeout. Define bounded controller behavior/process-isolation recovery policy rather than forced in-process Release. **Synthetic plan:** injected blocking worker point (Acquire/Copy/Map mock), Stop timeout then Dispose; assert no native resource freed until worker release and no restart; verify stop state. **Live authorization:** not for synthetic injection; physical driver-hang acceptance requires explicit separate authorization.

### B2 HIGH — Stop/Dispose, Start/Dispose, Initialize/Dispose races (confirmed by source)

**Evidence:** `DdagrabBackend.vb:640-689` sets Stopping inside `_sync` but writes `_stopSignal=True` *after unlocking* (line 663). `Dispose` on Stopping (lines 713–723) assumes that signal has been raised, then performs the potentially unsafe cleanup above. `Start` sets Starting under lock (559–574), then sets sink/recreates duplication outside the lock (578–600); Dispose treats Starting as stoppable with a possibly null `_workerThread` (702–724), and can free native state while Start still recreates. `Initialize` does lengthy DXGI work after Initializing state (289–558) while Dispose has no Initialize-in-flight coordination (692–756). A second concurrent Stop returns immediately for Stopping (649–652) without waiting for first Stop's worker exit; nonvolatile signal writes/read synchronization are asymmetric (662 vs 884–888).

**Impact:** Conflicting lifecycle operations can release/replace native resources while initialization or acquisition is in flight, and Stop may return before producer quiescence. **Minimal fix:** unify state transitions, signal publication and active operation lease under one lifecycle gate; reserve teardown until Start/Initialize/worker have acknowledged completion; make repeated Stop await the same shutdown task. **Synthetic plan:** barriers at each named state transition + concurrent Start/Stop/Dispose/Initialize; assert single worker, deterministic states, zero premature cleanup. **Live authorization:** no for injected tests; live DXGI teardown validation separately gated.

### B3 HIGH — Acquired-frame ReleaseFrame can be skipped; release occurs after sink callback (confirmed by source)

**Evidence:** `DdagrabBackend.vb:926-929` may successfully acquire; `desktopResource.QueryInterface` runs at line 998 but `frameAcquired=True` only at line 999. If QI throws, Finally at 1128–1155 does **not** call ReleaseFrame. Logging, timestamp conversion and allocation preparation in lines 957–994 also occur **before** the frame-scoped Try/Finally begins. ReleaseFrame is at 1150, but `_sink.TryPush` runs at 1081, so success path holds duplication frame across external sink callbacks, contrary to the own top-of-file promise (lines 18–23). An injected exception in the pre-try interval may bypass release entirely.

**Impact:** Outstanding acquired frame can poison subsequent AcquireNextFrame; callback stalls prolong duplication lock and complicate Stop/Dispose. **Minimal fix:** enter an acquired-frame guard immediately after success; always attempt ReleaseFrame exactly once on that guard, including QI/timestamp faults; copy/composite required DXGI-dependent data, release desktop resource + frame **before** calling sink, preserving staging texture ownership. **Synthetic plan:** mock Acquire successful then QI/timestamp/cursor/sink faults; assert one ReleaseFrame and no callback before release. **Live authorization:** no for mocks; physical DXGI path not authorized.

### B4 HIGH — Raw texture / NT handle cleanup is ambiguous on exceptions (confirmed by source; consequence requires test)

**Evidence:** `DdagrabBackend.vb:1030-1058` creates a shared texture and `CreateSharedHandle`, transfers its wrapper into `D3D11VideoFrame` at 1067–1076 but retains nonnull `sharedTexture/stagingTexture` locals. Catch (1106–1126) disposes `frame` AND these same resource wrappers; shared staging is then disposed again in Finally (1138–1145). If constructor/handle creation fails before a frame owns `sharedHandle`, catch/finally have no CloseHandle for that local. `D3D11VideoFrame.vb:200–234` disposes its owned texture/NT handle exactly once **through the frame**, but cannot guard a separate backend Dispose on the same wrapper. `_texturesDisposed` callback counts one frame-wrapped resource, not necessarily all shared allocations; accounting is noncanonical in mixed paths.

**Impact:** double COM-release attempts and NT handle leak in exception windows; inaccurate leak metrics. **Minimal fix:** explicit single ownership variables/try-finally: clear raw locals upon transfer, separately close orphan NT handle, count each physical texture once; normalize Replaced/Dropped even on throwing sink. **Synthetic plan:** fault inject at shared resource creation, handle creation, frame construction, TryPush and callback, assert one release/CloseHandle per owner and balanced metrics. **Live authorization:** no for synthetic resource fakes; actual NT share/keyed mutex protocol requires new task and separate live approval.

### B5 HIGH — RecreateDuplication failure holds stale disposed wrapper and may leak temporary COM refs (confirmed by source)

**Evidence:** `DdagrabBackend.vb:1198–1245` disposes `_duplication` at 1211 but does not set it to Nothing before trying `output1.DuplicateOutput`; `output1.Dispose()` at 1220 only runs on success. Failure catches all and calls `NotifySourceFault(Disconnected)` (1230), increments retry counter and returns until 120 failures. The worker's `If _duplication Is Nothing` gate (915–922) can miss a non-null **disposed** object left by a failed replacement. Initialize's first `output1` creation/retry also has release only on success (520–535).

**Impact:** use of stale disposed duplication wrapper, leaking IDXGIOutput1 COM references over retries, and transient access loss escalated to terminal Disconnected before evidence of true disconnect. **Minimal fix:** use local `Using output1`, set `_duplication=Nothing` immediately after old release, assign only a fully valid new duplication, classify error/target loss accurately, terminalize after bounded attempts. **Synthetic plan:** fake DuplicateOutput fails for first N calls then succeeds; verify null state, balanced release counts, correct AccessLost vs Disconnected signals. **Live authorization:** not for mock; device removal/hang not certified.

### B6 HIGH — Old-generation lifecycle callbacks are untagged; QPC recovery can terminalize (source-confirmed interface risk)

**Evidence:** `Contracts/SourceLifecycle.vb:3–12` only sends `SourceFault(kind)`, no source generation or producer incarnation; `DdagrabBackend.vb:102–105, 937–951, 1160, 1230` forwards to current `_sink` without generation check. `BgraSessionSource.cs:76–97` transitions on that untagged event and forbids a second recovery. Frame-generation checks in `BgraSessionSource.cs:62–70, 131, 148` **do** reject stale frames, but cannot identify a late fault callback from an old producer. `DdagrabBackend.vb:961–993` clamps backward source present QPC (potentially preserving equality) and `BgraSessionSource.cs:140–150` rejects QPC <= lastQpc even after authorized generation transition (only sequence resets at line 94).

**Impact:** late old-generation callback may fault a new healthy generation; equal/stale QPC on a recreated duplication may force InvalidFrame rather than a bounded timestamp-gap policy. **Minimal fix:** producer/controller generation-stamped fault envelope + callback invalidation; validate QPC monotonicity before handing off with an explicit fault, not by silently clipping to identical stamps. Respect session-owned PTS; never reset epoch or compress long gaps. **Synthetic plan:** race delayed callbacks with generation 7→8, equal/backward raw QPC, output change, stale callbacks after Stop; assert old callbacks ignored, new faults delivered, gap fails truthfully. **Live authorization:** no for mocks; real mode-switch matrix separately gated.

### B7 MEDIUM/HIGH — shared NT / keyed-mutex path is NOT an Intel Frame Adapter capability (confirmed restriction)

**Evidence:** `DdagrabBackend.vb:138–162, 231–239, 1040–1057` optionally creates Shared/SharedNthandle texture and NT handle, without a demonstrated keyed mutex handshake. `D3D11VideoFrame.vb:73–78, 173–181, 200–221` wraps it. `Transport/BgraFrameAdapter.cs:57–68` requires zero SharedHandle and `ResourceOptionFlags.None`; `Transport/BgraStagingTransport.cs:80–101` likewise rejects shared handle and any foreign native D3D11 device, **even on same LUID**. `BgraFrameAdapter.cs:72–99` requires preexisting multithread protection and serialized Enter/Leave. Transport's Copy/Map has bounded DoNotWait polling and finally Unmap (`BgraStagingTransport.cs:104–136`).

**Impact:** enabling shared producer mode will be rejected by current Intel path; same-LUID foreign-device CopyResource is invalid, and no OpenSharedResource1/AcquireSync/ReleaseSync integration is certified. **Minimal fix:** Agent A composition must configure direct, unshared source-device textures only and reject foreign-device imports. Future sharing requires a separate ownership/handle/synchronization protocol and tests. **Synthetic plan:** positive same native-device case, negative sharedHandle, same-LUID distinct device, no multithread protection and mismatched descriptor; no accidental GPU copy. **Live authorization:** no for synthetic negative gates; real shared-handle path separately authorized.

### B8 MEDIUM — frame-drop, callback and restart edges need deterministic coverage (hypotheses + source)

**Evidence:** `Contracts/IVideoFrameSink.vb:8–25` defines nonblocking TryPush; `BgraSessionSource.cs:55–74` refuses Dropped without taking ownership, drains accepted queue on Stop/Fault (`69–85,100`) and consumer disposes dequeued raw frame in finally (`156`). `DdagrabBackend.vb:1081–1127` transfers only Pushed/Replaced and disposes Dropped. `DdagrabBackend.vb:1160–1186` converts unexpected worker crash into Stopped (allowing future Start) after notifying Disconnected, not a durable Faulted state. `D3D11VideoFrame.vb:200–234` guards its own Dispose, but does not guarantee upstream refs are independent or that `OnDisposed` setter races safely with callback invocation.

**Impact:** sink throw/unknown PushOutcome, crash/restart without session fault reconciliation, and external callbacks during shutdown need explicit proof. Current source-receiver queue discipline looks sound for ordinary Pushed/Dropped, but COM lifetime isn't. **Minimal fix:** exhaustive outcome/default handling, explicit restart authorization only after producer/sink generation reconciliation, stable callback detach and worker-exit notification. **Synthetic plan:** each PushOutcome including exception/unknown, blocked callback, queue-full, Stop interleave, injected worker crash, delayed callback and restart; exact once-dispose assertions. **Live authorization:** no for fakes; real driver races separately gated.

## Failure-mode map / allowed recovery

| Trigger | Observed code behavior | Risk / required policy |
|---|---|---|
| `DXGI_ERROR_WAIT_TIMEOUT` | `DdagrabBackend.vb:931–936` increments NoFrame; continues | Not proof of captured video; deadline/first-frame policy belongs session. |
| `DXGI_ERROR_ACCESS_LOST` / mode change / session switch | `937–945` faults source AccessLost and recreates | Only controller-approved bounded SAME target/device/new generation; no automatic new display/session/clock. |
| Disconnected/removed/reset | `948–955` classifies some HRESULTs; target revalidation at `1216` | DeviceRemoved/Disconnected terminal for existing session; no GPU/device silent replacement. |
| DuplicateOutput transient failure | `1210–1245` retry path labels Disconnected | B5: classify HRESULT/topology, clear invalid COM wrapper and honor bounded timeout. |
| In-flight ReleaseFrame/QI/Copy/Map exception | `998–1152`; transport `104–136` | B1/B3/B4: cleanup at every acquired-frame/transfer edge; no unsafe force release. |
| Worker blocked in driver lock | `Stop 640–688`, `Dispose 692–756` | B1: forced cleanup unsafe; no physical driver-hang coverage in I3-B. |
| Stale QPC/generation | `961–993`; `BgraSessionSource.cs:88–97, 140–150` | Require monotonic QPC/sequence, explicit gap handling, never synthesize success by timestamp clamp. |

## Agent A integration compatibility requirements (no edits to A files)

1. Keep `intel_native_live_input_not_verified` CLOSED; no production `DuplicateOutput/AcquireNextFrame` or OS output creation under I3-C synthetic task.
2. Initialize/revalidate target and only while backend idle call `PrepareSourceGeneration()`; construct `BgraSessionSource` from exactly that target/generation before Start. Do not assume a reservation starts capture.
3. Explicitly require direct/no-shared-handle `D3D11VideoFrame`, same native `ID3D11Device` (not merely same Adapter LUID), unrotated eligible output and multithread-protected context. Reject cross-device CopyResource and any shared NT handle/keyed mutex expectations.
4. Session/controller owns one start/stop intent, calls source Stop to cancel reads, awaits consumption, then disposes **only after backend worker exit**. Until B1/B2 repair do not invoke backend Dispose as a hang recovery workaround; return blocked/faulted and retain native object until safely quiescent.
5. Bind lifecycle faults to current source generation; never route stale frames/callbacks, never auto-select a different GPU/output. Allow at most one authorized AccessLost recovery; DeviceRemoved/Disconnected/OutputChanged are terminal. Preserve original session encoder/mux/clock only if compatible, otherwise fail truthfully.
6. Agent A may implement synthetic controller integration but **must not fix Ddagrab source inside A ownership**, loosen the hard gate, or claim live-safe Stop/Dispose based on fake queue tests.
7. Submit B1–B6 as distinct Main-authorized DXGI producer/contract repair task(s) and use Agent C independent review to determine synthetic acceptance separately from LIVE readiness.

## Validation and evidence limitations

- **Agent B this task:** read-only source inspections and SHA-256 hashes; **Build: Not Run. Tests: Not Run. Passed: 0 newly executed; Failed: 0 newly executed; Skipped: 0 newly executed**. No exit-code claims for unexecuted builds/tests; Git status and file-hash commands completed as read-only checks.
- **Historical I3-B evidence (not rerun by B):** I3-B handoff + `docs/reports/intel-duluka-i3b-2026-10-25/README.md` report **2,056 Passed / 0 Failed / 33 Skipped**, 13 synthetic audio mux checks **Not Run**, scoped Release 0 warnings/0 errors and main Release 3 existing warnings. `QsvNative.Tests/I3B.cs:13–84,109–126` uses synthetic owned source textures and injected AccessLost/device faults; it does **not** start legacy Ddagrab worker or reproduce a real driver wedge. These reported numbers are **historical**, not Agent B measured output.
- Real driver removal/hang, Fullscreen Exclusive, real Desktop/game pixels, audio, 1080p/long-run throughput and cross-device shared NT-keyed mutex are **not verified**.

### Source SHA-256 (observed by Agent B; working-tree copies)

| Path suffix | SHA256 |
|---|---|
| `Contracts/IVideoCaptureBackend.vb` | `996D8C5CA0CC88CD43758D01D1F7B3DA5A37E21F076C8590252D816FD4528D95` |
| `Contracts/IVideoFrameSink.vb` | `90953A7F84760706107D7F7C581CB252C283A3E70517417E9C91A8915B683897` |
| `Contracts/SourceLifecycle.vb` | `7F90FA213F97065B1E664FD9496A3085818FA2DAD2872CF3AE107CFAE5E5E85F` |
| `DXGI/D3D11VideoFrame.vb` | `3C247619A8A1B37E10D9A4C1B647CC0BAFB97EDFBADDE03443BB16050B78AB23` |
| `DXGI/DdagrabBackend.vb` | `3905D2F8A6B3A6291478551215CF2FAFB4994C7558936BE05217353A5EDECF8C` |
| `Transport/BgraFrameAdapter.cs` | `37AF3E796CAF32EE02F215485D8306131CDAFB60707A16B389BDEB2F6347B20C` |
| `Transport/BgraSessionSource.cs` | `64F1E2032A90DA0E0EE42522D673EBC5E35B379317D656FEDFC40829F5D91061` |
| `Transport/BgraStagingTransport.cs` | `8C93E86E7D70A113979B25ECB86AFB5A9F775D0E929F42FFB0B830BF4724185A` |

## Disposition

**Agent B submission: COMPLETED_WITH_BLOCKERS (LIVE BLOCKED).** Audit/document completed as authorized; no proposed source fixes implemented or tested. Main must assign separate source-ownership repair tasks for B1/B2/B3/B4/B5/B6 before even considering an explicitly scoped, privacy-cleared live DXGI acceptance test. Agent C can review Agent A synthetic integration independently, but an I3-C synthetic PASS must never be misreported as Intel Live Capture authorized.
