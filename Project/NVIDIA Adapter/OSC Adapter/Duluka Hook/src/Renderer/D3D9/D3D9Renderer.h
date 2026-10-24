#pragma once
#include <windows.h>
#include <d3d9.h>
#include <cstdint>
namespace duluka::d3d9
{
    void OnPresent(IDirect3DDevice9 *device);
    void OnReShadePresent(IDirect3DDevice9 *device,
        IDirect3DSurface9 *backBuffer, bool gameForeground);
    void OnReset(IDirect3DDevice9 *device = nullptr);
    void RenderForTest(IDirect3DDevice9 *device);
    // Explicit, opt-in GPU frame (D3D9Ex legacy shared handle).
    // The publisher owns the resource and must synchronize writes.
    bool BindExSharedGpuTexture(IDirect3DDevice9Ex *device, HANDLE sharedHandle,
        std::uint32_t width, std::uint32_t height, std::uint64_t frameNumber);
    bool UnbindExSharedGpuTexture(IDirect3DDevice9Ex *device);
    // Stage a resource without presenting it; commit ONLY after the
    // producer has completed all GPU writes and retained its lifetime.
    bool StageExSharedGpuTexture(IDirect3DDevice9Ex *device, HANDLE sharedHandle,
        std::uint32_t width, std::uint32_t height, std::uint64_t frameNumber);
    bool CommitExSharedGpuTexture(IDirect3DDevice9Ex *device,
        std::uint64_t frameNumber);
    bool DiscardExPendingGpuTexture(IDirect3DDevice9Ex *device);
    std::uint64_t GetDrawCount();
    // Read-only diagnostic state. Does not change the existing shared-memory
    // wire layouts or drive Host GPU selection.
    void GetAdapterStatus(std::uint64_t &adapterLuid, bool &isD3D9Ex,
        bool &gpuFrameBound, std::uint64_t &busyFrameSkips);
    void Shutdown();
}
