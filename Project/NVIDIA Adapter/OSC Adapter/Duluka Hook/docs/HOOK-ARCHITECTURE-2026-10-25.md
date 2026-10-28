# ShadowPlay NEXT / Duluka Hook — ผังสถาปัตยกรรมที่ตรวจจาก Source
อัปเดต: 25 ตุลาคม 2026 | สาขา Stable | โฟกัส: Hook / OSC Adapter เท่านั้น

> ข้อมูลนี้อ้างอิง Source ที่มีในเครื่อง AORUS-DESKTOP ณ เวลาตรวจ ไม่ใช่คำรับรองว่า GPU Hook รองรับทุกเกมหรือทุกโหมด จงเทียบ HASH/ผลทดสอบใน HOOK-STATUS-EVIDENCE-2026-10-25.md ก่อนแก้หรือ Deploy

## 1) ขอบเขตและความรับผิดชอบ

โฟลเดอร์หลัก: C:/My Project/NVIDIA-Shadowplay/Project/NVIDIA Adapter/OSC Adapter/

- OSC Adapter Host (.NET 10; Program.cs): ตรวจ PID, executable identity, target architecture, API evidence, policy, แยก x64/x86 และดูแล Native DLL attachment/lifecycle
- Duluka Hook (C++ DLL): ติดตั้ง interception, Present/Reset callbacks, คืนค่า Original API, ดูแล renderer resources, ReShade arbitration และ read-only diagnostics
- OSC Native Host (อยู่อีกโมดูล): จัดหาเฟรม CEF, publisher, CPU bridge และ GPU compositor ภายนอก; เป็น producer ไม่ใช่ตัวแทน GPU hook ในเกม
- NVIDIA Container: ตัวสั่งงาน Auto-Hook / GameWatcher (อยู่นอกขอบเขตเอกสารนี้) ส่ง game identity และ render-api hint ให้ Adapter Host

### ผังควบคุม/โหลดโมดูล

~~~mermaid
flowchart TD
    GW["GameWatcher / Container<br/>game PID, image path, API hint"] --> AH["OSC Adapter Host (.NET 10)<br/>verify PID + EXE path / graphics modules"]
    AH --> RP{"RenderApiPolicy<br/>auto | d3d9 | d3d9ex | d3d11"}
    RP --> AR{"Target machine architecture"}
    AR -->|"x86"| H32["x86 Adapter Host<br/>x86 Hook DLL"]
    AR -->|"x64"| H64["x64 Adapter Host<br/>x64 Hook DLL"]
    H32 --> GH["Target Game Process"]
    H64 --> GH
    GH --> D9["D3D9 / D3D9Ex Hook<br/>Present, PresentEx, Reset, ResetEx"]
    GH --> D11["DXGI / D3D11 Hook<br/>Present / ReShade"]
    D9 --> R9["D3D9 Renderer"]
    D11 --> R11["D3D11 Renderer"]
    R9 --> FB["CPU Frame D3D9.v1<br/>and Adapter Identity v2"]
    R11 --> GS["D3D11 shared GPU resources"]
~~~

ข้อสังเกต: Detection พบ d3d9.dll ไม่ใช่หลักฐานว่าเกม Present ผ่าน D3D9Ex หรือว่ามีภาพ Overlay; ต้องอาศัย device/adapter evidence และการวาดจริง

## 2) Source tree ที่ยืนยันในเครื่อง

~~~text
OSC Adapter/
  Program.cs
  NVIDIA OSC Adapter Host.csproj
  src/Detection/
    GraphicsApiProbe.cs
    RenderApiPolicy.cs
    RemoteLoaderWaitPolicy.cs
  tests/
    RenderApiPolicyTests.cs
    RemoteLoaderWaitPolicyTests.cs
    verify-dx9-routing.ps1

  Duluka Hook/
    NVIDIA OSC Adapter.vcxproj
    ARCHITECTURE.md
    HOOK-CONTRACT.md
    IMPLEMENTATION-STATUS.md
    CODEX-HANDOFF.md                   [เอกสารส่งต่องานใหม่]
    README.md                          [ดัชนีใหม่]
    docs/
      HOOK-ARCHITECTURE-2026-10-25.md [ไฟล์นี้]
      HOOK-STATUS-EVIDENCE-2026-10-25.md
      DX9-PERFORMANCE-VALIDATION-2026-10-25.md
    src/
      Common/README.md
      Detection/README.md
      Hook/
        D3D9/
          D3D9Hook.cpp
          LateD3D9Detours.inc
          D3D9ExGpuMailbox.cpp
        D3D11/D3D11Hook.cpp
        D3D10/README.md
        D3D12/README.md
        DXGI/README.md
        OpenGL/README.md
        Vulkan/README.md
      Renderer/
        D3D9/D3D9Renderer.cpp
        D3D9/D3D9Renderer.h
        D3D11/D3D11Renderer.cpp
        D3D11/D3D11Renderer.h
        D3D11/D3D11SharedTextureRelay.cpp
        D3D11/D3D11SharedTextureRelay.h
        D3D10/README.md
        D3D12/README.md
        Vulkan/README.md
        OpenGL/OpenGLRenderer.h
    include/
      osc_frame_bridge.h
      d3d9ex_gpu_mailbox.h
      d3d9_timing_diagnostics.h
      d3d9_pipeline_timing_diagnostics.h
      d3d11_timing_diagnostics.h
      reshade-api-v3/
    third_party/minhook/
    tests/
      d3d9_smoke_test.cpp
      d3d9_late_attach_test.cpp
      d3d9ex_gpu_interop_test.cpp
      d3d9ex_mailbox_ipc_test.cpp
      smoke_test.cpp
      verify-adapter.ps1
      verify-dx9-cross-bitness.ps1
      verify-host-probe.ps1
~~~

OpenGLRenderer.h เป็น header ในโฟลเดอร์ แต่ยังไม่มี native OpenGL composition implementation ที่ยืนยันได้; ไม่มีสิทธิ์นับว่า OpenGL รองรับแล้ว

## 3) D3D9 / D3D9Ex ภายในโปรเซสเกม

~~~mermaid
flowchart TD
    G["Game: Direct3DCreate9 / Direct3DCreate9Ex"] --> F["Factory / device hook installation<br/>D3D9Hook.cpp + LateD3D9Detours.inc"]
    F --> P["HookPresent / HookPresentEx"]
    F --> RS["HookReset / HookResetEx"]
    P --> J{"ReShade owns composition?"}
    J -->|"Yes"| RE["ReShade present event -> D3D9 Renderer"]
    J -->|"No"| R["D3D9 Renderer OnPresent"]
    R --> ID["PublishConsumerAdapter<br/>real game LUID + renderApi=9"]
    ID --> C{"D3D9Ex GPU frame bound?"}
    C -->|"Yes"| EX["D3D9Ex legacy shared GPU texture"]
    C -->|"No"| CP{"D3D9 CPU frame mapping<br/>valid / visible / new?"}
    CP -->|"Yes"| UP["Snapshot / texture upload / blend"]
    CP -->|"No"| SK["No overlay draw<br/>preserve original game frame"]
    EX --> REST["Restore API state / original Present"]
    UP --> REST
    SK --> REST
    RS --> RESET["Invalidate / recreate device resources"]
~~~

D3D9Hook.cpp owns interception / import and factory handling; LateD3D9Detours.inc owns automatic late-attach code detours / new factory-entry fallback for dummy-device failure. Do NOT patch a disposable device's heap vtable to claim interception of preexisting devices.

D3D9Renderer.cpp owns adapter identity publication, state block and render-target preservation, shared CPU frame snapshot, alpha/pixel orientation, D3D9 texture upload, D3D9Ex GPU alias binding, reset/recovery and in-process diagnostics. The experimental early no-frame short-circuit exists **only in the Source Candidate** as of this report; it has not been deployed. See status document.

## 4) Frame transport — THREE distinct routes

~~~mermaid
flowchart LR
    CEF["OSC CEF accelerated GPU frame"] --> HOST["OSC Native Host / Publisher"]
    HOST --> D11["D3D11 GPU shared path"]
    D11 --> DR11["D3D11 Hook renderer<br/>in-game"]
    HOST --> CPU["D3D9.v1 CPU shared memory<br/>seqlock + pixels"]
    CPU --> DR9["Classic D3D9 Hook renderer<br/>in-game backbuffer"]
    HOST --> EX["D3D9Ex GPU mailbox<br/>generation / ACK / legacy handle"]
    EX --> DREX["D3D9Ex Hook renderer<br/>experimental"]
    CEF --> DCOMP["D3D11 + DirectComposition<br/>external OSC HWND"]
    DCOMP --> DWM["Windows DWM"]
    GAME["Windowed D3D9 game HWND"] --> DWM
~~~

สำคัญ: Classic D3D9 GPU Windowed ไม่ใช่การ Copy ภาพผ่าน D3D9 Hook backbuffer แต่เป็นหน้าต่าง OSC GPU composited โดย DWM; ฟีเจอร์นี้พิสูจน์ภาพจริงบน Floating Point Windowed แล้ว ไม่ใช่ Fullscreen Exclusive.

- D3D9 CPU bridge: OSC Host -> mapping Local\NVIDIAOSC.Frame.D3D9.v1 -> in-game renderer -> D3D9 texture upload -> blend; มีทาง fallback
- D3D9Ex GPU mailbox: legacy shared GPU BGRA8 + frame generation, Stage/Commit/Release ACK, LUID, producer liveness; ผ่าน synthetic cross-process/Host-source tests; **ยังไม่ได้รับรองเป็น production transport selector**
- D3D11: Hook owns DXGI Present slot and native D3D11 composition; avoid a second D3D10/D3D12 owner for same swap-chain Present slot
- External D3D9 GPU Windowed: OSC Host/DWM route (outside the game); no classic D3D9 NT shared-texture compatibility implied; can fail in fullscreen/exclusive

## 5) Wire/ABI contracts

Canonical header: Duluka Hook/include/osc_frame_bridge.h

| Protocol | Mapping / ABI | Purpose |
|---|---|---|
| Main D3D11 bridge | Local\NVIDIAOSC.Frame.v3 | OSC GPU shared resource metadata |
| Consumer adapter | Local\NVIDIAOSC.Frame.ConsumerAdapter.v2 | game PID, adapter LUID, renderApi identity |
| D3D9 CPU bridge | Local\NVIDIAOSC.Frame.D3D9.v1 | CPU pixels + metadata + seqlock |
| D3D9Ex GPU mailbox | include/d3d9ex_gpu_mailbox.h | producer/consumer generations, sync and ACK |

D3D9 frame mapping: magic/version, volatile sequence (writer odd / stable even), owner PID, width, height, rowPitch, pixelBytes, frameNumber and pixels. Limit 64 MiB pixel bytes; fixed offsets verified in x86/x64. Never change size/field offsets or v1/v2 wire version without coordinated Host + Hook + regression.

ConsumerAdapterInfo v2: magic/version, ownerProcessId, adapterLuid, renderApi (D3D9=9, D3D11=11). D3D9 vs 9Ex is confirmed by native device inspection/status, not guessed from module name.

## 6) API support (truth table)

| API | Native Hook | Native renderer | Shipping interpretation |
|---|---|---|---|
| Classic D3D9 | Yes | CPU frame composition | Windowed external DWM GPU route proven in real game; fullscreen GPU Hook NOT certified |
| D3D9Ex | Yes | CPU + experimental legacy GPU bind | GPU mailbox/interop tests pass; selectable production GPU route NOT certified |
| D3D11 | Yes | D3D11 shared GPU composition | Native smoke/regression; preserve DXGI owner |
| D3D10 | No | No | Folder/README only |
| D3D12 | No | No | Folder/README only |
| Vulkan | No | No | Folder/README only |
| OpenGL | No | No | Folder/README / incomplete header only |

## 7) Runtime topology and safe lifecycle

~~~text
Build/NVIDIA ShadowPlay/NVIDIA Adapter/
  NVIDIA OSC Adapter.exe                 (x64)
  NVIDIA OSC Adapter Hook.dll            (x64)
  x86/NVIDIA OSC Adapter.exe             (x86)
  x86/NVIDIA OSC Adapter Hook.dll        (x86)
  CrossIntegrity/
    NVIDIA OSC Adapter.exe               (x64)
    NVIDIA OSC Adapter Hook.dll          (x64)
    x86/NVIDIA OSC Adapter.exe           (x86)
    x86/NVIDIA OSC Adapter Hook.dll      (x86)
~~~

Host must verify target PID + exact EXE path, target process handle, architecture and appropriate render API BEFORE DLL load. Container/game identity checks are separate and must remain on. Never kill another process just to free a DLL. Do not unload native GPU COM resources under DllMain loader lock. Device-thread quiescence/controlled safe dynamic unload is NOT fully certified. Respect game window focus; no overlay over unrelated apps.

## 8) Where to continue

- Codex instructions: ../CODEX-HANDOFF.md
- Actual source-vs-runtime status & hashes: HOOK-STATUS-EVIDENCE-2026-10-25.md
- D3D9 stutter diagnostic/acceptance plan: DX9-PERFORMANCE-VALIDATION-2026-10-25.md
- Existing canonical architecture: ../ARCHITECTURE.md
- Existing native ABI/lifecycle rules: ../HOOK-CONTRACT.md
- Existing historical progress: ../IMPLEMENTATION-STATUS.md
