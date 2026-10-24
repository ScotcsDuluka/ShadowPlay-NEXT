# ShadowPlay NEXT — Duluka Hook DX9Ex GPU Mailbox Receiver (Phase 0.1)

**Date:** 2026-10-24. **Owner:** Duluka Hook chat. **Branch:** Stable.
**Status:** x64/x86/cross-bitness synthetic-Host IPC fixture PASSED;
**NOT DEPLOYED and NOT AUTO-ENABLED**.

## Source & ABI

- `include/d3d9ex_gpu_mailbox.h` duplicates the **exact 104-byte v1 wire
  layout** from OSC Host `OscD3D9ExFramePublisher.h`. Offsets 24/80,
  magic `0x4F584739`, version 1. The existing CPU Shared Memory v1,
  ConsumerAdapterInfo v2 and D3D11 v3 layouts were NOT changed.
- `src/Hook/D3D9/D3D9ExGpuMailbox.cpp` implements the consumer.
- `BOOL WINAPI NvOscAdapterPumpD3D9ExGpuMailbox(IDirect3DDevice9Ex* device)`
  is a **same-game-process, opt-in** entry point. It must be called
  explicitly by a trusted integrator **on the legally owning D3D9Ex
  device thread, outside Present**. A COM device pointer is NEVER
  sent through the mailbox or cross-process.
- `NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW=1` must be set
  in the receiving game process before calling Pump; default OFF.
  `NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_NAME` optionally chooses a
  private mailbox for isolated testing; production default is
  `Local\\NVIDIAOSC.Frame.D3D9Ex.Gpu.v1`.
- Host still requires its independent opt-in
  `OSC_DX9EX_HOST_PREVIEW=1` and actual DX9Ex game identity.
- Pump returns TRUE on successfully committing/retiring a frame,
  FALSE otherwise. FALSE includes "no frame ready", "not connected"
  and invalid messages. No waiting for Host query/fence occurs in Hook.

## Receiver algorithm

1. Open mapped mailbox only when opt-in enabled; verify header,
   non-self Host producer PID and liveness without a blocking wait.
2. Obtain the **actual game's D3D9Ex adapter LUID**, then register
   Consumer PID + capability bit 0 + LUID. Reject stealing a mailbox
   claimed by another consumer.
3. Read metadata with the producer's even/even seqlock; require a
   nonzero generation, `readyGeneration==generation`, matching game
   LUID, BGRA8 format 87, bounded dimensions/bytes and a nonzero
   legacy shared handle. On x86 reject handles with nonzero top 32 bits.
4. Stage resource with `StageExSharedGpuTexture`, double-check that
   Host still offers the same ready generation, then write
   `stagedGeneration` via `InterlockedExchange64`.
5. Commit via `CommitExSharedGpuTexture` on the same game device
   thread, ACK `committedGeneration`, then write
   `releasedGeneration=previousGeneration` only **after Commit has
   released the previous alias**.
6. On Offer=0 or game Reset, unbind old GPU alias and write Release
   ACK; return to CPU Shared Memory fallback if CPU publisher exists.
7. On Host death: discard GPU alias and disconnect. On PID/producer
   turnover, do not send OLD generation ACKs into the next epoch.

**Caveats:** This v1 mailbox is a preview, not a secure authenticated
IPC protocol. No cryptographic authorization/session epoch or robust
PID-reuse protection. Never enable for arbitrary game processes.
The Hook trusts Host's Ready claim; it does not independently check
the D3D11 completion query. The Host must keep the committed GPU
resource alive until Release ACK. The D3D9Ex game device is generally
thread-affine: NEVER invoke Stage/Pump on a foreign worker thread
without a safe D3D9 device threading contract. No heavy Stage is
called from the Hook's Present/PresentEx callbacks.

## Cross-process validation

`tests/d3d9ex_mailbox_ipc_test.cpp` is a synthetic GPU publisher
running in a **SEPARATE OS process**. It uses a unique, private
per-PID mailbox and the same legacy BGRA8 texture + metadata ABI as
the real Host. It verifies:
- x64 Publisher → x64 Hook: PASS
- x86 Publisher → x86 Hook: PASS
- x64 Publisher → **x86 Hook**: PASS
- Unready generation rejected before Ready without Stage/Commit ACK.
- Wrong GPU LUID rejected without Stage/Commit ACK.
- 1st GPU frame 0x123456 and 2nd GPU frame 0x789ABC read back from
  the D3D9Ex Backbuffer in the game fixture.
- Previous generation's Release ACK only after successor Commit.
- Final Offer=0 retires alias and ACKs last generation.
- The fixture owns and cleans up its synthetic producer child. No
  running game, production Host, NVIDIA Config, GPU selection, or
  live CrossIntegrity binary is touched.

**Not yet proved:** the actual OSC Host/CEF Producer calling through
a trusted device-thread connector, real game Present composition,
automatic late attach, ReShade coexistence, safe dynamic unload,
continuous high-frame-rate latency and fully secure publisher
restart/authorization. **Not game-ready, not deployed.**
