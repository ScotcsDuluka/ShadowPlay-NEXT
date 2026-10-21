#pragma once
#include <d3d11_1.h>
#include <dxgi.h>

namespace duluka::d3d11
{
    // In-process GPU relay for devices that cannot import named NT resources.
    // The OSC wire protocol and producer remain unchanged.
    class SharedTextureRelay
    {
    public:
        SharedTextureRelay() = default;
        SharedTextureRelay(const SharedTextureRelay &) = delete;
        SharedTextureRelay &operator=(const SharedTextureRelay &) = delete;
        ~SharedTextureRelay();
        HRESULT Open(ID3D11Device *gameDevice, const wchar_t *name,
            ID3D11Texture2D **gameTexture);
        HRESULT Refresh(DWORD timeoutMs = 2);
    private:
        ID3D11Device *device_ = nullptr;
        ID3D11DeviceContext *context_ = nullptr;
        ID3D11Texture2D *source_ = nullptr;
        IDXGIKeyedMutex *sourceMutex_ = nullptr;
        ID3D11Texture2D *relay_ = nullptr;
        IDXGIKeyedMutex *relayMutex_ = nullptr;
    };
}
