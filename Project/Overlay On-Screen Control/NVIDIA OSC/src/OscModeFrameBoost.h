#pragma once
#include <atomic>
#include <cstdint>
#include "OscDesktopFps.h"

namespace osc {
// One shared deadline: transitions restart a five-second boost; ordinary paints,
// geometry updates and visibility refreshes do not prolong it.
class ModeFrameBoost {
public:
    static constexpr std::uint64_t DurationMs = 5000;
    void Start(std::uint64_t nowMs) noexcept {
        deadline_.store(nowMs + DurationMs, std::memory_order_release);
    }
    bool Active(std::uint64_t nowMs) const noexcept {
        const auto deadline = deadline_.load(std::memory_order_acquire);
        return deadline && nowMs < deadline;
    }
    void Stop() noexcept { deadline_.store(0, std::memory_order_release); }
    int DesktopPresentLimit(std::uint64_t nowMs, int configured) const noexcept {
        return Active(nowMs) ? DesktopFpsMax : configured;
    }
    int CefLimit(std::uint64_t nowMs, int normal, bool sharedTexture = false) const noexcept {
        return Active(nowMs) ? CefFrameRateMax(sharedTexture) : normal;
    }
    static int PaintDelayMs(bool sharedTexture) noexcept {
        return 1000 / CefFrameRateMax(sharedTexture);
    }
private:
    std::atomic<std::uint64_t> deadline_{0};
};
}
