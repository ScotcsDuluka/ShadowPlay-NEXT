#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <d3dcompiler.h>
#include <strsafe.h>

#include <atomic>
#include <cstdio>
#include <cstdint>
#include <iterator>
#include <mutex>
#include <string>
#include "../include/osc_frame_bridge.h"
#include "../../../Close Project/ReShadeHookOverlay/vendor/reshade/include/reshade.hpp"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace
{
    using PresentFunction = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain *, UINT, UINT);
    constexpr wchar_t WindowClassName[] = L"DulukaNvOscAdapterProbe";
    std::atomic<std::uint64_t> g_presentCount{ 0 };
    std::atomic<bool> g_hookInstalled{ false };
    std::atomic<bool> g_reshadeAddonRegistered{ false };
    std::atomic<bool> g_firstPresentLogged{ false };
    PresentFunction g_originalPresent = nullptr;
    void **g_presentSlot = nullptr;
    HMODULE g_module = nullptr;
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
    ID3D11ShaderResourceView *g_overlayView = nullptr;
    IDXGIKeyedMutex *g_overlayKeyedMutex = nullptr;
    ID3D11Texture2D *g_overlayCachedTexture = nullptr;
    ID3D11ShaderResourceView *g_overlayCachedView = nullptr;
    std::uint64_t g_cachedFrameNumber = 0;
    bool g_overlayCacheValid = false;
    HANDLE g_overlaySourceProcess = nullptr;
    std::uint32_t g_overlaySourcePid = 0;
    std::uint64_t g_overlaySourceHandle = 0;
    std::uint32_t g_lastLoggedOpenFailurePid = 0;
    std::uint64_t g_lastLoggedOpenFailureHandle = 0;
    ULONGLONG g_nextTextureOpen = 0;
    std::atomic<std::uint64_t> g_overlayDrawCount{ 0 };
    std::atomic<bool> g_bridgeConnected{ false };
    std::atomic<bool> g_overlayTextureReady{ false };
    std::atomic<long> g_lastOverlayError{ 0 };
    const char *g_diagnosticStage = "waiting-for-first-present";
    long g_diagnosticHresult = 0;
    std::uint32_t g_diagnosticSwapWidth = 0;
    std::uint32_t g_diagnosticSwapHeight = 0;

    void Log(const char *message)
    {
        OutputDebugStringA("[NVIDIA OSC Adapter] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");

        wchar_t localAppData[MAX_PATH]{};
        const DWORD pathLength = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
        if (!pathLength || pathLength >= std::size(localAppData))
            return;

        wchar_t directory[MAX_PATH]{};
        if (FAILED(StringCchCopyW(directory, std::size(directory), localAppData)) ||
            FAILED(StringCchCatW(directory, std::size(directory), L"\\NVIDIA")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\OSC Adapter")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\Logs")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\adapter-hook-")))
            return;

        wchar_t filePath[MAX_PATH]{};
        if (FAILED(StringCchCopyW(filePath, std::size(filePath), directory)))
            return;
        wchar_t processId[16]{};
        if (FAILED(StringCchPrintfW(processId, std::size(processId), L"%lu.log",
                GetCurrentProcessId())) ||
            FAILED(StringCchCatW(filePath, std::size(filePath), processId)))
            return;

        HANDLE file = CreateFileW(filePath, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME time{};
        GetSystemTime(&time);
        char line[2048]{};
        const int length = sprintf_s(line, sizeof(line),
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ PID=%lu %s\r\n",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
            time.wSecond, time.wMilliseconds, GetCurrentProcessId(), message);
        if (length > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        }
        CloseHandle(file);
    }

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
                MemoryBarrier();
                g_consumerAdapterInfo->magic =
                    osc_frame_bridge::ConsumerAdapterMagic;
            }
        }

        const LONG currentProcessId = static_cast<LONG>(GetCurrentProcessId());
        if (InterlockedCompareExchange(
                &g_consumerAdapterInfo->ownerProcessId, 0, 0) == currentProcessId &&
            InterlockedCompareExchange64(
                &g_consumerAdapterInfo->adapterLuid, 0, 0) != 0)
            return;

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
        if (g_overlaySourceProcess) { CloseHandle(g_overlaySourceProcess); g_overlaySourceProcess = nullptr; }
        g_overlaySourcePid = 0;
        g_overlaySourceHandle = 0;
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
                        "device=0x%04X adapterLuid=%08lX:%08lX",
                        static_cast<unsigned>(device->GetFeatureLevel()),
                        adapterDesc.VendorId, adapterDesc.DeviceId,
                        static_cast<unsigned long>(adapterDesc.AdapterLuid.HighPart),
                        static_cast<unsigned long>(adapterDesc.AdapterLuid.LowPart));
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

    bool ReadBridgeFrame(std::uint32_t &processId, std::uint64_t &sharedHandle,
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
            const auto handle = g_bridgeFrame->sharedHandle;
            const auto width = g_bridgeFrame->width;
            const auto height = g_bridgeFrame->height;
            const auto number = g_bridgeFrame->frameNumber;
            std::atomic_thread_fence(std::memory_order_acquire);
            if (before != g_bridgeFrame->sequence)
                continue;
            if (!pid || ownerPid != static_cast<LONG>(pid) ||
                !handle || !width || !height ||
                static_cast<std::uint64_t>(width) * height * 4 >
                    osc_frame_bridge::MaxFrameBytes)
                return false;
            processId = pid;
            sharedHandle = handle;
            frameNumber = number;
            return true;
        }
        return false;
    }

    bool OpenOverlayTexture(ID3D11Device *device, std::uint32_t sourcePid,
        std::uint64_t sourceHandle)
    {
        const ULONGLONG now = GetTickCount64();
        if (now < g_nextTextureOpen)
            return false;
        g_nextTextureOpen = now + 1000;

        HANDLE sourceProcess = OpenProcess(
            PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            FALSE, sourcePid);
        if (!sourceProcess)
        {
            g_lastOverlayError.store(
                static_cast<long>(HRESULT_FROM_WIN32(GetLastError())),
                std::memory_order_relaxed);
            return false;
        }
        if (WaitForSingleObject(sourceProcess, 0) != WAIT_TIMEOUT)
        {
            CloseHandle(sourceProcess);
            g_lastOverlayError.store(
                static_cast<long>(HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED)),
                std::memory_order_relaxed);
            return false;
        }

        HANDLE localHandle = nullptr;
        const BOOL duplicated = DuplicateHandle(sourceProcess,
            reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(sourceHandle)),
            GetCurrentProcess(), &localHandle, 0, FALSE, DUPLICATE_SAME_ACCESS);
        const HRESULT duplicateError = duplicated
            ? S_OK : HRESULT_FROM_WIN32(GetLastError());

        ID3D11Device1 *device1 = nullptr;
        ID3D11Resource *resource = nullptr;
        HRESULT device1Result = E_NOINTERFACE;
        HRESULT openNtResult = E_HANDLE;
        HRESULT openDuplicatedLegacyResult = E_HANDLE;
        HRESULT openSourceLegacyResult = E_HANDLE;
        HRESULT result = E_HANDLE;
        if (duplicated)
        {
            device1Result = device->QueryInterface(__uuidof(ID3D11Device1),
                reinterpret_cast<void **>(&device1));
            if (SUCCEEDED(device1Result))
            {
                openNtResult = device1->OpenSharedResource1(localHandle,
                    __uuidof(ID3D11Resource), reinterpret_cast<void **>(&resource));
                result = openNtResult;
                if (FAILED(openNtResult))
                {
                    openDuplicatedLegacyResult = device->OpenSharedResource(localHandle,
                        __uuidof(ID3D11Resource), reinterpret_cast<void **>(&resource));
                    result = openDuplicatedLegacyResult;
                }
                device1->Release();
            }
            else
                result = device1Result;
            CloseHandle(localHandle);
        }

        // Legacy DXGI shared handles are system-wide tokens rather than NT
        // handles, so DuplicateHandle is not applicable to those resources.
        if (FAILED(result))
        {
            openSourceLegacyResult = device->OpenSharedResource(
                reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(sourceHandle)),
                __uuidof(ID3D11Resource), reinterpret_cast<void **>(&resource));
            result = openSourceLegacyResult;
        }
        if (FAILED(result) || !resource)
        {
            CloseHandle(sourceProcess);
            if (g_lastLoggedOpenFailurePid != sourcePid ||
                g_lastLoggedOpenFailureHandle != sourceHandle)
            {
                g_lastLoggedOpenFailurePid = sourcePid;
                g_lastLoggedOpenFailureHandle = sourceHandle;
                char diagnostic[512]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "Shared texture open failed: sourcePid=%lu handle=0x%llX "
                    "duplicate=%s duplicateHr=0x%08lX device1Hr=0x%08lX "
                    "openNtHr=0x%08lX openDuplicatedLegacyHr=0x%08lX "
                    "openSourceLegacyHr=0x%08lX",
                    sourcePid,
                    static_cast<unsigned long long>(sourceHandle),
                    duplicated ? "yes" : "no",
                    static_cast<unsigned long>(duplicateError),
                    static_cast<unsigned long>(device1Result),
                    static_cast<unsigned long>(openNtResult),
                    static_cast<unsigned long>(openDuplicatedLegacyResult),
                    static_cast<unsigned long>(openSourceLegacyResult));
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
            CloseHandle(sourceProcess);
            g_lastOverlayError.store(static_cast<long>(result), std::memory_order_relaxed);
            return false;
        }

        IDXGIKeyedMutex *keyedMutex = nullptr;
        texture->QueryInterface(__uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void **>(&keyedMutex));
        ReleaseOverlayTexture();
        g_overlayTexture = texture;
        g_overlayView = view;
        g_overlayKeyedMutex = keyedMutex;
        g_overlayCachedTexture = cachedTexture;
        g_overlayCachedView = cachedView;
        g_overlaySourceProcess = sourceProcess;
        g_overlaySourcePid = sourcePid;
        g_overlaySourceHandle = sourceHandle;
        g_overlayTextureReady.store(true, std::memory_order_release);
        g_lastOverlayError.store(0, std::memory_order_relaxed);
        Log("Opened the CEF shared texture for in-game composition.");
        return true;
    }

    void DrawOverlay(ID3D11Device *device, ID3D11Texture2D *backBuffer,
        UINT backBufferWidth, UINT backBufferHeight)
    {
        PublishConsumerAdapterLuid(device);
        if (!EnsureBridgeMapping())
        {
            SetDiagnosticStage("bridge-unavailable",
                static_cast<HRESULT>(g_lastOverlayError.load(std::memory_order_relaxed)));
            return;
        }

        std::uint32_t sourcePid = 0;
        std::uint64_t sourceHandle = 0;
        std::uint64_t frameNumber = 0;
        if (!ReadBridgeFrame(sourcePid, sourceHandle, frameNumber))
        {
            SetDiagnosticStage("bridge-frame-not-ready");
            return;
        }
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
            g_overlaySourceHandle != sourceHandle || !g_overlayCachedView)
        {
            ReleaseOverlayTexture();
            if (!OpenOverlayTexture(device, sourcePid, sourceHandle))
            {
                SetDiagnosticStage("shared-texture-open-failed",
                    static_cast<HRESULT>(g_lastOverlayError.load(std::memory_order_relaxed)));
                return;
            }
        }
        else if (!g_overlaySourceProcess ||
            WaitForSingleObject(g_overlaySourceProcess, 0) != WAIT_TIMEOUT)
        {
            ReleaseOverlayTexture();
            SetDiagnosticStage("source-process-exited",
                HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
            return;
        }

        if (!g_overlayCacheValid || frameNumber != g_cachedFrameNumber)
        {
            bool acquired = true;
            if (g_overlayKeyedMutex)
            {
                result = g_overlayKeyedMutex->AcquireSync(0, 0);
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
            }
        }
        if (!g_overlayCacheValid)
            return;

        ID3D11RenderTargetView *renderTarget = nullptr;
        result = device->CreateRenderTargetView(
            backBuffer, nullptr, &renderTarget);
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
                ID3D11ShaderResourceView *nullView = nullptr;
                g_renderContext->PSSetShaderResources(0, 1, &nullView);
            }
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

    bool IsGameProcessForeground()
    {
        wchar_t testMappingName[128]{};
        wchar_t expectedTestMappingName[128]{};
        if (GetEnvironmentVariableW(
                osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
                testMappingName, static_cast<DWORD>(std::size(testMappingName))) > 0 &&
            swprintf_s(expectedTestMappingName,
                std::size(expectedTestMappingName),
                L"Local\\NVIDIAOSC.Frame.Test.ConsumerAdapter.%lu",
                GetCurrentProcessId()) >= 0 &&
            wcscmp(testMappingName, expectedTestMappingName) == 0)
            return true;
        return osc_frame_bridge::IsGameOrNvidiaOverlayForeground(
            GetCurrentProcessId());
    }

    void OnReShadePresent(reshade::api::effect_runtime *runtime)
    {
        if (!runtime)
            return;
        reshade::api::device *reshadeDevice = runtime->get_device();
        if (!reshadeDevice ||
            reshadeDevice->get_api() != reshade::api::device_api::d3d11)
            return;

        auto *device = reinterpret_cast<ID3D11Device *>(
            static_cast<std::uintptr_t>(reshadeDevice->get_native()));
        if (!device)
            return;
        PublishConsumerAdapterLuid(device);

        const auto backBufferHandle = runtime->get_current_back_buffer().handle;
        auto *backBuffer = reinterpret_cast<ID3D11Texture2D *>(
            static_cast<std::uintptr_t>(backBufferHandle));
        if (!backBuffer || !IsGameProcessForeground())
            return;

        D3D11_TEXTURE2D_DESC backBufferDesc{};
        backBuffer->GetDesc(&backBufferDesc);
        std::lock_guard<std::mutex> lock(g_renderMutex);
        DrawOverlay(device, backBuffer, backBufferDesc.Width,
            backBufferDesc.Height);
    }

    bool RegisterReShadeAddon()
    {
        if (!reshade::register_addon(g_module))
        {
            Log("ReShade add-on API unavailable; using the regular DXGI Present compositor.");
            return false;
        }

        reshade::register_event<reshade::addon_event::reshade_present>(
            &OnReShadePresent);
        g_reshadeAddonRegistered.store(true, std::memory_order_release);
        Log("Registered ReShade post-overlay compositor; OSC will render after ReShade effects.");
        return true;
    }

    void UnregisterReShadeAddon()
    {
        if (!g_reshadeAddonRegistered.exchange(false, std::memory_order_acq_rel))
            return;

        reshade::unregister_event<reshade::addon_event::reshade_present>(
            &OnReShadePresent);
        reshade::unregister_addon(g_module);
    }

    HRESULT STDMETHODCALLTYPE HookPresent(
        IDXGISwapChain *swapChain,
        UINT syncInterval,
        UINT flags)
    {
        const auto presentCount = g_presentCount.fetch_add(
            1, std::memory_order_relaxed) + 1;
        {
            std::lock_guard<std::mutex> lock(g_renderMutex);
            if (!g_reshadeAddonRegistered.load(std::memory_order_acquire))
            {
                ID3D11Device *device = nullptr;
                if (SUCCEEDED(swapChain->GetDevice(
                        __uuidof(ID3D11Device),
                        reinterpret_cast<void **>(&device))) && device)
                {
                    PublishConsumerAdapterLuid(device);
                    device->Release();
                }
            }
            if (IsGameProcessForeground() &&
                !g_reshadeAddonRegistered.load(std::memory_order_acquire))
                DrawOverlay(swapChain);
            if (presentCount == 1 || presentCount % 1200 == 0)
            {
                char diagnostic[512]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "Present diagnostics: presents=%llu draws=%llu bridge=%s texture=%s "
                    "stage=%s hr=0x%08lX swap=%ux%u error=0x%08lX",
                    static_cast<unsigned long long>(presentCount),
                    static_cast<unsigned long long>(
                        g_overlayDrawCount.load(std::memory_order_relaxed)),
                    g_bridgeConnected.load(std::memory_order_acquire) ? "connected" : "disconnected",
                    g_overlayTextureReady.load(std::memory_order_acquire) ? "ready" : "not-ready",
                    g_diagnosticStage,
                    static_cast<unsigned long>(g_diagnosticHresult),
                    g_diagnosticSwapWidth,
                    g_diagnosticSwapHeight,
                    static_cast<unsigned long>(
                        g_lastOverlayError.load(std::memory_order_relaxed)));
                Log(diagnostic);
            }
        }
        if (!g_firstPresentLogged.exchange(true, std::memory_order_relaxed))
            Log("DXGI Present hook received its first frame callback.");
        return g_originalPresent(swapChain, syncInterval, flags);
    }

    LRESULT CALLBACK ProbeWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool InstallDxgiPresentHook()
    {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = ProbeWindowProc;
        windowClass.hInstance = g_module;
        windowClass.lpszClassName = WindowClassName;

        const ATOM classAtom = RegisterClassExW(&windowClass);
        if (classAtom == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            Log("RegisterClassExW failed while locating the DXGI Present slot.");
            return false;
        }

        HWND window = CreateWindowExW(
            0, WindowClassName, L"", WS_OVERLAPPED,
            0, 0, 2, 2, nullptr, nullptr, g_module, nullptr);
        if (!window)
        {
            Log("CreateWindowExW failed while locating the DXGI Present slot.");
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        DXGI_SWAP_CHAIN_DESC swapChainDesc{};
        swapChainDesc.BufferDesc.Width = 2;
        swapChainDesc.BufferDesc.Height = 2;
        swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swapChainDesc.SampleDesc.Count = 1;
        swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapChainDesc.BufferCount = 1;
        swapChainDesc.OutputWindow = window;
        swapChainDesc.Windowed = TRUE;
        swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ID3D11Device *device = nullptr;
        ID3D11DeviceContext *context = nullptr;
        IDXGISwapChain *swapChain = nullptr;
        D3D_FEATURE_LEVEL featureLevel{};
        HRESULT result = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &swapChainDesc,
            &swapChain,
            &device,
            &featureLevel,
            &context);

        if (FAILED(result))
        {
            result = D3D11CreateDeviceAndSwapChain(
                nullptr,
                D3D_DRIVER_TYPE_WARP,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                &swapChainDesc,
                &swapChain,
                &device,
                &featureLevel,
                &context);
        }

        if (FAILED(result) || !swapChain)
        {
            Log("Could not create a temporary DXGI swapchain to resolve its Present slot.");
            if (context) context->Release();
            if (device) device->Release();
            if (swapChain) swapChain->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        auto vtable = *reinterpret_cast<void ***>(swapChain);
        void **presentSlot = &vtable[8];
        auto originalPresent = reinterpret_cast<PresentFunction>(*presentSlot);
        if (originalPresent == &HookPresent)
        {
            Log("DXGI Present is already hooked; refusing a recursive second installation.");
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }
        g_originalPresent = originalPresent;
        g_presentSlot = presentSlot;
        DWORD oldProtection = 0;
        if (!VirtualProtect(presentSlot, sizeof(void *), PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            Log("VirtualProtect failed for the DXGI Present vtable slot.");
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        const auto previous = InterlockedExchangePointer(
            reinterpret_cast<PVOID volatile *>(presentSlot),
            reinterpret_cast<void *>(&HookPresent));
        DWORD ignoredProtection = 0;
        VirtualProtect(presentSlot, sizeof(void *), oldProtection, &ignoredProtection);
        FlushInstructionCache(GetCurrentProcess(), presentSlot, sizeof(void *));

        if (previous != reinterpret_cast<void *>(originalPresent))
        {
            DWORD restoreProtection = 0;
            if (VirtualProtect(presentSlot, sizeof(void *), PAGE_EXECUTE_READWRITE, &restoreProtection))
            {
                InterlockedCompareExchangePointer(
                    reinterpret_cast<PVOID volatile *>(presentSlot), previous,
                    reinterpret_cast<void *>(&HookPresent));
                VirtualProtect(presentSlot, sizeof(void *), restoreProtection, &ignoredProtection);
            }
            g_originalPresent = nullptr;
            g_presentSlot = nullptr;
            Log("DXGI Present vtable was changed by another hook; adapter declined to chain it.");
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }
        g_hookInstalled.store(true, std::memory_order_release);

        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        if (classAtom) UnregisterClassW(WindowClassName, g_module);

        Log("DXGI IDXGISwapChain::Present hook installed (x64).");
        return true;
    }

    DWORD WINAPI InitializeHook(void *)
    {
        RegisterReShadeAddon();
        InstallDxgiPresentHook();
        return 0;
    }

    void RemoveDxgiPresentHook()
    {
        if (!g_hookInstalled.exchange(false, std::memory_order_acq_rel) || !g_presentSlot)
            return;

        DWORD oldProtection = 0;
        if (!VirtualProtect(g_presentSlot, sizeof(void *), PAGE_EXECUTE_READWRITE, &oldProtection))
            return;

        auto current = InterlockedCompareExchangePointer(
            reinterpret_cast<PVOID volatile *>(g_presentSlot),
            reinterpret_cast<void *>(g_originalPresent),
            reinterpret_cast<void *>(&HookPresent));
        DWORD ignoredProtection = 0;
        VirtualProtect(g_presentSlot, sizeof(void *), oldProtection, &ignoredProtection);
        if (current == reinterpret_cast<void *>(&HookPresent))
            Log("DXGI Present hook removed.");
    }
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetStatus(
    std::uint64_t *presentCount,
    BOOL *hookInstalled)
{
    if (!presentCount || !hookInstalled)
        return FALSE;
    *presentCount = g_presentCount.load(std::memory_order_relaxed);
    *hookInstalled = g_hookInstalled.load(std::memory_order_acquire) ? TRUE : FALSE;
    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetOverlayStatus(
    std::uint64_t *drawCount,
    BOOL *bridgeConnected,
    BOOL *textureReady,
    long *lastError)
{
    if (!drawCount || !bridgeConnected || !textureReady || !lastError)
        return FALSE;
    *drawCount = g_overlayDrawCount.load(std::memory_order_relaxed);
    *bridgeConnected = g_bridgeConnected.load(std::memory_order_acquire) ? TRUE : FALSE;
    *textureReady = g_overlayTextureReady.load(std::memory_order_acquire) ? TRUE : FALSE;
    *lastError = g_lastOverlayError.load(std::memory_order_relaxed);
    return TRUE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, InitializeHook, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
        else
            OutputDebugStringA("[NVIDIA OSC Adapter] Failed to start hook initialization thread.\n");
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        UnregisterReShadeAddon();
        RemoveDxgiPresentHook();
    }
    return TRUE;
}
