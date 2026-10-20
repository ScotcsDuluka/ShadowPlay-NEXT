#include "D3D11SharedTextureRelay.h"

namespace duluka::d3d11
{
    SharedTextureRelay::~SharedTextureRelay()
    {
        if (context_) { context_->ClearState(); context_->Flush(); }
        if (relayMutex_) relayMutex_->Release();
        if (relay_) relay_->Release();
        if (sourceMutex_) sourceMutex_->Release();
        if (source_) source_->Release();
        if (context_) context_->Release();
        if (device_) device_->Release();
    }

    HRESULT SharedTextureRelay::Open(ID3D11Device *gameDevice,
        const wchar_t *name, ID3D11Texture2D **gameTexture)
    {
        if (!gameDevice || !name || !gameTexture) return E_INVALIDARG;
        *gameTexture = nullptr;
        IDXGIDevice *dxgiDevice = nullptr;
        IDXGIAdapter *adapter = nullptr;
        HRESULT result = gameDevice->QueryInterface(__uuidof(IDXGIDevice),
            reinterpret_cast<void **>(&dxgiDevice));
        if (SUCCEEDED(result)) result = dxgiDevice->GetAdapter(&adapter);
        if (dxgiDevice) dxgiDevice->Release();
        if (FAILED(result)) return result;
        result = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            &device_, nullptr, &context_);
        adapter->Release();
        if (FAILED(result)) return result;
        ID3D11Device1 *device1 = nullptr;
        result = device_->QueryInterface(__uuidof(ID3D11Device1),
            reinterpret_cast<void **>(&device1));
        if (SUCCEEDED(result))
            result = device1->OpenSharedResourceByName(name, DXGI_SHARED_RESOURCE_READ,
                __uuidof(ID3D11Texture2D), reinterpret_cast<void **>(&source_));
        if (device1) device1->Release();
        if (FAILED(result)) return result;
        D3D11_TEXTURE2D_DESC desc{};
        source_->GetDesc(&desc);
        if (!desc.Width || !desc.Height || desc.Width > 8192 || desc.Height > 8192 ||
            desc.MipLevels != 1 || desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
            return E_INVALIDARG;
        result = source_->QueryInterface(__uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void **>(&sourceMutex_));
        if (FAILED(result)) return result;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        result = device_->CreateTexture2D(&desc, nullptr, &relay_);
        if (FAILED(result)) return result;
        result = relay_->QueryInterface(__uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void **>(&relayMutex_));
        if (FAILED(result)) return result;
        IDXGIResource *resource = nullptr;
        HANDLE handle = nullptr;
        result = relay_->QueryInterface(__uuidof(IDXGIResource),
            reinterpret_cast<void **>(&resource));
        if (SUCCEEDED(result)) result = resource->GetSharedHandle(&handle);
        if (resource) resource->Release();
        if (SUCCEEDED(result))
            result = gameDevice->OpenSharedResource(handle, __uuidof(ID3D11Texture2D),
                reinterpret_cast<void **>(gameTexture));
        // Legacy DXGI shared handles are owned by the resource, not CloseHandle.
        return result;
    }

    HRESULT SharedTextureRelay::Refresh()
    {
        HRESULT result = sourceMutex_->AcquireSync(0, 2);
        if (result != S_OK) return result;
        result = relayMutex_->AcquireSync(0, 0);
        if (result == S_OK)
        {
            context_->CopyResource(relay_, source_);
            context_->Flush();
            result = relayMutex_->ReleaseSync(0);
        }
        const HRESULT releaseResult = sourceMutex_->ReleaseSync(0);
        return result == S_OK ? releaseResult : result;
    }
}
