# ShadowPlay NEXT — OSC Host DX9 / DX9Ex Frame Publisher, Phase 0

**Owner: OSC Host** — updated 2026-10-24. Source branch: `Stable`.
**Do not deploy to production yet.** Hook receiver/IPC integration is owned by a different chat.

## Resulting Host frame routes

### Classic Direct3D9 (existing, now explicitly synchronized)

`CEF OnAcceleratedPaint → OscD3D11FramePublisher Worker → CPU readback → PublishD3D9FramePixels → Local\NVIDIAOSC.Frame.D3D9.v1 → D3D9 Hook CPU renderer`.

Wire ABI is the **unchanged** `osc_frame_bridge.h::D3D9Frame` version 1. Host uses one mutex to serialize Shared Memory writes and visibility updates, preventing concurrent seqlock writes. `SnapshotFullD3D9Frame` provides a complete BGRA8 snapshot to the optional GPU producer. Partial dirty readbacks are **not** mistakenly treated as complete GPU textures.

### D3D9Ex (new Host producer, disabled by default)

`CEF → Host CPU frame bridge (always retained) → SnapshotFullD3D9Frame → Host D3D11 device on GAME adapter LUID → BGRA8 legacy-shared texture → D3D11 EVENT completion → Host mailbox announces ready → in-game receiver Stage → Commit → ACK`.

- Source: `src/OscFramePublisher/OscD3D9ExFramePublisher`.
- D3D11 producer creates a **separate** `D3D11_RESOURCE_MISC_SHARED` texture, `DXGI_FORMAT_B8G8R8A8_UNORM`, one mip, one sample, render-target and shader-resource flags.
- `IDXGIResource::GetSharedHandle` returns a **legacy DXGI handle**, NOT the DX11 `SHARED_NTHANDLE` / keyed-mutex resource. **Do not CloseHandle on a legacy DXGI handle.**
- Producer device is the existing game-adapter D3D11 mirror device, selected by `ConsumerAdapterInfo` LUID and API=9; Host and game may render on different GPUs.
- The Host worker updates a new immutable GPU resource, ends a D3D11 `QUERY_EVENT`, and polls `GetData(..., D3D11_ASYNC_GETDATA_DONOTFLUSH)` **outside Game Present**. No `readyGeneration` is sent until completion is confirmed.
- CPU shared-memory publication remains active for DX9 **and** DX9Ex. A failing or absent GPU handshake never disables it.
- GPU publication is opt-in using **`OSC_DX9EX_HOST_PREVIEW=1` in the Host process environment**. Defaults to OFF. Enabling the flag alone is insufficient: a real, alive DX9Ex receiver must explicitly advertise the handshake. This has **not** been connected to the live Hook.

## Proposed Host → Hook mailbox (versioned, separate from CPU v1 and D3D11 v3)

**Name:** `Local\NVIDIAOSC.Frame.D3D9Ex.Gpu.v1` (Host C++ constant `MailboxName`).
**Header:** `OscD3D9ExFramePublisher.h`, 104 bytes, 8-byte packing and alignment.

### Fields / ownership

| Field | Writer | Meaning |
|---|---|---|
| `magic/version` | Host | `0x4F584739 / 1`; reject mismatches |
| `sequence` | Host | Even/stable double-read seqlock for producer metadata |
| `producerPid` | Host | Host process identity, verify process alive |
| `consumerPid` | Trusted in-game connector | Game process ID |
| `consumerCapabilities` | Connector | Bit 0 = D3D9Ex Stage/Commit supported |
| `consumerAdapterLuid` | Connector | 64-bit packed LUID, must match game adapter |
| `targetAdapterLuid` | Host | D3D11 producer device LUID |
| `generation` | Host | Offered resource generation, nonzero |
| `readyGeneration` | Host | Set to offered generation **only after GPU completion** |
| `stagedGeneration` | Connector | ACK successful Stage call |
| `committedGeneration` | Connector | ACK successful Commit call |
| `releasedGeneration` | Connector | ACK previous committed alias fully retired |
| `legacySharedHandle` | Host | 64-bit stored legacy handle value; NOT NT handle |
| `width/height/format` | Host | BGRA8, 1..8192, bounded to 64 MiB |

### Mandatory receiver algorithm (Hook-team work, not yet implemented)

1. In a trusted, same-game-process connector, open the Host mailbox; verify magic/version, Host PID/liveness and consumer adapter identity. Register consumer PID, LUID and Stage/Commit capability. **Never pass an in-process `IDirect3DDevice9Ex*` pointer through IPC.**
2. Snapshot Host metadata using `sequence` even-before/even-after checks. Ignore generation 0, stale generations, mismatched LUID/PID/format/size, or `readyGeneration != generation`. The in-game connector must use the *actual D3D9Ex device on a legal device thread*.
3. Call `NvOscAdapterStageD3D9ExGpuFrame(deviceEx, legacySharedHandle, width, height, generation)`. On success write `stagedGeneration=generation` using `InterlockedExchange64`. On failure, do not acknowledge. Keep last committed frame/CPU fallback.
4. Call `NvOscAdapterCommitD3D9ExGpuFrame(deviceEx, generation)` when Host ready is confirmed. On success write `committedGeneration=generation`.
5. On completion of the replacement, old Hook alias must be truly released before writing `releasedGeneration=oldGeneration`. Otherwise the Host must retain old texture. The Host producer keeps at most one pending + one active + one retiring texture and refuses new offers while waiting for release ACK.
6. On device Reset/Hide/producer exit: drop GPU alias and use the still-live CPU v1 fallback. Driver thread ownership must be respected; **never block Game Present waiting for the Host**.

A raw handle or mailbox is **not** a COM pointer and should not be treated as authenticated communication. This shared-memory preview currently has **no complete trust/authentication or producer-restart epoch design**, and must not be enabled for arbitrary untrusted processes or released to end users until hardened.

## Validation

- Native `NVIDIA OSC` Release x64 **isolated build passed** with the new module and DX9 CPU bridge changes.
- `tests/HostD3D9ExPublisherTest.vcxproj` and `HostD3D9ExPublisherTest.cpp`: isolated **Win32 + x64 fixtures passed** (binaries under `build_test/HostD3D9ExPublisherTest/bin/<Platform>`). The tests validated:
  - Host BGRA8 GPU texture and opening a Legacy Shared Handle;
  - pixels `0xFF123456` after D3D11 completion query;
  - generation cannot be replaced before Commit ACK;
  - generation replacement blocked while a retired alias lacks Release ACK;
  - publication resumes after release ACK.
- Both fixtures **simulate receiver acknowledgements inside their own process**. No real Hook game callback, no cross-process x86-to-x64 IPC integration, no foreground game test, no sustained throughput benchmark, and no production Host deploy were performed.

## Required next gates

1. Hook team implements the trusted, game-process mailbox connector and the stage/commit/retire acknowledgements; review and agree on this wire schema before relying on it.
2. Test x86 and x64 cross-process receiver independently, LUID mismatch/restart/resize/fault cases, and D3D9Ex device thread ownership.
3. Measure sustained GPU upload time, high-FPS dirty frames, OSR frame consistency and nonblocking Present behavior in controlled game tests.
4. Decide on more secure IPC/session epoch and resource ownership; do **not** enable GPU publishing just because a single fixture passes.

## Addendum — receiver completed, Host cross-process integration verified

The earlier `Receiver not connected yet` status above is historical. On
2026-10-24 the Hook team supplied its actual mailbox Pump receiver and
x64/x86 DLL build. The Host side now has a cross-process test using the
**actual Host producer module**, which passes against that Hook receiver
for x64 and x86. Both CPU-upload and GPU-source CopyResource transports
pass. See `HOST-DX9EX-IPC-DIRECT-GPU-20261024.md` for proof, exact
commands and remaining CEF/live-game limits. The direct GPU-from-CEF
route still needs actual Host frame callback and game-thread integration.
