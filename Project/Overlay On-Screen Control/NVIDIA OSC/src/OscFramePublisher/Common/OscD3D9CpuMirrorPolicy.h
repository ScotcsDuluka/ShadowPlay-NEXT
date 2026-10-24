#pragma once
#include <cstdint>
#include "../../../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"

namespace osc::d3d9_cpu_mirror {
// Legacy D3D9 (including D3D9Ex CPU fallback) consumes ONLY the v1 CPU
// pixel mapping, not the D3D11 NT-handle/keyed-mutex shared texture.
// DX9Ex's opt-in GPU mailbox uses a DIFFERENT legacy-shared texture.
// Preserve the keyed GPU mirror for other consumers, especially D3D11.
constexpr bool PublishCpuBridgeWithoutNtGpuMirror(std::uint32_t api) noexcept {
    return api == osc_frame_bridge::ConsumerRenderApiD3D9;
}
}
