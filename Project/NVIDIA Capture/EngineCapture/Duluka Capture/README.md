# Duluka Capture

Duluka Capture is the capture and recording subsystem used by ShadowPlay-NEXT. This directory is organized by responsibility. Project and assembly identities are kept where other code may depend on them.

## Layout

- API/ - API ownership and placeholder boundary; concrete contracts live under Video/Contracts/.
- Core/DulukaEngine/ - engine lifecycle and configuration foundation.
- Audio/DulukaEngine.Audio/ - audio session and model layer.
- Audio/DulukaEngine.Audio.Wasapi/ - Windows WASAPI capture implementation.
- Video/Contracts/DulukaEngine.Video/ - video backend/frame contracts and shared types.
- Video/DXGI Desktop Duplication/DulukaEngine.Video.DXGI/ - native DXGI Output Duplication backend. The project file is DulukaEngine.Video.DXGI.vbproj; its AssemblyName remains DulukaEngine.Video.Ddagrab for runtime compatibility.
- Video/Windows Graphics Capture/WgcCapture/ - WGC source for the WebView2/Chromium Overlay surface; it is separate from the game-desktop DXGI backend.
- Encoding/DulukaEngine.Encoder/ - encoder abstraction and contracts.
- Encoding/NVENC/DulukaEngine.Encoder.Nvenc/ - NVIDIA NVENC implementation.
- Recording/DulukaEngine.Recording/ - capture-session orchestration and recording contracts.
- Recording/Backends/FFmpeg/DulukaEngine.FFmpegBackend/ - FFmpeg process, muxing, and audio/video stream handling.
- Tests/ - configuration, core, contracts, video, encoder, FFmpeg, and recording tests.
- Tools/DulukaEngine.Recording.ConsoleDriver/ - console harness for manual and integration recording checks.

## Notes

- Folder names describe responsibility; project file names follow their implementations.
- The DXGI project's assembly name remains DulukaEngine.Video.Ddagrab so existing runtime references are not silently renamed.
- Generated bin/ and obj/ contents were moved with their projects, not deleted.
- The solution and solution filter point to the reorganized project paths.