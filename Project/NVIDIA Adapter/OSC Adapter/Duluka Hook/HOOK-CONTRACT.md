# Hook layer contract (D3D10 / D3D11 / D3D12 / OpenGL / Vulkan / D3D9)

This file is the binding contract for every translation unit under `src/Hook/` and `src/Renderer/`.
Read `ARCHITECTURE.md` first. Where the two disagree, `ARCHITECTURE.md` wins.

## 1. Hard rules

1. **One API per translation unit pair.** A hook file never creates resources for another API's device.
2. **No stub .cpp in the DLL build.** If an API is not implemented, its folder keeps only a README.
   A file that compiles but reports success without drawing is worse than no file.
3. **Never claim support in status exports for a path that has not drawn a frame.**
4. **No COM release from `DllMain`.** The loader lock is not a safe place to tear down GPU objects.
5. **No exceptions across the DLL boundary.** Every entry point is `noexcept` in practice: catch nothing,
   propagate nothing, return the previous API result unchanged on failure.
6. **A hook must be removable.** Every installed patch keeps the original pointer and restores it on shutdown.
7. **Build with `/W4 /std:c++20`.** No new warnings. No `/permissive-` violations in new code.

## 2. Presentation ownership (important)

`IDXGISwapChain` and `IDXGISwapChain1` vtables are shared by D3D10, D3D11 and D3D12 swap chains created
against the same adapter/driver. `src/Hook/D3D11/D3D11Hook.cpp` already patches `Present` / `Present1`
once and owns that slot.

Therefore:

- **D3D10 and D3D12 must not patch `Present` / `Present1` again.** Double patching the same slot
  corrupts the original-pointer chain and is a crash source.
- D3D10 and D3D12 instead expose a *presence probe* that the DXGI owner calls from inside its existing
  handler. The probe returns `bool handled` and must be cheap and lock-free when the API is absent.

Required probe signatures (exact, so the call sites can be added mechanically):

```cpp
// src/Hook/D3D10/D3D10Hook.cpp
namespace duluka::d3d10 {
    // Called from the shared DXGI Present handler, after the D3D11 attempt has failed.
    // Returns true only if this swap chain belongs to an ID3D10Device and a frame was processed.
    bool TryPresent(IDXGISwapChain *swapChain, bool gameForeground, bool reshadeAddonRegistered);
    void ShutdownProbe();
}

// src/Hook/D3D12/D3D12Hook.cpp
namespace duluka::d3d12 {
    // Called from the shared DXGI Present handler, before/after the D3D11 attempt.
    // Returns true only if this swap chain belongs to a D3D12 device and a frame was processed.
    bool TryPresent(IDXGISwapChain *swapChain, bool gameForeground, bool reshadeAddonRegistered);
    void ShutdownProbe();
}
```

D3D12 additionally needs its own `ID3D12CommandQueue::ExecuteCommandLists` interception, because a
composition pass must be submitted on a queue the swap chain actually presents from. That hook lives in
`D3D12Hook.cpp` and is installed lazily, from the first D3D12 `Present` probe, not from `DllMain`.

## 3. Renderer contract

Mirror `src/Renderer/D3D11/D3D11Renderer.h`. Each renderer exposes one narrow namespace:

```cpp
namespace duluka::<api>
{
    using LogCallback = void (*)(const char *message);

    struct RendererStatus
    {
        std::uint64_t drawCount = 0;
        std::uint64_t reshadeCallbackCount = 0;
        std::uint64_t lastBridgeFrameNumber = 0;
        bool bridgeConnected = false;
        bool textureReady = false;
        long lastError = 0;
        std::uint32_t swapWidth = 0;
        std::uint32_t swapHeight = 0;
        const char *diagnosticStage = nullptr;
    };

    void Initialize(LogCallback logger);
    void Shutdown();
    RendererStatus GetStatus();
}
```

Plus one API-specific present entry:

- D3D10: `bool OnPresent(ID3D10Device *, ID3D10RenderTargetView *rtv, UINT width, UINT height, bool gameForeground);`
- D3D12: `bool OnPresent(ID3D12Device *, ID3D12CommandQueue *, IDXGISwapChain3 *, bool gameForeground);`
- OpenGL: `bool OnPresent(HDC, bool gameForeground);`
- Vulkan: `bool OnPresent(VkQueue, VkSwapchainKHR, bool gameForeground);`

The renderer owns device resources, synchronization and teardown. The hook owns interception,
lifecycle, thread guards and calling the renderer exactly once per presented frame.

## 4. Frame source

The overlay pixels come from the shared frame bridge in `include/osc_frame_bridge.h`
(CPU snapshot + frame number). Do not invent a second transport. A renderer that has no bridge frame
yet must skip composition and leave the frame untouched; it must not draw a black or stale quad.

## 5. Threading

- Present-side state is per-thread-guarded: a reentrancy depth counter, plus a mutex only around
  resource creation/destruction. Never hold a lock across `Present` itself.
- A renderer is initialized on the first frame that actually needs it, on the presenting thread.
- Resize/swap-chain recreation invalidates device resources; release and recreate, never reuse.

## 6. D3D9

D3D9 does not go through DXGI. `src/Hook/D3D9/D3D9Hook.cpp` owns factory import patching and the
device vtable. D3D9 audit findings and fixes belong to that pair of files plus
`src/Renderer/D3D9/D3D9Renderer.cpp`; do not route D3D9 through the DXGI handler.

## 7. Integration

Each author reports, but does not edit, the exact `NVIDIA OSC Adapter.vcxproj` entries needed:

- `<ClCompile Include="..."/>` lines
- additional `AdditionalDependencies` libraries
- any additional include directories

The project file, the shared `D3D11Hook.cpp` call sites, and the final build are integrated centrally
in one pass so five parallel edits cannot fight over the same file.
