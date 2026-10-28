# Capture unified architecture audit — 2026-10-27

Status: implementation and validation in progress; final runtime results are recorded below as they complete. No commit, index staging, merge, pull, push, branch switch, source reset, or cleanup was performed.

## Scope and ownership

Source: `C:\My Project\NVIDIA-Shadowplay`, branch `Stable`. Canonical architecture remains SPN-ADR-2026-10-24-SESSION-001 in `docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md`; its Thai companion, Intel architecture audit and I2 handoff were read before implementation. The canonical contract was not changed.

NvCapture owns recording selection, lifecycle and evidence. Container is the policy/authorization gateway. NodeAPI relays its status. Duluka retains one CaptureSession, clock, source generation, encoder packet sink, audio plan and mux. Standalone FFmpeg retains its independent process pipeline. OBS and the installed NVIDIA recorder remain unavailable/unverified integrations; NVENC is an encoder and does not establish NvFBC or NVIDIA external recorder support.

The user authorized NVIDIA tests on AORUS and Intel access through MCP. Root's live NVIDIA tests use fullscreen test-owned moving grids/counters/barcodes on every attached screen, both system audio and microphone disabled. Intel tests use owned synthetic pixels and actual QSV hardware. No game, fullscreen mode, user microphone, or actual system audio recording is covered by those results.

## Preflight and preservation

| Evidence | AORUS-DESKTOP | HUAWEI-MateBook |
| --- | --- | --- |
| Branch | Stable | Stable, verified through MCP |
| HEAD and cached origin/Stable | e63b52544a1dd54e51e9d75f2099c808f8a2e392 | Same |
| Live GitHub refs/heads/Stable | e63b52544a1dd54e51e9d75f2099c808f8a2e392 | Same |
| Uncommitted snapshot | `C:\My Project\_CaptureAuditSnapshots\20261027-014248` | `C:\My Project\_CaptureAuditSnapshots\20261027-014830` |
| Files preserved | 177 dirty/untracked files, 4,805,426 bytes; exact copies and SHA256 manifest | 13 existing dirty/untracked evidence files; exact copies and SHA256 manifest |
| Initial custom runtime | NvCapture PID30016, Container PID26152, NvNode PID41660 | No custom NvCapture/Container/Node processes observed |
| Initial ports | 59077, 59012, 59011 listening on127.0.0.1 | Production runtime not launched during preflight |
| Initial recording | /health and /state report false | No running worker observed |

Initial endpoint bodies, process identities and executable hashes are retained under [capture-unified-20261027](capture-unified-20261027/): `runtime-before.json`, `hashes-before.json`, `endpoints-before.json`. Remote PowerShell's service PATH did not contain Git; the verified installed `C:\Program Files\Git\cmd\git.exe` was used. The failed first read-only attempt did not change files.

Preexisting OSC, Hook, Launcher, Gallery/server and other module changes were preserved. This task changes the existing Container constructor only to supply the protected worker credential; it does not replace the preexisting hotkey work. No OSC HTML/CSS/component changes or Hook changes are part of this task.

## Source/build/runtime inventory and repair

All source paths below are relative to the repository.

| Component and symbol | Original implementation / defect | Implemented result / evidence limit |
| --- | --- | --- |
| `Project/NVIDIA Capture/src/[API]/CaptureRestApiServer.vb`, RestTruth/RestActiveRoute | A queued native task or optimistic external recorder flag could imply an actual engine | Lifecycle owner/busy/preparation are separate from actual running; native capture requires real-frame and encoded progress. Hardware/media verification remain independent. |
| New API runtime-truth, write-security and settings-policy classes | Direct loopback POST had no credential; settings defaults could override canonical saved config | Mutating routes require the existing CurrentUser DPAPI pairing token. Reads cannot enroll/rotate it. Requested/saved/selected/available/running/verification fields are additive under existing engines schema2. Explicit settings overrides apply consistently to both engines; absent override store does not invent60FPS/50Mbps. |
| `CaptureHttpRequestReader.vb` | Request headers were not retained for worker authorization | Headers are retained for bounded authorization; invalid/missing enrollment or token fails closed. Secrets are not returned or logged. |
| `Project/NVIDIA Container/Components/Runtime/CaptureWorkerGateway.cs` | Worker writes were unauthenticated; queued cancellation and stopped-vs-saved were ambiguous | POST-only protected header supplier; no credential on GET. Stop can target an owned preparing lifecycle without inventing a running engine. Partial and Saved evidence pass through separately. |
| `CaptureEngineCatalog.cs` | Locked controller policy selected Duluka while worker config selected FFmpeg | Explicit `controllerPolicyMatchesWorker`, worker-owned `effectiveSelection` and separate configuration/actual/verification fields; read-only GET never rewrites the user choice. |
| `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/capture-runtime-status.js` | Direct worker /health reported ready while selected FFmpeg hardware readiness was false | Reads Container /v1/capture/status. Worker ready and selected engine ready are distinct. Active or unknown recording state is not masked as idle. Initial NodeAPI was responding200; no claim of repairing an unobserved outage. |
| `Project/NVIDIA Capture/NVIDIA Capture.vbproj` | Managed references already used ProjectReference | Preserve those references and verify a fresh dependency graph; no stale bin/Release HintPath was substituted. |
| `Scripts/build-dev.ps1`, new Capture-only build script | Building Capture required unrelated CEF/C++ modules; native QSV assets could silently be absent | Capture-only isolated Release output, fresh artifacts directory, dependency closure and source/artifact hashes. Required native package fails closed. Default All packaging also explicitly stages the QSV license. |
| Intel native package generator/validator and QSV csproj | Bridge, dispatcher or license could be individually absent | Flat package validates current native/CMake source hashes, pinned libvpl revision, x64 PE, license hash and actual ABI3 export before staging. Missing optional package is explicit; it does not break NVIDIA, while native QSV initialization remains closed. |
| `CaptureSession.vb`, `RecordingEngine.vb`, `RecordingDTOs.vb` | Live counters were unpopulated; Saved did not require complete media evidence | Thread-safe source/submission/packet counters, actual encoder geometry, full decode/counts/audio-intent verification, explicit Pending/Partial/Failed/Cancelled. Native long recordings are not subject to the synthetic3600-frame fixture cap. |
| `SessionFrameRouter.vb`, `SessionPacketSink.vb`, `CaptureSession.OwnedFrames.vb` | Detached/stale topology could enter controlled route; source failure bypassed delayed drain | Reject stale/invalid/disposed inputs; one-time delayed drain and packet conservation into the same mux; preserve primary error and verified Partial or Cancelled. Unsupported owned-frame audio returns an authoritative failed SessionResult before device start. |
| `LiveMuxSession.vb` | Mic-only mapping referenced nonexistent input2; padded mixed inputs could outlive video | Correct independent input indexing and bounded mixed EOF; five test-owned PCM intent combinations validated. |
| `src/[Capture]/LegacyCaptureEngine.vb`, immutable CompletionEvidence | Forced/nonzero child exit could settle Idle and later look Saved | Completion records graceful zero-exit, fatal stderr, forced stop and mux outcome against the exact output; API takes one immutable snapshot. Idle alone never proves Saved. |

Detailed symbol/command evidence: [Control plane](capture-unified-20261027/control-plane.md), [Session/media](capture-unified-20261027/session-media.md), [Unified build](capture-unified-20261027/unified-build.md).

## Native package provenance

The original MateBook native source and CMake hashes matched this Stable source before receipt creation. Package transport was a bounded one-shot LAN transfer, with archive and individual file hashes checked at both ends. No credential, private config, desktop pixels or audio was transferred in that package.

| Artifact | SHA256 |
| --- | --- |
| DulukaQsvNative.dll, ABI3 x64 | 986CB127AB280403D1098D6D3266ACAC63B3A6F936E37E923E1757B6B1E4B543 |
| libvpl.dll | 75233A36F1C4FDC82C9CE2E0F8221881516A11C380611E135E31EF28A9F2BE96 |
| libvpl-LICENSE.txt | 6B53987F5DE35662CCBCF2753DC84A713068443ACB482FD6FD431FF7CF5DA9F9 |
| Native source qsv_bridge.cpp | 00339B5031A10F1AEFD69EE533AAF05AFB9A4FAFA82464B461BE2D826A3FB65D |
| CMakeLists.txt | 09C0491F47205CF4FFC20A25715DEAACA2A7857E1EB4D4D79312ABAEA03B5AD4 |

Pinned Intel dispatcher revision: `778a66d6c6537f08eabb91955dbbf1bce3812894` (2.16.0). Actual ABI export loaded successfully on both NVIDIA and Intel hosts without opening a capture or encoder device. That package validation is separate from hardware encoding/media acceptance.

## Validation completed before final runtime acceptance

| Gate | Result | Evidence boundary |
| --- | --- | --- |
| API routes/parser/authorization/truth/settings/isolation | 67 checks pass | Device-free; includes protected vault read without secret disclosure |
| Container gateway | 59 checks pass | Stub HTTP including missing/invalid credentials, cancellation, unknown outcomes and Partial relay |
| Container display relay | 26 checks pass | Stub HTTP; no screenshot or display change |
| Container engine/policy catalog | 24 checks pass | Worker truth vs locked policy |
| Container request security / pairing / hotkey | 13 /14 /59 pass | Isolated security and existing concurrent hotkey compatibility |
| Node Capture routes and existing related UI tests | 61 pass | Actual loopback fixtures; no live OSC click traversal |
| Session contracts | 35 pass | No GPU/audio devices |
| Media acceptance and failure fixtures | 43 pass | Generated H.264/MP4, full decode/counts, corrupt output, cancellation/Partial, native timebase rounding,3601-frame native acceptance |
| Audio intent mux | 5 pass | Test-owned PCM; off, system-only, mic-only modes and mixed EOF; not microphone/WASAPI readiness |
| Container Release | Pass,0 warnings/errors | Isolated artifacts; dependencies rebuilt |

Numbered failed build attempts are retained. Native package integration exposed Windows PowerShell5.1 argument/module-path edge cases; those were corrected rather than bypassing ABI validation. Main Capture build warnings and final two-machine results are recorded by the build workstream.

## Live NVIDIA failure evidence and correction

Attempt01 is preserved in [nvidia-desktop-attempt-01](capture-unified-20261027/nvidia-desktop-attempt-01/). Native DXGI → native NVENC → session mux produced1680×1050 H.264,158 packets/frames,5.266614seconds, no audio. Full decode passed;11 sampled frames had11 distinct hashes, and the extracted preview shows the test-owned moving marker/counter rather than a placeholder. Runtime reported the same Duluka SessionId with actual real-frame and encoded progress.

The attempt correctly did not report Saved: the newly strict native verifier compared `avg_frame_rate` text exactly to30/1 while the container represented30.000299 after timestamp rescaling. It was a verifier compatibility defect, not proof of damaged media. The fix permits only the mathematically bounded container tick rounding while preserving exact counts, geometry, duration and complete decode. The retained158-frame file then passed the production native verifier. A fresh end-to-end retry is required and recorded separately below.

## Final build, deploy, restart and runtime verification

Pending final coordinated builds, fresh NVIDIA retry, Intel MCP hardware fixtures and guarded deployment. No production readiness is inferred from the preceding synthetic tests or package presence.

## Open blockers and unsupported capabilities

1. Native Intel Desktop production gate remains closed. QSV synthetic encoding/transport is separate from privacy-cleared live DXGI → QSV → accepted MP4 on Intel.
2. Intel owned-frame audio remains explicitly unimplemented. AudioEngineSession reuse alone does not supply an owned-source QPC origin, per-track lifecycle, drain and complete media acceptance. The FFmpeg AudioSidecar stub remains disabled.
3. No automatic Desktop ↔ Fullscreen Exclusive mode/source manager or independently verified exclusive-only source was added. Same-target DXGI recovery and controlled generation checks exist, but each actual game/API/mode still needs its own acceptance. No game was forced borderless, minimized, injected or moved to another display.
4. WGC overlay/window source is not a verified monitor recording backend. AMD AMF/software native encoding, OBS external recording and installed NVIDIA recorder lifecycle/NvFBC remain unavailable or unverified where unwired.
5. Real microphone, WASAPI system loopback, A/V drift under load, separate live hardware audio tracks and long-recording performance are not established by test-owned PCM mux cases.
6. Complete full-decode child processes are bounded. A deadline yields VerificationPending, not Saved; a cancellable background owner for completing very long media verification remains open.
7. Live OSC settings/click traversal, games, exclusive transitions and Hook regression require their own actual runtime flows. No claim is made from Node-only tests that those passed.

Every unresolved item is an open acceptance gap. This audit must not be used as a declaration that all phases or the full Definition of Done are complete.
