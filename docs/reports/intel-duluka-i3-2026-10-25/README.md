# Duluka Capture — I3 synthetic transport evidence

Date: **2026-10-25, Asia/Bangkok**. Machine: HUAWEI-MateBook.
Workspace `C:\My Project\NVIDIA-Shadowplay`, branch `intel/duluka-capture`.
HEAD remains `015f0548f82b5c1ed4d9c352fe095cc55e9a5453`.

**Requested synthetic transport scope completed: 1,453 passed / 0 failed /
33 skipped**, including all **1,078/0/33** retained I2 assertions and **375** I3
checks. Transport/test Release build, native I2 build and main capture Release
build passed. This report does **not** qualify live Intel Desktop recording.

The supplied in-progress handoff, AGENTS.md, canonical Session-Centric Contract,
Thai companion, original Intel architecture, I2/I3 handoffs and I2 report were
read before source edits. Only this root agent was active. Concurrent/uncommitted
I1/I2 source was preserved. No desktop/game pixels, real audio, microphone or
loopback were captured. No production worker, Desktop Duplication/WGC acquisition,
Windows GPU-setting change, AORUS edit, Git stage/reset/commit/push or deployment
was performed. WARP was used only for synthetic software-adapter rejection tests.

## What was implemented

```text
Owned synthetic BGRA texture on selected source D3D11 device
  → neutral source-side BgraStagingTransport
    → GPU CopyResource → bounded nonblocking Map → pitched CPU BGRA rows
    → existing BT.709 limited BGRA-to-NV12 converter → owned NV12 frame
  → existing SessionFrameRouter / RecordingEngine / CaptureSession
  → native Intel QSV → same packet sink / MP4 mux / full media verifier
```

There is one existing Duluka session, one SessionId/timeline and the existing
encoder/mux ownership. The new transport has no acquisition API, encoder, audio,
recording clock, SessionId or output file. The canonical architecture was not
replaced or amended. The test fixture is an explicitly controlled frame source;
it does not enable the production Desktop provider.

### Source changes

All paths below are under `Project/NVIDIA Capture/EngineCapture/`:

| Source | Change |
| --- | --- |
| `Duluka Capture/Video/Contracts/DulukaEngine.Video/Conversion/BgraToNv12Converter.vb` | Existing I3 in-progress implementation retained; verified patched Byte() API builds. Top-down pitched BGRA to owned packed NV12, BT.709 limited, 2×2 box chroma and checked input/allocation bounds |
| `Duluka Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/` | New neutral Windows C# transport project, existing video interfaces + Vortice 3.6.2; actual adapter/software/device attestation, one owned staging surface, bounded nonblocking readback, cancellation checks, finally-protected unmap, independently owned NV12 result |
| `QsvNative.Tests/I3PixelTransport.cs` | Preserve the supplied CPU transport/session test; strengthen RGB/chroma/ownership/bounds checks; parameterize decoded-marker dimensions |
| `QsvNative.Tests/I3D3D11Transport.cs` | New owned texture upload/readback/session proof for VBR/CQP; real padded GPU pitch, exact marker and raw RGB color checks; device/target/resource/lifetime rejection cases |
| `QsvNative.Tests/Program.cs` | Preserve `--i3-transport`; run CPU and GPU tests under that flag. Add optional `--evidence-dir` for default I2 results without changing old assertions |
| `QsvNative.Tests/SessionTests.cs` | Add test-fixture source-device access and optional surface geometry; default I2 dimensions remain 320×180. Optional evidence root only |
| `QsvNative.Tests/QsvNative.Tests.csproj` | Reference new transport project for synthetic tests |
| `Invoke-IntelCaptureI2Regression.ps1` | Optional evidence directory forwarded to I1/tests so this rerun retains I2 historical artifacts |
| `Invoke-IntelCaptureI3Regression.ps1` | Build/test I3, execute original I2 safe regression under a separate report folder, build main Release; persist actual exit codes |

`source-manifest.json` hashes the scoped sources. Comparison against the original
I2 manifest is in `i2-source-preservation.json`: **27 of 31 hashes are unchanged**.
The 4 intentional differences are the I2 test project, Program, SessionTests and
regression runner listed above. RecordingEngine, CaptureSession, factory, native
QSV bridge/backend, shared packet/mux/media implementation and I2 configuration
policy have no I3 source change. `Encoding/NVENC/` has no Git diff. No I1 source
was reverted to obtain this result.

The new transport DLL is currently referenced by the **synthetic test project**;
it is not silently added to the default production Desktop composition. Future
integration must resolve source-device/context and shared-resource compatibility.

## Build and compiler evidence

The first requested build of the supplied handoff source succeeded with **0
warnings / 0 errors** (`initial-build.log`). Its earlier BC30668 ReadOnlySpan
problem had already been patched to Byte() before this task; no unsupported span
signature was reintroduced.

The added staging project initially produced **CS0104**, because both Vortice
DXGI and D3D11 expose `MapFlags`. It was corrected by explicitly selecting
`Vortice.Direct3D11.MapFlags.DoNotWait`; `staging-build.log` retains the failure.
The final `transport-release-build.log` is **0 warnings / 0 errors**. Main capture
Release passed in I2 regression (3 existing WebRequest/unassigned-variable
warnings), then the final capture Release invocation passed with **0 warnings /
0 errors** incrementally (`capture-release-build.log`). No validation or warning
policy was disabled to hide the compiler error.

## Final regression results

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Retained I1 safe regression | 400 | 0 | 33 |
| Retained VBR/CQP and corrupt CBR full-decode media controls | 6 | 0 | 0 |
| Original I2 native QSV/session/flush/ownership/error/recovery | 659 | 0 | 0 |
| Original shared mux synthetic video/sine lifecycle/fault/track tests | 13 | 0 | 0 |
| I3 CPU pitched BGRA/conversion/same-session acceptance | 83 | 0 | 0 |
| I3 GPU staging/session/ownership/color/input rejection | 292 | 0 | 0 |
| **Total** | **1,453** | **0** | **33** |

The original **I2 1,078/0/33 baseline is preserved**. The 33 skips remain 28 live
DXGI cases explicitly excluded by privacy scope and 5 NVENC hardware cases
without NVIDIA hardware. These are not hardware passes. CBR's negative control
passes by rejecting corruption; CBR itself remains known failed/disabled.

`checks.json` records four successful I3 orchestration stages. The retained I2
runner completed all five stages; its `checks.json`, baseline 12-stage results,
all logs and regenerated I2 synthetic media are under `i2-regression/`. The final
runner prints `I3_SAFE_REGRESSION_PASS stages=4 live_desktop=false audio_devices=false`.
Historical I1/I2 report artifacts were not overwritten by this final rerun.

## Actual GPU identity and completed sessions

Current read-only DXGI/CCD/CIM inventory shows physical Intel UHD Graphics,
**8086:9B41**, driver **27.20.100.8984**, LUID **`00000000:0000bf60`**, output **0**.
Its physical monitor remains 1920×1080; synthetic surfaces are independently sized
test resources and do not change display settings. The Intel-looking root device
at `00000000:0001127a` is Parsec, PnP `ROOT\DISPLAY\0000`, and is ineligible.
Microsoft Basic Render Driver is flagged software and excluded.

The actual source device, staging device and native QSV device all attest the
same complete LUID/vendor/device identity. Native hardware implementation is
**770 (`0x302`, D3D11 hardware)**, runtime **API 1.34**; dispatcher/headers remain
the pinned I2 libvpl 2.16.0 build. LUIDs are observations, not source constants.
See `windows-adapter-identity.json`, `i2-regression/baseline/dxgi-metadata.log` and
each `*-transport.json` / `SessionResult.VideoIdentity`.

| Actual final run | CPU pitched BGRA | D3D11 → staging VBR | D3D11 → staging CQP |
| --- | ---: | ---: | ---: |
| Resolution / FPS / duration | 320×180 / 30 / 2 s | 322×180 / 30 / 2 s | 322×180 / 30 / 2 s |
| Rate control | VBR target 2 / peak 4 Mbps | Same | CQP 24, no bitrate target |
| CPU source/upload row pitch | 1312 | 1320 | 1320 |
| Actual mapped GPU row pitch | Not a GPU readback | **1344** | **1344** |
| Submitted / encoded / muxed / fully decoded | 60 / 60 / 60 / 60 | 60 / 60 / 60 / 60 | 60 / 60 / 60 / 60 |
| Packets emitted during flush | **3** | **2** | **2** |
| Real desktop frames / synthetic frames | 0 / 60 | 0 / 60 | 0 / 60 |
| Final state | Saved / full decode verified | Saved / full decode verified | Saved / full decode verified |

Flush amounts above are measurements of this run, not fixed promises; they vary
with driver progress. Every delayed packet is delivered to the same session mux.
FPS describes the encoded session timeline; the synthetic producer is not
real-time paced and these clips do not establish capture throughput.
All MP4 packet PTS/DTS/durations, count, declared/read/decoded frames, geometry,
FPS and duration pass the unchanged exact session media acceptance. Every decoded
moving marker is checked across all 60 frames, including the first and last.
Full decode, not encode exit 0 or a container header, is the success evidence.

Artifacts include `bgra-stride-to-qsv-synthetic.mp4`, `controlled-session-result.json`,
`texture-staging-vbr.mp4`, `texture-staging-cqp.mp4`, matching `*-result.json`,
`*-transport.json`, `*-ffprobe.json` and `media-manifest.json`. Logs and MP4s are
local ignored artifacts under existing repository rules; JSON/source/docs remain
uncommitted and reviewable.

## Synchronization, ownership and negative cases

Each synthetic upload copies a poisoned-pitch CPU BGRA array into an owned GPU
texture; its pinned input is released/cleared immediately after creation. The
transport uses the texture's actual owning source device, not the QSV device.
It owns separate COM device/context references and one read-only staging surface.
`CopyResource` queues the copy, `Flush` submits it, and bounded nonblocking
`Map(Read, DoNotWait)` waits for completion. The final VBR/CQP runs observed
**58/59 busy polls**, respectively, so the real not-ready retry path was exercised.
The mapped row pitch is honored; only meaningful bytes are read from each row.
Finally-protected Unmap completes before the CPU NV12 result is returned.

Each GPU session records **60 readbacks, 60 maps, 60 unmaps, staging 1 created /
1 disposed, 60 source GPU frame disposals and 60 CPU frame disposals**. The
transport borrows each GPU frame. The source disposes it after Convert and before
NV12 submission; the session owns/disposes the NV12 envelope after Submit returns.
The native encoder copies retained input into its existing owned pool. After
flush, native outstanding jobs and locked surfaces are zero. COM lifetime tests
also continue successful readback after the caller's device/context wrappers
have been disposed while the transport holds its own references.

CPU tests validate BT.709 limited black/white, alpha ignored, exact red/green/blue
Y/UV vectors, 2×2 box chroma averaging, padding poison, owned output and invalid
dimensions/pitch/length/null/overflow. GPU color patches verify exact Y/UV channel
order and matrix/range through actual readback at top/middle/bottom rows.

Negative cases reject stale generation, wrong output, relabelled LUID, disposed
frame/transport, actual RGBA descriptor hidden behind a BGRA label, array textures,
same-LUID foreign-device texture and software-adapter texture. An actual DXGI
software flag also defeats a caller's forged physical-device label. Pre-copy
cancellation does not map or fault a valid transport; input-validation failures
leave the real staging path usable. No cross-device CopyResource, shared-handle
opening, GPU fallback or new session is hidden inside the transport.

Official synchronization references used during implementation:
[CopyResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource),
[Map and DO_NOT_WAIT](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-map),
[GetData completion semantics](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata).
The implementation uses nonblocking Map, not a query-completion shortcut or an
assumed completed copy. Local test evidence above is separate from API docs.

## Remaining gates

**`intel_native_live_input_not_verified` remains unchanged.** The reusable
transport handles unshared, same-device, unrotated SDR BGRA8 synthetic textures.
Production DXGI source wiring, its shared NT/keyed-mutex protocol and immediate
context concurrency still need an explicitly compatible composition. Same LUID
alone never permits copying a foreign-device resource. Existing I2 runtime 1.34
handle-ownership compatibility restrictions remain.

The raw BT.709 limited conversion is verified, but ffprobe reports **no explicit
color-range/matrix/primaries/transfer tags** in the current H.264 output. Color
descriptor propagation, Intel VUI signaling and color-managed playback acceptance
remain work before live color certification; no I2/NVENC encoder defaults were
silently changed to hide that gap.

Actual Desktop/Borderless/Fullscreen Exclusive, audio, source switching, hardware
device loss, forced copy timeout/cancellation during a mapped copy, HDR, rotation,
scaling, 1080p throughput and long-run performance are untested. Polling/deadlines
are bounded in code; an actual GPU-hang recovery was not certified. No universal
Exclusive support or zero-copy/performance claim is made. CBR remains known failed.

No live test is authorized by this synthetic result. Follow the
[synthetic handoff](../../INTEL-DULUKA-CAPTURE-I3-SYNTHETIC-HANDOFF-2026-10-25.md)
for the next scoped work. Reproduce safely with the existing I2 native prerequisites:

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI3Regression.ps1'
```

Direct transport entry: `dotnet run --project "Project\NVIDIA Capture\EngineCapture\QsvNative.Tests\QsvNative.Tests.csproj" -c Release -- --i3-transport`.
The runner does not recreate missing historical controls with real capture or
turn missing tools into a silent pass. Synthetic scope stops here.
