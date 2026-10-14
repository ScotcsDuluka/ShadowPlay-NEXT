[**OPEN SOURCE PROJECT**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#)

- [**OBT3 LIVE →**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/obt3.html)
- [**Features**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#features)
- [**Overlay**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#overlay)
- [**API Capture**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#api-specs)
- [**Requirements**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#requirements)
- [**Architecture**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#architecture)
- [**FAQ**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#faq)
- [**Team**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/#team)
- [**Discord Server**](https://discord.gg/v5qUGVD3jZ)

Please check the app information at https://scotcsduluka.github.io/NVIDIA-Shadowplay/ as this README may not always reflect the latest application status.

# NVIDIA ShadowPlay

> **Independent-project notice:** This repository is independently developed and is not affiliated with, endorsed by, sponsored by, or associated with NVIDIA Corporation. NVIDIA, ShadowPlay, and related marks belong to their respective owners. This project is not an official NVIDIA product.

> **Repository structure:** The repository currently contains both the legacy WinForms Overlay system and a newer CEF-based Overlay/platform codebase. The intended destination name for the newer project is **ShadowPlay-NEXT**. See [the repository rebuild plan](docs/REPOSITORY-REBUILD-PLAN.md). This notice documents the plan only; it does not mean source files have already been moved.

> [!NOTE]
> A custom screen capture utility inspired by NVIDIA ShadowPlay, built primarily in **<img src="https://raw.githubusercontent.com/github/explore/refs/heads/main/topics/visual-basic/visual-basic.png" alt="Visual Basic Logo" width="15"> VB.NET** with a **no-hook** capture design.<br>
> Recording and encoding are powered by **FFmpeg**, with modular capture and encoder components under active development.

> [!WARNING]
> This project is still under active development. Some applications and protected/DRM content may not be capturable, depending on the capture path and operating-system restrictions.

> [!IMPORTANT]
> **Runtime Dependency:** The current desktop application targets **.NET 10** and requires the **.NET 10 Desktop Runtime** when deployed as framework-dependent.<br><br>
> **OS Requirement:** Windows is required. Supported operating-system versions depend on the .NET 10 and Windows API requirements of the selected build. See the official .NET Windows support matrix for the current platform details.

## Capture Every Momen

### Zero latency. Zero compromise. Built for performance.

**Flagship Build — OBT3**
*Open Beta Test 3 — the audio-timeline rebuild is out now.*

[**✦ WHAT'S NEW — OBT3**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/obt3.html)
Side-by-side comparison and measured results are available on the OBT3 page.

**BUILD: OBT3 · SEP 2026**
**SDK 26100 · NVENC READY · A/V 0.000s**

---

## Overlay

Precision-engineered, borderless overlay that keeps you in control — without ever breaking immersion.

### In-game Overlay

- Borderless Windowed Design
- Real-time Performance Checked
- Keyboard Shortcut Access
- [View Supported Games](https://scotcsduluka.github.io/NVIDIA-Shadowplay/games.html)

![In-game Overlay UI](https://scotcsduluka.github.io/NVIDIA-Shadowplay/Assets/overlay.png)

---

# DOWNLOAD NVIDIA SHADOWPLAY

## NVIDIA ShadowPlay OBT3

**Open Beta Test 3 · Windows x64 bit**

**The current flagship build.**
Audio-timeline rebuild (no tail loss) · fixed video-only path · honest pass reporting · Intel QSV via FFmpeg path · WASAPI on one common QPC clock (A/V 0.000s).

[**Download Installer**](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/download/Release-OBT-3/NVIDIA.ShadowPlay.OBT3.exe)
[**See OBT3 vs Stable →**](https://scotcsduluka.github.io/NVIDIA-Shadowplay/obt3.html)

## NVIDIA ShadowPlay

**3.3.2057.1 · Windows x64 bit · App + Runtime + Resources**

[**Download Installer**](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/download/3.3.2057.1-BETA/NVIDIA.ShadowPlay.3.3.2057.1-BETA.exe)

This stable release of NVIDIA ShadowPlay is built for smooth and reliable everyday use, delivering high-performance recording with minimal impact on gameplay.

## NVIDIA ShadowPlay

**3.35.2542.30 · Windows x64 bit + Experimental · App + Runtime + Resources**

[**Download Installer**](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/download/3.35.2542.30-PRE/NVIDIA.ShadowPlay.3.35.2542.30-PRE.exe)

This experimental build provides early access to new features and ongoing improvements. While it offers the latest updates and faster iteration, it may include bugs and is recommended for advanced users who want to test upcoming changes.

---

## Features Overview

# Capture Your Best Moments

Professional-grade features inspired by NVIDIA ShadowPlay.

### Real-time Screen Recording

Record your gameplay or desktop in real time.
High-quality output with minimal performance impact, fully powered by NVIDIA NVENC hardware acceleration.

- Hardware-accelerated encoding
- Low CPU Usage
- Supports multiple formats

### Instant Replay

Automatically capture and save the last minutes of your gameplay with a single click. **Buffer engine is under active development** — not yet in the current build.

### Screensho

Snap crisp, high-resolution screenshots instantly. **Under active development** — not yet in the current build.

### No-Hook Architecture

Safe and secure by design. No memory injection, no hooks — just pure stability.

### NVIDIA Hardware Acceleration

Fully optimized with NVIDIA NVENC via FFmpeg. Fast, efficient, and reliable GPU-based encoding.
---

## Encoder & Capture

Modern capture APIs + hardware encoder status.

### Engine Regimes

| Engine Regime | Status | Description |
|---|---|---|
| **FFmpeg Capture** | Flexible | FFmpeg subprocess engine with multiple capture and encoder paths |
| **Duluka Capture** | **Production** | Native D3D11 + NVENC pipeline — the production engine behind the overlay |
| **OBS Capture** | **SOON** | Bridge / event forwarding |

### NVIDIA NVENC

**Ready**

Native in-app GPU encoding (production path): H.264 CBR/CFR, D3D11 texture submission, and explicit keyframe handling.

### Encoder Status

- [x] NVIDIA / NVENC ready
- [ ] Intel / Quick Sync
- [ ] AMD / AMF

### Intel QSV

**Not in this regime**

The native engine binds NVENC directly — QSV lives in the FFmpeg engine regime.

### AMD AMF

**Soon**

AMD Advanced Media Framework encoder on roadmap.

---

## API Capture

### DXGI Desktop Duplication

**Active**

The production capture path — D3D11 GPU frames with driver QPC presentation timestamps.

### Windows.Graphics.Capture

**Coming**

Slot prepared in the modular engine (`VideoBackendKind.GfxCapture`) — not the active production backend.

### Window / Region / Game

**Coming**

`d3d11_native`, `window_capture`, `region_capture` and `native_game_capture` remain on the roadmap.

Additionally supported capture options and planned backends:

- Windows.Graphics.Capture
- Desktop Duplication API
- GDI screen grabber

---

# Requirements

## System Requirements

### Operating System

**Windows 10 (19045+) / Windows 11**
64-bit — the application targets .NET 10. Older Windows versions including Windows 8/7/Vista/XP are not supported.

### .NET Desktop Runtime 10

The app targets .NET 10. Install the **.NET Desktop Runtime 10 (x64)** when it is not already included with the selected release package.

[**Download Runtime Package**](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases/download/REQUIRED/64bit.runtime.exe)

### Hardware

The native production engine requires an NVIDIA GPU with a supported NVENC encoder. The FFmpeg regime provides broader capture/encoder options where supported.
---

## Known Limitations

- **DRM Content:** Netflix/Disney+ blocked by Windows protection.
- **Fullscreen:** Exclusive mode not supported by the no-hook design.
- **Solution:** Use Borderless Windowed mode in games.

### Performance Optimization

For best results, use **NVIDIA hardware encoders** such as `h264_nvenc` through the FFmpeg path at **1920 × 1080 resolution** when appropriate for your hardware.

---

# Architecture

## Modular Architecture

Each module represents a distinct part of the current stack.

### CaptureEngine/

Core contracts and configuration: `IVideoBackend` / `IEncoderBackend` seams, configuration pipeline, and engine diagnostics. Producers plug into explicit backend contracts.

### Capture — Ddagrab/

Production capture: DXGI Desktop Duplication on D3D11, GPU-backed frames, and driver presentation timestamps in the QPC time domain.

### Encoder — NVENC/

Native in-app NVENC encoder: direct function-table binding rather than an FFmpeg encoder wrapper, with CBR/CFR configuration, first-frame keyframe handling, and D3D11 texture submission.

### Audio — WASAPI/

Shared audio path for system audio and microphone, aligned to the common QPC time domain with explicit queue and drop accounting.

### Live Mux — FFmpeg/

Named-pipe live muxer: H.264 + PCM stream into FFmpeg, fragmented MP4 during recording, faststart finalization, and controlled draining at stop.

### Overlay/

In-game borderless overlay with recording controls, keyboard shortcuts, settings, account UI, and gallery integration. The overlay does not own the native engine's GPU resources.

### Hub — NVIDIA API/

Local application hub coordinating the desktop application family and carrying engine command messages between components.
### Two Engine Regimes

The project intentionally keeps two distinct engine regimes:

```tex
Duluka Native Engine
    D3D11 Desktop Duplication
        ↓
    Native NVENC H.264
        ↓
    Audio / LiveMux orchestration

FFmpeg Engine Regime
    FFmpeg-managed capture backends
        ↓
    FFmpeg encoders / filters
        ↓
    Wider hardware support, including Intel QSV paths
```

The native Duluka engine is the production path. The FFmpeg regime provides broader capture and encoder combinations where the corresponding FFmpeg components are available and validated.

### Hub — NVIDIA API

TCP/local hub used to coordinate the desktop application family and carry engine commands between components.

---

## Development

| Branch | Purpose |
|--------|---------|
| **`Stable`** | Default branch — last known-good build |
| **`Engine-Rebuild-Stabilization`** | Active development — modular capture engine, lifecycle hardening, sync/audio stabilization, and account integration |

The project is being developed as a modular capture stack rather than a direct copy of NVIDIA's internal implementation.

Engine documentation lives in [`docs/`](docs/), starting with [`docs/PHASE_PLAN.md`](docs/PHASE_PLAN.md). The module map is documented in [`docs/reports/PROJECT-STRUCTURE.md`](docs/reports/PROJECT-STRUCTURE.md), with build and diagnostic entry points under [`scripts/`](scripts/).

### Current Engineering Focus
- Capture backend lifecycle and ownership
- Video frame delivery and timestamp correctness
- NVENC configuration authority and synchronization
- FFmpeg recording/mux integration
- Duluka Account authentication and identity semantics
- Regression and integration testing

---

## License

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

| File | Description | Click to View |
|---------|-------------|------------------|
| **[LICENSE](LICENSE)** | MIT License | [Open LICENSE](LICENSE) |
| **[LICENSE.NOTICE](LICENSE.NOTICE)** | Third-Party Components Attribution | [Open LICENSE.NOTICE](LICENSE.NOTICE) |

---

## Disclaimer

> [!CAUTION]
> **Trademark Notice:** This is an **independent third-party application** and is **NOT affiliated with, endorsed by, sponsored by, or approved by NVIDIA Corporation**.
>
> **NVIDIA**, **GeForce**, and **ShadowPlay** are trademarks or registered trademarks of NVIDIA Corporation in the United States and/or other countries.
>
> This project uses third-party and platform technologies such as FFmpeg, NVIDIA NVENC, and Microsoft Windows capture APIs according to their respective licensing and platform terms. This project does not imply endorsement by NVIDIA.

---

# Contributing

1. Fork the project.
2. Create your feature branch.
3. Make focused changes.
4. Run the relevant tests and builds.
5. Push your branch.
6. Open a Pull Request.

| Component | License | Author | Source |
|-----------|---------|--------|--------|
| [NAudio](https://github.com/naudio/NAudio) | MIT | Mark Heath | NAudio.Core.dll, NAudio.Wasapi.dll |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) | MIT | James Newton-King | Newtonsoft.Json.dll |
| [libmp3lame](https://lame.sourceforge.io/) | LGPL-2.0 | The LAME Project | libmp3lame.32.dll, libmp3lame.64.dll |
| [FFmpeg](https://ffmpeg.org/) | LGPL/GPL | FFmpeg Developers | Recording / Encoding pipeline |
| [.NET](https://dotnet.microsoft.com/) | MIT | Microsoft | .NET runtime / desktop components |
| [Windows SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/) | Microsoft licenses | Microsoft | Windows API / WinRT components |

See the full attribution list in **[LICENSE.NOTICE](LICENSE.NOTICE)**.

---

# Contact & Links

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

For substantial engine or architecture changes, please review the documentation under [`docs/`](docs/) first.

---

## Credits

| Role | Name | Description |
|------|------|-------------|
| **Creator & Lead Developer** | [ScotcsDuluka](https://github.com/ScotcsDuluka) | Architecture, UX Design, Core Engine, Overlay System, Animation Framework |
| **Tester & QA** | [ApiwitKaemanee](https://www.facebook.com/profile.php?id=61577847980691) | Testing, Validation, Stability Assurance |

---

## Contact & Links

| Resource | Link |
|---|---|
| **Project Website** | [NVIDIA ShadowPlay](https://scotcsduluka.github.io/NVIDIA-Shadowplay/) |
| **OBT3** | [Live OBT3 page](https://scotcsduluka.github.io/NVIDIA-Shadowplay/obt3.html) |
| **Repository** | [GitHub](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay) |
| **Releases** | [GitHub Releases](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases) |
| **Issues** | [GitHub Issues](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/issues) |
| **Creator** | [ScotcsDuluka](https://github.com/ScotcsDuluka) |
| **Releases** | [GitHub Releases](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/releases) |
| **Issues** | [GitHub Issues](https://github.com/ScotcsDuluka/NVIDIA-Shadowplay/issues) |
| **Creator** | [ScotcsDuluka](https://github.com/ScotcsDuluka) |
| **Discord** | [Discord Server](https://discord.gg/v5qUGVD3jZ) |

---

*Crafted with passion, engineered for performance, and continuously evolving.* ❤️
