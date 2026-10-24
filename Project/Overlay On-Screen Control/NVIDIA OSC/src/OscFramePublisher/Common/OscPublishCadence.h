#pragma once
#include <cstdint>
#include <limits>
namespace osc::frame_cadence {
// The producer may update metadata any number of times between CEF paints.
// Only a successfully copied frame sourced from a NEW CEF paint advances
// frameNumber. Keep the last frame stable for Hook texture-cache reuse.
struct FrameCadence {
    std::uint64_t latestIssued = 0;
    std::uint64_t lastPublishedSourceSerial = 0;
    std::uint64_t Next(std::uint64_t wireFrameNumber,
        std::uint64_t completedSourceSerial,
        bool visible, bool completedPixelCopy) {
        if (!visible) return 0; // Explicit cache invalidation, once.
        if (!completedPixelCopy || completedSourceSerial == 0 ||
            completedSourceSerial <= lastPublishedSourceSerial)
            return wireFrameNumber;
        lastPublishedSourceSerial = completedSourceSerial;
        if (wireFrameNumber > latestIssued)
            latestIssued = wireFrameNumber;
        ++latestIssued;
        if (latestIssued == 0) ++latestIssued; // nonzero on wrap
        return latestIssued;
    }
};
}
