# Intel Duluka Capture I1 — implementation and validation report

Date: 2026-10-24 (Asia/Bangkok). Host: HUAWEI-MateBook.
Branch: `intel/duluka-capture`. Base commit: `015f0548f82b5c1ed4d9c352fe095cc55e9a5453`.

**I1 complete.** Read the [I2 handoff](../../INTEL-DULUKA-CAPTURE-I2-HANDOFF.md) for extension points and remaining gates. No commit, push, deployment, AORUS access, Windows GPU-setting change, real screen pixels or real audio input was involved.

## Result

- Intel GPU/display identity is resolved with DXGI LUID plus Windows CCD/PnP identity. The second Intel-looking DXGI entry is correlated with Parsec's `ROOT\DISPLAY\0000` and excluded.
- Factory selection uses registered provider capabilities and the selected adapter's actual vendor, with full LUID/output/monitor identity forwarded through startup, native capture, encoder initialization/rebuild, status and session result.
- Intel native QSV remains planned and blocked before capture initialization. The existing NVENC source tree is unchanged; hardware-independent regressions pass. NVIDIA hardware validation remains pending.
- Final selected regression: **400 passed, 0 failed, 33 skipped**, plus **6 passed production media-validation assertions** on retained synthetic files. Total: **406 passed, 0 failed, 33 skipped**. CBR media corruption remains a known failure.
- Release rebuild: **PASS, 0 errors, 3 existing warnings** (two SYSLIB0014, one BC42030), exit 0, 26.32 s. See `release-rebuild.log`. The runner's preceding incremental build also passed; zero warnings in that incremental output does not imply the existing warnings were fixed.

## Source changes

Paths below are relative to `Project/NVIDIA Capture/`. Newly added source files are included even though they are untracked. No files were staged.

| Ownership / files | Change |
| --- | --- |
| `EngineCapture/Duluka Capture/Video/Contracts/DulukaEngine.Video/Contract/CaptureTarget.vb` **new** | Immutable adapter and target identity, central PCI vendor taxonomy, physical eligibility and exact-current-target validation |
| Same contract directory, `IVideoCaptureRuntime.vb` **new** | Supplemental capture geometry/identity/metrics interface |
| `EngineCapture/Duluka Capture/Encoding/DulukaEngine.Encoder/Contract/IEncoderRuntimeBackend.vb` **new** | Supplemental actual encoder adapter and video-configuration interface |
| `EngineCapture/Duluka Capture/Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DisplayTopology.vb`, `DxgiDisplayInventory.vb` **new** | Read-only CCD/DXGI inventory; adapter path/PnP mapping; current LUID → index resolution; explicit FFmpeg capture device arguments |
| Same DXGI directory, `DdagrabBackend.vb` | Target-based vendor-neutral device selection, actual D3D11 parent validation, exact output verification and same-target recovery checks |
| `EngineCapture/Duluka Capture/Recording/DulukaEngine.Recording/Composition/RecordingBackendFactory.vb`, `NvencRuntimeAdapter.vb` **new** | Provider/factory/plan seams, Intel pending registration, NVENC runtime-info adapter and strict target checks |
| Same Recording directory, `RecordingEngine.vb` | Factory injection, neutral fields, identity verification on init/session/rebuild, scalar identity evidence |
| Same Recording directory, `CaptureSession.vb` | Neutral backend interfaces and diagnostics; retained compatibility constructor |
| Same Recording directory, `RecordingDTOs.vb` | Full startup target and serializable status/session identity DTO; old Pass/error semantics retained |
| `src/[API]/CaptureDisplayCatalog.vb`, `RecordingEngineHost.vb` | All eligible vendors, full target forwarding, schema-2 monitor identity, legacy schema reading and safe JSON serialization |
| `src/[API]/CaptureScreenshotEngine.vb`, `src/[FFmpeg]/FFmpegArgumentBuilder.vb` | Explicit FFmpeg capture-adapter binding derived from current selected LUID |
| `EngineCapture/HardwareReadiness.Tests/DxgiInventory.cs`, `Program.cs` | Replace NVIDIA-only metadata gate with production inventory and strict cross-vendor identity assertions |
| `EngineCapture/DisplaySelection.Tests/DisplaySelection.Tests.vbproj`, `Program.vb` | Reference production inventory; extend persistence/monitor-replacement/JSON tests |
| `EngineCapture/Composition.Tests/CaptureComposition.Tests.csproj`, `Program.cs` **new** | 78 device-free composition, virtual-device, identity, rebuild and cleanup assertions |
| `EngineCapture/Routing/BackendCatalog.cs`, `Routing.Tests/Program.cs` | Intel planned endpoint with regression preventing execution |
| `EngineCapture/Duluka Capture/Tests/Video/DulukaEngine.Video.Tests/HardwareGate.vb`, `Program.vb` | Explicit `--offline` option; no hardware probe and all live DDAGRAB cases counted as SKIP |
| `EngineCapture/Invoke-CaptureHardwarePreflight.ps1` | Cross-vendor driver metadata, no nvidia-smi dependency; existing worker idle/disk guards preserved |
| `EngineCapture/Invoke-IntelCaptureI1Regression.ps1` **new** | Reproducible safe 12-stage build/regression runner with per-stage logs and exit-code manifest |

`source-manifest.json` records changed/new project-source paths and SHA-256 hashes at completion. Documentation from the earlier audit was already untracked and has been preserved. `Encoding/NVENC/` has no source diff; no new native QSV encoder is present.

## Actual read-only hardware evidence

The production inventory returned:

| Entry | LUID | Identity | Attached / eligible |
| --- | --- | --- | --- |
| Intel UHD Graphics, index 0 | `00000000:0000bf60` | PCI 8086:9B41, PnP ID matches independent Intel controller metadata | Output 0, 1920×1080; eligible |
| Intel-looking entry, index 1 | `00000000:0001127a` | `ROOT\DISPLAY\0000`; independently named Parsec Virtual Display Adapter | No outputs; not physical/eligible |
| Microsoft Basic Render Driver, index 2 | `00000000:0000c2ac` | Software flag, no CCD physical adapter path | No outputs; excluded |

The Intel output has technology `2147483648` (0x80000000, internal panel). CCD returns an adapter device path for the requested LUID, enabling PnP correlation; indirect output types 16/17 are excluded independently of vendor names. [Microsoft adapter-name contract](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_adapter_name), [output-technology definitions](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology).

No D3D11 device, `DuplicateOutput`, `AcquireNextFrame` or audio device is created by this inventory. The 8-assertion real metadata gate passed. Its Intel plan explicitly reports `wired=False` and both actual capture/encoder proofs false. LUIDs above are observations for this boot only.

## Final test results

| Suite | Passed | Failed | Skipped | Evidence |
| --- | ---: | ---: | ---: | --- |
| Hardware/disk/LUID dry run | 24 | 0 | 0 | `hardware-dryrun.log` |
| Route/capability/HTTP/isolation | 33 | 0 | 0 | `route-guards.log` |
| Display selection and screenshot argument/persistence checks | 21 | 0 | 0 | `display-selection.log` |
| Routing catalog | 14 | 0 | 0 | `routing.log` |
| Configuration | 95 | 0 | 0 | `configuration.log` |
| New I1 composition/identity | 78 | 0 | 0 | `composition.log` |
| Real read-only DXGI/CCD metadata | 8 | 0 | 0 | `dxgi-metadata.log` |
| Recording state/failure recovery | 15 | 0 | 0 | `recovery.log` |
| Video contracts, explicit offline mode | 38 | 0 | 28 | `video-contracts-offline.log` |
| Encoder/NVENC contracts | 64 | 0 | 5 | `encoder-contracts.log` |
| Session-result contract | 10 | 0 | 0 | `session-result-contracts.log` |
| Production inspector: retained CBR negative case | 2 | 0 | 0 | `retained-qsv-synthetic-mp4-validation.log` |
| Production inspector: retained VBR positive case | 2 | 0 | 0 | `retained-qsv-vbr-validation.log` |
| Production inspector: retained CQP positive case | 2 | 0 | 0 | `retained-qsv-cqp-validation.log` |
| **Total** | **406** | **0** | **33** | |

The original 181 checks are retained within the first five suites, now 187 due to 5 new display checks and 1 routing check. The 28 live DXGI tests were not run because real screen capture is not authorized. The 5 native NVENC hardware tests skipped because no NVIDIA GPU is available. These skips are not hardware passes. Expected negative/fault-injection diagnostics in the console logs are separate from final assertion failures.

The first runner attempt found a linked-source VB namespace resolution error in `RecordingDTOs.vb`; it was corrected with the `Global.` prefix. `session-result-contracts-initial-build-failure.log` retains that diagnostic. The complete final 12-stage runner then passed; `checks.json` contains its actual exit codes. No source validation or failing assertion was disabled.

## CBR known failure and media evidence

No new recording/encode was performed in I1. The production `CaptureMediaInspector.InspectWithVideoDecode` was run against the retained Stage-A synthetic files:

- VBR and CQP: accepted with full decode; their original deterministic probes show 60 packets and 60 decoded frames.
- CBR: the structural packet inspector accepts the complete container, but full decode rejects it as `video_decode_failed`. Its original probe found 60 packets but only 28 decoded frames and NAL errors, even though encode exit was 0.

The negative CBR validation assertion passing means the corrupt file is correctly rejected. It is **not** a CBR repair or a media-success claim. Original encode arguments, frame-count JSON and exact temporary file paths remain in the [Stage-A evidence index](../intel-duluka-2026-10-24/README.md).

Each media-validator command used the existing `MediaEvidence.Tests/CaptureMediaInspector.Tests.vbproj` with:

```text
--decode-only <bundled-ffprobe> <bundled-ffmpeg> <retained-synthetic-mp4> corrupt|valid
```

## Reproduction and limits

```powershell
cd 'C:\My Project\NVIDIA-Shadowplay'
& '.\Project\NVIDIA Capture\EngineCapture\Invoke-IntelCaptureI1Regression.ps1'
dotnet build '.\Project\NVIDIA Capture\NVIDIA Capture.vbproj' -c Release -t:Rebuild --nologo -m:1
```

The modified older hardware-preflight script was not run end-to-end: it retains a guard requiring an already running idle production worker. The dedicated I1 runner builds/test-drives its identity and disk seams without launching a worker. PowerShell source parsing is checked separately.

Native Intel encoding, live capture/encode LUID matching, delayed-encoder flush, finalized-recording truth, WGC monitor fallback, game/API compatibility and performance remain I2/later work. Existing NVENC still cannot explicitly select a second NVIDIA encoder adapter; I1 rejects that execution before capture while retaining the requested identity in the plan. Schema-1 historical monitor identity and FFmpeg CLI actual-device attestation limits are documented in the I2 handoff.

Logs are local artifacts ignored by the repository's existing `*.log` rule. Source, documentation and JSON evidence remain uncommitted. No skipped or metadata-only check is represented as a live recording pass.
