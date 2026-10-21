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

inline int CefDesktopFps(int fps) noexcept { return fps < 60 ? fps : 60; }

// Pace accepted Desktop Presents; CEF paints continue updating the latest image.
class DesktopFpsLimiter {
public:
    void MarkPresented(std::uint64_t nowUs) noexcept {
        lastPresentedUs_ = nowUs;
        hasPresented_ = true;
    }

    [[nodiscard]] std::uint64_t RemainingUs(
        std::uint64_t nowUs, int fps) const noexcept {
        if (!hasPresented_ || nowUs < lastPresentedUs_)
            return 0;
        const auto rate = static_cast<std::uint64_t>(
            IsValidDesktopFps(fps) ? fps : 60);
        const std::uint64_t intervalUs = (1000000ULL + rate - 1) / rate;
        const std::uint64_t elapsedUs = nowUs - lastPresentedUs_;
        return elapsedUs >= intervalUs ? 0 : intervalUs - elapsedUs;
    }

private:
    std::uint64_t lastPresentedUs_ = 0;
    bool hasPresented_ = false;
};
} // namespace osc
