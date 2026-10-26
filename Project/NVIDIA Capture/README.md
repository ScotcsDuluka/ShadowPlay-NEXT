# NVIDIA Capture — engine and media ownership

**Status: active development, 24 October 2026.** `NVIDIA Capture` owns the actual display capture, recording, screenshots, encoder/audio pipelines and worker runtime. `NVIDIA Container` forwards validated requests but must never fabricate media files or claim success from a status-only response.

## Source navigation

- `NVIDIA Capture.vbproj` — application/worker project definition.
- `src/` — application routes and current Capture integration.
- `EngineCapture/Duluka Capture/` — modular capture engine source: DXGI, WGC, audio, encoder, recording and tests.
- `tests/` — Capture-specific regression and validation.
- [Duluka Capture source map](EngineCapture/Duluka%20Capture/README.md)

## Current contract and limitations

NvCapture's worker API is described by [Container's Capture communication contract](../NVIDIA%20Container/CAPTURE-COMMUNICATION-HUB-CONTRACT-20261024.md) and [display/screenshot relay](../NVIDIA%20Container/CAPTURE-DISPLAY-RELAY-CONTRACT-20261024.md). The recorded worker route set includes `/health`, `/state`, `/engines`, guarded record commands, `/displays`, `/display/select` and `/screenshot` depending on the installed worker revision.

- A route in **source** does not prove that the installed NvCapture worker implements it.
- Display selection is identity-checked and may apply only after engine rebuild.
- Screenshot success requires a verified saved file, not merely HTTP 200.
- Replay is not considered ready; the documented Container replay route responds 501.
- Native NVENC, FFmpeg, audio/video sync and real game/window capture require pipeline-specific tests and compatible drivers/hardware.

[Historical REST integration proposal](README-REST-INTEGRATION.md) describes an older API/polling design; **do not substitute its port or protocol for current NvCapture/Container worker contracts**.

Never deploy an Engine update while recording without protecting finalization and preserving rollback backups.

## Intel Capture development source (25 October snapshot)

The MateBook Intel/QSV development notes have been preserved in [MateBook Intel development map](MATEBOOK-INTEL-DEVELOPMENT-20261025.md). Current source must be verified with build, tests and live hardware gates; merging source does not certify Intel recording.

