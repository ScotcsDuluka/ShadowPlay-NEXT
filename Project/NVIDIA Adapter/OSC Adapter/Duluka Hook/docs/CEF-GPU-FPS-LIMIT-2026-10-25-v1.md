# CEF GPU frame-rate ceiling — 2026-10-25, v1

User requested raising the CEF source ceiling. SOURCE/BUILD PASS; no deployment or new live CEF/game run. This supersedes the earlier statement that all CEF 73 paths are limited to 60 and the 60-FPS GPU boost policy in MODE-FRAME-BOOST-2026-10-25-v1.md.

## Pinned upstream implementation evidence

Local SDK identifies CEF `73.1.12+gee4b49f`, commit `ee4b49f311d5081909d0823244708d9ff157354d`.

- [render_widget_host_view_osr.cc, SetFrameRate](https://github.com/chromiumembedded/cef/blob/ee4b49f311d5081909d0823244708d9ff157354d/libcef/browser/osr/render_widget_host_view_osr.cc#L1558-L1604): shared-texture OnAcceleratedPaint uses the requested settings value without the software upper clamp. Software uses ClampFrameRate.
- [osr_util.cc](https://github.com/chromiumembedded/cef/blob/ee4b49f311d5081909d0823244708d9ff157354d/libcef/browser/osr/osr_util.cc): software maximum 60.
- [browser_host_impl.cc](https://github.com/chromiumembedded/cef/blob/ee4b49f311d5081909d0823244708d9ff157354d/libcef/browser/browser_host_impl.cc#L1189-L1209): the getter clamps its return to 60 even when the setter forwards a higher shared-texture setting. Thus getter readback alone cannot validate the accelerated source ceiling.

Header API comments describe a maximum of 60; the implementation has the explicit shared-GPU exception. This source evidence does not establish unique-frame throughput of the installed binary. No SDK or libcef DLL was patched.

## Changes

- `OscDesktopFps.h`: GPU CEF requests follow the Desktop setting up to 240 (75, 120, 144, 165 and 240 no longer become 60). CPU requests retain the supported 60 maximum; invalid configuration falls back to 60.
- Fullscreen Hook GPU source requests 240 independently of the Desktop cap. Hook CPU source stays at 60. Game Present rate is not modified.
- `OscModeFrameBoost.h`: mode changes request GPU 240 / CPU 60 for five seconds, then restore normal policy. Desktop Present retains its maximum of 240 during boost and restores the selected setting afterward.
- `ModeFrameBoostPaintTask`: a single CEF UI delayed-task chain requests fresh paints at a 4 ms GPU / 16 ms CPU request interval. This avoids relying on Win32 SetTimer's minimum interval for high-rate requests. Deadline checks stop delayed boost paints; the HWND timer remains the watchdog/restoration path. Scheduling and actual generated rates are not guaranteed by these request intervals.
- FPS diagnostics preserve legacy `cefFps` getter readback and add `cefRequestedFps` and `cefSharedTexture`. A requested value is not reported as measured FPS.
- Creation and subsequent runtime changes both apply the route-aware ceiling. Renderer fallback transitions re-evaluate the software limit. Existing frame publication, shared-handle ownership and wire ABI remain unchanged. No fake frameNumber increment or external-begin-frame mode was introduced.

The legacy config `frameRate` parser remains unchanged; active source pacing uses the Overlay `maxFps` policy and Hook/boost policy described above.

## Verification and gates

PASS: Win32/x64 HostPublishCadenceTest assertions, including GPU settings 15/30/60/75/120/144/165/240, CPU clamp, invalid settings, Hook GPU/CPU limits, five-second boost boundaries/restoration, task delay policy and existing frame publication cadence.

PASS: OSC Host Release x64 isolated build, exit 0. Compiler diagnostics retained. Existing tests and the newly built Host were not launched against a game.

PASS: scoped whitespace checks; production OSC remains SHA256 `A2A778C40FD84987101637A43046F291FB718301E5BF42E9894AEFFF4C0EA136`.

UNVERIFIED: actual CEF GPU frames/second, unique-frame serial/age, displayed rate, game FPS/1% Low/P95/P99, power/load and D3D11/ReShade runtime behavior at the new source rate. This does not claim the stutter is fixed. Requested 240 is a configured upper target, not proof that 240 new images reach the display.

Evidence: `build_test/NVIDIA CefHighFps_Test/20261025-attempt1/`: exact pre-edit backups, `build-policy-Win32.log`, `build-policy-x64.log`, `test-policy-Win32.log`, `test-policy-x64.log`, `build-host-attempt1.log`, `host/NVIDIA OSC.exe`, `manifest.json`.

Deployment remains explicitly gated. No installed runtime, game, ReShade or CEF DLL was replaced; no commit/push performed.
