# NVIDIA Adapter — game-facing adapters

**Status: experimental API paths and controlled integration (25 October 2026).**

[Current Duluka Hook guide](OSC%20Adapter/Duluka%20Hook/README.md) now indexes Source vs deployed DLL evidence, real Floating Point x86 Windowed GPU visual proof, DX9 frame-pacing diagnosis and the **non-deployed** empty-frame candidate. [CEF 73 fresh-image delivery results](OSC%20Adapter/Duluka%20Hook/docs/CEF-FRAME-DELIVERY-2026-10-25-v1.md) are fixture data, **not** game FPS or an approved production CEF patch. This root owns two distinct boundaries; they must not be confused with the native OSC Host in `Overlay On-Screen Control/`.

- [OSC Adapter](OSC%20Adapter/README.md): managed Host/Controller; validates process identity and architecture, routes API selection and manages Hook attachment lifecycle.
- [Duluka Hook](OSC%20Adapter/Duluka%20Hook/IMPLEMENTATION-STATUS.md): native D3D9/D3D9Ex and D3D11 Hook/renderer; GPU frame mailbox and fallback experiments. Build/test evidence is scoped; no blanket real-game FPS or compatibility claims.
- [Ansel Adapter](Ansel%20Adapter/README.md): ReShade/Game Filter integration boundary; native ReShade Ansel Engine has its own lifecycle and named-pipe protocol.

Runtime controls, source files, isolated tests, staged DLLs and game-visible Overlay behavior are separate milestones. Do not hot-replace native DLLs inside a running game; avoid bypassing game protections and preserve controlled detach/rollback procedures.
