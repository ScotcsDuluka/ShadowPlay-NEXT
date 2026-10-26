# Intel native QSV — I2 scope

`DulukaEngine.Encoder.Qsv` implements `IEncoderRuntimeBackend` and the additive
`IEncoderSubmissionBackend`. It encodes **owned CPU NV12** through native Intel
H.264, using an explicitly selected D3D11 adapter. It does not acquire video,
open audio, launch FFmpeg, create files, or own a recording timeline.

Proven on HUAWEI-MateBook / Intel UHD 9B41 / driver 27.20.100.8984 / runtime
API 1.34: synthetic 320×180, 30 fps, VBR 2 Mbps / peak 4 Mbps, CQP 24, GOP 30,
no B frames, async depth 4. Wider configuration bounds are validation limits,
not a tested compatibility or performance matrix. CBR is rejected as a known
failure. Direct QSV D3D11 texture import, **live** desktop acquisition, real audio and
Desktop/Exclusive transitions remain unverified or unimplemented. **Synthetic** BGRA staging and BT.709 Limited/Full conversion/color-tag signaling are covered by the later I3-B results below.

## I3 follow-up — D3D11 synthetic staging and borrowed-frame adapter

The newer I3 pipeline produces **owned CPU NV12** using a separate, bounded D3D11 BGRA staging/readback and BT.709 limited-range conversion path, then feeds **this same QSV encoder and original RecordingSession**. No direct D3D11 QSV texture import was added to the native encoder. [Transport contract](../../Video/Transport/DulukaEngine.Video.Transport.D3D11/README.md).

The [I3 transport report](../../../../../../docs/reports/intel-duluka-i3-2026-10-25/README.md) recorded **1,453/0/33** passed/failed/skipped, and the later [I3 production-shaped frame-adapter report](../../../../../../docs/reports/intel-duluka-i3-composition-2026-10-25/README.md) recorded **1,725/0/33** across six successful orchestration stages (13 previously tested synthetic-audio assertions were deliberately not rerun in the adapter-only scope).

These are small **test-owned** Intel GPU surfaces and decoded synthetic media, **not an Intel Desktop recording or a production source/encoder connection**. The source's default Desktop gate `intel_native_live_input_not_verified` remains in force. Intel native **CBR remains rejected**, and playback colorimetry tags, input-recovery and genuine live source timing require separate verification.

## Build and dependency provenance

From the repository root:

```powershell
& '.\Project\NVIDIA Capture\EngineCapture\Duluka Capture\Encoding\Intel\Native\Build-NativeQsv.ps1' -Bootstrap
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI2Regression.ps1'
```

`-Bootstrap` downloads portable, pinned archives and official dispatcher source
into ignored `Native/packages`; it does not install software or change Windows
GPU settings. Subsequent builds omit `-Bootstrap`. Intel libvpl v2.16.0 is pinned
to `778a66d6c6537f08eabb91955dbbf1bce3812894`; archive URLs and SHA256 values
are in the build script. The x64 bridge and dispatcher are built in `Native/bin`.
Managed builds copy these DLLs and the dispatcher license when present; they
do not implicitly download/build native tools. A missing DLL fails initialization.
The existing NVIDIA build can still compile without these native artifacts.

I3-B update (2026-10-25): the private native bridge now uses **ABI 3** for optional
color signaling. Rebuild native and managed artifacts together; stale ABI 2 DLLs
fail the explicit ABI check. Existing unspecified-color profiles retain their
old parameters. Explicit BT.709 Limited/Full descriptors are validated against
runtime parameters and tested in elementary H.264 VUI and finalized MP4. NVENC
is unchanged. See the I3-B report and handoff for source lifecycle/session routing.

The dispatcher is modern; the **installed driver implementation is API 1.34**.
Only API-compatible structures/functions are used. Modern 2.x surface acquisition
is not assumed. DLL ABI version 3 is checked before creation. VPL's upstream
license is copied beside the binaries; dependency/compiler archives are local
build inputs, not application payloads.

## Lifetime and timeline

- `Submit` borrows the source frame, copies into a native aligned NV12 surface
  pool, and returns explicit acceptance. `Accepted=True, Packet=Nothing` is delay;
  do not resubmit it. Driver locks protect pool entries until reuse is safe.
- Sync-point jobs retain their own bitstream buffers. Native and managed queues,
  surface count, bitstream growth, device-busy retries and flush deadlines are
  bounded. Native failure is terminal until disposal/new initialization.
- The session owns exact 100 ns PTS. QSV receives 90 kHz timestamps and returned
  timestamps map back to submitted inputs. This implementation requires no B
  frames and validates monotonic output with DTS=PTS.
- `Flush(sessionPacketSink)` delivers delayed packets to the **existing session
  mux**, then verifies no pending inputs/jobs/locked surfaces. Explicit drain is
  required before `Stop`: pending output causes failure rather than discarding
  packets. The synchronous NVENC implementation remains unchanged.
- Runtime 1.34 on this driver returns a borrowed D3D11 pointer from
  `MFXVideoCORE_GetHandle`. Releasing that borrowed reference caused a teardown
  access violation during development. The bridge releases QueryInterface-owned
  references only and keeps its device through `MFXClose`. Other runtime versions
  fail `qsv_runtime_handle_ownership_not_verified` until separately verified.
  This compatibility handling is deliberately narrower than current VPL docs.

## RecordingSession integration

The generic `RecordingEngine.StartOwnedFrameSession` / `CaptureSession.RunOwnedFrames`
entry uses `SessionFrameRouter`, `SessionPacketSink` and the same `LiveMuxSession`
as the existing engine. It requires video-only controlled inputs with one
generation, fixed output surface and session-normalized CFR timestamps. It never
starts the capture source. No alternate Intel recording engine exists.

`RecordingBackendFactory.CreateEncoderForInput` selects the tested NV12 encoder
after current-target validation. Default Desktop composition remains blocked as
`intel_native_live_input_not_verified`: its DXGI BGRA texture transport is not
implemented by this CPU NV12 backend. Tests inject an explicitly owned NV12
source factory; they do not change the production Desktop gate.

The session validates every output access unit before mux. Its opt-in CFR mux
mode stamps that validated timeline because a raw H.264 pipe has no packet PTS.
Full decode, declared/read frame counts, packet count, dimensions, FPS, every
MP4 PTS/DTS/duration and final duration must pass before `Saved` is reported.
Legacy NVENC mux timing options retain their defaults.

Evidence and remaining work: `docs/reports/intel-duluka-i2-2026-10-24/README.md`
and `docs/INTEL-DULUKA-CAPTURE-I3-HANDOFF.md` at repository root. These implement
the canonical session contract; they do not replace or amend its invariants.
