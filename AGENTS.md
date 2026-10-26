# ShadowPlay NEXT — Repository Agent Notes

This repository is actively modified by multiple developers/agents. Preserve concurrent changes; do not reset, checkout over, clean, stage, commit, merge, pull, or push without the user's explicit direction. Keep changes scoped to the requested module and test them.

## Mandatory Duluka Capture architecture

When working on **Duluka Capture**, **NVIDIA Capture native session orchestration**, **video/encoder factories**, **Desktop / Fullscreen Exclusive source selection**, **recording audio/mux**, or their OSC controls, **read these files before editing**:

1. `docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md` — accepted canonical architectural decision, SPN-ADR-2026-10-24-SESSION-001.
2. `docs/architecture/SESSION-CENTRIC-RECORDING-TH.md` — Thai explanatory companion.
3. `docs/INTEL-DULUKA-CAPTURE-ARCHITECTURE.md` and `docs/INTEL-DULUKA-CAPTURE-I2-HANDOFF.md` — Intel-specific implementation evidence; these do not override the canonical Session contract.

**Invariant:** ONE Duluka RecordingSession owns SessionId, clock, audio/video timeline, output surface, selected encoder and MP4 mux. Desktop Capture API and Fullscreen Exclusive Capture API are independently configurable frame-source choices feeding the same FrameRouter/Session; the Encoder is selected independently after compatibility checks. Maintain truthful state, source generation, LUID/monitor identity and bounded recovery. Do not mistake black placeholders or encoder exit 0 for captured footage/saved MP4. OBS/NVIDIA/standalone FFmpeg keep their own engine-owned recording pipelines.

Feature declarations are NOT proof of implementation. Intel QSV native recording, automatic Fullscreen Exclusive switching and FFmpeg AudioSidecar are not certified just because their UI/config/design slots exist. Verify actual source and hardware capability, preserve existing NVENC behavior, perform media decode acceptance and report unimplemented/unverified cases.

Never run an actual desktop/game capture, microphone or system-loopback recording without a privacy-cleared test scene and explicit test scope. Synthetic/test-owned data and read-only DXGI metadata inspection are permitted within task scope.

**Architecture change control:** never silently replace/remove the canonical contract. Add a dated amendment with owner rationale, compatibility consequences and regression plan. The original machine's `intel/duluka-capture` worktree contains concurrent uncommitted Codex I1/I2 work; do not overwrite it.
