#include "D3D11Renderer.h"
#include <d3d11_1.h>
#include "D3D11SharedTextureRelay.h"
#include <d3dcompiler.h>
#include <strsafe.h>
#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <memory>
#include <new>
#include <string>
#include "../../../include/osc_frame_bridge.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace
{
    duluka::d3d11::LogCallback g_logCallback = nullptr;
    void Log(const char *message)
    {
        if (g_logCallback)
            g_logCallback(message);
    }

    HANDLE g_bridgeMapping = nullptr;
    const osc_frame_bridge::Frame *g_bridgeFrame = nullptr;
    HANDLE g_consumerAdapterMapping = nullptr;
    osc_frame_bridge::ConsumerAdapterInfo *g_consumerAdapterInfo = nullptr;
    ULONGLONG g_nextBridgeOpen = 0;
    std::mutex g_renderMutex;
    ID3D11Device *g_renderDevice = nullptr;
    ID3D11DeviceContext1 *g_renderContext = nullptr;
    ID3DDeviceContextState *g_renderContextState = nullptr;
    ID3D11VertexShader *g_overlayVertexShader = nullptr;
    ID3D11PixelShader *g_overlayPixelShader = nullptr;
    ID3D11BlendState *g_overlayBlendState = nullptr;
    ID3D11SamplerState *g_overlaySampler = nullptr;
    ID3D11Texture2D *g_overlayTexture = nullptr;
    // Explicit lifetime: never run a GPU-owning static destructor at process exit.
    duluka::d3d11::SharedTextureRelay *g_textureRelay = nullptr;
    ID3D11ShaderResourceView *g_overlayView = nullptr;
    IDXGIKeyedMutex *g_overlayKeyedMutex = nullptr;
    ID3D11Texture2D *g_overlayCachedTexture = nullptr;
    ID3D11ShaderResourceView *g_overlayCachedView = nullptr;
    std::uint64_t g_cachedFrameNumber = 0;
    bool g_overlayCacheValid = false;
    HANDLE g_overlaySourceProcess = nullptr;
    std::uint32_t g_overlaySourcePid = 0;
    std::uint64_t g_overlaySourceResourceId = 0;
    std::uint32_t g_lastLoggedOpenFailurePid = 0;
    std::uint64_t g_lastLoggedOpenFailureResourceId = 0;
    ULONGLONG g_nextTextureOpen = 0;
    std::atomic<std::uint64_t> g_overlayDrawCount{ 0 };
    std::atomic<std::uint64_t> g_reshadeCallbackCount{ 0 };
    std::atomic<std::uint64_t> g_lastBridgeFrameNumber{ 0 };
    std::atomic<std::uint64_t> g_overlayCacheCopyCount{ 0 };
    std::atomic<std::uint64_t> g_backBufferRtvReuseCount{ 0 };
    std::atomic<std::uint64_t> g_backBufferRtvCreateCount{ 0 };
    std::atomic<bool> g_bridgeConnected{ false };
    std::atomic<bool> g_overlayTextureReady{ false };
    std::atomic<long> g_lastOverlayError{ 0 };
    const char *g_diagnosticStage = "waiting-for-first-present";
    long g_diagnosticHresult = 0;
    std::uint32_t g_diagnosticSwapWidth = 0;
    std::uint32_t g_diagnosticSwapHeight = 0;

    void SetDiagnosticStage(const char *stage, HRESULT result = S_OK)
    {
        g_diagnosticStage = stage;
        g_diagnosticHresult = static_cast<long>(result);
    }

    bool EnsureBridgeMapping()
    {
        if (g_bridgeFrame)
            return true;

        const ULONGLONG now = GetTickCount64();
        if (now < g_nextBridgeOpen)
            return false;
        g_nextBridgeOpen = now + 1000;

        wchar_t mappingName[128]{};
        const DWORD mappingNameLength = GetEnvironmentVariableW(
            osc_frame_bridge::MappingNameEnvironmentVariable,
            mappingName, static_cast<DWORD>(std::size(mappingName)));
        if (mappingNameLength >= std::size(mappingName))
        {
            Log("The configured OSC frame bridge name is too long.");
            return false;
        }
        const wchar_t *mappingNameToOpen = mappingNameLength
            ? mappingName : osc_frame_bridge::MappingName;
        HANDLE mapping = OpenFileMappingW(
            FILE_MAP_READ, FALSE, mappingNameToOpen);
        if (!mapping)
        {
            g_lastOverlayError.store(
                static_cast<long>(HRESULT_FROM_WIN32(GetLastError())),
                std::memory_order_relaxed);
            return false;
        }

        auto frame = static_cast<const osc_frame_bridge::Frame*>(
            MapViewOfFile(mapping, FILE_MAP_READ, 0, 0,
                osc_frame_bridge::MappingBytes));
        if (!frame)
        {
            const DWORD error = GetLastError();
            CloseHandle(mapping);
            g_lastOverlayError.store(
                static_cast<long>(HRESULT_FROM_WIN32(error)),
                std::memory_order_relaxed);
            Log("Could not map the OSC frame bridge.");
            return false;
        }

        g_bridgeMapping = mapping;
        g_bridgeFrame = frame;
        g_bridgeConnected.store(true, std::memory_order_release);
        g_lastOverlayError.store(0, std::memory_order_relaxed);
        Log("Connected to the NVIDIA OSC CEF texture bridge.");
        return true;
    }

    void PublishConsumerAdapterLuid(ID3D11Device *device)
    {
        if (!device)
            return;

        if (!g_consumerAdapterInfo)
        {
            wchar_t mappingName[128]{};
            const DWORD mappingNameLength = GetEnvironmentVariableW(
                osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
                mappingName, static_cast<DWORD>(std::size(mappingName)));
            if (mappingNameLength >= std::size(mappingName))
            {
                Log("The configured consumer adapter mapping name is too long.");
                return;
            }
            const wchar_t *mappingNameToCreate = mappingNameLength
                ? mappingName : osc_frame_bridge::ConsumerAdapterMappingName;
            g_consumerAdapterMapping = CreateFileMappingW(
                INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                0, sizeof(osc_frame_bridge::ConsumerAdapterInfo),
                mappingNameToCreate);
            if (!g_consumerAdapterMapping)
                return;
            g_consumerAdapterInfo =
                static_cast<osc_frame_bridge::ConsumerAdapterInfo*>(
                    MapViewOfFile(g_consumerAdapterMapping, FILE_MAP_ALL_ACCESS,
                        0, 0, sizeof(osc_frame_bridge::ConsumerAdapterInfo)));
            if (!g_consumerAdapterInfo)
            {
                CloseHandle(g_consumerAdapterMapping);
                g_consumerAdapterMapping = nullptr;
                return;
            }
            if (g_consumerAdapterInfo->magic !=
                    osc_frame_bridge::ConsumerAdapterMagic ||
                g_consumerAdapterInfo->version !=
                    osc_frame_bridge::ConsumerAdapterVersion)
            {
                g_consumerAdapterInfo->magic = 0;
                g_consumerAdapterInfo->version =
                    osc_frame_bridge::ConsumerAdapterVersion;
                InterlockedExchange(&g_consumerAdapterInfo->ownerProcessId, 0);
                InterlockedExchange64(&g_consumerAdapterInfo->adapterLuid, 0);
                InterlockedExchange(&g_consumerAdapterInfo->renderApi, 0);
                MemoryBarrier();
                g_consumerAdapterInfo->magic =
                    osc_frame_bridge::ConsumerAdapterMagic;
            }
        }

        const LONG currentProcessId = static_cast<LONG>(GetCurrentProcessId());
        if (InterlockedCompareExchange(
                &g_consumerAdapterInfo->ownerProcessId, 0, 0) == currentProcessId &&
            InterlockedCompareExchange64(
                &g_consumerAdapterInfo->adapterLuid, 0, 0) != 0) {
            InterlockedExchange(&g_consumerAdapterInfo->renderApi,
                static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D11));
            return;
        }

        IDXGIDevice *dxgiDevice = nullptr;
        IDXGIAdapter *adapter = nullptr;
        DXGI_ADAPTER_DESC adapterDesc{};
        HRESULT result = device->QueryInterface(
            __uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice));
        if (SUCCEEDED(result))
            result = dxgiDevice->GetAdapter(&adapter);
        if (SUCCEEDED(result))
            result = adapter->GetDesc(&adapterDesc);
        if (adapter) adapter->Release();
        if (dxgiDevice) dxgiDevice->Release();
        if (FAILED(result))
            return;

        const std::uint64_t packedLuid =
            (static_cast<std::uint64_t>(
                static_cast<std::uint32_t>(adapterDesc.AdapterLuid.HighPart)) << 32) |
            adapterDesc.AdapterLuid.LowPart;
        InterlockedExchange64(&g_consumerAdapterInfo->adapterLuid,
            static_cast<LONG64>(packedLuid));
        InterlockedExchange(&g_consumerAdapterInfo->renderApi,
            static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D11));
        InterlockedExchange(&g_consumerAdapterInfo->ownerProcessId,
            static_cast<LONG>(GetCurrentProcessId()));
    }

    void ReleaseOverlayTexture()
    {
        if (g_overlayCachedView) { g_overlayCachedView->Release(); g_overlayCachedView = nullptr; }
        if (g_overlayCachedTexture) { g_overlayCachedTexture->Release(); g_overlayCachedTexture = nullptr; }
        if (g_overlayView) { g_overlayView->Release(); g_overlayView = nullptr; }
        if (g_overlayKeyedMutex) { g_overlayKeyedMutex->Release(); g_overlayKeyedMutex = nullptr; }
        if (g_overlayTexture) { g_overlayTexture->Release(); g_overlayTexture = nullptr; }
        delete g_textureRelay;
        g_textureRelay = nullptr;
        if (g_overlaySourceProcess) { CloseHandle(g_overlaySourceProcess); g_overlaySourceProcess = nullptr; }
        g_overlaySourcePid = 0;
        g_overlaySourceResourceId = 0;
        g_cachedFrameNumber = 0;
        g_overlayCacheValid = false;
        g_overlayTextureReady.store(false, std::memory_order_release);
    }

    void ReleaseRenderResources()
    {
        ReleaseOverlayTexture();
        if (g_overlaySampler) { g_overlaySampler->Release(); g_overlaySampler = nullptr; }
        if (g_overlayBlendState) { g_overlayBlendState->Release(); g_overlayBlendState = nullptr; }
        if (g_overlayPixelShader) { g_overlayPixelShader->Release(); g_overlayPixelShader = nullptr; }
        if (g_overlayVertexShader) { g_overlayVertexShader->Release(); g_overlayVertexShader = nullptr; }
        if (g_renderContextState) { g_renderContextState->Release(); g_renderContextState = nullptr; }
        if (g_renderContext) { g_renderContext->Release(); g_renderContext = nullptr; }
        if (g_renderDevice) { g_renderDevice->Release(); g_renderDevice = nullptr; }
    }

    bool EnsureRenderResources(ID3D11Device *device)
    {
        if (g_renderDevice == device)
            return g_renderContext && g_renderContextState &&
                g_overlayVertexShader && g_overlayPixelShader &&
                g_overlayBlendState && g_overlaySampler;

        ReleaseRenderResources();
        g_renderDevice = device;
        g_renderDevice->AddRef();

        IDXGIDevice *dxgiDevice = nullptr;
        IDXGIAdapter *adapter = nullptr;
        if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice),
                reinterpret_cast<void **>(&dxgiDevice))))
        {
            if (SUCCEEDED(dxgiDevice->GetAdapter(&adapter)))
            {
                DXGI_ADAPTER_DESC adapterDesc{};
                if (SUCCEEDED(adapter->GetDesc(&adapterDesc)))
                {
                    char diagnostic[256]{};
                    sprintf_s(diagnostic, sizeof(diagnostic),
                        "Game D3D11 device: featureLevel=0x%X vendor=0x%04X "
                        "device=0x%04X adapterLuid=%08lX:%08lX creationFlags=0x%X",
                        static_cast<unsigned>(device->GetFeatureLevel()),
                        adapterDesc.VendorId, adapterDesc.DeviceId,
                        static_cast<unsigned long>(adapterDesc.AdapterLuid.HighPart),
                        static_cast<unsigned long>(adapterDesc.AdapterLuid.LowPart),
                        device->GetCreationFlags());
                    Log(diagnostic);
                }
                adapter->Release();
            }
            dxgiDevice->Release();
        }

        ID3D11Device1 *device1 = nullptr;
        ID3D11DeviceContext *context = nullptr;
        if (FAILED(device->QueryInterface(__uuidof(ID3D11Device1),
                reinterpret_cast<void **>(&device1))))
        {
            Log("The game D3D11 device does not expose the D3D11.1 state API.");
            return false;
        }
        device->GetImmediateContext(&context);
        if (!context || FAILED(context->QueryInterface(__uuidof(ID3D11DeviceContext1),
                reinterpret_cast<void **>(&g_renderContext))))
        {
            if (context) context->Release();
            device1->Release();
            Log("Could not acquire the D3D11.1 immediate context.");
            return false;
        }
        context->Release();

        const D3D_FEATURE_LEVEL featureLevel = device->GetFeatureLevel();
        D3D_FEATURE_LEVEL chosenFeatureLevel{};
        HRESULT result = device1->CreateDeviceContextState(
            0, &featureLevel, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device),
            &chosenFeatureLevel, &g_renderContextState);
        device1->Release();
        if (FAILED(result))
        {
            Log("Could not create a D3D11 context state for restoring game render state.");
            return false;
        }

        static constexpr char vertexShaderSource[] =
            "struct O { float4 p : SV_POSITION; float2 uv : TEXCOORD0; };"
            "O VS(uint id : SV_VertexID) {"
            "  float2 p[3] = { float2(-1,1), float2(3,1), float2(-1,-3) };"
            "  float2 t[3] = { float2(0,0), float2(2,0), float2(0,2) };"
            "  O o; o.p=float4(p[id],0,1); o.uv=float2(t[id].x,1.0-t[id].y); return o; }";
        static constexpr char pixelShaderSource[] =
            "Texture2D frame : register(t0);"
            "SamplerState frameSampler : register(s0);"
            "float4 PS(float4 p : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target {"
            "  return frame.Sample(frameSampler, uv); }";

        ID3DBlob *vertexCode = nullptr;
        ID3DBlob *pixelCode = nullptr;
        ID3DBlob *errors = nullptr;
        result = D3DCompile(vertexShaderSource, sizeof(vertexShaderSource) - 1,
            nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vertexCode, &errors);
        if (FAILED(result))
        {
            if (errors) errors->Release();
            return false;
        }
        if (errors) { errors->Release(); errors = nullptr; }
        result = D3DCompile(pixelShaderSource, sizeof(pixelShaderSource) - 1,
            nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pixelCode, &errors);
        if (FAILED(result))
        {
            if (vertexCode) vertexCode->Release();
            if (errors) errors->Release();
            return false;
        }
        if (errors) { errors->Release(); errors = nullptr; }

        result = device->CreateVertexShader(vertexCode->GetBufferPointer(),
            vertexCode->GetBufferSize(), nullptr, &g_overlayVertexShader);
        if (SUCCEEDED(result))
            result = device->CreatePixelShader(pixelCode->GetBufferPointer(),
                pixelCode->GetBufferSize(), nullptr, &g_overlayPixelShader);
        vertexCode->Release();
        pixelCode->Release();
        if (FAILED(result))
        {
            Log("Could not create the OSC overlay shaders.");
            return false;
        }

        D3D11_BLEND_DESC blend{};
        auto &target = blend.RenderTarget[0];
        target.BlendEnable = TRUE;
        target.SrcBlend = D3D11_BLEND_ONE;
        target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        target.BlendOp = D3D11_BLEND_OP_ADD;
        target.SrcBlendAlpha = D3D11_BLEND_ONE;
        target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        result = device->CreateBlendState(&blend, &g_overlayBlendState);

        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        if (SUCCEEDED(result))
            result = device->CreateSamplerState(&sampler, &g_overlaySampler);
        if (FAILED(result))
        {
            Log("Could not create OSC overlay blend/sampler state.");
            return false;
        }

        Log("D3D11 OSC overlay compositor initialized.");
        return true;
    }

    bool ReadBridgeFrame(std::uint32_t &processId, std::uint64_t &resourceId,
        std::uint64_t &frameNumber)
    {
        if (!EnsureBridgeMapping())
            return false;
        if (g_bridgeFrame->magic != osc_frame_bridge::Magic ||
            g_bridgeFrame->version != osc_frame_bridge::Version)
            return false;

        for (int attempt = 0; attempt < 3; ++attempt)
        {
            const LONG before = g_bridgeFrame->sequence;
            if (before & 1)
                continue;
            std::atomic_thread_fence(std::memory_order_acquire);
            const auto pid = g_bridgeFrame->processId;
            const auto ownerPid = g_bridgeFrame->ownerProcessId;
            const auto candidateResourceId = g_bridgeFrame->sharedResourceId;
            const auto width = g_bridgeFrame->width;
            const auto height = g_bridgeFrame->height;
            const auto number = g_bridgeFrame->frameNumber;
            std::atomic_thread_fence(std::memory_order_acquire);
            if (before != g_bridgeFrame->sequence)
                continue;
            if (!pid || ownerPid != static_cast<LONG>(pid) ||
                !candidateResourceId || !width || !height ||
                static_cast<std::uint64_t>(width) * height * 4 >
                    osc_frame_bridge::MaxFrameBytes)
                return false;
            processId = pid;
            resourceId = candidateResourceId;
            frameNumber = number;
            return true;
        }
        return false;
    }

    bool OpenOverlayTexture(ID3D11Device *device, std::uint32_t sourcePid,
        std::uint64_t sourceResourceId)
    {
        const ULONGLONG now = GetTickCount64();
        if (now < g_nextTextureOpen)
            return false;
        g_nextTextureOpen = now + 1000;

        wchar_t sharedResourceName[MAX_PATH]{};
        if (!osc_frame_bridge::MakeSharedTextureName(sourcePid,
                sourceResourceId, sharedResourceName,
                ARRAYSIZE(sharedResourceName)))
        {
            g_lastOverlayError.store(E_INVALIDARG, std::memory_order_relaxed);
            return false;
        }

        // A named NT shared resource avoids PROCESS_DUP_HANDLE access to the
        // elevated OSC process when a game is running at medium integrity.
        HANDLE sourceProcess = OpenProcess(SYNCHRONIZE, FALSE, sourcePid);
        const DWORD syncOpenError = sourceProcess ? ERROR_SUCCESS : GetLastError();
        if (sourceProcess && WaitForSingleObject(sourceProcess, 0) != WAIT_TIMEOUT)
        {
            CloseHandle(sourceProcess);
            g_lastOverlayError.store(
                static_cast<long>(HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED)),
                std::memory_order_relaxed);
            return false;
        }
        ID3D11Device1 *device1 = nullptr;
        ID3D11Resource *resource = nullptr;
        std::unique_ptr<duluka::d3d11::SharedTextureRelay> relay;
        HRESULT result = device->QueryInterface(__uuidof(ID3D11Device1),
            reinterpret_cast<void **>(&device1));
        if (SUCCEEDED(result))
            result = device1->OpenSharedResourceByName(sharedResourceName,
                DXGI_SHARED_RESOURCE_READ, __uuidof(ID3D11Resource),
                reinterpret_cast<void **>(&resource));
        if (device1)
            device1->Release();
        wchar_t testBridge[128]{}, expectedBridge[128]{}, forceRelay[2]{};
        swprintf_s(expectedBridge, std::size(expectedBridge),
            L"Local\\NVIDIAOSC.Frame.Test.%lu", GetCurrentProcessId());
        if (GetEnvironmentVariableW(osc_frame_bridge::MappingNameEnvironmentVariable,
                testBridge, static_cast<DWORD>(std::size(testBridge))) &&
            wcscmp(testBridge, expectedBridge) == 0 &&
            GetEnvironmentVariableW(L"NVIDIA_OSC_TEST_FORCE_TEXTURE_RELAY",
                forceRelay, static_cast<DWORD>(std::size(forceRelay))) == 1 &&
            forceRelay[0] == L'1')
        {
            if (resource) { resource->Release(); resource = nullptr; }
            result = E_INVALIDARG;
        }
        if (result == E_INVALIDARG || result == E_NOINTERFACE)
        {
            relay.reset(new (std::nothrow) duluka::d3d11::SharedTextureRelay());
            ID3D11Texture2D *relayTexture = nullptr;
            const HRESULT relayResult = relay
                ? relay->Open(device, sharedResourceName, &relayTexture) : E_OUTOFMEMORY;
            if (SUCCEEDED(relayResult) && relayTexture)
            {
                resource = relayTexture;
                result = S_OK;
            }
            else
            {
                if (relayTexture) relayTexture->Release();
                relay.reset();
                char diagnostic[192]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "Shared texture relay failed: namedHr=0x%08lX relayHr=0x%08lX",
                    static_cast<unsigned long>(result), static_cast<unsigned long>(relayResult));
                Log(diagnostic);
            }
        }
        if (SUCCEEDED(result) && !resource)
            result = E_UNEXPECTED;
        if (FAILED(result) || !resource)
        {
            if (sourceProcess)
                CloseHandle(sourceProcess);
            if (g_lastLoggedOpenFailurePid != sourcePid ||
                g_lastLoggedOpenFailureResourceId != sourceResourceId)
            {
                g_lastLoggedOpenFailurePid = sourcePid;
                g_lastLoggedOpenFailureResourceId = sourceResourceId;
                char diagnostic[512]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "Named shared texture open failed: sourcePid=%lu resourceId=%llu "
                    "openByNameHr=0x%08lX optionalSyncOpenError=%lu deviceRemovedHr=0x%08lX",
                    sourcePid,
                    static_cast<unsigned long long>(sourceResourceId),
                    static_cast<unsigned long>(result),
                    static_cast<unsigned long>(syncOpenError),
                    static_cast<unsigned long>(device->GetDeviceRemovedReason()));
                Log(diagnostic);
            }
            g_lastOverlayError.store(static_cast<long>(result), std::memory_order_relaxed);
            return false;
        }

        ID3D11Texture2D *texture = nullptr;
        result = resource->QueryInterface(__uuidof(ID3D11Texture2D),
            reinterpret_cast<void **>(&texture));
        const HRESULT textureQueryResult = result;
        resource->Release();
        if (FAILED(result) || !texture)
        {
            if (sourceProcess)
                CloseHandle(sourceProcess);
            char diagnostic[256]{};
            sprintf_s(diagnostic, sizeof(diagnostic),
                "Opened shared resource is not a D3D11 texture: hr=0x%08lX",
                static_cast<unsigned long>(textureQueryResult));
            Log(diagnostic);
            g_lastOverlayError.store(static_cast<long>(result), std::memory_order_relaxed);
            return false;
        }

        D3D11_TEXTURE2D_DESC textureDesc{};
        texture->GetDesc(&textureDesc);
        if (!textureDesc.Width || !textureDesc.Height ||
            textureDesc.Width > 8192 || textureDesc.Height > 8192)
        {
            texture->Release();
            if (sourceProcess)
                CloseHandle(sourceProcess);
            g_lastOverlayError.store(E_INVALIDARG, std::memory_order_relaxed);
            return false;
        }
        D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc{};
        viewDesc.Format = textureDesc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS
            ? DXGI_FORMAT_B8G8R8A8_UNORM : textureDesc.Format;
        viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        viewDesc.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView *view = nullptr;
        result = device->CreateShaderResourceView(texture, &viewDesc, &view);
        const HRESULT explicitViewResult = result;
        if (FAILED(result))
            result = device->CreateShaderResourceView(texture, nullptr, &view);
        if (FAILED(result))
        {
            texture->Release();
            if (sourceProcess)
                CloseHandle(sourceProcess);
            char diagnostic[384]{};
            sprintf_s(diagnostic, sizeof(diagnostic),
                "Shared texture SRV creation failed: explicitHr=0x%08lX "
                "defaultHr=0x%08lX format=%u size=%ux%u",
                static_cast<unsigned long>(explicitViewResult),
                static_cast<unsigned long>(result),
                static_cast<unsigned>(textureDesc.Format),
                textureDesc.Width, textureDesc.Height);
            Log(diagnostic);
            g_lastOverlayError.store(static_cast<long>(result), std::memory_order_relaxed);
            return false;
        }

        D3D11_TEXTURE2D_DESC cacheDesc = textureDesc;
        cacheDesc.Usage = D3D11_USAGE_DEFAULT;
        cacheDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        cacheDesc.CPUAccessFlags = 0;
        cacheDesc.MiscFlags = 0;
        ID3D11Texture2D *cachedTexture = nullptr;
        result = device->CreateTexture2D(&cacheDesc, nullptr, &cachedTexture);
        ID3D11ShaderResourceView *cachedView = nullptr;
        if (SUCCEEDED(result))
            result = device->CreateShaderResourceView(cachedTexture, &viewDesc,
                &cachedView);
        if (FAILED(result))
        {
            if (cachedView) cachedView->Release();
            if (cachedTexture) cachedTexture->Release();
            view->Release();
            texture->Release();
            if (sourceProcess)
                CloseHandle(sourceProcess);
            g_lastOverlayError.store(static_cast<long>(result), std::memory_order_relaxed);
            return false;
        }

        IDXGIKeyedMutex *keyedMutex = nullptr;
        texture->QueryInterface(__uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void **>(&keyedMutex));
        ReleaseOverlayTexture();
        g_overlayTexture = texture;
        g_textureRelay = relay.release();
        g_overlayView = view;
        g_overlayKeyedMutex = keyedMutex;
        g_overlayCachedTexture = cachedTexture;
        g_overlayCachedView = cachedView;
        g_overlaySourceProcess = sourceProcess;
        g_overlaySourcePid = sourcePid;
        g_overlaySourceResourceId = sourceResourceId;
        g_overlayTextureReady.store(true, std::memory_order_release);
        g_lastOverlayError.store(0, std::memory_order_relaxed);
        Log("Opened the CEF shared texture for in-game composition.");
        if (g_textureRelay) Log("Texture transport: same-adapter GPU legacy-handle relay.");
        return true;
    }

    void DrawOverlay(ID3D11Device *device, ID3D11Texture2D *backBuffer,
        UINT backBufferWidth, UINT backBufferHeight)
    {
        PublishConsumerAdapterLuid(device);
        if (!EnsureBridgeMapping())
        {
            // Fail closed: never reuse a cached overlay frame when the live
            // bridge is unavailable or Hook Mode is no longer publishing.
            g_overlayCacheValid = false;
            g_cachedFrameNumber = 0;
            SetDiagnosticStage("bridge-unavailable",
                static_cast<HRESULT>(g_lastOverlayError.load(std::memory_order_relaxed)));
            return;
        }

        std::uint32_t sourcePid = 0;
        std::uint64_t sourceResourceId = 0;
        std::uint64_t frameNumber = 0;
        if (!ReadBridgeFrame(sourcePid, sourceResourceId, frameNumber))
        {
            // A torn/invalid bridge read is not proof of a current frame.
            // Drop the prior image rather than drawing stale pixels again.
            g_overlayCacheValid = false;
            g_cachedFrameNumber = 0;
            SetDiagnosticStage("bridge-frame-not-ready");
            return;
        }
        g_lastBridgeFrameNumber.store(frameNumber, std::memory_order_relaxed);
        if (!frameNumber)
        {
            g_overlayCacheValid = false;
            g_cachedFrameNumber = 0;
            SetDiagnosticStage("overlay-hidden");
            return;
        }

        if (!EnsureRenderResources(device))
        {
            SetDiagnosticStage("render-resources-failed", E_FAIL);
            return;
        }

        HRESULT result = S_OK;
        if (g_overlaySourcePid != sourcePid ||
            g_overlaySourceResourceId != sourceResourceId || !g_overlayCachedView)
        {
            ReleaseOverlayTexture();
            if (!OpenOverlayTexture(device, sourcePid, sourceResourceId))
            {
                SetDiagnosticStage("shared-texture-open-failed",
                    static_cast<HRESULT>(g_lastOverlayError.load(std::memory_order_relaxed)));
                return;
            }
        }
        else if (g_overlaySourceProcess &&
            WaitForSingleObject(g_overlaySourceProcess, 0) != WAIT_TIMEOUT)
        {
            ReleaseOverlayTexture();
            SetDiagnosticStage("source-process-exited",
                HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
            return;
        }

        if (!g_overlayCacheValid || frameNumber != g_cachedFrameNumber)
        {
            // Never wait on the producer's keyed mutex from the game thread.
            // If the first frame is busy, skip it and continue original Present.
            constexpr DWORD mutexTimeoutMs = 0;
            bool acquired = true;
            if (g_textureRelay)
            {
                result = g_textureRelay->Refresh(mutexTimeoutMs);
                acquired = result == S_OK;
                if (!acquired) SetDiagnosticStage("shared-texture-relay-not-ready", result);
            }
            if (acquired && g_overlayKeyedMutex)
            {
                result = g_overlayKeyedMutex->AcquireSync(0, mutexTimeoutMs);
                acquired = result == WAIT_OBJECT_0;
                if (!acquired && result != WAIT_TIMEOUT)
                    SetDiagnosticStage("shared-texture-mutex-failed", result);
                else if (!acquired)
                    SetDiagnosticStage("shared-texture-mutex-busy", result);
            }
            if (acquired)
            {
                g_renderContext->CopyResource(
                    g_overlayCachedTexture, g_overlayTexture);
                if (g_overlayKeyedMutex)
                    g_overlayKeyedMutex->ReleaseSync(0);
                g_cachedFrameNumber = frameNumber;
                g_overlayCacheValid = true;
                g_overlayCacheCopyCount.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (!g_overlayCacheValid)
            return;

        // Reuse the game's EXISTING bound backbuffer view when possible.
        // Crucially, NEVER retain a RTV reference across Present: doing so
        // without hooking ResizeBuffers prevents DXGI from resizing.
        ID3D11RenderTargetView *renderTarget = nullptr;
        g_renderContext->OMGetRenderTargets(1, &renderTarget, nullptr);
        if (renderTarget)
        {
            ID3D11Resource *bound = nullptr;
            renderTarget->GetResource(&bound);
            const bool isBackBuffer =
                bound == static_cast<ID3D11Resource *>(backBuffer);
            if (bound) bound->Release();
            if (!isBackBuffer)
            {
                renderTarget->Release();
                renderTarget = nullptr;
            }
            else
                g_backBufferRtvReuseCount.fetch_add(1, std::memory_order_relaxed);
        }
        if (!renderTarget)
        {
            result = device->CreateRenderTargetView(
                backBuffer, nullptr, &renderTarget);
            if (SUCCEEDED(result) && renderTarget)
                g_backBufferRtvCreateCount.fetch_add(1, std::memory_order_relaxed);
        }
        if (FAILED(result) || !renderTarget)
        {
            SetDiagnosticStage("backbuffer-target-failed", result);
            if (renderTarget) renderTarget->Release();
            return;
        }

        ID3DDeviceContextState *previousState = nullptr;
        g_renderContext->SwapDeviceContextState(
            g_renderContextState, &previousState);
        if (previousState)
        {
            if (backBufferWidth > 0 && backBufferHeight > 0)
            {
                g_diagnosticSwapWidth = backBufferWidth;
                g_diagnosticSwapHeight = backBufferHeight;
                D3D11_VIEWPORT viewport{};
                viewport.Width = static_cast<float>(backBufferWidth);
                viewport.Height = static_cast<float>(backBufferHeight);
                viewport.MaxDepth = 1.0f;

                g_renderContext->OMSetRenderTargets(1, &renderTarget, nullptr);
                g_renderContext->OMSetBlendState(g_overlayBlendState, nullptr, 0xFFFFFFFF);
                g_renderContext->RSSetViewports(1, &viewport);
                g_renderContext->IASetInputLayout(nullptr);
                g_renderContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                g_renderContext->VSSetShader(g_overlayVertexShader, nullptr, 0);
                g_renderContext->PSSetShader(g_overlayPixelShader, nullptr, 0);
                g_renderContext->PSSetShaderResources(0, 1, &g_overlayCachedView);
                g_renderContext->PSSetSamplers(0, 1, &g_overlaySampler);
                g_renderContext->Draw(3, 0);
            }
            g_renderContext->ClearState();
            ID3DDeviceContextState *discardedState = nullptr;
            g_renderContext->SwapDeviceContextState(previousState, &discardedState);
            previousState->Release();
            if (discardedState) discardedState->Release();
            if (backBufferWidth > 0 && backBufferHeight > 0)
            {
                g_overlayDrawCount.fetch_add(1, std::memory_order_relaxed);
                SetDiagnosticStage("draw-ok");
            }
            else
            {
                SetDiagnosticStage("backbuffer-dimensions-unavailable");
            }
        }
        else if (previousState)
        {
            SetDiagnosticStage("context-state-switch-failed");
            previousState->Release();
        }
        renderTarget->Release();
    }

    void DrawOverlay(IDXGISwapChain *swapChain)
    {
        ID3D11Device *device = nullptr;
        HRESULT result = swapChain->GetDevice(__uuidof(ID3D11Device),
            reinterpret_cast<void **>(&device));
        if (FAILED(result) || !device)
        {
            SetDiagnosticStage("swapchain-device-failed", result);
            return;
        }

        ID3D11Texture2D *backBuffer = nullptr;
        result = swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
            reinterpret_cast<void **>(&backBuffer));
        DXGI_SWAP_CHAIN_DESC swapDesc{};
        if (SUCCEEDED(result))
            result = swapChain->GetDesc(&swapDesc);
        if (SUCCEEDED(result) && backBuffer)
            DrawOverlay(device, backBuffer, swapDesc.BufferDesc.Width,
                swapDesc.BufferDesc.Height);
        else
            SetDiagnosticStage("backbuffer-acquire-failed", result);

        if (backBuffer) backBuffer->Release();
        device->Release();
    }
}
namespace duluka::d3d11
{
    void Initialize(LogCallback logger) { g_logCallback = logger; }

    void Shutdown()
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        ReleaseRenderResources();
        if(g_bridgeFrame){UnmapViewOfFile(g_bridgeFrame);g_bridgeFrame=nullptr;}
        if(g_bridgeMapping){CloseHandle(g_bridgeMapping);g_bridgeMapping=nullptr;}
        if(g_consumerAdapterInfo){UnmapViewOfFile(g_consumerAdapterInfo);g_consumerAdapterInfo=nullptr;}
        if(g_consumerAdapterMapping){CloseHandle(g_consumerAdapterMapping);g_consumerAdapterMapping=nullptr;}
        g_bridgeConnected.store(false,std::memory_order_release);
        g_overlayTextureReady.store(false,std::memory_order_release);
    }

    bool OnPresent(IDXGISwapChain *swapChain, bool gameForeground, bool reshadeAddonRegistered)
    {
        std::unique_lock<std::mutex> lock(g_renderMutex,std::try_to_lock);
        if(!lock.owns_lock()) return false;
        if(!reshadeAddonRegistered && swapChain){
            ID3D11Device *device=nullptr;
            if(SUCCEEDED(swapChain->GetDevice(__uuidof(ID3D11Device),reinterpret_cast<void **>(&device))) && device){
                PublishConsumerAdapterLuid(device);device->Release();
            }
        }
        if(!gameForeground){
            const bool discarded=g_overlayCacheValid || g_cachedFrameNumber!=0;
            g_overlayCacheValid=false;g_cachedFrameNumber=0;
            if(discarded){SetDiagnosticStage("hook-inactive-cache-cleared");Log("Hook inactive: dropped the cached overlay frame.");}
        } else if(!reshadeAddonRegistered && swapChain) {
            DrawOverlay(swapChain);
        }
        return true;
    }

    void OnReShadePresent(ID3D11Device *device, ID3D11Texture2D *backBuffer, bool gameForeground)
    {
        if(!device || !backBuffer) return;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        PublishConsumerAdapterLuid(device);
        if(!gameForeground){
            const bool discarded=g_overlayCacheValid || g_cachedFrameNumber!=0;
            g_overlayCacheValid=false;g_cachedFrameNumber=0;
            if(discarded){SetDiagnosticStage("hook-inactive-cache-cleared");Log("Hook inactive: dropped the cached overlay frame.");}
            return;
        }
        D3D11_TEXTURE2D_DESC desc{};backBuffer->GetDesc(&desc);
        DrawOverlay(device,backBuffer,desc.Width,desc.Height);
        g_reshadeCallbackCount.fetch_add(1, std::memory_order_relaxed);
        // Detailed counters are polled off the render thread via GetStatus().
    }
    RendererStatus GetStatus()
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        RendererStatus s{};
        s.drawCount=g_overlayDrawCount.load(std::memory_order_relaxed);
        s.reshadeCallbackCount=g_reshadeCallbackCount.load(std::memory_order_relaxed);
        s.lastBridgeFrameNumber=g_lastBridgeFrameNumber.load(std::memory_order_relaxed);
        s.cacheCopyCount=g_overlayCacheCopyCount.load(std::memory_order_relaxed);
        s.backBufferRtvReuseCount=g_backBufferRtvReuseCount.load(std::memory_order_relaxed);
        s.backBufferRtvCreateCount=g_backBufferRtvCreateCount.load(std::memory_order_relaxed);
        s.cachedFrameNumber=g_cachedFrameNumber;s.cacheValid=g_overlayCacheValid;
        s.bridgeConnected=g_bridgeConnected.load(std::memory_order_acquire);
        s.textureReady=g_overlayTextureReady.load(std::memory_order_acquire);
        s.lastError=g_lastOverlayError.load(std::memory_order_relaxed);
        s.diagnosticHresult=g_diagnosticHresult;s.swapWidth=g_diagnosticSwapWidth;
        s.swapHeight=g_diagnosticSwapHeight;s.diagnosticStage=g_diagnosticStage;
        return s;
    }
}
