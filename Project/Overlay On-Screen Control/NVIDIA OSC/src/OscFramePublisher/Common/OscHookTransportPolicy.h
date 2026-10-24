#pragma once
#include <cstddef>
#include <string_view>
namespace osc::hook_transport {
// This table reflects NATIVE PIXEL RENDERERS, not merely folders or API probes.
// No CPU D3D11 bridge and no D3D9Ex game-owned Pump are ready in live builds.
struct Api {
    const char* id;
    const char* settingKey;
    bool rendererImplemented;
    bool gpuModeAvailable;
    bool cpuModeAvailable;
    const char* route;
};
inline constexpr Api Apis[] = {
    {"d3d9", "OSC.HookTransport.d3d9", true, false, true, "cpu-shared-memory"},
    {"d3d9ex", "OSC.HookTransport.d3d9ex", true, false, true, "cpu-shared-memory"},
    {"d3d10", "OSC.HookTransport.d3d10", false, false, false, "unavailable"},
    {"d3d11", "OSC.HookTransport.d3d11", true, true, false, "gpu-shared-or-cpu-mirror"},
    {"d3d12", "OSC.HookTransport.d3d12", false, false, false, "unavailable"},
    {"vulkan", "OSC.HookTransport.vulkan", false, false, false, "unavailable"},
    {"opengl", "OSC.HookTransport.opengl", false, false, false, "unavailable"}
};
inline const Api* Find(std::string_view id) {
    for(const auto& api : Apis)
        if(id == api.id) return &api;
    return nullptr;
}
inline bool Allowed(const Api& api, std::string_view mode) {
    if(mode == "auto") return true;
    if(!api.rendererImplemented) return false;
    if(mode == "gpu") return api.gpuModeAvailable;
    if(mode == "cpu") return api.cpuModeAvailable;
    return false;
}
inline std::string_view SafeMode(const Api& api, std::string_view mode) {
    return Allowed(api, mode) ? mode : std::string_view("auto");
}
}
