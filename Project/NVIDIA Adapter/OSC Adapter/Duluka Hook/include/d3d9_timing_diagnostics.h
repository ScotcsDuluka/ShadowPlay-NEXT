#pragma once
// Diagnostic ABI only. This header does not change the OSC frame bridge
// or its shared-memory wire layout.
#include <cstdint>

namespace duluka::diagnostics
{
    constexpr std::uint32_t D3D9TimingVersion = 1;

    struct D3D9TimingSnapshotV1
    {
        std::uint32_t size;
        std::uint32_t version;
        std::uint64_t performanceFrequency;
        std::uint64_t presents;
        std::uint64_t draws;
        std::uint64_t overlaySamples;
        std::uint64_t overlayTotalTicks;
        std::uint64_t overlayMaxTicks;
        std::uint64_t gamePresentSamples;
        std::uint64_t gamePresentTotalTicks;
        std::uint64_t gamePresentMaxTicks;
    };

    // Same binary layout for Win32 and x64, independent of pointer size.
    static_assert(sizeof(D3D9TimingSnapshotV1) == 80);
}

// Export provided by the DLL (not a cross-process call):
// BOOL WINAPI NvOscAdapterGetD3D9TimingSnapshot(
//    duluka::diagnostics::D3D9TimingSnapshotV1* out, DWORD outBytes);
// Fields are lifetime-aggregated monotonically; consumers calculate deltas.
// This avoids file I/O and resetting statistics on the game Present thread.
