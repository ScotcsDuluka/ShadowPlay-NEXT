# Duluka Capture — I2 implementation / I3 handoff

Date: 2026-10-24, Asia/Bangkok. Machine: HUAWEI-MateBook.
Workspace: `C:\My Project\NVIDIA-Shadowplay`, branch `intel/duluka-capture`.

I2 implements and tests the native **owned CPU NV12 → Intel H.264** backend
inside the existing Duluka session. **Do not start Live Desktop Capture.** The
production Intel Desktop pair is still blocked as
`intel_native_live_input_not_verified`. This is an encoder/synthetic-session
milestone, not certification of Desktop, Borderless or Fullscreen Exclusive.

Read `AGENTS.md`, the canonical
`docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md`, its Thai companion,
the original Intel architecture, then
`docs/reports/intel-duluka-i2-2026-10-24/README.md` before editing.
The workspace includes concurrent/uncommitted I1 and I2 work. No source was
committed, pushed, staged, reset, deployed to AORUS, or tested on live inputs.

## Implemented path and source entry points

All implementation paths here are under
`Project/NVIDIA Capture/EngineCapture/Duluka Capture/`:

1. `Encoding/Intel/Native/`: pinned portable x64 toolchain, official Intel
   dispatcher/headers v2.16.0, C ABI 2 bridge. Native runtime on this machine is
   **1.34**, not 2.16. The bridge creates its D3D11 device by exact LUID, binds
   it before `MFXCreateSession`, reads back the actual runtime device's parent
   DXGI adapter and hardware/D3D11 implementation, and rejects mismatches.
2. `Encoding/Intel/DulukaEngine.Encoder.Qsv/`: `IEncoderRuntimeBackend` plus
   additive `IEncoderSubmissionBackend`. NV12 is copied into encoder-owned
   aligned native surfaces. No encoder-owned file, process, audio or clock.
3. `Recording/DulukaEngine.Recording/SessionFrameRouter.vb`: one generation,
   exact display identity/output, output surface dimensions, increasing source
   sequence, and session-normalized CFR PTS. `SessionFrameInput` is the sole
   source-frame owner; the encoder borrows until `Submit` returns.
4. `SessionPacketSink.vb`: shared immediate/delayed/flush packet consumption,
   sole packet disposal after the mux copies bytes, separate acceptance/output/
   mux-queue counters. `CaptureSession.vb` now explicitly drains the encoder to
   this same sink before stopping encoder/mux. Tail-fill uses accepted input
   count so encoder delay does not cause duplicate submissions.
5. `CaptureSession.OwnedFrames.vb`: generic controlled-input entry on the SAME
   `CaptureSession`, reached through `RecordingEngine.StartOwnedFrameSession`.
   One SessionId/result/finalization; audio explicitly rejected. It never calls
   source.Start. The test factory supplies a synthetic NV12 source plus a real
   separately created D3D11 device for identity proof. It uses normal engine
   initialization, identity checks, factory rebuild/recovery and shared mux.
6. `Composition/RecordingBackendFactory.vb`: input-specific native encoder
   creation is available through `CreateEncoderForInput(..., Nv12, ...)`.
   Default Desktop composition/`RequireWired()` still refuses Intel's unverified
   BGRA texture pair. NVIDIA uses the original runtime adapter and NVENC tree.
   Do not change the Desktop gate merely because synthetic encoding passed.
7. `Composition/EncoderConfigurationPolicy.vb`: explicit Intel VBR/CQP mapping,
   CQP has no bitrate target, preset `veryfast`, no CBR fallback. NVIDIA defaults
   retain their existing policy.
8. `Recording/Media/DulukaEngine.Media/`: existing production
   `src/[API]/CaptureMediaInspector.vb` is compiled once as linked source in this
   shared assembly. Session acceptance adds exact decoded/declared/read frames,
   packet count, dimensions, FPS, duration and every MP4 packet PTS/DTS/duration.
   Main capture and media tests reference that same assembly.

## Semantics the next phase must preserve

- `Accepted=True, Packet=Nothing` means accepted/delayed, **never retry input**.
  Input ownership remains with `SessionFrameInput`. Flush transfers each packet
  to the session sink, including its disposal obligation.
- Native pool entries are reusable only when driver lock count is zero. Sync
  jobs own their bitstreams; queues/buffers/deadlines are bounded. Do not free
  surfaces or device before closing the encoder/runtime session.
- Explicit session flush is required before QSV `Stop`; a pending-output stop
  fails rather than silently discarding output. A faulted backend is disposed
  and replaced through the normal engine factory for a NEW session. Mid-session
  device recovery/source switching is not implemented by this milestone.
- Exact session timestamps are 100 ns; QSV uses 90 kHz and maps back to submitted
  inputs. No B frames are supported in this path. The session validates no
  reordering and muxes validated CFR slots with opt-in `setts` timebase stamping;
  legacy mux timing remains the default. Do not use that mode for arbitrary
  timestamped or reordered packets.
- Read native device identity from the runtime device, not from requested config
  or successful encode exit. Runtime 1.34 on this driver returns a BORROWED
  `MFXVideoCORE_GetHandle` D3D11 pointer. Releasing it caused a teardown access
  violation. Current code releases only owned QueryInterface references, holds
  its own device through `MFXClose`, and rejects unverified runtime versions.
  Validate handle semantics on other runtime/driver combinations before widening
  this compatibility gate. Current documentation describes different semantics.
- `FramesCaptured=0` for synthetic tests, distinct `SyntheticFramesConsumed`.
  Metadata/source-device evidence must not be presented as captured pixels.
  `Saved` for the controlled-input entry requires successful full decode plus
  matching packet/frame/timeline evidence. Encoder exit zero is insufficient.
- VBR/CQP proof: 320×180, 30 fps, 60 frames, GOP 30, depth 4, VBR 2/4 Mbps,
  CQP 24, Intel UHD 8086:9B41, physical LUID `00000000:0000bf60`, output 0.
  Both paths produced 60 decoded frames, including 5 packets delivered by flush.
  Source-buffer disposal was checked by decoding every moving marker frame.
- **CBR remains known failed and rejected.** The retained CLI baseline still has
  a complete container but decode corruption. I2's VBR/QP union initialization
  fix is not proof of the unrelated baseline CBR cause or a CBR repair.

## I3 work boundaries

Continue in the canonical graph:

```text
Selected Video Source → FrameRouter → same Duluka RecordingSession
  → selected Encoder → Encoded Packets → same Session Muxer → Verified MP4
```

Prioritize an explicitly validated **BGRA D3D11 texture → NV12** transport for
the Intel source/encoder pair (or a separately declared compatible source).
Choose and document CPU staging vs GPU conversion/native texture import,
including row pitch, color range/matrix, shared-device rules, copy completion,
resource lifetime, adapter LUID and source generation. Keep acquisition API and
encoder selection independent. Do not add an Intel-specific recording engine.

Before any live input test, obtain explicit authorization and a privacy-cleared
scene/test scope. Synthetic-owned surfaces and metadata inspection may continue
within an authorized development phase. Real display/loopback/microphone,
borderless/exclusive API/game testing, automatic source transitions, black-gap
composition, rotation/HDR, 1080p throughput, hardware device-loss injection and
long-run stability are **not proven**. Fullscreen Exclusive claims require a
recorded per-API/game/mode matrix; no universal support claim is permitted.

Native NVENC hardware was unavailable on this machine. Keep its 5 hardware cases
as skipped; offline regression and an unchanged NVENC source tree are evidence
of compatibility checks, not an AORUS hardware run. Do not touch AORUS without a
separate authorized scope. Do not commit/push or change Windows GPU settings.

## Reproduce safe regression

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI2Regression.ps1'
```

Use `-BootstrapNative` only when portable native dependencies are absent. The
runner builds native Release, builds capture Release, retains all I1 safe cases,
validates three historical synthetic media files (including CBR negative case),
tests native synthetic sessions/fault/flush/recovery, then shared mux faults with
generated video/sine data. The historical media files live at paths recorded in
the original report; absence is a failure requiring evidence recovery, not an
automatic skip or new live recording.

Do not run the Video suite without `--offline`, launch a production worker, or
run unrelated recording integration entrypoints to satisfy test discovery.
The detailed report, JSON evidence, source hashes and local logs are in the I2
report folder. Stop after this handoff; I3 live work has not been authorized.
