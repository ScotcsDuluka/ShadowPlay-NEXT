# ShadowPlay NEXT — Phase I3: handoff to Codex (2026-10-25)

Machine: HUAWEI-MateBook ONLY
Project: C:\My Project\NVIDIA-Shadowplay
Branch: intel/duluka-capture
STATUS: PARTIAL / UNVERIFIED. I3 is NOT finished.

## Mandatory reading
Read AGENTS.md, docs/architecture/SESSION-CENTRIC-RECORDING-CONTRACT.md, docs/architecture/SESSION-CENTRIC-RECORDING-TH.md, docs/INTEL-DULUKA-CAPTURE-ARCHITECTURE.md, docs/INTEL-DULUKA-CAPTURE-I2-HANDOFF.md, docs/INTEL-DULUKA-CAPTURE-I3-HANDOFF.md, docs/reports/intel-duluka-i2-2026-10-24/README.md.

## Last verified baseline (I2, not I3)
I2 report: 1,078 Passed / 0 Failed / 33 Skipped. Native QSV VBR/CQP 60/60 full-decode synthetic frames. Real physical Intel LUID 00000000:0000bf60, output 0, native runtime 1.34. CBR is still known-failed. No live Desktop, Exclusive or audio capture.

## Current I3 changes on disk (do NOT overwrite concurrent I1/I2 changes)
1. NEW: Project/NVIDIA Capture/EngineCapture/Duluka Capture/Video/Contracts/DulukaEngine.Video/Conversion/BgraToNv12Converter.vb
   Converts test-owned top-down BGRA Byte() with optional padded row pitch to tightly packed NV12 BT.709 LIMITED. Rejects invalid dimensions, stride, input length and overflow. Does not read any real D3D11 desktop texture.
2. NEW: Project/NVIDIA Capture/EngineCapture/QsvNative.Tests/I3PixelTransport.cs
   Synthetic pitched BGRA -> NV12 -> SAME existing RecordingEngine.StartOwnedFrameSession -> Intel QSV -> existing mux -> verification. Tests 60-frame moving-marker output, full decode, identity, disposition and synthetic-vs-live counters.
3. MODIFIED within pre-existing UNTRACKED I2 test subtree: Project/NVIDIA Capture/EngineCapture/QsvNative.Tests/Program.cs
   Adds an explicit --i3-transport entrypoint; default I2 test path remains intact.

## Compiler issue: important
Initial dotnet build of QsvNative.Tests FAILED with compiler error BC30668: Visual Basic compiler in this project rejects ReadOnlySpan(Of Byte) in the new converter.
Patched new converter to use parameter bgra As Byte() and guard bgra Is Nothing OrElse bgra.LongLength < requiredSource. Confirmed the patched source exists. NO BUILD or TEST was rerun after patch. Expect more errors and DO NOT report I3 PASS without actual test evidence.
Remote MCP process may inherit PATH lacking bare git. Use C:\Program Files\Git\cmd\git.exe or temporarily prepend to PATH.

## Next steps — Codex owns this work exclusively now
- Check branch, git status and other running agents before editing. Do not reset/clean/checkout over/commit/push or overwrite I1/I2 working files.
- Run: dotnet build "Project\NVIDIA Capture\EngineCapture\QsvNative.Tests\QsvNative.Tests.csproj" -c Release --nologo -m:1
- Fix only scoped compile/test failures.
- Run: dotnet run --project "Project\NVIDIA Capture\EngineCapture\QsvNative.Tests\QsvNative.Tests.csproj" -c Release --no-build -- --i3-transport
- Confirm 60 input/encoded/muxed/decoded synthetic frames, delayed flush, exact LUID, full media decode, moving marker, ownership, and FramesCaptured=0.
- Re-run existing Invoke-IntelCaptureI2Regression.ps1 and Release capture build, preserving historical 1,078/0/33 baseline if all retained evidence exists. Preserve --offline flags for Video tests.
- The ACTUAL remaining I3 gap is mapping/synchronizing a synthetic-owned BGRA D3D11 texture into CPU pitched rows (or separately verified native GPU path), ensuring selected physical device LUID, output, source generation and resource ownership before converting into NV12. Current converter only accepts caller-provided byte arrays; no GPU staging, no live DXGI path, no auto-switch.
- Do NOT lift intel_native_live_input_not_verified based on synthetic proof. CBR remains disabled. No true desktop/game pixels or loopback/mic tests without explicit privacy-cleared authorization. No AORUS changes. Keep NVENC source unchanged.
- Document exact results and limitations with I3 evidence. No commit/push.

This is a mid-task handoff, not a completion report.
