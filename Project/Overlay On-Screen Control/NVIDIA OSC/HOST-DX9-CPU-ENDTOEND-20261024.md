# OSC Host — DX9 CPU Bridge Step 1 (2026-10-24)

**Scope:** DX9 first, before DX9Ex and D3D11. Branch `Stable`. No changes to the Hook source, production DLL or live OSC runtime during this pass.

## Verified on local Windows GPU hardware

The newly added Host-owned fixture `tests/HostD3D9CpuCrossProcessTest.cpp` compiles the **actual** `OscD3D9FramePublisher.cpp` Host source. It runs an x64 Host producer in a separate Windows process with a unique, private CPU `D3D9Frame` v1 mapping and an unrelated consumer process that loads the **actual installed** Duluka Hook DLL. The consumer creates a real D3D9 device and calls `NvOscAdapterRenderD3D9ForTest` to draw the producer's BGRA8 frame to an actual D3D9 backbuffer.

The receiver checks:
- Mapping v1 magic/version, even seqlock, nonzero frame generation, correct owner PID and dimensions.
- Pixel orientation: opaque green top, opaque red bottom.
- Alpha blending at the center on a real backbuffer against a known background.

| Actual producer → installed Hook DLL | Result |
|---|---|
| Host x64 → Hook x64 → D3D9 backbuffer | **PASS**, process exit 0 |
| Host x64 → Hook Win32 → D3D9 backbuffer | **PASS**, process exit 0 |

Test binaries are under `build_test/HostD3D9CpuCrossProcess/bin/x64` and `.../Win32`. Test project: `tests/HostD3D9CpuCrossProcessTest.vcxproj` (both platforms compile).

Existing native tests against the installed Hook DLL already passed 10/10 cases across Win32/x64, including D3D9 present interception, Reset, cooperative late attach, and DX9Ex GPU legacy sharing; separate DX9Ex IPC checks passed 6/6.

## Fixture-only improvements and known boundaries

- Host's `OscD3D9FramePublisher.cpp` now optionally accepts `NVIDIA_OSC_HOST_D3D9_FRAME_MAPPING_NAME` to target a **private test mapping**. Default in production remains `Local\\NVIDIAOSC.Frame.D3D9.v1`, ABI/layout unchanged. No production config changed.
- Corrected a 32-bit build warning: the 64MiB mapping size is cast to `std::uint64_t` before shifting right 32 for the mapping size high dword. This does not alter x64 behavior.
- The **first iteration** of this fixture loaded the Hook **after** creating the D3D9 device and hung when unloading DLL from an unregistered late-attach state. This was a fixture teardown issue, not a failed readback or pixel copy. The corrected fixture loads Hook before device creation (same as Hook's normal smoke test), unloads cleanly, and both cross-process runs exit successfully. Late attach remains separately tested by Hook's cooperative late-attach suite.
- A real accelerated CEF paint and OSC main menu rendered **inside a foreground DX9 game** have **not yet** been tested end-to-end. This fixture uses known synthetic BGRA8 source pixels through the *real Host CPU publisher module*.
- No sustained frame pacing, p95/p99, ReShade coexistence, or in-game alpha/ResizeBuffers stress test was collected.

## Build validation

- Host x64 isolated Release build: **PASS**, `build_test/NVIDIA OSC_Test/NVIDIA OSC_Test.exe` (test-only).
- `git diff --check`: **PASS**. Worktree has changes from parallel subsystem agents. No Git commit performed.
- Live Release from earlier combined Host+Hook deployment still has `DEPLOY_SUCCESS` and OSC main + CEF renderer/GPU processes were present during verification. No repeat deploy or restart performed.

## Next ordered checks

1. Controlled foreground DX9 test with **actual OSC CEF content**, CPU v1 bridge active: confirm pixels, visibility, interaction, Reset and game frame pacing.
2. After classic DX9 succeeds in-game, separately enable **DX9Ex GPU mailbox** on a trusted game-owned device thread; confirm Stage/Commit/Release plus fallback/reset.
3. Last, verify D3D11 GPU_SHARED with Dirty-only cadence and Hook timing snapshots (5-minute menu idle, p95/p99).
