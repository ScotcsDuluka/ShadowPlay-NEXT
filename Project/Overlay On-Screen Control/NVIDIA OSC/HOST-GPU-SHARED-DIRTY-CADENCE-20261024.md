# ShadowPlay NEXT — Host GPU_SHARED Dirty Content Publish Cadence

Date: 2026-10-24. Owner: OSC Host. Branch: `Stable`.
Status: **SOURCE FIX + ISOLATED BUILD + UNIT REGRESSION PASS; NOT DEPLOYED OR LIVE-ENABLED.**

## Issue reported by GLM

SMASH LEGENDS (D3D11) live test at 2026-10-24 07:49–07:54
showed severe stutter with OSC menu open. Hook was performing
keyed-mutex AcquireSync / full-texture CopyResource on repeated
`frameNumber` changes even when the actual view was static.

One concrete Host bug: `PublishMetadataLocked()` previously incremented
`g_frame_bridge->frameNumber` on **every visible metadata update**,
including idle/visibility/resource refreshes without new CEF content.
This can defeat Hook frame caches even when GPU pixels have not changed.

Note: `[oscperf] present_n` is **Host desktop Present()**, not a direct
count of shared-memory frame-number advances. Its idle ~1 Hz counter
may continue and is not evidence of new Hook GPU content on its own.
Likewise `view_cb/s` is integer-rounded, so a displayed 0 can still
be fewer than three callbacks in a 3-second window. Hook-side
`frameNumber/cacheCopies` counters are the authoritative retest signal.

## Implemented Host-only changes

- New small, unit-testable policy:
  `src/OscFramePublisher/Common/OscPublishCadence.h`.
- `osc_main.cpp::OnAcceleratedPaint` marks a new CEF source serial
  **only after a processed PET_VIEW callback successfully opens and
  composites the CEF GPU frame**, inside the existing publisher mutex.
- The same-adapter D3D11 copy publishes metadata with the latest CEF
  source serial only **after CopyResource + keyed-mutex ReleaseSync
  succeeded**.
- The cross-adapter worker stores a CEF source serial in each bounded
  readback slot and propagates it **only after the GPU query, staging
  Map, upload and keyed-mutex ReleaseSync succeed**.
- `PublishMetadataLocked(forceHidden, sourceSerial)` advances
  `frameNumber` only when (1) visible, (2) the pixel copy succeeded,
  and (3) `sourceSerial > lastPublishedSourceSerial`. Repeated
  metadata writes or timer-triggered readbacks of the same source
  keep the previous frame number exactly unchanged.
- Explicit hide/focus-loss still clears `frameNumber=0`. A new
  visible CEF paint may then advance to the next nonzero frame.
  The helper handles out-of-order, failed, duplicate, and coalesced
  serial numbers without falsely advancing.
- A failed CEF accelerated paint no longer causes a new-source
  submission through the stale previous shared texture. The existing
  `ForceFullReadback` recovery is requested instead.
- No change to `osc_frame_bridge.h`, Shared Memory layout,
  D3D11 v3 / D3D9 CPU v1 ABI, GPU_SHARED transport, keyed-mutex
  ownership, or Duluka Hook source code.

## Validation

- Isolated `NVIDIA OSC` x64 Release build: **PASS**;
  executable `build_test/NVIDIA OSC_Test/NVIDIA OSC_Test.exe`.
- Cadence regression:
  `tests/HostPublishCadenceTest.vcxproj` → x64 **PASS**,
  Win32 **PASS**, assertions explicitly enabled.
  Cases include no source, duplicate source, source older than already
  published, failed GPU copy, retry, hide/reopen, old timer reads,
  new source, and 10,000 repeated no-change metadata ticks.
- `git diff --check`: PASS. Changes remain uncommitted.
- **No game opened and no existing OSC or Hook process restarted,
  replaced or deployed** during this work.

## Remaining caveat and retest gate

This is a **source-event cadence fix**, not a GPU pixel hash:
CEF `OnAcceleratedPaint` sometimes fires for compositor animation
with empty `dirtyRects` and really changed pixels, so it MUST NOT be
blanket-skipped. Conversely, a *forced* `Invalidate` can cause CEF
to produce a pixel-identical callback. Without GPU content comparison
we cannot certify that every new callback contains distinct pixels.
The current host burst timer requests CEF invalidation for recovery,
which may yield such callbacks. Do not describe this as mathematically
guaranteed per-pixel deduplication.

For the controlled retest, compare:
- `view_cb/s` and `processed/s` with actual menu static/animated
  state and visible/focused mode.
- Host shared `frameNumber` deltas (not the desktop `present_n`).
- Hook `cacheCopies/callbacks` with unchanged frames: target near 0.
- Rendered OSC visuals, minimized copy time, and Present frame
  latency / p95 / p99. Check that animations and show/hide still work.

If Hook copies remain high while CEF produces many pixel-identical
callbacks, the next fix needs a **reliable content identity strategy**
(e.g. verified GPU-side frame digest) or removal of unnecessary forced
CEF repaints. Do not replace this by blindly treating empty
dirtyRect lists as unchanged, or disable animation recovery without
independent tests.

Do **not** deploy or enable a live build until the responsible owner
authorizes a controlled A/B retest and the Hook team is ready to
measure frame-cache reuse.
