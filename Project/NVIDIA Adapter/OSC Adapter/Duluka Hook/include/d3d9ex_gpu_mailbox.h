#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d9.h>
#include <cstddef>
#include <cstdint>

namespace duluka::d3d9ex_mailbox
{
    constexpr wchar_t Name[] = L"Local\\NVIDIAOSC.Frame.D3D9Ex.Gpu.v1";
    constexpr wchar_t NameEnvironment[] =
        L"NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_NAME";
    constexpr wchar_t PreviewEnvironment[] =
        L"NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW";
    constexpr std::uint32_t Magic = 0x4F584739;
    constexpr std::uint32_t Version = 1;
    constexpr std::uint32_t Bgra8Format = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
    constexpr LONG StageCommitCapability = 1;

#pragma pack(push, 8)
    struct alignas(8) Mailbox
    {
        std::uint32_t magic, version;
        volatile LONG sequence, producerPid;
        volatile LONG consumerPid, consumerCapabilities;
        volatile LONG64 consumerAdapterLuid;
        volatile LONG64 targetAdapterLuid;
        volatile LONG64 generation;
        volatile LONG64 readyGeneration;
        volatile LONG64 stagedGeneration;
        volatile LONG64 committedGeneration;
        volatile LONG64 releasedGeneration;
        std::uint64_t legacySharedHandle;
        std::uint32_t width, height, format, reserved;
    };
#pragma pack(pop)
    static_assert(sizeof(Mailbox) == 104);
    static_assert(offsetof(Mailbox, consumerAdapterLuid) == 24);
    static_assert(offsetof(Mailbox, legacySharedHandle) == 80);

    // Must be called explicitly by a trusted connector on a legal game
    // D3D9Ex device thread; NEVER called from HookPresent/PresentEx.
    // Success means that a generation was committed or old alias retired,
    // not that the producer's GPU write was independently verified here.
    bool Pump(IDirect3DDevice9Ex *device);
    void OnDeviceReset(IDirect3DDevice9 *device);
    void Shutdown();
}
