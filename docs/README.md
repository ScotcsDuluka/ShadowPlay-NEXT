# ShadowPlay NEXT — documentation index

**Refreshed: 24 October 2026.** The repo is changing quickly. This index points to **current ownership and source contracts** first; a dated test report is evidence of its **own revision only**, never proof of an entire deployed build.

## Start here — current source ownership

| Area | Current guide | Key limitation |
|---|---|---|
| Project overview | [English README](../README.md) · [README ภาษาไทย](../README.th.md) | CBT1 preparation does not mean a published NEXT installer. |
| Project directory map | [PROJECT-MAP.md](../Project/PROJECT-MAP.md) | Source / staged Build / running program / GitHub may differ. |
| Launcher / Core Modules | [Launcher](../Project/Launcher.Cef/README.md) · [read-only Server contract](../Project/Launcher.Cef/Modules/CORE-CATALOG-SERVER-CONTRACT.md) | 11 Core components are read-only; add-ons currently empty; Server catalog API not connected. |
| NVIDIA Capture | [Capture](../Project/NVIDIA%20Capture/README.md) · [Duluka Capture](../Project/NVIDIA%20Capture/EngineCapture/Duluka%20Capture/README.md) | Worker availability and saved media require runtime proof. |
| NVIDIA Container | [Container](../Project/NVIDIA%20Container/README.md) · [Capture relay contract](../Project/NVIDIA%20Container/CAPTURE-DISPLAY-RELAY-CONTRACT-20261024.md) | Container is a relay/supervisor, not a frame/encoder owner. |
| NVIDIA Graphics | [GPU profiles](../Project/NVIDIA%20Graphics/README.md) | Preference application differs from observed active renderer. |
| Native game adapters | [Adapter map](../Project/NVIDIA%20Adapter/README.md) · [OSC Adapter](../Project/NVIDIA%20Adapter/OSC%20Adapter/README.md) | DX9Ex GPU IPC tests are not real-game performance proof. |
| OSC | [OSC overview](../Project/Overlay%20On-Screen%20Control/README.md) · [Host](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/README.md) · [NodeAPI](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/README.md) · [OSC UI](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/README.md) | CEF frame cadence and game Hook require separate checks. |
| NVIDIA Config | [Config ownership](../Project/NVIDIA%20Config/README.md) · [CONFIG_OWNERSHIP_MATRIX.md](CONFIG_OWNERSHIP_MATRIX.md) | Never copy secrets into docs or assume a source flag is deployed. |
| FFmpeg | [Integration directory](../Project/FFmpeg/README.md) | A directory or listed codec is not proof of working hardware encoding. |
| Duluka Account / tester Portal | [Account Server](../Server/README.md) · [Launcher](../Project/Launcher.Cef/README.md) | Launcher opens an external HTTPS Portal; shared-mode public registration and CBT1 submission remain closed. This is **not** a Core module catalog API. |

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

**Latest README inventory audit (24 October 2026):** 64 indexed first-party files, 202 local Markdown link destinations checked (0 missing), 0 unbalanced fenced blocks; `git diff --check` passed. Launcher Account Portal/Quick UI focused source tests passed 9/9. This does **not** verify any public server login, CBT1 submission, deployed game Hook or recording pipeline.

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
