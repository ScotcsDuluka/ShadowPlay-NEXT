#pragma once
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdint>

namespace duluka::d3d11
{
    using LogCallback = void (*)(const char *message);
    struct RendererStatus
    {
        std::uint64_t drawCount = 0;
        std::uint64_t reshadeCallbackCount = 0;
        std::uint64_t lastBridgeFrameNumber = 0;
        std::uint64_t cacheCopyCount = 0;
        std::uint64_t backBufferRtvReuseCount = 0;
        std::uint64_t backBufferRtvCreateCount = 0;
        std::uint64_t cachedFrameNumber = 0;
        bool cacheValid = false;
        bool bridgeConnected = false;
        bool textureReady = false;
        long lastError = 0;
        long diagnosticHresult = 0;
        std::uint32_t swapWidth = 0;
        std::uint32_t swapHeight = 0;
        const char *diagnosticStage = nullptr;
    };
    void Initialize(LogCallback logger);
    void Shutdown();
    bool OnPresent(IDXGISwapChain *swapChain, bool gameForeground, bool reshadeAddonRegistered);
    void OnReShadePresent(ID3D11Device *device, ID3D11Texture2D *backBuffer, bool gameForeground);
    RendererStatus GetStatus();
}