// Isolated D3D9Ex <-> D3D11 GPU-sharing compatibility probe.
// No game is loaded and the OSC Host publisher is not modified.
// Windows legacy (non-NT) shared HANDLE only; do NOT CloseHandle it.
#include <windows.h>
#include <d3d9.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdint>
#include <vector>
#include <iostream>
#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace {
template<typename T> void Release(T*& p) { if (p) {p->Release(); p=nullptr;} }
LRESULT CALLBACK WindowProc(HWND w, UINT msg, WPARAM p1, LPARAM p2)
{ return DefWindowProcW(w,msg,p1,p2); }

using BindGpuFrame = BOOL(WINAPI *)(IDirect3DDevice9Ex*, HANDLE,
    std::uint32_t, std::uint32_t, std::uint64_t);
using UnbindGpuFrame = BOOL(WINAPI *)(IDirect3DDevice9Ex*);
using RenderOverlay = BOOL(WINAPI *)(IDirect3DDevice9*);
using GetAdapterState = BOOL(WINAPI *)(std::uint64_t *, BOOL *, BOOL *,
    std::uint64_t *);

using StageGpuFrame = BindGpuFrame;
using CommitGpuFrame = BOOL(WINAPI *)(IDirect3DDevice9Ex*, std::uint64_t);
using DiscardPendingGpuFrame = UnbindGpuFrame;

// Exercise a REAL resource generation change with visibly different
// pixels. The active GPU frame must stay unchanged until Commit.
// Host is responsible for synchronization; this test fills both textures
// before staging, and never writes during the draw.
bool TestStagedGpuFrameSwitch(IDirect3DDevice9Ex* device, HMODULE hook,
    IDirect3DSurface9* backBuffer, IDirect3DSurface9* image,
    HANDLE originalHandle, UINT width, UINT height)
{
    auto stage = reinterpret_cast<StageGpuFrame>(GetProcAddress(
        hook, "NvOscAdapterStageD3D9ExGpuFrame"));
    auto commit = reinterpret_cast<CommitGpuFrame>(GetProcAddress(
        hook, "NvOscAdapterCommitD3D9ExGpuFrame"));
    auto discard = reinterpret_cast<DiscardPendingGpuFrame>(GetProcAddress(
        hook, "NvOscAdapterDiscardD3D9ExPendingGpuFrame"));
    auto render = reinterpret_cast<RenderOverlay>(GetProcAddress(
        hook, "NvOscAdapterRenderD3D9ForTest"));
    if (!stage || !commit || !discard || !render) return false;

    IDirect3DTexture9* produced = nullptr;
    IDirect3DSurface9* producedSurface = nullptr;
    HANDLE newHandle = nullptr;
    HRESULT hr = device->CreateTexture(width, height, 1,
        D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
        &produced, &newHandle);
    if (FAILED(hr) || !produced || !newHandle)
    {
        Release(produced);
        return false;
    }
    bool ok = false;
    do {
        hr = produced->GetSurfaceLevel(0, &producedSurface);
        if (FAILED(hr)) break;
        hr = device->ColorFill(producedSurface, nullptr,
            D3DCOLOR_ARGB(255, 0x78, 0x9A, 0xBC));
        if (FAILED(hr)) break;

        auto outputPixel = [&]() -> std::uint32_t {
            if (FAILED(device->Clear(0, nullptr, D3DCLEAR_TARGET,
                D3DCOLOR_ARGB(255, 0, 0, 0), 1.0f, 0)) ||
                !render(device) ||
                FAILED(device->GetRenderTargetData(backBuffer, image)))
                return 0xffffffffu;
            D3DLOCKED_RECT locked{};
            if (FAILED(image->LockRect(
                &locked, nullptr, D3DLOCK_READONLY)))
                return 0xffffffffu;
            D3DSURFACE_DESC desc{};
            backBuffer->GetDesc(&desc);
            auto rgb = *reinterpret_cast<const std::uint32_t*>(
                static_cast<const std::uint8_t*>(locked.pBits) +
                (desc.Height/2)*locked.Pitch+(desc.Width/2)*4);
            image->UnlockRect();
            return rgb & 0x00ffffffu;
        };

        if (stage(device,newHandle,width+1,height,50)) break;
        if (!stage(device,newHandle,width,height,51)) break;
        if (commit(device,50)) break;  // wrong generation rejected
        const auto before=outputPixel();
        if (before != 0x00123456u) break; // not swapped early
        if (!commit(device,51) || commit(device,51)) break;
        const auto after=outputPixel();
        if (after != 0x00789abcu) break;
        // Discard an unready subsequent generation while the committed
        // frame remains visible. Then restore the source frame before
        // retiring our temporary producer resource.
        if (!stage(device,originalHandle,width,height,52) ||
            !discard(device) || discard(device)) break;
        if (outputPixel() != 0x00789abcu) break;
        if (!stage(device,originalHandle,width,height,53) ||
            !commit(device,53)) break;
        if (outputPixel() != 0x00123456u) break;
        std::cout << "STAGE_COMMIT_PASS old=0x" << std::hex << before
                  << " committed=0x" << after << std::dec << "\n";
        ok = true;
    } while (false);
    Release(producedSurface);
    Release(produced);
    return ok;
}

bool TestHookGpuOverlay(IDirect3DDevice9Ex* device, HANDLE sharedHandle,
    UINT width, UINT height, HMODULE hook)
{
    const auto bind = reinterpret_cast<BindGpuFrame>(
        GetProcAddress(hook, "NvOscAdapterBindD3D9ExGpuFrame"));
    const auto unbind = reinterpret_cast<UnbindGpuFrame>(
        GetProcAddress(hook, "NvOscAdapterUnbindD3D9ExGpuFrame"));
    const auto render = reinterpret_cast<RenderOverlay>(
        GetProcAddress(hook, "NvOscAdapterRenderD3D9ForTest"));
    const auto status = reinterpret_cast<GetAdapterState>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9AdapterStatus"));
    if(!bind || !unbind || !render || !status) {
        std::cerr << "Missing D3D9Ex GPU renderer exports" << std::endl;
        return false;
    }
    if(bind(device,nullptr,width,height,1) ||
       bind(device,sharedHandle,0,height,1) ||
       !bind(device,sharedHandle,width,height,1) ||
       !bind(device,sharedHandle,width,height,2)) {
        std::cerr << "D3D9Ex GPU handle validation/rebind failed" << std::endl;
        return false;
    }
    std::uint64_t luid=0, skipped=0;
    BOOL isEx=FALSE, gpuBound=FALSE;
    if(!status(&luid,&isEx,&gpuBound,&skipped) || !gpuBound) {
        std::cerr << "D3D9Ex GPU bound status not set" << std::endl;
        unbind(device);
        return false;
    }
    // Simulate a Host resource generation with incompatible dimensions.
    // The failed replacement must retire the old GPU alias; otherwise a
    // stale frame would stay onscreen after Host changes adapters.
    const bool rejectedMismatch = !bind(device,sharedHandle,width+1,height,3);
    const bool fallbackState = status(&luid,&isEx,&gpuBound,&skipped) &&
        !gpuBound;
    if (!rejectedMismatch || !fallbackState ||
        !bind(device,sharedHandle,width,height,4))
    {
        std::cerr << "GPU generation rollover/fallback/rebind failed"
                  << std::endl;
        unbind(device);
        return false;
    }
    IDirect3DSurface9* backBuffer=nullptr;
    IDirect3DSurface9* image=nullptr;
    bool ok=false;
    do {
        if(FAILED(device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&backBuffer)))
            break;
        D3DSURFACE_DESC desc{};backBuffer->GetDesc(&desc);
        if(FAILED(device->CreateOffscreenPlainSurface(desc.Width,desc.Height,
            desc.Format,D3DPOOL_SYSTEMMEM,&image,nullptr))) break;
        if(FAILED(device->Clear(0,nullptr,D3DCLEAR_TARGET,
            D3DCOLOR_ARGB(255,0,0,0),1.0f,0))) break;
        if(!render(device)) break;
        if(!status(&luid,&isEx,&gpuBound,&skipped) ||
           !luid || !isEx || !gpuBound) {
            std::cerr << "Phase-0 GPU renderer LUID/API/bound telemetry invalid"
                      << std::endl;
            break;
        }
        if(FAILED(device->GetRenderTargetData(backBuffer,image))) break;
        D3DLOCKED_RECT locked{};
        if(FAILED(image->LockRect(&locked,nullptr,D3DLOCK_READONLY))) break;
        const std::uint32_t pixel=*reinterpret_cast<const std::uint32_t*>(
            static_cast<const std::uint8_t*>(locked.pBits)+
            (desc.Height/2)*locked.Pitch+(desc.Width/2)*4);
        image->UnlockRect();
        std::cout << "D3D9Ex Hook GPU overlay pixel=0x"
                  << std::hex << pixel << std::dec << "\n";
        ok=(pixel & 0x00FFFFFFu) == 0x00123456u;
        if (ok && !TestStagedGpuFrameSwitch(device, hook, backBuffer,
            image, sharedHandle, width, height))
        {
            std::cerr << "D3D9Ex Stage/Commit/Discard GPU frame regression failed\n";
            ok = false;
        }
    }while(false);
    Release(image);Release(backBuffer);
    const bool released=unbind(device);
    const bool notDoubleReleased=!unbind(device);
    const bool stateCleared=status(&luid,&isEx,&gpuBound,&skipped) &&
        !gpuBound && isEx && luid!=0;
    return ok && released && notDoubleReleased && stateCleared;
}

// This is the production-relevant direction: a D3D11 publisher creates
// a legacy-shared GPU resource, while D3D9Ex is the receiver.
bool TestD3D11Publisher(IDirect3DDevice9Ex* dev9,
    IDXGIAdapter* adapter, HMODULE hook)
{
    constexpr UINT width=32,height=24;
    constexpr std::uint32_t expectedPixel=0xFF123456u;
    ID3D11Device* d11=nullptr;
    ID3D11DeviceContext* ctx=nullptr;
    ID3D11Texture2D* tex11=nullptr;
    IDXGIResource* dxgi=nullptr;
    IDirect3DTexture9* tex9=nullptr;
    IDirect3DSurface9* surface=nullptr;
    IDirect3DSurface9* readback=nullptr;
    int result=1;
    HRESULT hr=D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,
        &d11,nullptr,&ctx);
    if(FAILED(hr)) {
        std::cerr<<"D3D11 producer CreateDevice failed"<<std::endl;
        return result;
    }
    do {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=width;desc.Height=height;
        desc.MipLevels=1;desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;
        desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags=D3D11_RESOURCE_MISC_SHARED;
        std::vector<std::uint32_t> pixels(width*height,expectedPixel);
        D3D11_SUBRESOURCE_DATA data{};
        data.pSysMem=pixels.data();data.SysMemPitch=width*4;
        hr=d11->CreateTexture2D(&desc,&data,&tex11);
        if(FAILED(hr)) break;
        hr=tex11->QueryInterface(__uuidof(IDXGIResource),
            reinterpret_cast<void**>(&dxgi));
        if(FAILED(hr)) break;
        HANDLE legacyHandle=nullptr;
        hr=dxgi->GetSharedHandle(&legacyHandle);
        if(FAILED(hr) || !legacyHandle) break;
        HANDLE openingHandle=legacyHandle;
        hr=dev9->CreateTexture(width,height,1,D3DUSAGE_RENDERTARGET,
            D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&tex9,&openingHandle);
        if(FAILED(hr)) break;
        ctx->Flush();
        hr=tex9->GetSurfaceLevel(0,&surface);
        if(FAILED(hr)) break;
        hr=dev9->CreateOffscreenPlainSurface(width,height,
            D3DFMT_A8R8G8B8,D3DPOOL_SYSTEMMEM,&readback,nullptr);
        if(FAILED(hr)) break;
        hr=dev9->GetRenderTargetData(surface,readback);
        if(FAILED(hr)) break;
        D3DLOCKED_RECT locked{};
        hr=readback->LockRect(&locked,nullptr,D3DLOCK_READONLY);
        if(FAILED(hr)) break;
        auto pixel=*reinterpret_cast<const std::uint32_t*>(
            static_cast<const std::uint8_t*>(locked.pBits)+
            (height/2)*locked.Pitch+(width/2)*4);
        readback->UnlockRect();
        std::cout<<"D3D11 publisher -> D3D9Ex received pixel=0x"
                 <<std::hex<<pixel<<std::dec<<"\n";
        if(pixel!=expectedPixel){result=3;break;}
        if(hook && !TestHookGpuOverlay(dev9,legacyHandle,width,height,hook)){
            result=4;break;
        }
        result=0;
    }while(false);
    if(result==1)std::cerr<<"D3D11->D3D9Ex legacy share failed hr=0x"
                          <<std::hex<<hr<<std::dec<<"\n";
    Release(readback);Release(surface);Release(tex9);Release(dxgi);
    Release(tex11);Release(ctx);Release(d11);
    return result;
}

int Test(D3DFORMAT fmt, DXGI_FORMAT expectedFormat,
    IDirect3DDevice9Ex* dev9, IDXGIAdapter* adapter, HMODULE hook)
{
    constexpr UINT width=32, height=24;
    IDirect3DTexture9* tex9=nullptr;
    HANDLE legacyHandle=nullptr;
    HRESULT hr=dev9->CreateTexture(width,height,1,D3DUSAGE_RENDERTARGET,
        fmt,D3DPOOL_DEFAULT,&tex9,&legacyHandle);
    if (FAILED(hr) || !tex9 || !legacyHandle) {
        std::cout<<"Create D3D9Ex legacy share failed hr=0x"<<std::hex<<hr<<std::dec<<"\n";
        Release(tex9);return 1;
    }
    ID3D11Device* dev11=nullptr;ID3D11DeviceContext* ctx=nullptr;
    ID3D11Texture2D* shared11=nullptr;ID3D11Texture2D* source=nullptr;
    ID3D11Query* done=nullptr;
    IDirect3DSurface9* surface=nullptr;
    IDirect3DSurface9* readback=nullptr;
    int result=2;
    do {
        hr=D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,
            &dev11,nullptr,&ctx);
        if (FAILED(hr)) break;
        hr=dev11->OpenSharedResource(legacyHandle,__uuidof(ID3D11Texture2D),
            reinterpret_cast<void**>(&shared11));
        if (FAILED(hr)) break;
        D3D11_TEXTURE2D_DESC info{};
        shared11->GetDesc(&info);
        if(info.Format!=expectedFormat || info.Width!=width ||
            info.Height!=height || info.MipLevels!=1 || info.ArraySize!=1 ||
            info.SampleDesc.Count!=1){ hr=E_INVALIDARG; break; }
        D3D11_TEXTURE2D_DESC sourceInfo=info;
        sourceInfo.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        sourceInfo.MiscFlags=0;
        sourceInfo.CPUAccessFlags=0;
        sourceInfo.Usage=D3D11_USAGE_DEFAULT;
        constexpr std::uint32_t expectedPixel=0xFF123456u;
        std::vector<std::uint32_t> pixels(width*height,expectedPixel);
        D3D11_SUBRESOURCE_DATA src{};
        src.pSysMem=pixels.data();
        src.SysMemPitch=width*sizeof(std::uint32_t);
        hr=dev11->CreateTexture2D(&sourceInfo,&src,&source);
        if(FAILED(hr)) break;
        ctx->CopyResource(shared11,source);
        D3D11_QUERY_DESC query{};
        query.Query=D3D11_QUERY_EVENT;
        hr=dev11->CreateQuery(&query,&done);
        if(FAILED(hr)) break;
        ctx->End(done);
        ctx->Flush();
        const ULONGLONG limit=GetTickCount64()+4000;
        while((hr=ctx->GetData(done,nullptr,0,0))==S_FALSE &&
            GetTickCount64()<limit) Sleep(1);
        if(hr!=S_OK){ hr=E_FAIL;break; }
        hr=tex9->GetSurfaceLevel(0,&surface);
        if(FAILED(hr)) break;
        hr=dev9->CreateOffscreenPlainSurface(width,height,fmt,
            D3DPOOL_SYSTEMMEM,&readback,nullptr);
        if(FAILED(hr)) break;
        hr=dev9->GetRenderTargetData(surface,readback);
        if(FAILED(hr)) break;
        D3DLOCKED_RECT locked{};
        hr=readback->LockRect(&locked,nullptr,D3DLOCK_READONLY);
        if(FAILED(hr)) break;
        const auto actual=*reinterpret_cast<const std::uint32_t*>(
            static_cast<const std::uint8_t*>(locked.pBits)+
            (height/2)*locked.Pitch+(width/2)*4);
        readback->UnlockRect();
        std::cout<<"GPU roundtrip expected=0x"<<std::hex<<expectedPixel
                 <<" actual=0x"<<actual<<std::dec<<"\n";
        result=actual==expectedPixel?0:3;
        if(result==0 && hook && fmt==D3DFMT_A8R8G8B8 &&
           !TestHookGpuOverlay(dev9,legacyHandle,width,height,hook)) {
            std::cerr << "D3D9Ex Hook GPU overlay composition failed\n";
            result=4;
        }
    }while(false);
    if(result==2) std::cout<<"GPU interop failed hr=0x"<<std::hex<<hr<<std::dec<<"\n";
    Release(readback);Release(surface);Release(done);Release(source);
    Release(shared11);Release(ctx);Release(dev11);Release(tex9);
    // legacyHandle belongs to shared resource, not the NT handle table.
    return result;
}
}
int wmain(int argc, wchar_t **argv)
{
    HINSTANCE instance=GetModuleHandleW(nullptr);
    WNDCLASSW wc{};wc.hInstance=instance;wc.lpfnWndProc=WindowProc;
    wc.lpszClassName=L"DulukaD3D9ExSharedTextureProbe";
    if(!RegisterClassW(&wc)) return 10;
    HWND window=CreateWindowExW(0,wc.lpszClassName,L"GPU interop",
        WS_OVERLAPPED,0,0,80,80,nullptr,nullptr,instance,nullptr);
    if(!window) return 11;
    IDirect3D9Ex* d3d=nullptr;
    HRESULT hr=Direct3DCreate9Ex(D3D_SDK_VERSION,&d3d);
    if(FAILED(hr)||!d3d){DestroyWindow(window);return 12;}
    D3DPRESENT_PARAMETERS pp{};
    pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;
    pp.BackBufferWidth=64;pp.BackBufferHeight=64;
    pp.BackBufferCount=1;pp.hDeviceWindow=window;
    pp.BackBufferFormat=D3DFMT_UNKNOWN;
    IDirect3DDevice9Ex* dev9=nullptr;
    hr=d3d->CreateDeviceEx(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,window,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING,&pp,nullptr,&dev9);
    if(FAILED(hr)||!dev9) {Release(d3d);DestroyWindow(window);return 13;}
    LUID luid{};
    hr=d3d->GetAdapterLUID(D3DADAPTER_DEFAULT,&luid);
    IDXGIFactory1* factory=nullptr;
    if(SUCCEEDED(hr))hr=CreateDXGIFactory1(__uuidof(IDXGIFactory1),
        reinterpret_cast<void**>(&factory));
    IDXGIAdapter1* matching=nullptr;
    if(SUCCEEDED(hr)) for(UINT i=0; ;++i) {
        IDXGIAdapter1* candidate=nullptr;
        if(factory->EnumAdapters1(i,&candidate)==DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        if(SUCCEEDED(candidate->GetDesc1(&desc)) &&
           desc.AdapterLuid.HighPart==luid.HighPart &&
           desc.AdapterLuid.LowPart==luid.LowPart){
            matching=candidate;break;
        }
        Release(candidate);
    }
    if(!matching) {
        std::cerr<<"No matching DXGI adapter for D3D9Ex LUID\n";
        Release(factory);Release(dev9);Release(d3d);
        DestroyWindow(window);return 14;
    }
    HMODULE hook = argc == 2 ? LoadLibraryW(argv[1]) : nullptr;
    if (argc != 1 && (argc != 2 || !hook)) {
        std::cerr << "GPU fixture Hook DLL argument could not load\n";
        Release(matching);Release(factory);Release(dev9);Release(d3d);
        DestroyWindow(window);return 15;
    }
    std::cout<<"D3D9Ex <-> D3D11 same-adapter legacy shared handle test\n";
    std::cout<<"A8R8G8B8 (DXGI B8G8R8A8):\n";
    const int a=Test(D3DFMT_A8R8G8B8,DXGI_FORMAT_B8G8R8A8_UNORM,
        dev9,matching,hook);
    std::cout<<"A8B8G8R8 (DXGI R8G8B8A8):\n";
    const int b=Test(D3DFMT_A8B8G8R8,DXGI_FORMAT_R8G8B8A8_UNORM,
        dev9,matching,hook);
    std::cout<<"D3D11 publisher -> D3D9Ex Hook:"<<std::endl;
    const int c=TestD3D11Publisher(dev9,matching,hook);
    // No dynamic FreeLibrary: the current Hook is not certified safe to unload.
    // This owned fixture exits immediately and Windows reclaims its resources.
    Release(matching);Release(factory);Release(dev9);Release(d3d);
    DestroyWindow(window);
    UnregisterClassW(wc.lpszClassName,instance);
    std::cout<<"FORMAT_RESULT BGRA="<<a<<" RGBA="<<b<<"\n";
    std::cout<<"REVERSE_RESULT="<<c<<std::endl;
    return (a==0 || b==0) && c==0 ? 0 : 20;
}
