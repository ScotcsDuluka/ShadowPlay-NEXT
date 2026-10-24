# NVIDIA NodeAPI — Gallery Video Player / Seek Bridge

Date: 2026-10-24. Scope: read-only Gallery player UI bridge in NVIDIA NodeAPI's existing OSC web assets. User authorized Build and Restart without repeated confirmation.

## Root cause

- `osc/mods/gallery-media-source.js` existed and was served HTTP 200 but **was not included in `osc/index.html` nor loaded by another OSC script**.
- `GET /` and `GET /index.html` are the actual OSC NodeAPI page endpoints; `/osc/index.html` is *not* the public route (returns 404).
- Native Gallery video editor includes `<video><source ng-src="{{videoEditor.videoSrc}}" type=video/mp4></video>`. The Gallery player bridge must observe dynamically created `source[src]`, rewrite local file URI to MSE-based HTTP segment playback, and manage seeks/teardown.
- The old bridge would call `SourceBuffer.abort()` before *every* append, leave stale in-flight segments after seeks, and was not connected to the page.

## Changes

1. Added `<script src="/mods/gallery-media-source.js?v=20261024-seek1">` at bottom of existing `osc/index.html` using a surgical edit without reverting parallel UI work.
2. Revised `osc/mods/gallery-media-source.js` to support CEF video source mutation observer, local file path validation, MSE VP8 video + Vorbis audio SourceBuffers, bounded lookahead, seek prioritization and AbortController cancellation, no per-append SourceBuffer.abort, recycling old buffered segments, cleanup of event listeners/object URLs on source change and DOM removal, and explicit error event for unsupported codecs.
3. Added `tests/galleryMediaSource.test.js`: verifies HTML includes the player exactly once; simulated MSE seek aborts old segment request and appends the new one; switching clip disposes old state.
4. Did not alter user recordings, Capture worker, FFmpeg encoder settings, OSC app.js bundled templates, video files, or other owners' systems.

## Tests

- New Gallery player tests: **3/3 PASS**.
- Focused Gallery, NodeAPI Security, Capture regression: **58/58 PASS**.
- `node --check osc/mods/gallery-media-source.js`: PASS.
- Deployed only `osc/index.html` and `osc/mods/gallery-media-source.js` to NodeAPI Build; backed up prior files at `C:\My Project\NVIDIA-Shadowplay\Build\NodeAPI-GalleryPlayerBackup-20261024-115616`.
- Managed NVIDIA Web Helper/NvNode restarted; PIDs at validation: **Web Helper 30000**, **NvNode 24712** (may change).
- Runtime `GET /` and `GET /index.html`: HTTP 200; player script reference present.
- Runtime `GET /mods/gallery-media-source.js?v=20261024-seek1`: HTTP 200 with new `cancelPending` implementation.
- Runtime `GET /Gallery/v.1.0/PlaybackInfo?file=...`: HTTP 200 duration 50.76, segmentDuration 8, audio true for test file.
- Runtime actual segment **24 s video** with Range bytes=0-1023: HTTP **206**, 1024 bytes, WebM, ~3.03 s.
- Runtime actual segment **24 s audio** with Range bytes=0-1023: HTTP **206**, 1024 bytes, WebM, ~0.33 s.
- Runtime Recent and health: HTTP 200.

## Limits / next tasks

- **Not visually tested inside OSC's actual CEF renderer**. Real MSE play/pause, timeline drag, seeking backwards, A/V sync, long recordings, switching clips repeatedly, and error UI remain acceptance gates.
- Segment FFmpeg VP8 re-encoding may cause seek latency and high CPU; optimize only after user-visible CEF testing and measuring.
- The original Gallery Remove safe-stub is intentionally HTTP 501; do not claim delete functionality until explicit Recycle Bin workflow is designed and verified.
- UI changes made in existing HTML only; respect concurrent OSC owners' Customize/Main-menu work.
