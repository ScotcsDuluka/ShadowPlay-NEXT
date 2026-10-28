#pragma once
// Optional SAME-PROCESS diagnostic ABI. No OSC frame/mapping ABI changes.
#include <cstddef>
#include <cstdint>

namespace duluka::diagnostics
{
    constexpr std::uint32_t D3D9PresentSamplesVersion = 1;
    constexpr std::uint32_t D3D9PresentSamplesCapacity = 16384;
    constexpr std::uint32_t D3D9PresentKind = 1;
    constexpr std::uint32_t D3D9PresentExKind = 2;
    constexpr std::uint32_t D3D9PresentSamplesAttemptCounterSaturated = 1;

    struct D3D9PresentSampleV1
    {
        std::uint64_t entryQpc;
        std::uint64_t beforeOriginalQpc;
        std::uint64_t afterOriginalQpc;
        std::uint32_t threadId;
        std::uint32_t presentKind;
        std::int32_t result;
        std::uint32_t reserved;
    };

    struct D3D9PresentSamplesSnapshotV1
    {
        std::uint32_t size;
        std::uint32_t version;
        std::uint64_t performanceFrequency;
        std::uint64_t sessionId;
        std::uint64_t beganQpc;
        std::uint64_t stoppedQpc;
        std::uint32_t sampleCount;
        std::uint32_t capacity;
        std::uint64_t attemptedCalls;
        std::uint64_t busyDrops;
        std::uint64_t overflowDrops;
        std::uint64_t reentrantExcluded;
        std::uint64_t unfinishedOrStoppedCalls;
        std::uint32_t flags;
        std::uint32_t reserved;
    };

    static_assert(sizeof(D3D9PresentSampleV1) == 40);
    static_assert(offsetof(D3D9PresentSampleV1, result) == 32);
    static_assert(sizeof(D3D9PresentSamplesSnapshotV1) == 96);
    static_assert(offsetof(D3D9PresentSamplesSnapshotV1, attemptedCalls) == 48);
}

// Optional exports; resolve each with GetProcAddress (older DLLs omit them):
// BOOL WINAPI NvOscAdapterBeginD3D9PresentSamples(DWORD capacity);
// BOOL WINAPI NvOscAdapterStopD3D9PresentSamples();
// BOOL WINAPI NvOscAdapterReadD3D9PresentSamples(
//     D3D9PresentSamplesSnapshotV1* output, DWORD outputBytes,
//     D3D9PresentSampleV1* samples, DWORD sampleBytes);
//
// Disabled by default. Begin accepts [1, D3D9PresentSamplesCapacity] and
// rejects an active session. Stop freezes the session without waiting for
// in-flight Present calls. Read is allowed only after Stop; outputBytes must
// match exactly, sampleBytes must be a multiple of sizeof(sample), and the
// caller buffer must hold sampleCount samples. No partial reads. Exports use
// a nonblocking control lock and can return FALSE/ERROR_BUSY; retry out of band.
// Valid pointers into writable caller-owned memory are required, as with the
// existing same-process diagnostic exports. No remote-process pointer probing.
//
// Samples are stored in completion order; sort by entryQpc per thread when
// analyzing intervals. Only outermost instrumented Present/PresentEx calls
// are sampled. All entered calls are accounted as samples + busy + overflow +
// reentrantExcluded + unfinishedOrStoppedCalls. The last field includes calls
// still in Original Present at Stop and races stopped before accounting.
// Restart uses a new sessionId; stale completions cannot enter a new session.
//
// entryQpc is before foreground/ReShade gates; beforeOriginalQpc and
// afterOriginalQpc surround ONLY Original Present. Entry-to-before includes
// Hook work and optional recorder overhead. Post-Original existing cumulative
// counters/recorder publication are outside these intervals. ReShade-owned
// composition outside the raw Hook callback is NOT measured by these samples.
// No GPU completion/display timing is implied. Isolated fixture distributions
// are NOT real-game FPS or proof of a 60 FPS/240 FPS performance pass.
//
// Counters saturate at UINT32_MAX per session (flags reports attempt-counter
// saturation); use bounded sessions. The fixed sample array never overwrites
// earlier samples: subsequent completed calls are counted as overflowDrops.
