# Duluka Hook — Source, Docs และงานสำหรับ Codex

**ShadowPlay NEXT / NVIDIA OSC Adapter Hook Engine (native C++)**  
อัปเดตเอกสาร: 25 ตุลาคม 2026 | โครงสร้างยืนยันจาก Source บน AORUS-DESKTOP

## เริ่มตรงนี้

1. **[CODEX-HANDOFF.md](CODEX-HANDOFF.md)** — งานที่ต้องทำต่อ, guardrails, ลำดับขั้น, วิธีทดสอบ และข้อความส่ง Codex พร้อมใช้
2. **[ผังระบบ Hook ทั้งหมด](docs/HOOK-ARCHITECTURE-2026-10-25.md)** — C# Host, x86/x64, API Hook, Renderer, ReShade, CPU/GPU transports, ABI, runtime folders
3. **[สถานะและหลักฐานจริง](docs/HOOK-STATUS-EVIDENCE-2026-10-25.md)** — SOURCE vs BUILD vs DEPLOY, SHA-256, Native regression, Floating Point live proof, บั๊กที่ยังเปิดอยู่
4. **[แผนแก้ D3D9 กระตุก](docs/DX9-PERFORMANCE-VALIDATION-2026-10-25.md)** — frame pacing, p50/p95/p99, 1% low, ตัวนับ Hook, แผน A/B, เกณฑ์ PASS
5. [ARCHITECTURE.md](ARCHITECTURE.md) และ [HOOK-CONTRACT.md](HOOK-CONTRACT.md) — สัญญาการออกแบบ/ABI เดิมที่ต้องเคารพ
6. [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md) — ประวัติการพัฒนาที่อาจมีสถานะเก่ากว่า; ใช้คู่กับ Evidence doc ใหม่

## TL;DR — Current Hook State

| API/route | สถานะ |
|---|---|
| D3D9 CPU Native Hook/Renderer | Implemented / Regression PASS |
| D3D9 external Windowed GPU via OSC DComp | Live image PASS ใน Floating Point x86; FPS ยังไม่รับรอง |
| D3D9Ex CPU | Implemented |
| D3D9Ex GPU legacy mailbox | Experimental / synthetic Host-source IPC PASS; production GPU mode disabled |
| D3D11 GPU/DXGI Hook | Implemented / Native regression PASS |
| D3D10, D3D12, Vulkan, OpenGL | NOT implemented |
| D3D9 empty-frame COM-call short circuit | Source Candidate built/tested; **NOT DEPLOYED** |
| D3D9 Hook Mode stable 60 FPS | **NOT VERIFIED; user reports stutter** |

**Do not claim improvement until real game frame pacing has been measured.** Desktop Mode may be smooth at 240 FPS while D3D9 Hook Mode remains below a consistent 60 FPS experience.

## Main code locations

- src/Hook/D3D9/D3D9Hook.cpp
- src/Hook/D3D9/LateD3D9Detours.inc
- src/Renderer/D3D9/D3D9Renderer.cpp
- src/Hook/D3D11/D3D11Hook.cpp
- src/Renderer/D3D11/D3D11Renderer.cpp
- include/osc_frame_bridge.h
- include/d3d9_timing_diagnostics.h
- include/d3d9_pipeline_timing_diagnostics.h
- tests/verify-adapter.ps1

Cross-module context (read-only unless explicitly assigned):
- ../Program.cs — OSC Adapter Host
- ../src/Detection/RenderApiPolicy.cs — API routing
- ../../../Overlay On-Screen Control/NVIDIA OSC/src/osc_main.cpp — CEF/DComp route (navigate from repository root to avoid relative-path ambiguity)
- ../../../Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/Common/OscHookTransportPolicy.h — GPU/CPU mode policy

## Source-only verification (does not deploy)

~~~powershell
$hook = 'C:\My Project\NVIDIA-Shadowplay\Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
& (Join-Path $hook 'tests\verify-adapter.ps1')
~~~

Read the script and current process state before executing tests. Its most recently recorded full run passed with exit 0, but that does not prove smoothness in the game.

## SAFETY

No DLL, game process, OS graphics configuration, ReShade installation or Container runtime is changed by these documentation files. A previous attempt to deploy the candidate Hook was explicitly blocked; do **not** bypass that guard. Codex should start with reproducible measurement and a source-only review.
