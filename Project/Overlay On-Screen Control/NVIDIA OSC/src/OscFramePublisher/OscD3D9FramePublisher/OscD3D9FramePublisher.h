#pragma once

#include <cstdint>
#include <vector>
#include <d3d11.h>

namespace osc {
namespace d3d9_frame_publisher {

using VisibilityProvider = bool (*)();

// The D3D9 consumer receives CPU BGRA pixels through the versioned shared-memory bridge.
void SetVisibilityProvider(VisibilityProvider provider);
bool PublishD3D9FramePixels(const std::uint8_t* source,
    UINT sourceRowPitch, const std::vector<D3D11_BOX>& dirtyBoxes,
    UINT width, UINT height, bool fullFrame);
void SetD3D9FrameVisibility(bool visible);
// Complete, sequence-stable BGRA8 snapshot for optional DX9Ex GPU upload.
// Call off the CEF/Game Present threads; CPU bridge remains authoritative.
bool SnapshotFullD3D9Frame(std::vector<std::uint8_t>& pixels, UINT& width, UINT& height);

} // namespace d3d9_frame_publisher
} // namespace osc

// Shared entry to the existing OSC host logger.
void Log(const char* message);