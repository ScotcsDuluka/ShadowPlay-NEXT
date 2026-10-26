# ShadowPlay NEXT — dual-worktree status (25 October 2026)

> **Snapshot, not a deployment or synchronization command.** These are two authorized, independently inspected computers and branches. Source, media tests, deployed binaries and runtime behavior differ. This README update does not merge or transfer source between them.

## Separate machines, separate evidence

| Host | Branch | Ownership | Current documented proof | Remaining acceptance |
|---|---|---|---|---|
| **HUAWEI-MateBook** | `intel/duluka-capture` | Intel GPU/output identity, D3D11 BGRA staging, native Intel QSV H.264, shared Duluka RecordingSession | I3 transport **1,453/0/33**, frame Adapter **1,725/0/33**, I3-B synthetic **2,056/0/33**, I3-C Agent C independently rerun **1,938/0/0** (synthetic acceptance recommendation pending Main) | `intel_native_live_input_not_verified`; no actual live Desktop/game capture, real loopback/microphone audio, 1080p throughput or Fullscreen Exclusive transition |
| **AORUS-DESKTOP** | `Stable` | NVIDIA Capture/runtime, OSC/CEF Host, Hook/Adapter, Launcher/Container/NodeAPI | D3D9 Floating Point x86 external Windowed GPU visual evidence; CEF 73 isolated GPU pixel tests; Video/Advanced Capture UI v77 checks | DX9 Hook 60 FPS stability, CEF fresh-frame >60 bounded production fix, native Capture+audio true end-to-end media and multi-game stability |

**Do not read `intel/duluka-capture` as merged into AORUS `Stable`.** The public repository's Stable branch and the two machines' local worktrees can represent different revisions.

## MateBook source of truth

- [Canonical Session-Centric Recording Contract](architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) · [Thai companion](architecture/SESSION-CENTRIC-RECORDING-TH.md): one Duluka SessionId, clock, audio plan, encoder, packet sink, mux and verified output; Desktop/Exclusive source APIs are independently selected only when wired.
- [MateBook Capture subsystem and architecture map](../Project/NVIDIA%20Capture/README.md) · [native Intel QSV backend](../Project/NVIDIA%20Capture/EngineCapture/Duluka%20Capture/Encoding/Intel/README.md) · [I3 D3D11 staging/Adapter](../Project/NVIDIA%20Capture/EngineCapture/Duluka%20Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/README.md).
- [I3 1,453-check synthetic transport report](reports/intel-duluka-i3-2026-10-25/README.md) · [I3 1,725-check borrowed-frame composition report](reports/intel-duluka-i3-composition-2026-10-25/README.md) · [readme coverage inventory](README-COVERAGE-MATEBOOK-20261025.md).

### Intel I3-B complete synthetic proof; I3-C reviewed synthetic proof — live still CLOSED

The newer [I3-B implementation report](reports/intel-duluka-i3b-2026-10-25/README.md) documents **2,056 passed / 0 failed / 33 skipped**, retaining the former 1,725-case composition baseline and adding 331 lifecycle, clock, source ownership, recovery and BT.709 Limited/Full assertions. **13 synthetic-audio mux cases were not rerun**, and I3-B did not start live desktop/game/audio acquisition. The earlier BC30002 build-error log was an intermediate attempt, **not the final I3-B outcome**. I3-B also updates the private Intel QSV native ABI **2 → 3**; paired managed/native artifacts are mandatory.

I3-C [Agent A's scoped source/session composition report](reports/intel-duluka-i3c-2026-10-25/A-COMPOSITION-REPORT.md) records **1,935 passed / 0 failed / 0 skipped** *rerun in that task*, and [independent Agent C QA](reports/intel-duluka-i3c-2026-10-25/C-INDEPENDENT-QA.md) records **1,938 passed / 0 failed / 0 skipped** including 3 fake-worker B1 checks; four verified synthetic MP4 profiles are not double-counted. Agent C **recommends ACCEPTED for synthetic composition**, but **Main acceptance is separate and not established here**. [Agent B's B1 quiescence repair](reports/intel-duluka-i3c-2026-10-25/B-WORKER-QUIESCENCE-REPAIR.md) removed one known unsafe timeout cleanup path, but the independent QA flags a possible worker-finalization state race and B2–B8 remain outstanding. The production Intel live gate **`intel_native_live_input_not_verified` is still CLOSED**.

## Guardrails for the next integration

1. Preserve **all uncommitted work** and existing first-party/vendor dependency licenses. No cross-machine file overwrite or Git merge/push as a side effect of documentation.
2. Retain the physical Intel target identity (trusted LUID/output/generation, non-shared D3D11 resource ownership, borrowed upstream frame vs owned NV12) and native QSV's bounded packet/flush semantics. I3B source changes are not automatically a verified production Desktop composition.
3. CBR remains rejected unless independently verified with full decode; Intel driver QSV implementation API 1.34 and pinned dispatcher code version 2.16.0 are not interchangeable.
4. Any first live screen/system audio/microphone/game test requires an explicitly approved privacy-cleared scope. Worktree-level synthetic tests do not grant live testing permission.
5. Use the completed I3-B 2,056/0/33 report and C's independent I3-C 1,938/0/0 QA as synthetic evidence only. Obtain Main's scoped acceptance decision; resolve B1 final-state race and B2–B8 before any separately authorized live tests or NVIDIA/Intel integration. AORUS Hook, CEF, NVENC and UI must each retain independent regression gates.

**Status:** documentation-only comparison. No production recordings, runtime restart, staging, cross-machine code transfer, Git commit/push or changes to the other machine were performed while authoring this MateBook document.