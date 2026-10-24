#pragma once
// Additive D3D9/D3D9Ex pipeline timing. Does not change old ABI or shared mapping.
#include <cstdint>
namespace duluka::diagnostics
{
    constexpr std::uint32_t D3D9PipelineTimingVersion = 1;
    struct D3D9PipelineTimingSnapshotV1
    {
        std::uint32_t size;
        std::uint32_t version;
        std::uint64_t performanceFrequency;
        std::uint64_t snapshotSamples;
        std::uint64_t snapshotTotalTicks;
        std::uint64_t snapshotMaxTicks;
        std::uint64_t snapshotCacheHits;
        std::uint64_t cpuFrameCopiedBytes;
        std::uint64_t uploadAttempts;
        std::uint64_t uploadTotalTicks;
        std::uint64_t uploadMaxTicks;
        std::uint64_t uploadedBytes;
        std::uint64_t compositionSamples;
        std::uint64_t compositionTotalTicks;
        std::uint64_t compositionMaxTicks;
        std::uint64_t cpuDraws;
        std::uint64_t gpuExDraws;
    };
    static_assert(sizeof(D3D9PipelineTimingSnapshotV1) == 128);
}
// NvOscAdapterGetD3D9PipelineSnapshot(out, sizeof(*out))
// Poll from a non-render thread. Counters are monotonic. CPU QPC timing only.
