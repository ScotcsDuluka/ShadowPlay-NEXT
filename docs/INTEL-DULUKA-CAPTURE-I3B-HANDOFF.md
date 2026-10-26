# Intel Duluka Capture — I3-B handoff

2026-10-25, Asia/Bangkok. HUAWEI-MateBook only.
Workspace `C:\My Project\NVIDIA-Shadowplay`; branch `intel/duluka-capture`.

**I3-B synthetic scope completed: 2,056 Passed / 0 Failed / 33 Skipped.**
Baseline 1,725 retained; 331 new lifecycle/session/color assertions. Synthetic
audio mux **13 Not Run**, excluded from both Passed and framework Skipped totals.
Scoped Release 0 warnings/0 errors; main Release passes with 3 existing warnings.
See [full report](reports/intel-duluka-i3b-2026-10-25/README.md) and its manifests.

**Do not start Live Acceptance.** `intel_native_live_input_not_verified` remains
unchanged. No Desktop/game/real audio, AORUS, GPU-setting changes or Git mutations
were performed. Existing uncommitted work was preserved, including concurrent
documentation changes outside this task. Canonical architecture is unchanged.

Read AGENTS.md, canonical Session-Centric Contract/Thai companion, original Intel
architecture, I2/I3 handoffs and the composition/I3-B reports before continuing.

## Data plane and ownership

```text
Persistent source device / DXGI producer (not started in these tests)
   stamps ActualTarget + SourceGeneration + QPC100ns on D3D11VideoFrame
      → BgraSessionSource (existing IVideoFrameSink transfer protocol)
      → BgraFrameAdapter / same-device protected-context staging
      → owned NV12, original source timestamps
      → existing CaptureSession / SessionFrameRouter (session epoch + CFR PTS)
      → native QSV → existing SessionPacketSink / LiveMuxSession
      → full decode + frame/packet/timeline/color verification → Saved
```

- `SourceLifecycle.vb` adds source stamps/fault sink, monotonic generation IDs,
  signed QPC unit conversion and immutable BT.709 Limited/Full descriptor.
- `D3D11VideoFrame` has a stamped constructor; its old constructor is retained.
  Ddagrab stamps its actual target/generation after duplication recreation.
  Optional lifecycle sink gets access-lost/disconnect/device-removal notifications.
  Existing consumers do not opt in to these notifications.
- Future authorized producer wiring: initialize/revalidate backend, call
  `PrepareSourceGeneration()` while idle, construct the sink from the returned
  stamp, then Start with that sink. Reservation is metadata only, consumed at
  the next recreation; later recreations allocate a new generation. None of
  Initialize/Start/DuplicateOutput/AcquireNextFrame was run on live DXGI here.
- `BgraSessionSource` owns accepted raw frames and bounded queue; Dropped retains
  ownership upstream. Read owns a dequeued frame through conversion, then disposes
  it before returning independently owned NV12. The session envelope owns NV12
  until Submit returns. The router's normalized frame is a borrowed metadata view.
  Native encoder copies delayed input into its existing owned pool.
- Source is **not a clock**. `SessionFrameRouter.RouteSource` selects the session
  QPC epoch, validates monotonic raw timestamps/source sequence and maps to CFR
  encoder PTS. Jitter tolerance is half a frame; larger gaps fail explicitly.
  Generation authorization may reset source ordinal but never session epoch/PTS.
- Session owns use/stop of the supplied source; its caller owns final Dispose
  after session completion, as for other borrowed engine resources. Stop cancels
  waiting/copy reads and drains owned queue. Dispose waits for conversion before
  releasing adapter/COM references. Concurrent shutdown is synthetically tested.

## Recovery policy

AccessLost enters SourceInterrupted with a bounded monotonic deadline. Only an
explicit `AuthorizeRecovery(currentTarget, newerGeneration)` may transition, at
most once per source instance. Same target, source native device and session clock
must survive. Stale callbacks are refused (producer retains disposal), stale
in-flight output is disposed and cannot reach the encoder. A larger timestamp gap
still fails; no placeholder or silent time compression is introduced.

Disconnected, DeviceRemoved, OutputChanged and invalid ownership/device data are
terminal. A fatal event escalates a previous AccessLost. DeviceRemoved cannot
authorize same-session recovery. Tests show the original RecordingEngine creates
a NEW session/revalidates/replaces its faulted encoder through its normal factory;
the source never creates a replacement GPU device. This is an injected lifecycle
signal test, not physical GPU removal.

Default Desktop composition is still gated. Production producer start/stop,
shared NT/keyed-mutex handling, actual source contention and driver failure paths
need live-specific validation before factory wiring. Same LUID never permits
foreign-device CopyResource.

## Color and private native ABI

`VideoColorDescriptor` fixes primaries/transfer/matrix to BT.709 (code 1) with an
explicit Limited/Full flag. Legacy unspecified color remains the default. Explicit
descriptors flow startup → EncoderConfig → conversion → router view → QSV.
Other providers reject the new explicit color setting until independently wired;
NVENC source/defaults were not changed.

The native Intel bridge is now **private ABI 3**. Rebuild native and managed
outputs together; ABI 2 artifacts fail the check before creation. The descriptor
uses official `mfxExtVideoSignalInfo`; GetVideoParam must confirm negotiated values.
MP4 verification requires the requested color tags. Tests also extract elementary
H.264 and probe it to prove VUI rather than container metadata alone.

Actual VBR/CQP × Limited/Full cases each produce 60 submitted/encoded/muxed/decoded
frames, all moving markers, two packets delivered at flush in the final run,
BT.709 tags in MP4 and H.264, zero captured Desktop frames/audio. One case explicitly
changes generation 7→8 after AccessLost while retaining session ID/clock/encoder/mux.
Hardware: Intel UHD 8086:9B41, driver 27.20.100.8984, LUID `00000000:0000bf60`,
output 0, installed runtime 1.34/hardware D3D11 implementation 770. LUID is evidence
for this boot only. Dispatcher remains pinned 2.16.0; handle-ownership gate remains.

## Reproduce and remaining gates

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\docs\reports\intel-duluka-i3b-2026-10-25\Run-Regression.ps1'
```

Builds native, runs I1 safe/offline, retained video controls, I2 native, scoped
Release, I3 transport/composition and `--i3b`. No synthetic audio suite is invoked.
Native dependencies and retained control files must exist; absence fails.

CBR remains known failed/rejected. No real driver removal/hang, real DXGI frame,
shared-resource protocol, HDR/rotation/scaling, long-run/1080p performance,
audio or Exclusive API/game matrix is certified. Existing legacy Ddagrab driver-
hang shutdown behavior (including its forced cleanup after the join budget) was
not exercised/redesigned by synthetic testing; audit it before live acceptance.
Color metadata and raw conversion are verified, not arbitrary color-managed
playback/HDR fidelity. FPS in synthetic clips is timeline rate, not throughput.

Before Live Acceptance, finish compatible producer/session start-stop wiring,
shared-resource policy and real shutdown audit, obtain an explicitly approved
privacy-cleared scene and bounded scope, then measure actual source/QSV/MP4 output.
Do not remove the gate, change display settings, edit NVENC/AORUS, or commit/push.
Stop after this handoff; no automatic live continuation is authorized.
