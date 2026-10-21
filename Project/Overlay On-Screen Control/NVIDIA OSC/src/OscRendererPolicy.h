#pragma once
#include <string>

namespace osc {
enum class RendererChoice { Auto, Gpu, Cpu };

inline bool ParseRendererChoice(const std::string& value, RendererChoice& choice) {
    if (value == "auto") choice = RendererChoice::Auto;
    else if (value == "gpu") choice = RendererChoice::Gpu;
    else if (value == "cpu") choice = RendererChoice::Cpu;
    else return false;
    return true;
}

inline const char* RendererName(RendererChoice choice) noexcept {
    return choice == RendererChoice::Gpu ? "gpu" :
        choice == RendererChoice::Cpu ? "cpu" : "auto";
}

struct RendererPolicyInputs {
    RendererChoice uiRenderer = RendererChoice::Auto;
    RendererChoice desktopRenderer = RendererChoice::Auto;
    int legacyRenderMode = 0;
    bool legacyGdi = false;
    bool softwareFallback = false;
    bool disableGpuEnv = false;
    bool disableSharedEnv = false;
    bool sharedFallback = false;
};
struct RendererPolicy {
    bool disableUiGpu;
    bool cpuDesktop;
    bool requestSharedTexture;
};

inline RendererPolicy ResolveRendererPolicy(const RendererPolicyInputs& input) noexcept {
    const bool disableUiGpu = input.disableGpuEnv ||
        input.uiRenderer == RendererChoice::Cpu ||
        (input.uiRenderer == RendererChoice::Auto && input.softwareFallback);
    const bool cpuDesktop = input.desktopRenderer == RendererChoice::Cpu ||
        (input.desktopRenderer == RendererChoice::Auto && input.legacyGdi);
    bool shared = !input.sharedFallback;
    if (input.uiRenderer == RendererChoice::Gpu) shared = true;
    else if (input.uiRenderer == RendererChoice::Auto) {
        if (input.legacyRenderMode == 1) shared = true;
        else if (input.legacyRenderMode == 2) shared = false;
    }
    return { disableUiGpu, cpuDesktop,
        shared && !disableUiGpu && !cpuDesktop && !input.disableSharedEnv };
}
} // namespace osc
