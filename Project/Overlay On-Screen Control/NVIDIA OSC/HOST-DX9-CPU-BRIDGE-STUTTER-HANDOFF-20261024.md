# ShadowPlay NEXT — NVIDIA OSC owner handoff: D3D9 CPU transport stutter
Date: 2026-10-24. Owner requested: OSC Host / Frame Publisher. Source inspection and live feedback; NO OSC source change here.

## Real-game observation / limits
- Floating Point is a classic **D3D9** x86 target (NOT verified as D3D9Ex).
- Container auto-hook launched x86 Host and in-game Hook DLL, confirmed by 32-bit module enumeration. D3D9 code detours installed in the game.
- User still reports stutter despite new Duluka D3D9 pipeline diagnostics. No objective FPS/frametime baseline yet; do not claim stutter has been fixed or GPU->GPU works on ordinary D3D9.
- Duluka D3D9 Hook currently uses the CPU v1 BGRA frame mapping; D3D9Ex GPU mailbox remains opt-in, not game-proven.

## High-priority Host bottleneck hypotheses (inspect/measure, do not just remove sync)
Source: src/OscFramePublisher/OscD3D11FramePublisher/OscD3D11FramePublisher.cpp
1. QueueFrameReadbackLocked submits CopySubresourceRegion, End(query) and HostGpuContext()->Flush() on the CEF path for every queued readback. Consider decoupling Flush into the worker with bounded cadence and forward-progress fallback; verify GPU completion without indefinite DONOTFLUSH starvation. Avoid extra UI thread GPU flushes.
2. PublishCompletedAdapterFrame: after Map(readback), for ConsumerRenderApiD3D9 it still calls keyed-mutex AcquireSync, UpdateSubresource for EVERY dirty rect, game-adapter context Flush and ReleaseSync, IN ADDITION to publishing CPU D3D9 v1 pixels. A pure legacy D3D9 consumer cannot consume this D3D11 NT shared texture. Determine whether to skip the redundant CPU->D3D11 game-adapter mirror for only classic D3D9, while maintaining metadata, CEF serial accounting, D3D11 consumers, recovery, and future D3D9Ex opt-in. Do not assume all consumers want the same resource.
3. Worker interval is 4ms and staging ring has 3 slots. Profile CPU map/query frequency, frame serial advance, full-frame readbacks, dirty rectangle coalescing, Host CPU time, GPU flushes, frame age, readback failures; avoid unbounded queuing and duplicate pixel work.
4. osc::d3d9_frame_publisher::PublishD3D9FramePixels copies the CPU bridge; Hook then copies snapshot and uploads a D3D9 texture. This is legitimate fallback for classic D3D9, but costly on continuous CEF animation. Consider opt-in adaptive publish cadence with visible UI responsiveness; not a blanket FPS limit.
5. Distinguish CPU bridge cost (Host + Hook) from D3D9 Present composition (Hook stateblock capture/apply and quad). Save P50/P95/P99 game frametime with overlay hidden, static, and animated. Never infer FPS from synthetic tests.

## Contract and acceptance
- Preserve D3D11 NT Shared Handle + KeyedMutex pipeline and independent frame-number dirty cadence.
- Preserve D3D9 CPU v1 pixel mapping, magic, seqlock, owner-PID, orientation, hide=frameNumber0, reset/recovery.
- Preserve D3D9Ex preview switch + Release ACK correctness, not forced on D3D9 game.
- Add isolated regression verifying Host CPU v1 publisher to Hook x64/x86 and CEF paint cadence; stress resize/hidden/owner death.
- Build native x64 and Win32. Deploy/restart only affected Host after coordinated test: no live game holding DLL, backup/rollback SHA-256, compare before/after.
- Current source ownership: Duluka Hook chat changes Hook ONLY; OSC owner changes OSC source, not D3D9 Hook internals.

## Duluka side
- Phase 0.1 instrumentation is in include/d3d9_pipeline_timing_diagnostics.h; NvOscAdapterGetD3D9PipelineSnapshot is same-process only, read off render thread.
- Follow-up Hook fast path skips backbuffer/state changes only when OSC is hidden. Visible-frame CPU/GPU hot path remains unchanged pending data.
