# Gallery Video Playback Engine — Architecture & Prototype Design
> 2026-09-10 — authored by ZCode session (Gallery playback workstream)
> Branch: `Engine-Rebuild-Stabilization` @ `463eae5` at time of writing.
> Status: DESIGN + PROTOTYPE. This document follows the repo's evidence-first
> convention: every claim about existing code cites the real file it came from.

---

## 1. Mission & Non-Mission

**Mission**: design + provable prototype of a **Gallery Video Playback Engine**
for files already produced by ShadowPlay (Duluka/FFmpeg regimes both end in
MP4 — see §5 File Format). Scope is **Gallery playback ONLY**:

```
Gallery UI → PlaybackSession → Media Source/File → Demux → Video Decode
           → Frame Queue → D3D11 Renderer → Gallery Viewer
audio:     → Audio Decode → Audio Buffer → existing audio output stack (NAudio)
```

**Non-mission (explicitly out of scope, per owner spec):**
- NOT a capture engine task. `CaptureEngine.*` is capture/encode/recording.
  This document and the prototype live in a **separate domain** (`Gallery.*`).
- The playback lifecycle MUST NOT be bound to the recording lifecycle.
- No changes to `CaptureEngine*`, `Engine/`, `Overlay/`, `Duluka/`, installer,
  M1/M2 audited surfaces. New files only, in new directories.
- No Gallery UI overhaul: the existing `Base_Gallery` stub form stays as-is
  for now; the prototype proves the ENGINE, the UI wiring is a follow-up.

---

## 2. Code Archaeology Summary (evidence-based)

Keyword sweep mandated by the owner spec: `FFmpeg, NVDEC, D3D11, D3D11VA,
MediaFoundation, decoder, demux, thumbnail, video player, renderer, texture,
IVideoFrame` (+ `Gallery, ffprobe, audio render`).

### 2.1 REUSABLE (safe, with evidence)

| Asset | Where | Why reusable |
|---|---|---|
| FFmpeg binary validation | `Common/FFmpegLocator.vb` | MZ-header + `-version` probe + per-file-version cache; directly reusable for locating the playback backend's ffmpeg/ffprobe (link-compile convention — `Common/` has no vbproj, apps `<Compile Include>` it, e.g. `Engine/NVIDIA Capture.vbproj`, `CaptureEngine.Recording.ConsoleDriver.vbproj`) |
| Vortice D3D11/DXGI stack | `Vortice.Direct3D11` / `Vortice.DXGI` 3.6.2 (`DulukaEngine.Video.DXGI.vbproj`) | Proven interop stack in-product; builds on Linux CI (verified 0 errors in this session) |
| Frame ownership contract | `CaptureEngine.Video/Contract/IVideoFrame.vb`, `Frames/IVideoFrame.vb` | Single-owner, thread-movable, dispose-once semantics — the exact pattern a playback frame queue needs |
| Dispose-once guard pattern | `CaptureEngine.Video.Ddagrab/D3D11VideoFrame.vb:192` | `Interlocked.CompareExchange` one-shot Dispose + swallow-exceptions-on-dispose + dispose callback for leak-invariant metrics |
| Bounded handoff semantics | `CaptureEngine.Video/Handoff/BoundedVideoFrameSink.vb` + `Tester/.../BoundedHandoffTests.vb` | Bounded queue + drop-oldest + ownership transfer on push — design precedent for the playback frame queue (capture push-model; playback needs a clock-driven pull — pattern is reused, code is not imported) |
| Timestamp math | `CaptureEngine.FFmpegBackend/SyncMath.vb`, `CaptureEngine.Audio.Wasapi/AudioPositionTracker.cs` | QPC/100-ns stamp math, offset clamping — same time-domain conventions adopted for the playback clock |
| ffprobe-alongside-ffmpeg | `Tester/test/CaptureEngine/Recording/RuntimeSyncTests.vb` (`RRT_FFMPEG` env, both exes in one dir) | Existing convention for finding/probing media binaries; ffprobe is already the repo's metadata authority (`CaptureSession.vb` step 7) |
| Audio output stack | `NAudio 2.3.0` (already referenced: `CaptureEngine.Recording.vbproj` = `NAudio.Wasapi`, legacy `Engine/NVIDIA Capture.vbproj` = full NAudio) | `NAudio.Wasapi.WasapiOut` IS the existing audio-output abstraction — no new audio stack needed, no duplicated abstraction (owner spec: do not invent one when a good one exists) |
| stderr parsing discipline | `CaptureEngine.FFmpegBackend/FFmpegStderrParser.vb`, `FFmpegProcessHost.vb` | Repo precedent: parse FFmpeg stderr defensively, drain pipes async (the 64KB-buffer deadlock lesson is documented in `FFmpegLocator.vb`) |
| Console-runner test pattern | `Tester/test/**`, `Engine.ConfigTruth.Tests` | Standalone runners, `TestRunner.Run/Skip`, hardware gates (`HardwareGate.vb`), fake sinks (`Tester/test/CaptureEngine/Video/Fakes/*`) |
| Gallery UI shell | `Overlay/.../[Gallery]/[1] Main.vb` | Form exists (path editor + nav); engine plugs in later without redesigning it |

### 2.2 MISSING (must be built — nothing exists)

1. **Media file demux/decode layer** — no code in this repo reads an MP4.
   (`CaptureSession.vb` only ffprobe-probes duration of files it just wrote.)
2. **Hardware decode (NVDEC / D3D11VA / cuvid)** — zero code; docs mentions
   only (`docs/STATUS-2026-08-28.md`, `docs/PHASE_PLAN.md`, `docs/PHASE0_BASELINE.md`).
3. **Playback frame queue** (clock-driven pull; the capture bounded sink is
   push-shaped).
4. **D3D11 present/renderer** — capture side never presents to a window.
5. **PlaybackSession lifecycle state machine** — `CaptureSession.vb` is a
   recording lifecycle; wrong domain, audited, do not couple.
6. **Playback A/V sync clock** — `SyncMath` solves capture-domain offsets;
   playback needs a master presentation clock (new, small, testable).
7. **Seek logic** — nothing seeks.
8. **Thumbnail generation + cache** — nothing exists (0 grep hits).
9. **Gallery file listing/index** — stub form only.

### 2.3 UNSAFE TO REUSE (capture-domain, do not couple)

- `CaptureEngine.Video.Ddagrab/DdagrabBackend.vb` — DXGI duplication capture
  worker; its lifecycle IS the capture lifecycle (M2-audited stop path).
- `CaptureEngine.Recording/CaptureSession.vb`, `RecordingEngine.vb` — recording
  state machine (M1-audited cleanup path).
- `CaptureEngine.FFmpegBackend/LiveMuxSession.vb`, `AudioTap*`, `AudioSidecar*`,
  `WavSidecarWriter` — mux/write direction (writes files; playback reads them).
- Legacy `Engine/Engine/[Capture]/CaptureEngine.vb` — legacy regime + QSV
  branches; explicitly out of scope per owner spec.
- `BoundedVideoFrameSink` code (import) — semantics reusable, code is capture-
  push-shaped; playback imports the PATTERN, not the file.

---

## 3. Architecture

### 3.1 Domain separation (enforced by project boundaries)

```
CaptureEngine.Video        CaptureEngine.Recording        Gallery.Video  (NEW)
(capture sources)          (recording lifecycle)          (file playback)
        │                          │                            ▲
        └────── writes MP4 ────────┴──────────►  files  ────────┘
                                    (no shared objects, no shared threads,
                                     no shared lifecycle — ONLY files)
```

`Gallery.Video` references **nothing** from `CaptureEngine.*`. The only shared
source file is `Common/FFmpegLocator.vb` via the repo's link-compile convention
(zero new dependency direction; no CaptureEngine code involved).

### 3.2 Project layout (NEW files only)

```
Gallery.Video/                          (net10.0, VB.NET, Option Strict On)
  Gallery.Video.vbproj
  PlaybackState.vb                      9-state lifecycle enum + docs
  MediaInfo.vb                          probe result DTOs (no codec assumptions)
  MediaProbe.vb                         ffprobe JSON → MediaInfo
  PlaybackFrame.vb                      BGRA8 frame + PTS (100-ns ticks), dispose-once
  FrameQueue.vb                         bounded, generation-token flush, drop policy
  PlaybackClock.vb                      QPC-domain master clock
  FfmpegDecodeWorker.vb                 subprocess decode (video+audio pipes)
  AudioRenderer.vb                      NAudio WasapiOut wrapper (+sample clock)
  D3D11VideoRenderer.vb                 Vortice swapchain present loop
  PlaybackSession.vb                    orchestrator / state machine / guards
  ThumbnailService.vb                   short-lived decodes + disk cache
  GalleryVideoFaults.vb                 failure taxonomy (§7)

Tester/test/Gallery/Gallery.Video.Tests/  (standalone console runner, repo pattern)
  Gallery.Video.Tests.vbproj
  Program.vb                            TestRunner.Run/Skip harness (copy of pattern)
  StateMachineTests.vb                  guards: double start/stop/dispose, seek-after-dispose
  FrameQueueTests.vb                    full/late/flush/pause/stop semantics, no unbounded growth
  MediaProbeTests.vb                    real ffprobe on synthetic MP4s (Linux-runnable)
  DecodeIntegrationTests.vb             REAL ffmpeg decode: open→frames→EOF (Linux-runnable)
  SeekIntegrationTests.vb               backward/forward/near-end/to-begin/pause-seek/seek-playing
  FaultIntegrationTests.vb              missing/locked/corrupt/unsupported/EOF/no-audio/no-video
  StressLoopTests.vb                    A↔B switch ≥50 iterations, worker/GPU/memory trend
  HardwareGatedTests.vb                 D3D11 present + NVDEC + perf: honest SKIPS off-hardware
```

### 3.3 Threading model (no UI blocking; bounded everywhere)

| Thread | Owns | Forbidden |
|---|---|---|
| **UI thread** | `PlaybackSession` API calls (all return immediately, queued commands), state events | demux, decode, seek file IO, present |
| **Decode worker** (1 per session) | ffmpeg subprocess pipes (video+audio), probe | touching UI, touching textures, long locks |
| **Render thread** | D3D11 device/context/swapchain, texture uploads, present | file IO, blocking on decode locks |
| **Audio thread** (NAudio internal) | WasapiOut buffer fill | GPU work |

Cross-thread handoff: `FrameQueue` only (bounded). Every frame is a
`PlaybackFrame` with dispose-once guard — identical discipline to
`D3D11VideoFrame.vb` (§2.1). Ownership chain:

```
DecodeWorker creates BGRA8 buffer → FrameQueue (owns) → RenderThread takes
→ uploads into pooled dynamic texture → frame.Dispose() immediately after copy
(present loop never holds decoded buffers; queue is CPU-side, GPU gets copies)
```

**Why CPU buffers + one upload per frame for the MVP**: the repo's FFmpeg usage
is 100% subprocess-based (no library bindings exist). A decode subprocess
emits BGRA8 through a pipe; the render thread uploads it into a GPU dynamic
texture ONCE and renders GPU-only afterwards. There is **no GPU→CPU→GPU**
round-trip anywhere in this pipeline (the forbidden pattern). The zero-copy
NVDEC path (decode in-GPU → share surface to renderer) is a separately-tagged
hardware spike (§9) — NOT silently assumed, per owner spec.

### 3.4 Frame queue semantics (bounded; every case defined)

Configuration: `Capacity` (default 3 video frames ≈ 50 ms at 60 fps; a frame at
1080p60 BGRA is ~8.3 MB, so ~25 MB in flight — bounded by design), audio ring
~200 ms.

| Event | Behavior |
|---|---|
| Queue full (decode faster than present) | Decode worker **blocks** on enqueue (backpressure — decode is subprocess-paced; no memory growth possible) |
| Frame late (PTS < clock − 1.5×frame interval at dequeue) | **Drop at render**, count `DroppedLateFrames`, never present stale |
| Frame early | Hold until PTS window (clock ±1.5×interval) |
| Seek issued | Bump **generation token**; decode worker kills current ffmpeg, re-spawns at target; queue.Flush(generation) drops all frames of older generations atomically |
| Pause | Decode worker parks (pipe drained and ffmpeg SIGSTOP-free exit); queue drains to render-side hold buffer ≤1 frame; clock freezes at pause PTS |
| Stop/Dispose | Flush(generation=∞), kill both subprocesses (JobObject-style cleanup is capture-side precedent; here: explicit Kill + WaitForExit(timeout) + pipe dispose), dispose all queued frames |

### 3.5 PlaybackSession lifecycle (9 states, owner-mandated)

```
Created → Opening → Playing ⇄ Paused ⇄ Seeking (Pause↔Seek both directions)
                      │          │         │
                      ▼          ▼         ▼
                   Stopping → Stopped
                      (any state) ──fault──► Faulted
Created/Stopped/Faulted/Playing/Paused/Seeking/Stopping/Opening ──dispose──► Disposed
```

Guard rules (all enforced + unit-tested):
- `Play()` valid only from `Playing→no-op, Paused/Stopped/Opening-complete`; double-start guarded (no-op or fault per state table).
- `Stop()` idempotent; `Seek()` rejected in `Opening/Stopping/Stopped/Faulted/Disposed`.
- `Dispose()` from ANY state; idempotent (Interlocked guard, same pattern as `D3D11VideoFrame.vb:192`); after dispose: every API returns fault, render loop exits, no stale callbacks (generation token checked on every event fire).
- `Faulted` carries a `GalleryVideoFault` reason (§7) and keeps the session dumpable for diagnostics; recovery = Stop → new Open.

### 3.6 A/V sync (one timestamp domain)

- **Domain**: 100-ns QPC ticks — the repo's existing convention
  (`FrameDiagnostics` capture/PTS ticks, `AudioPositionTracker` QPC math).
- **Master clock**: audio sample-position when an audio stream is being
  rendered (samples played ÷ sample rate — NAudio exposes IWavePlayer
  playback position); else QPC wall-clock from play-start.
- Video frame presented when `|framePts − masterClock| ≤ 1.5×frame-interval`;
  early → wait; late → drop (counted).
- Subprocess decode has no shared in-process timestamps — PTS comes from
  `showinfo` stderr parse (video) and sample counting (audio), both normalized
  to the same 100-ns domain, anchored at the seek target (`-ss` gives the
  starting PTS; `showinfo` pts_time is absolute — no drift between streams).
- MVP tolerance: one frame interval; measured skew is reported, not hidden
  (repo culture: measured results only).

### 3.7 Seek protocol (stale-frame-free, owner-mandated cases)

1. `Seek(t)` accepted in `Playing/Paused` only.
2. State → `Seeking`; generation++ (all in-flight frames become garbage).
3. Kill video+audio ffmpeg; drain queues (flush by generation).
4. Spawn ffmpeg with `-ss t` (input-side, keyframe-fast) — MVP accepts
   keyframe-accurate seek; frame-accurate refine (`-ss` output-side, slower)
   is a config flag `SeekAccuracy` defaulting to Keyframe for 1080p60 UX.
5. Restart decode; first decoded frame ≥ t presents; clock re-anchors at t.
6. Verified cases (integration tests, real ffmpeg): backward, forward,
   near-end, to-beginning, pause+seek, seek-while-playing, seek-failure
   (bad time) → Faulted with reason, session recoverable via Stop.

### 3.8 D3D11 resource ownership (owner-mandated table)

| Resource | Owner | Created | Destroyed |
|---|---|---|---|
| `ID3D11Device` + immediate context | `D3D11VideoRenderer` | Open (render thread) | renderer.Dispose (session Stop/Dispose) |
| Swapchain + backbuffer | `D3D11VideoRenderer` | Open (HWND from Gallery viewer) | renderer.Dispose; device-lost → recreate-once-then-Fault |
| Dynamic upload texture (pooled ×2, ping-pong) | render thread | first frame / resize | renderer.Dispose |
| Shader resource view per pooled texture | render thread | texture creation | texture disposal |
| Decoded BGRA8 buffers (`PlaybackFrame`) | FrameQueue → render thread | decode worker | dispose-once after upload copy |
| ffmpeg subprocesses + pipes | decode worker | Open/Seek | kill+dispose on Stop/Seek/Dispose |

Rules: no D3D11 object crosses threads except the device (created before
threads start, using `D3D11_CREATE_DEVICE_SINGLETHREADED` off — context used
exclusively on render thread); no shared-handle interop in the MVP (the
NVDEC/zero-copy interop is the separately-gated spike §9); every Dispose
swallows exceptions (repo pattern) and metrics count textures created vs
disposed (leak-invariant, same as DdagrabBackend).

---

## 4. Thumbnail separation (owner-mandated)

```
Gallery Index (UI) → ThumbnailRequest queue (bounded=4, coalesced by path)
                   → ThumbnailService: ONE worker, short-lived decode per request:
                       ffmpeg -ss <25% duration> -i file -frames:v 1 -f image2 pipe:1
                   → disk cache: <cacheDir>/<hash(path|mtime|size)>.jpg
                   → UI poll timer collects finished results (no thread leak)
```

- NEVER a full `PlaybackSession` per thumbnail (owner rule: no 100 decoders
  for 100 videos). One worker total; requests are cancellable (generation
  token per request); cache hit = zero decode.
- Failure → placeholder tile + reason recorded; cache negative-results too
  (corrupt files not re-probed every UI refresh).

## 5. File Format (probe, don't assume)

Owner rule: verify from REAL outputs. Three sources, in increasing authority:
1. **This box**: synthetic MP4s generated with real ffmpeg 7.1.5
   (H.264+AAC, CFR 60, yuv420p, start_time 0 — matching the validated product
   matrix in `HANDOFF.md` §3: "MP4: H264 … + AAC, start_time=0.000000 both").
2. **REAL ShadowPlay recording (pinned 2026-09-10)**: owner-machine ffprobe
   of `C:\Users\ScotcsDuluka\Videos\Shadowplay\Gallery\Record_2026-09-10_21-18-52.mp4`
   — run with the repo-distributed
   `Overlay\bin\Release\net10.0-windows10.0.26100.0\FFmpeg\ffprobe.exe`
   (which also proves the FFmpegLocator bundling works on the real box).
   **Measured facts (the format authority):**

   | Field | Measured value |
   |---|---|
   | Container | `mov,mp4,m4a,3gp,3g2,mj2` (mov/mp4, avc1+mp4a tracks) |
   | Writer | `Lavf62.13.101` — **our own LiveMuxSession** (product output) |
   | Video codec | `h264`, profile **High** (avc1) |
   | Pixel format | `yuv420p` |
   | Resolution | **1680×1050 — 16:10, both dims even** (the owner's monitor is 1680×1050; NOT 16:9) |
   | Frame rate | `r_frame_rate 60/1` nominal, `avg_frame_rate ≈ 59.88` — **slight VFR** |
   | B-frames | `has_b_frames=0` |
   | Color metadata | **all unknown** (range/space/transfer/primaries untagged) |
   | Audio codec | `aac`, profile **LC**, 48000 Hz, stereo |
   | Stream lengths | audio runs **~54 ms longer** than video |
   | start_time | ≈ 0 (both streams) |

   **Prototype-compatibility verdicts (each checked against code):**
   - **1680×1050**: D3D11 swapchain is created at the PROBED size
     (`D3D11VideoRenderer` ctor) and DXGI `Scaling.Stretch` maps it into the
     window — resolution-agnostic; both dims even so yuv420p→BGRA is trivially
     safe. Pinned in tests via `real_shape` synthetic (MediaProbeTests).
     Letterbox/aspect UI is a Gallery-UI wiring concern (§11.4), not engine.
   - **59.88 avg fps (VFR-ish)**: the clock is PTS-driven (showinfo
     `pts_time` → 100-ns ticks) and never assumes CFR; `avg_frame_rate` is
     used ONLY for the ±1.5×frame late/early window and seek step heuristics,
     where a 0.2% error is irrelevant. `ParseFrameRate` accepts `N/M`
     rationals and decimals. VFR-safe by construction.
   - **has_b_frames=0**: input-side `-ss` + showinfo sequential PTS — no
     reorder delay; matches the deterministic seek model (§3.7).
   - **Color metadata unknown**: the yuv420p→BGRA conversion happens inside
     the ffmpeg swscale, which applies its DEFAULT matrix (BT.601) to
     untagged sources. ShadowPlay content is effectively BT.709, so colors
     are expected to be slightly off until pinned. Honest handling: declared
     assumption + follow-up (§11.4 #2) — pin `in_color_matrix=bt709` and
     verify VISUALLY on the owner machine (Tier-3), never silently.
   - **Audio +54 ms trailing**: EOS is video-master (§3.4): at video EOF the
     clock freezes and state → Paused(EOS); up to ~54 ms of trailing audio
     may be truncated. Perceptually negligible; measured semantics documented
     here, drain-then-EOS listed as a follow-up (§11.4 #3).
   - **Lavf62.13.101 = product output**: the Gallery is its own recording's
     primary consumer — the synthetic matrix (source 1) and this real file
     agree on shape, so Tier-2 coverage is representative.

3. **Support policy (unchanged)**: supported set is probed, never assumed —
   now ENFORCED: the session open path faults `UnsupportedFormat` for anything
   outside `h264`/`yuv420p` with nonzero dims (design §7; session test in
   `SessionTests.Test_FaultMapping`, probe-side `MediaProbeTests.Test_WrongCodec`).
   MP4/H.264 variants (HEVC/QSV recordings noted in HANDOFF §5 Intel path)
   stay gated out until a real file is probed-for-real and the set is widened
   deliberately.

## 6. Performance measurement plan (owner-mandated, measured only)

Hardware-gated (GTX 1080 Ti machine): 1080p60 / 1440p60 / 4K60 synthetic
files → measure decode FPS, present FPS, dropped-late count, seek latency
(command→first-new-frame), CPU/GPU/mem via `System.Diagnostics` + counters,
50-iteration A↔B stress trend. Off-hardware: everything above reports SKIP —
honest gates, never converted to PASS (PROJECT_MEMORY rule).

## 7. Failure matrix (never crash, never deadlock, never leak)

| Case | Detection | Outcome |
|---|---|---|
| File missing | pre-open File.Exists + probe exit | Faulted.FileMissing (UI recovers via Stop) |
| File locked | probe/probe-spawn exception | Faulted.FileLocked |
| Corrupt/truncated | ffprobe invalid OR ffmpeg exits <1s with frames=0 | Faulted.CorruptFile (thumbnail: negative-cache) |
| Unsupported codec/pixfmt | probe codec_name/pix_fmt not in supported set | Faulted.UnsupportedFormat (lists probed codecs) |
| Decoder unavailable (ffmpeg missing/broken) | FFmpegLocator FirstUsable → "" | Faulted.BackendMissing |
| GPU device unavailable/lost | D3D11 device creation fail / DXGI error at present | recreate-once → Faulted.RendererUnavailable |
| No audio stream | probe: 0 audio streams | video-only mode (clock = QPC), logged, NOT a fault |
| No video stream | probe: 0 video streams | Faulted.NoVideoStream |
| Seek failure | ffmpeg restart fails / no frame in timeout | Faulted.SeekFailed (session stays usable at old position) |
| EOF | pipe closes after last frame | natural EOF: hold last frame, state→Paused(EOF) + EosReached event (no fault) |

All subprocess waits use timeouts; all pipe reads are async-drained
(the 64KB stderr deadlock lesson from `FFmpegLocator.vb` comments); all
Dispose paths swallow + count.

## 8. Test strategy (3 tiers, honest labeling)

1. **Unit** (runs HERE, Linux, real): state machine guards, FrameQueue
   semantics (full/late/flush/pause/stop/generation), clock math, probe JSON
   parser, thumbnail cache keys.
2. **Deterministic integration** (runs HERE, real ffmpeg 7.1.5 + synthetic
   MP4s): open→decode→EOF, all seek cases, all fault cases, stress 50×A↔B
   (worker-count + handle trend via `/proc`, memory trend).
3. **Real hardware smoke** (owner GTX 1080 Ti, Windows — SKIPS here):
   D3D11 present, WasapiOut, NVDEC spike, perf §6. Env gates mirror
   `HardwareGate.vb` / `RRT_FFMPEG` conventions.

## 9. Hardware decode evolution (tagged PoC, NOT claimed)

- **Spike (future, separately tagged proof-of-concept)**: NVDEC via FFmpeg
  hwaccel inside the decode subprocess emitting D3D11 surfaces, or in-process
  FFmpeg.AutoGen + d3d11va — both require DLL-bundling decisions that belong
  to the installer workstream (out of scope here).
- **MVP claim**: software-agnostic pipeline (decode happens inside ffmpeg;
  whether ffmpeg uses hw decoders internally is probed at runtime via
  `-hwaccels` and REPORTED, never assumed — fallback deterministic: sw).
- No performance claim without §6 measurements on real hardware.

## 10. Scope safety (untouched, enforced by diff)

Zero modifications: `CaptureEngine*/**`, `Engine/**`, `Overlay/**`, `Duluka/**`,
`API/**`, `Launcher/**`, `Notifier/**`, `installer/**`, `Web/**`, `Common/**`.
Additions only: `Gallery.Video/**`, `Tester/test/Gallery/**`, this doc,
`PROJECT_MEMORY.txt` (append-only note at the very end, per its own format).
Git safety: no reset/rebase/clean/stash/revert; before commit: full
`git status` / `git diff --check` / `git log -3` review.

---

## 11. Implementation status (prototype phase — updated 2026-09-10)

**Test result on the Linux dev box (ffmpeg 7.1.5, no GPU): PASS 53 / FAIL 0 /
SKIP 3 — the 3 SKIPS are the honest Tier-3 hardware gates (D3D11 present,
WasapiOut, perf matrix), never converted to PASS. 51 of those were verified
6× on 2026-09-10; the 53 count includes the two §5-pin tests added the same
day (real_shape 1680×1050 + wrong_codec probe gate). Fresh-sandbox note: the
first cold build exposed two defects in the runner tail committed in 7c49302
(explicit `Shared` in a VB Module + missing `Runtime.InteropServices` import)
— both compile-time only, fixed before this run; shard discipline now
includes a cold build.**

### 11.1 Delivered

| Design item | File(s) | Status |
|---|---|---|
| 9-state lifecycle + static truth table | `PlaybackState.vb` | done + unit-pinned |
| Probe (ffprobe JSON authority) | `MediaProbe.vb`, `MediaInfo.vb` | done (real ffprobe tests) |
| Bounded queue, generation flush | `FrameQueue.vb` | done (leak invariant tested) |
| Subprocess decode (video showinfo PTS + audio s16le) | `FfmpegDecodeWorker.vb` | done (real ffmpeg tests) |
| QPC master clock (100-ns domain) | `PlaybackClock.vb` | done (fake-QPC tests) |
| Render contract + headless proof sink | `VideoRenderSink.vb` (added vs §3.2 list) | done |
| **PlaybackSession orchestrator** (open/play/pause/seek/stop/dispose, EOF→Paused+Eos, seek-failure recovery, fault taxonomy) | `PlaybackSession.vb` | done (session tests incl. 50× stress) |
| D3D11 present (zero-shader CopyResource path, device-lost recreate-once) | `D3D11VideoRenderer.vb` | compiled-anywhere; runtime-gated (Tier 3) |
| Audio render (WasapiOut — the product's existing stack) + sample-position clock | `AudioRenderer.vb` | compiled-anywhere; runtime-gated (Tier 3) |
| ThumbnailService | — | NOT in this phase (design §4 stands; build after owner validates playback) |

### 11.2 Documented deviations from §3.4/§3.7 text

1. **Pause = park the decoder** (kill the ffmpeg subprocess), not "drain to a
   render-side hold ≤1 frame". Same invariant (bounded memory, zero growth,
   no subprocess CPU while paused), fewer moving parts. Resume respawns a new
   decode generation at the paused PTS — the same machinery seek uses.
2. **Single-decoder invariant** (enforced + tested): one session owns at most
   ONE live decode generation at any moment. `Resume-from-pause` kills any
   live worker before spawning (a paused-seek leaves its generation running).
3. **Post-seek presentation gate** is (generation, sequence)-ordered — a raw
   sequence compare would suppress every frame of a new generation forever.
4. **EosReached fires BEFORE the EOF waiter gate opens** (wake-before-notify
   race otherwise).
5. **Seek clamp**: target is clamped to `[0, duration − 1.5·frame]` when the
   probe knows the duration — a generation spawned exactly at EOF emits 0
   frames (ffmpeg exits cleanly) which reads as the corrupt-file signature.
6. **Post-EOF resume** steps back one frame interval so the final frames are
   re-emitted (same 0-frames-at-EOF reasoning).

### 11.3 Measured on this box (ENVIRONMENT REPORT — not a product claim)

- 1080p60 H.264 → BGRA8 subprocess pipe (actual pipeline, queue + drain):
  **82.2 decode-fps** (120 frames / 1459 ms) = ×1.4 real-time headroom on
  CPU alone. Present path + GPU measurements remain Tier-3 (owner machine).
- Full suite wall time ≈ 4 min (includes 2 × 50-iteration stress loops).
- **Runner hardening (added on re-verification)**: (a) suite-name CLI filter
  (`dotnet run -- SessionTests …`) for sharding on slow/CI boxes — exit code
  2 when a filter matches nothing, so a shard misconfiguration can never
  masquerade as green; (b) deterministic exit: stdout/stderr flush →
  kill-leftover-direct-children sweep (/proc ppid scan) → libc `exit()` on
  Unix (Environment.Exit kept on Windows). Rationale: ~3 of 5 observed runs
  on this box held the CALLING shell open after a complete green summary,
  although a direct /proc probe proved the app itself exits cleanly — the
  residual flake is at the sandbox/supervisor boundary for runs that spawn
  many ffmpeg children (Tier-1-only runs return in <1 s). The sweep is
  product-relevant hygiene regardless: no ffmpeg child may outlive the
  runner on the owner's CI either.

### 11.4 Known follow-ups (ordered)

1. Wire `FFmpegLocator` into product runtime paths (prototype takes explicit
   binary paths — deliberate, no silent PATH assumptions).
2. ~~Real-recording validation~~ **DONE (2026-09-10)**: owner-machine ffprobe
   of a real ShadowPlay recording pinned §5 (1680×1050 / 59.88 avg / untagged
   color / AAC LC / +54 ms audio tail); compatibility verdicts + the
   `UnsupportedFormat` gate landed with it.
3. Pin the color matrix: untagged ShadowPlay sources go through swscale's
   default (BT.601) today — add `in_color_matrix=bt709` to the video decode
   `-vf` chain and verify VISUALLY on the owner machine (Tier-3) before
   claiming correctness (§5 verdicts).
4. Trailing-audio policy: video-master EOS may truncate up to ~54 ms of
   audio tail; if the owner hears a cut, switch EOS to drain-then-freeze
   (hold last frame until the audio buffer empties, bounded).
5. Tier-3 hardware pass on the GTX 1080 Ti machine: D3D11 present, WasapiOut,
   NVDEC/D3D11VA availability, §6 perf matrix (1080p60/1440p60/4K60 + the
   real 1680×1050 shape) + 50-iteration on-hardware stress.
6. Gallery UI wiring: session ↔ `[Gallery]/[1] Main.vb` (handle → renderer,
   buttons → session commands — UI thread never blocks by contract); decide
   letterbox-vs-stretch for 16:10 content in non-16:10 windows.
7. ThumbnailService (§4) + listing/index.
8. Frame-accurate seek mode (`SeekAccuracy` flag, §3.7) if keyframe seek
   proves too coarse on real recordings.
