// Native encoder only. No capture, audio, clock owner, subprocess or file/mux API.
// Intel structures come exclusively from the pinned official libvpl headers.
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_4.h>
#include <vpl/mfxdispatcher.h>
#include <vpl/mfxvideo.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#define API extern "C" __declspec(dllexport)
namespace {
void checked(mfxStatus s, const char* stage) {
    if (s < 0) throw std::runtime_error(std::string(stage) + ":mfx=" + std::to_string(s));
}
void hrcheck(HRESULT hr, const char* stage) {
    if (FAILED(hr)) throw std::runtime_error(std::string(stage) + ":hr=" + std::to_string(uint32_t(hr)));
}
template<class T> struct Com {
    T* p = nullptr;
    ~Com() { if (p) p->Release(); }
    T** out() { return &p; }
    T* operator->() const { return p; }
};
struct Surface {
    mfxFrameSurface1 frame{};
    std::vector<mfxU8> bytes;
};
struct Job {
    mfxBitstream bits{};
    mfxSyncPoint sync = nullptr;
    std::vector<mfxU8> bytes;
    Job(size_t size) : bytes(size) { bits.Data = bytes.data(); bits.MaxLength = (mfxU32)size; }
};
struct Context {
    std::mutex mutex;
    Com<ID3D11Device> device;
    mfxLoader loader = nullptr;
    mfxSession session = nullptr;
    mfxVideoParam params{};
    mfxExtVideoSignalInfo color{};
    mfxExtBuffer* extensions[1]{};
    std::vector<std::unique_ptr<Surface>> surfaces;
    std::deque<std::unique_ptr<Job>> jobs;
    DXGI_ADAPTER_DESC adapter{};
    mfxVersion version{};
    mfxIMPL impl{};
    bool encoderOpen = false, drained = false, faulted = false;
    uint64_t submitted = 0, delivered = 0;
    uint64_t lastPts = 0;
    bool havePts = false;
    int timeout = 5000;
    size_t bitCapacity = 1024 * 1024;
    std::string error;
    ~Context() {
        // Runtime releases all surface locks before owned storage/device destruction.
        if (encoderOpen) MFXVideoENCODE_Close(session);
        jobs.clear(); surfaces.clear();
        if (session) MFXClose(session);
        if (loader) MFXUnload(loader);
        if(device.p) { device.p->Release(); device.p=nullptr; }
    }
    void requireHealthy() { if (faulted) throw std::runtime_error("qsv_context_faulted"); }
    void attest(uint32_t low, int32_t high) {
        checked(MFXQueryVersion(session, &version), "QueryVersion");
        // This legacy 1.34 runtime returns a BORROWED GetHandle device, unlike
        // current VPL documentation. Verified by before/after COM counts and
        // repeated create/encode/drain/destroy tests on this driver. Gate other
        // runtime versions until their handle ownership has been validated.
        if(version.Major!=1 || version.Minor!=34) throw std::runtime_error("qsv_runtime_handle_ownership_not_verified");
        mfxHDL handle = nullptr;
        checked(MFXVideoCORE_GetHandle(session, MFX_HANDLE_D3D11_DEVICE, &handle), "GetHandle");
        if (!handle) throw std::runtime_error("qsv_device_handle_missing");
        Com<IDXGIDevice> dxgi;
        HRESULT hr = ((ID3D11Device*)handle)->QueryInterface(__uuidof(IDXGIDevice), (void**)dxgi.out());
        // Only the successful QueryInterface reference is owned/released here.
        hrcheck(hr, "QueryDXGI");
        Com<IDXGIAdapter> parent;
        hrcheck(dxgi->GetAdapter(parent.out()), "GetAdapter");
        hrcheck(parent->GetDesc(&adapter), "GetDesc");
        if (adapter.AdapterLuid.LowPart != low || adapter.AdapterLuid.HighPart != high)
            throw std::runtime_error("qsv_actual_adapter_luid_mismatch");
        checked(MFXQueryIMPL(session, &impl), "QueryIMPL");
        auto base = impl & 0xff;
        if ((base != MFX_IMPL_HARDWARE && base != MFX_IMPL_HARDWARE2 && base != MFX_IMPL_HARDWARE3 && base != MFX_IMPL_HARDWARE4) ||
            (impl & 0x0f00) != MFX_IMPL_VIA_D3D11)
            throw std::runtime_error("qsv_hardware_implementation_required");
    }
    void verifyParams() {
        mfxVideoParam actual{};
        mfxExtVideoSignalInfo actualColor{};
        mfxExtBuffer* actualExtensions[]{(mfxExtBuffer*)&actualColor};
        if(params.NumExtParam) {
            actualColor.Header.BufferId=MFX_EXTBUFF_VIDEO_SIGNAL_INFO;
            actualColor.Header.BufferSz=sizeof(actualColor);
            actual.NumExtParam=1; actual.ExtParam=actualExtensions;
        }
        checked(MFXVideoENCODE_GetVideoParam(session, &actual), "GetVideoParam");
        if(params.NumExtParam && (actualColor.ColourDescriptionPresent!=1 || actualColor.VideoFullRange!=color.VideoFullRange ||
            actualColor.ColourPrimaries!=1 || actualColor.TransferCharacteristics!=1 || actualColor.MatrixCoefficients!=1))
            throw std::runtime_error("qsv_negotiated_color_changed");
        if (actual.mfx.CodecId != MFX_CODEC_AVC || actual.mfx.RateControlMethod != params.mfx.RateControlMethod ||
            actual.mfx.GopPicSize != params.mfx.GopPicSize || actual.mfx.TargetUsage != params.mfx.TargetUsage ||
            actual.IOPattern != MFX_IOPATTERN_IN_SYSTEM_MEMORY || actual.mfx.FrameInfo.FourCC != MFX_FOURCC_NV12 ||
            actual.mfx.GopRefDist != 1 || actual.mfx.FrameInfo.CropW != params.mfx.FrameInfo.CropW ||
            actual.mfx.FrameInfo.CropH != params.mfx.FrameInfo.CropH ||
            actual.mfx.FrameInfo.FrameRateExtN != params.mfx.FrameInfo.FrameRateExtN ||
            actual.mfx.FrameInfo.FrameRateExtD != 1)
            throw std::runtime_error("qsv_negotiated_parameters_changed");
        if (params.mfx.RateControlMethod == MFX_RATECONTROL_VBR && actual.mfx.TargetKbps != params.mfx.TargetKbps)
            throw std::runtime_error("qsv_negotiated_bitrate_changed");
        if (params.mfx.RateControlMethod == MFX_RATECONTROL_VBR && actual.mfx.MaxKbps != params.mfx.MaxKbps)
            throw std::runtime_error("qsv_negotiated_maxrate_changed");
        if (params.mfx.RateControlMethod == MFX_RATECONTROL_CQP &&
            (actual.mfx.QPI!=params.mfx.QPI || actual.mfx.QPP!=params.mfx.QPP || actual.mfx.QPB!=params.mfx.QPB))
            throw std::runtime_error("qsv_negotiated_quantizers_changed");
    }
    void begin() {
        requireHealthy();
        if (!jobs.empty()) throw std::runtime_error("qsv_undelivered_packets");
        for (auto& s : surfaces) if (s->frame.Data.Locked) throw std::runtime_error("qsv_locked_surface_at_restart");
        if (encoderOpen) { checked(MFXVideoENCODE_Close(session), "EncoderClose"); encoderOpen = false; }
        checked(MFXVideoENCODE_Init(session, &params), "EncoderInit"); encoderOpen = true;
        verifyParams(); drained = false; havePts = false;
    }
    // Copies caller bytes before submission. Driver-owned locks protect our pool.
    void submit(const uint8_t* data, int length, int stride, uint64_t pts) {
        requireHealthy();
        if (drained) throw std::runtime_error("qsv_submit_after_drain");
        if (!data || stride < params.mfx.FrameInfo.CropW || length < int64_t(stride) * params.mfx.FrameInfo.CropH * 3 / 2)
            throw std::runtime_error("qsv_invalid_nv12_buffer");
        if (havePts && pts <= lastPts) throw std::runtime_error("qsv_nonmonotonic_input_pts");
        Surface* owned = nullptr;
        for (auto& s : surfaces) if (!s->frame.Data.Locked) { owned = s.get(); break; }
        if (!owned) throw std::runtime_error("qsv_owned_surface_pool_exhausted");
        auto& f = owned->frame;
        const int width = params.mfx.FrameInfo.CropW, height = params.mfx.FrameInfo.CropH;
        for (int y=0; y<height; ++y) std::memcpy(f.Data.Y + size_t(y)*f.Data.Pitch, data+size_t(y)*stride, width);
        for (int y=0; y<height/2; ++y) std::memcpy(f.Data.UV + size_t(y)*f.Data.Pitch, data+size_t(stride)*height+size_t(y)*stride, width);
        f.Data.TimeStamp = pts;
        encode(&f);
        lastPts = pts; havePts = true; ++submitted;
    }
    // Returns true when a null submission reaches MORE_DATA (drain complete).
    bool encode(mfxFrameSurface1* surface) {
        if (jobs.size() >= 32) throw std::runtime_error("qsv_packet_queue_limit");
        auto job = std::make_unique<Job>(bitCapacity);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
        for (;;) {
            mfxStatus s = MFXVideoENCODE_EncodeFrameAsync(session, nullptr, surface, &job->bits, &job->sync);
            if (s == MFX_WRN_DEVICE_BUSY) {
                if (std::chrono::steady_clock::now() >= deadline) throw std::runtime_error("qsv_device_busy_timeout");
                Sleep(1); continue; // DEVICE_BUSY has not accepted input.
            }
            if (s == MFX_ERR_NOT_ENOUGH_BUFFER && job->bytes.size() < 16*1024*1024) {
                bitCapacity = std::min<size_t>(16*1024*1024, job->bytes.size()*2);
                job = std::make_unique<Job>(bitCapacity); continue;
            }
            if (s == MFX_ERR_MORE_DATA) return surface == nullptr;
            checked(s, "EncodeFrameAsync");
            if (!job->sync) throw std::runtime_error("qsv_missing_sync_point");
            jobs.push_back(std::move(job)); return false;
        }
    }
};
int fail(Context* c, const std::exception& ex) { c->error = ex.what(); c->faulted = true; return -1; }
}

API int dc_qsv_abi_version() { return 3; }
API int dc_qsv_create(uint32_t low, int32_t high, int width, int height, int fps, int gop, int rc,
                      uint32_t bitrate, int qp, int depth, int timeout, int color, void** output, char* error, int errorSize) {
    if (!output) return -1;
    *output = nullptr;
    try {
        if(color<0 || color>2) throw std::runtime_error("qsv_invalid_color_descriptor");
        if ((!low && !high) || width<16 || height<16 || width>1920 || height>1080 || (width&1) || (height&1) ||
            fps<1 || fps>120 || gop<1 || gop>600 || (rc!=1 && rc!=2) || depth<1 || depth>8 || timeout<1 || timeout>30000 ||
            (rc==1 && (bitrate<1000 || bitrate>30000000)) || (rc==2 && (qp<1 || qp>51)))
            throw std::runtime_error("qsv_invalid_configuration_or_cbr_known_failure");
        auto c = std::make_unique<Context>(); c->timeout = timeout;
        Com<IDXGIFactory4> factory;
        hrcheck(CreateDXGIFactory1(__uuidof(IDXGIFactory4), (void**)factory.out()), "CreateFactory");
        LUID luid{low,high}; Com<IDXGIAdapter1> adapter;
        hrcheck(factory->EnumAdapterByLuid(luid, __uuidof(IDXGIAdapter1), (void**)adapter.out()), "EnumAdapterByLuid");
        D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0};
        hrcheck(D3D11CreateDevice(adapter.p,D3D_DRIVER_TYPE_UNKNOWN,nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,levels,2,D3D11_SDK_VERSION,
            c->device.out(),nullptr,nullptr), "CreateD3D11Device");
        Com<ID3D10Multithread> multithread;
        hrcheck(c->device->QueryInterface(__uuidof(ID3D10Multithread),(void**)multithread.out()),"D3DMultithread");
        multithread->SetMultithreadProtected(TRUE);
        c->loader = MFXLoad(); if (!c->loader) throw std::runtime_error("MFXLoad_failed");
        auto filter = [&](const char* property, uint32_t value) {
            mfxConfig cfg = MFXCreateConfig(c->loader); if (!cfg) throw std::runtime_error("MFXCreateConfig_failed");
            mfxVariant v{}; v.Type=MFX_VARIANT_TYPE_U32; v.Data.U32=value;
            checked(MFXSetConfigFilterProperty(cfg,(mfxU8*)property,v),property);
        };
        filter("mfxImplDescription.Impl",MFX_IMPL_TYPE_HARDWARE);
        mfxVersion required{}; required.Major=1; required.Minor=3;
        filter("mfxImplDescription.ApiVersion.Version",required.Version);
        filter("mfxImplDescription.AccelerationMode",MFX_ACCEL_MODE_VIA_D3D11);
        DXGI_ADAPTER_DESC1 requestedDesc{}; hrcheck(adapter->GetDesc1(&requestedDesc),"RequestedDesc");
        filter("mfxImplDescription.VendorID",requestedDesc.VendorId);
        mfxConfig deviceCfg=MFXCreateConfig(c->loader); if(!deviceCfg) throw std::runtime_error("DeviceConfig_failed");
        mfxVariant deviceValue{}; deviceValue.Type=MFX_VARIANT_TYPE_PTR; deviceValue.Data.Ptr=&luid;
        checked(MFXSetConfigFilterProperty(deviceCfg,(mfxU8*)"mfxExtendedDeviceId.DeviceLUID",deviceValue),"DeviceLUIDFilter");
        deviceValue.Type=MFX_VARIANT_TYPE_U16; deviceValue.Data.U16=(mfxU16)requestedDesc.DeviceId;
        checked(MFXSetConfigFilterProperty(deviceCfg,(mfxU8*)"mfxExtendedDeviceId.DeviceID",deviceValue),"DeviceIDFilter");
        filter("mfxExtendedDeviceId.LUIDDeviceNodeMask",1);
        filter("mfxHandleType",MFX_HANDLE_D3D11_DEVICE);
        mfxConfig handleCfg=MFXCreateConfig(c->loader); if(!handleCfg) throw std::runtime_error("HandleConfig_failed");
        deviceValue.Type=MFX_VARIANT_TYPE_PTR; deviceValue.Data.Ptr=c->device.p;
        checked(MFXSetConfigFilterProperty(handleCfg,(mfxU8*)"mfxHDL",deviceValue),"DeviceHandleFilter");
        checked(MFXCreateSession(c->loader,0,&c->session),"MFXCreateSession");
        c->params.AsyncDepth=(mfxU16)depth;
        c->params.IOPattern=MFX_IOPATTERN_IN_SYSTEM_MEMORY;
        if(color) {
            auto& v=c->color;
            v.Header.BufferId=MFX_EXTBUFF_VIDEO_SIGNAL_INFO; v.Header.BufferSz=sizeof(v);
            v.VideoFormat=5; v.VideoFullRange=color==2?1:0; v.ColourDescriptionPresent=1;
            v.ColourPrimaries=1; v.TransferCharacteristics=1; v.MatrixCoefficients=1;
            c->extensions[0]=(mfxExtBuffer*)&v; c->params.NumExtParam=1; c->params.ExtParam=c->extensions;
        }
        auto& m=c->params.mfx;
        m.CodecId=MFX_CODEC_AVC; m.CodecProfile=MFX_PROFILE_AVC_HIGH; m.TargetUsage=MFX_TARGETUSAGE_BEST_SPEED;
        m.RateControlMethod=rc==1?MFX_RATECONTROL_VBR:MFX_RATECONTROL_CQP;
        // These rate-control fields are unions in the Intel ABI.
        // Writing QPB in VBR would overwrite TargetKbps.
        if(rc==1) {
            m.TargetKbps=(mfxU16)(bitrate/1000);
            m.MaxKbps=(mfxU16)(bitrate/1000*2);
        } else {
            m.QPI=m.QPP=m.QPB=(mfxU16)qp;
        }
        m.GopPicSize=(mfxU16)gop; m.GopRefDist=1; m.GopOptFlag=MFX_GOP_CLOSED; m.IdrInterval=0;
        auto& f=m.FrameInfo;
        f.FourCC=MFX_FOURCC_NV12; f.ChromaFormat=MFX_CHROMAFORMAT_YUV420; f.PicStruct=MFX_PICSTRUCT_PROGRESSIVE;
        f.Width=(mfxU16)((width+15)&~15); f.Height=(mfxU16)((height+15)&~15);
        f.CropW=(mfxU16)width; f.CropH=(mfxU16)height; f.FrameRateExtN=fps; f.FrameRateExtD=1;
        mfxFrameAllocRequest request{};
        checked(MFXVideoENCODE_QueryIOSurf(c->session,&c->params,&request),"QueryIOSurf");
        const int count=std::max<int>(request.NumFrameSuggested+depth+2,8);
        if(count>32) throw std::runtime_error("qsv_surface_budget_exceeded");
        for(int i=0;i<count;++i) {
            auto s=std::make_unique<Surface>(); s->bytes.resize(size_t(f.Width)*f.Height*3/2,128);
            s->frame.Info=f; s->frame.Data.Pitch=f.Width;
            s->frame.Data.Y=s->bytes.data(); s->frame.Data.UV=s->bytes.data()+size_t(f.Width)*f.Height;
            c->surfaces.push_back(std::move(s));
        }
        c->begin(); c->attest(low,high);
        *output=c.release(); return 0;
    } catch(const std::exception& ex) {
        if(error && errorSize>0) { std::strncpy(error,ex.what(),errorSize-1); error[errorSize-1]=0; }
        return -1;
    }
}
API int dc_qsv_identity(void* handle, uint32_t* low, int32_t* high, uint32_t* vendor, uint32_t* device,
                       uint32_t* major, uint32_t* minor, uint32_t* impl) {
    auto c=(Context*)handle; if(!c) return -1; std::lock_guard<std::mutex> lock(c->mutex);
    *low=c->adapter.AdapterLuid.LowPart; *high=c->adapter.AdapterLuid.HighPart;
    *vendor=c->adapter.VendorId; *device=c->adapter.DeviceId; *major=c->version.Major; *minor=c->version.Minor; *impl=c->impl;
    return 0;
}
API int dc_qsv_begin(void* handle) {
    auto c=(Context*)handle; if(!c) return -1; std::lock_guard<std::mutex> lock(c->mutex);
    try { c->begin(); return 0; } catch(const std::exception& ex) { return fail(c,ex); }
}
API int dc_qsv_submit(void* handle,const uint8_t* bytes,int length,int stride,uint64_t pts) {
    auto c=(Context*)handle; if(!c) return -1; std::lock_guard<std::mutex> lock(c->mutex);
    try { c->submit(bytes,length,stride,pts); return 0; } catch(const std::exception& ex) { return fail(c,ex); }
}
API int dc_qsv_drain(void* handle) {
    auto c=(Context*)handle; if(!c) return -1; std::lock_guard<std::mutex> lock(c->mutex);
    try { c->requireHealthy(); if(c->drained) return 1; c->drained=c->encode(nullptr); return c->drained?1:0; }
    catch(const std::exception& ex) { return fail(c,ex); }
}
// 0 packet copied, 1 no packet ready, 2 output capacity too small (packet retained).
API int dc_qsv_receive(void* handle,uint8_t* output,int capacity,int* length,uint64_t* pts,int64_t* dts,int* key,int waitMs) {
    auto c=(Context*)handle; if(!c) return -1; std::lock_guard<std::mutex> lock(c->mutex);
    try {
        c->requireHealthy(); *length=0; if(c->jobs.empty()) return 1;
        auto& j=*c->jobs.front();
        mfxStatus s=MFXVideoCORE_SyncOperation(c->session,j.sync,(mfxU32)std::max(0,waitMs));
        if(s==MFX_WRN_IN_EXECUTION) return 1;
        checked(s,"SyncOperation");
        *length=(int)j.bits.DataLength;
        if(!*length || *length>16*1024*1024) throw std::runtime_error("qsv_invalid_bitstream_length");
        if(capacity<*length) return 2;
        if(!output) throw std::runtime_error("qsv_output_buffer_missing");
        std::memcpy(output,j.bits.Data+j.bits.DataOffset,*length);
        *pts=j.bits.TimeStamp; *dts=j.bits.DecodeTimeStamp;
        *key=(j.bits.FrameType&(MFX_FRAMETYPE_IDR|MFX_FRAMETYPE_I))?1:0;
        c->jobs.pop_front(); ++c->delivered; return 0;
    } catch(const std::exception& ex) { return fail(c,ex); }
}
API uint64_t dc_qsv_counter(void* handle,int field) {
    auto c=(Context*)handle; if(!c) return 0; std::lock_guard<std::mutex> lock(c->mutex);
    if(field==0) return c->submitted; if(field==1) return c->delivered; if(field==2) return c->jobs.size();
    uint64_t count=0; for(auto& s:c->surfaces) if(s->frame.Data.Locked) ++count; return count;
}
API const char* dc_qsv_error(void* handle) { auto c=(Context*)handle; return c?c->error.c_str():"qsv_null_handle"; }
API void dc_qsv_destroy(void* handle) { delete (Context*)handle; }
