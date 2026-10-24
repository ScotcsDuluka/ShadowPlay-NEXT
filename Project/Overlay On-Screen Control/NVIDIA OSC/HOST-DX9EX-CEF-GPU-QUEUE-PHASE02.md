# ShadowPlay NEXT — OSC Host CEF GPU Queue (Phase 0.2)

Date: 2026-10-24. Owner: **OSC Host**. Working branch: **Stable**.
**Not deployed. Not enabled by default. Not yet measured inside a live game.**

## Implemented host path

```text
CEF OnAcceleratedPaint (GPU Shared Handle; callback-scoped)
    -> GpuComposite opens CEF shared texture on Host D3D11 device
    -> QueueD3D9ExGpuFrameLocked
        -> CopyResource to Host-owned BGRA8 full-frame staging texture
        -> End(D3D11_QUERY_EVENT)
        -> returns WITHOUT Map/GetData/GPU completion wait
    -> bounded 2-slot queue, drop if no free staging texture
    -> OSC publish worker (~4 ms polling, not Game Present)
        -> lazily allocates/resize-replaces staging textures off CEF thread
        -> Flush() and GetData(DONOTFLUSH) for completion
        -> confirm original Host adapter LUID = game adapter LUID
        -> confirm trusted Hook Stage/Commit mailbox handshake
        -> TryPublishGpuTexture(HostGpuDevice, HostGpuContext, stage, ...)
            -> GPU CopyResource to NEW legacy BGRA8 shared texture
            -> D3D11 completion query
            -> after GPU completion, readyGeneration in DX9Ex mailbox
    -> trusted DX9Ex game-side Pump: Stage -> Commit -> Release ACK
```

The shared handle received from CEF is **never passed to the worker**.
The Host-owned GPU texture survives callback return. A copy command is
enqueued during the callback; GPU completion is checked on the worker.

## Important runtime gates

- **Host:** `OSC_DX9EX_HOST_PREVIEW=1` activates the legacy-GPU mailbox publisher.
- **Host additional gate:** `OSC_DX9EX_CEF_GPU_PREVIEW=1` activates the new CEF GPU queue. Both are OFF by default; settings menu GPU preference alone does NOT enable these.
- **Game/Hook:** `NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW=1` and a trusted game-process integrator calling `NvOscAdapterPumpD3D9ExGpuMailbox` on a legal D3D9Ex device thread.
- Same **GPU LUID** is mandatory for GPU-only CopyResource route. An Intel->NVIDIA or GTX1080Ti(0)->GTX1080Ti(1) transfer uses existing CPU mirror/fallback; equal card names do NOT qualify.
- Same **D3D11 device** is mandatory between the staged source and the legacy producer copy. The new Host queue uses the exact `HostGpuDevice()` for both.
- Allowed source formats are BGRA8 typed/typeless 2D, one mip, one sample, one array slice, at most 64 MiB BGRA8.
- Only publishes while game is focused and overlay visible. Hide triggers graceful Withdraw(Offer=0) on worker, with release acknowledged by receiver. CPU shared-memory v1 remains active.
- The callback can submit GPU CopyResource/End, which may still consume CPU driver time; **no guarantee of zero CPU overhead**. This preview avoids *waiting for GPU completion* inside CEF, not every possible driver delay.

## Queue / resource guarantees

- At most two staging textures. If both busy or resources not prepared, drop the callback frame rather than grow a backlog. The worker selects the highest submission serial and refuses to publish an older frame once a newer generation has already been consumed (latest-frame wins).
- Worker creates/recreates textures on changed width/height or Host device identity, **outside the CEF publish mutex**; never passes a raw CEF handle to that thread.
- Each successful copy submits a D3D11 event query; the worker issues Flush once per frame generation and polls using DONOTFLUSH. No CPU Map/Readback in this route.
- Queue state is protected by the existing Host publisher mutex, processing stages retain references until complete, and the Host joins the publish worker before releasing stage resources.
- CPU Shared Memory v1 continues even when Direct GPU is active. Only the redundant CPU->legacy GPU upload yields temporarily to recent CEF GPU queue submissions; if CEF GPU traffic stops, that fallback resumes.
- DX9Ex legacy producer retains the active and retiring resources until Stage/Commit/Release ACK. Backpressure skips new frames, does not block Game Present.

## Available diagnostics

Worker log every 5s while enabled:
`[dx9ex-gpu-preview] queued=... drop=... gpu-submit=... unready=... cb-copy-us-avg=... stage-ready-ms-avg=...`

- `cb-copy-us-avg`: **CPU wall time for recording CopyResource + End commands on callback**, not GPU processing time.
- `stage-ready-ms-avg`: queueing to observed query completion (poll interval included), not pure GPU execution time.
- `gpu-submit`: successfully offered GPU frame to legacy producer. **Does not mean Hook committed/displayed it**.
- These counters allow initial queue/backpressure diagnosis but do NOT establish game FPS or 1% lows. Use Hook Present timing snapshots and actual game frame pacing for that.

## Completed and pending validation

- OSC Host isolated x64 Release compile/build: PASS (Build only, no deployment; requires last build to correspond to latest source edits).
- Previously real Host standalone publisher -> real Hook receiver across Windows processes: PASS with x64 and x86 receivers (both CPU Upload and GPU Source paths), synthetic textured frames.
- The **new CEF callback integration and staging queue** has compiled but NOT been executed against real CEF frames on a full OSC runtime or game. No live smoothness measurements have been made.
- Pending: isolated full CEF process run using genuine accelerated paint; ensure pre-multiplication and dirty-rect/animation correctness, GPU device-loss/hotplug, repeated resize/restart, DX9Ex Pump on the real game device thread, sustained latency, Present average/p95/p99 and FPS 1% lows.
- No deployment, Hook modifications, shared Hook ABI changes, or Git commit in this task.
