# Overlay On-Screen Control — OSC subsystem

**Status: active development, 25 October 2026.**

For the latest cross-service and CEF/Backend separation, see [OSC/Backend Full Architecture & Runtime Audit](../../docs/osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md). New CEF pixel-delivery measurements have confirmed that GPU callbacks alone do not establish fresh frames above 60/s; a bounded CEF 73 source candidate is not yet compiled or deployed. [Native OSC Host status](NVIDIA%20OSC/README.md) and [UI v77 evidence](NVIDIA%20NodeAPI/osc/mods/CAPTURE-UI-REDESIGN-20261025.md) describe separate acceptance gates.

This subsystem separates the native CEF/desktop Overlay Host from the OSC UI/backend. The two source owners and a game-facing adapter are distinct:

- [NVIDIA OSC](NVIDIA%20OSC/README.md) — C++ native CEF Host, desktop compositor, GPU frame publishers, window/input/focus lifecycle.
- [NVIDIA NodeAPI](NVIDIA%20NodeAPI/README.md) — web UI, localization, OSC mods, service bridges and tests.
- [OSC Adapter and Duluka Hook](../NVIDIA%20Adapter/OSC%20Adapter/README.md) — process-bound attachment and native per-game Hook/Renderer.

D3D9 CPU shared-memory and D3D9Ex opt-in GPU shared texture are **different transports**. D3D11 uses its own shared-texture path. A compiled publisher and passing isolated IPC tests do not prove correct CEF frame cadence, game FPS, anti-stutter or verified public-release compatibility.

Source, Build, running runtime and GitHub `Stable` can differ. Documentation-only changes require no OSC/Container restart.
