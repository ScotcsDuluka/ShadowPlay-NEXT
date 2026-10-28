# D3D9 sender / Hook code audit — 2026-10-25, v1

Scope: Stable, HEAD `529a0d31a8`. Source-only continuation after the user stopped Computer Use and requested code work. No further desktop input, game launch, injection, GPU fixture execution, deployment, commit or push was performed in this continuation.

## Result

**Runtime stability remains UNVERIFIED, with observed FAIL evidence.** CEF callback delivery and frame-counter correctness do not establish smooth displayed overlay frames. The earlier game session recorded OSC exceptions `0x80000003` and a visible white area reported by the user. Their root causes remain unknown. No sender or compositor performance fix is claimed.

**PASS — code checks:** native Hook and the two new graphics fixtures compile for Win32/x64; the actual recorder core passes 38 CPU-only checks per architecture; existing HostPublishCadence tests pass on both architectures. Installed Hook hashes remain unchanged. Graphics fixture execution, vendor ReShade and D3D11 runtime regression remain UNVERIFIED under the code-only constraint.

## Does the sender send continuously?

Source inspected, without changing the OSC Host:

- `NVIDIA OSC/src/osc_main.cpp`, `OnAcceleratedPaint`: CEF callbacks supply GPU surfaces. Eligible callbacks update the composed surface; empty dirty rectangles are retained when the visible UI needs compositor animation. Empty, unnecessary callbacks can be skipped while hidden.
- `OscDesktopFrameQueue.h`: the external Windowed D3D9 GPU route uses the Desktop worker. It waits for pending work, keeps a copied buffer across busy Present retries, and coalesces newer callbacks into the next copy. It does not generate new content merely because its FPS ceiling is 240.
- `OscDesktopFps.h`: Desktop supports a 15–240 Present ceiling. CEF requested frame rate is separately limited to 60 by `CefDesktopFps`; observed callback rates can differ. These are separate measurements, not a promise of 240 unique displayed UI frames.
- `OscPublishCadence.h`: the in-game shared bridge advances frameNumber only after copying content from a new CEF serial. Metadata/timer refreshes retain the serial. Hidden content advertises zero. The external DWM route retires the CPU bridge intentionally; `publish_n=0` is expected there.
- The burst timer ticks at 16 ms, but its explicit CEF invalidation is throttled to 100 ms. The log text “60fps frame refresh burst” describes the timer setup, not measured new-content FPS. Fullscreen Hook mode extends refresh while visible; Windowed GPU uses the separate desktop composition path.

Therefore, an idle, closed UI need not keep sending new image content. Animation/input should produce callbacks, but callback counts alone do not prove fresh image age, GPU completion, or DWM display cadence.

Recorded pre-crash samples at 00:26:20–22 Bangkok show CEF callbacks 120/120/118 per second and OSC accepted Presents 114/110/114. Each has CPU bridge publish_n=0 and busy drops=0. This establishes callback activity during those seconds, not displayed FPS or the absence of scheduling stalls.

## Actual game measurements retained from before code-only work

Game: Floating Point x86, Windowed 1024×768, PID 24936, D3D9 swapchain `0xC6DAFA0`, presenting thread 7352. Loaded Hook was the installed CrossIntegrity x86 DLL, not the new candidate. No ReShade module was observed in that process. Monitor inventory reported 1680×1050 at 75 Hz plus other displays; exact target-monitor refresh, VSync, cap and thermal state were not independently certified. Game preferences were left at the pre-existing Windowed 1024×768 selection.

| Measurement | Run 2, UI closed | Run 3, mixed / invalid GPU-open phase | Reviewed candidate |
|---|---:|---:|---|
| Present Starts / intervals | 1951 / 1950 | 2428 / 2427 | UNVERIFIED |
| Interval span | 35.0743 s | 35.3559 s | UNVERIFIED |
| Average FPS | 55.5962 | 68.6449 | UNVERIFIED |
| 1% Low FPS | 27.3212 | 7.9516 | UNVERIFIED |
| P50 frame time | 16.9273 ms | 13.3235 ms | UNVERIFIED |
| P95 / P99 frame time | 29.6567 / 33.6719 ms | 18.4385 / 49.7694 ms | UNVERIFIED |
| Maximum frame time | 43.6223 ms | 461.9898 ms | UNVERIFIED |
| Runtime Present duration mean | 16.4794 ms | 0.6386 ms | UNVERIFIED |
| Runtime Present duration P95 / P99 | 27.5871 / 31.0975 ms | 2.8308 / 5.3588 ms | UNVERIFIED |
| Total Hook overhead P95 / P99 | UNVERIFIED | UNVERIFIED | UNVERIFIED |
| Trace events / buffers lost | 0 / 0 | 0 / 0 | UNVERIFIED |

**Run 2 phase evidence:** capture requested 00:24:58.831 Bangkok for 35 seconds. Last preceding UI state was CLOSED at 00:22:21; external DWM route was active at 00:23:11. During the selected boundary seconds 00:24:58–00:25:35, only two OSC log lines appear, including a route-inactive/focus change at 00:25:34. The end boundary is consequently not a certified uninterrupted foreground baseline. The user had reported the white-area problem. This is real game Present data, with these functional and phase limits, not a clean acceptance benchmark. One unmatched Stop and one unpaired Start occur at capture boundaries; all 1950 paired Stops have Result=0.

**Run 3 phase evidence:** 00:26:33.095–00:27:08.742 Bangkok. An OSC exception occurred at 00:26:31 before capture; a new initialization appeared at 00:26:44. Ten OPEN and nine CLOSED transitions occurred during capture. Twenty OSC performance windows had present_n=0. This did not hold the external GPU overlay open. Intentional toggling is not classified as a duplicate-hotkey defect. All 2428 paired Stops have Result=0. A further OSC exception appeared after capture at 00:27:16.

**FAIL:** observed sessions do not establish steady 60 FPS or reliable overlay visuals. The increased average in Run 3 is not evidence of a candidate improvement: the candidate was never installed, and lifecycle/focus/rendering conditions changed.

Runtime Present Start→Stop excludes Hook work before runtime entry. Its duration can include driver/GPU/compositor waits; it does not isolate which subsystem caused the delay. The first run spends substantial time inside runtime Present, so removing empty-frame Hook COM calls cannot be declared the solution from these data.

## Reproducible measurement method

`tests/Measure-D3D9PresentTrace.ps1` captures only Microsoft-Windows-D3D9 using a fresh named logman session, all keywords, level 5 and QPC clock. It stops only the session it created, never launches/focuses a game and never installs a Hook. A provider preflight at keyword 0x2/level 0 yielded no events; all-keywords/level 5 yielded actual Present Starts/Stops. FrameView PresentMon live/offline attempts did not produce usable CSV; their failed evidence is retained.

Offline analysis filters the requested PID and keeps each swapchain/thread stream separate. Raw QPC and trace PerfFreq 10,000,000 avoid locale-formatted Buddhist-calendar timestamp artifacts. Two decoder passes are required because tracerpt forbids combining `-rts` and `-summary`. Explicit EventsLost/BuffersLost plus summary counters validate loss status.

Average FPS = 1000/mean(all consecutive runtime PresentStart intervals). 1% Low = 1000/mean(the largest ceil(N×0.01) intervals). P50/P95/P99 use nearest rank. No warm-up, long gap or failed event is silently removed. Runtime Starts are CPU presentation cadence, not proof of unique displayed frames.

## Code changes and verification

1. D3D9 empty-frame guard now requires current shared identity magic/version, owning PID, API=9 and matching adapter LUID before skipping COM backbuffer work. A competing writer previously could replace the shared identity while the cached device/LUID stayed valid; the candidate could then bypass the ordinary republish path indefinitely. Mismatch now falls through that existing path. New identity publication is not moved into the shortcut. Renderer lock lifetime and original Present forwarding are preserved.
2. Additive opt-in same-process diagnostics record entry, before-Original and after-Original QPC values, thread, Present/PresentEx kind and HRESULT. Entry precedes foreground/ReShade gates. Default is disabled; fixed storage holds up to 16,384 records, with busy/overflow/unfinished accounting and epoch protection against stale completions. No lock is held across Original Present. Counter CAS retries are lock-free but have no strict per-call latency bound when enabled. Separate ReShade composition outside the raw callback is not measured.
3. CPU-only tests exercise the actual extracted recorder implementation: validation, reentrancy, bounded storage, frozen reads, deterministic contention, Stop with an in-flight call, stale completion/entry after restart, and ten rounds of eight concurrent writers. No Hook DLL, window, GPU device or shared bridge is used.
4. New graphics fixtures compile for Win32/x64 and cover identity recovery/hidden→visible/reset and real intercepted Present samples. They were not executed in this code-only continuation. A negative run against the archived original candidate and positive run against the reviewed build remain required.

`tests/verify-d3d9-code.ps1` builds into a fresh test directory and runs only the CPU core. Its completed result records all eight builds and both CPU test executions with exit 0. Each CPU execution reports 38 checks, zero failures. Compiler warnings in Hook builds were confined to the existing third-party MinHook sources; no warning was emitted from the newly changed Hook/renderer or tests.

Export comparison preserved all installed DLL export names: Win32 32→40, x64 18→22, no removals. Additions are the empty-skip diagnostic and three recorder functions, including Win32 aliases. This checks export availability, not full behavioral compatibility. The existing frame bridge header, D3D11 Hook/renderer and OSC Host/publisher sources have no working-tree diff from HEAD. Wire-layout assertions compile for both architectures.

Source-only identity recovery finding is addressed. Its new graphics regression execution and actual-game performance remain UNVERIFIED. Existing full native, cross-bitness GPU mailbox, D3D11 and vendor ReShade runtime regressions are deferred, not represented as new PASS results.

## Sender audit limits and withdrawn inference

- An initial geometry suspicion was withdrawn after reading `OscRenderScreenRect`: it returns active D3D9 game bounds before monitor bounds. Opening OSC therefore does not by itself prove the proposed monitor-sized/cached-bounds mismatch. No geometry fix is justified from that inference.
- Source-derived CPU-bridge recovery risk remains separate: takeover of a valid mapping left with odd sequence does not visibly reset parity, while each publication adds two. This needs an isolated actual-publisher recovery reproducer; it is not established as the Windowed GPU stutter cause and no publisher edit was made.
- Existing cadence tests validate monotonic/new-content metadata behavior, not the live CEF callback thread, GPU synchronization, focus/resize, white-area defect, content age or displayed cadence.

## Runtime / ownership gate

Installed Hook hashes before and after code checks remain:

- x64 root and CrossIntegrity: `BEE32E1F9B76785EA9C5E5B389C060C65CB1B7F7DD5C36BE34F0764401BF7CA4`
- x86 root and CrossIntegrity: `D931EBF2D2C85AAF6C040E30FF47AA57D399556D38A40DD907147DFBD9747598`

OSC Host, publisher fastpath, Desktop FPS settings, ReShade, game files and unrelated dirty work were not edited. The earlier tool deployment block remains respected; no substitute deployment route was attempted. User stopped Computer Use; it was not resumed. A temporary read-only inspection command in an earlier agent reported CreateProcess access denied and was not rerouted.

Support remains: classic D3D9 CPU Hook and Windowed external DWM GPU route; D3D9Ex CPU plus experimental mailbox; D3D11 existing GPU renderer. Fullscreen classic D3D9 GPU is not certified. D3D9Ex selectable production GPU, D3D10, D3D12, Vulkan and OpenGL are not newly enabled or claimed.

## Evidence locations

Relative to repository root:

- `build_test/NVIDIA DulukaHook_Perf_Test/20261025-attempt1/analysis-2-closed/summary.json`
- `.../analysis-2-closed/log-correlation-root-attempt3/selection.json` and `phase-log.txt` (corrected extraction; earlier attempts retained)
- `.../analysis-3-open-invalid/summary.json`, `PHASE-CORRELATION.md`, `log-extract-attempt2/`
- `.../original-candidate-Win32/`, `original-candidate-x64/`, `candidate-before-review.cpp`
- `build_test/NVIDIA DulukaHook_Perf_Test/20261025-code-attempt1/result.json`, `sources-before.json`, `runtime-before.json`, `runtime-after.json`, `export-compatibility.json`, build/test logs and `bin/`
- `build_test/NVIDIA DulukaHook_Perf_Test/20261025-sender-audit-attempt1/sender-hashes.json`, `test-cadence-Win32.log`, `test-cadence-x64.log`

Next runtime gate, only after code-only restriction changes and an authorized supported workflow exists: certify actual route/foreground/monitor/VSync, run closed→held GPU-open→closed for at least 30 seconds each, collect game P2P plus source-content serial/age and Hook pre-Original timing, and complete x86/x64/D3D11/ReShade regressions. Preserve all failed evidence. No deployment or game stability claim follows from the current code PASS results.
