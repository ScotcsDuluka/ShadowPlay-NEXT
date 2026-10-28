# DX9 Hook Performance — สาเหตุที่ต้องพิสูจน์ + แผนวัด FPS/Frame Pacing

**วันที่:** 25 ตุลาคม 2026
**เจ้าของงานต่อ:** Codex / Duluka Hook + OSC Adapter
**ปัญหาจากผู้ใช้:** Desktop Mode ลื่นได้ถึงระดับ 240 FPS แต่เมื่อเป็น Hook Mode / DX9 ยังรับรอง 60 FPS นิ่ง ๆ ไม่ได้ และยังเห็นอาการกระตุกแม้ Windowed GPU Overlay แสดงภาพแล้ว

## นิยามความสำเร็จ

การเห็น OSC Main-menu ผ่าน Alt+Z เป็นเพียง functional proof. Performance PASS ต้องมีผลวัดเกมจริง โดยแยกอย่างน้อย 4 ตัววัด:
1. **Game Present pacing:** เวลา Present-to-Present ต่อเฟรมของโปรเซสเกม; FPS average และ FPS percentiles/1% low จากการแจกแจง frame time (กำหนดวิธีคำนวณให้ชัด)
2. **Hook overhead:** เวลาที่ใช้ใน HookPresent ก่อนเรียก Original Present, p50/p95/p99/max; ค่า overhead ไม่ควรรวมเวลาที่ Present ของระบบปฏิบัติการ block
3. **OSC compositor:** GPU frame arrival, present interval, GPU present latency, GPU drop/coalescing; ไม่ใช้ตัวเลขนี้แทน Game FPS
4. **User interaction stability:** Alt+Z เปิดค้าง, ปิด, Alt-Tab, move/resize, ReShade, multiple GPUs และ Exit/Reset; ไม่มี crash, UI ซ้อนผิดหน้าต่าง หรือคีย์ค้าง

Target stage 1: ระหว่างเปิด OSC Hook ใน D3D9 Windowed ความเร็วเกมที่ตั้งเป้าหมาย 60 FPS ต้องรักษาจังหวะประมาณ 16.67 ms ต่อเฟรมได้โดยไม่มี recurring spikes; บันทึก p50/p95/p99/1% low และเทียบ baseline. ระบุ refresh rate จริง, VSync, game frame cap และ thermal/power state ก่อนตัดสิน. หากเครื่องถูกตั้งเป้า >60 Hz ให้แสดงทั้ง FPS กับเวลาจริงอย่างซื่อสัตย์ และอย่าดาวน์เกรด Desktop Mode จาก 240 FPS โดยไม่มีเหตุ.

## เส้นทางที่ต้องแยกในการวัด

~~~mermaid
flowchart TD
    GAME["เกม D3D9"] --> PRES["Game::Present"]
    PRES --> HOOK["Duluka HookPresent<br/>timestamp before/after overlay work"]
    HOOK --> ORIG["Original D3D9 Present"]
    OSC["OSC CEF accelerated frame"] --> HOST["OSC Host Publisher"]
    HOST --> CPU["CPU Shared Memory<br/>frameNumber + seqlock"]
    CPU --> R9["D3D9 Renderer<br/>Texture Upload / Blend"]
    R9 --> HOOK
    OSC --> DCOMP["External D3D11/DComp"]
    DCOMP --> DWM["Windows DWM<br/>Windowed game composite"]
    GAME --> DWM
    HOST --> MAIL["D3D9Ex GPU mailbox (experimental)"]
    MAIL --> EX["D3D9Ex GPU Alias/Hook"]
    EX --> HOOK
~~~

### A: External D3D9 Windowed GPU (current proof path)
- CEF -> native D3D11 + DComp -> DWM, separate HWND.
- D3D9 Hook is **still attached** to game even though Overlay pixels are not drawn into D3D9 backbuffer.
- Thus any per-Present Hook work, identity query, backbuffer COM fetch, window focus or synchronization can still disturb game pacing.
- Currently deployed Hook does extra GetRenderTarget/GetBackBuffer on frames with no CPU overlay; Source-only candidate short-circuits under strict guards. This overhead is a **hypothesis**, not yet proof of root cause.
- External overlay cannot be assumed visible in fullscreen exclusive.
- OSC Host desktop FPS limit observed 75 in configuration in previous inspection; user's Desktop Mode can reach higher fps in other configurations. Do not globally clamp the UI/desktop clock to mask a DX9 issue.

### B: Classic D3D9 CPU shared frame
- Need exact CPU snapshot duration, texture-lock/upload, stateblock & render-target save/restore times; end-to-end Host->Bridge->Hook frame age; stale-pixel handling
- Respect renderer mutex try_lock; avoid blocking Present behind producer
- Validate image/premultiplied alpha/Reset under CPU fallback

### C: D3D9Ex GPU legacy mailbox
- Verify actual game device is Ex, matching adapter LUID, generation, staging synchronization, ACK only after legal GPU operations, release and restart behavior
- Tests passed in synthetic real Host-source IPC, **not equivalent to smooth actual game**; no blanket transport enable

### D: D3D11
- Independent regression gate; do not change D3D11 DXGI Present owner, ReShade callback order, texture relay or ABI as collateral while tuning DX9

## Inspect these exact files in order

1. Duluka Hook/src/Renderer/D3D9/D3D9Renderer.cpp
   - OnPresent, DrawOverlay, PublishConsumerAdapter, SnapshotFrame, GetRenderTarget, GetBackBuffer, CPU snapshot/copy, GPU alias and timing exports.
2. Duluka Hook/src/Hook/D3D9/D3D9Hook.cpp and LateD3D9Detours.inc
   - HookPresent/PresentEx, reentrancy, factory fallback, late attach, Reset lifecycle, original function forwarding.
3. Duluka Hook/include/osc_frame_bridge.h and include/d3d9_pipeline_timing_diagnostics.h
   - frameNumber/sequence, mapping version/size/reader consistency; diagnostic ABI.
4. Project/Overlay On-Screen Control/NVIDIA OSC/src/osc_main.cpp
   - OscUpdateClassicD3D9GpuPreview, window/focus/geometry, g_game_focused, DComp Present timer, CEF accelerated callbacks and limit/coalescing.
5. Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/OscD3D11FramePublisher/OscD3D11FramePublisher.cpp
   - CPU D3D9 fallback mapping, GPU shared resource path, frame publish cadence; **earlier experimental fastpath was rolled back after actual-game crash**. Do NOT reapply without independent proof.
6. Project/Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/Common/OscHookTransportPolicy.h
   - D3D9 GPU means Windowed DComp preview only. D3D9Ex GPU selection disabled.
7. Project/NVIDIA Adapter/OSC Adapter/Program.cs + src/Detection/RenderApiPolicy.cs
   - x64->x86 Host selection, PID/EXE identity, explicit API hint vs runtime evidence.
8. Duluka Hook/tests/verify-adapter.ps1
   - CI/regression source of truth.

## Known diagnostic assets

- Native Hook read-only timing:
  - include/d3d9_timing_diagnostics.h
  - include/d3d9_pipeline_timing_diagnostics.h
  - Export: NvOscAdapterGetD3D9TimingSnapshot and NvOscAdapterGetD3D9PipelineSnapshot (verify the exact function ABI in headers/source).
  - Candidate adds NvOscAdapterGetD3D9EmptyFrameSkips; do not query this export on old Runtime without GetProcAddress null check.
- OSC Host logging: Build/NVIDIA ShadowPlay/Logs/NVIDIA OSC.log, [oscperf] counters and [dx9-windowed-gpu] activation.
- Native Hook logs: %LOCALAPPDATA%/NVIDIA/OSC Adapter/Logs/adapter-d3d9-<PID>.log and adapter-hook-<PID>.log.
- Windows ETW tools installed: Windows Performance Recorder/Analyzer (GPU trace preflight succeeded). Note: NVIDIA FrameView SDK bundled PresentMon_x64.exe v1.9 failed to capture CSV (exit 1); do not claim measured game FPS using those attempts. WPR GPU-Telemetry-Preflight.etl was game-free, not a performance baseline.
- Existing real-game screenshot: Build/DulukaHook-D3D9-FactoryFallback-20261024/FloatingPoint-GPU-Windowed-RealGame.png
- Real-game Host sample, six one-second windows: 389 OSC GPU Presents, 0 CPU publish, 0 busy drops; cannot calculate game 1% low from it.

## Minimum A/B test matrix

| Run | Game / configuration | Hook state | OSC UI state | Objective |
|---|---|---|---|---|
| A | Floating Point classic D3D9 Windowed same resolution | baseline Hook build | Overlay closed for >=30 s | game frametime baseline, no UI animation |
| B | Same game, scene, window, refresh and VSync | same Hook | GPU Windowed Overlay OPEN and held >=30 s | game frametime + OSC present + focus |
| C | Same as A | same Hook | Overlay closed for >=30 s | confirm recovery/repeatability |
| D | Controlled isolated synthetic fixture | candidate Hook only (not installed) | no CPU frame | quantify no-frame skip count + Present duration |
| E | After authorized installation via approved method | candidate Hook | closed / held open / closed | prove candidate improves game FPS, preserves rendering |
| F | Optional, after safety checks | stable CPU shared bridge | CPU Overlay OPEN | distinguish texture-upload stutter from external compositor |
| G | D3D11 controlled fixture | unchanged DXGI Hook | hidden/show/reset | prove no regression |

Capture time windows from same scene, warm-up, resolution, monitor, backend, game PID; report effective API and verified LUID. Exclude Unity Configuration launcher; start timing only after a real UnityWndClass window and actual D3D9 Present exist. Do not close/kill someone else's game, change global display refresh or silently modify game preferences.

## Suggested instrumentation (low-overhead, opt-in)

**No new synchronous logging, QueryPerformanceCounter formatting, lock waits or disk I/O inside the Present hotpath.**
- Game Hook: fixed-capacity lock-free/very-short critical section ring buffer; QPC before Hook overlay work, before/after original Present; counters only; snapshot from trusted read-only same-process tester.
- Avoid allocations on every frame; keep diagnostic gathering outside DllMain and avoid COM on instrumentation thread.
- Record intervals and p50/p95/p99/max from a bounded test session; correlate by relative QPC clock and PID, not UI display tick alone.
- Host: record CEF frame serial, GPU present attempt/success/busy, DComp window reposition count, focus transitions, stale-frame retire count, real frame age and timing.
- Hook: count Present/PresentEx, empty-frame skips, skipped try_lock, CPU snapshot bytes and upload duration, D3D9Ex GPU frame/ACK counts.
- For game Present-to-Present use a verified ETW decoder/PresentMon with matching platform permissions; do not substitute Host present_n for game presents.

## Candidate empty-frame patch guard review

Current Source-only patch in D3D9Renderer.cpp:
- g_identityDevice == game device and g_adapterLuid != 0 (identity already published via standard path).
- CPU mapping exists and its magic/version match v1.
- no D3D9Ex bound/pending GPU alias; cached CPU frame/upload numbers both zero.
- seqlock stable/even before and after read; ownerProcessId > 0; frameNumber == 0.
- only then skip GetRenderTarget and GetBackBuffer, preserving Original Present.
- new read-only export for skip counter; native x86/x64 tests passed.
- It is not deployed; test fixture observed 199 skips/210 frame calls.
- Important safety review: if previously composited CPU pixels exist or producer races a newer frame, do NOT accidentally erase overlay. Frame generation 0 only indicates hidden/none according to producer contract and must be validated against Host updates. Review race and reentrancy invariants on every change.

Prior dangerous fastpath: skipping Host GPU resource updates in OscD3D11FramePublisher.cpp caused a real Floating Point launch crash; source was restored. Avoid resurrecting it without crash analysis, controlled A/B and easy rollback.

## Test commands (Source-only by default)

Run from PowerShell:

~~~powershell
$repo = 'C:\My Project\NVIDIA-Shadowplay'
$native = Join-Path $repo 'Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
git -C $repo status --short
& (Join-Path $native 'tests\verify-adapter.ps1')
# These native regressions DO NOT install a DLL into the live game.
# Only if an interactive desktop test is appropriate and isolated:
& (Join-Path $native 'tests\verify-adapter.ps1') -RunDx9WindowTests
~~~

Before any build, check if another chat modified source or if a test is already running. All PASS claims must mention the exact source SHA and log path.

## Safety/rollback and deployment contract

1. Source modifications do not imply Runtime changes.
2. Preserve x86/x64 matching, active CrossIntegrity copies and native ABI.
3. Recheck hashes and current processes immediately before any install. Do not overwrite running Hook or user's game files.
4. Previous deployment attempt for this candidate was BLOCKED by tool-level safety. **Do not bypass tool guards or attempt substitute installation routes**. Seek an explicitly supported, authorized deployment workflow. Source-only diagnostics/fixtures are still allowed.
5. If an authorized release test later proceeds, create new unique backup & verified manifest and roll back on crash, missing UI, input/focus issue or regression. Do not run stale deployment scripts automatically.

## Final reporting format required of Codex

| Measurement | Baseline closed | Stable Windowed GPU open | Candidate open | Reference |
|---|---:|---:|---:|---|
| Game Present count | TBD | TBD | TBD | game ETW/counter |
| Game average FPS | TBD | TBD | TBD | game event stream |
| Game p50/p95/p99 frame time (ms) | TBD | TBD | TBD | per-frame timestamps |
| Game 1% low FPS | TBD | TBD | TBD | describe calculation |
| Hook CPU time p50/p95/p99 (μs) | TBD | TBD | TBD | Hook timer |
| OSC GPU Present / busy drop | TBD | TBD | TBD | [oscperf] |
| Crash/Focus/Resize/Reset | TBD | TBD | TBD | logs + repro |

If data missing, explicitly write **UNVERIFIED**. Do not close the stutter issue based only on UI visibility, native build PASS, or 389 OSC present events.

## Hand-off & related docs

- ../CODEX-HANDOFF.md
- HOOK-ARCHITECTURE-2026-10-25.md
- HOOK-STATUS-EVIDENCE-2026-10-25.md
