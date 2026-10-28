# ShadowPlay NEXT — dual-worktree status (25 October 2026)

> **Snapshot, not a synchronization instruction.** Read from authorized AORUS-DESKTOP and HUAWEI-MateBook worktrees separately. They are not the same branch, checkout or runtime. This document **does not assert that either was pulled, merged, committed, deployed or functionally synchronized**.

## Machine and branch ownership

| Machine | Local branch | Current owner work | Strongest reviewed evidence | Explicitly not certified |
|---|---|---|---|---|
| **AORUS-DESKTOP** | `Stable` | Launcher, OSC/CEF native Host, NodeAPI/Container, Duluka Hook, NVIDIA Capture and Portal | D3D9 Windowed external GPU overlay *visually observed* in Floating Point x86; 50/50 focused UI/Hook source checks from the 25 October README audit; Capture UI v77 source/build hashes 4/4 and partial CEF Advanced geometry | Smooth in-game D3D9 Hook 60 FPS; general DX9Ex GPU selectable production route; full native Capture+audio end-to-end recording |
| **HUAWEI-MateBook** | `intel/duluka-capture` | Intel hardware identity, D3D11 BGRA staging/Frame Adapter, native QSV encoder and shared Duluka recording session | I3 transport **1,453/0/33**, I3 frame Adapter **1,725/0/33**, I3-B synthetic **2,056/0/33**, Agent C independent I3-C synthetic QA **1,938/0/0**, pending Main acceptance | Actual live Intel Desktop/Exclusive capture, real audio, unrestricted native QSV CBR, all colorimetry, independent AORUS GPU testing |

**Fresh cross-machine evidence (25 Oct, inspected at 20:07 Bangkok time):** On **MateBook**, Intel I3-B has a dated **2,056 passed / 0 failed / 33 skipped** complete *synthetic* milestone, including 331 new lifecycle/color assertions. The older I1-safe build error was an intermediate log and **no longer describes the published final I3-B result**. Later I3-C Agent A synthetic producer/session composition reran **1,935 / 0 / 0**, and Agent C independently reran **1,938 / 0 / 0**, including three B1 fake-worker checks; the I3-C synthetic result is an **Agent C ACCEPTED recommendation, pending Main acceptance**. Agent B's B1 cleanup guard is synthetic-only, with a possible final-state race and B2–B8 lifecycle/COM gaps requiring follow-up. **`intel_native_live_input_not_verified` remains CLOSED**. These report paths and test logs exist on MateBook only; none has been merged or deployed to AORUS.

## Evidence on this AORUS worktree

- [Hook ownership and dated evidence](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/README.md), [D3D9 performance verification plan](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/DX9-PERFORMANCE-VALIDATION-2026-10-25.md), [CEF 73 frame pixel measurements](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/CEF-FRAME-DELIVERY-2026-10-25-v1.md).
- [OSC/Backend architecture audit](osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md) and [Capture UI v77 source/build/partial-CEF receipt](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/mods/CAPTURE-UI-REDESIGN-20261025.md).
- [NVIDIA Capture Control/Media plane](../Project/NVIDIA%20Capture/README.md), [first-party AORUS README inventory](README-COVERAGE-20261024.md).

The Intel I1–I3-C proof artifacts are owned by **the other computer**; this local AORUS checkout does not contain their in-progress branches and must not invent local links to MateBook files or assert they are deployed here.

## Integration policy

1. **Keep worktrees independent:** no cross-machine copy, `git pull`, merge, reset, auto-push, DLL staging, AORUS NVENC edits or Intel Desktop runtime enablement merely because documentation was synchronized.
2. **Canonical Duluka design:** one recording session owns timeline, audio plan, mux and finalized media; capture source and encoder selection are separate and must pass real hardware/format/generation/target guards. This architecture is authoritative on the MateBook branch; a matching design claim on AORUS does not prove that the running NvCapture worker implements Intel transport.
3. **Evidence levels:** source present → tests pass → Build artifact → deployed fingerprint → observed CEF/Host runtime → game hardware performance → validated recording. Never collapse these into one "supported" label.
4. **Intended integration gate:** review both branch diffs and authoritative target-identity/session contracts, review I3-B's final synthetic 2,056/0/33 report, I3-C's independent 1,938/0/0 QA, pending Main acceptance and B1 worker final-state race/B2–B8 blockers; independently rerun required NVIDIA/Intel tests on their hardware, then plan an authorized merge and deployment. No integration action was taken during this documentation task.

This is a *documentation-only* cross-machine comparison. Third-party Intel libvpl/vendor READMEs and historical first-party report logs have not been rewritten.