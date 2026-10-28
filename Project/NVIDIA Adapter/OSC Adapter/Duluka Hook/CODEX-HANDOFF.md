# CODEX HANDOFF — ShadowPlay NEXT / Duluka Hook
วันที่ส่งต่องาน: 25 ตุลาคม 2026
**Primary objective:** ทำให้ D3D9 Hook Mode รักษาเกม 60 FPS อย่างสม่ำเสมอโดยไม่ลดความลื่นของ Desktop Mode (ผู้ใช้รายงาน Desktop Mode ไปได้ถึง 240 FPS)
**Status:** Documentation handoff ready. NO new hook DLL deployment in this documentation-only work.

## 0) อ่านก่อนแก้โค้ด (สำคัญ)

Repo: C:/My Project/NVIDIA-Shadowplay
Branch at handoff inspection: Stable; HEAD 529a0d31a8
Owner scope: Project/NVIDIA Adapter/OSC Adapter/ and native Duluka Hook
Codex MUST read these files first, in order:

1. Duluka Hook/README.md — navigation and safety summary
2. Duluka Hook/docs/HOOK-ARCHITECTURE-2026-10-25.md — verified current architecture, host/Hook/data flow/ABI
3. Duluka Hook/docs/HOOK-STATUS-EVIDENCE-2026-10-25.md — precise deployed vs candidate truth, SHA, tests, known risks
4. Duluka Hook/docs/DX9-PERFORMANCE-VALIDATION-2026-10-25.md — execution plan, timing definitions, acceptance criteria
5. Duluka Hook/HOOK-CONTRACT.md — mandatory COM, API ownership, callback and unload invariants
6. Duluka Hook/ARCHITECTURE.md — canonical architecture, includes older history
7. Duluka Hook/tests/verify-adapter.ps1 — actual native regression driver
8. Build/DulukaHook-D3D9-ZeroFrameShortCircuit-20261024/HANDOFF-20261024.md — previous candidate-only handoff
9. Build/DulukaHook-D3D9-FactoryFallback-20261024/REAL-D3D9-GPU-VALIDATION-20261024.md — actual Floating Point Windowed visual proof

Do not assume historical statements that say "not deployed" apply to every present-day module: inspect actual binary hashes and installed config. The docs in this handoff separate Source/Build/Runtime.

## 1) User-reported truth

- Desktop Mode is already fluid, potentially 240 FPS depending on configuration. DO NOT reduce desktop FPS caps or CEF globally to "solve" DX9.
- D3D9 Hook Mode / real game still visibly stutters and is NOT certified to sustain 60 FPS. Showing pixels successfully is not a performance pass.
- The user intentionally toggled OPEN <-> CLOSED many times while testing responsiveness. Previous interpretation "duplicate Alt+Z hotkey bug" was wrong; do not fix Helper based solely on that log pattern.
- Classic D3D9 Windowed GPU Preview uses OSC's separate D3D11/DirectComposition GPU surface + DWM. The game's Hook continues to intercept Present, but the DWM route does not inject GPU overlay pixels into D3D9 backbuffer.
- Fullscreen Exclusive Classic D3D9 GPU Hook is NOT implemented/certified by that Windowed compositor.
- A proposed candidate skips unnecessary D3D9 GetRenderTarget/GetBackBuffer calls when there is a verified absent CPU frame. It passes synthetic + full native regression; its effect on the actual game is **unmeasured** and it has **NOT** been deployed.

## 2) Explicit current stage

| Area | State |
|---|---|
| GameWatcher -> .NET OSC Adapter x86/x64 routing | Exists and regression tests |
| D3D9 factory fallback Hook | Deployed old production Hook (newer than original baseline) |
| Windowed D3D9 GPU via OSC Host/DWM | Live visual PASS in Floating Point x86; stutter remains |
| D3D9 CPU bridge | Implemented, Native regression |
| D3D9Ex legacy shared GPU mailbox | Cross-process/Host-source fixture PASS, production GPU selector disabled |
| DXGI/D3D11 GPU Hook | Implemented; preserve existing renderer and DXGI ownership |
| D3D9 ZeroFrame early skip | SOURCE/BUILD/SYNTHETIC/REGRESSION PASS; RUNTIME NOT DEPLOYED |
| D3D10 / D3D12 / Vulkan / OpenGL | NOT implemented native renderers |
| Actual game 60 FPS steady / P95/P99 frametimes | UNVERIFIED |
| Safe dynamic unload from arbitrary game | UNVERIFIED |

## 3) Immediate work order (perform in sequence)

### Phase A — Preflight / no mutation

1. Confirm source path, project branch, relevant Git dirtiness and expected SHA. Do not reset/stash other developers' changes.
2. Inspect all four deployed Hook DLL locations and the x64/x86 candidate binary fingerprints from HOOK-STATUS-EVIDENCE. Confirm no active game or adapter before controlled fixture work.
3. Read log and source; determine the real active route from API/device/window evidence. A saved Hook Settings GPU preference alone is not proof of the route.
4. Compare timing/feature changes only inside Duluka Hook/OSC Adapter scope. No Launcher, Capture, Server, Portal or broad Build scripts.

### Phase B — Measurement FIRST

1. Instrument the native D3D9 Hook with low-overhead opt-in counters only. Distinguish time spent in our Hook from blocking time inside Original Present.
2. Collect actual game Present-to-Present frame intervals with verified ETW/PresentMon or equivalent safe tool. Old NVIDIA FrameView PresentMon v1.9 did not produce CSV in prior runs. WPR GPU trace preflight was successful but game-free. Do NOT invent FPS.
3. Benchmark actual Floating Point Windowed (same scene, same refresh, same VSync, same resolution):
   - Overlay closed 30+ sec -> Overlay GPU open 30+ sec -> closed 30+ sec
   - Correlate with OSC GPU Present/coalescing and focus transitions
   - Report Average FPS, 1% low, p50/p95/p99/max game frame time, Hook overhead, OSC present and drop counters.
4. If real game cannot be operated/observed safely, explicitly report blocker and keep changes/test source-only.

### Phase C — Isolated candidate review

1. Examine src/Renderer/D3D9/D3D9Renderer.cpp candidate empty-frame guard: identity published, valid frame v1, stable sequence, owner PID, frameNumber=0, no Ex GPU aliases, no cached CPU image.
2. Audit all exits/Reset/ReShade interactions, hide/show/new-frame race, device identity changes, producer recovery, Original Present forwarding, p95/p99 thread lock contention. Do not blindly assume synthetic 199 skips means improvement to actual game.
3. Re-run controlled x86/x64 smoke, late attach, DX9Ex GPU mailbox, cross-bitness, D3D11 regression with exact log evidence. Verify ABI remains unchanged except additive optional diagnostic export.
4. If needed, implement isolated experiment in a new scope and measure against stable Hook without removing protections. Do not alter OscD3D11FramePublisher fastpath: earlier experimental resource-skipping build was associated with Floating Point crash and rolled back.

### Phase D — Deployment gate (NOT automatic)

**STOP if tooling blocks changes to installed DLLs. Do not circumvent security restrictions.**
If and only if an explicitly supported, authorized deployment workflow is available:
1. Ensure no game process/Hook/adapter instance is active; recheck once at action time.
2. Verify fresh SHA256 of each of the 4 runtime DLLs and both release binaries; all must match architecture.
3. Create fresh, unique rollback backups and a dry-run verified manifest. Do not overwrite earlier backups.
4. Deploy only within authorized scope. Run native regression plus actual game Windowed A/B; roll back if any crash, corruption, FPS regression, focus/input breakage, DPI/resize mismatch.
5. Record exact paths, final hashes, PID/adapter evidence, restart/lifecycle state. Do not falsely claim that a Source edit equals a successful deploy.
6. Do not commit, push, create releases or alter files of other modules unless expressly requested and verified safe.

### Phase E — Review & end result

- Produce a versioned Markdown result in Duluka Hook/docs/ with a reproducible benchmark method and exact numbers.
- Use PASS/FAIL/UNVERIFIED, not "ลื่นแล้ว" based on subjective assumptions or CPU/GPU compositor Present rate alone.
- If real game P95/P99 is still poor, attribute the actual measured bottleneck (CPU snapshot, texture upload, Backbuffer COM calls, OS compositor, mode/focus transitions) before next patch.
- Maintain API support truth table; do not advertise D3D9Ex GPU or Fullscreen Classic D3D9 GPU based on Windowed DWM success.

## 4) Useful commands (read-only / build / native regression)

~~~powershell
$repo = 'C:\My Project\NVIDIA-Shadowplay'
$hook = Join-Path $repo 'Project\NVIDIA Adapter\OSC Adapter\Duluka Hook'
git -C $repo status --short
git -C $repo branch --show-current
Get-FileHash (Join-Path $hook 'src\Renderer\D3D9\D3D9Renderer.cpp') -Algorithm SHA256
Get-FileHash (Join-Path $repo 'Build\NVIDIA ShadowPlay\NVIDIA Adapter\CrossIntegrity\x86\NVIDIA OSC Adapter Hook.dll') -Algorithm SHA256
& (Join-Path $hook 'tests\verify-adapter.ps1')
# verify-adapter is non-deploying unless its script is changed; inspect before execution.
~~~

Relevant actual logs:
- Build/NVIDIA ShadowPlay/Logs/NVIDIA OSC.log
- %LOCALAPPDATA%/NVIDIA/OSC Adapter/Logs/adapter-d3d9-<PID>.log
- %LOCALAPPDATA%/NVIDIA/OSC Adapter/Logs/adapter-hook-<PID>.log
- Build/DulukaHook-D3D9-ZeroFrameShortCircuit-20261024/FULL-REGRESSION-attempt1.log

## 5) Ownership, no-go boundaries

- Maintain matching x86 / x64 binaries, explicit API detection and PID/image validation.
- Preserve all CPU bridge/mapping and versioned wire ABI. Preserve ReShade callback sequencing and original function pointers.
- No COM teardown in DllMain. No renderer lock held while calling Original Present. No hotpath disk logging.
- Do not enable unsupported APIs or D3D9Ex GPU UI solely because a fixture passes.
- Do not edit or deploy NVIDIA game files or arbitrary third-party processes.
- User is testing UX often; don't interfere with their running game or leave Unity window-mode preferences modified without tracking restoration.
- The broader Git worktree has other chats' modifications; only touch files necessary for assigned Hook work.

## 6) Final task request to paste into Codex

> Project: C:\My Project\NVIDIA-Shadowplay (Stable). Own only Project\NVIDIA Adapter\OSC Adapter\Duluka Hook and the minimal native OSC Host interfaces required for DX9 measurement. Start by reading Duluka Hook\CODEX-HANDOFF.md and ALL THREE Markdown docs under Duluka Hook\docs dated 2026-10-25; also read HOOK-CONTRACT.md and previous Build handoff. Current user problem: Desktop Mode can be fluid at 240 FPS, but D3D9 Hook Mode still visibly stutters and is not even proven stable at 60 FPS. Verify runtime vs candidate SHA, preserve dirty worktree, gather real-game Present pacing and Hook CPU overhead, isolate the actual bottleneck, run x86/x64 and D3D11/D3D9Ex regressions. The current D3D9 empty-frame early-skip candidate is NOT deployed; previous install attempt was blocked. Do not bypass tooling protection, do not force deploy, commit, push, rewrite existing work or call an unverified improvement a fix. Report detailed benchmark table and honest PASS/FAIL/UNVERIFIED, creating a new versioned report in Hook/docs. Prioritize measurable 60 FPS steady before higher FPS.

## 7) Handoff completeness

This file is a **persistent work order for Codex** placed inside the local project and linked by README. It does NOT mean a Codex task has been initiated in a separate chat, and it does not make unscheduled background progress. No source/runtime DLL was modified while preparing this documentation bundle.
