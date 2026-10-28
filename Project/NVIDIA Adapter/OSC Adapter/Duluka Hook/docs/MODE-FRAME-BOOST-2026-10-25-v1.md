# Overlay mode transition: maximum-rate frame requests for five seconds

User request: on Overlay mode changes, force continuous maximum-rate frame sending for five seconds. Implementation is SOURCE/BUILD only; no deployment, game interaction, commit or push.

## Behavior

- Desktop↔Hook, classic D3D9 Windowed GPU entry/exit, and GPU_SHARED/GPU_UPLOAD/CPU_GDI renderer transitions start a new 5,000 ms deadline.
- During that window, CEF is requested at its supported maximum 60 FPS and the Desktop Present ceiling temporarily becomes 240 FPS. CEF 73's bundled `cef_browser.h` explicitly documents a maximum windowless rate of 60; actual delivered FPS can be lower.
- A 15 ms HWND timer queues fresh paint requests on CEF's UI thread. At most one boost repaint task is pending. Delayed tasks check the deadline and do not force paints after expiry.
- At expiry, normal configured CEF/Desktop limits are restored; configuration is never rewritten. Existing 10-second transition/visibility maintenance remains separate at its ordinary cadence. Ordinary paints, window movement and visibility maintenance do not extend the five-second boost.
- Frame counters are not advanced by the new timer. Existing actual-pixel publication, shared-handle lifetime, CPU/GPU fallback, visibility gating and publisher fastpath remain intact. An explicitly hidden overlay remains hidden in the game bridge.

Implementation: OSC `src/OscModeFrameBoost.h` and `src/osc_main.cpp`. The helper's deadline is shared atomically between the HWND, CEF and Desktop worker threads. Actual callback/presentation/display throughput remains UNVERIFIED; this change requests fresh frames and cannot guarantee GPU completion or smooth 60 FPS.

## Validation

PASS: `HostPublishCadenceTest` builds and executes for Win32/x64 with assertions enabled. New cases cover inactive limits, active maximum limits, the exact 5,000 ms boundary, restoration, restart on another transition, explicit Stop and preserving a configured Desktop ceiling of 240. Existing publication-cadence assertions also pass.

PASS: OSC Host x64 Release builds in an isolated output directory. Compiler diagnostics are retained in the log. No built executable was launched.

PASS: whitespace checks. Installed OSC Host SHA256 remains `A2A778C40FD84987101637A43046F291FB718301E5BF42E9894AEFFF4C0EA136`.

UNVERIFIED: real-game frame delivery, timer/UI scheduling under load, white-area behavior, actual FPS/1% Low/P95/P99, D3D11/ReShade runtime regression. Previous game measurements do not measure this new build.

Evidence and exact pre-edit source backups: `build_test/NVIDIA ModeFrameBoost_Test/20261025-attempt1/`. See `build-host-attempt1.log`, `build-policy-Win32.log`, `build-policy-x64.log`, `test-policy-Win32.log`, `test-policy-x64.log` and isolated `host/NVIDIA OSC.exe`. No installed binary or game file was changed.
