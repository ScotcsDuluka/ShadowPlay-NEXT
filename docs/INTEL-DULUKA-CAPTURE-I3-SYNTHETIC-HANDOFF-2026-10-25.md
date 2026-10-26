# Duluka Capture — I3 synthetic transport handoff

Date: 2026-10-25, Asia/Bangkok. HUAWEI-MateBook only.
Branch `intel/duluka-capture`; workspace `C:\My Project\NVIDIA-Shadowplay`.

The requested **synthetic transport slice is verified**. Live Intel Desktop,
audio and Fullscreen Exclusive remain unverified/disabled. Read the final
[I3 evidence report](reports/intel-duluka-i3-2026-10-25/README.md) for actual
regression/build results, source hashes and media. The earlier in-progress
handoff is a historical partial snapshot, not the latest result.

Mandatory architecture remains `AGENTS.md`, the Session-Centric Contract/Thai
companion, original Intel architecture and I2/I3 handoffs. No canonical invariant
was changed. No source/file was committed, pushed, reset, staged or synchronized
to AORUS. Existing uncommitted I1/I2 work and historical evidence were preserved.

## Implemented path

```text
Synthetic owned BGRA D3D11 texture, on selected physical Intel device
  → BgraStagingTransport: CopyResource / bounded nonblocking Map / pitched CPU rows
  → existing BgraToNv12Converter: owned NV12, BT.709 limited
  → existing SessionFrameRouter / same RecordingEngine + CaptureSession
  → native Intel QSV → existing packet sink / mux → verified MP4
```

The reusable transport is in
`Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/`.
It references only video contracts and the existing Vortice 3.6.2 D3D11/DXGI
packages, not an encoder or recording engine. It consumes the existing
`ID3D11VideoFrame` and produces `INv12VideoFrame`. Caller source ownership remains
explicit. It owns its own COM references and one reusable staging surface.

Its README documents dimensions/format/generation/target/resource and external
immediate-context serialization requirements. Same adapter LUID does not imply
same device. Different-device/shared-handle inputs are rejected pending a real
sharing/synchronization protocol. Actual software flags override claimed metadata.

`QsvNative.Tests/I3PixelTransport.cs` preserves the initial pitched CPU converter
test and uses the same session. `I3D3D11Transport.cs` adds real synthetic GPU
textures, VBR/CQP sessions, marker/color/ownership/negative tests. The test fixture
source has an explicit owned device and configurable test surface; it never
calls a capture API. Production DXGI and the NVENC tree were not edited.

## Verified and preserved

- I3 transport tests: **375 passed**, including 83 CPU converter/session checks
  and 292 GPU transport/session/fault-input checks. Each synthetic session uses
  one real session ID, full decode and the existing packet/timeline acceptance.
- CPU BGRA case: 320×180, 30 fps, 60 decoded frames. GPU cases: 322×180, 30 fps,
  60 submitted/encoded/muxed/decoded frames each, actual row pitch 1344. Delayed
  packets reach the mux; final exact flush counts are in each result JSON.
- Physical source/staging/encoder LUID is `00000000:0000bf60`, output 0,
  Intel 8086:9B41, driver 27.20.100.8984, native API 1.34/hardware D3D11 770.
  LUID is observed evidence for this boot; no code hardcodes it.
- Every successful map/unmap pairs; borrowed GPU frames survive Convert, then
  are disposed before CPU NV12 submission. CPU frames remain valid until Submit
  returns. Native pending jobs/locked surfaces are zero after flush.
- Wrong target/output/generation, actual format/array, foreign-device texture,
  software adapter, relabelled device LUID, disposed resources and pre-copy
  cancellation fail honestly. Existing I2 native failure/recovery cases remain.
- The new `--evidence-dir` test option and I2 runner `-EvidenceDirectory` keep
  this rerun's I2 evidence under the I3 report instead of overwriting I2 history.
  Default I2 behavior and all original assertions remain.

## Remaining work before any live test

Do not remove `intel_native_live_input_not_verified`. The transport is exercised
by a synthetic fixture; it is not wired into the default production Desktop
composition. Shared NT texture import/keyed mutexes, source-context concurrency,
actual DXGI source integration and transport/source failure policy still need
explicit compatible composition and tests. Do not use foreign-device CopyResource.

The CPU conversion matrix/range and raw GPU RGB patches are verified, but the
current H.264 stream has **unspecified colorimetry tags**. Add an explicit common
color descriptor and verify Intel bitstream signaling/color-managed playback
before claiming live color correctness. This was recorded rather than changing
I2/NVENC profile semantics implicitly.

Real Desktop/Borderless/Exclusive pixels, audio, runtime device removal, forced
GPU-copy timeout, HDR/rotation/scaling, 1080p performance, long-run stability and
source transitions are not proven. CBR remains known failed/rejected. A passing
negative corruption test does not fix CBR. No universal Exclusive support claim.

Before a later live test, obtain an explicit user-approved privacy-cleared scene
and bounded test scope. The current instruction authorizes synthetic input only.
Keep the one session/clock/encoder/mux graph. Do not create a parallel Intel engine,
change Windows display settings, edit NVENC/AORUS, or commit/push without direction.

Safe reproduction:

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI3Regression.ps1'
```

Native I2 prerequisites must already exist. Missing tools/artifacts fail instead
of causing a live capture or an unreported skip. Stop here for the next scoped
development request.
