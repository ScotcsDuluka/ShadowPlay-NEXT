#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <d3d11.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "OscD3D9FramePublisher.h"
#include "../../../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"

namespace osc {
namespace d3d9_frame_publisher {
namespace {
HANDLE g_d3d9_frame_mapping = nullptr;
osc_frame_bridge::D3D9Frame* g_d3d9_frame = nullptr;
LONG64 g_d3d9_frame_number = 0;
// Both the publish worker and UI visibility path modify one seqlock.
std::mutex g_d3d9_frame_write_mutex;
VisibilityProvider g_visibility_provider = nullptr;
void LogPublisher(const char* message) {
    ::Log(message);
}
void LogPublisher(const std::string& message) {
    ::Log(message.c_str());
}
static bool EnsureD3D9FrameMapping() {
    if (g_d3d9_frame)
        return g_d3d9_frame->magic == osc_frame_bridge::D3D9FrameMagic &&
            g_d3d9_frame->version == osc_frame_bridge::D3D9FrameVersion;

    const DWORD sizeHigh = static_cast<DWORD>(
        static_cast<std::uint64_t>(osc_frame_bridge::D3D9FrameMappingBytes) >> 32);
    const DWORD sizeLow = static_cast<DWORD>(
        osc_frame_bridge::D3D9FrameMappingBytes & 0xFFFFFFFF);
    // Isolated producer fixtures may supply a private mapping. Production
    // still uses the unchanged versioned CPU bridge mapping name.
    wchar_t testMappingName[128]{};
    const DWORD testMappingLength = GetEnvironmentVariableW(
        L"NVIDIA_OSC_HOST_D3D9_FRAME_MAPPING_NAME",
        testMappingName, static_cast<DWORD>(std::size(testMappingName)));
    if (testMappingLength >= std::size(testMappingName))
        return false;
    const wchar_t* mappingName = testMappingLength
        ? testMappingName : osc_frame_bridge::D3D9FrameMappingName;
    SetLastError(ERROR_SUCCESS);
    g_d3d9_frame_mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        sizeHigh, sizeLow, mappingName);
    if (!g_d3d9_frame_mapping) {
        LogPublisher("[d3d9-bridge] CreateFileMappingW failed: " +
            std::to_string(GetLastError()));
        return false;
    }

    const bool created = GetLastError() != ERROR_ALREADY_EXISTS;
    g_d3d9_frame = static_cast<osc_frame_bridge::D3D9Frame*>(
        MapViewOfFile(g_d3d9_frame_mapping, FILE_MAP_WRITE, 0, 0,
            osc_frame_bridge::D3D9FrameMappingBytes));
    if (!g_d3d9_frame) {
        LogPublisher("[d3d9-bridge] MapViewOfFile failed: " +
            std::to_string(GetLastError()));
        CloseHandle(g_d3d9_frame_mapping);
        g_d3d9_frame_mapping = nullptr;
        return false;
    }

    if (created || g_d3d9_frame->magic != osc_frame_bridge::D3D9FrameMagic ||
        g_d3d9_frame->version != osc_frame_bridge::D3D9FrameVersion) {
        g_d3d9_frame->sequence = 0;
        g_d3d9_frame->ownerProcessId = 0;
        g_d3d9_frame->width = 0;
        g_d3d9_frame->height = 0;
        g_d3d9_frame->rowPitch = 0;
        g_d3d9_frame->pixelBytes = 0;
        g_d3d9_frame->frameNumber = 0;
        g_d3d9_frame->version = osc_frame_bridge::D3D9FrameVersion;
        std::atomic_thread_fence(std::memory_order_release);
        g_d3d9_frame->magic = osc_frame_bridge::D3D9FrameMagic;
        g_d3d9_frame_number = 0;
    }

    const LONG currentProcessId = static_cast<LONG>(GetCurrentProcessId());
    LONG ownerProcessId = InterlockedCompareExchange(
        &g_d3d9_frame->ownerProcessId, currentProcessId, 0);
    if (ownerProcessId != 0 && ownerProcessId != currentProcessId) {
        HANDLE ownerProcess = OpenProcess(SYNCHRONIZE, FALSE,
            static_cast<DWORD>(ownerProcessId));
        const DWORD openError = ownerProcess ? ERROR_SUCCESS : GetLastError();
        const bool ownerExited = ownerProcess
            ? WaitForSingleObject(ownerProcess, 0) == WAIT_OBJECT_0
            : openError == ERROR_INVALID_PARAMETER;
        if (ownerProcess) CloseHandle(ownerProcess);
        if (!ownerExited ||
            InterlockedCompareExchange(&g_d3d9_frame->ownerProcessId,
                currentProcessId, ownerProcessId) != ownerProcessId) {
            LogPublisher("[d3d9-bridge] another OSC process owns the pixel endpoint");
            UnmapViewOfFile(g_d3d9_frame);
            CloseHandle(g_d3d9_frame_mapping);
            g_d3d9_frame = nullptr;
            g_d3d9_frame_mapping = nullptr;
            return false;
        }
    }
    LogPublisher("[d3d9-bridge] CPU pixel endpoint ready");
    return true;
}
} // namespace
void SetD3D9FrameVisibility(bool visible) {
    std::lock_guard<std::mutex> writeGuard(g_d3d9_frame_write_mutex);
    if (!g_d3d9_frame ||
        g_d3d9_frame->magic != osc_frame_bridge::D3D9FrameMagic ||
        g_d3d9_frame->version != osc_frame_bridge::D3D9FrameVersion)
        return;
    InterlockedIncrement(&g_d3d9_frame->sequence);
    InterlockedExchange64(&g_d3d9_frame->frameNumber,
        visible ? g_d3d9_frame_number : 0);
    std::atomic_thread_fence(std::memory_order_release);
    InterlockedIncrement(&g_d3d9_frame->sequence);
}

bool PublishD3D9FramePixels(const std::uint8_t* source,
    UINT sourceRowPitch, const std::vector<D3D11_BOX>& dirtyBoxes,
    UINT width, UINT height, bool fullFrame) {
    std::lock_guard<std::mutex> writeGuard(g_d3d9_frame_write_mutex);
    if (!source || !width || !height ||
        static_cast<std::uint64_t>(width) * height * 4 >
            osc_frame_bridge::MaxFrameBytes ||
        sourceRowPitch < width * 4 ||
        !EnsureD3D9FrameMapping())
        return false;

    const bool dimensionsChanged =
        g_d3d9_frame->width != width || g_d3d9_frame->height != height ||
        g_d3d9_frame->rowPitch != width * 4;
    if (dimensionsChanged && !fullFrame) {
        LogPublisher("[d3d9-bridge] waiting for full frame after a size change");
        return false;
    }

    InterlockedIncrement(&g_d3d9_frame->sequence);
    if (fullFrame) {
        const std::size_t rowBytes = static_cast<std::size_t>(width) * 4;
        auto* destination = g_d3d9_frame->pixels;
        for (UINT row = 0; row < height; ++row)
            std::memcpy(destination + static_cast<std::size_t>(row) * rowBytes,
                source + static_cast<std::size_t>(row) * sourceRowPitch, rowBytes);
    } else {
        const std::size_t destinationPitch = static_cast<std::size_t>(width) * 4;
        for (const D3D11_BOX& box : dirtyBoxes) {
            const UINT boxWidth = box.right - box.left;
            const std::size_t rowBytes = static_cast<std::size_t>(boxWidth) * 4;
            for (UINT row = box.top; row < box.bottom; ++row) {
                const std::size_t sourceOffset =
                    static_cast<std::size_t>(row) * sourceRowPitch +
                    static_cast<std::size_t>(box.left) * 4;
                const std::size_t destinationOffset =
                    static_cast<std::size_t>(row) * destinationPitch +
                    static_cast<std::size_t>(box.left) * 4;
                std::memcpy(g_d3d9_frame->pixels + destinationOffset,
                    source + sourceOffset, rowBytes);
            }
        }
    }

    g_d3d9_frame->width = width;
    g_d3d9_frame->height = height;
    g_d3d9_frame->rowPitch = width * 4;
    g_d3d9_frame->pixelBytes = width * height * 4;
    const bool visible = g_visibility_provider && g_visibility_provider();
    const LONG64 frameNumber = visible
        ? InterlockedIncrement64(&g_d3d9_frame_number) : 0;
    InterlockedExchange64(&g_d3d9_frame->frameNumber, frameNumber);
    std::atomic_thread_fence(std::memory_order_release);
    InterlockedIncrement(&g_d3d9_frame->sequence);
    return true;
}

bool SnapshotFullD3D9Frame(std::vector<std::uint8_t>& pixels, UINT& width, UINT& height) {
    std::lock_guard<std::mutex> writeGuard(g_d3d9_frame_write_mutex);
    if (!g_d3d9_frame ||
        g_d3d9_frame->magic != osc_frame_bridge::D3D9FrameMagic ||
        g_d3d9_frame->version != osc_frame_bridge::D3D9FrameVersion ||
        g_d3d9_frame->frameNumber == 0 ||
        !g_d3d9_frame->width || !g_d3d9_frame->height ||
        static_cast<std::uint64_t>(g_d3d9_frame->width) *
            g_d3d9_frame->height * 4 > osc_frame_bridge::MaxFrameBytes ||
        g_d3d9_frame->rowPitch != g_d3d9_frame->width * 4 ||
        g_d3d9_frame->pixelBytes != g_d3d9_frame->width *
            g_d3d9_frame->height * 4)
        return false;
    width = g_d3d9_frame->width;
    height = g_d3d9_frame->height;
    pixels.assign(g_d3d9_frame->pixels,
        g_d3d9_frame->pixels + g_d3d9_frame->pixelBytes);
    return true;
}
void SetVisibilityProvider(VisibilityProvider provider) {
    g_visibility_provider = provider;
}

} // namespace d3d9_frame_publisher
} // namespace osc