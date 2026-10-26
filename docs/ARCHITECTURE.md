# ScotcsDuluka Capture Engine Architecture

> **Canonical recording decision (2026-10-24): [Session-Centric Recording Architecture — ACCEPTED](architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) ([ฉบับภาษาไทย](architecture/SESSION-CENTRIC-RECORDING-TH.md)).** Duluka owns one logical RecordingSession; Desktop and Exclusive Capture APIs plus Encoder are independently selectable after capability validation. OBS/NVIDIA keep their own engines. This contract overrides older diagrams where they disagree.
>
> This document preserves a historical component/status inventory for the Engine-Rebuild branch. A listed component is not implied to be fully implemented unless separately marked by current implementation/test evidence.

## Intel development worktree evidence — 25 October 2026

This historical map is **not** the current proof of Intel live recording. On HUAWEI-MateBook `intel/duluka-capture`, the canonical [Session-Centric Recording Contract](architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) governs **one Duluka SessionId/clock/packet sink/mux**, with independent capture-source and encoder choices subject to compatibility. [Current MateBook Capture README](../Project/NVIDIA%20Capture/README.md) links the I1/I2/I3 source evidence.

The I3 synthetic D3D11 BGRA → staging CPU → NV12 → QSV → MP4 pipeline and subsequent guarded frame adapter were tested against **owned synthetic frames**. The adapter report recorded **1,725 passed / 0 failed / 33 skipped**; this is **not** a full live Desktop, Exclusive or audio-success claim. The `intel_native_live_input_not_verified` production gate, Intel CBR rejection and unspecified colorimetry remain. Do not upgrade the older `Native Backend Not Started` headings below as a blanket modern claim—those are historical snapshot labels predating scoped Intel work.

## Architecture

```text
ScotcsDuluka Capture Engine
│
├── Core Layer ✅ Stable
│   │
│   ├── Capture Session ✅ Done
│   │   ├── Session lifecycle
│   │   ├── Start / Stop / Dispose
│   │   └── Session ownership
│   │
│   ├── Device Ownership 🟡 Design Ready
│   │   ├── GPU ownership
│   │   ├── Encoder ownership
│   │   └── Resource lifetime
│   │
│   ├── Lifecycle State Machine ✅ Stable
│   │   ├── Created
│   │   ├── Starting
│   │   ├── Running
│   │   ├── Stopping
│   │   ├── Muxing
│   │   ├── Stopped
│   │   ├── Faulted
│   │   └── Disposed
│   │
│   ├── Error Model 🟡 Partial
│   │   ├── Backend error
│   │   ├── Process crash detection
│   │   ├── Fault propagation
│   │   └── Recovery path
│   │
│   └── Metrics / Diagnostics 🟡 Partial
│       ├── FPS parser ✅
│       ├── Frame count ✅
│       ├── Drop/Duplicate ✅
│       ├── Performance metrics 🔴
│       └── Telemetry system 🔴
│
│
├── Capture Backend 🟡 Framework Ready
│   │
│   ├── FFmpeg Backend ✅ Stable Framework
│   │   │
│   │   ├── FFmpegProcessHost ✅ Done
│   │   │   ├── Process.Start
│   │   │   ├── stdin control
│   │   │   ├── stderr drain
│   │   │   ├── Exit handling
│   │   │   └── Generation Guard
│   │   │
│   │   ├── FFmpegStderrParser ✅ Done
│   │   │   ├── frame=
│   │   │   ├── fps=
│   │   │   ├── dup=
│   │   │   ├── drop=
│   │   │   ├── speed=
│   │   │   └── Error detection
│   │   │
│   │   ├── FFmpegPipelineBackend ✅ Stable
│   │   │   ├── Start/Stop lifecycle
│   │   │   ├── Race protection
│   │   │   ├── Restart safe
│   │   │   ├── Mux coordination
│   │   │   └── Stress tested
│   │   │
│   │   ├── MuxCoordinator 🟡 Partial
│   │   │   ├── ffprobe support ✅
│   │   │   ├── mux execution ✅
│   │   │   ├── cleanup ✅
│   │   │   └── Production validation 🔴
│   │   │
│   │   ├── ddagrab 🔴 Not Started
│   │   │   └── Windows Desktop Capture
│   │   │
│   │   ├── gdigrab 🔴 Not Started
│   │   │   └── Windows fallback
│   │   │
│   │   └── avfoundation ⚪ Future
│   │       └── macOS support
│   │
│   └── Native Backend 🔴 Not Started
│       │
│       ├── DXGI Desktop Duplication 🟡 Spike Proven
│       │   ├── Capture test ✅
│       │   ├── D3D11 device ✅
│       │   └── Production backend 🔴
│       │
│       ├── Windows Graphics Capture 🔴
│       │
│       └── NvFBC ⚪ Future
│
│
├── Encoder Backend 🟡 Architecture Only
│   │
│   ├── NVIDIA NVENC 🔴 Not Integrated
│   │   ├── H.264
│   │   ├── HEVC
│   │   └── AV1
│   │
│   ├── Intel QSV 🔴 Not Started
│   │   ├── H.264
│   │   └── HEVC
│   │
│   ├── AMD AMF 🔴 Not Started
│   │
│   └── Software Encoder 🟡 Via FFmpeg
│       ├── libx264
│       └── libx265
│
│
├── Audio Backend 🔴 Not Started
│   │
│   └── WASAPI
│       │
│       ├── System Audio
│       │
│       ├── Microphone
│       │
│       └── Audio Mixer
│
│
├── Frame Pipeline 🔴 Not Started
│   │
│   ├── Frame Acquisition
│   │   └── Capture → Frame object
│   │
│   ├── Timestamping 🟡 Research Complete
│   │   ├── QPC
│   │   ├── PTS
│   │   └── Clock normalization
│   │
│   ├── Queue / Buffer 🔴
│   │
│   ├── Frame Synchronization 🔴
│   │
│   └── Backpressure 🔴
│       ├── Queue limit
│       ├── Drop policy
│       └── Adaptive control
│
│
├── Output Pipeline 🟡 Partial
│   │
│   ├── MP4 🟡
│   │   ├── FFmpeg output path
│   │   └── Mux support
│   │
│   ├── MKV 🔴
│   │
│   └── Raw / Pipe 🔴
│
│
└── UI Integration 🟡 Existing Foundation
    │
    ├── JSON Config ✅
    │
    ├── Profile System 🟡
    │
    ├── Recording Control 🔴
    │
    └── Diagnostics UI 🔴
```

## Architectural Rules

1. **Capture Backend** is responsible for acquiring video frames; capture mechanisms are backend implementations, not the pipeline itself.
2. **Encoder Backend** is responsible for video encoding and must remain replaceable independently of frame acquisition.
3. **Audio Backend** owns audio acquisition and mixing concerns. Video/audio synchronization belongs to the Frame Pipeline rather than a capture backend implementation.
4. **Frame Pipeline** owns acquisition handoff, timestamps, buffering, synchronization, and backpressure.
5. **Output** represents the final sink/container/pipe layer and must not own capture-device lifecycle.
6. **Core** owns Capture Session, device ownership, lifecycle, error semantics, and metrics/diagnostics.
7. **Future** components are architectural targets only; their presence here does not claim implementation completeness.
8. Legacy implementation details must not redefine this architecture. Migration work must preserve the boundaries above.

## Status Semantics

Architecture and implementation status are intentionally separate:

- `Implemented` — verified in the current Engine-Rebuild implementation.
- `In Progress` — actively being implemented or validated.
- `Planned` — part of the architecture but not yet implemented.
- `Future` — explicitly reserved for a future platform/vendor capability.

The architecture tree above is the canonical structural definition; implementation status should be recorded separately so that an architectural component is never mistaken for completed code.
