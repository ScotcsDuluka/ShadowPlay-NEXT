# ShadowPlay NEXT — documentation index

**Refreshed: 25 October 2026.** The repo is changing quickly. This index points to **current ownership and source contracts** first; a dated test report is evidence of its **own revision only**, never proof of an entire deployed build.

## Start here — current source ownership

| Area | Current guide | Key limitation |
|---|---|---|
| Project overview | [English README](../README.md) · [README ภาษาไทย](../README.th.md) | CBT1 preparation does not mean a published NEXT installer. |
| Project directory map | [PROJECT-MAP.md](../Project/PROJECT-MAP.md) | Source / staged Build / running program / GitHub may differ. |
| Launcher / Core Modules | [Launcher](../Project/Launcher.Cef/README.md) · [read-only Server contract](../Project/Launcher.Cef/Modules/CORE-CATALOG-SERVER-CONTRACT.md) | 11 Core components are read-only; add-ons currently empty; Server catalog API not connected. |
| NVIDIA Capture | [Capture architecture — control/media diagrams](../Project/NVIDIA%20Capture/README.md) · [Duluka Capture source map](../Project/NVIDIA%20Capture/EngineCapture/Duluka%20Capture/README.md) | NvCapture owns media; Container relays only. Worker availability and saved files require runtime proof. |
| NVIDIA Container | [Container](../Project/NVIDIA%20Container/README.md) · [Capture relay contract](../Project/NVIDIA%20Container/CAPTURE-DISPLAY-RELAY-CONTRACT-20261024.md) | Container is a relay/supervisor, not a frame/encoder owner. |
| NVIDIA Graphics | [GPU profiles](../Project/NVIDIA%20Graphics/README.md) | Preference application differs from observed active renderer. |
| Native game adapters | [Adapter map](../Project/NVIDIA%20Adapter/README.md) · [OSC Adapter](../Project/NVIDIA%20Adapter/OSC%20Adapter/README.md) | DX9Ex GPU IPC tests are not real-game performance proof. |
| OSC | [OSC overview](../Project/Overlay%20On-Screen%20Control/README.md) · [Host](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/README.md) · [NodeAPI](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/README.md) · [OSC UI](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/README.md) | CEF frame cadence and game Hook require separate checks. |
| NVIDIA Config | [Config ownership](../Project/NVIDIA%20Config/README.md) · [CONFIG_OWNERSHIP_MATRIX.md](CONFIG_OWNERSHIP_MATRIX.md) | Never copy secrets into docs or assume a source flag is deployed. |
| FFmpeg | [Integration directory](../Project/FFmpeg/README.md) | A directory or listed codec is not proof of working hardware encoding. |
| Duluka Account / tester Portal | [Account Server](../Server/README.md) · [Launcher](../Project/Launcher.Cef/README.md) | Launcher opens an external HTTPS Portal; shared-mode public registration and general CBT1 submissions remain closed; existing-account invite-only submission uses a separate gate. This is **not** a Core module catalog API. |

**Dual-worktree review (25 October):** [AORUS Stable vs MateBook Intel development branch — separate evidence and integration gates](WORKTREE-STATUS-20261025.md). The MateBook I3/I3B work is **not** part of the installed AORUS NVENC/Hook runtime. the MateBook I3-B synthetic suite now reports **2,056/0/33**, followed by I3-C independent synthetic QA **1,938/0/0** (Agent C acceptance recommendation only). B1 quiescence and B2–B8 remain open; live Intel capture stays blocked and no cross-machine merge/deploy was performed.

## 25 October updates — current engineering gates

- **[Duluka Hook source/runtime guide](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/README.md)** — Floating Point x86 external D3D9 Windowed GPU *live visual* proof, while game FPS/stutter remains **unverified**; empty-CPU-frame short circuit is a **not-deployed candidate**.
- **[CEF 73 GPU image delivery](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/CEF-FRAME-DELIVERY-2026-10-25-v1.md)** — requested 75/120/240 vs ~60 unique images/s in isolated GPU-pixel measurements, despite more callbacks. A temporary unlimited test reaches ~250 but does **not** obey FPS limits.
- **[CEF 73 build preparation](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/docs/CEF73-BUILD-PREPARATION-2026-10-25-v1.md)** — bounded source patch applied to isolated checkout; compatible VS 2017 toolchain unavailable; no patched CEF binary or production deployment.
- **[Capture UI v77](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/mods/CAPTURE-UI-REDESIGN-20261025.md)** — Video/Advanced Settings restyling; 32/32 UI checks and partial Advanced CEF acceptance. Live recording/encoder work unchanged and Video Capture CEF geometry still needs acceptance.
- **[OSC/Backend architecture audit](osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md)** — native query/frame lane, NodeAPI, Container, Capture, Gallery and Adapter/Hook separated. [Source route inventory](osc/OSC-BACKEND-ROUTE-INVENTORY-20261025.md) is not a live API availability or authorization certificate.

**Audit scope:** source docs were re-read against 25 October owner reports. Historical UI, Hook and old Capture snapshots remain intentionally dated. Consult [the README inventory](README-COVERAGE-20261024.md) for classification.

## 24 October late updates — source vs runtime

- **[CBT1 invite-only applications](../Server/README.md)**: single-use private codes for existing-account holders, manual admin approval; public account registration and general CBT1 submissions remain closed. [CBT1 website](https://nv-cbt.pages.dev/) is **not** a public NEXT installer or open registration promise.
- **[Guided ReShade Manager](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/NODEAPI-RESHADE-MANAGER-20261024.md)**: guarded official-Setup link and read-only evidence, no automatic installer/DLL mutation; live CEF click validation still required.
- **[Hook Transport policy](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/README.md)**: D3D9 windowed GPU preview, D3D9Ex CPU mode selected despite experimental GPU mailbox tests; other native renderers have per-API gates.
- **[Capture Pipeline Design](UI-CAPTURE-PIPELINE-SIZE-LOCK-20261024.md)**: size-locked preview only, **not** an active or persisted capture engine selector.
- **ApplicationOntology** local source `C:\My Project\nv-ontology-pages\public\catalog.json` now reports **v1.2.9 / 23 entries**. Its public deployment has not been independently verified.

## Build, deploy and validation

### Historical MateBook Intel-worktree notes (2026-10-25; pre-unification)

**Dual-worktree review (25 October, evening refresh):** [MateBook Intel branch vs AORUS Stable — proof levels, I3-B/I3-C and B1 blockers](WORKTREE-STATUS-20261025.md). I3-B **2,056/0/33** synthetic report is complete, and I3-C Agent C independently reran **1,938/0/0** synthetic checks; Agent C's acceptance recommendation is **not Main approval**. B1 worker quiescence and B2–B8 remain open, while Intel Live Desktop remains blocked. No file merge/deploy was performed.

## HUAWEI-MateBook Intel Capture worktree — 25 October 2026

**Important:** This directory is in `intel/duluka-capture`, **not** the published/AORUS `Stable` runtime. Its local Intel QSV updates are uncommitted, and other agents may be editing native Capture source concurrently. Preserve the original accepted session design and each report's dated scope.

- [MateBook NVIDIA Capture module map](../Project/NVIDIA%20Capture/README.md) — Control/Media ownership, one session, source/encoder separation and live gate.
- [Canonical Session-Centric Recording Contract](architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) · [ฉบับภาษาไทย](architecture/SESSION-CENTRIC-RECORDING-TH.md) — accepted architecture; independent Desktop/Exclusive API selections and selected encoder, one Duluka session/clock/mux.
- [Intel architecture and rationale](INTEL-DULUKA-CAPTURE-ARCHITECTURE.md) · [I2 handoff](INTEL-DULUKA-CAPTURE-I2-HANDOFF.md) · [I3 handoff](INTEL-DULUKA-CAPTURE-I3-HANDOFF.md) — revision-scoped implementation evidence, not an override of the canonical contract.
- [I1 regression evidence](reports/intel-duluka-i1-2026-10-24/README.md) · [I2 native QSV evidence](reports/intel-duluka-i2-2026-10-24/README.md) · [I3 synthetic staging](reports/intel-duluka-i3-2026-10-25/README.md) · [I3 borrowed-frame adapter](reports/intel-duluka-i3-composition-2026-10-25/README.md) · [I3-B 2,056/0/33 synthetic lifecycle/color](reports/intel-duluka-i3b-2026-10-25/README.md) · [I3-C Agent C 1,938/0/0 independent QA](reports/intel-duluka-i3c-2026-10-25/C-INDEPENDENT-QA.md). The latest I3-C synthetic acceptance is **Agent C's recommendation pending Main**; the live gate `intel_native_live_input_not_verified` remains closed.
- [MateBook first-party README coverage](README-COVERAGE-MATEBOOK-20261025.md) — inventory and local documentation link validation, excluding vendored Intel libvpl and generated folders.

**What this does not prove:** running an actual Intel Desktop/Fullscreen Exclusive recording, native QSV CBR, audio capture/AV synchronization, arbitrary HDR/color-managed playback or deployment to AORUS. I3-B verified BT.709 Limited/Full *synthetic H.264/MP4* color metadata only. Synthetic media tests and a connected default acquisition route are separate gates.

## Start here (CURRENT — maintained, reflect the live tree)

| File | What it is |
|---|---|
| `ARCHITECTURE.md` | Historical engine component inventory; **canonical Session-Centric Contract** in `architecture/` overrides inconsistent source/route descriptions |
| `APP-LAYOUT.md` | Root-fixed product tree assembled by `scripts/layout.proj` / `build-all.ps1 -StageLayout` |
| `BUILD_PROTOCOL.md` | Clean-build protocol followed by `scripts/build-all.ps1` |
| `CONFIG_RUNTIME_CONTRACT.md` | v2.0 config↔runtime contract (writer inventory, FPS/rate-control authority) |
| `CONFIG_OWNERSHIP_MATRIX.md` | Which config file owns which setting |
| `PHASE-13-SHADOWPLAY-CLOCK.md` | Audio clock analysis behind the current A/V alignment design |

**MateBook README sync validation (25 October evening):** 61 first-party README entries, **180 relative README links** checked (0 missing); 192 local links including the cross-worktree summary (0 missing); 0 unmatched triple-backtick fences and `git diff --check` PASS. This is a **documentation-only** audit, not a fresh Intel recording or source-regression run. See [complete local inventory](README-COVERAGE-MATEBOOK-20261025.md).

## Reference (design specs & audit records of shipped phases — verify against code before relying)

- **[Build/deployment policy](BUILD-DEPLOY-POLICY.md)** — prefer scoped targeted build/test, verify SHA-256, avoid hot-replacing loaded native DLLs.
- [BUILD_PROTOCOL.md](BUILD_PROTOCOL.md) — older broad clean-build protocol; check actual current scripts before using any referenced paths.
- [CONFIG_RUNTIME_CONTRACT.md](CONFIG_RUNTIME_CONTRACT.md) · [CONFIG_OWNERSHIP_MATRIX.md](CONFIG_OWNERSHIP_MATRIX.md) — ownership and runtime settings.
- [README coverage inventory](README-COVERAGE-20261024.md) — index of first-party README-like files; distinguishes active guides, component notes, and historical experiments.

**Latest README inventory audit (25 October 2026):** 65 indexed first-party files, 277 local Markdown links checked (0 missing), 0 unbalanced triple-backtick fences. Four selected Source test files passed 50/50 checks (Capture Pipeline UI, Video Settings, Hook Settings, Launcher). This does **not** verify public server login, CBT1 submission, native CEF pixels, D3D9 game FPS or finalized media. The 24 October 33/33 results remain a dated earlier sample.

**Documentation-only changes do not require a Build, deploy, process restart or game attachment.** For any code/runtime change, run the corresponding module tests and the release-specific acceptance gates. Do not merge/push unrelated dirty changes.

## Architectural design references (not universal current status)

- [ARCHITECTURE.md](ARCHITECTURE.md) — **Engine-Rebuild target architecture**, with historical per-component statuses; compare to active `Project/` source and current subsystem reports.
- [APP-LAYOUT.md](APP-LAYOUT.md) — older staged-layout specification; current builds are assembled from `Project/build.bat` and `Scripts/build-dev.ps1` where applicable.
- [PHASE-13-SHADOWPLAY-CLOCK.md](PHASE-13-SHADOWPLAY-CLOCK.md) — A/V clock rationale.
- `P1-F-*` — NVENC architecture and phase tests; `phase-12-*` — modular-engine split and revisioned architecture proposals.
- [P13.5-REMOVAL-INVENTORY.md](P13.5-REMOVAL-INVENTORY.md) · [NOTIFIER_SLOT_AUDIT.md](NOTIFIER_SLOT_AUDIT.md) — audit records.

## Historical / point-in-time records

`PHASE_PLAN.md`, `PHASE0_BASELINE.md`, `PHASE1_VIDEO_VALIDATION_STATUS.md`, `phase-11-postmortem.md`, `STATUS-2026-08-28.md`, `STATUS-2026-09-07.md`, and old OSC unpacked/reverse-map READMEs are **reference evidence**, not a current runtime declaration. Some original plans are superseded.

**Rule:** Explicit current owner contracts and matching source/tests outrank earlier design notes; installed runtime and verified in-game behavior must be checked before stating a feature is released.
