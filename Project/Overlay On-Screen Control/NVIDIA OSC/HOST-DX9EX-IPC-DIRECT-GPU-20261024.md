# OSC Host — DX9Ex IPC + Direct GPU Producer Verification
**Date:** 2026-10-24, branch `Stable`. **Owner:** OSC Host. **Not deployed or committed.**

## Verification completed with a real Host publisher and real Hook receiver

Earlier Hook IPC tests used a synthetic producer. This follow-up built
**the actual `OscD3D9ExFramePublisher.cpp` into an independent x64
producer process**. The receiver was the unchanged, staged Hook build:
`Build/DulukaHook-DX9Ex-Mailbox-Phase01-20261024`, including real
`NVIDIA OSC Adapter Hook.dll` x64/x86 and the receiver's IPC fixture.

All **four separate-process** combinations passed:

| Host producer route | Hook x64 | Hook x86 |
|---|---|---|
| CPU BGRA8 -> D3D11 Upload -> legacy GPU texture | PASS | PASS |
| D3D11 texture -> **GPU CopyResource** -> legacy GPU texture | PASS | PASS |

- Validated the actual Hook mailbox Pump, generation Stage/Commit,
  previous resource Release ACK, and overlay pixel values in the **D3D9Ex
  test backbuffer**.
- The Host's Direct GPU method is `TryPublishGpuTexture()`. It does
  **not** map/readback the source GPU texture to the CPU. It refuses
  a foreign D3D11 device, mismatched adapter LUID, unsupported
  format/size, missing trusted receiver capability, and pending
  resource retirement.
- The CPU method `TryPublish()` remains operational.
- The Host's `Withdraw()` publishes Offer=0 and keeps the last GPU
  resource alive until the receiver has actually released its alias.
- `OSC_DX9EX_HOST_PREVIEW=1` is still required for this **opt-in** host
  protocol. The test runner sets a private mailbox name through
  `NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME`, so **no production mailbox was
  used**. The Hook fixture also sets its own receiver preview flag.

### Reproduction

Build the Host's isolated x64 process from:

`Project/Overlay On-Screen Control/NVIDIA OSC/tests/DX9ExHostActualPublisher.vcxproj`

The output is:
`.../NVIDIA OSC/build_test/DX9ExHostCrossProcessTest/bin/x64/DX9ExHostActualPublisher.exe`.

Run the staged Hook IPC fixture x64 or x86 with two arguments:
`"<matching Hook DLL>" "<Host x64 producer EXE>"`.

For the direct-GPU source route set
`OSC_HOST_TEST_DIRECT_GPU=1` in the **fixture parent**
environment; omit it to exercise the original CPU-upload path. The
producer fixture uses an actual D3D11 source texture with known pixels
and the real Host C++ publisher, but does **not** start CEF.

The Hook fixture requests a test-only intentionally wrong LUID for
the second generation. The Host test subprocess temporarily corrupts
**only its private test mailbox** to let the unchanged Hook fixture
verify rejection. Production Host never intentionally publishes
a mismatched LUID.

## What is and is not integrated

**Integrated and proven:** Host Publisher <-> Hook Receiver module
across Windows processes, both 32-bit and 64-bit consumers, game-adapter
LUID checks, Stage/Commit/Release ACK and test backbuffer correctness.

**Still incomplete:** CEF's real OnAcceleratedPaint ->
`TryPublishGpuTexture` scheduling; real game's legally owned D3D9Ex
device-thread call to `NvOscAdapterPumpD3D9ExGpuMailbox`;
render/alpha correctness with actual OSC animation, ongoing
GPU synchronization/throughput, Reset/resize/restart stress, a
cross-GPU **GPU-only** path, and any live deployment.

**Important distinction:** The new direct-copy producer **supports**
GPU-only copies for a compatible same-device source. The main OSC
`OscD3D11FramePublisher` still drives the CPU bridge, so a live
CEF-to-Hook GPU-only route is **not yet enabled or verified**.

The existing `D3D9Frame` v1 CPU bridge, D3D11 v3 texture contract and
Hook sources were not modified by this Host work. No real game or
production overlay process was restarted, killed, or overwritten.
