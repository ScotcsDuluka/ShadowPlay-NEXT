# Intel I3 source/frame composition slice — 2026-10-25

HUAWEI-MateBook only; `C:\My Project\NVIDIA-Shadowplay`, branch
`intel/duluka-capture`. This is a standalone reusable source/frame adapter,
**not a completed production or live Intel pipeline**. The gate
`intel_native_live_input_not_verified` remains unchanged.

## Scope and source audit

Read AGENTS.md, the canonical Session-Centric Contract and Thai companion,
Intel architecture/I2 handoff, I3 synthetic handoff and original I3 report before
editing. Inspected DdagrabBackend, D3D11VideoFrame, ID3D11VideoFrame,
SessionFrameRouter, BgraStagingTransport and I3D3D11Transport.

The earlier Codex rollout ends with `task_complete` at 2026-10-25 04:40:50 UTC;
the other rollout is this task. Process inventory showed multiple Codex hosts,
but no other active editing turn was found in the available rollout logs.
Win32_Process CIM command-line access was denied; process names/start times and
rollout state were used instead. Pre-edit source hashes and final preservation
comparison record changes without resetting or overwriting prior work.

DdagrabBackend creates a private D3D11 device on the resolved adapter, enables
ID3D11Multithread protection, and creates a fresh BGRA texture for each frame.
The direct path wraps that texture in D3D11VideoFrame; the shared path wraps a
separate shared texture and NT handle. A pushed/replaced frame transfers to the
sink; a dropped frame stays with the source for disposal. NativeTexture exposes
the real Vortice texture and returns null after disposal. The adapter does not
start this backend or change its worker, queue, recovery or disposal paths.

## Exact source changes

Paths below are relative to `Project/NVIDIA Capture/EngineCapture/`:

| File | Change |
| --- | --- |
| `Duluka Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/BgraFrameAdapter.cs` | New lazy, generation-bound adapter taking borrowed ID3D11VideoFrame and source target; derives device from actual texture; validates physical adapter/output, metadata and descriptor; requires existing context protection; reuses one staging transport; terminal faults stay terminal |
| `Duluka Capture/Video/Transport/DulukaEngine.Video.Transport.D3D11/BgraStagingTransport.cs` | Small ownership fix: QueryInterface an independent device reference instead of disposing the texture's cached Vortice Device wrapper |
| `QsvNative.Tests/I3FrameAdapter.cs` | New synthetic production-frame-wrapper tests, VBR/CQP session/media acceptance and negative/lifetime cases |
| `QsvNative.Tests/Program.cs` | Invoke new adapter cases under existing `--i3-transport` flag |
| `QsvNative.Tests/I3PixelTransport.cs` | Honor existing `--evidence-dir` option for I3 outputs, preserving historical I3 artifacts |

Documentation/reproduction additions are this README and `Run-Regression.ps1`.
Evidence files are confined to this report directory. No project reference,
native bridge, encoder, capture worker, router, session or architecture contract
was changed. No NVENC/AORUS edit or Git stage/commit/push/reset/clean/checkout.

## Adapter contract

Construct one `BgraFrameAdapter(selectedTarget, sourceGeneration)` per source
generation. Default surface size is the selected display size; optional fixed
dimensions support preplanned synthetic surfaces, without scaling or cropping.
Call `Convert(borrowedFrame, sourceStampedTarget, sourceStampedGeneration)` and
hand its owned NV12 result to the existing SessionFrameInput/router/session.
Keep the upstream frame stable and alive until Convert returns; dispose the
upstream frame through its original owner and transfer/dispose NV12 exactly once.

The first valid frame supplies the actual D3D11 device. The adapter never calls
D3D11CreateDevice. It checks current read-only DXGI output metadata on first bind,
including output index, attachment, monitor handle, device name, geometry and
rotation. Every call checks the source envelope, topology token, physical device
path, generation, BGRA dimensions, descriptor and exact native device identity.
Same physical LUID is necessary but never authorizes a foreign-device copy.

The input target must come from fresh trusted DXGI/CCD inventory. A texture does
not independently encode its monitor or generation; these must be stamped by
the source/session, not fabricated from the selected target at conversion time.
Source recreation/topology change requires a new generation and explicitly
created adapter. This seam neither polls topology per frame nor migrates targets.

Shared NT handles, resource misc flags, missing/disposed native textures,
virtual/root/Parsec identities, software adapters, wrong LUID/output/size and
stale generations fail closed. The existing staging transport retains its
descriptor and actual adapter checks. No shared-resource import, cross-device
CopyResource, GPU fallback or automatic transport replacement was added.

The source must already have ID3D11Multithread protection enabled and keep it
enabled. The adapter checks that setting without changing it, then pairs
Enter/Leave around conversion. Its own lock serializes Convert/Dispose. This
does not protect against an owner concurrently disposing or rewriting a borrowed
texture. The GPU Map polling deadline is bounded; acquiring managed/D3D context
locks is not a hard real-time guarantee. Live source contention is unverified.

Copy/map/conversion faults remain terminal; pre-copy cancellation and invalid
input remain retryable. Disposal is idempotent and releases only adapter-owned
transport/COM references. Frame PTS/sequence pass through unchanged: production
QPC-to-session timestamp normalization remains a session integration gate.

Native API basis: [GetDevice ownership](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicechild-getdevice)
and [ID3D11Multithread synchronization](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nn-d3d11_4-id3d11multithread).
The Vortice managed Device property caches a wrapper; consumers use a distinct
QueryInterface reference. Repeated-borrow tests specifically cover that lifetime.

## Verification

Final regression: **1,725 passed / 0 failed / 33 skipped**, with the prior 13
synthetic-audio mux checks explicitly excluded, not claimed as rerun. All six
orchestration stages exited 0 (`checks.json`).

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Retained I1 safe regression | 400 | 0 | 33 |
| Retained VBR/CQP/corrupt CBR media controls | 6 | 0 | 0 |
| Retained I2 native QSV/session/fault/flush | 659 | 0 | 0 |
| Original I3 CPU/GPU cases | 375 | 0 | 0 |
| New frame adapter/session/negative/ownership cases | 285 | 0 | 0 |
| **Executed total** | **1,725** | **0** | **33** |

The 33 skips are 28 live DXGI cases and 5 NVIDIA hardware cases. No hardware pass
is inferred from a skip. Scoped Release finished with **0 warnings / 0 errors**;
main capture Release passed with **3 existing warnings / 0 errors**; native
Release passed using the existing pinned dispatcher. `git diff --check` passed
(only existing line-ending notices). Of 740 pre-existing source/document/script
hashes, 737 are unchanged; only the three existing files listed above changed.
The two added C# files and all five final source hashes are in `source-manifest.json`.

The new VBR and CQP sessions each used production D3D11VideoFrame wrappers around
test-owned 322x180 BGRA textures: **60 submitted / 60 encoded / 60 muxed / 60 fully
decoded**, 2 delayed flush packets in each final run, Saved/full decode verified,
60 upstream GPU disposals and 60 returned NV12 disposals, each exactly once, and
one staging transport created/disposed per session. All 60 decoded moving-marker
positions pass. Source texture ownership remains upstream through conversion;
the source disposes before the session submits the independently owned NV12.
Native outstanding jobs and locked surfaces are zero after drain. Each session
has its own single session ID, existing clock/router/encoder/mux path and zero
captured desktop frames/audio samples. Current physical Intel LUID is
`00000000:0000bf60`, output 0 (boot-specific evidence, not a constant).

The final negative cases include a proven different native device on the SAME
Intel LUID, wrong supplied and actual output/monitor, relabelled LUID, root/Parsec
identity, shared NT handle, missing/disposed texture, wrong declared and actual
size/format, stale generation, unprotected context, pre-copy cancellation,
terminal post-readback source fault and disposed adapter. Repeated borrowing and
independent device-wrapper disposal preserve upstream COM references. The original
staging software-adapter, array and color/pitch cases also remain passing.

The first runtime attempt passed both new 60-frame sessions, then exposed a
cached-wrapper ownership defect during repeated borrowing. The stack was in
GetImmediateContext, not output enumeration. `i3-first-attempt.log` and
`i3-diagnostic.log` retain the failures. An intermediate attempt to call Vortice's
non-public GetDevice method failed compilation (`i3-fixed-ownership.log`). The
final fix uses public QueryInterface and independently owned references.
`i3-owned-reference.log` records the subsequent 657/0 pass before three additional
descriptor/repeated-borrow checks were added for the final regression.

Initial scoped Release build passed with four NU1900 warnings because package
vulnerability metadata could not be reached, and Git was initially absent from
PATH. No package was upgraded. The regression sets Git's existing install path;
existing safe regression commands use their established `NuGetAudit=false` option.
This does not constitute a fresh dependency vulnerability audit.

Reproduce from repository root with existing native dependencies (no bootstrap):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  './docs/reports/intel-duluka-i3-composition-2026-10-25/Run-Regression.ps1'
```

ExecutionPolicy is scoped to that child process; no machine policy was changed.
The script contains every exact build/test command and redirects evidence here.
It builds native Release, runs I1 safe regression (including main Release),
retained media controls, I2 native synthetic sessions, scoped Release and all I3
synthetic cases. The earlier unmodified full I3 runner includes 13 synthetic-sine
audio mux assertions; **those 13 are deliberately not run under this task's
"No audio / synthetic textures only" scope**. They are not counted as passes or
added to the test framework's skip count. No audio source/device was opened.

## Next live gates

1. Explicit production source composition and an audited stable frame borrow,
   sink eviction/shutdown/error-path ownership and shared-context contention.
   Preserve the existing backend loop; this slice establishes only a compatible
   unshared-frame seam, not production worker ownership under load.
2. Authoritative target/generation stamping and session QPC/PTS normalization;
   bounded source/device-loss recovery and stale callback handling. The injected
   fault is a deterministic diagnostics exception after readback, not GPU removal.
3. Explicit session color descriptor/Intel VUI tags and playback acceptance.
   Existing H.264 colorimetry tags remain unspecified; CBR remains disabled.
4. A separately authorized privacy-cleared scene and bounded live scope, then
   actual DXGI-to-session/QSV/MP4 full decode. No actual screen pixels were read
   here. HDR/rotation/scaling, 1080p throughput, long runs, real GPU hangs and
   Desktop/Exclusive transitions remain unverified; audio is outside this slice.

Synthetic success does not remove `intel_native_live_input_not_verified`.
