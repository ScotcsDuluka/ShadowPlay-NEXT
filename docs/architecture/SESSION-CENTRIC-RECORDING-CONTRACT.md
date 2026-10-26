# ShadowPlay NEXT — Session-Centric Recording Architecture

**Decision ID:** SPN-ADR-2026-10-24-SESSION-001  
**Status:** ACCEPTED — canonical target design; features remain gated by actual implementation and tests  
**Date:** 2026-10-24 (Asia/Bangkok)  
**Owners:** ShadowPlay NEXT / Duluka Capture; applies to both Intel and NVIDIA development worktrees.

> **NON-NEGOTIABLE:** Duluka owns ONE logical RecordingSession with one SessionId, timeline, output composition, audio plan, selected encoder and mux/finalization. Desktop and Fullscreen Exclusive are **replaceable VIDEO SOURCES** feeding this same session. Their capture APIs are configurable independently; the encoder is selected independently. OBS and NVIDIA keep their OWN native sessions/recording engines.
>
> This document is the canonical architectural contract. Old `docs/ARCHITECTURE.md` descriptions are historical where inconsistent. **Do not delete, replace, or quietly weaken this contract.** Revisions require an explicit dated amendment with rationale and compatibility/regression impact. A design slot is never proof that its backend is implemented.

## A. High-level session graph

```text
                         ShadowPlay NEXT / OSC CONTROLLER
                                    |
                    Engine: Duluka | FFmpeg | OBS | NVIDIA
                                    |
           +------------------------+----------------------------+
           |                        |                            |
         DULUKA                Standalone FFmpeg           OBS / NVIDIA
           |                  owns its own session       own their own pipeline
           v                        |                            |
  +------------------------------------------------+          commands/status
  | RECORDING SESSION                              |          only (not frames)
  | SessionId + Clock/QPC/PTS + State               |
  | Selected Display Target [LUID, output, ID]      |
  | Black/Placeholder Composition Surface (internal)|
  | Audio Tracks + Encoder Plan + Mux + Evidence    |
  +------------------------------------------------+
           |
           +--> DISPLAY / PRESENTATION MONITOR
           |             |
           |       +-----+----------------------------------+
           |       |                                        |
           |  DESKTOP MODE                          FULLSCREEN EXCLUSIVE
           |       |                                        |
           |  Desktop Capture API                    Exclusive Capture API
           |  [DXGI / WGC* / FFmpeg*]                 [DXGI where valid /
           |       |                                  WGC* / Verified Source*]
           |       +------------------+---------------------+
           |                          |
           |                    FRAME ROUTER
           |             [source generation + LUID + PTS,
           |              format/size/rotation + gap flags]
           |                          |
           |               COMPOSITION / FRAME QUEUE
           |                          |
           +-------------------> VIDEO ENCODER
           |                        [Intel QSV | NVIDIA NVENC |
           |                         AMD AMF* | Software*]
           |                          |
           |                   ENCODED PACKET SINK
           |                          |
           +--> AUDIO TRACKS --------+ 
           |    System + Mic, independent on/off
           |    [Duluka WASAPI | FFmpeg Audio*]
           |                          |
           +--------------------> MP4 MUXER
                                      |
                             Finalize -> MediaInspector
                                      |
                           Verified MP4 | Partial | Failed
```

Asterisk `*` = optional / future / not necessarily wired. Availability MUST be determined by real backend capability, not merely the presence of a selection label. Desktop Duplication can capture some exclusive modes without any special ExclusiveFrameSource; do not falsely force a switch in that case.

## B. Invariants

1. **The session is the owner.** Acquisition backends must never independently own a new MP4, encoder, SessionId or audio clock when Duluka owns the recording.
2. **The black canvas is internal.** Stable output surface/format, not an OS window the user must display. Tagged black placeholder during interruption is allowed by explicit policy, but never described as actual captured gameplay.
3. **Desktop and Exclusive have separate *API selections*.** They may resolve to the same implementation if evidence proves it works; separate settings do not imply two copies of the engine.
4. **Encoder selection is independent** of the capture mode, but only compatible combinations may start. Verify capture-device LUID and encoder-device LUID, formats, ownership, video rate-control and packet timing.
5. **Audio capture is independently selectable** for System and Microphone tracks; two audio providers are alternatives, not duplicated capture. A disabled track must stay off.
6. **The recording clock continues across recoverable video-source switches.** Do not restart audio/encoder/mux solely because Desktop became Exclusive or Alt+Tab occurred.
7. **No heavy per-frame mode detection.** Use display/mode notifications and bounded source-health monitoring; use measured frame progress and access-lost errors.
8. **No arbitrary target migration.** LUID, monitor identity and output must remain the selected target. Never silently capture a Parsec virtual output or another monitor.
9. **No optimistic success.** Encode exit 0, an MP4 header, a Stop event or a present placeholder are insufficient evidence of a valid saved recording.
10. **No forced game-mode changes.** Do not minimize games, force Borderless, change resolution or inject hooks as an automatic workaround.
11. **The GUI must describe honest status.** Available, Experimental, Not wired, Unsupported, Unverified and Verified are distinct; a roadmap option must not be clickable as if functional.
12. **OBS/NVIDIA adapters stay outside this data plane.** UI/Controller can normalize status and commands while those products own video/audio/encoding and output.

## C. User-facing selections (future OSC Configuration)

| Setting | Selectable value / meaning | Availability requirements |
| --- | --- | --- |
| `Recording Engine` | `Duluka`, `FFmpeg`, `OBS`, `NVIDIA` | Only engines with a working controller and runtime |
| `Display Target` | stable monitor identity, current adapter LUID + output, current mode | Physically eligible, resolved and revalidated |
| `Desktop Capture API` | `Auto`, `DXGI Desktop Duplication`, `Windows Graphics Capture monitor`, `FFmpeg ddagrab`, `FFmpeg gdigrab` | Per-provider readiness, scope and evidence |
| `Exclusive Capture API` | `Auto`, `DXGI Desktop Duplication`, `Windows Graphics Capture monitor`, `Game/Present Frame Source` (future) | Must actually capture the selected game's tested presentation path; never assume hooks or protected modes work |
| `Encoder Provider` | `Auto`, `Intel Quick Sync`, `NVIDIA NVENC`, `AMD AMF`, `Software` | Codec/profile, hardware/driver, bound device and capture-frame transport must be compatible |
| `Video Codec` | `H.264`, `HEVC`, `AV1` where verified | Advertised != supported; honor actual result |
| `Rate Control` | provider-specific `VBR/CQP/CBR/...` | No silent substitution, validate full media decoding |
| `System Audio` | Off / Device + source provider (`Duluka WASAPI`, `FFmpeg Audio` when implemented) | Explicit opt-in, actual stream readiness |
| `Microphone` | Off / Device + source provider | Independent selection/clock, no unintended capture |
| `Switch Policy` | `Automatic verified fallback` / `Manual source/API lock` | Bounded safe source switching, compatibility checks |
| `Gap Policy` | Tagged black, bounded last-frame, segmented output, or fail | Explicit, truthful gap metrics and bounded duration |

**Auto** picks from the user-approved, currently available and verified candidates; it must show what was actually selected. **Manual** requires the specified API and must not silently select another API or encoder on failure. A separate ordered fallback allowlist can be provided for each mode (Desktop and Exclusive). Never cross the user's selected display boundary.

A setting can be visible but disabled with a reason such as `not_implemented`, `not_tested_on_this_gpu`, `protected_content`, `driver_unsupported`, `capture_encoder_luid_mismatch`, `frame_format_incompatible`, `mode_unsupported`, `recording_active`. This is preferable to fake availability.

### Configuration shape — design schema, NOT currently deployed API

```yaml
recording:
  engine: duluka
  display:
    identity: persisted CCD monitor device ID + revalidated runtime LUID/output
  video:
    desktopApi:
      preference: auto               # or named provider
      allowedFallbacks: [dxgi, wgc_monitor]
    exclusiveApi:
      preference: auto               # independent from desktopApi
      allowedFallbacks: [dxgi, wgc_monitor]
    encoder:
      provider: intel_qsv            # requested, not automatically wired
      codec: h264
      rateControl: vbr
      strictHardwareIdentity: true
    outputSurface:
      resolutionPolicy: fixed        # or tested reconfigure/segments
      interruptedFramePolicy: tagged_black_with_limit
  audio:
    system: { enabled: false, provider: duluka_wasapi }
    microphone: { enabled: false, provider: duluka_wasapi }
  output:
    container: mp4
    mediaVerification: full_decode_when_bounded
```

All names above are **proposal only**. Do not write them into the current on-disk config until a versioned migration and backward-compatible parser/validation are implemented. Encoder profiles and actual pixel formats are provider-specific.

## D. Source selection and Fullscreen transitions

The source is selected based on verified behavior and target identity, not by a guess made for every frame. A game reporting “fullscreen” does NOT prove true Fullscreen Exclusive: Windows Fullscreen Optimizations, flips and borderless presentation can behave differently.

```text
Session PREPARING -> AWAITING_FIRST_REAL_FRAME -> RECORDING
                                         |
                 mode/geometry change, access lost, true frame stall
                                         |
                                         v
                           SOURCE_INTERRUPTED (session remains owned)
                                         |
                        same source recreation on same LUID/output
                            +----------------+------------------+
                            | succeeds                         | fails
                            v                                  v
                         RECORDING                  fallback API permitted?
                                                           |
                                                   yes     |     no
                                                    |      |      |
                                                    v             v
                                            verify next source   PARTIAL/FAILED
                                            frame + identity
                                                    |
                                         format compatible with encoder?
                                                |           |
                                              yes           no
                                                |           |
                                            RECORDING    reconfigure/segment
                                                          or PARTIAL/FAILED
```

- **DXGI recovery:** `DXGI_ERROR_ACCESS_LOST` can require releasing/recreating duplication; a mere `AcquireNextFrame` timeout is not sufficient to declare a failure.
- **Exclusive support:** try Desktop Duplication when it still delivers valid changing frames; an exclusive-only source is needed only when the regular source demonstrably fails and a compatible, permitted source exists.
- **Source generation:** each recreated source has a new generation ID; old callbacks/frames are discarded. Do not splice stale frames from the previous display.
- **Selected display:** require requested/actual adapter LUID, monitor device identity, output index, rotation and topology match before acquisition or fallback. No silent Parsec, virtual, cloned or different-monitor substitution.
- **Recovery budget:** attempts and time are bounded. If expired, return a truthful `Partial/Failed` instead of looping indefinitely or showing falsely running.
- **Frame validity:** static or black gameplay can be legitimate. Source health requires transport/device evidence and controlled changing-marker acceptance tests, not black-pixel detection alone.
- **Protected/restricted screens:** reject protected/secure desktop and restricted content; no bypass, unauthorized injection or privilege escalation.
- **Encoder continuity:** keep encoder/mux session alive only if device/size/format/PTS contracts remain valid. If output dimensions change, compose safely into the stable canvas when supported, use a tested reconfiguration/segmentation path, or end truthfully.
- **Timeline:** preserve monotonic QPC/PTS across interruptions. Tagged placeholders or bounded last-frame policy are not real video capture and must be counted separately.
- **No duplicate frames hidden as real progress:** `RealFrameCount`, `PlaceholderFrameCount`, `DroppedFrameCount`, `EncoderPacketCount`, `MuxedPacketCount` are distinct.

## E. Component ownership and contracts

| Component | Owns | Must not own |
| --- | --- | --- |
| `RecordingSession` | lifecycle, SessionId, clock/timestamps, capture/encoder/audio/mux plans, frame budget, authoritative results | game-specific hook code |
| `CompositionSurface` | stable output frame descriptor; optional tagged black placeholder, crop/scale policy | visible Windows black window requirement |
| `DisplayModeMonitor` | presentation/topology changes and source-health signals | forcibly changing game focus/resolution |
| `DesktopFrameSource` | bound target capture, timestamped frames and access-lost recovery | MP4 or audio |
| `ExclusiveFrameSource` | independently verified game/mode frame acquisition when needed | unproven all-game support |
| `FrameRouter` | runtime source generation, frame validation, normalized timestamps, transitions | controlling encoder codec without session consent |
| `AudioTrackSource` | PCM samples, system/mic device timestamps, chosen provider | Saved notification or final MP4 |
| `VideoEncoderProvider` | GPU-bound frame encode, owned surfaces, delayed packet drain + `Flush` | choosing source/target, audio or final file success |
| `SessionMuxer` | interleaving audio/video, segmentation, packet timing, final container | mode detection |
| `MediaInspector` | finalized MP4 evidence, packet scan/full decode as required | optimistic saved notification |

Required conceptual values: `CaptureTarget` (stable monitor ID + runtime LUID and output); `CaptureFrame` (source generation, PTS/QPC, format, dimensions, rotation, texture ownership, placeholder/gap origin); `AudioPacket` (track, sample format, device clock, samples); `EncodedPacket` (PTS/DTS, codec, keyframe and delayed/flush origin); `RecordingResult` (media evidence, segment list, dropped/gap intervals, failure reason).

Existing `IVideoCaptureBackend`, `IVideoCaptureRuntime`, `IEncoderBackend` and current Session DTOs are the compatibility floor. Introduce additive interfaces and adapters, not breaking ABI changes just to match names in this document.

## F. Audio architecture — same session, independent sources

Duluka has one audio timeline under the RecordingSession, with independently controllable **System Audio** and **Microphone** tracks. Each enabled track chooses one actual provider; default must honor the user's current saved audio permissions.

- **Duluka Audio provider:** reuse `AudioEngineSession` and the WASAPI device-position-aware capture components. The existing code contains device-clock/audio-gap logic and bounded sinks; preserve verified behavior.
- **FFmpeg Audio provider:** an architectural option, not proof it works in native Duluka. `Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/AudioSidecar.vb` is documented as a **dormant no-op STUB** with no real PCM recorded. Do not enable it until a tested implementation is integrated and the owner of its clock is clear.
- **Standalone FFmpeg Engine:** can own its own complete audio/video session; it does NOT need to feed audio into Duluka's native Session.
- **OBS/NVIDIA Engines:** they own audio with their own recording backends. Controller only reports verified status.
- During a recoverable video-source interruption, audio can keep running if enabled and the device stays available. Correct QPC/device timestamp alignment and drift; do not fabricate recorded sound from silence or duplicate system audio across providers.
- If device or provider changes mid-session, require a separately tested discontinuity/resample/reconfigure policy; otherwise explicitly stop the affected track or session with truthful diagnostics.
- No real system or microphone capture without explicit user-controlled enable state, including in tests.

## G. Encoder -> Session Mux -> MP4 contract

The encoder does **not** own the recording file. RecordingSession selects a verified provider/codec/profile compatible with the currently resolved target and output format, passes frames, collects encoded packets and drains delayed output. The session-owned muxer interleaves audio and video, finalizes MP4 and hands it to MediaInspector.

- Providers: NVIDIA NVENC, Intel QSV, AMD AMF, or software only when **actually implemented, enabled and validated**. An unimplemented factory slot stays disabled.
- Use exact device identity (adapter LUID, owner D3D11 device), capability checks, pixel-format/stride/rotation, resolution and rate-control policy. Same LUID does not automatically allow a foreign D3D11 texture to be copied without a proper shared-resource contract.
- An encode call may *accept* a frame but emit no immediate packet. Don't retry a delayed frame as if rejected, and don't drop outstanding QSV surfaces. All `Flush` packets must reach the muxer.
- No silent encoder switch (Intel -> NVIDIA/CPU) or rate-control fallback during an active recording. Such a change requires compatible explicit policy, device support and verified resulting media.
- Keep source-frame count, packet count, muxed packet count, duration and decoded-frame count separate, with invariant timestamps and a bounded frame queue/backpressure policy.
- State machine for user truth: `Created -> Preparing -> AwaitingFirstRealFrame -> Recording -> SourceInterrupted/Recovering (optional) -> Stopping -> Draining -> Finalizing -> Verifying -> Saved | Partial | Failed | Cancelled`.
- `Saved` requires a finalized file and positive evidence appropriate to size/duration (streams, packet scan, valid frame decode; entire decode for bounded acceptance). A file header or process exit code is not enough.
- **Known Intel QSV issue on 2026-10-24:** synthetic H.264 VBR/CQP passed 60/60 decoded frames, but tested CBR produced 60 readable packets and only 28 decoded frames. That configuration is **FAILED** until its root cause and a full-decode fix are proven. Do not silently call it supported.
- A mode switch that cannot preserve encoder contract must use tested reconfigure, explicit segments or `Partial/Failed`; never silently write mixed incompatible streams into one corrupt MP4.

## H. Runtime compatibility planning and UI truth

Before Start, `RecordingPlanResolver` should evaluate a *tuple*:

```text
(Engine, Target LUID/Output/Monitor ID, Desktop API, Exclusive API,
 Encoder Provider, Codec, Profile, Rate Control, Output Geometry,
 Color Format, Audio Providers/Devices, Switch/Gap Policy)
       |
       +--> capability inventory and current driver/OS
       +--> actual provider wiring and target/device attestation
       +--> (source -> frame format -> encoder) transport compatibility
       +--> allowed transition routes and reconfigure/segment capability
       +--> permitted audio clock + mux format
       |
       +--> Runnable plan with evidence + reasons
              OR precise NotReady / Unsupported / Unverified
```

Planner and UI must separate `Declared`, `Compiled`, `Wired`, `DeviceEligible`, `SyntheticTested`, `LiveFrameVerified`, `MP4Verified`, `PerGameVerified`. Never change a persistent user choice during a read-only GET. UI shows requested vs resolved vs actual backend separately and exposes recovery/gap/segment status during recording.

A *manual API choice* fails closed when unavailable. Automatic fallback is constrained by an explicit allowlist; do not silently cross to a capture API that could expose a different display. Show why a choice is disabled.

## I. Implementation reality (not promises)

| Component | Evidence on 2026-10-24 | Honest state |
| --- | --- | --- |
| Native Duluka Session, frame sink, lifecycle, audio/mux coordinator | `Recording/DulukaEngine.Recording/CaptureSession.vb` | Exists; NVENC-biased, migration incomplete |
| Desktop DXGI source | `Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/DdagrabBackend.vb` | Existing; Intel I1 source/target work; Intel live recording unproven |
| Intel capture/display identity + factory | `docs/INTEL-DULUKA-CAPTURE-I2-HANDOFF.md` | Local I1 changes; 400 checks pass, 33 skipped reported; not proof of live Intel media |
| Mode monitor / automatic Desktop ↔ Exclusive switch | Planned from this ADR | **NOT IMPLEMENTED / NOT VERIFIED** |
| Exclusive-only frame source | Future plugin/adapter slot | **NOT IMPLEMENTED / NOT VERIFIED** |
| Duluka WASAPI audio | `Audio/DulukaEngine.Audio/AudioEngineSession.cs` | Existing building block; Intel full E2E audio unproven |
| FFmpeg AudioSidecar | `Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/AudioSidecar.vb` | **DORMANT STUB**; no audio samples |
| Intel QSV | FFmpeg synthetic one-frame and VBR/CQP tests; I2 native work pending at baseline | QSV synthetic proven, **native Duluka provider not proven** |
| Native NVIDIA NVENC | Existing vendor encoder; separate AORUS runtime tests | NVIDIA-specific evidence, not Intel validation |
| AMD AMF | Architecture slot | Not demonstrated here |
| MP4 inspection | `src/[API]/CaptureMediaInspector.vb` | Implemented evidence checks; CBR corrupt case is negative control |
| OBS / NVIDIA separate recorders | Existing external engine integrations where wired | Own session, audio, encoder and MP4; no shared-frame assumption |

I1 evidence: [Intel implementation handoff](../INTEL-DULUKA-CAPTURE-I2-HANDOFF.md) and [I1 report](../reports/intel-duluka-i1-2026-10-24/README.md). This table records a **baseline date**, not an automatic reflection of future Codex changes.

## J. Phased roadmap and acceptance (each gate independently tested)

1. **I1 / identity + composition factory:** stable target (LUID/CCD monitor/output), capture/encoder factory, NVIDIA compatibility, Intel physical vs Parsec mapping. Local development already documented; preserve 400-check result.
2. **I2 / Intel QSV synthetic:** native encoder contract, actual Intel device attestation, bounded async packet ownership and flush, synthetic owned input -> MP4 -> full decode. CBR remains disabled until fixed.
3. **I3 / first live Desktop source:** selected Intel physical display -> timestamped real frames -> single Duluka Session -> QSV -> verified MP4, only with privacy-cleared scene.
4. **I4 / selectable Desktop and Exclusive APIs:** capability inventory and UI/settings, source-mode monitor, bounded recovery and explicit fallback, format and timestamp continuity. Test real Windows presentation cases and APIs separately.
5. **I5 / audio and final acceptance:** optional system/mic tracks, device clocks and drift, complete mux, error/partial states, verified Saved toast, interrupted-source tests.
6. **I6 / external engine integration:** OSC Controller presents unified choices/actual statuses while OBS, NVIDIA and standalone FFmpeg retain independent recording engines.

Tests required: desktop and borderless, D3D9/D3D9Ex exclusive, D3D10, D3D11, D3D12, Vulkan and any additional supported API **each independently**; Alt+Tab, mode/refresh/resolution change, `DXGI_ERROR_ACCESS_LOST`, slow GPU, hotplug, Parsec virtual adapter, stale callbacks, target LUID mismatch, encoder delayed packets, stopped audio, fail-before-first-frame, corrupted media and end-to-end recovery. **No universal Fullscreen Exclusive guarantee** until each case is measured.

No privacy-sensitive desktop/audio/game capture is authorized merely by accepting this ADR. Safe synthetic and read-only tests may proceed; live capture requires a deliberately cleared scene and explicit recording scope.

## K. Persistence and amendment rules

- This contract is the root decision for Duluka Session-Centric Recording. Link it from `docs/ARCHITECTURE.md`, the Duluka Capture README and top-level READMEs; keep the dedicated Intel audit/handoff as supporting evidence, not as a competing architecture.
- Do not erase the original NVIDIA-specific implementation or strip validation to force Intel to pass. Existing ABI and tests remain a required compatibility floor.
- The Codex I1/I2 development worktree contains uncommitted changes. **No automatic commit, push, checkout, reset, merge or source overwrite is implied by saving this document.**
- Changes to this contract must record date, author/owner instruction, old vs new invariant, why, compatibility, tests and migration steps. Store an independent backup outside the repo as protection against accidental edits while local Git state remains uncommitted.
- Architecture acceptance is not feature acceptance. Each implementation step must carry source diff, test evidence, build result and truthful remaining limitations.

### Architecture decision summary (ภาษาไทย)

**Duluka RecordingSession = เจ้าของการอัดหนึ่งชุด** พร้อม Clock, Composition Surface (จะเป็นภาพดำสำรองภายในก็ได้), Audio, Encoder, Mux และ MP4. ผู้ใช้เลือก **Desktop Capture API, Exclusive Capture API และ Encoder ได้เป็นอิสระ** แต่ระบบตรวจความเข้ากันได้ก่อนใช้งาน. หากเกมสลับ Desktop/Exclusive ให้พยายามเปลี่ยนเฉพาะแหล่งเฟรมโดยรักษา Session, เวลาและเสียงเดิม; หากทำไม่ได้ต้องรายงาน Partial/Failed ไม่ใช่ทำไฟล์เสีย. FFmpeg/OBS/NVIDIA ที่เป็นเครื่องอัดแยกให้ใช้ Session และระบบของตัวเอง. ฟีเจอร์ยังไม่พร้อมต้องแสดง Not Implemented/Unverified ตามจริง.

