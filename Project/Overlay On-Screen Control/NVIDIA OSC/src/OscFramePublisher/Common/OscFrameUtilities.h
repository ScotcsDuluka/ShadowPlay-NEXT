#pragma once

#include <cstdint>
#include <vector>
#include <d3d11.h>
#include "include/cef_browser.h"

namespace osc {
namespace frame_utilities {

std::uint64_t ComputeAdapterFrameFingerprint(
    const std::uint8_t* bytes, UINT rowPitch, UINT width, UINT height);
void LogAdapterFramePixelProof(std::uint64_t fingerprint);
std::vector<D3D11_BOX> MakeAdapterDirtyBoxes(
    const std::vector<CefRect>& dirtyRects, UINT width, UINT height);

} // namespace frame_utilities
} // namespace osc

void Log(const char* message);