# Hook Implementation / Evidence / Runtime Truth — 25 Oct 2026

Project: C:/My Project/NVIDIA-Shadowplay
Machine inspected: AORUS-DESKTOP | Git branch Stable | HEAD (inspection): 529a0d31a8
Scope: OSC Adapter (.NET) and Duluka Hook (native), plus OSC Host transport needed to interpret Hook status

## Status vocabulary

- SOURCE: code exists, not necessarily built
- BUILD: the candidate compiled successfully
- SYNTHETIC PASS: isolated fixture exercised the actual DLL or publisher code; not proof of in-game frame rate
- LIVE VISUAL PASS: overlay image observed in a real game's window
- RUNTIME: installed binary verified by SHA256; may differ from current Source build
- UNVERIFIED: no sufficient current evidence; do not declare support or frame-rate success
- BLOCKED: a connected tool explicitly refused the deployment action; **do not bypass the block**

## Actual support and status (as observed)

| Feature | Source/tests | Runtime / evidence | Unresolved |
|---|---|---|---|
| OSC Adapter x86/x64 host | .NET 10 Host + native DLL routing | Cross-bitness test PASS | General safe unload not certified |
| D3D9 factory/import + late attach | Native DX9 Hook, MinHook fallback | Factory fallback deployed 24 Oct | Not all game factory patterns/guards verified |
| Classic D3D9 CPU frame composition | Native renderer + seqlock/mapping | Smoke/regression PASS | Stutter in actual user experience |
| Classic D3D9 GPU Windowed (DWM) | OSC Host DComp external GPU route, opt-in | **Floating Point LIVE VISUAL PASS** | P95/P99 game frametimes; monitor/move, input, fullscreen |
| Classic D3D9 Fullscreen Exclusive GPU Hook | No proven direct GPU sharing path | NOT CERTIFIED | Requires different GPU renderer design |
| D3D9Ex CPU | Native Hook/renderer | Smoke passes | Game-specific evidence still needed |
| D3D9Ex GPU mailbox | Host-source IPC, legacy GPU alias, stage/commit/ACK | x86/x64/x64→x86 synthetic regressions PASS | Live publisher/CEF integration/approved transport not certified |
| D3D11/DXGI | Native Hook/Shared GPU renderer | Native smoke and regression PASS | Intermittent smoke failure history and safe unload |
| D3D10/D3D12/Vulkan/OpenGL | Planned folders | NOT IMPLEMENTED | Do not advertise as supported |
| D3D9 empty CPU-frame early skip | Candidate Source only | Candidate x86 synthetic and full regression PASS | **NOT DEPLOYED**, no real-game A/B |

## Exact SHA-256 gates, captured 25 Oct 2026

### Current Source

| Path, relative to repo root | SHA-256 |
|---|---|
| Project/NVIDIA Adapter/OSC Adapter/Program.cs | 321E9F102D2BCED65CD29CDB4EB9DAA0EFACD8E5AAD1B8DB1B93B5EAE8529837 |
| Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D9/D3D9Hook.cpp | 22404F30B297499AAECEB956A8A82521DB9CD0D117ACD9B792138EC089273032 |
| Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Hook/D3D9/LateD3D9Detours.inc | 7752B3AB11B807600013F0FAD8876FAF0F635FC451906E746DC73597C7935528 |
| Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/src/Renderer/D3D9/D3D9Renderer.cpp | 01776D0454CE861D6A7118D65389E3C114808AAAC2BF40CED340374982192C66 |
| Project/Overlay On-Screen Control/NVIDIA OSC/src/osc_main.cpp | 1B94EDB0BABA9E14C4D8FB6549BA9DEDDCF0470533172A2CAAD5843D13F228B9 |
| Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/OscD3D11FramePublisher/OscD3D11FramePublisher.cpp | 0F413630B96BBC531DBD3B9FA35E99AF44E05447900A20DE5163875AA64EB56B |
| Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/Common/OscHookTransportPolicy.h | 223E15FF6712AEC69A33E82F85DC09A2538906D0F3DA6153C5E6FA373388999E |

These hashes are inspection fingerprints, NOT permission to overwrite modified files without checking fresh status.

### Installed binaries vs non-deployed candidate

| File | SHA-256 | Interpretation |
|---|---|---|
| Build/NVIDIA ShadowPlay/NVIDIA Adapter/NVIDIA OSC Adapter Hook.dll | BEE32E1F9B76785EA9C5E5B389C060C65CB1B7F7DD5C36BE34F0764401BF7CA4 | deployed x64 |
| Build/NVIDIA ShadowPlay/NVIDIA Adapter/CrossIntegrity/NVIDIA OSC Adapter Hook.dll | BEE32E1F9B76785EA9C5E5B389C060C65CB1B7F7DD5C36BE34F0764401BF7CA4 | deployed x64 |
| Build/NVIDIA ShadowPlay/NVIDIA Adapter/x86/NVIDIA OSC Adapter Hook.dll | D931EBF2D2C85AAF6C040E30FF47AA57D399556D38A40DD907147DFBD9747598 | deployed x86 |
| Build/NVIDIA ShadowPlay/NVIDIA Adapter/CrossIntegrity/x86/NVIDIA OSC Adapter Hook.dll | D931EBF2D2C85AAF6C040E30FF47AA57D399556D38A40DD907147DFBD9747598 | deployed x86 |
| Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/bin/x64/Release/NVIDIA OSC Adapter Hook.dll | F1964813BEBD5D46969C466ED731141A233D56C6C1B0714DB17EEB04AE9E8412 | **candidate only, not deployed** |
| Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/bin/Win32/Release/NVIDIA OSC Adapter Hook.dll | E9D4E730BAB4B523501C247BD0C8C64F738CA1AD08B5EF084671A233D920AF4C | **candidate only, not deployed** |
| Build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA OSC/NVIDIA OSC.exe | A2A778C40FD84987101637A43046F291FB718301E5BF42E9894AEFFF4C0EA136 | deployed native OSC Host |

Current storage setting as read: OSC.HookTransport.d3d9 = gpu. It is a **saved preference**, not per-frame proof of GPU execution. OSC itself uses a windowed target + DComp capability gate before activating; fullscreen fallback and other choices differ. OSC process count during inspection: 3.

### Status at preflight

- Floating Point processes: 0.
- Container OSC Adapter activeCount: 0.
- Repo branch Stable HEAD 529a0d31a8; Git worktree contains unrelated modifications from other workstreams. **Do not stash/reset/check out/delete/commit/push unrelated changes.**
- Previous attempt to stage/deploy the new early-skip Hook DLL was BLOCKED by tooling; no permission to evade. Stable runtime SHA remains unchanged.

## Actual regression evidence (historical; verify if Source has changed)

### Latest candidate: D3D9 empty-frame early skip

Evidence:
- Build/DulukaHook-D3D9-ZeroFrameShortCircuit-20261024/FULL-REGRESSION-attempt1.log
  SHA256 FC1B68C782FE8182130623BD005E367A0C638CFEF546A44FE845D84313C8A920
- Build/DulukaHook-D3D9-ZeroFrameShortCircuit-20261024/HANDOFF-20261024.md
- D3D9Renderer.cpp Source SHA as above

Results of the candidate test:
- Native Release Win32/x64: PASS.
- Synthetic classic D3D9 x86: 210 Present calls by fixture; Hook intercepted 200; confirmed zero-CPU-frame short circuit on 199; renderer draws 0; adapter LUID published. This validates **work removed**, not FPS improved.
- Full native tests/verify-adapter.ps1: PASS, exit 0. Last line: PASS: verified safe adapter checks. NOTHING DEPLOYED.
- D3D11 smoke has previous intermittent 0xC0000005 failure history, so one pass is not proof of indefinite stability.

### Previous factory fallback already deployed

Evidence:
- Build/DulukaHook-D3D9-FactoryFallback-20261024/REAL-D3D9-GPU-VALIDATION-20261024.md
- Build/DulukaHook-D3D9-FactoryFallback-20261024/Deploy-FactoryFallback.ps1 (SHA-gated rollback script; do not run blindly)
- A forced dummy-device failure fixture installed Direct3DCreate9[Ex] factory entry detours BEFORE a real device was created; captured 210/210 Present calls, LUID present; exit 0.
- Follow-up full native regression passed on second run. Earlier attempt had D3D11 intermittent smoke crash; existing and candidate DLL separately passed 3/3 quick replays.

### Real Floating Point Windowed image and GPU Present

Evidence:
- Build/DulukaHook-D3D9-FactoryFallback-20261024/FloatingPoint-GPU-Windowed-RealGame.png
- Build/DulukaHook-D3D9-FactoryFallback-20261024/REAL-D3D9-GPU-VALIDATION-20261024.md
- Game: Floating Point x86, Windowed 1024×768; Unity Configuration -> Play, foreground -> Alt+Z.
- OSC Native Host logged activation of DWM GPU compositor over the real D3D9 game client.
- Six one-second host windows on 24 Oct around 22:36:14–19: 389 successful OSC GPU Presents (52–72 per sampled second), 0 CPU Bridge publish, 0 busy Present drops, maximum sampled per-call GPU Present time 849 μs.
- This is **NOT** game FPS; user reports visible stutter regardless. Desktop Mode can feel smooth at high FPS but Hook Mode DX9 still not confirmed stable at 60. Do not use 389/6 (~64.8) as a claim of game or UI content FPS.
- Earlier OPEN/CLOSED transitions were **intentional user toggling during smoothness testing**; do NOT report them as duplicate-hotkey defect without independent reproducer.

## Known problem & risk inventory

1. DX9 60 FPS steady (game frame pacing and/or overlay animation) NOT verified. User observes stutter.
2. DirectComposition in Windowed is not a true Fullscreen Exclusive GPU Hook; do not over-generalize.
3. CPU -> GPU route may leave stale frames unless publisher advertises hide; deployed OSC host includes stale-frame retirement as documented.
4. D3D9 classic GPU LUID identification in hybrid systems may be ambiguous; don't match PCI vendor/device alone.
5. D3D9Ex mailbox receiver is experimental; no blanket production GPU flag.
6. Safe dynamic Detach/Unload is incomplete; never FreeLibrary under loader lock or while Present callbacks might run.
7. D3D11 Smoke known intermittent fail; preserve DXGI owner and regression tests.
8. WPR/WPA GPU ETW can collect a trace, but NVIDIA FrameViewSDK bundled old PresentMon_x64.exe failed to produce usable CSV in prior attempts. The game-free GPU-Telemetry-Preflight.etl is NOT a valid game frame-time report.
9. User may have changed Unity display preference while testing (original observed Fullscreen 1680×1050 vs later Windowed 1024×768). Do not change the user's live game settings without accounting for this.
10. Multiple chats are editing the broader repo. Avoid overwriting unrelated work.

## Canonical follow-ups

- Architecture: HOOK-ARCHITECTURE-2026-10-25.md
- Next DX9 performance validation: DX9-PERFORMANCE-VALIDATION-2026-10-25.md
- Codex work order: ../CODEX-HANDOFF.md
- ABI hard rules: ../HOOK-CONTRACT.md
