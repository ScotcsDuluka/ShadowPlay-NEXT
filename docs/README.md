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
