# D3D11 BGRA staging transport — I3 synthetic scope

`BgraStagingTransport` is a source-side format/ownership adapter. It borrows an
`ID3D11VideoFrame` and returns an independently owned `INv12VideoFrame` suitable
for the existing FrameRouter/RecordingSession. It has no capture/acquisition,
encoder, recording ID, clock, audio, file or mux ownership. The live Intel
composition gate is unchanged.

The caller supplies a freshly resolved physical `CaptureTarget`, the actual
source device, fixed surface dimensions and source generation. Constructor
attestation reads the D3D11 device's parent DXGI LUID/vendor/device/software flag.
Every conversion validates target/output identity, generation, frame metadata,
actual texture descriptor and actual texture device. A different D3D11 device
is rejected even on the same adapter. Shared NT handles/keyed mutexes are not
implemented and are rejected before copy.

### I3 composition adapter follow-up — 25 October 2026

The additional `BgraFrameAdapter` accepts **borrowed, production-shaped `ID3D11VideoFrame` frames** stamped with a trusted physical `CaptureTarget` and source generation. It lazily borrows the upstream texture's **actual device** and checks device/context protection, current output identity, generation, real descriptor, source size and per-call target envelope. It delegates to the existing staging transport and returns **an owned NV12 frame**, leaving the borrowed upstream frame with its original owner. A separate QueryInterface-owned COM device reference avoids releasing the cached Vortice device wrapper; existing returned NV12 frames are disposed once by the caller.

The [new frame-adapter report](../../../../../../../docs/reports/intel-duluka-i3-composition-2026-10-25/README.md) documents **1,725 passed / 0 failed / 33 skipped** across six regression stages. It explicitly does **not** connect this adapter to production Desktop acquisition, QPC-to-session PTS normalization, real capture recovery, colorimetry signaling or a live Intel recording. **The live-input gate remains unchanged.**

Supported input contract:

- Unrotated target (DXGI identity rotation), even surface dimensions in the
  bounded 16..1920 × 16..1080 validation range. Tested dimensions are narrower
  than these validation bounds; no throughput matrix is implied.
- One `B8G8R8A8_UNorm` 2D texture, one array slice/mip, no MSAA, default or
  immutable usage, no resource misc flags. SDR RGB code values; alpha is ignored.
  No HDR, sRGB texture reinterpretation, scaling, crop or rotation conversion.
- Stable borrowed source through `Convert`; the caller serializes any other
  use of this device's immediate context. The transport's lock serializes its
  own calls, not arbitrary external context users.

The transport owns separate COM references to its device/immediate context and
one reusable read-only staging texture. `CopyResource` is followed by `Flush`
and nonblocking `Map(Read, DoNotWait)` polls. Busy returns retry within a bounded
Stopwatch deadline/cancellation check. A successful Map establishes GPU copy
completion. Only meaningful row bytes are copied; driver padding is never read.
The actual row pitch is passed to the shared BT.709 limited-range BGRA→NV12
converter. Every successful map has a finally-protected Unmap before return.
Returned CPU data remains valid after the caller disposes the source texture.

Validation/pre-copy cancellation is retryable. Copy/map/conversion errors mark
the transport faulted; dispose and create a new transport before using it again.
No hidden retry on another GPU or source-generation migration occurs. Polling
is bounded; actual device-loss/GPU-hang fault injection has not been performed.
The caller owns disposal of each returned frame and the transport.

Evidence: HUAWEI-MateBook Intel UHD 8086:9B41, runtime 1.34. Owned 322×180 BGRA
textures (upload pitch 1320) mapped with real GPU staging pitch **1344**. VBR and
CQP each delivered 60 frames through the same existing RecordingSession/QSV/mux
with full decode and moving-marker checks. GPU/CPU owners each disposed 60
frames; 60 maps/unmaps paired; staging created/disposed 1/1. Raw RGB color patches
also verify channel order and BT.709 limited Y/UV values through GPU readback.

The generated H.264 currently has **unspecified colorimetry tags**. Raw conversion
color correctness is tested; color-managed playback is not certified. Add and
verify the session/encoder color descriptor and bitstream signaling before live
color acceptance. This project is not wired into the default Desktop factory.

Safe test command from repository root:

```powershell
dotnet run --project 'Project\NVIDIA Capture\EngineCapture\QsvNative.Tests\QsvNative.Tests.csproj' -c Release -- --i3-transport
```

Requires the built I2 native QSV DLL/dispatcher and installed supported runtime.
Full regression: `Project/NVIDIA Capture/EngineCapture/Invoke-IntelCaptureI3Regression.ps1`.
See `docs/reports/intel-duluka-i3-2026-10-25/README.md` for exact evidence and limits.
