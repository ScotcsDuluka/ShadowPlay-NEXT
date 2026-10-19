#pragma once

#include <cstdint>
#include <vector>
#include <d3d11.h>
#include "include/cef_browser.h"

namespace osc {
namespace frame_publisher {

using VisibilityProvider = bool (*)();

// Configure the host-owned visibility query before CEF frame callbacks begin.
void SetVisibilityProvider(VisibilityProvider provider);

// Publish a CPU readback to the D3D9 compatibility bridge used by the Hook adapter.
bool PublishD3D9FramePixels(const std::uint8_t* source,
    UINT sourceRowPitch, const std::vector<D3D11_BOX>& dirtyBoxes,
    UINT width, UINT height, bool fullFrame);
void SetD3D9FrameVisibility(bool visible);

// Utilities shared by the D3D11 copy/readback pipeline.
std::uint64_t ComputeAdapterFrameFingerprint(
    const std::uint8_t* bytes, UINT rowPitch, UINT width, UINT height);
void LogAdapterFramePixelProof(std::uint64_t fingerprint);
std::vector<D3D11_BOX> MakeAdapterDirtyBoxes(
    const std::vector<CefRect>& dirtyRects, UINT width, UINT height);

} // namespace frame_publisher
} // namespace osc

// Shared entry to the existing OSC host logger.
void Log(const char* message);