#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <d3d11.h>
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#include "OscFrameUtilities.h"

namespace osc {
namespace frame_utilities {
namespace {
void LogPublisher(const char* message) {
    ::Log(message);
}
void LogPublisher(const std::string& message) {
    ::Log(message.c_str());
}
} // namespace
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
} // namespace frame_utilities
} // namespace osc