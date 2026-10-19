#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <d3d11.h>
#include <atomic>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "OscFramePublisher.h"
#include "../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"

namespace osc {
namespace frame_publisher {
namespace {
HANDLE g_d3d9_frame_mapping = nullptr;
osc_frame_bridge::D3D9Frame* g_d3d9_frame = nullptr;
LONG64 g_d3d9_frame_number = 0;
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
        osc_frame_bridge::D3D9FrameMappingBytes >> 32);
    const DWORD sizeLow = static_cast<DWORD>(
        osc_frame_bridge::D3D9FrameMappingBytes & 0xFFFFFFFF);
    SetLastError(ERROR_SUCCESS);
    g_d3d9_frame_mapping = CreateFileMappingW(
        INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        sizeHigh, sizeLow, osc_frame_bridge::D3D9FrameMappingName);
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

}

void SetD3D9FrameVisibility(bool visible) {
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


std::uint64_t ComputeAdapterFrameFingerprint(
    const std::uint8_t* bytes, UINT rowPitch, UINT width, UINT height) {
    if (!bytes || !rowPitch || !width || !height)
        return 0;

    // A small spatial sample proves the readback contains changing pixels
    // without hashing/copying the entire 7 MB overlay on every frame.
    constexpr UINT sampleColumns = 32;
    constexpr UINT sampleRows = 18;
    std::uint64_t hash = 14695981039346656037ULL;
    for (UINT row = 0; row < sampleRows; ++row) {
        const UINT y = sampleRows > 1
            ? row * (height - 1) / (sampleRows - 1) : 0;
        const auto* scanline = bytes + static_cast<std::size_t>(y) * rowPitch;
        for (UINT column = 0; column < sampleColumns; ++column) {
            const UINT x = sampleColumns > 1
                ? column * (width - 1) / (sampleColumns - 1) : 0;
            const auto* pixel = scanline + static_cast<std::size_t>(x) * 4;
            for (UINT channel = 0; channel < 4; ++channel) {
                hash ^= pixel[channel];
                hash *= 1099511628211ULL;
            }
        }
    }
    return hash;
}

void LogAdapterFramePixelProof(std::uint64_t fingerprint) {
    static ULONGLONG windowStart = 0;
    static std::uint64_t fullReadbacks = 0;
    static std::uint64_t contentFingerprintChanges = 0;
    static std::uint64_t lastFingerprint = 0;
    static bool haveLastFingerprint = false;

    const ULONGLONG now = GetTickCount64();
    if (!windowStart)
        windowStart = now;
    ++fullReadbacks;
    if (haveLastFingerprint && fingerprint != lastFingerprint)
        ++contentFingerprintChanges;
    lastFingerprint = fingerprint;
    haveLastFingerprint = true;

    if (now - windowStart >= 1000) {
        char diagnostic[256]{};
        sprintf_s(diagnostic, sizeof(diagnostic),
            "[adapter-bridge] pixel proof: fullReadbacks=%llu "
            "sampledContentChanges=%llu fingerprint=0x%016llX",
            static_cast<unsigned long long>(fullReadbacks),
            static_cast<unsigned long long>(contentFingerprintChanges),
            static_cast<unsigned long long>(fingerprint));
        LogPublisher(diagnostic);
        windowStart = now;
        fullReadbacks = 0;
        contentFingerprintChanges = 0;
    }
}


std::vector<D3D11_BOX> MakeAdapterDirtyBoxes(
    const std::vector<CefRect>& dirtyRects, UINT width, UINT height) {
    std::vector<D3D11_BOX> boxes;
    if (!width || !height || dirtyRects.empty())
        return boxes;

    const auto appendClamped = [&](long long left, long long top,
        long long right, long long bottom) {
        left = (std::max)(0LL, (std::min)(left, static_cast<long long>(width)));
        top = (std::max)(0LL, (std::min)(top, static_cast<long long>(height)));
        right = (std::max)(left,
            (std::min)(right, static_cast<long long>(width)));
        bottom = (std::max)(top,
            (std::min)(bottom, static_cast<long long>(height)));
        if (left < right && top < bottom) {
            boxes.push_back(D3D11_BOX{
                static_cast<UINT>(left), static_cast<UINT>(top), 0,
                static_cast<UINT>(right), static_cast<UINT>(bottom), 1 });
        }
    };

    if (dirtyRects.size() > 64) {
        long long left = width;
        long long top = height;
        long long right = 0;
        long long bottom = 0;
        for (const CefRect& rect : dirtyRects) {
            left = (std::min)(left, static_cast<long long>(rect.x));
            top = (std::min)(top, static_cast<long long>(rect.y));
            right = (std::max)(right,
                static_cast<long long>(rect.x) + rect.width);
            bottom = (std::max)(bottom,
                static_cast<long long>(rect.y) + rect.height);
        }
        appendClamped(left, top, right, bottom);
        return boxes;
    }

    boxes.reserve(dirtyRects.size());
    for (const CefRect& rect : dirtyRects) {
        if (rect.width <= 0 || rect.height <= 0)
            continue;
        const long long left = rect.x;
        const long long top = rect.y;
        appendClamped(left, top, left + rect.width, top + rect.height);
    }
    return boxes;
}

void SetVisibilityProvider(VisibilityProvider provider) {
    g_visibility_provider = provider;
}

} // namespace frame_publisher
} // namespace osc