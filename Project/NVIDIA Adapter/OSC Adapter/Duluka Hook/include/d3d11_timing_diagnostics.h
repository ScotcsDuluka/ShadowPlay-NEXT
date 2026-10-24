#pragma once
#include <cstdint>

// Read-only, per-process D3D11 telemetry. Query from a NON-render thread.
// No bridge ABI / DLL loader / DXGI vtable layout is affected.
namespace duluka::d3d11_timing {
inline constexpr std::uint32_t Version = 1;
struct alignas(8) Snapshot {
    std::uint32_t size = sizeof(Snapshot);
    std::uint32_t version = Version;
    std::uint64_t presentCallbacks = 0;
    std::uint64_t busyRenderSkips = 0;
    std::uint64_t recursiveCallbacks = 0;
    std::uint64_t overlayDraws = 0;
    std::uint64_t reshadeCallbacks = 0;
    std::uint64_t bridgeFrameNumber = 0;
    std::uint64_t overlayCacheCopies = 0;
    std::uint64_t backBufferRtvCreated = 0;
    std::uint64_t backBufferRtvReused = 0;
    std::uint64_t suppressedRenderLogs = 0;
    std::uint64_t overlayCallbackTotalTicks = 0;
    std::uint64_t overlayCallbackMaxTicks = 0;
    std::uint64_t overlayCallbackSamples = 0;
    std::uint64_t gamePresentTotalTicks = 0;
    std::uint64_t gamePresentMaxTicks = 0;
    std::uint64_t gamePresentSamples = 0;
    std::uint64_t reshadeCallbackTotalTicks = 0;
    std::uint64_t reshadeCallbackMaxTicks = 0;
    std::uint64_t reshadeCallbackSamples = 0;
};
static_assert(sizeof(Snapshot) == 160);
} // namespace duluka::d3d11_timing
// BOOL WINAPI NvOscAdapterGetD3D11TimingSnapshot(
//     duluka::d3d11_timing::Snapshot* output, uint32_t bytes);
// Caller MUST set bytes=sizeof(Snapshot), called off render thread.
