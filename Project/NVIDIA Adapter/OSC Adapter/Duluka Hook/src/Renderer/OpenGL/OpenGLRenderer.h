#pragma once
#include <windows.h>
#include <cstdint>

// OpenGL overlay compositor.
// Resources are owned per GL context (HGLRC); the public entry points only act
// on the context that is current on the calling thread.
namespace duluka::opengl
{
    using LogCallback = void (*)(const char *message);

    struct RendererStatus
    {
        std::uint64_t drawCount = 0;
        std::uint64_t reshadeCallbackCount = 0;
        std::uint64_t lastBridgeFrameNumber = 0;
        bool bridgeConnected = false;
        bool textureReady = false;
        long lastError = 0;
        std::uint32_t swapWidth = 0;
        std::uint32_t swapHeight = 0;
        const char *diagnosticStage = nullptr;
    };

    void Initialize(LogCallback logger);
    void Shutdown();
    bool OnPresent(HDC dc, bool gameForeground);
    RendererStatus GetStatus();
}
