# Intel Duluka Capture — I1 complete / I2 handoff

Date: 2026-10-24, Asia/Bangkok. Machine: HUAWEI-MateBook.
Workspace: `C:\My Project\NVIDIA-Shadowplay`.
Branch: `intel/duluka-capture`; base HEAD remains `015f0548f82b5c1ed4d9c352fe095cc55e9a5453`.

**I1 implementation and safe regression are complete. Stop here. I2 is not started.** All changes are local and uncommitted; no push, deployment, AORUS edit, GPU-setting change or real screen/audio capture occurred.

Read the [original architecture audit](INTEL-DULUKA-CAPTURE-ARCHITECTURE.md), then the [I1 source and test report](reports/intel-duluka-i1-2026-10-24/README.md). The original audit describes the pre-I1 source; the implemented state below supersedes its I1 to-do list.

## Implemented boundaries

All paths below are under `Project/NVIDIA Capture/` unless stated otherwise.

- `EngineCapture/Duluka Capture/Video/Contracts/DulukaEngine.Video/Contract/CaptureTarget.vb`: immutable adapter identity and display target. Identity carries actual DXGI vendor/device ID, signed-high/unsigned-low LUID, physical/software classification and the adapter's Windows device path. Target carries adapter-local output index, current HMONITOR, CCD monitor device path, geometry, rotation and output technology. Standard PCI vendor identifiers are centralized in the taxonomy; there is no NVIDIA-only vendor-ID filter in inventory, shared DXGI initialization or RecordingEngine.
- `Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DisplayTopology.vb` and `DxgiDisplayInventory.vb` (under the Duluka Capture directory): one read-only DXGI + Windows CCD inventory for production and tests. No D3D11 device, duplication or pixels are involved in collection. Missing topology, unknown device identity, software/root devices and indirect/Miracast/ambiguous outputs fail eligibility.
- `IVideoCaptureRuntime` and `IEncoderRuntimeBackend`: supplemental runtime-info interfaces. Existing `IVideoCaptureBackend`, `IVideoFrame`, `ID3D11VideoFrame` and `IEncoderBackend` contracts remain intact.
- `Recording/DulukaEngine.Recording/Composition/RecordingBackendFactory.vb`: provider registry, immutable plan, current-target checks and creation seams. Provider defaults preserve NVENC's existing first-adapter behavior. Explicit Intel target selects `intel:qsv-h264` as a **planned** provider and fails `RequireWired()` before creating a capture device. `auto` is available to the composition planner; existing public REST route policy still rejects native QSV.
- `Composition/NvencRuntimeAdapter.vb`: wrapper around the existing encoder. Reads the actual LUID reported by the original NVENC D3D11 factory and resolves its actual vendor/device metadata from inventory. The entire `Encoding/NVENC/` source tree has no diff.
- `RecordingEngine`: capture/encoder creation and encoder rebuild go through the injected factory. Requested target, actual capture output and actual encoder identity are checked on initialization, before sessions, and before swapping a rebuilt encoder. A mismatched rebuilt encoder is disposed while the previous valid encoder remains owned. Status and session result include scalar `VideoIdentity` evidence; that evidence does not certify frames or media.
- `CaptureSession`: uses the neutral runtime/backend interfaces, with the old concrete constructor retained as a compatibility overload. Queue/timing/mux behavior was not redesigned in I1.
- `src/[API]/CaptureDisplayCatalog.vb` and `RecordingEngineHost.vb`: enumerate eligible displays across vendors and forward the full immutable target to startup, rather than dropping LUID after selecting an output. New saved selections use schema 2 with CCD monitor device path; a replacement monitor at the same LUID/output is rejected. Schema 1 remains readable for compatibility. Process-local target/handle objects are excluded from REST JSON.
- `src/[FFmpeg]/FFmpegArgumentBuilder.vb` and `src/[API]/CaptureScreenshotEngine.vb`: resolve current DXGI adapter index from the selected LUID and explicitly bind FFmpeg's capture D3D11 device. The binding is capture-selection metadata; it does not attest an FFmpeg child's actual encoder-device LUID.
- Hardware preflight no longer requires `nvidia-smi` or a NVIDIA adapter. It uses the same production inventory/factory metadata and retains disk, nonzero-LUID, uniqueness, physical-device and exact-target checks. Its production-worker idle guard remains.

## Actual Intel / Parsec mapping

The new read-only gate observed:

| DXGI entry | LUID | Windows device identity | Eligibility |
| --- | --- | --- | --- |
| Intel description, vendor 8086/device 9B41, index 0 | `00000000:0000bf60` | `PCI\VEN_8086&DEV_9B41&SUBSYS_125D152D&REV_02\3&11583659&0&10` | Physical Intel output 0, 1920×1080, internal technology 0x80000000 |
| Same Intel description/vendor/device, index 1 | `00000000:0001127a` | `ROOT\DISPLAY\0000` | Excluded; no attached output |
| Microsoft Basic Render Driver, index 2 | `00000000:0000c2ac` | No CCD adapter path; software flag set | Excluded |

Independent `Win32_VideoController` metadata identifies `ROOT\DISPLAY\0000` as **Parsec Virtual Display Adapter**, driver 0.45.0.0. Thus the second Intel-looking DXGI entry can now be correlated with Parsec by device instance ID, rather than guessed from its description. The physical Intel device ID matches the independent Intel PnP entry, driver 27.20.100.8984. No display setting was modified. These LUIDs are this boot's observations, not constants in source.

The Intel plan logs `wired=False`, `intel_native_encoder_not_implemented`, `actual_capture_proven=false`, `actual_encoder_proven=false`.

## Validation outcome

The final safe regression runner completed 12 stages: **400 checks passed, 0 failed, 33 skipped**. The original 181 checks remain included; display/routing additions increase that subset to 187. Composition adds 78 checks. The skips are 28 explicitly excluded live DXGI cases and 5 NVENC hardware cases on this Intel-only machine.

The production media inspector was also run against the three retained synthetic MP4s from Stage A: **6 assertions passed**. It accepts VBR/CQP and rejects the corrupted CBR output despite complete packet counts. **CBR remains a known failed media configuration; a passing negative-validation test is not a repaired CBR encoder.** No new QSV encode or live recording was needed for this validation.

See the report for Release build/rebuild evidence and individual suite counts. An intermediate linked-source VB compile failure was corrected by using `Global.DulukaEngine.Video.CaptureTarget`; the complete final runner subsequently passed. No validation was removed to obtain a pass.

Reproduce safe I1 regression:

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI1Regression.ps1'
```

Do not run the Video suite without `--offline` during this task: its default hardware probe creates desktop duplication. The offline switch reports those cases as SKIP and leaves the fake contract cases running. The older full offline release script expects an existing production worker; do not launch a worker merely to satisfy its guard.

## I2 entry points and acceptance

1. Establish the native build toolchain and versioned Intel dispatcher/header packaging. The audit found `vswhere.exe`, no VC toolchain installation from its query, and no `cl`/`cmake` in PATH. Nothing was installed in I1. FFmpeg's oneVPL dispatcher negotiated an installed implementation at API 1.34; do not require modern 2.x surface APIs without probing.
2. Add a separate Intel QSV project/native bridge implementing `IEncoderRuntimeBackend`. Bind an explicitly created Intel D3D11 device and return the actual parent adapter identity. Resolve the requested target afresh and validate after initialization; fail on software implementation, wrong LUID or missing capabilities.
3. Prove **synthetic owned NV12 → H.264 → finalized MP4 → packet/frame counts → full decode** before enabling the provider. Begin with truthful VBR/CQP cases. Preserve baseline CBR as known failure until its cause is isolated and a fix passes media acceptance. Do not silently convert requested CBR into VBR.
4. Keep `Providers` Intel `Implemented=False` until the implementation and tests justify changing it; add its creation case explicitly. Do not bypass `RequireWired`, physical eligibility, current-target checks or `RequireIdentity` to enable Intel.
5. Add Intel-specific configuration policy. The current engine defaults (`p4`, CBR) and `ResolveRateControl` (`cbr/vbr/cq`, unknown → cbr) are NVENC legacy semantics. A `cqp` request currently cannot be forwarded correctly by that mapper. Add a provider-aware mapping; preserve old NVIDIA defaults and reject unsupported Intel modes without substitution.
6. Implement bounded input ownership, conversion/alignment and timestamp handling. QSV may delay output; the existing Boolean `Encode` does not distinguish “accepted but delayed” from backpressure. Avoid duplicate input submission and retain encoder-owned surfaces until processing completes.
7. **Required session integration gap:** deliver every `Flush(packetSink)` packet to the existing mux before encoder stop/mux completion. Current native NVENC is synchronous and emits no flush packets; `CaptureSession` still does not explicitly drain a delayed Intel encoder. Do not enable QSV until this is covered by tests.
8. Add generic encoder error/media-verification evidence as needed. `SessionResult.Pass` and `NvencErrors` retain old semantics in I1; `VideoIdentity` is device metadata only. Intel recording acceptance needs finalized MP4 and full-decode evidence, including expected decoded frame counts for deterministic samples.
9. Build Release, rerun I1 safe regression, add Intel synthetic/fault/flush tests, report real pass/fail/skip, then stop for the next model/phase. Live DXGI capture belongs to a later privacy-cleared scene; no real screen/audio input is authorized here.

## Remaining limits

- Native Intel encoder, native capture/encode LUID comparison, live display frames, audio, borderless/exclusive games, WGC monitor fallback and 1080p performance remain unproven. Fullscreen Exclusive support must be tested per API/game/mode.
- Existing NVENC selects the first NVIDIA adapter internally. I1 retains requested second-GPU identity in the plan but rejects execution on that target before capture, with `encoder_explicit_adapter_selection_not_implemented`, rather than silently using the first GPU. Supporting encoder selection on a second NVIDIA GPU needs a separate authorized change to the vendor implementation.
- A legacy schema-1 display selection has no historical CCD monitor identifier; I1 cannot reconstruct one. Its original LUID/output matching remains compatible; explicitly saving the current choice upgrades to schema 2. No runtime configuration was migrated on this machine.
- FFmpeg CLI adapter indices are resolved from LUID immediately before use, but CLI logs still do not attest the actual child device LUID or eliminate a topology race after process launch. These routes do not satisfy the strict native Intel device-proof gate.
- Clone/indirect/unknown topology is rejected when identity cannot be established. Read-only metadata eligibility is not proof that duplication can initialize.
- NVIDIA hardware regression on AORUS is pending under that machine's own authorized workflow. No AORUS source/runtime was accessed or changed.
