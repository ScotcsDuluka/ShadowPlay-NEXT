# Duluka Capture

Duluka Capture is the capture and recording subsystem used by ShadowPlay-NEXT. This directory is organized by responsibility. Project and assembly identities are kept where other code may depend on them.

**Architecture invariant (ACCEPTED):** [Session-Centric Recording Contract](../../../../docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md) — one RecordingSession owns clock/audio/encoder/mux, selectable Desktop and Exclusive capture APIs feed its FrameRouter, and external OBS/NVIDIA engines keep their own sessions. Read this before changing CaptureSession, video-source selection or encoder factory. This is the target contract, not a statement that exclusive switching or Intel native QSV is already functional.

## Intel development evidence — 25 October 2026 (HUAWEI-MateBook only)

The local `intel/duluka-capture` worktree has reached **native QSV I2** and **synthetic D3D11-to-NV12 I3** testing, plus an **I3 borrowed D3D11 frame adapter** that was exercised with production-shaped frame wrappers. The full I3 synthetic transport report recorded **1,453 passed / 0 failed / 33 skipped**; the newer adapter/composition report recorded **1,725 passed / 0 failed / 33 skipped** across six successful stages, explicitly **excluding** 13 historical synthetic-audio mux checks from this new composition-only execution.

**The Intel live capture gate remains `intel_native_live_input_not_verified`**. Do not infer actual Desktop Duplication frames, real audio/mux sync, game/fullscreen transitions or release readiness from test-owned textures. I3's `BgraFrameAdapter` lives under `Video/Transport/DulukaEngine.Video.Transport.D3D11/`, but has **not** been wired into the default production Desktop factory. Native QSV still receives independently owned CPU NV12; there is no direct GPU texture import. Intel CBR remains rejected.

Read [MateBook NVIDIA Capture guide](../../README.md), [Intel QSV backend](Encoding/Intel/README.md), [D3D11 BGRA transport](Video/Transport/DulukaEngine.Video.Transport.D3D11/README.md), [I3 frame adapter evidence](../../../../docs/reports/intel-duluka-i3-composition-2026-10-25/README.md) and [I3 synthetic pipeline](../../../../docs/reports/intel-duluka-i3-2026-10-25/README.md). These links and statuses describe this local worktree, not the independently deployed AORUS runtime.

## Latest I3-B / I3-C synthetic status (25 October 2026)

- **I3-B lifecycle/session/BT.709 metadata:** [full result](../../../../docs/reports/intel-duluka-i3b-2026-10-25/README.md) **2,056 Passed / 0 Failed / 33 Skipped**; Intel QSV private ABI **3** requires matching native/managed build; the earlier test-suite compile failure was intermediate.
- **I3-C SourceCompositionDriver:** synthetic producer only; [Agent A report](../../../../docs/reports/intel-duluka-i3c-2026-10-25/A-COMPOSITION-REPORT.md) and [independent Agent C QA](../../../../docs/reports/intel-duluka-i3c-2026-10-25/C-INDEPENDENT-QA.md) report 1,935/0/0 and 1,938/0/0 respectively. Agent C recommends synthetic acceptance; Main sign-off not established. [B1 source-guard repair](../../../../docs/reports/intel-duluka-i3c-2026-10-25/B-WORKER-QUIESCENCE-REPAIR.md) remains insufficient for real DXGI driver-hang/COM safety; B2–B8 remain pending.
- **Production gate:** `intel_native_live_input_not_verified` remains closed. No actual desktop/game audio capture or AORUS runtime migration is implied.

## Layout

- API/ - API ownership and placeholder boundary; concrete contracts live under Video/Contracts/.
- Core/DulukaEngine/ - engine lifecycle and configuration foundation.
- Audio/DulukaEngine.Audio/ - audio session and model layer.
- Audio/DulukaEngine.Audio.Wasapi/ - Windows WASAPI capture implementation.
- Video/Contracts/DulukaEngine.Video/ - video backend/frame contracts and shared types.
- Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/ - native DXGI Output Duplication backend. The project file is DulukaEngine.Video.DXGI.vbproj; its AssemblyName remains DulukaEngine.Video.Ddagrab for runtime compatibility.
- Video/Windows Graphics Capture/WgcCapture/ - WGC source for the WebView2/Chromium Overlay surface; it is separate from the game-desktop DXGI backend.
- Video/Transport/DulukaEngine.Video.Transport.D3D11/ - staging BGRA-to-owned-NV12 converter and guarded borrowed-frame adapter. This is a **source/format seam**, not live acquisition; [I3 contract](Video/Transport/DulukaEngine.Video.Transport.D3D11/README.md).
- Encoding/DulukaEngine.Encoder/ - encoder abstraction and contracts.
- Encoding/NVENC/DulukaEngine.Encoder.Nvenc/ - NVIDIA NVENC implementation.
- Encoding/Intel/ - native Intel QSV H.264 + pinned dispatcher integration, with owned CPU NV12 inputs; [scope and limitations](Encoding/Intel/README.md).
- Recording/DulukaEngine.Recording/ - capture-session orchestration and recording contracts.
- Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/ - FFmpeg process, muxing, and audio/video stream handling.
- Tests/ - configuration, core, contracts, video, encoder, FFmpeg, and recording tests.
- Tools/DulukaEngine.Recording.ConsoleDriver/ - console harness for manual and integration recording checks.

## Notes

- Folder names describe responsibility; project file names follow their implementations.
- The DXGI project's assembly name remains DulukaEngine.Video.Ddagrab so existing runtime references are not silently renamed.
- Generated bin/ and obj/ contents were moved with their projects, not deleted.
- The solution and solution filter point to the reorganized project paths.