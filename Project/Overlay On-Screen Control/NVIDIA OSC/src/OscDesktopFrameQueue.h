#pragma once

#include <cstdint>

namespace osc {

// Tracks the latest CEF texture and the one copied to the desktop backbuffer.
// The caller must hold the shared GPU mutex for every operation.
class DesktopFrameQueue {
public:
    void QueueFrame() noexcept { ++latestSerial_; }

    [[nodiscard]] bool HasPendingFrame() const noexcept {
        return latestSerial_ != presentedSerial_;
    }

    // Check HasPendingFrame first. A busy Present must retain its copied buffer.
    [[nodiscard]] bool NeedsCopy() const noexcept { return !bufferPending_; }

    // Call after copying the latest texture, only when NeedsCopy is true.
    void MarkCopied() noexcept {
        bufferSerial_ = latestSerial_;
        bufferPending_ = true;
    }

    // Call only after Present accepts the copied buffer; Busy changes no state.
    void MarkPresented() noexcept {
        if (!bufferPending_) return;
        presentedSerial_ = bufferSerial_;
        bufferPending_ = false;
    }

    void Reset() noexcept {
        latestSerial_ = 0;
        presentedSerial_ = 0;
        bufferSerial_ = 0;
        bufferPending_ = false;
    }

private:
    std::uint64_t latestSerial_ = 0;
    std::uint64_t presentedSerial_ = 0;
    std::uint64_t bufferSerial_ = 0;
    bool bufferPending_ = false;
};

} // namespace osc
