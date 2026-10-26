# Intel Duluka Capture — Phase I2 evidence

Date: 2026-10-24, Asia/Bangkok. Machine: HUAWEI-MateBook.
Branch: `intel/duluka-capture`; base HEAD remains
`015f0548f82b5c1ed4d9c352fe095cc55e9a5453`.

**I2 native encoder and synthetic session scope completed: 1,078 passed,
0 failed, 33 skipped; native and capture Release builds passed.** Intel VBR and
CQP each produced a verified 60-frame MP4 through the existing RecordingEngine /
CaptureSession / shared mux. This is **not live Intel Desktop readiness**.
Default Intel Desktop remains blocked. CBR remains a known failure.

No desktop/game pixels, microphone or system-loopback audio were captured.
Only owned synthetic NV12 frames, generated test video/sine data, D3D11 device
creation and read-only DXGI/CCD/CIM metadata were used. No AORUS access, Windows
GPU-setting changes, staging, commit, push, production worker launch or deployment
occurred. I1/concurrent uncommitted work was preserved.

## Architecture and source changes

The canonical session contract and Thai companion were read and kept intact.
The changes implement their existing graph; no alternate Intel recording engine
or architecture replacement was introduced:

```text
Owned NV12 source → SessionFrameRouter → same Duluka CaptureSession
  → selected QSV encoder → SessionPacketSink → existing LiveMuxSession
  → MP4 full decode/count/timeline verification → Saved
```

Paths below are relative to `Project/NVIDIA Capture/EngineCapture/` unless stated:

| Source | I2 change |
| --- | --- |
| `Duluka Capture/Encoding/Intel/Native/` | Portable pinned toolchain build; official Intel dispatcher/headers; x64 C ABI 2 bridge; explicit adapter selection, actual runtime-device attestation, owned NV12 surfaces, async jobs, drain and bounded queues |
| `Duluka Capture/Encoding/Intel/DulukaEngine.Encoder.Qsv/` | New separate `IEncoderRuntimeBackend` implementation; VBR/CQP policy validation, accepted/delayed submission, exact timestamp mapping, packet delivery, flush, error state and teardown |
| `Duluka Capture/Video/Contracts/DulukaEngine.Video/Contract/INv12VideoFrame.vb` | Additive owned CPU NV12 input contract |
| `Duluka Capture/Encoding/DulukaEngine.Encoder/Contract/IEncoderSubmissionBackend.vb` | Additive explicit accepted/delayed result; old encoder interface and NVENC implementation retained |
| `Duluka Capture/Recording/DulukaEngine.Recording/SessionFrameRouter.vb` | Source envelope ownership; generation, exact display identity, surface dimensions, source sequence and session-normalized PTS validation |
| `SessionPacketSink.vb` in that recording project | Same session sink for immediate and flushed output; packet ownership/disposal; separate accepted, encoded and queued counts; strict no-B/CFR checking for controlled inputs |
| `CaptureSession.vb` | SessionId; use common packet sink; tail budget based on accepted frames; explicit encoder flush into same mux before stop |
| `CaptureSession.OwnedFrames.vb` | Generic controlled video-only source entry on the same session; no source.Start/audio device; bounded frame budget, truthful finalization and required media verification |
| `RecordingEngine.vb`, `RecordingDTOs.vb` | Same engine/factory entry for controlled frames, provider-aware config/rebuild, real session ID, generic packet/error/media evidence and faulted-backend replacement for a new session |
| `Composition/RecordingBackendFactory.vb`, `EncoderConfigurationPolicy.vb` | Current-target-validated native NV12 encoder selection; explicit Intel config; default Desktop BGRA composition still gated as `intel_native_live_input_not_verified` |
| `Duluka Capture/Recording/Media/DulukaEngine.Media/` | Shared assembly compiles the original production `src/[API]/CaptureMediaInspector.vb` as linked source; adds session frame/packet/timestamp acceptance |
| Recording project, main `NVIDIA Capture.vbproj`, `MediaEvidence.Tests` project | Reference common QSV/media assemblies; avoid compiling a duplicate inspector in the host/tests |
| `Duluka Capture/Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/LiveMuxSession.vb` | Bounded pending-packet connection wait at stop; opt-in validated CFR timestamp stamping; preserve all streams in final remux with `-map 0` |
| `Composition.Tests/Program.cs` | Update expected rejection reason to unverified live input, preserving all 78 identity/composition assertions |
| `QsvNative.Tests/` | Real synthetic native encode/session/decode/content/identity/ownership/error/recovery assertions |
| FFmpeg test `Program.vb`, `LiveMuxSessionTests.vb` | Safe synthetic-only mux entry; fix bundled FFmpeg discovery for this repository layout; all existing mux assertions retained |
| `Invoke-IntelCaptureI1Regression.ps1`, `Invoke-IntelCaptureI2Regression.ps1` | Optional I1 evidence output folder; reproducible I2 safe build/regression orchestration |

The complete `Encoding/NVENC/` source tree has no Git diff. Existing NVIDIA
provider/configuration/first-adapter behavior remains. Shared orchestration/mux
changes are covered by offline contracts and synthetic media tests; NVIDIA
hardware execution on AORUS was not performed.

`source-manifest.json` hashes the scoped I2 source set. The broader working tree
also contains earlier I1/concurrent changes; this report does not attribute every
uncommitted file to I2.

## Actual GPU and runtime identity

| Evidence | Actual result |
| --- | --- |
| Physical Intel | Intel UHD Graphics, PCI 8086:9B41, driver 27.20.100.8984 |
| Selected physical display | LUID `00000000:0000bf60`, output 0, 1920×1080 internal display; CCD/monitor identity eligible |
| Actual separately created synthetic-source D3D11 device | Parent adapter equals the selected physical LUID |
| Actual QSV runtime D3D11 device | Parent adapter LUID `00000000:0000bf60`, vendor 8086/device 9B41; hardware implementation 770 (`0x302`, hardware/D3D11), API 1.34 |
| Intel-looking second DXGI entry | LUID `00000000:0001127a`, vendor 8086/device 9B41, but PnP `ROOT\DISPLAY\0000`, no outputs, physical=False |
| Independent CIM cross-check | `ROOT\DISPLAY\0000` belongs to **Parsec Virtual Display Adapter**, driver 0.45.0.0; it cannot pass physical target selection |
| Microsoft Basic Render Driver | Software adapter, excluded |

See `native-device-vbr.json`, `native-device-cqp.json`,
`windows-adapter-identity.json`, `baseline/dxgi-metadata.log` and session result
`VideoIdentity`. Source-device creation proves adapter binding; it does not prove
desktop acquisition. Missing LUID fails native initialization instead of falling
back to a default GPU. Existing high/low-part and virtual-display guards remain.

## Native toolchain and compatibility

Portable LLVM MinGW clang 22.1.8, CMake 4.1.1, Ninja 1.13.1; .NET SDK 10.0.402.
Official `intel/libvpl` v2.16.0 dispatcher and headers are pinned at
`778a66d6c6537f08eabb91955dbbf1bce3812894`, with a clean dependency source tree.
The **installed hardware runtime negotiated API 1.34**. Modern dispatcher/header
version is not presented as the driver runtime version.

`toolchain-provenance.json` records checked archive SHA256 values, exact tool
versions and native DLL hashes. Build script pins URLs/hashes and source revision.
Dependencies/build outputs remain in ignored local packages/bin directories;
no system install was necessary. VPL license is copied beside the runtime DLLs.
Managed NVIDIA builds do not implicitly download or require native Intel tools;
missing Intel artifacts fail initialization rather than fabricating capability.

One compatibility issue was diagnosed on this real runtime: current VPL
documentation describes an AddRef-owned handle, but API 1.34 on this driver
returned a borrowed `MFXVideoCORE_GetHandle` D3D11 pointer (measured reference
count 35 before/35 after). Releasing it caused a native teardown access violation
during development. The final bridge holds its own device through `MFXClose`,
releases only QueryInterface-owned references, and rejects unverified runtime
versions. Repeated native create/submit/flush/destroy and engine recovery tests
passed after that fix. This is a narrow tested compatibility rule, not a promise
for every driver exposing API 1.34 or for newer runtimes.

Official references: [Intel dispatcher source](https://github.com/intel/libvpl/tree/v2.16.0),
[Intel hardware/session guidance](https://intel.github.io/libvpl/latest/programming_guide/VPL_prg_hw.html),
[current GetHandle documentation](https://intel.github.io/libvpl/latest/API_ref/VPL_func_vidcore.html).
The runtime-specific ownership finding above is local experimental evidence.

## Final test results

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Retained I1 safe suites (11 test suites + Release build) | 400 | 0 | 33 |
| Retained synthetic VBR/CQP and CBR-negative production media validation | 6 | 0 | 0 |
| New native QSV / same-session MP4 / identity / ownership / fault / recovery | 659 | 0 | 0 |
| Existing shared production mux lifecycle/fault/track/process tests, synthetic-only entry | 13 | 0 | 0 |
| **Total** | **1,078** | **0** | **33** |

I1 baseline **406/0/33** is retained in full. The 33 skips are 28 live DXGI cases
(privacy scope) and 5 native NVENC hardware cases (no NVIDIA hardware). No skip
was counted as hardware success. CBR's expected negative media assertion passes
by detecting corruption; CBR encoding itself remains a known failure.

Release capture build and native Release build both succeeded. Earlier full
capture compilation reported the 3 existing warnings (2 obsolete WebRequest,
1 potentially unassigned `pr`); the final incremental capture build reported
0 warnings/0 errors. No warning policy or media/GPU validation was weakened.

The final runner reports `I2_SAFE_REGRESSION_PASS stages=5`; `checks.json` and
`baseline/checks.json` contain actual exit codes and log paths. Detailed logs:
`i1-safe-regression.log`, `native-release-build.log`, `retained-media.log`,
`native-session-synthetic.log`, `shared-mux-synthetic.log` and `baseline/*.log`.
Logs/media/tool packages are local artifacts ignored by existing repository
rules; JSON/source/docs remain available for review without committing.

## MP4 acceptance, ownership and errors

| Actual test | VBR | CQP |
| --- | ---: | ---: |
| Input | Owned NV12, 320×180, 30 fps | Same |
| Configuration | Target 2 Mbps, peak 4 Mbps | QP 24, no bitrate target |
| GOP / B frames / async depth | 30 / 0 / 4 | Same |
| Accepted / encoded / muxed / decoded frames | 60 / 60 / 60 / 60 | 60 / 60 / 60 / 60 |
| Delayed packets delivered on flush | 5 | 5 |
| Native outstanding jobs / locked surfaces after flush | 0 / 0 | 0 / 0 |
| Final MP4 duration / average FPS | 2 seconds / 30 | 2 seconds / 30 |
| MP4 every-packet PTS/DTS/duration | Exact CFR, PTS=DTS, ordered | Same |
| Full decode / every decoded moving marker | Passed / 60 frames checked | Passed / 60 frames checked |
| Session finalization | Saved, full verification required | Same |

Artifacts: `native-session-vbr.mp4`, `native-session-cqp.mp4`, their `*-result.json`
and `*-ffprobe.json`, and `media-manifest.json`. All input frames were returned to
their source owner exactly once; all packets were disposed by their receiver.
The decoded moving marker test proves driver-retained input survives immediate
caller buffer disposal/clearing. Synthetic frames are counted separately;
`FramesCaptured=0` throughout. The successful recovery session also produced
60 fully decoded frames (`native-recovery-result.json`, `native-recovered.mp4`).

Negative/recovery tests reject CBR without substitution, NVENC-only preset,
unsupported BGRA input, unsupported scaling, duplicate input PTS, source PTS
discontinuity, stale generation and wrong output/LUID. A throwing mux sink faults
flush, does not count a queued packet, and cannot claim Saved. An incomplete
session with pending QSV output fails; the next session replaces the faulted
backend through the original RecordingEngine factory and succeeds with a new ID.
Stop/start uses a fresh timestamp epoch. Three additional native lifecycle cycles
complete without the earlier access violation. A wrong expected decoded frame
count is rejected even for otherwise healthy media.

Issues found and fixed before the final pass:

- Stop could precede FFmpeg's pipe connection for fast synthetic input, discarding
  all queued video. Bounded connection wait now preserves accepted bytes for drain.
- Raw H.264 demux timestamps had quantization drift. The controlled-input session
  first validates ordered no-B CFR packets, then explicitly stamps that timeline
  in the shared mux. Exact MP4 packet timing is verified; acceptance was not relaxed.
- The mux tests initially could not discover this repository's FFmpeg bundle;
  the synthetic-only entry failed rather than treating zero executed tests as a
  pass. Discovery now supports this tree and all 13 assertions execute.
- The expanded mux regression exposed lost second audio track in final remux.
  `-map 0` now retains every stream; the unchanged THREE-stream assertion passes.
  Audio in this test was generated sine data, not microphone/loopback input.

`shared-mux-initial-discovery-failure.log` and
`shared-mux-initial-track-loss-failure.log` retain the initial diagnostics.

## Remaining limits and I3

**Do not enable live Intel Desktop composition based on this report.** The
production DXGI source emits BGRA D3D11 textures; the tested QSV input is CPU
NV12. GPU conversion/import, resource synchronization, live display pixels,
audio integration, source switching/black-gap composition, HDR/rotation,
1080p/long-run performance and real device-loss recovery are unproven. Runtime
ownership compatibility outside this driver needs separate validation.
CBR remains blocked. Borderless and Fullscreen Exclusive have not been tested;
Exclusive support must be established per graphics API/game/mode.

Follow [I3 handoff](../../INTEL-DULUKA-CAPTURE-I3-HANDOFF.md). Reproduce safely:

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI2Regression.ps1'
```

Use `-BootstrapNative` if portable dependencies are missing. Historical media
paths must remain available; the runner fails if they are absent. Do not start
live capture or audio to recreate a missing synthetic artifact. I2 stops here.
