#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include "OscD3D11FramePublisher.h"
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <iterator>
#include <string>
#include <vector>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include "include/cef_base.h"
#include "include/internal/cef_types_wrappers.h"
#include "../../../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"
#include "../OscD3D9FramePublisher/OscD3D9FramePublisher.h"
#include "../OscD3D9ExFramePublisher/OscD3D9ExFramePublisher.h"
#include "../Common/OscFrameUtilities.h"
#include "../Common/OscPublishCadence.h"
namespace osc::d3d11_frame_publisher {
static HostBindings g_host{};
static ID3D11Device* HostGpuDevice() { return g_host.gpuDevice ? g_host.gpuDevice() : nullptr; }
static ID3D11DeviceContext* HostGpuContext() { return g_host.gpuContext ? g_host.gpuContext() : nullptr; }
static ID3D11Texture2D* HostSharedTexture() { return g_host.sharedTexture ? g_host.sharedTexture() : nullptr; }
static HWND HostWindow() { return g_host.window ? g_host.window() : nullptr; }
static UINT HostViewWidth() { return g_host.viewWidth ? g_host.viewWidth() : 0; }
static UINT HostViewHeight() { return g_host.viewHeight ? g_host.viewHeight() : 0; }
static bool HostGameFocused() { return g_host.gameFocused && g_host.gameFocused(); }
static bool HostOverlayVisible() { return g_host.overlayVisible && g_host.overlayVisible(); }
static void PublisherLog(const std::string& m) { if (g_host.log) g_host.log(m); }
static void PublisherLog(const char* m) { if (g_host.log) g_host.log(std::string(m ? m : "")); }
static HANDLE g_frame_bridge_mapping = nullptr;
static osc_frame_bridge::Frame* g_frame_bridge = nullptr;
static HANDLE g_consumer_adapter_mapping = nullptr;
static osc_frame_bridge::ConsumerAdapterInfo* g_consumer_adapter_info = nullptr;
static std::mutex g_consumer_adapter_info_mutex;
static ID3D11Texture2D* g_adapter_frame_texture = nullptr;
static IDXGIKeyedMutex* g_adapter_frame_mutex = nullptr;
static HANDLE g_adapter_frame_handle = nullptr;
static std::uint64_t g_adapter_frame_resource_id = 0;
static std::uint64_t g_adapter_frame_resource_generation = 0;
static ID3D11Device* g_adapter_frame_device = nullptr;
static ID3D11DeviceContext* g_adapter_frame_context = nullptr;
static std::mutex g_adapter_publish_mutex;
// The publish mutex serializes the source serial with queue submissions.
// A timer-driven recovery copy carries the last serial, not a NEW frame.
static std::uint64_t g_cef_content_serial = 0;
static osc::frame_cadence::FrameCadence g_frame_cadence{};
static std::atomic<bool> g_publish_worker_stopping{ false };
static std::atomic<bool> g_d3d9ex_cpu_only{ false };
static bool GetDeviceAdapterLuid(ID3D11Device*, LUID&);
static constexpr size_t AdapterReadbackSlotCount = 3;
struct AdapterReadbackSlot {
 ID3D11Texture2D* texture = nullptr;
 ID3D11Query* completion = nullptr;
 bool pending = false;
 bool fullFrame = false;
 std::uint64_t cefContentSerial = 0;
 std::vector<D3D11_BOX> dirtyBoxes;
};
static AdapterReadbackSlot g_adapter_readback_slots[AdapterReadbackSlotCount]{};
static size_t g_adapter_readback_write = 0;
static size_t g_adapter_readback_read = 0;
static LUID g_adapter_frame_target_luid{};
static bool g_adapter_frame_target_selected = false;
// [DX11-LATENCY] event-driven publish wake: a queued readback must not wait
// up to one 4ms worker tick before the first completion poll.
static HANDLE g_publish_wake_event = nullptr;
static std::atomic<long> g_readback_pending{ 0 };
static std::uint32_t g_adapter_frame_consumer_api = 0;
static UINT g_adapter_frame_width = 0;
static UINT g_adapter_frame_height = 0;
static DXGI_FORMAT g_adapter_frame_format = DXGI_FORMAT_UNKNOWN;
static bool g_adapter_frame_initialized = false;
static bool g_adapter_force_full_readback = false;
static std::vector<CefRect> g_adapter_deferred_dirty_rects;

// Phase 0.2: a bounded, opt-in GPU-only input queue. CEF's shared handle is
// never retained: CopyResource is submitted while OnAcceleratedPaint still owns
// the handle. The worker owns two independent GPU textures and queries.
static constexpr size_t GpuPreviewStageCount = 2;
struct GpuPreviewStage {
    ID3D11Texture2D* texture = nullptr;
    ID3D11Query* completion = nullptr;
    std::uint64_t serial = 0;
    ULONGLONG queuedAtMs = 0;
    bool queued = false;
    bool processing = false;
};
static GpuPreviewStage g_gpu_preview_stages[GpuPreviewStageCount]{};
static UINT g_gpu_preview_width = 0, g_gpu_preview_height = 0;
static ID3D11Device* g_gpu_preview_device = nullptr;
static UINT g_gpu_preview_requested_width = 0, g_gpu_preview_requested_height = 0;
static std::uint64_t g_gpu_preview_serial = 0;
// Publish worker exclusively owns this counter and GPU submission flush.
static std::uint64_t g_gpu_preview_last_flushed_serial = 0;
// Prevent an older GPU query from publishing after a newer frame was consumed.
static std::uint64_t g_gpu_preview_last_consumed_serial = 0;
static ULONGLONG g_gpu_preview_next_prepare_ms = 0;
static std::atomic<std::uint64_t> g_gpu_preview_queued{0};
static std::atomic<std::uint64_t> g_gpu_preview_dropped{0};
static std::atomic<std::uint64_t> g_gpu_preview_sent{0};
static std::atomic<std::uint64_t> g_gpu_preview_not_ready{0};
static std::atomic<ULONGLONG> g_gpu_preview_last_sent_ms{0};
static std::atomic<ULONGLONG> g_gpu_preview_last_queued_ms{0};
// Report queue age and callback submission cost, not game-frame time.
static std::atomic<std::uint64_t> g_gpu_preview_enqueue_cpu_us{0};
static std::atomic<std::uint64_t> g_gpu_preview_enqueue_count{0};
static std::atomic<std::uint64_t> g_gpu_preview_ready_latency_ms{0};
static std::atomic<std::uint64_t> g_gpu_preview_ready_count{0};

static bool GpuPreviewEnabled() {
    if (g_d3d9ex_cpu_only.load(std::memory_order_acquire)) return false;
    static const bool enabled = [] {
        char flag[8]{};
        return GetEnvironmentVariableA("OSC_DX9EX_CEF_GPU_PREVIEW",
            flag, sizeof(flag)) == 1 && flag[0] == '1';
    }();
    return enabled;
}
static void ReleaseGpuPreviewStagesLocked() {
    for (auto& slot : g_gpu_preview_stages) {
        if (slot.completion) { slot.completion->Release(); slot.completion = nullptr; }
        if (slot.texture) { slot.texture->Release(); slot.texture = nullptr; }
        slot.queued = false;
        slot.processing = false;
        slot.serial = 0;
        slot.queuedAtMs = 0;
    }
    g_gpu_preview_width = g_gpu_preview_height = 0;
    if (g_gpu_preview_device) {
        g_gpu_preview_device->Release();
        g_gpu_preview_device=nullptr;
    }
}
bool QueueD3D9ExGpuFrameLocked(ID3D11Texture2D* cefTexture) {
    if (!GpuPreviewEnabled() || !cefTexture ||
        g_publish_worker_stopping.load(std::memory_order_acquire) ||
        !HostGameFocused() || !HostOverlayVisible())
        return false;
    // Only BGRA8 single-sample, whole-frame copies. Dirty-rect tracking is
    // intentionally not used: every accepted stage is an independent frame.
    D3D11_TEXTURE2D_DESC desc{};
    cefTexture->GetDesc(&desc);
    if (!desc.Width || !desc.Height || desc.MipLevels != 1 ||
        desc.ArraySize != 1 || desc.SampleDesc.Count != 1 ||
        (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
         desc.Format != DXGI_FORMAT_B8G8R8A8_TYPELESS) ||
        static_cast<std::uint64_t>(desc.Width)*desc.Height*4 >
            osc_frame_bridge::MaxFrameBytes)
        return false;
    ID3D11Device* owner = nullptr;
    cefTexture->GetDevice(&owner);
    const bool sameDevice = owner && owner == HostGpuDevice();
    if (owner) owner->Release();
    if (!sameDevice) return false;

    g_gpu_preview_requested_width = desc.Width;
    g_gpu_preview_requested_height = desc.Height;
    if (g_gpu_preview_width != desc.Width ||
        g_gpu_preview_height != desc.Height ||
        g_gpu_preview_device != HostGpuDevice()) {
        ++g_gpu_preview_dropped;
        return false; // worker prepares resources without blocking CEF
    }
    for (auto& slot : g_gpu_preview_stages) {
        if (!slot.texture || !slot.completion ||
            slot.queued || slot.processing) continue;
        // D3D11 context is multithread-protected; NO CPU Map/Wait/GetData.
        LARGE_INTEGER begin{},end{},freq{};
        QueryPerformanceCounter(&begin);
        HostGpuContext()->CopyResource(slot.texture, cefTexture);
        HostGpuContext()->End(slot.completion);
        QueryPerformanceCounter(&end);
        QueryPerformanceFrequency(&freq);
        if (freq.QuadPart > 0) {
            g_gpu_preview_enqueue_cpu_us.fetch_add(
                static_cast<std::uint64_t>(
                    (end.QuadPart-begin.QuadPart)*1000000 / freq.QuadPart),
                std::memory_order_relaxed);
            ++g_gpu_preview_enqueue_count;
        }
        slot.serial = ++g_gpu_preview_serial;
        slot.queuedAtMs = GetTickCount64();
        g_gpu_preview_last_queued_ms.store(slot.queuedAtMs,
            std::memory_order_relaxed);
        slot.queued = true;
        ++g_gpu_preview_queued;
        return true;
    }
    ++g_gpu_preview_dropped; // bounded capacity: drop, never build backlog
    return false;
}
static void PrepareGpuPreviewOnWorker() {
    if (!GpuPreviewEnabled()) return;
    UINT width=0,height=0;
    ID3D11Device* device=nullptr;
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        width=g_gpu_preview_requested_width;
        height=g_gpu_preview_requested_height;
        if (!width || !height ||
            (g_gpu_preview_width==width &&
             g_gpu_preview_height==height &&
             g_gpu_preview_device==HostGpuDevice()) ||
            GetTickCount64()<g_gpu_preview_next_prepare_ms) return;
        for (const auto& slot : g_gpu_preview_stages)
            if (slot.queued || slot.processing) return;
        device=HostGpuDevice();
        if (!device) return;
        device->AddRef();
    }
    GpuPreviewStage prepared[GpuPreviewStageCount]{};
    bool success=true;
    D3D11_TEXTURE2D_DESC td{};
    td.Width=width;td.Height=height;
    td.MipLevels=1;td.ArraySize=1;td.SampleDesc.Count=1;
    td.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
    td.Usage=D3D11_USAGE_DEFAULT;
    td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    D3D11_QUERY_DESC q{};q.Query=D3D11_QUERY_EVENT;
    for (auto& s : prepared) {
        if (FAILED(device->CreateTexture2D(&td,nullptr,&s.texture)) ||
            FAILED(device->CreateQuery(&q,&s.completion))) {
            success=false;
            break;
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        bool empty=true;
        for (const auto& slot : g_gpu_preview_stages)
            if (slot.queued || slot.processing) empty=false;
        if (success && empty && HostGpuDevice()==device &&
            g_gpu_preview_requested_width==width &&
            g_gpu_preview_requested_height==height &&
            !g_publish_worker_stopping.load(std::memory_order_acquire)) {
            ReleaseGpuPreviewStagesLocked();
            for (size_t i=0;i<GpuPreviewStageCount;++i) {
                g_gpu_preview_stages[i]=prepared[i];
                prepared[i]={};
            }
            g_gpu_preview_width=width;
            g_gpu_preview_height=height;
            device->AddRef();
            g_gpu_preview_device=device;
        } else {
            g_gpu_preview_next_prepare_ms=GetTickCount64()+250;
        }
    }
    for (auto& s : prepared) {
        if (s.texture) s.texture->Release();
        if (s.completion) s.completion->Release();
    }
    device->Release();
}
// CPU memory publication remains enabled even in GPU preview. Only the
// redundant CPU -> legacy-GPU path yields briefly to an actively receiving
// CEF GPU queue; it resumes automatically when that queue becomes idle.
static bool PreferGpuPreviewToCpuGpuUpload() {
    if (!GpuPreviewEnabled()) return false;
    const ULONGLONG last=g_gpu_preview_last_queued_ms.load(
        std::memory_order_relaxed);
    const ULONGLONG now=GetTickCount64();
    return last && now>=last && now-last<750;
}
static void ProcessGpuPreviewOnWorker() {
    if (!GpuPreviewEnabled()) return;
    // Do not access the Host context without an independent strong reference:
    // CEF could resize while the worker handles this slot.
    ID3D11Texture2D* texture=nullptr;
    ID3D11Query* query=nullptr;
    GpuPreviewStage* chosen=nullptr;
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        for (auto& slot : g_gpu_preview_stages) {
            if (slot.queued && !slot.processing &&
                (!chosen || slot.serial > chosen->serial))
                chosen=&slot; // latest-wins
        }
        if (!chosen || !chosen->texture || !chosen->completion) return;
        chosen->processing=true;
        texture=chosen->texture;texture->AddRef();
        query=chosen->completion;query->AddRef();
    }

    // The CEF callback submits CopyResource + End only. Flush on the
    // worker once per generation so an idle GPU-only path makes progress.
    if (HostGpuContext() && chosen->serial > g_gpu_preview_last_flushed_serial) {
        HostGpuContext()->Flush(); // submits commands; NOT a GPU completion wait
        g_gpu_preview_last_flushed_serial=chosen->serial;
    }
    const HRESULT completion=HostGpuContext()
        ? HostGpuContext()->GetData(query,nullptr,0,
            D3D11_ASYNC_GETDATA_DONOTFLUSH) : E_FAIL;
    bool published=false;
    const bool stale = chosen->serial <= g_gpu_preview_last_consumed_serial;
    if (completion==S_OK) {
        ++g_gpu_preview_ready_count;
        const ULONGLONG readyAt=GetTickCount64();
        g_gpu_preview_ready_latency_ms.fetch_add(
            readyAt>=chosen->queuedAtMs
                ? readyAt-chosen->queuedAtMs : 0,
            std::memory_order_relaxed);
    }
    if (completion==S_OK && !stale &&
        HostGameFocused() && HostOverlayVisible()) {
        const auto* consumer=GetConsumerAdapterInfo();
        const LONG pid=consumer ? consumer->ownerProcessId : 0;
        const LONG api=consumer ?
            osc_frame_bridge::LoadConsumerRenderApi(*consumer) : 0;
        const LONG64 packedLuid=consumer ? consumer->adapterLuid : 0;
        LUID hostLuid{};
        if (consumer &&
            consumer->magic==osc_frame_bridge::ConsumerAdapterMagic &&
            consumer->version==osc_frame_bridge::ConsumerAdapterVersion &&
            pid>0 && packedLuid!=0 &&
            api==osc_frame_bridge::ConsumerRenderApiD3D9 &&
            ([&]() {
                std::lock_guard<std::mutex> guard(g_adapter_publish_mutex);
                return GetDeviceAdapterLuid(HostGpuDevice(),hostLuid);
            })()) {
            const std::uint64_t h=
                (static_cast<std::uint64_t>(
                    static_cast<std::uint32_t>(hostLuid.HighPart))<<32) |
                hostLuid.LowPart;
            if (h==static_cast<std::uint64_t>(packedLuid) &&
                osc::d3d9ex_frame_publisher::CanPublish(
                    static_cast<DWORD>(pid),hostLuid))
                published=osc::d3d9ex_frame_publisher::TryPublishGpuTexture(
                    HostGpuDevice(),HostGpuContext(),texture,hostLuid,
                    static_cast<DWORD>(pid));
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        if (completion==S_FALSE) {
            ++g_gpu_preview_not_ready;
        } else {
            chosen->queued=false; // drop unavailable/outdated frames
            if (completion==S_OK) {
                g_gpu_preview_last_consumed_serial =
                    (std::max)(g_gpu_preview_last_consumed_serial,
                        chosen->serial);
                if (stale) ++g_gpu_preview_dropped;
            }
            if (published) {
                ++g_gpu_preview_sent;
                g_gpu_preview_last_sent_ms.store(GetTickCount64(),
                    std::memory_order_relaxed);
            }
        }
        chosen->processing=false;
    }
    query->Release();
    texture->Release();
}

static void ReleaseAdapterFrameTexture() {
    for (auto& slot : g_adapter_readback_slots) {
        if (slot.texture) {
            slot.texture->Release();
            slot.texture = nullptr;
        }
        if (slot.completion) {
            slot.completion->Release();
            slot.completion = nullptr;
        }
        slot.pending = false;
        slot.fullFrame = false;
        slot.cefContentSerial = 0;
        slot.dirtyBoxes.clear();
    }
    g_adapter_readback_write = 0;
    g_adapter_readback_read = 0;
    // Every slot was just cleared; keep the wake/adaptive-cadence counter
    // consistent with the visible slot state.
    g_readback_pending.store(0, std::memory_order_release);
    if (g_adapter_frame_mutex) {
        g_adapter_frame_mutex->Release();
        g_adapter_frame_mutex = nullptr;
    }
    if (g_adapter_frame_texture) {
        g_adapter_frame_texture->Release();
        g_adapter_frame_texture = nullptr;
    }
    if (g_adapter_frame_handle) {
        CloseHandle(g_adapter_frame_handle);
        g_adapter_frame_handle = nullptr;
    }
    g_adapter_frame_resource_id = 0;
    if (g_adapter_frame_context) {
        g_adapter_frame_context->Release();
        g_adapter_frame_context = nullptr;
    }
    if (g_adapter_frame_device) {
        g_adapter_frame_device->Release();
        g_adapter_frame_device = nullptr;
    }
    g_adapter_frame_target_luid = {};
    g_adapter_frame_target_selected = false;
    g_adapter_frame_consumer_api = 0;
    g_adapter_frame_width = 0;
    g_adapter_frame_height = 0;
    g_adapter_frame_format = DXGI_FORMAT_UNKNOWN;
    g_adapter_frame_initialized = false;
    g_adapter_force_full_readback = false;
    g_adapter_deferred_dirty_rects.clear();
}

static bool GetDeviceAdapterLuid(ID3D11Device* device, LUID& luid) {
    // The publisher mutex serializes this cache. Retaining the device prevents
    // a released device's address from being mistaken for a replacement.
    struct DeviceAdapterCache {
        ID3D11Device* device = nullptr;
        LUID luid{};
        ~DeviceAdapterCache() { if (device) device->Release(); }
    };
    static DeviceAdapterCache cache;
    if (!device)
        return false;
    if (cache.device == device) {
        luid = cache.luid;
        return true;
    }

    IDXGIDevice* dxgiDevice = nullptr;
    IDXGIAdapter* adapter = nullptr;
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
        return false;
    device->AddRef();
    if (cache.device)
        cache.device->Release();
    cache.device = device;
    cache.luid = adapterDesc.AdapterLuid;
    luid = adapterDesc.AdapterLuid;
    return true;
}

osc_frame_bridge::ConsumerAdapterInfo* GetConsumerAdapterInfo() {
    std::lock_guard<std::mutex> lock(g_consumer_adapter_info_mutex);
    if (g_consumer_adapter_info)
        return g_consumer_adapter_info;

    HANDLE mapping = OpenFileMappingW(
        FILE_MAP_READ, FALSE, osc_frame_bridge::ConsumerAdapterMappingName);
    if (!mapping)
        return nullptr;

    auto* info =
        static_cast<osc_frame_bridge::ConsumerAdapterInfo*>(
            MapViewOfFile(mapping, FILE_MAP_READ,
                0, 0, sizeof(osc_frame_bridge::ConsumerAdapterInfo)));
    if (!info) {
        CloseHandle(mapping);
        return nullptr;
    }

    g_consumer_adapter_mapping = mapping;
    g_consumer_adapter_info = info;
    return g_consumer_adapter_info;
}

static void StartPublishWorker();
static void StopPublishWorker();

// [FIX-B] publish-worker shared state (early: EnsureAdapterFrameTexture and
// the mirror-enable path touch these before the worker block below).
static HANDLE g_publish_worker_stop_event = nullptr;
static HANDLE g_publish_worker_thread = nullptr;
static std::atomic<long> g_publish_inflight{ 0 };
// Callers hold g_adapter_publish_mutex. The worker reserves its resource access
// under the same mutex, so zero cannot become nonzero during a rebuild.
static bool DeferResourceTransitionLocked() {
    if (g_publish_inflight.load(std::memory_order_acquire) == 0)
        return false;
    g_adapter_force_full_readback = true;
    static ULONGLONG lastLogMs = 0;
    const ULONGLONG now = GetTickCount64();
    if (!lastLogMs || now - lastLogMs >= 1000) {
        lastLogMs = now;
        PublisherLog("[adapter-bridge] resource change deferred until active publication completes");
    }
    return true;
}

static bool EnsureAdapterFrameDevice() {
    LUID targetLuid{};
    bool hasTargetAdapter = false;
    std::uint32_t consumerApi = osc_frame_bridge::ConsumerRenderApiD3D11;
    const auto* consumerAdapterInfo = GetConsumerAdapterInfo();
    if (consumerAdapterInfo &&
        consumerAdapterInfo->magic == osc_frame_bridge::ConsumerAdapterMagic &&
        consumerAdapterInfo->version == osc_frame_bridge::ConsumerAdapterVersion) {
        const LONG ownerPid = consumerAdapterInfo->ownerProcessId;
        const LONG64 packedLuid = consumerAdapterInfo->adapterLuid;
        const LONG publishedApi =
            osc_frame_bridge::LoadConsumerRenderApi(*consumerAdapterInfo);
        if (publishedApi == static_cast<LONG>(
                osc_frame_bridge::ConsumerRenderApiD3D9))
            consumerApi = osc_frame_bridge::ConsumerRenderApiD3D9;
        if (ownerPid > 0 && packedLuid != 0) {
            HANDLE ownerProcess = OpenProcess(SYNCHRONIZE, FALSE,
                static_cast<DWORD>(ownerPid));
            const bool ownerAlive = ownerProcess &&
                WaitForSingleObject(ownerProcess, 0) == WAIT_TIMEOUT;
            if (ownerProcess) CloseHandle(ownerProcess);
            if (ownerAlive) {
                targetLuid.LowPart = static_cast<DWORD>(packedLuid);
                targetLuid.HighPart = static_cast<LONG>(
                    static_cast<std::uint64_t>(packedLuid) >> 32);
                hasTargetAdapter = true;
            }
        }
    }

    LUID sourceLuid{};
    if (!GetDeviceAdapterLuid(HostGpuDevice(), sourceLuid))
        return false;
    if (!hasTargetAdapter)
        targetLuid = sourceLuid;
    if (g_adapter_frame_target_selected &&
        g_adapter_frame_target_luid.LowPart == targetLuid.LowPart &&
        g_adapter_frame_target_luid.HighPart == targetLuid.HighPart &&
        g_adapter_frame_consumer_api == consumerApi)
        return g_adapter_frame_device != nullptr ||
            (sourceLuid.LowPart == targetLuid.LowPart &&
             sourceLuid.HighPart == targetLuid.HighPart &&
             consumerApi != osc_frame_bridge::ConsumerRenderApiD3D9);

    if (DeferResourceTransitionLocked())
        return false;
    ReleaseAdapterFrameTexture();
    if (sourceLuid.LowPart == targetLuid.LowPart &&
        sourceLuid.HighPart == targetLuid.HighPart &&
        consumerApi != osc_frame_bridge::ConsumerRenderApiD3D9) {
        g_adapter_frame_target_luid = targetLuid;
        g_adapter_frame_target_selected = true;
        g_adapter_frame_consumer_api = consumerApi;
        PublisherLog("[adapter-bridge] game adapter matches the CEF device; using direct GPU copy");
        return true;
    }

    IDXGIFactory1* factory = nullptr;
    HRESULT result = CreateDXGIFactory1(
        __uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(result) || !factory) {
        g_adapter_frame_target_luid = targetLuid;
        g_adapter_frame_target_selected = true;
        g_adapter_frame_consumer_api = consumerApi;
        PublisherLog("[adapter-bridge] DXGI factory creation for game adapter failed: " +
            std::to_string(static_cast<long>(result)));
        return false;
    }

    IDXGIAdapter1* matchingAdapter = nullptr;
    for (UINT index = 0; ; ++index) {
        IDXGIAdapter1* adapter = nullptr;
        result = factory->EnumAdapters1(index, &adapter);
        if (result == DXGI_ERROR_NOT_FOUND)
            break;
        if (FAILED(result))
            break;
        DXGI_ADAPTER_DESC1 desc{};
        const HRESULT descResult = adapter->GetDesc1(&desc);
        if (SUCCEEDED(descResult) &&
            desc.AdapterLuid.LowPart == targetLuid.LowPart &&
            desc.AdapterLuid.HighPart == targetLuid.HighPart) {
            matchingAdapter = adapter;
            break;
        }
        adapter->Release();
    }
    factory->Release();
    if (!matchingAdapter) {
        g_adapter_frame_target_luid = targetLuid;
        g_adapter_frame_target_selected = true;
        g_adapter_frame_consumer_api = consumerApi;
        PublisherLog("[adapter-bridge] game adapter LUID is not available to the OSC process");
        return false;
    }

    D3D_FEATURE_LEVEL featureLevel{};
    result = D3D11CreateDevice(matchingAdapter, D3D_DRIVER_TYPE_UNKNOWN,
        nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
        D3D11_SDK_VERSION, &g_adapter_frame_device, &featureLevel,
        &g_adapter_frame_context);
    matchingAdapter->Release();
    if (FAILED(result) || !g_adapter_frame_device ||
        !g_adapter_frame_context) {
        char diagnostic[128]{};
        sprintf_s(diagnostic,
            "[adapter-bridge] game-adapter D3D11 device creation failed: 0x%08lX",
            static_cast<unsigned long>(result));
        PublisherLog(diagnostic);
        ReleaseAdapterFrameTexture();
        g_adapter_frame_target_luid = targetLuid;
        g_adapter_frame_target_selected = true;
        g_adapter_frame_consumer_api = consumerApi;
        return false;
    }

    {
        // [FIX-B] the publish worker touches this context off-thread.
        ID3D11Multithread* mt = nullptr;
        if (SUCCEEDED(g_adapter_frame_context->QueryInterface(
                __uuidof(ID3D11Multithread),
                reinterpret_cast<void**>(&mt)))) {
            mt->SetMultithreadProtected(TRUE);
            mt->Release();
        }
    }
    g_adapter_frame_target_luid = targetLuid;
    g_adapter_frame_target_selected = true;
    g_adapter_frame_consumer_api = consumerApi;
    char diagnostic[192]{};
    sprintf_s(diagnostic, sizeof(diagnostic),
        "[adapter-bridge] enabled CPU readback mirror for render API %lu "
        "on adapter %08lX:%08lX",
        static_cast<unsigned long>(consumerApi),
        static_cast<unsigned long>(targetLuid.HighPart),
        static_cast<unsigned long>(targetLuid.LowPart));
    PublisherLog(diagnostic);
    StartPublishWorker();
    return true;
}

static bool EnsureAdapterFrameTexture(ID3D11Texture2D* sourceTexture) {
    if (g_publish_worker_stopping.load(std::memory_order_acquire) ||
        !sourceTexture || !EnsureAdapterFrameDevice())
        return false;
    D3D11_TEXTURE2D_DESC sourceDesc{};
    sourceTexture->GetDesc(&sourceDesc);
    if (!sourceDesc.Width || !sourceDesc.Height ||
        sourceDesc.SampleDesc.Count != 1 ||
        static_cast<std::uint64_t>(sourceDesc.Width) * sourceDesc.Height * 4 >
            osc_frame_bridge::MaxFrameBytes)
        return false;

    if (g_adapter_frame_texture &&
        g_adapter_frame_width == sourceDesc.Width &&
        g_adapter_frame_height == sourceDesc.Height &&
        g_adapter_frame_format == sourceDesc.Format)
        return true;

    // Never wait while holding the mutex needed by worker completion. Keep the
    // current resources alive and let the next frame retry the transition.
    if (DeferResourceTransitionLocked())
        return false;
    ReleaseAdapterFrameTexture();
    if (!EnsureAdapterFrameDevice())
        return false;
    ID3D11Device* targetDevice = g_adapter_frame_device
        ? g_adapter_frame_device : HostGpuDevice();
    D3D11_TEXTURE2D_DESC sharedDesc = sourceDesc;
    sharedDesc.Usage = D3D11_USAGE_DEFAULT;
    sharedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    sharedDesc.CPUAccessFlags = 0;
    sharedDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

    HRESULT result = targetDevice->CreateTexture2D(
        &sharedDesc, nullptr, &g_adapter_frame_texture);
    if (FAILED(result)) {
        PublisherLog("[adapter-bridge] CreateTexture2D failed: " +
            std::to_string(static_cast<long>(result)));
        ReleaseAdapterFrameTexture();
        return false;
    }

    IDXGIResource1* sharedResource = nullptr;
    const std::uint64_t resourceId = ++g_adapter_frame_resource_generation;
    wchar_t sharedResourceName[MAX_PATH]{};
    const bool hasSharedResourceName = osc_frame_bridge::MakeSharedTextureName(
        GetCurrentProcessId(), resourceId, sharedResourceName,
        ARRAYSIZE(sharedResourceName));
    if (!hasSharedResourceName)
        result = E_INVALIDARG;
    else
        result = g_adapter_frame_texture->QueryInterface(
            __uuidof(IDXGIResource1), reinterpret_cast<void**>(&sharedResource));
    if (SUCCEEDED(result))
        result = sharedResource->CreateSharedHandle(
            nullptr, DXGI_SHARED_RESOURCE_READ, sharedResourceName,
            &g_adapter_frame_handle);
    if (sharedResource) sharedResource->Release();
    if (SUCCEEDED(result))
        result = g_adapter_frame_texture->QueryInterface(
            __uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void**>(&g_adapter_frame_mutex));
    if (SUCCEEDED(result) && g_adapter_frame_device) {
        D3D11_TEXTURE2D_DESC stagingDesc = sourceDesc;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        D3D11_QUERY_DESC queryDesc{};
        queryDesc.Query = D3D11_QUERY_EVENT;
        for (auto& slot : g_adapter_readback_slots) {
            result = HostGpuDevice()->CreateTexture2D(
                &stagingDesc, nullptr, &slot.texture);
            if (SUCCEEDED(result))
                result = HostGpuDevice()->CreateQuery(&queryDesc, &slot.completion);
            if (FAILED(result))
                break;
        }
    }
    if (FAILED(result) || !g_adapter_frame_handle || !g_adapter_frame_mutex) {
        PublisherLog("[adapter-bridge] shared-handle or keyed-mutex setup failed: " +
            std::to_string(static_cast<long>(result)));
        ReleaseAdapterFrameTexture();
        return false;
    }

    g_adapter_frame_width = sourceDesc.Width;
    g_adapter_frame_height = sourceDesc.Height;
    g_adapter_frame_format = sourceDesc.Format;
    g_adapter_frame_resource_id = resourceId;
    char diagnostic[256]{};
    sprintf_s(diagnostic, sizeof(diagnostic),
        "[adapter-bridge] created named NT shared texture id=%llu %ux%u format=%u "
        "adapterLuid=%08lX:%08lX",
        static_cast<unsigned long long>(g_adapter_frame_resource_id),
        sourceDesc.Width, sourceDesc.Height,
        static_cast<unsigned>(sourceDesc.Format),
        static_cast<unsigned long>(g_adapter_frame_target_luid.HighPart),
        static_cast<unsigned long>(g_adapter_frame_target_luid.LowPart));
    PublisherLog(diagnostic);
    return true;
}


static bool PublishCompletedAdapterFrame(std::uint64_t& completedCefSerial) {
    completedCefSerial = 0;
    AdapterReadbackSlot& slot =
        g_adapter_readback_slots[g_adapter_readback_read];
    {
        // [FIX-B] brief lock for slot state only; the heavy D3D work below
        // runs lock-free on the publish worker (contexts are MT-protected).
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        if (!slot.pending)
            return false;
    }

    const HRESULT ready = HostGpuContext()->GetData(
        slot.completion, nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (ready == S_FALSE)
        return false;
    if (FAILED(ready)) {
        PublisherLog("[adapter-bridge] readback completion query failed: " +
            std::to_string(static_cast<long>(ready)));
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        g_adapter_force_full_readback = true;
        slot.pending = false;
        slot.fullFrame = false;
        slot.dirtyBoxes.clear();
        g_readback_pending.fetch_sub(1, std::memory_order_acq_rel);
        g_adapter_readback_read =
            (g_adapter_readback_read + 1) % AdapterReadbackSlotCount;
        return false;
    }

    D3D11_MAPPED_SUBRESOURCE mapped{};
    const HRESULT mapResult = HostGpuContext()->Map(
        slot.texture, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
    bool published = false;
    bool requestFullReadback = false;
    bool completedFullFrame = false;
    if (SUCCEEDED(mapResult)) {
        const auto* mappedBytes =
            static_cast<const std::uint8_t*>(mapped.pData);
        if (g_adapter_frame_consumer_api ==
                osc_frame_bridge::ConsumerRenderApiD3D9) {
            // Classic D3D9 consumers read the CPU v1 BGRA mapping and cannot
            // open a D3D11 NT shared texture. Publish straight from the mapped
            // staging and skip the game-adapter keyed-mutex mirror entirely:
            // it added per-frame GPU copies plus AcquireSync coupling to the
            // game's Present thread, and a stuck keyed mutex used to freeze
            // the CPU bridge even though no D3D9 receiver consumed the mirror.
            D3D11_TEXTURE2D_DESC sourceDesc{};
            slot.texture->GetDesc(&sourceDesc);
            const bool d3d9Published =
                osc::d3d9_frame_publisher::PublishD3D9FramePixels(mappedBytes,
                    mapped.RowPitch, slot.dirtyBoxes, sourceDesc.Width,
                    sourceDesc.Height, slot.fullFrame);
            if (!d3d9Published) {
                PublisherLog("[d3d9-bridge] CPU frame publication failed");
            } else if (g_adapter_frame_device && g_adapter_frame_context) {
                // DX9Ex is strictly opt-in: the trusted in-game receiver
                // must advertise Stage/Commit capability + matching LUID.
                // Reconstruct a COMPLETE image from the CPU v1 bridge:
                // staging dirty rectangles are not a full GPU frame.
                const auto* consumer = GetConsumerAdapterInfo();
                const DWORD gamePid = consumer && consumer->ownerProcessId > 0
                    ? static_cast<DWORD>(consumer->ownerProcessId) : 0;
                if (!g_d3d9ex_cpu_only.load(std::memory_order_acquire) &&
                    !PreferGpuPreviewToCpuGpuUpload() &&
                    osc::d3d9ex_frame_publisher::CanPublish(
                        gamePid, g_adapter_frame_target_luid)) {
                    std::vector<std::uint8_t> fullPixels;
                    UINT frameWidth = 0, frameHeight = 0;
                    if (osc::d3d9_frame_publisher::SnapshotFullD3D9Frame(
                            fullPixels, frameWidth, frameHeight)) {
                        osc::d3d9ex_frame_publisher::TryPublish(
                            g_adapter_frame_device, g_adapter_frame_context,
                            fullPixels.data(), frameWidth * 4,
                            frameWidth, frameHeight,
                            g_adapter_frame_target_luid, gamePid);
                    }
                }
            }
            if (d3d9Published) {
                published = true;
                if (slot.fullFrame) {
                    osc::frame_utilities::LogAdapterFramePixelProof(
                        osc::frame_utilities::ComputeAdapterFrameFingerprint(
                            mappedBytes, mapped.RowPitch,
                            g_adapter_frame_width, g_adapter_frame_height));
                }
            } else {
                requestFullReadback = true;
            }
        } else {
            const HRESULT acquireResult = g_adapter_frame_mutex->AcquireSync(0, 0);
            if (acquireResult == S_OK) {
                const std::uint64_t pixelFingerprint = slot.fullFrame
                    ? osc::frame_utilities::ComputeAdapterFrameFingerprint(mappedBytes,
                        mapped.RowPitch, g_adapter_frame_width,
                        g_adapter_frame_height)
                    : 0;
                for (const D3D11_BOX& box : slot.dirtyBoxes) {
                    const std::size_t offset =
                        static_cast<std::size_t>(box.top) * mapped.RowPitch +
                        static_cast<std::size_t>(box.left) * 4;
                    g_adapter_frame_context->UpdateSubresource(
                        g_adapter_frame_texture, 0, &box, mappedBytes + offset,
                        mapped.RowPitch, 0);
                }
                HostGpuContext()->Unmap(slot.texture, 0);
                g_adapter_frame_context->Flush();
                const HRESULT releaseResult = g_adapter_frame_mutex->ReleaseSync(0);
                if (SUCCEEDED(releaseResult)) {
                    published = true;
                    if (slot.fullFrame)
                        osc::frame_utilities::LogAdapterFramePixelProof(
                            pixelFingerprint);
                } else {
                    requestFullReadback = true;
                    PublisherLog("[adapter-bridge] shared texture mutex release failed: " +
                        std::to_string(static_cast<long>(releaseResult)));
                }
            } else {
                HostGpuContext()->Unmap(slot.texture, 0);
                if (acquireResult == WAIT_TIMEOUT)
                    return false;
                requestFullReadback = true;
                PublisherLog("[adapter-bridge] shared texture mutex acquire failed: " +
                    std::to_string(static_cast<long>(acquireResult)));
            }
        }
        if (published && slot.fullFrame)
            completedFullFrame = true;
    } else if (mapResult == DXGI_ERROR_WAS_STILL_DRAWING) {
        return false;
    } else {
        requestFullReadback = true;
        PublisherLog("[adapter-bridge] completed readback map failed: " +
            std::to_string(static_cast<long>(mapResult)));
    }

    {
        // [FIX-B] publish back the slot so the UI thread may reuse it.
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        if (requestFullReadback)
            g_adapter_force_full_readback = true;
        else if (completedFullFrame) {
            g_adapter_frame_initialized = true;
            g_adapter_force_full_readback = false;
        }
        if (published)
            completedCefSerial = slot.cefContentSerial;
        slot.pending = false;
        slot.fullFrame = false;
        slot.cefContentSerial = 0;
        slot.dirtyBoxes.clear();
        g_readback_pending.fetch_sub(1, std::memory_order_acq_rel);
        g_adapter_readback_read =
            (g_adapter_readback_read + 1) % AdapterReadbackSlotCount;
    }
    return published;
}

bool QueueFrameReadbackLocked(ID3D11Texture2D* sourceTexture,
    const std::vector<CefRect>& dirtyRects) {
    if (g_publish_worker_stopping.load(std::memory_order_acquire))
        return false;
    AdapterReadbackSlot& slot =
        g_adapter_readback_slots[g_adapter_readback_write];
    D3D11_TEXTURE2D_DESC sourceDesc{};
    sourceTexture->GetDesc(&sourceDesc);
    if (slot.pending) {
        if (!g_adapter_force_full_readback) {
            g_adapter_deferred_dirty_rects.insert(
                g_adapter_deferred_dirty_rects.end(),
                dirtyRects.begin(), dirtyRects.end());
            if (g_adapter_deferred_dirty_rects.size() > 64) {
                const auto boxes = osc::frame_utilities::MakeAdapterDirtyBoxes(
                    g_adapter_deferred_dirty_rects,
                    sourceDesc.Width, sourceDesc.Height);
                g_adapter_deferred_dirty_rects.clear();
                for (const D3D11_BOX& box : boxes)
                    g_adapter_deferred_dirty_rects.emplace_back(
                        static_cast<int>(box.left),
                        static_cast<int>(box.top),
                        static_cast<int>(box.right - box.left),
                        static_cast<int>(box.bottom - box.top));
            }
        }
        return false;
    }

    std::vector<CefRect> combinedDirtyRects;
    combinedDirtyRects.reserve(
        g_adapter_deferred_dirty_rects.size() + dirtyRects.size());
    combinedDirtyRects.insert(combinedDirtyRects.end(),
        g_adapter_deferred_dirty_rects.begin(),
        g_adapter_deferred_dirty_rects.end());
    combinedDirtyRects.insert(combinedDirtyRects.end(),
        dirtyRects.begin(), dirtyRects.end());
    slot.dirtyBoxes = osc::frame_utilities::MakeAdapterDirtyBoxes(
        combinedDirtyRects, sourceDesc.Width, sourceDesc.Height);
    slot.fullFrame = !g_adapter_frame_initialized ||
        g_adapter_force_full_readback;
    if (slot.fullFrame && !slot.dirtyBoxes.empty()) {
        slot.dirtyBoxes.clear();
        slot.dirtyBoxes.push_back(D3D11_BOX{
            0, 0, 0, sourceDesc.Width, sourceDesc.Height, 1 });
    }
    if (slot.dirtyBoxes.empty()) {
        slot.fullFrame = false;
        return false;
    }

    for (const D3D11_BOX& box : slot.dirtyBoxes)
        HostGpuContext()->CopySubresourceRegion(
            slot.texture, 0, box.left, box.top, 0, sourceTexture, 0, &box);
    HostGpuContext()->End(slot.completion);
    // No Flush here: this runs inside the CEF UI callback on the shared
    // immediate context, and a per-paint GPU flush stalled CEF compositing
    // plus the desktop presenter. The publish worker flushes with bounded
    // cadence (once per tick, only while readbacks are pending), which also
    // guarantees forward progress for GetData(DONOTFLUSH) on this query.
    slot.cefContentSerial = g_cef_content_serial;
    slot.pending = true;
    g_adapter_readback_write =
        (g_adapter_readback_write + 1) % AdapterReadbackSlotCount;
    g_adapter_deferred_dirty_rects.clear();
    g_readback_pending.fetch_add(1, std::memory_order_acq_rel);
    if (g_publish_wake_event)
        SetEvent(g_publish_wake_event);
    if (HostWindow())
        SetTimer(HostWindow(), 6, 16, nullptr);
    return true;
}

bool PublishMetadataLocked(bool forceHidden,
    std::uint64_t completedCefContentSerial) {
    // [FIX-B] serialize bridge seqlock writes: the UI thread (visibility
    // changes) and the publish worker both push metadata now.
    static std::mutex g_bridge_metadata_mutex;
    std::lock_guard<std::mutex> metadata_lock(g_bridge_metadata_mutex);
    if (!g_frame_bridge) {
        const DWORD sizeHigh = static_cast<DWORD>(
            osc_frame_bridge::MappingBytes >> 32);
        const DWORD sizeLow = static_cast<DWORD>(
            osc_frame_bridge::MappingBytes & 0xFFFFFFFF);
        SetLastError(ERROR_SUCCESS);
        g_frame_bridge_mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            sizeHigh, sizeLow, osc_frame_bridge::MappingName);
        if (!g_frame_bridge_mapping) {
            PublisherLog("[adapter-bridge] CreateFileMappingW failed: " +
                std::to_string(GetLastError()));
            return false;
        }
        const bool createdMapping = GetLastError() != ERROR_ALREADY_EXISTS;
        g_frame_bridge = static_cast<osc_frame_bridge::Frame*>(
            MapViewOfFile(g_frame_bridge_mapping, FILE_MAP_WRITE, 0, 0,
                osc_frame_bridge::MappingBytes));
        if (!g_frame_bridge) {
            PublisherLog("[adapter-bridge] MapViewOfFile failed: " +
                std::to_string(GetLastError()));
            CloseHandle(g_frame_bridge_mapping);
            g_frame_bridge_mapping = nullptr;
            return false;
        }
        if (createdMapping) {
            g_frame_bridge->sequence = 0;
            g_frame_bridge->ownerProcessId = 0;
            g_frame_bridge->frameNumber = 0;
            g_frame_bridge->version = osc_frame_bridge::Version;
            std::atomic_thread_fence(std::memory_order_release);
            g_frame_bridge->magic = osc_frame_bridge::Magic;
        }
        PublisherLog("[adapter-bridge] shared CEF texture endpoint ready");
    }

    if (g_frame_bridge->magic != osc_frame_bridge::Magic ||
        g_frame_bridge->version != osc_frame_bridge::Version)
        return false;

    const LONG currentProcessId = static_cast<LONG>(GetCurrentProcessId());
    LONG ownerProcessId = InterlockedCompareExchange(
        &g_frame_bridge->ownerProcessId, currentProcessId, 0);
    if (ownerProcessId != 0 && ownerProcessId != currentProcessId) {
        HANDLE ownerProcess = OpenProcess(SYNCHRONIZE, FALSE,
            static_cast<DWORD>(ownerProcessId));
        const DWORD openError = ownerProcess ? ERROR_SUCCESS : GetLastError();
        const bool ownerExited = ownerProcess
            ? WaitForSingleObject(ownerProcess, 0) == WAIT_OBJECT_0
            : openError == ERROR_INVALID_PARAMETER;
        if (ownerProcess) CloseHandle(ownerProcess);
        if (!ownerExited ||
            InterlockedCompareExchange(&g_frame_bridge->ownerProcessId,
                currentProcessId, ownerProcessId) != ownerProcessId) {
            static bool loggedOtherOwner = false;
            if (!loggedOtherOwner) {
                loggedOtherOwner = true;
                PublisherLog("[adapter-bridge] another OSC process owns the shared-texture endpoint");
            }
            return false;
        }
    }

    InterlockedIncrement(&g_frame_bridge->sequence);
    g_frame_bridge->width = g_adapter_frame_width;
    g_frame_bridge->height = g_adapter_frame_height;
    g_frame_bridge->processId = GetCurrentProcessId();
    g_frame_bridge->sharedResourceId = g_adapter_frame_resource_id;
    const bool overlayVisible = HostOverlayVisible();
    // Resource metadata and liveness may be published repeatedly. Never
    // make the Hook re-copy unchanged GPU pixels by advancing frameNumber.
    // Only successfully committed pixels from a new CEF view callback do.
    g_frame_bridge->frameNumber = g_frame_cadence.Next(
        g_frame_bridge->frameNumber, completedCefContentSerial,
        !forceHidden && overlayVisible, completedCefContentSerial != 0);
    std::atomic_thread_fence(std::memory_order_release);
    InterlockedIncrement(&g_frame_bridge->sequence);
    osc::d3d9_frame_publisher::SetD3D9FrameVisibility(overlayVisible && !forceHidden);
    return true;
}

bool PublishMetadata(bool forceHidden) {
    std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
    return PublishMetadataLocked(forceHidden);
}

void PublishFrameLocked(ID3D11Texture2D* sourceTexture,
    const std::vector<CefRect>& dirtyRects) {
    if (!EnsureAdapterFrameTexture(sourceTexture))
        return;

    bool frameReady = false;
    if (g_adapter_frame_device) {
        // [FIX-B] UI thread only queues the GPU-side copy; the publish
        // worker completes readbacks + pushes bridge metadata off-thread.
        QueueFrameReadbackLocked(sourceTexture, dirtyRects);
        return;
    } else {
        const HRESULT acquireResult = g_adapter_frame_mutex->AcquireSync(0, 0);
        if (acquireResult != S_OK) {
            g_adapter_force_full_readback = true;
            return;
        }
        D3D11_TEXTURE2D_DESC sourceDesc{};
        sourceTexture->GetDesc(&sourceDesc);
        std::vector<D3D11_BOX> dirtyBoxes = osc::frame_utilities::MakeAdapterDirtyBoxes(
            dirtyRects, sourceDesc.Width, sourceDesc.Height);
        const bool fullFrame = !g_adapter_frame_initialized ||
            g_adapter_force_full_readback;
        if (fullFrame && !dirtyBoxes.empty()) {
            dirtyBoxes.clear();
            dirtyBoxes.push_back(D3D11_BOX{
                0, 0, 0, sourceDesc.Width, sourceDesc.Height, 1 });
        }
        for (const D3D11_BOX& box : dirtyBoxes)
            HostGpuContext()->CopySubresourceRegion(
                g_adapter_frame_texture, 0, box.left, box.top, 0,
                sourceTexture, 0, &box);
        HostGpuContext()->Flush();
        const HRESULT releaseResult = g_adapter_frame_mutex->ReleaseSync(0);
        if (FAILED(releaseResult)) {
            g_adapter_force_full_readback = true;
            PublisherLog("[adapter-bridge] shared texture mutex release failed: " +
                std::to_string(static_cast<long>(releaseResult)));
            return;
        }
        frameReady = !dirtyBoxes.empty();
        if (frameReady && fullFrame) {
            g_adapter_frame_initialized = true;
            g_adapter_force_full_readback = false;
        }
    }
    if (!frameReady)
        return;

    PublishMetadataLocked(false, g_cef_content_serial);
}


// [FIX-B] dedicated publish worker -------------------------------------
// The CPU readback mirror (GPU A staging copy -> CPU map -> GPU B upload)
// used to run inline on the CEF UI thread (OnAcceleratedPaint + timer 6)
// and throttled CEF's compositor to 1-11 fps whenever the game was
// focused (112 fps when not). The worker owns all heavy steps now.
static DWORD WINAPI OscPublishWorkerProc(LPVOID) {
    HANDLE waits[2] = { g_publish_worker_stop_event, g_publish_wake_event };
    for (;;) {
        // Adaptive cadence: a queued readback wakes this worker immediately
        // (SetEvent on the queue path). While readbacks or the opt-in D3D9Ex
        // preview are active, keep the 4ms completion tick; otherwise drop to
        // a 15ms idle poll so a hidden/static overlay costs almost no CPU.
        const bool workExpected = GpuPreviewEnabled() ||
            g_readback_pending.load(std::memory_order_acquire) > 0;
        const DWORD wait = WaitForMultipleObjects(2, waits, FALSE,
            workExpected ? 4 : 15);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED) break;
        // Poll pending GPU-completion and Stage/Commit acknowledgements
        // without ever executing D3D9Ex calls in the game's Present.
        osc::d3d9ex_frame_publisher::Poll();
        if (g_d3d9ex_cpu_only.load(std::memory_order_acquire))
            osc::d3d9ex_frame_publisher::Withdraw();
        PrepareGpuPreviewOnWorker();
        ProcessGpuPreviewOnWorker();
        if (GpuPreviewEnabled()) {
            static ULONGLONG lastPreviewLog=0;
            const ULONGLONG tick=GetTickCount64();
            if (!lastPreviewLog || tick-lastPreviewLog>=5000) {
                lastPreviewLog=tick;
                char line[220]{};
                const auto count=g_gpu_preview_enqueue_count.load();
                const auto readyCount=g_gpu_preview_ready_count.load();
                const auto submitted=g_gpu_preview_queued.load();
                sprintf_s(line,"[dx9ex-gpu-preview] queued=%llu drop=%llu gpu-submit=%llu unready=%llu cb-copy-us-avg=%llu stage-ready-ms-avg=%llu",
                    static_cast<unsigned long long>(submitted),
                    static_cast<unsigned long long>(g_gpu_preview_dropped.load()),
                    static_cast<unsigned long long>(g_gpu_preview_sent.load()),
                    static_cast<unsigned long long>(g_gpu_preview_not_ready.load()),
                    static_cast<unsigned long long>(count
                        ?g_gpu_preview_enqueue_cpu_us.load()/count:0),
                    static_cast<unsigned long long>(readyCount
                        ?g_gpu_preview_ready_latency_ms.load()/readyCount:0));
                PublisherLog(line);
            }
            if (!HostGameFocused() || !HostOverlayVisible())
                osc::d3d9ex_frame_publisher::Withdraw();
        }

        {
            std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
            if (g_publish_worker_stopping.load(std::memory_order_acquire))
                break;
            if (!g_adapter_frame_device)
                continue;
            const bool hasPending = std::any_of(
                std::begin(g_adapter_readback_slots),
                std::end(g_adapter_readback_slots),
                [](const AdapterReadbackSlot& slot) {
                    return slot.pending;
                });
            if (!hasPending) {
                // Idle maintenance uses the same lock as resource rebuilding.
                // The host texture pointer cannot be replaced during this copy.
                ID3D11Texture2D* sharedTexture = HostSharedTexture();
                if (!sharedTexture)
                    continue;
                const bool allowVisibleFrame =
                    HostGameFocused() && HostOverlayVisible();
                if (allowVisibleFrame && g_adapter_force_full_readback) {
                    const std::vector<CefRect> fullFrame{
                        CefRect(0, 0, HostViewWidth(), HostViewHeight()) };
                    QueueFrameReadbackLocked(sharedTexture, fullFrame);
                } else if (!g_adapter_deferred_dirty_rects.empty()) {
                    if (allowVisibleFrame) {
                        QueueFrameReadbackLocked(sharedTexture, {});
                    } else {
                        g_adapter_deferred_dirty_rects.clear();
                        g_adapter_force_full_readback = true;
                    }
                }
                continue;
            }

            // Reserve before dropping the mutex. Rebuild checks this count
            // while holding the mutex and defers instead of freeing live data.
            g_publish_inflight.fetch_add(1, std::memory_order_acq_rel);
        }

        // Submit the staging copies + completion queries queued by the CEF
        // callback from THIS thread: the callback no longer flushes (a
        // per-paint GPU flush on the shared immediate context stalled CEF
        // compositing and the desktop presenter). Bounded: at most once per
        // worker tick and only while readbacks are pending.
        HostGpuContext()->Flush();

        std::uint64_t completedCefSerial = 0;
        const bool published = PublishCompletedAdapterFrame(completedCefSerial);
        {
            std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
            if (published &&
                !g_publish_worker_stopping.load(std::memory_order_acquire))
                PublishMetadataLocked(
                    !(HostGameFocused() && HostOverlayVisible()),
                    completedCefSerial);
            // Metadata also references the resource dimensions/id, so finish
            // the reservation only after publishing it under the same lock.
            g_publish_inflight.fetch_sub(1, std::memory_order_acq_rel);
        }
    }
    return 0;
}

static void StartPublishWorker() {
    if (g_publish_worker_stopping.load(std::memory_order_acquire)) return;
    if (g_publish_worker_thread) return;
    if (!g_publish_worker_stop_event)
        g_publish_worker_stop_event =
            CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_publish_wake_event)
        g_publish_wake_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!g_publish_worker_stop_event || !g_publish_wake_event) return;
    g_publish_worker_thread = CreateThread(nullptr, 0,
        OscPublishWorkerProc, nullptr, 0, nullptr);
    if (g_publish_worker_thread)
        PublisherLog("[adapter-bridge] publish worker started (off CEF UI thread)");
}

static void StopPublishWorker() {
    HANDLE thread = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        g_publish_worker_stopping.store(true, std::memory_order_release);
        thread = g_publish_worker_thread;
        if (g_publish_worker_stop_event)
            SetEvent(g_publish_worker_stop_event);
    }
    if (!thread) return;

    // Completion takes the publisher mutex. Join only after releasing it and
    // keep handles/resources valid until the thread has actually exited.
    if (WaitForSingleObject(thread, INFINITE) != WAIT_OBJECT_0) {
        PublisherLog("[adapter-bridge] publish worker join failed; retaining its handles");
        return;
    }
    std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
    if (g_publish_worker_thread == thread) {
        CloseHandle(g_publish_worker_thread);
        g_publish_worker_thread = nullptr;
        if (g_publish_worker_stop_event) {
            CloseHandle(g_publish_worker_stop_event);
            g_publish_worker_stop_event = nullptr;
        }
        if (g_publish_wake_event) {
            CloseHandle(g_publish_wake_event);
            g_publish_wake_event = nullptr;
        }
    }
}


void Initialize(const HostBindings& bindings) { g_host = bindings; }
void SetD3D9ExCpuOnly(bool cpuOnly) {
    g_d3d9ex_cpu_only.store(cpuOnly, std::memory_order_release);
}
void MarkCefContentUpdatedLocked() {
    ++g_cef_content_serial;
    if (g_cef_content_serial == 0) ++g_cef_content_serial;
}
std::mutex& Mutex() { return g_adapter_publish_mutex; }
bool ForceFullReadbackLocked() { return g_adapter_force_full_readback; }
void SetForceFullReadbackLocked(bool force) { g_adapter_force_full_readback = force; }
bool HasPendingReadbackLocked() {
 return std::any_of(std::begin(g_adapter_readback_slots),std::end(g_adapter_readback_slots),
  [](const AdapterReadbackSlot& slot) { return slot.pending; });
}
bool HasCrossAdapterDeviceLocked() { return g_adapter_frame_device != nullptr; }
void Stop() {
    StopPublishWorker();
    {
        std::lock_guard<std::mutex> lock(g_adapter_publish_mutex);
        ReleaseGpuPreviewStagesLocked();
    }
    osc::d3d9ex_frame_publisher::Stop();
}
}
