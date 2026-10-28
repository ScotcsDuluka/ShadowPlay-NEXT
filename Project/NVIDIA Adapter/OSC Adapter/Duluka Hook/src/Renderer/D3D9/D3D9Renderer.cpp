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
#include "../../../include/d3d9_pipeline_timing_diagnostics.h"
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
    // Cheap, lock-free status reads for the adapter Host.
    std::atomic<std::uint64_t> g_adapterLuid{ 0 };
    std::atomic<bool> g_isD3D9Ex{ false };
    std::atomic<bool> g_gpuFrameBound{ false };
    std::atomic<std::uint64_t> g_busyFrameSkips{ 0 };
    std::atomic<std::uint64_t> g_snapshotSamples{ 0 };
    std::atomic<std::uint64_t> g_snapshotTotalTicks{ 0 };
    std::atomic<std::uint64_t> g_snapshotMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_snapshotCacheHits{ 0 };
    std::atomic<std::uint64_t> g_cpuFrameCopiedBytes{ 0 };
    std::atomic<std::uint64_t> g_uploadAttempts{ 0 };
    std::atomic<std::uint64_t> g_uploadTotalTicks{ 0 };
    std::atomic<std::uint64_t> g_uploadMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_uploadedBytes{ 0 };
    std::atomic<std::uint64_t> g_compositionSamples{ 0 };
    std::atomic<std::uint64_t> g_compositionTotalTicks{ 0 };
    std::atomic<std::uint64_t> g_compositionMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_cpuDraws{ 0 };
    std::atomic<std::uint64_t> g_gpuExDraws{ 0 };
    // Count only confirmed empty CPU frames skipped AFTER D3D9 game GPU
    // identity has already been published through the ordinary renderer path.
    std::atomic<std::uint64_t> g_emptyFramePresentSkips{ 0 };

    void RecordPipelineInterval(const LARGE_INTEGER start,
        const LARGE_INTEGER end, std::atomic<std::uint64_t> &samples,
        std::atomic<std::uint64_t> &total, std::atomic<std::uint64_t> &maximum) noexcept
    {
        const auto ticks = end.QuadPart > start.QuadPart
            ? static_cast<std::uint64_t>(end.QuadPart - start.QuadPart) : 0ull;
        samples.fetch_add(1, std::memory_order_relaxed);
        total.fetch_add(ticks, std::memory_order_relaxed);
        auto prior = maximum.load(std::memory_order_relaxed);
        while (ticks > prior && !maximum.compare_exchange_weak(
            prior, ticks, std::memory_order_relaxed)) {}
    }

    class PipelineTimer final
    {
    public:
        PipelineTimer(std::atomic<std::uint64_t> &samples,
            std::atomic<std::uint64_t> &total,
            std::atomic<std::uint64_t> &maximum) noexcept
            : samples_(samples), total_(total), maximum_(maximum)
        {
            QueryPerformanceCounter(&start_);
        }
        ~PipelineTimer() noexcept
        {
            LARGE_INTEGER end{};
            QueryPerformanceCounter(&end);
            RecordPipelineInterval(start_, end, samples_, total_, maximum_);
        }
        PipelineTimer(const PipelineTimer &) = delete;
        PipelineTimer &operator=(const PipelineTimer &) = delete;
    private:
        LARGE_INTEGER start_{};
        std::atomic<std::uint64_t> &samples_;
        std::atomic<std::uint64_t> &total_;
        std::atomic<std::uint64_t> &maximum_;
    };
    // Only read/write while holding g_renderMutex.
    IDirect3DDevice9 *g_identityDevice = nullptr;
    HANDLE g_frameMapping = nullptr;
    HANDLE g_framePublisherProcess = nullptr;
    LONG g_framePublisherPid = 0;
    const osc_frame_bridge::D3D9Frame *g_frame = nullptr;
    HANDLE g_consumerMapping = nullptr;
    osc_frame_bridge::ConsumerAdapterInfo *g_consumerInfo = nullptr;
    IDirect3DTexture9 *g_overlayTexture = nullptr;
    IDirect3DTexture9 *g_exGpuTexture = nullptr;
    IDirect3DDevice9 *g_exGpuDevice = nullptr;
    std::uint64_t g_exGpuFrameNumber = 0;
    IDirect3DTexture9 *g_exPendingTexture = nullptr;
    IDirect3DDevice9 *g_exPendingDevice = nullptr;
    std::uint64_t g_exPendingFrameNumber = 0;
    IDirect3DStateBlock9 *g_stateBlock = nullptr;
    IDirect3DDevice9 *g_resourceDevice = nullptr;
    std::vector<std::uint8_t> g_pixels;
    std::uint64_t g_cachedFrameNumber = 0;
    std::uint32_t g_cachedWidth = 0;
    std::uint32_t g_cachedHeight = 0;
    std::uint32_t g_textureWidth = 0;
    std::uint32_t g_textureHeight = 0;
    std::uint64_t g_uploadedFrameNumber = 0;
    ULONGLONG g_lastConfirmedFrameTick = 0;
    constexpr ULONGLONG MaxTransientFrameHoldMs = 250;
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
        const bool cached = g_identityDevice == device &&
            g_adapterLuid.load(std::memory_order_acquire) != 0;
        std::uint64_t packed = g_adapterLuid.load(std::memory_order_acquire);
        bool isEx = g_isD3D9Ex.load(std::memory_order_acquire);
        if (!cached)
        {
            D3DDEVICE_CREATION_PARAMETERS creation{};
            if (FAILED(device->GetCreationParameters(&creation)))
                return;
            IDirect3D9 *direct3d = nullptr;
            if (FAILED(device->GetDirect3D(&direct3d)) || !direct3d)
                return;
            IDirect3DDevice9Ex *deviceEx = nullptr;
            isEx = SUCCEEDED(device->QueryInterface(
                __uuidof(IDirect3DDevice9Ex),
                reinterpret_cast<void **>(&deviceEx))) && deviceEx;
            if (deviceEx)
                deviceEx->Release();
            IDirect3D9Ex *direct3dEx = nullptr;
            const HRESULT result = direct3d->QueryInterface(
                __uuidof(IDirect3D9Ex),
                reinterpret_cast<void **>(&direct3dEx));
            LUID luid{};
            bool luidFound = false;
            if (SUCCEEDED(result) && direct3dEx)
            {
                luidFound = SUCCEEDED(direct3dEx->GetAdapterLUID(
                    creation.AdapterOrdinal, &luid));
                direct3dEx->Release();
            }
            if (!luidFound)
                luidFound = GetMonitorAdapterLuid(
                    direct3d->GetAdapterMonitor(creation.AdapterOrdinal), luid);
            direct3d->Release();
            g_isD3D9Ex.store(isEx, std::memory_order_release);
            if (!luidFound)
            {
                g_adapterLuid.store(0, std::memory_order_release);
                g_identityDevice = nullptr;
                Log("Could not resolve D3D9 game adapter LUID.");
                return;
            }
            packed =
                (static_cast<std::uint64_t>(
                    static_cast<std::uint32_t>(luid.HighPart)) << 32) |
                luid.LowPart;
            g_identityDevice = device;
            g_adapterLuid.store(packed, std::memory_order_release);
        }

        // v2 wire layout stays intact and keeps API=9 for compatibility.
        // Read the exact D3D9 vs D3D9Ex detail through the optional export.
        if (InterlockedCompareExchange(
                &g_consumerInfo->ownerProcessId, 0, 0) == currentPid &&
            static_cast<std::uint64_t>(InterlockedCompareExchange64(
                &g_consumerInfo->adapterLuid, 0, 0)) == packed &&
            InterlockedCompareExchange(
                &g_consumerInfo->renderApi, 0, 0) ==
                static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9))
            return;

        InterlockedExchange64(&g_consumerInfo->adapterLuid,
            static_cast<LONG64>(packed));
        InterlockedExchange(&g_consumerInfo->renderApi,
            static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9));
        InterlockedExchange(&g_consumerInfo->ownerProcessId, currentPid);
        Log(isEx ? "Published D3D9Ex game adapter LUID."
                 : "Published D3D9 game adapter LUID.");
    }
    bool SnapshotFrame(std::uint64_t &frameNumber,
        std::uint32_t &width, std::uint32_t &height,
        bool &producerWriteInProgress)
    {
        producerWriteInProgress = false;
        if (!EnsureFrameMapping())
            return false;
        const LONG ownerPid = g_frame->ownerProcessId;
        if (ownerPid <= 0)
            return false;
        // Cache the publisher process handle rather than opening it on every
        // game Present. Only a zero-time poll is allowed on this render path.
        if (g_framePublisherPid != ownerPid || !g_framePublisherProcess)
        {
            if (g_framePublisherProcess)
                CloseHandle(g_framePublisherProcess);
            g_framePublisherProcess = OpenProcess(SYNCHRONIZE, FALSE,
                static_cast<DWORD>(ownerPid));
            g_framePublisherPid = g_framePublisherProcess ? ownerPid : 0;
        }
        if (!g_framePublisherProcess ||
            WaitForSingleObject(g_framePublisherProcess, 0) != WAIT_TIMEOUT)
            return false;
        if (ownerPid != g_cachedPublisherPid)
        {
            g_cachedPublisherPid = ownerPid;
            g_cachedFrameNumber = 0;
            g_uploadedFrameNumber = 0;
            g_pixels.clear();
        }

        // At most one CPU copy per Present. If the producer changes the
        // seqlock during our copy, skip this frame and retry on NEXT Present.
        // Never recopy a large frame repeatedly while the game waits.
        for (int attempt = 0; attempt < 1; ++attempt)
        {
            const LONG before = g_frame->sequence;
            if (before & 1)
            {
                producerWriteInProgress = true;
                continue;
            }
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
            {
                producerWriteInProgress = true;
                continue;
            }

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
                // rowPitch is producer-controlled. Checking only the packed
                // bytes would permit out-of-bounds reads on the final row.
                (static_cast<std::uint64_t>(nextHeight - 1) * rowPitch +
                    static_cast<std::uint64_t>(nextWidth) * 4) >
                    osc_frame_bridge::MaxFrameBytes ||
                expectedBytes > osc_frame_bridge::MaxFrameBytes)
                return false;
            if (nextFrame == g_cachedFrameNumber &&
                nextWidth == g_cachedWidth && nextHeight == g_cachedHeight)
            {
                g_snapshotCacheHits.fetch_add(1, std::memory_order_relaxed);
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
            g_cpuFrameCopiedBytes.fetch_add(expectedBytes, std::memory_order_relaxed);
            MemoryBarrier();
            if (before != g_frame->sequence)
            {
                producerWriteInProgress = true;
                continue;
            }
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
        if (g_exPendingTexture)
        {
            g_exPendingTexture->Release();
            g_exPendingTexture = nullptr;
        }
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;
        if (g_exGpuTexture) { g_exGpuTexture->Release(); g_exGpuTexture = nullptr; }
        g_exGpuDevice = nullptr;
        g_exGpuFrameNumber = 0;
        g_gpuFrameBound.store(false, std::memory_order_release);
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
        g_lastConfirmedFrameTick = 0;
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
        // A bound DX9Ex GPU frame bypasses CPU Shared Memory. The publisher
        // must keep its legacy-shared BGRA8 resource alive and synchronize
        // its writes while the game uses the texture.
        const bool gpuFrame = g_exGpuTexture && g_exGpuDevice == device &&
            g_exGpuFrameNumber != 0;
        if (gpuFrame)
        {
            if (!g_stateBlock && FAILED(device->CreateStateBlock(
                    D3DSBT_ALL, &g_stateBlock)))
                return;
        }
        else
        {
            std::uint64_t frameNumber = 0;
            std::uint32_t width = 0;
            std::uint32_t height = 0;
            bool producerWriteInProgress = false;
            bool valid = false;
            {
                PipelineTimer snapshotTimer(g_snapshotSamples,
                    g_snapshotTotalTicks, g_snapshotMaxTicks);
                valid = SnapshotFrame(
                    frameNumber, width, height, producerWriteInProgress);
            }
            if (!valid)
            {
                // A seqlock collision means a frame is being replaced.
                // Keep the last verified GPU texture (no copy, no retry).
                const ULONGLONG now = GetTickCount64();
                if (!producerWriteInProgress || !g_overlayTexture ||
                    !g_stateBlock || !g_uploadedFrameNumber ||
                    !g_lastConfirmedFrameTick ||
                    now - g_lastConfirmedFrameTick > MaxTransientFrameHoldMs)
                    return;
            }
            else
            {
                if (!frameNumber)
                {
                    g_cachedFrameNumber = 0;
                    g_uploadedFrameNumber = 0;
                    g_lastConfirmedFrameTick = 0;
                    g_pixels.clear();
                    return;
                }
                if (g_pixels.empty() ||
                    !EnsureDeviceResources(device, width, height))
                    return;
                if (g_overlayTexture && frameNumber != g_uploadedFrameNumber)
                {
                    PipelineTimer uploadTimer(g_uploadAttempts,
                        g_uploadTotalTicks, g_uploadMaxTicks);
                    D3DLOCKED_RECT locked{};
                    HRESULT result = g_overlayTexture->LockRect(
                        0, &locked, nullptr, D3DLOCK_DISCARD);
                    if (FAILED(result))
                        return;
                    const std::size_t rowBytes =
                        static_cast<std::size_t>(width) * 4;
                    for (std::uint32_t row = 0; row < height; ++row)
                        std::memcpy(
                            static_cast<std::uint8_t *>(locked.pBits) +
                                static_cast<std::size_t>(row) * locked.Pitch,
                            g_pixels.data() + static_cast<std::size_t>(row) *
                                rowBytes, rowBytes);
                    result = g_overlayTexture->UnlockRect(0);
                    if (FAILED(result))
                        return;
                    g_uploadedFrameNumber = frameNumber;
                    g_uploadedBytes.fetch_add(
                        static_cast<std::uint64_t>(width) * height * 4,
                        std::memory_order_relaxed);
                }
                if (g_uploadedFrameNumber)
                    g_lastConfirmedFrameTick = GetTickCount64();
            }
        }

        D3DVIEWPORT9 viewport{};
        if (FAILED(device->GetViewport(&viewport)) ||
            !viewport.Width || !viewport.Height)
            return;
        // CPU-side submission timer, not a GPU execution timer.
        PipelineTimer compositionTimer(g_compositionSamples,
            g_compositionTotalTicks, g_compositionMaxTicks);
        if (FAILED(g_stateBlock->Capture()))
            return;

        const float left = static_cast<float>(viewport.X);
        const float top = static_cast<float>(viewport.Y);
        const float right = left + static_cast<float>(viewport.Width);
        const float bottom = top + static_cast<float>(viewport.Height);
        // OSC's raw CEF GPU image is bottom-up. The D3D11 compositor
        // flips Y with kSharedQuadVS. Match that orientation in the D3D9
        // CPU bridge without altering the distinct D3D9Ex GPU alias path.
        const float topV = gpuFrame ? 0.0f : 1.0f;
        const float bottomV = gpuFrame ? 1.0f : 0.0f;
        const Vertex vertices[] = {
            { left - 0.5f, top - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 0.0f, topV },
            { right - 0.5f, top - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 1.0f, topV },
            { left - 0.5f, bottom - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 0.0f, bottomV },
            { right - 0.5f, bottom - 0.5f, 0.0f, 1.0f, 0xFFFFFFFF, 1.0f, bottomV }
        };

        device->SetVertexShader(nullptr);
        device->SetPixelShader(nullptr);
        device->SetFVF(VertexFormat);
        device->SetTexture(0, gpuFrame ? g_exGpuTexture : g_overlayTexture);
        // Normalize game-owned fixed-function state every frame, then
        // restore through the preexisting state block.
        device->SetRenderState(D3DRS_ZENABLE, FALSE);
        device->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
        device->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);
        device->SetRenderState(D3DRS_STENCILENABLE, FALSE);
        device->SetRenderState(D3DRS_FOGENABLE, FALSE);
        device->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE, FALSE);
        device->SetRenderState(D3DRS_COLORWRITEENABLE,
            D3DCOLORWRITEENABLE_RED | D3DCOLORWRITEENABLE_GREEN |
            D3DCOLORWRITEENABLE_BLUE | D3DCOLORWRITEENABLE_ALPHA);
        device->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        device->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
        device->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        device->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD);
        device->SetRenderState(D3DRS_LIGHTING, FALSE);
        device->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        device->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);
        device->SetTextureStageState(0, D3DTSS_TEXCOORDINDEX, 0);
        device->SetTextureStageState(0, D3DTSS_TEXTURETRANSFORMFLAGS,
            D3DTTFF_DISABLE);
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
        {
            g_drawCount.fetch_add(1, std::memory_order_relaxed);
            (gpuFrame ? g_gpuExDraws : g_cpuDraws).fetch_add(
                1, std::memory_order_relaxed);
        }
    }
}
namespace duluka::d3d9
{
    // No new identity probing or D3D9 device state changes are allowed here.
    // The FIRST Present must go through DrawOverlay to publish the real game
    // adapter. Only skip subsequent COM backbuffer requests when the CPU
    // mapping is stable and EXPLICITLY advertises frameNumber=0 (hidden).
    // If a producer begins publishing, the next Present takes the old path.
    static bool HasConfirmedEmptyCpuFrameLocked(IDirect3DDevice9 *device) noexcept
    {
        // Another renderer can replace the shared identity while this device
        // stays cached. Let the ordinary path repair it before skipping work.
        const auto adapterLuid = g_adapterLuid.load(std::memory_order_acquire);
        if (!device || g_identityDevice != device ||
            !adapterLuid || !g_consumerInfo ||
            g_consumerInfo->magic != osc_frame_bridge::ConsumerAdapterMagic ||
            g_consumerInfo->version != osc_frame_bridge::ConsumerAdapterVersion ||
            InterlockedCompareExchange(&g_consumerInfo->ownerProcessId, 0, 0) !=
                static_cast<LONG>(GetCurrentProcessId()) ||
            static_cast<std::uint64_t>(InterlockedCompareExchange64(
                &g_consumerInfo->adapterLuid, 0, 0)) != adapterLuid ||
            InterlockedCompareExchange(&g_consumerInfo->renderApi, 0, 0) !=
                static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9) ||
            !g_frame ||
            g_frame->magic != osc_frame_bridge::D3D9FrameMagic ||
            g_frame->version != osc_frame_bridge::D3D9FrameVersion ||
            g_exGpuTexture || g_exPendingTexture ||
            g_cachedFrameNumber || g_uploadedFrameNumber)
            return false;
        const LONG before = g_frame->sequence;
        if (before & 1)
            return false; // producer currently mutating frame metadata
        MemoryBarrier();
        const LONG producerPid = g_frame->ownerProcessId;
        const auto frameNumber = g_frame->frameNumber;
        MemoryBarrier();
        return before == g_frame->sequence && producerPid > 0 &&
            frameNumber == 0;
    }

    void OnPresent(IDirect3DDevice9 *device)
    {
        if (!device) return;
        // Host GPU rebind must never block the game's Present callback.
        std::unique_lock<std::mutex> lock(g_renderMutex, std::try_to_lock);
        if (!lock.owns_lock())
        {
            g_busyFrameSkips.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        // GPU-only Windowed DComp has retired the CPU frame. In that precise
        // state, querying even the game's backbuffer is unnecessary and can
        // perturb D3D9 frame pacing. Do not repeat the earlier unsafe fastpath
        // that called PublishConsumerAdapter while bypassing normal rendering.
        if (HasConfirmedEmptyCpuFrameLocked(device))
        {
            g_emptyFramePresentSkips.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        // Present may leave an offscreen render target bound. Compose to the
        // swapchain backbuffer explicitly, and restore the game's target.
        IDirect3DSurface9 *originalTarget = nullptr;
        IDirect3DSurface9 *backBuffer = nullptr;
        if (FAILED(device->GetRenderTarget(0, &originalTarget)) ||
            !originalTarget)
            return;
        if (SUCCEEDED(device->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO,
                &backBuffer)) && backBuffer)
        {
            const bool sameTarget = originalTarget == backBuffer;
            const bool ready = sameTarget ||
                SUCCEEDED(device->SetRenderTarget(0, backBuffer));
            if (ready)
                DrawOverlay(device, false);
            if (ready && !sameTarget)
                device->SetRenderTarget(0, originalTarget);
            backBuffer->Release();
        }
        originalTarget->Release();
    }
    void OnReShadePresent(IDirect3DDevice9 *device,
        IDirect3DSurface9 *backBuffer, bool gameForeground)
    {
        if (!device || !backBuffer || !gameForeground) return;
        std::unique_lock<std::mutex> lock(g_renderMutex, std::try_to_lock);
        if (!lock.owns_lock())
        {
            g_busyFrameSkips.fetch_add(1, std::memory_order_relaxed);
            return;
        }
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
        g_identityDevice = nullptr;
        g_adapterLuid.store(0, std::memory_order_release);
        g_isD3D9Ex.store(false, std::memory_order_release);
        if (g_consumerInfo &&
            InterlockedCompareExchange(&g_consumerInfo->ownerProcessId, 0, 0) ==
                static_cast<LONG>(GetCurrentProcessId()))
        {
            InterlockedExchange(&g_consumerInfo->ownerProcessId, 0);
            InterlockedExchange64(&g_consumerInfo->adapterLuid, 0);
            InterlockedExchange(&g_consumerInfo->renderApi, 0);
        }
    }
    void RenderForTest(IDirect3DDevice9 *device)
    {
        if (!device) return;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        DrawOverlay(device, false);
    }
    bool BindExSharedGpuTexture(IDirect3DDevice9Ex *device, HANDLE sharedHandle,
        std::uint32_t width, std::uint32_t height, std::uint64_t frameNumber)
    {
        // The producer supplies a *legacy* DXGI shared handle, never an NT
        // handle. D3D9Ex only accepts this path with same-adapter BGRA8.
        if (!device || !sharedHandle || !frameNumber || !width || !height ||
            width > 8192 || height > 8192)
            return false;
        std::lock_guard<std::mutex> lock(g_renderMutex);
        // Direct bind supersedes any staged (uncommitted) generation.
        if (g_exPendingTexture)
        {
            g_exPendingTexture->Release();
            g_exPendingTexture = nullptr;
        }
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;
        if (g_resourceDevice != device)
        {
            ReleaseDeviceResources();
            g_resourceDevice = device;
        }
        IDirect3DTexture9 *texture = nullptr;
        HANDLE openingHandle = sharedHandle;
        const HRESULT result = device->CreateTexture(width, height, 1,
            D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
            &texture, &openingHandle);
        if (FAILED(result) || !texture)
        {
            // Never keep showing the previous GPU frame after a failed
            // replacement. Drop GPU input and use the CPU bridge instead.
            if (g_exGpuTexture)
            {
                g_exGpuTexture->Release();
                g_exGpuTexture = nullptr;
            }
            g_exGpuDevice = nullptr;
            g_exGpuFrameNumber = 0;
            g_gpuFrameBound.store(false, std::memory_order_release);
            Log("D3D9Ex legacy GPU shared texture failed; CPU fallback enabled.");
            return false;
        }
        if (g_exGpuTexture)
            g_exGpuTexture->Release();
        g_exGpuTexture = texture;
        g_exGpuDevice = device;
        g_exGpuFrameNumber = frameNumber;
        g_gpuFrameBound.store(true, std::memory_order_release);
        return true;
    }

    bool UnbindExSharedGpuTexture(IDirect3DDevice9Ex *device)
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        if (!device || g_resourceDevice != device)
            return false;
        const bool hadFrame = (g_exGpuTexture && g_exGpuDevice == device) ||
            (g_exPendingTexture && g_exPendingDevice == device);
        if (!hadFrame)
            return false;
        if (g_exPendingTexture)
        {
            g_exPendingTexture->Release();
            g_exPendingTexture = nullptr;
        }
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;
        if (g_exGpuTexture)
            g_exGpuTexture->Release();
        g_exGpuTexture = nullptr;
        g_exGpuDevice = nullptr;
        g_exGpuFrameNumber = 0;
        g_gpuFrameBound.store(false, std::memory_order_release);
        return true;
    }

    bool StageExSharedGpuTexture(IDirect3DDevice9Ex *device,
        HANDLE sharedHandle, std::uint32_t width, std::uint32_t height,
        std::uint64_t frameNumber)
    {
        if (!device || !sharedHandle || !frameNumber || !width || !height ||
            width > 8192 || height > 8192)
            return false;

        // The producer may be busy opening a cross-GPU transferred frame.
        // Only that producer thread waits for this mutex; OnPresent uses
        // try_lock and skips OSC instead of stalling the game.
        std::lock_guard<std::mutex> lock(g_renderMutex);
        if (g_resourceDevice != device)
        {
            ReleaseDeviceResources();
            g_resourceDevice = device;
        }
        if (g_exPendingTexture)
        {
            g_exPendingTexture->Release();
            g_exPendingTexture = nullptr;
        }
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;

        HANDLE openingHandle = sharedHandle;
        IDirect3DTexture9 *pending = nullptr;
        const HRESULT result = device->CreateTexture(width, height, 1,
            D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
            &pending, &openingHandle);
        if (FAILED(result) || !pending)
        {
            // A failed replacement must never destroy the last committed
            // resource: leave it active until Host explicitly retires it.
            Log("D3D9Ex frame stage failed; committed frame is unchanged.");
            return false;
        }
        g_exPendingTexture = pending;
        g_exPendingDevice = device;
        g_exPendingFrameNumber = frameNumber;
        return true;
    }

    bool CommitExSharedGpuTexture(IDirect3DDevice9Ex *device,
        std::uint64_t frameNumber)
    {
        // Caller is responsible for GPU-write completion. This API does
        // not wait on GPU fences, devices or the Host rendering GPU.
        std::lock_guard<std::mutex> lock(g_renderMutex);
        if (!device || !g_exPendingTexture ||
            g_exPendingDevice != device || !frameNumber ||
            g_exPendingFrameNumber != frameNumber)
            return false;
        if (g_exGpuTexture)
            g_exGpuTexture->Release();
        g_exGpuTexture = g_exPendingTexture;
        g_exGpuDevice = device;
        g_exGpuFrameNumber = frameNumber;
        g_exPendingTexture = nullptr;
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;
        g_gpuFrameBound.store(true, std::memory_order_release);
        return true;
    }

    bool DiscardExPendingGpuTexture(IDirect3DDevice9Ex *device)
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        if (!device || !g_exPendingTexture || g_exPendingDevice != device)
            return false;
        g_exPendingTexture->Release();
        g_exPendingTexture = nullptr;
        g_exPendingDevice = nullptr;
        g_exPendingFrameNumber = 0;
        return true;
    }

    void GetAdapterStatus(std::uint64_t &adapterLuid, bool &isD3D9Ex,
        bool &gpuFrameBound, std::uint64_t &busyFrameSkips)
    {
        adapterLuid = g_adapterLuid.load(std::memory_order_acquire);
        isD3D9Ex = g_isD3D9Ex.load(std::memory_order_acquire);
        gpuFrameBound = g_gpuFrameBound.load(std::memory_order_acquire);
        busyFrameSkips = g_busyFrameSkips.load(std::memory_order_relaxed);
    }
    std::uint64_t GetDrawCount()
    {
        return g_drawCount.load(std::memory_order_relaxed);
    }
    void Shutdown()
    {
        std::lock_guard<std::mutex> lock(g_renderMutex);
        ReleaseDeviceResources();
        if(g_framePublisherProcess)
        {
            CloseHandle(g_framePublisherProcess);
            g_framePublisherProcess = nullptr;
            g_framePublisherPid = 0;
        }
        if(g_frame){UnmapViewOfFile(g_frame);g_frame=nullptr;}
        if(g_frameMapping){CloseHandle(g_frameMapping);g_frameMapping=nullptr;}
        if(g_consumerInfo){UnmapViewOfFile(g_consumerInfo);g_consumerInfo=nullptr;}
        if(g_consumerMapping){CloseHandle(g_consumerMapping);g_consumerMapping=nullptr;}
    }
}

#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9EmptyFrameSkips=_NvOscAdapterGetD3D9EmptyFrameSkips@4")
#endif
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9EmptyFrameSkips(
    std::uint64_t *skips) noexcept
{
    if (!skips) return FALSE;
    *skips = g_emptyFramePresentSkips.load(std::memory_order_relaxed);
    return TRUE;
}

#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9PipelineSnapshot=_NvOscAdapterGetD3D9PipelineSnapshot@8")
#endif
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9PipelineSnapshot(
    duluka::diagnostics::D3D9PipelineTimingSnapshotV1 *out, DWORD outBytes) noexcept
{
    using namespace duluka::diagnostics;
    if (!out || outBytes < sizeof(D3D9PipelineTimingSnapshotV1))
        return FALSE;
    LARGE_INTEGER frequency{};
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0)
        return FALSE;
    D3D9PipelineTimingSnapshotV1 value{};
    value.size = sizeof(value);
    value.version = D3D9PipelineTimingVersion;
    value.performanceFrequency = static_cast<std::uint64_t>(frequency.QuadPart);
    value.snapshotSamples = g_snapshotSamples.load(std::memory_order_relaxed);
    value.snapshotTotalTicks = g_snapshotTotalTicks.load(std::memory_order_relaxed);
    value.snapshotMaxTicks = g_snapshotMaxTicks.load(std::memory_order_relaxed);
    value.snapshotCacheHits = g_snapshotCacheHits.load(std::memory_order_relaxed);
    value.cpuFrameCopiedBytes = g_cpuFrameCopiedBytes.load(std::memory_order_relaxed);
    value.uploadAttempts = g_uploadAttempts.load(std::memory_order_relaxed);
    value.uploadTotalTicks = g_uploadTotalTicks.load(std::memory_order_relaxed);
    value.uploadMaxTicks = g_uploadMaxTicks.load(std::memory_order_relaxed);
    value.uploadedBytes = g_uploadedBytes.load(std::memory_order_relaxed);
    value.compositionSamples = g_compositionSamples.load(std::memory_order_relaxed);
    value.compositionTotalTicks = g_compositionTotalTicks.load(std::memory_order_relaxed);
    value.compositionMaxTicks = g_compositionMaxTicks.load(std::memory_order_relaxed);
    value.cpuDraws = g_cpuDraws.load(std::memory_order_relaxed);
    value.gpuExDraws = g_gpuExDraws.load(std::memory_order_relaxed);
    *out = value;
    return TRUE;
}
