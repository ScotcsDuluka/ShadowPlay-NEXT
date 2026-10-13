#pragma once

#include <windows.h>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cwchar>

namespace osc_frame_bridge
{
    inline constexpr wchar_t MappingName[] = L"Local\\NVIDIAOSC.Frame.v2";
    inline constexpr wchar_t MappingNameEnvironmentVariable[] =
        L"NVIDIA_OSC_ADAPTER_BRIDGE_NAME";
    inline constexpr wchar_t ConsumerAdapterMappingName[] =
        L"Local\\NVIDIAOSC.Frame.ConsumerAdapter.v1";
    inline constexpr wchar_t ConsumerAdapterMappingNameEnvironmentVariable[] =
        L"NVIDIA_OSC_ADAPTER_IDENTITY_MAPPING_NAME";
    inline constexpr std::uint32_t Magic = 0x4F534346;
    inline constexpr std::uint32_t Version = 2;
    inline constexpr std::uint32_t ConsumerAdapterMagic = 0x4F534341;
    inline constexpr std::uint32_t ConsumerAdapterVersion = 1;
    inline constexpr std::size_t MaxFrameBytes = 64ull * 1024 * 1024;

    inline bool IsGameOrNvidiaOverlayForeground(std::uint32_t gameProcessId)
    {
        if (!gameProcessId)
            return false;

        static std::atomic<std::uint32_t> lastForegroundGameProcessId{ 0 };
        static std::atomic<std::uint64_t> foregroundCache{ 0 };
        DWORD foregroundProcessId = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), &foregroundProcessId);
        if (foregroundProcessId == gameProcessId)
        {
            lastForegroundGameProcessId.store(gameProcessId,
                std::memory_order_release);
            return true;
        }

        const std::uint64_t cached = foregroundCache.load(std::memory_order_acquire);
        bool foregroundIsNvidiaOverlay = false;
        if (foregroundProcessId == static_cast<DWORD>(cached))
        {
            foregroundIsNvidiaOverlay = (cached >> 32) != 0;
        }
        else
        {
            bool isNvidiaOverlay = false;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,
                FALSE, foregroundProcessId);
            if (process)
            {
                wchar_t imagePath[MAX_PATH]{};
                DWORD pathLength = ARRAYSIZE(imagePath);
                if (QueryFullProcessImageNameW(process, 0, imagePath, &pathLength))
                {
                    const wchar_t *imageName = std::wcsrchr(imagePath, L'\\');
                    imageName = imageName ? imageName + 1 : imagePath;
                    isNvidiaOverlay =
                        _wcsicmp(imageName, L"NVIDIA Overlay.exe") == 0;
                }
                CloseHandle(process);
            }
            foregroundCache.store(
                foregroundProcessId |
                    (static_cast<std::uint64_t>(isNvidiaOverlay) << 32),
                std::memory_order_release);
            foregroundIsNvidiaOverlay = isNvidiaOverlay;
        }
        if (!foregroundIsNvidiaOverlay)
        {
            lastForegroundGameProcessId.store(0, std::memory_order_release);
            return false;
        }
        if (lastForegroundGameProcessId.load(std::memory_order_acquire) !=
            gameProcessId)
            return false;

        HANDLE gameProcess = OpenProcess(SYNCHRONIZE, FALSE, gameProcessId);
        if (!gameProcess)
            return false;
        const bool gameIsRunning = WaitForSingleObject(gameProcess, 0) == WAIT_TIMEOUT;
        CloseHandle(gameProcess);
        return gameIsRunning;
    }

    struct ConsumerAdapterInfo
    {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG ownerProcessId;
        volatile LONG64 adapterLuid;
    };

    struct Frame
    {
        std::uint32_t magic;
        std::uint32_t version;
        volatile LONG sequence;
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t processId;
        volatile LONG ownerProcessId;
        std::uint64_t sharedHandle;
        std::uint64_t frameNumber;
        std::uint8_t unused[1];
    };

    inline constexpr std::size_t MappingBytes =
        offsetof(Frame, unused) + MaxFrameBytes;
}
