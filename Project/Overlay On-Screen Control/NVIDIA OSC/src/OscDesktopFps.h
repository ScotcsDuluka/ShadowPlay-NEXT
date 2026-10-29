#pragma once

#include <cmath>
#include <cstdint>

namespace osc {
inline constexpr int DesktopFpsMin = 15;
inline constexpr int DesktopFpsMax = 240;

inline bool IsValidDesktopFps(double fps) noexcept {
    return std::isfinite(fps) && fps >= DesktopFpsMin &&
        fps <= DesktopFpsMax && std::floor(fps) == fps;
}

// Preserve the stock CEF 73 60fps ceiling for CPU/legacy rendering.
// The patched accelerated OSR shared-texture backend may request up to 240fps.
// This is a request ceiling, not a measured frame delivery guarantee.
inline constexpr int CefFrameRateMax(bool acceleratedSharedTexture) noexcept {
    return acceleratedSharedTexture ? DesktopFpsMax : 60;
}
inline int CefDesktopFps(int fps, bool acceleratedSharedTexture = false) noexcept {
    if (!IsValidDesktopFps(fps)) return 60;
    const int ceiling = CefFrameRateMax(acceleratedSharedTexture);
    return fps < ceiling ? fps : ceiling;
}
inline constexpr int CefHookFps(bool acceleratedSharedTexture) noexcept {
    return CefFrameRateMax(acceleratedSharedTexture);
}

// Pace accepted Desktop Presents; CEF paints continue updating the latest image.
// The pacing anchor is the instant the Present call is issued (present START),
// so Present submission, DComp Commit and worker wake-up land INSIDE the frame
// interval instead of extending every cycle past it. Measuring from present
// completion capped the presenter at roughly interval + per-cycle overhead.
class DesktopFpsLimiter {
public:
    // nowUs = timestamp captured immediately before the Present call.
    void MarkPresented(std::uint64_t nowUs, int fps) noexcept {
        if (!hasPresented_) {
            nextDeadlineUs_ = nowUs + IntervalUs(fps);
        } else {
            nextDeadlineUs_ += IntervalUs(fps);
            // A long stall (game focus, capacity timeout) must re-anchor instead
            // of issuing a catch-up burst of presents.
            if (nextDeadlineUs_ < nowUs)
                nextDeadlineUs_ = nowUs + IntervalUs(fps);
        }
        lastPresentedUs_ = nowUs;
        hasPresented_ = true;
    }

    [[nodiscard]] std::uint64_t RemainingUs(std::uint64_t nowUs) const noexcept {
        if (!hasPresented_ || nowUs >= nextDeadlineUs_)
            return 0;
        return nextDeadlineUs_ - nowUs;
    }

private:
    [[nodiscard]] static std::uint64_t IntervalUs(int fps) noexcept {
        const auto rate = static_cast<std::uint64_t>(
            IsValidDesktopFps(fps) ? fps : 60);
        return (1000000ULL + rate - 1) / rate;
    }
    std::uint64_t nextDeadlineUs_ = 0;
    std::uint64_t lastPresentedUs_ = 0;
    bool hasPresented_ = false;
};
} // namespace osc
