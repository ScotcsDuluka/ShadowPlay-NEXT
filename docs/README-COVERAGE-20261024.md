# README coverage — ShadowPlay NEXT

**Inventory originally generated 24 October and rechecked 25 October 2026** from tracked and untracked first-party README-like Markdown paths under this repository. Scope excludes generated builds, vendored dependencies and archives. Being listed means the README was inspected for navigation/coverage; **not** that every historical statement is current or all tests were rerun.

## Re-audit — 25 October 2026

- **65 first-party README-like Markdown files** inventoried (including the new [Duluka Hook guide](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/README.md)); archive/vendor/generated Build paths excluded.
- **277 local relative Markdown links** checked: **0 missing targets**. GitHub anchors, remote links and external availability are excluded from the local filesystem-only verification.
- **0 unmatched triple-backtick fences**, **65/65 indexed README entries**.
- Focused source-only regression on **4 test files: 50/50 PASS** (Launcher Quick UI, Capture Pipeline Design, Video Capture Settings, Hook Transport Settings). This is not a native CEF visual test, in-game Hook FPS measurement or completed recording.
- The 25 October project update records real D3D9 Windowed GPU visual proof, but game frame pacing and safe unload remain unverified. The source-only D3D9 fast-skip candidate and bounded CEF 73 patch are **not deployed**; the latter lacks a compatible build toolchain.
- [Capture UI v77 report](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/mods/CAPTURE-UI-REDESIGN-20261025.md) documents partial live Advanced CEF geometry and source/build alignment, **not** an encoder or full Video Capture CEF acceptance.
- **No code, runtime or media writes** were performed by this documentation sync. No commit/push or running-service restart.

## Latest full-inventory verification (24 October 2026)

- **64 first-party README-like Markdown files** indexed (including this file); generated/vendor folders excluded.
- **217 local relative Markdown links** checked: **0 missing targets**. Remote URLs and GitHub anchor rendering are not covered by this filesystem-only check.
- **0 unbalanced triple-backtick code fences** across the indexed files.
- `git diff --check -- '*.md'` passed; the existing Working Tree also contains unrelated uncommitted source work and was **not committed/pushed** by this documentation update.
- Focused source-only regressions rerun: **33/33 passed** across Launcher Account Portal/Quick UI, ReShade Manager, Capture Pipeline Design and Hook Transport Settings. These tests are not live CEF/game or remote authentication acceptance.
- Current shared Account Portal supports existing-account use; **public account signup and general CBT1 applications remain closed; invite-only application remains separately gated for existing users**. The proposed Server-backed Launcher **Core Module Catalog** is a different API and is not connected.

## How to interpret the catalog

- **Current/module guide:** consult alongside the latest source owner and runtime evidence.
- **Developer/testing note:** scoped tests and tools; do not extrapolate to release readiness.
- **Historical/spike:** dated investigation, archived system map or older design.
- **API note/placeholder:** narrow Hook/Renderer component notes, including unsupported APIs.

| README | Classification | First heading |
|---|---|---|
| [docs/README-COVERAGE-20261024.md](../docs/README-COVERAGE-20261024.md) | Current/module guide | README coverage — ShadowPlay NEXT |
| [Project/NVIDIA Container/NVIDIA ShadowPlay Helper/README.md](../Project/NVIDIA%20Container/NVIDIA%20ShadowPlay%20Helper/README.md) | Current/module guide | NVIDIA ShadowPlay Helper — hotkey / input service |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/README.md) | Current/module guide | OSC web UI — source assets and mods |
| [devtools/osc-live/README.md](../devtools/osc-live/README.md) | Developer/testing note | ShadowPlay NEXT — V4 Live UI Updater |
| [docs/osc/README.md](../docs/osc/README.md) | Historical/spike | OSC REVERSE MAP — the GFE "OSC" overlay package (Overlay/osc) |
| [docs/osc/unpacked/README.md](../docs/osc/unpacked/README.md) | Historical/spike | Unpacked legacy OSC bundles |
| [docs/osc/unpacked/src/README.md](../docs/osc/unpacked/src/README.md) | Historical/spike | Source-like reconstruction |
| [docs/osc/v1-knowledge/README.md](../docs/osc/v1-knowledge/README.md) | Historical/spike | OSC v1-knowledge — knowledge synced from A:\ShadowPlay\ShadowPlay_V1 |
| [docs/README.md](../docs/README.md) | Current/module guide | ShadowPlay NEXT — documentation index |
| [Project/FFmpeg/README.md](../Project/FFmpeg/README.md) | Current/module guide | FFmpeg — integration location |
| [Project/Launcher.Cef/Modules/README.md](../Project/Launcher.Cef/Modules/README.md) | Historical/spike | ShadowPlay NEXT — Appearance Modules v1 (.spmod); current Core UI is read-only |
| [Project/Launcher.Cef/README.md](../Project/Launcher.Cef/README.md) | Current/module guide | Launcher.Cef — CEF launcher lane (root Launcher.exe + NVIDIA Launcher/Cef/Launcher... |
| [Project/NVIDIA Adapter/Ansel Adapter/README.md](../Project/NVIDIA%20Adapter/Ansel%20Adapter/README.md) | Current/module guide | NVIDIA Ansel Adapter |
| [Project/NVIDIA Adapter/Ansel Adapter/ReShade Ansel/README.md](../Project/NVIDIA%20Adapter/Ansel%20Adapter/ReShade%20Ansel/README.md) | Current/module guide | Ansel Adapter — live Game Filter |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/README.md) | Current/module guide | Duluka Hook: source, runtime evidence and Codex handoff (25 Oct 2026) |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Common/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Common/README.md) | API note/placeholder | Shared native infrastructure |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Detection/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Detection/README.md) | API note/placeholder | Graphics API Detection |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D10/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/D3D10/README.md) | API note/placeholder | D3D10 Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D11/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/D3D11/README.md) | API note/placeholder | D3D11 Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D12/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/D3D12/README.md) | API note/placeholder | D3D12 Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D9/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/D3D9/README.md) | API note/placeholder | D3D9 Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/DXGI/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/DXGI/README.md) | API note/placeholder | DXGI presentation boundary |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/OpenGL/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/OpenGL/README.md) | API note/placeholder | OpenGL Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/README.md) | API note/placeholder | Hook layer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/Vulkan/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Hook/Vulkan/README.md) | API note/placeholder | Vulkan Hook |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/D3D10/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/D3D10/README.md) | API note/placeholder | D3D10 Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/D3D11/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/D3D11/README.md) | API note/placeholder | D3D11 Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/D3D12/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/D3D12/README.md) | API note/placeholder | D3D12 Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/D3D9/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/D3D9/README.md) | API note/placeholder | D3D9 Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/OpenGL/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/OpenGL/README.md) | API note/placeholder | OpenGL Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/README.md) | API note/placeholder | Renderer layer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/Vulkan/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/src/Renderer/Vulkan/README.md) | API note/placeholder | Vulkan Renderer |
| [Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/tests/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/Duluka%20Hook/tests/README.md) | Developer/testing note | Native Hook tests |
| [Project/NVIDIA Adapter/OSC Adapter/README.md](../Project/NVIDIA%20Adapter/OSC%20Adapter/README.md) | Current/module guide | OSC Adapter — Host / Controller |
| [Project/NVIDIA Adapter/README.md](../Project/NVIDIA%20Adapter/README.md) | Current/module guide | NVIDIA Adapter — game-facing adapters |
| [Project/NVIDIA Capture/EngineCapture/Duluka Capture/README.md](../Project/NVIDIA%20Capture/EngineCapture/Duluka%20Capture/README.md) | Current/module guide | Duluka Capture |
| [Project/NVIDIA Capture/EngineCapture/OBS Capture/README.md](../Project/NVIDIA%20Capture/EngineCapture/OBS%20Capture/README.md) | Current/module guide | OBS Capture (reserved bridge) |
| [Project/NVIDIA Capture/README.md](../Project/NVIDIA%20Capture/README.md) | Current/module guide | NVIDIA Capture — engine and media ownership |
| [Project/NVIDIA Capture/README-REST-INTEGRATION.md](../Project/NVIDIA%20Capture/README-REST-INTEGRATION.md) | Historical/spike | Engine REST integration — NVIDIA Capture.exe ต่อ REST :59001 (replaces TCP :5001) |
| [Project/NVIDIA Config/README.md](../Project/NVIDIA%20Config/README.md) | Current/module guide | NVIDIA Config — runtime policy inputs |
| [Project/NVIDIA Container/Components/README.md](../Project/NVIDIA%20Container/Components/README.md) | Current/module guide | NvContainer Components |
| [Project/NVIDIA Container/README.md](../Project/NVIDIA%20Container/README.md) | Current/module guide | NVIDIA Container — orchestration, not a capture engine |
| [Project/NVIDIA Graphics/README.md](../Project/NVIDIA%20Graphics/README.md) | Current/module guide | NVIDIA Graphics — per-application Windows GPU preferences |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/NVIDIA Web Helper/Backend/data/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/NVIDIA%20Web%20Helper/Backend/data/README.md) | Current/module guide | Backend/data — runtime state (generated, do not hand-edit) |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/NVIDIA Web Helper/Backend/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/NVIDIA%20Web%20Helper/Backend/README.md) | Current/module guide | Backend — NVIDIA ShadowPlay Portable (clean-room Web Helper replacement) |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/NVIDIA Web Helper/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/NVIDIA%20Web%20Helper/README.md) | Current/module guide | Project NodeAPI — NVIDIA Web Helper host |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/notifier/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/osc/mods/notifier/README.md) | Current/module guide | OSC notifier map |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/README.md) | Current/module guide | NVIDIA NodeAPI — local service and OSC web assets |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/README-NEXT-LAYOUT-V5.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/README-NEXT-LAYOUT-V5.md) | Historical/spike | ShadowPlay NEXT — Layout V5 implementation contract |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/README-NEXT-V5-NATIVE.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/README-NEXT-V5-NATIVE.md) | Historical/spike | ShadowPlay NEXT Layout V5 — Native Adapter Phase 2 |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/README-OSC-LIVE-PREVIEW.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/README-OSC-LIVE-PREVIEW.md) | Developer/testing note | OSC Live Preview: Project Stable to Build Runtime |
| [Project/Overlay On-Screen Control/NVIDIA NodeAPI/tools/README-OSC-PREVIEW.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20NodeAPI/tools/README-OSC-PREVIEW.md) | Developer/testing note | OSC Source & Live Preview Contract — Stable |
| [Project/Overlay On-Screen Control/NVIDIA OSC/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/README.md) | Current/module guide | NVIDIA OSC — Native UI / Desktop Overlay Host |
| [Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/OscD3D10FramePublisher/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/src/OscFramePublisher/OscD3D10FramePublisher/README.md) | API note/placeholder | D3D10 Frame Publisher â€” reserved |
| [Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/OscD3D11FramePublisher/README.md](../Project/Overlay%20On-Screen%20Control/NVIDIA%20OSC/src/OscFramePublisher/OscD3D11FramePublisher/README.md) | Current/module guide | D3D11 Frame Publisher |
| [Project/Overlay On-Screen Control/README.md](../Project/Overlay%20On-Screen%20Control/README.md) | Current/module guide | Overlay On-Screen Control — OSC subsystem |
| [README.md](../README.md) | Current/module guide | SHADOWPLAY NEXT |
| [README.th.md](../README.th.md) | Current/module guide | SHADOWPLAY NEXT |
| [Server/README.md](../Server/README.md) | Current/module guide | Duluka Account — v0 auth/security slice |
| [Tester/Spikes/D3D11_NVENC_Spike/README.md](../Tester/Spikes/D3D11_NVENC_Spike/README.md) | Historical/spike | CaptureEngine.Video.Spike.D3D11 — D3D11/NVENC Interop Spike |
| [Tester/Spikes/V3_WGC_Runtime_Timestamp_Spike/README.md](../Tester/Spikes/V3_WGC_Runtime_Timestamp_Spike/README.md) | Historical/spike | V3 WGC Runtime Timestamp Capture Spike |
| [Tester/Spikes/V4_WGC_Timestamp_EdgeCase_Spike/README.md](../Tester/Spikes/V4_WGC_Timestamp_EdgeCase_Spike/README.md) | Historical/spike | V4 WGC Timestamp Edge-Case Spike |
| [Tester/Spikes/V5_WASAPI_Position_Spike/README.md](../Tester/Spikes/V5_WASAPI_Position_Spike/README.md) | Historical/spike | V5 WASAPI Position Spike (PHASE 13.1) |
| [Tester/test/scripts/apphost_test/README.md](../Tester/test/scripts/apphost_test/README.md) | Historical/spike | apphost_test — PROOF of the Application\ -> ..\Services\ host mechanism |
| [tools/duluka-account/README.md](../tools/duluka-account/README.md) | Current/module guide | Duluka Account — mod tools (in-place, reversible) |

## Follow-up source review (later 24 October 2026)

The inventory of **64 first-party README-like files** was rescanned. Key corrections to main guides reflect new facts in source/detailed owner reports:

- **Local ApplicationOntology catalog v1.2.9 / 23 entries**; public version is unverified and must not be inferred from local data.
- **CBT1 invitation-only acceptance** for existing Duluka Accounts is separate from closed general registration (`Cbt1:Enabled=false`) and from any public NEXT installer. A user needs a privately issued single-use invite; admin reviews applications manually.
- **ReShade Manager** uses a fail-closed verified guided Setup contract, read-only game-folder inspection, and the official vendor handoff. No native installation is performed, and actual CEF clicks are not certified.
- **Hook transport policy:** D3D9 GPU is Windowed DWM/DirectComposition preview only; D3D9Ex GPU is not offered by current production selector despite experimental GPU mailbox regression. The Capture Pipeline Design is UI-only/non-persisted.
- **Launcher Core** remains a read-only suite-managed 11-card UI with source-labelled bundled catalog; the newer Web Github-inspired visual styling has deployed asset/hash evidence, **not** a confirmed pixel screenshot. Server metadata is still not connected.

## 24 October editorial corrections

- Current Launcher Core page: **11 built-in read-only components**; visible Add-ons is empty. Server-owned metadata API is **proposed but not verified/connected**. Old .spmod appearance tooling is not a current editable Core control.
- D3D9 automatic late-attach negative tests in old README sections predate the scoped MinHook fallback. Preserve dated cases; DX9Ex real-Host cross-process GPU tests do not guarantee real-game FPS or full CEF integration.
- Separate local ApplicationOntology catalog: **v1.2.9 / 23 entries** observed in this inspection. Public publication is not automatically proven.
- Container routes are not NvCapture media engines. Source routes, mock passes, deployed worker contracts, saved media and public-release readiness are different milestones.

**README-like files inventoried: 65 as of 25 October 2026.** Recheck local relative links after documentation changes. Do not push unrelated working-tree changes.
