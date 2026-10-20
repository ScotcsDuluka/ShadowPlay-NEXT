#include "D3D9Renderer.h"
#include <d3d9types.h>
#include <dxgi1_2.h>
#include <strsafe.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string>
#include <vector>
#include "../../../include/osc_frame_bridge.h"
#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "dxgi.lib")
namespace
{
    void Log(const char *message)
    {
        OutputDebugStringA("[NVIDIA OSC Adapter D3D9 Renderer] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");
    }

    std::atomic<std::uint64_t> g_drawCount{ 0 };
    std::mutex g_renderMutex;
    HANDLE g_frameMapping = nullptr;
    const osc_frame_bridge::D3D9Frame *g_frame = nullptr;
    HANDLE g_consumerMapping = nullptr;
    osc_frame_bridge::ConsumerAdapterInfo *g_consumerInfo = nullptr;
    IDirect3DTexture9 *g_overlayTexture = nullptr;
    IDirect3DStateBlock9 *g_stateBlock = nullptr;
    IDirect3DDevice9 *g_resourceDevice = nullptr;
    std::vector<std::uint8_t> g_pixels;
    std::uint64_t g_cachedFrameNumber = 0;
    std::uint32_t g_cachedWidth = 0;
    std::uint32_t g_cachedHeight = 0;
    std::uint32_t g_textureWidth = 0;
    std::uint32_t g_textureHeight = 0;
    std::uint64_t g_uploadedFrameNumber = 0;
    LONG g_cachedPublisherPid = 0;

    struct Vertex
    {
        float x;
        float y;
        float z;
        float rhw;
        D3DCOLOR color;
        float u;
        float v;
    };

    constexpr DWORD VertexFormat =
        D3DFVF_XYZRHW | D3DFVF_DIFFUSE | D3DFVF_TEX1;

    bool EnsureFrameMapping()
    {
        if (g_frame)
            return g_frame->magic == osc_frame_bridge::D3D9FrameMagic &&
                g_frame->version == osc_frame_bridge::D3D9FrameVersion;

        wchar_t testMappingName[128]{};
        const DWORD nameLength = GetEnvironmentVariableW(
            osc_frame_bridge::D3D9FrameMappingNameEnvironmentVariable,
            testMappingName, static_cast<DWORD>(std::size(testMappingName)));
        if (nameLength >= std::size(testMappingName))
        {
            Log("The configured D3D9 frame mapping name is too long.");
            return false;
        }
        const wchar_t *mappingName = nameLength
            ? testMappingName : osc_frame_bridge::D3D9FrameMappingName;
        g_frameMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName);
        if (!g_frameMapping)
            return false;
        g_frame = static_cast<const osc_frame_bridge::D3D9Frame *>(
            MapViewOfFile(g_frameMapping, FILE_MAP_READ, 0, 0,
                osc_frame_bridge::D3D9FrameMappingBytes));
        if (!g_frame)
        {
            CloseHandle(g_frameMapping);
            g_frameMapping = nullptr;
            return false;
        }
        if (g_frame->magic != osc_frame_bridge::D3D9FrameMagic ||
            g_frame->version != osc_frame_bridge::D3D9FrameVersion)
        {
            UnmapViewOfFile(g_frame);
            CloseHandle(g_frameMapping);
            g_frame = nullptr;
            g_frameMapping = nullptr;
            return false;
        }
        Log("Connected to the OSC D3D9 CPU frame bridge.");
        return true;
    }

    bool ReadConsumerMappingName(wchar_t (&name)[128])
    {
        const DWORD length = GetEnvironmentVariableW(
            osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
            name, static_cast<DWORD>(std::size(name)));
        if (length >= std::size(name))
        {
            Log("The configured consumer adapter mapping name is too long.");
            return false;
        }
        if (!length)
            wcscpy_s(name, osc_frame_bridge::ConsumerAdapterMappingName);
        return true;
    }

    bool GetMonitorAdapterLuid(HMONITOR monitor, LUID &luid)
    {
        if (!monitor)
            return false;
        IDXGIFactory1 *factory = nullptr;
        HRESULT result = CreateDXGIFactory1(__uuidof(IDXGIFactory1),
            reinterpret_cast<void **>(&factory));
        if (FAILED(result) || !factory)
            return false;

        bool found = false;
        for (UINT adapterIndex = 0; !found; ++adapterIndex)
        {
            IDXGIAdapter1 *adapter = nullptr;
            result = factory->EnumAdapters1(adapterIndex, &adapter);
            if (result == DXGI_ERROR_NOT_FOUND)
                break;
            if (FAILED(result))
                break;

            DXGI_ADAPTER_DESC1 adapterDescription{};
            if (SUCCEEDED(adapter->GetDesc1(&adapterDescription)))
            {
                for (UINT outputIndex = 0; ; ++outputIndex)
                {
                    IDXGIOutput *output = nullptr;
                    result = adapter->EnumOutputs(outputIndex, &output);
                    if (result == DXGI_ERROR_NOT_FOUND)
                        break;
                    if (FAILED(result))
                        break;
                    DXGI_OUTPUT_DESC outputDescription{};
                    const bool outputMatches =
                        SUCCEEDED(output->GetDesc(&outputDescription)) &&
                        outputDescription.Monitor == monitor;
                    output->Release();
                    if (outputMatches)
                    {
                        luid = adapterDescription.AdapterLuid;
                        found = true;
                        break;
                    }
                }
            }
            adapter->Release();
        }
        factory->Release();
        return found;
    }

    void PublishConsumerAdapter(IDirect3DDevice9 *device)
    {
        if (!device)
            return;
        if (!g_consumerInfo)
        {
            wchar_t mappingName[128]{};
            if (!ReadConsumerMappingName(mappingName))
                return;
            g_consumerMapping = CreateFileMappingW(
                INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                0, sizeof(osc_frame_bridge::ConsumerAdapterInfo), mappingName);
            if (!g_consumerMapping)
                return;
            g_consumerInfo =
                static_cast<osc_frame_bridge::ConsumerAdapterInfo *>(
                    MapViewOfFile(g_consumerMapping, FILE_MAP_ALL_ACCESS,
                        0, 0, sizeof(osc_frame_bridge::ConsumerAdapterInfo)));
            if (!g_consumerInfo)
            {
                CloseHandle(g_consumerMapping);
                g_consumerMapping = nullptr;
                return;
            }
            if (g_consumerInfo->magic != osc_frame_bridge::ConsumerAdapterMagic ||
                g_consumerInfo->version != osc_frame_bridge::ConsumerAdapterVersion)
            {
                g_consumerInfo->magic = 0;
                g_consumerInfo->version =
                    osc_frame_bridge::ConsumerAdapterVersion;
                InterlockedExchange(&g_consumerInfo->ownerProcessId, 0);
                InterlockedExchange64(&g_consumerInfo->adapterLuid, 0);
                InterlockedExchange(&g_consumerInfo->renderApi, 0);
                MemoryBarrier();
                g_consumerInfo->magic = osc_frame_bridge::ConsumerAdapterMagic;
            }
        }

        const LONG currentPid = static_cast<LONG>(GetCurrentProcessId());
        if (InterlockedCompareExchange(
                &g_consumerInfo->ownerProcessId, 0, 0) == currentPid &&
            InterlockedCompareExchange64(
                &g_consumerInfo->adapterLuid, 0, 0) != 0)
        {
            InterlockedExchange(&g_consumerInfo->renderApi,
                static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9));
            return;
        }

        D3DDEVICE_CREATION_PARAMETERS creation{};
        if (FAILED(device->GetCreationParameters(&creation)))
            return;
        IDirect3D9 *direct3d = nullptr;
        if (FAILED(device->GetDirect3D(&direct3d)) || !direct3d)
            return;
        IDirect3D9Ex *direct3dEx = nullptr;
        HRESULT result = direct3d->QueryInterface(
            __uuidof(IDirect3D9Ex), reinterpret_cast<void **>(&direct3dEx));
        LUID luid{};
        bool luidFound = false;
        if (SUCCEEDED(result) && direct3dEx)
        {
            luidFound = SUCCEEDED(
                direct3dEx->GetAdapterLUID(creation.AdapterOrdinal, &luid));
            direct3dEx->Release();
        }
        if (!luidFound)
            luidFound = GetMonitorAdapterLuid(
                direct3d->GetAdapterMonitor(creation.AdapterOrdinal), luid);
        direct3d->Release();
        if (!luidFound)
        {
            Log("Could not map the D3D9 display monitor to a DXGI adapter LUID.");
            return;
        }
        const std::uint64_t packed =
            (static_cast<std::uint64_t>(
                static_cast<std::uint32_t>(luid.HighPart)) << 32) |
            luid.LowPart;
        InterlockedExchange64(&g_consumerInfo->adapterLuid,
            static_cast<LONG64>(packed));
        InterlockedExchange(&g_consumerInfo->renderApi,
            static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9));
        InterlockedExchange(&g_consumerInfo->ownerProcessId, currentPid);
        Log("Published the D3D9 game adapter identity.");
    }

    bool SnapshotFrame(std::uint64_t &frameNumber,
        std::uint32_t &width, std::uint32_t &height)
    {
        if (!EnsureFrameMapping())
            return false;
        const LONG ownerPid = g_frame->ownerProcessId;
        if (ownerPid <= 0)
            return false;
        HANDLE publisher = OpenProcess(SYNCHRONIZE, FALSE,
            static_cast<DWORD>(ownerPid));
        if (!publisher)
            return false;
        const bool exited = WaitForSingleObject(publisher, 0) == WAIT_OBJECT_0;
        CloseHandle(publisher);
        if (exited)
        {
            return false;
        }
        if (ownerPid != g_cachedPublisherPid)
        {
            g_cachedPublisherPid = ownerPid;
            g_cachedFrameNumber = 0;
            g_uploadedFrameNumber = 0;
            g_pixels.clear();
        }

        for (int attempt = 0; attempt < 3; ++attempt)
        {
            const LONG before = g_frame->sequence;
            if (before & 1)
                continue;
            MemoryBarrier();
            const auto nextFrame =
                static_cast<std::uint64_t>(g_frame->frameNumber);
            const auto nextWidth = g_frame->width;
            const auto nextHeight = g_frame->height;
            const auto rowPitch = g_frame->rowPitch;
            const auto pixelBytes = g_frame->pixelBytes;
            const std::uint64_t expectedBytes =
                static_cast<std::uint64_t>(nextWidth) * nextHeight * 4;
            if (before != g_frame->sequence)
                continue;

            if (!nextFrame)
            {
                frameNumber = 0;
                width = 0;
                height = 0;
                return true;
            }
            if (!nextWidth || !nextHeight ||
                rowPitch < static_cast<std::uint64_t>(nextWidth) * 4 ||
                pixelBytes != expectedBytes ||
                expectedBytes > osc_frame_bridge::MaxFrameBytes)
                return false;
            if (nextFrame == g_cachedFrameNumber &&
                nextWidth == g_cachedWidth && nextHeight == g_cachedHeight)
            {
                frameNumber = nextFrame;
                width = nextWidth;
                height = nextHeight;
                return true;
            }

            g_pixels.resize(static_cast<std::size_t>(expectedBytes));
            const std::size_t packedPitch =
                static_cast<std::size_t>(nextWidth) * 4;
            for (std::uint32_t row = 0; row < nextHeight; ++row)
                std::memcpy(g_pixels.data() + static_cast<std::size_t>(row) * packedPitch,
                    g_frame->pixels + static_cast<std::size_t>(row) * rowPitch,
                    packedPitch);
            MemoryBarrier();
            if (before != g_frame->sequence)
                continue;
            g_cachedFrameNumber = nextFrame;
            g_cachedWidth = nextWidth;
            g_cachedHeight = nextHeight;
            frameNumber = nextFrame;
            width = nextWidth;
            height = nextHeight;
            return true;
        }
        return false;
    }

    void ReleaseDeviceResources()
    {
        if (g_stateBlock)
        {
            g_stateBlock->Release();
            g_stateBlock = nullptr;
        }
        if (g_overlayTexture)
        {
            g_overlayTexture->Release();
            g_overlayTexture = nullptr;
        }
        g_textureWidth = 0;
        g_textureHeight = 0;
        g_uploadedFrameNumber = 0;
    }

    bool EnsureDeviceResources(IDirect3DDevice9 *device,
        std::uint32_t width, std::uint32_t height)
    {
        if (!g_overlayTexture || width != g_textureWidth ||
            height != g_textureHeight)
        {
            if (g_overlayTexture)
            {
                g_overlayTexture->Release();
                g_overlayTexture = nullptr;
            }
            HRESULT result = device->CreateTexture(width, height, 1,
                D3DUSAGE_DYNAMIC, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
                &g_overlayTexture, nullptr);
            if (FAILED(result))
            {
                char diagnostic[160]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "D3D9 dynamic texture creation failed: hr=0x%08lX",
                    static_cast<unsigned long>(result));
                Log(diagnostic);
                return false;
            }
            g_textureWidth = width;
            g_textureHeight = height;
        }
        if (!g_stateBlock)
        {
            const HRESULT result = device->CreateStateBlock(
                D3DSBT_ALL, &g_stateBlock);
            if (FAILED(result))
                return false;
        }
        return true;
    }

    void DrawOverlay(IDirect3DDevice9 *device, bool sceneMayBeActive)
    {
        if (g_resourceDevice != device)
        {
            ReleaseDeviceResources();
            g_resourceDevice = device;
        }
        PublishConsumerAdapter(device);
        std::uint64_t frameNumber = 0;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        if (!SnapshotFrame(frameNumber, width, height))
            return;
        if (!frameNumber)
        {
            g_cachedFrameNumber = 0;
            g_pixels.clear();
            return;
        }
        if (g_pixels.empty() || !EnsureDeviceResources(device, width, height))
            return;

        if (g_overlayTexture && frameNumber != g_uploadedFrameNumber)
        {
            D3DLOCKED_RECT locked{};
            HRESULT result = g_overlayTexture->LockRect(
                0, &locked, nullptr, D3DLOCK_DISCARD);
            if (FAILED(result))
                return;
            const std::size_t rowBytes = static_cast<std::size_t>(width) * 4;
            for (std::uint32_t row = 0; row < height; ++row)
                std::memcpy(static_cast<std::uint8_t *>(locked.pBits) +
                        static_cast<std::size_t>(row) * locked.Pitch,
                    g_pixels.data() + static_cast<std::size_t>(row) * rowBytes,
                    rowBytes);
            result = g_overlayTexture->UnlockRect(0);
            if (FAILED(result))
                return;
            g_uploadedFrameNumber = frameNumber;
        }

        D3DVIEWPORT9 viewport{};
        if (FAILED(device->GetViewport(&viewport)) ||
            !viewport.Width || !viewport.Height)
            return;
        if (FAILED(g_stateBlock->Capture()))
            return;

        const float left = static_cast<float>(viewport.X);
        const float top = static_cast<float>(viewport.Y);
        const float right = left + static_cast<float>(viewport.Width);
        const float bottom = top + static_cast<float>(viewport.Height);
        const Vertex vertices[] = {
            { left - 0.5f, top - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 0.0f, 0.0f },
            { right - 0.5f, top - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 1.0f, 0.0f },
            { left - 0.5f, bottom - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 0.0f, 1.0f },
            { right - 0.5f, bottom - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 1.0f, 1.0f }
        };

        device->SetVertexShader(nullptr);
        device->SetPixelShader(nullptr);
        device->SetFVF(VertexFormat);
        device->SetTexture(0, g_overlayTexture);
        device->SetRenderState(D3DRS_ZENABLE, FALSE);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
        device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        device->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD);
        device->SetRenderState(D3DRS_LIGHTING, FALSE);
        device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        device->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);
        device->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
        device->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
        device->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        device->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
        device->SetTextureStageState(1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);
        device->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);
        device->SetSamplerState(0, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
        device->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
        device->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
        bool openedScene = false;
        if (sceneMayBeActive)
        {
            // Inside the ReShade present event a scene is usually already
            // active; BeginScene failing means we draw into that open scene.
            openedScene = SUCCEEDED(device->BeginScene());
        }
        else
        {
            if (FAILED(device->BeginScene()))
            {
                g_stateBlock->Apply();
                return;
            }
            openedScene = true;
        }
        const HRESULT drawResult = device->DrawPrimitiveUP(
            D3DPT_TRIANGLESTRIP, 2, vertices, sizeof(Vertex));
        const HRESULT endResult = openedScene ? device->EndScene() : S_OK;
        g_stateBlock->Apply();
        if (SUCCEEDED(drawResult) && SUCCEEDED(endResult))
            g_drawCount.fetch_add(1, std::memory_order_relaxed);
    }
}
namespace duluka::d3d9
{
    void OnPresent(IDirect3DDevice9 *device)
    {
        if (!device) return;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        DrawOverlay(device, false);
    }
    void OnReShadePresent(IDirect3DDevice9 *device,
        IDirect3DSurface9 *backBuffer, bool gameForeground)
    {
        if (!device || !backBuffer || !gameForeground) return;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        IDirect3DSurface9 *previousTarget = nullptr;
        if (FAILED(device->GetRenderTarget(0, &previousTarget)) ||
            !previousTarget)
            return;
        const bool switched = previousTarget != backBuffer &&
            SUCCEEDED(device->SetRenderTarget(0, backBuffer));
        DrawOverlay(device, true);
        if (switched)
            device->SetRenderTarget(0, previousTarget);
        previousTarget->Release();
    }
    void OnReset(IDirect3DDevice9 *device)
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        if (device && device != g_resourceDevice) return;
        ReleaseDeviceResources();
        g_resourceDevice = nullptr;
    }
    void RenderForTest(IDirect3DDevice9 *device)
    {
        if (!device) return;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        DrawOverlay(device, false);
    }
    std::uint64_t GetDrawCount()
    {
        return g_drawCount.load(std::memory_order_relaxed);
    }
    void Shutdown()
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        ReleaseDeviceResources();
        if(g_frame){UnmapViewOfFile(g_frame);g_frame=nullptr;}
        if(g_frameMapping){CloseHandle(g_frameMapping);g_frameMapping=nullptr;}
        if(g_consumerInfo){UnmapViewOfFile(g_consumerInfo);g_consumerInfo=nullptr;}
        if(g_consumerMapping){CloseHandle(g_consumerMapping);g_consumerMapping=nullptr;}
    }
}
