// Synthetic cross-process Host-mailbox producer + real Duluka Hook receiver.
// Private test mapping; never touches OSC Host production mailbox.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d9.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <cstdint>
#include <iostream>
#include <functional>
#include <string>
#include <vector>
#include <cstring>
#include "../include/d3d9ex_gpu_mailbox.h"
#include "../../../../Overlay On-Screen Control/NVIDIA OSC/src/OscFramePublisher/OscD3D9ExFramePublisher/OscD3D9ExFramePublisher.h"
#include <algorithm>
#pragma comment(lib,"d3d9.lib")
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"dxgi.lib")

using duluka::d3d9ex_mailbox::Mailbox;
namespace {
template<typename T> void Drop(T*& v) { if(v){v->Release();v=nullptr;} }
LONG R32(volatile LONG* p) {return InterlockedCompareExchange(p,0,0);}
LONG64 R64(volatile LONG64* p) {return InterlockedCompareExchange64(p,0,0);}
LONG64 ToPacked(const LUID& l) {
    return static_cast<LONG64>(
        (static_cast<std::uint64_t>(
            static_cast<std::uint32_t>(l.HighPart)) << 32) | l.LowPart);
}
bool WaitUntil(const std::function<bool()>& check, DWORD millis=8000) {
    const auto limit=GetTickCount64()+millis;
    while(GetTickCount64()<limit){if(check())return true;Sleep(2);}
    return false;
}
LRESULT CALLBACK Proc(HWND w,UINT msg,WPARAM a,LPARAM b) {
    return DefWindowProcW(w,msg,a,b);
}
struct SharedGPU {
    ID3D11Texture2D* texture=nullptr;
    HANDLE legacy=nullptr; // never CloseHandle
    void Close(){Drop(texture);legacy=nullptr;}
};
bool MakeTexture(ID3D11Device* dev, ID3D11DeviceContext* ctx,
    UINT w,UINT h,std::uint32_t color,SharedGPU& dst) {
    D3D11_TEXTURE2D_DESC ds{};
    ds.Width=w;ds.Height=h;ds.MipLevels=1;ds.ArraySize=1;
    ds.Format=DXGI_FORMAT_B8G8R8A8_UNORM;ds.SampleDesc.Count=1;
    ds.Usage=D3D11_USAGE_DEFAULT;
    ds.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    ds.MiscFlags=D3D11_RESOURCE_MISC_SHARED;
    std::vector<std::uint32_t> pixels(w*h,color);
    D3D11_SUBRESOURCE_DATA sd{};
    sd.pSysMem=pixels.data();sd.SysMemPitch=w*4;
    if(FAILED(dev->CreateTexture2D(&ds,&sd,&dst.texture)))return false;
    IDXGIResource* dxgi=nullptr;
    HRESULT hr=dst.texture->QueryInterface(__uuidof(IDXGIResource),
        reinterpret_cast<void**>(&dxgi));
    if(SUCCEEDED(hr))hr=dxgi->GetSharedHandle(&dst.legacy);
    Drop(dxgi);
    if(FAILED(hr)||!dst.legacy)return false;
    ID3D11Query* query=nullptr;
    D3D11_QUERY_DESC q{};q.Query=D3D11_QUERY_EVENT;
    if(FAILED(dev->CreateQuery(&q,&query)))return false;
    ctx->End(query);ctx->Flush();
    const bool completed=WaitUntil([&](){
        return ctx->GetData(query,nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH)==S_OK;
    });
    Drop(query);
    return completed;
}
void Publish(Mailbox* w, LONG64 adapter, LONG64 gen,
    HANDLE handle,UINT width,UINT height) {
    InterlockedIncrement(&w->sequence);
    InterlockedExchange64(&w->targetAdapterLuid,adapter);
    InterlockedExchange64(&w->generation,gen);
    InterlockedExchange64(&w->readyGeneration,0);
    w->legacySharedHandle=static_cast<std::uint64_t>(
        reinterpret_cast<std::uintptr_t>(handle));
    w->width=width;w->height=height;
    w->format=duluka::d3d9ex_mailbox::Bgra8Format;
    MemoryBarrier();
    InterlockedIncrement(&w->sequence);
    // Keep the synthetic offer unready until the receiving process
    // explicitly confirms it ignored the pending generation.
    // This negative test does NOT represent actual Host query timing.
}
int Producer(const wchar_t* mapName) {
    HANDLE handle=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,mapName);
    if(!handle){std::cerr<<"producer cannot open private mailbox\n";return 20;}
    auto* wire=static_cast<Mailbox*>(
        MapViewOfFile(handle,FILE_MAP_ALL_ACCESS,0,0,sizeof(Mailbox)));
    if(!wire){CloseHandle(handle);return 21;}
    InterlockedExchange(&wire->producerPid,GetCurrentProcessId());
    const bool registered=WaitUntil([&](){
        return R32(&wire->consumerPid)>0 &&
            (R32(&wire->consumerCapabilities)&1)==1 &&
            R64(&wire->consumerAdapterLuid)!=0;
    });
    if(!registered){std::cerr<<"producer receiver handshake timeout\n";
        UnmapViewOfFile(wire);CloseHandle(handle);return 22;}
    const LONG64 luid=R64(&wire->consumerAdapterLuid);
    IDXGIFactory1* factory=nullptr;IDXGIAdapter1* adapter=nullptr;
    HRESULT hr=CreateDXGIFactory1(__uuidof(IDXGIFactory1),
        reinterpret_cast<void**>(&factory));
    if(SUCCEEDED(hr))for(UINT i=0;;++i){
        IDXGIAdapter1* next=nullptr;
        if(factory->EnumAdapters1(i,&next)==DXGI_ERROR_NOT_FOUND)break;
        DXGI_ADAPTER_DESC1 desc{};
        if(SUCCEEDED(next->GetDesc1(&desc)) &&
           ToPacked(desc.AdapterLuid)==luid){adapter=next;break;}
        Drop(next);
    }
    ID3D11Device* dev=nullptr;ID3D11DeviceContext* ctx=nullptr;
    if(!adapter||FAILED(D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
        D3D11_SDK_VERSION,&dev,nullptr,&ctx))){
        std::cerr<<"producer cannot create matching adapter\n";
        Drop(adapter);Drop(factory);UnmapViewOfFile(wire);CloseHandle(handle);
        return 23;
    }
    constexpr UINT W=32,H=24;
    SharedGPU first{},second{};
    int result=0;
    do {
        if(!MakeTexture(dev,ctx,W,H,0xFF123456u,first)) {result=24;break;}
        std::cout<<"PRODUCER_LEGACY_HANDLE_1=0x"<<std::hex
            <<reinterpret_cast<std::uintptr_t>(first.legacy)
            <<std::dec<<"\n";
        Publish(wire,luid,1,first.legacy,W,H);
        if(!WaitUntil([&](){return wire->reserved==11;})){result=29;break;}
        InterlockedExchange64(&wire->readyGeneration,1);
        if(!WaitUntil([&](){return R64(&wire->stagedGeneration)==1 &&
            R64(&wire->committedGeneration)==1 &&
            wire->reserved==1;})){result=25;break;}
        if(!MakeTexture(dev,ctx,W,H,0xFF789ABCu,second)){result=26;break;}
        std::cout<<"PRODUCER_LEGACY_HANDLE_2=0x"<<std::hex
            <<reinterpret_cast<std::uintptr_t>(second.legacy)
            <<std::dec<<"\n";
        // Offer a deliberately wrong adapter LUID first. The receiver
        // must NOT Stage or ACK an inaccessible/cross-adapter texture.
        Publish(wire,luid ^ 1,2,second.legacy,W,H);
        InterlockedExchange64(&wire->readyGeneration,2);
        if(!WaitUntil([&](){return wire->reserved==13;})){result=29;break;}
        Publish(wire,luid,2,second.legacy,W,H);
        if(!WaitUntil([&](){return wire->reserved==12;})){result=29;break;}
        InterlockedExchange64(&wire->readyGeneration,2);
        if(!WaitUntil([&](){return R64(&wire->stagedGeneration)==2 &&
            R64(&wire->committedGeneration)==2 &&
            R64(&wire->releasedGeneration)>=1 &&
            wire->reserved==2;})){result=27;break;}
        first.Close();
        Publish(wire,luid,0,nullptr,0,0);
        if(!WaitUntil([&](){return R64(&wire->releasedGeneration)>=2;})){
            result=28;break;
        }
        std::cout<<"PRODUCER_ACK_STAGED_COMMITTED_RELEASED=PASS\n";
    }while(false);
    second.Close();first.Close();Drop(ctx);Drop(dev);
    Drop(adapter);Drop(factory);
    InterlockedExchange(&wire->producerPid,0);
    UnmapViewOfFile(wire);CloseHandle(handle);
    if(result)std::cerr<<"PRODUCER_FAIL="<<result<<"\n";
    return result;
}
// Compiles the REAL, unmodified current Host module as a second source.
// It has its own test-only mapping-name environment override; the live
// OSC Host mailbox is never opened by this fixture.
int HostProducer(const wchar_t* mapName, bool crashAfterFirst=false,
    bool gpuSource=false) {
    namespace hp=osc::d3d9ex_frame_publisher;
    SetEnvironmentVariableW(
        L"NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME",mapName);
    SetEnvironmentVariableA("OSC_DX9EX_HOST_PREVIEW","1");
    hp::Poll();
    HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,mapName);
    if(!mapping){std::cerr<<"Host source failed to create isolated mapping\n";return 60;}
    auto* wire=static_cast<Mailbox*>(
        MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Mailbox)));
    if(!wire){CloseHandle(mapping);return 61;}
    int result=0;
    IDXGIFactory1* factory=nullptr;IDXGIAdapter1* adapter=nullptr;
    ID3D11Device* dev=nullptr;ID3D11DeviceContext* ctx=nullptr;
    SharedGPU gpuSources[2]{}; // lifetime spans host completion events
    do {
        if(R32(&wire->producerPid)!=static_cast<LONG>(GetCurrentProcessId())){
            result=62;break;
        }
        if(!WaitUntil([&](){
            return R32(&wire->consumerPid)>0 &&
                (R32(&wire->consumerCapabilities)&1)==1 &&
                R64(&wire->consumerAdapterLuid)!=0;
        })){result=63;break;}
        const LONG gamePid=R32(&wire->consumerPid);
        const LONG64 packed=R64(&wire->consumerAdapterLuid);
        if(FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1),
            reinterpret_cast<void**>(&factory)))){result=64;break;}
        LUID gameLuid{};
        gameLuid.LowPart=static_cast<DWORD>(packed);
        gameLuid.HighPart=static_cast<LONG>(packed>>32);
        for(UINT i=0;;++i){
            IDXGIAdapter1* next=nullptr;
            if(factory->EnumAdapters1(i,&next)==DXGI_ERROR_NOT_FOUND)break;
            DXGI_ADAPTER_DESC1 description{};
            if(SUCCEEDED(next->GetDesc1(&description)) &&
               ToPacked(description.AdapterLuid)==packed){
                adapter=next;break;
            }
            Drop(next);
        }
        if(!adapter || FAILED(D3D11CreateDevice(adapter,
            D3D_DRIVER_TYPE_UNKNOWN,nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
            D3D11_SDK_VERSION,&dev,nullptr,&ctx))){result=65;break;}
        constexpr UINT W=32,H=24;
        std::vector<std::uint32_t> image(W*H);
        for(LONG64 gen=1;gen<=2;++gen){
            if(!WaitUntil([&](){
                hp::Poll();
                return hp::CanPublish(static_cast<DWORD>(gamePid),gameLuid);
            })){result=66;break;}
            std::fill(image.begin(),image.end(),
                gen==1?0xFF123456u:0xFF789ABCu);
            bool published=false;
            if(gpuSource) {
                if(!MakeTexture(dev,ctx,W,H,image[0],
                    gpuSources[gen-1])){result=74;break;}
                published=hp::TryPublishGpuTexture(
                    dev,ctx,gpuSources[gen-1].texture,gameLuid,
                    static_cast<DWORD>(gamePid));
            } else {
                published=hp::TryPublish(dev,ctx,
                    reinterpret_cast<const std::uint8_t*>(image.data()),
                    W*4,W,H,gameLuid,static_cast<DWORD>(gamePid));
            }
            if(!published){result=67;break;}
            if(!WaitUntil([&](){
                hp::Poll();
                return R64(&wire->readyGeneration)==gen &&
                    R64(&wire->stagedGeneration)==gen &&
                    R64(&wire->committedGeneration)==gen &&
                    R32(reinterpret_cast<volatile LONG*>(&wire->reserved))==gen &&
                    (gen==1 || R64(&wire->releasedGeneration)>=1);
            })){result=68;break;}
            std::cout<<"ACTUAL_HOST_PUBLISHER_GEN="<<gen
                <<" ACK_STAGED_COMMITTED=PASS\n";
            if(crashAfterFirst){
                std::cout<<"ACTUAL_HOST_SIMULATED_UNEXPECTED_EXIT=PASS\n";
                std::cout.flush();
                ExitProcess(0); // owned child: no Host Stop()/Release ACK
            }
        }
        if(result)break;
        // Use the ACTUAL Host public graceful WithDraw path: it must
        // retain the active producer texture until Hook Release ACK.
        if(!WaitUntil([&](){
            hp::Poll();
            return hp::Withdraw();
        })){result=69;break;}
        if(!WaitUntil([&](){
            hp::Poll();
            return R64(&wire->releasedGeneration)>=2;
        })){result=69;break;}
        std::cout<<"ACTUAL_HOST_PUBLISHER_SAFE_RELEASE_ACK=PASS\n";
        if(gpuSource)
            std::cout<<"ACTUAL_HOST_GPU_TO_GPU_TEXTURE_COPY=PASS\n";
    }while(false);
    hp::Stop();
    for(auto &source:gpuSources)source.Close();
    Drop(ctx);Drop(dev);Drop(adapter);Drop(factory);
    UnmapViewOfFile(wire);CloseHandle(mapping);
    if(result)std::cerr<<"ACTUAL_HOST_PUBLISHER_TEST_FAIL="<<result<<"\n";
    return result;
}

int Consumer(const wchar_t* hookDll, const wchar_t* producerExe,
    bool actualHostPublisher=false, bool simulateRestart=false,
    bool gpuSource=false, bool automaticExPump=false) {
    const DWORD self=GetCurrentProcessId();
    wchar_t name[128]{};
    swprintf_s(name,L"Local\\NVIDIAOSC.Frame.D3D9Ex.MailboxTest.%lu",self);
    HANDLE mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,
        PAGE_READWRITE,0,sizeof(Mailbox),name);
    if(!mapping || GetLastError()==ERROR_ALREADY_EXISTS)return 30;
    auto* wire=static_cast<Mailbox*>(
        MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Mailbox)));
    if(!wire){CloseHandle(mapping);return 31;}
    std::memset(wire,0,sizeof(Mailbox));
    wire->magic=duluka::d3d9ex_mailbox::Magic;
    wire->version=duluka::d3d9ex_mailbox::Version;
    SetEnvironmentVariableW(
        duluka::d3d9ex_mailbox::NameEnvironment,name);
    SetEnvironmentVariableA(
        "NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW","1");
    HINSTANCE inst=GetModuleHandleW(nullptr);
    WNDCLASSW wc{};wc.hInstance=inst;wc.lpfnWndProc=Proc;
    wc.lpszClassName=L"DulukaGPUReceiverIPCFixture";
    if(!RegisterClassW(&wc)){UnmapViewOfFile(wire);CloseHandle(mapping);return 32;}
    HWND window=CreateWindowExW(0,wc.lpszClassName,L"IPC GPU test",
        WS_OVERLAPPED,0,0,80,80,nullptr,nullptr,inst,nullptr);
    IDirect3D9Ex* d3d=nullptr;
    HRESULT hr=Direct3DCreate9Ex(D3D_SDK_VERSION,&d3d);
    D3DPRESENT_PARAMETERS pp{};
    pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;
    pp.BackBufferWidth=64;pp.BackBufferHeight=64;
    pp.hDeviceWindow=window;pp.BackBufferCount=1;
    IDirect3DDevice9Ex* game=nullptr;
    if(SUCCEEDED(hr))hr=d3d->CreateDeviceEx(D3DADAPTER_DEFAULT,
        D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,
        &pp,nullptr,&game);
    if(FAILED(hr)||!game)return 33;
    if(!automaticExPump) {
        // Existing manual IPC regressions prove the new hook is default-off.
        SetEnvironmentVariableA(
            "NVIDIA_OSC_ADAPTER_D3D9EX_ENDSCENE_AUTO_PUMP_PREVIEW",nullptr);
    }
    if(automaticExPump) {
        // Only this owned fixture enables the EndScene GPU receiver.
        // Production/game processes retain default-off behavior.
        SetEnvironmentVariableW(L"DULUKA_D3D9_FORCE_LATE_ATTACH",L"1");
        SetEnvironmentVariableA(
            "NVIDIA_OSC_ADAPTER_D3D9EX_ENDSCENE_AUTO_PUMP_PREVIEW","1");
    }
    HMODULE hook=LoadLibraryW(hookDll);
    if(!hook)return 34;
    using PumpFn=BOOL(WINAPI *)(IDirect3DDevice9Ex*);
    using RenderFn=BOOL(WINAPI *)(IDirect3DDevice9*);
    using UnbindFn=BOOL(WINAPI *)(IDirect3DDevice9Ex*);
    auto pump=reinterpret_cast<PumpFn>(GetProcAddress(
        hook,"NvOscAdapterPumpD3D9ExGpuMailbox"));
    auto render=reinterpret_cast<RenderFn>(GetProcAddress(
        hook,"NvOscAdapterRenderD3D9ForTest"));
    auto unbind=reinterpret_cast<UnbindFn>(GetProcAddress(
        hook,"NvOscAdapterUnbindD3D9ExGpuFrame"));
    if(!pump||!render||!unbind)return 35;
    using AutoStatusFn=BOOL(WINAPI *)(BOOL*,std::uint64_t*,std::uint64_t*);
    using HookStatusFn=BOOL(WINAPI *)(std::uint64_t*,std::uint64_t*,BOOL*);
    auto autoStatus=reinterpret_cast<AutoStatusFn>(GetProcAddress(
        hook,"NvOscAdapterGetD3D9ExAutoPumpStatus"));
    if(automaticExPump) {
        auto hookStatus=reinterpret_cast<HookStatusFn>(GetProcAddress(
            hook,"NvOscAdapterGetD3D9Status"));
        const bool ready=hookStatus && autoStatus && WaitUntil([&](){
            std::uint64_t presents=0,draws=0,calls=0,commits=0;
            BOOL hooked=FALSE,enabled=FALSE;
            return hookStatus(&presents,&draws,&hooked) && hooked &&
                autoStatus(&enabled,&calls,&commits) && enabled;
        },5000);
        if(!ready){
            std::cerr<<"D3D9Ex EndScene preview detour did not initialize\n";
            return 75;
        }
        SetEnvironmentVariableW(L"DULUKA_D3D9_FORCE_LATE_ATTACH",nullptr);
    }
    if(!automaticExPump && autoStatus) {
        BOOL enabled=TRUE;
        std::uint64_t calls=1,commits=1;
        if(!autoStatus(&enabled,&calls,&commits) || enabled || calls || commits)
            return 77; // no EndScene interception without BOTH preview flags
    }
    auto devicePump=[&]()->bool {
        if(!automaticExPump) return !!pump(game);
        // Real D3D9Ex device owner-thread scene boundary, not Present.
        return SUCCEEDED(game->BeginScene()) &&
            SUCCEEDED(game->EndScene());
    };
    wchar_t executable[MAX_PATH]{};
    if(!GetModuleFileNameW(nullptr,executable,MAX_PATH))return 36;
    std::wstring cmd=L"\""+std::wstring(producerExe)+
        (actualHostPublisher ?
            (simulateRestart ? L"\" --host-crash \"" :
            (gpuSource ? L"\" --host-gpu-source \"" :
                L"\" --host-producer \"")) : L"\" --producer \"")+
        name+L"\"";
    STARTUPINFOW si{};si.cb=sizeof(si);
    PROCESS_INFORMATION pi{};
    if(!CreateProcessW(producerExe,cmd.data(),nullptr,nullptr,
        TRUE,0,nullptr,nullptr,&si,&pi)) {
        std::cerr<<"CreateProcess failed="<<GetLastError()<<"\n";return 37;
    }
    IDirect3DSurface9 *back=nullptr,*sys=nullptr;
    if(FAILED(game->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back)) ||
       FAILED(game->CreateOffscreenPlainSurface(64,64,D3DFMT_X8R8G8B8,
           D3DPOOL_SYSTEMMEM,&sys,nullptr)))return 38;
    auto pixel=[&]() -> std::uint32_t {
        if(FAILED(game->Clear(0,nullptr,D3DCLEAR_TARGET,
            0xFF000000,1.0f,0)) || !render(game) ||
            FAILED(game->GetRenderTargetData(back,sys)))
            return 0xffffffffu;
        D3DLOCKED_RECT locked{};
        if(FAILED(sys->LockRect(&locked,nullptr,D3DLOCK_READONLY)))
            return 0xffffffffu;
        const auto value=*reinterpret_cast<const std::uint32_t*>(
            static_cast<const std::uint8_t*>(locked.pBits)+32*locked.Pitch+32*4);
        sys->UnlockRect();
        return value & 0x00ffffff;
    };
    LUID consumerAdapter{};
    d3d->GetAdapterLUID(D3DADAPTER_DEFAULT,&consumerAdapter);
    const LONG64 correctLuid=ToPacked(consumerAdapter);
    auto ConsumeFrames = [&](int first, int last)->int {
    int result=0;
    for(int expected=first;expected<=last;++expected) {
        const auto deadline=GetTickCount64()+8000;
        bool arrived=false, observedUnready=actualHostPublisher;
        bool observedWrongLuid=actualHostPublisher;
        while(GetTickCount64()<deadline) {
            devicePump(); // same device thread; auto mode uses EndScene, never explicit Pump
            if(!actualHostPublisher && expected==2 && !observedWrongLuid &&
                R64(&wire->generation)==2 &&
                R64(&wire->targetAdapterLuid)!=correctLuid &&
                R64(&wire->readyGeneration)==2) {
                if(R64(&wire->stagedGeneration)==2 ||
                   R64(&wire->committedGeneration)==2 ||
                   pixel()!=0x00123456u){result=46;break;}
                InterlockedExchange(reinterpret_cast<volatile LONG*>(
                    &wire->reserved),13);
                observedWrongLuid=true;
            }
            if (!observedUnready &&
                (expected==1 || observedWrongLuid) &&
                R64(&wire->generation)==expected &&
                R64(&wire->targetAdapterLuid)==correctLuid &&
                R64(&wire->readyGeneration)==0 &&
                R32(&wire->producerPid)>0) {
                const bool earlyAck =
                    R64(&wire->stagedGeneration)==expected ||
                    R64(&wire->committedGeneration)==expected;
                const bool oldTextureVisible = expected==1 ||
                    pixel()==0x00123456u;
                if (earlyAck || !oldTextureVisible) {
                    result=45;break;
                }
                // Test-only out-of-band marker in a private mailbox.
                InterlockedExchange(reinterpret_cast<volatile LONG*>(
                    &wire->reserved),expected==1?11:12);
                observedUnready=true;
            }
            if(observedUnready &&
                R64(&wire->readyGeneration)==expected &&
                R64(&wire->stagedGeneration)==expected &&
                R64(&wire->committedGeneration)==expected) {
                const std::uint32_t rgb=pixel();
                if(rgb == (expected==1?0x00123456u:0x00789abcu)){
                    arrived=true;
                    InterlockedExchange(reinterpret_cast<volatile LONG*>(
                        &wire->reserved),expected);
                    break;
                }
            }
            Sleep(2);
        }
        if(!arrived){if(result==0)result=40+expected;break;}
    }
    return result;
    };
    int result=ConsumeFrames(1,simulateRestart?1:2);
    if(simulateRestart && result==0) {
        if(WaitForSingleObject(pi.hProcess,6000)!=WAIT_OBJECT_0)
            result=70;
        if(result==0) {
            // Host process vanished WITHOUT Stop(), while game held a
            // D3D9Ex texture opened from its now-dead GPU producer.
            using StatusFn=BOOL(WINAPI*)(std::uint64_t*,BOOL*,BOOL*,std::uint64_t*);
            auto status=reinterpret_cast<StatusFn>(GetProcAddress(
                hook,"NvOscAdapterGetD3D9AdapterStatus"));
            if(!status) result=71;
            else if(!WaitUntil([&](){
                devicePump(); // retirement on legal game thread, auto = EndScene
                std::uint64_t luid=0,skipped=0;
                BOOL isEx=FALSE,bound=TRUE;
                return status(&luid,&isEx,&bound,&skipped) && !bound &&
                    R32(&wire->consumerPid)==0;
            })) result=72;
            else std::cout<<"HOST_DIED_HOOK_GPU_UNBOUND_CPU_FALLBACK=PASS\n";
        }
        if(!result) {
            CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
            pi={};
            // Fixture-specific marker: reserved is NOT part of Host
            // protocol. Prevent old pixel ACK from satisfying restart.
            InterlockedExchange(reinterpret_cast<volatile LONG*>(
                &wire->reserved),0);
            std::wstring fresh=L"\""+std::wstring(producerExe)+
                L"\" --host-producer \""+name+L"\"";
            STARTUPINFOW si2{};si2.cb=sizeof(si2);
            if(!CreateProcessW(producerExe,fresh.data(),nullptr,nullptr,
                TRUE,0,nullptr,nullptr,&si2,&pi)) result=73;
            else result=ConsumeFrames(1,2);
            if(!result)
                std::cout<<"HOST_RESTART_SAME_GENERATION_RESYNC=PASS\n";
        }
    }
    if(result==0){
        if(!WaitUntil([&](){devicePump();
            return R64(&wire->generation)==0 &&
                R64(&wire->releasedGeneration)>=2;}))result=43;
    }
    if(result==0 && automaticExPump) {
        std::uint64_t calls=0,commits=0;
        BOOL enabled=FALSE;
        if(!autoStatus(&enabled,&calls,&commits) || !enabled ||
            calls < 2 || commits < 2) result=76;
        else std::cout<<"DX9EX_ENDSCENE_GPU_AUTOPUMP_PASS calls="
            <<calls<<" commits="<<commits<<"\n";
    }
    if(result==0){
        DWORD childExit=STILL_ACTIVE;
        WaitForSingleObject(pi.hProcess,6000);
        GetExitCodeProcess(pi.hProcess,&childExit);
        if(childExit!=0){result=44;std::cerr<<"Child exit="<<childExit<<"\n";}
        else std::cout<<(actualHostPublisher?
            "ACTUAL_HOST_SOURCE_MAILBOX_IPC_PIXELS=PASS\n":
            "IPC_MAILBOX_STAGE_COMMIT_RELEASE_AND_PIXELS=PASS\n");
    }
    if(result){
        // This fixture owns the child, not an external process.
        TerminateProcess(pi.hProcess,99);
        WaitForSingleObject(pi.hProcess,1000);
    }
    CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
    unbind(game);
    Drop(sys);Drop(back);Drop(game);Drop(d3d);
    // Do not FreeLibrary dynamically: Hook native unload still unverified.
    DestroyWindow(window);UnregisterClassW(wc.lpszClassName,inst);
    UnmapViewOfFile(wire);CloseHandle(mapping);
    return result;
}
}
int wmain(int argc,wchar_t** argv){
    if(argc==3 && wcscmp(argv[1],L"--producer")==0)
        return Producer(argv[2]);
    if(argc==3 && wcscmp(argv[1],L"--host-producer")==0)
        return HostProducer(argv[2]);
    if(argc==3 && wcscmp(argv[1],L"--host-crash")==0)
        return HostProducer(argv[2],true);
    if(argc==3 && wcscmp(argv[1],L"--host-gpu-source")==0)
        return HostProducer(argv[2],false,true);
    if(argc==4 && wcscmp(argv[3],L"--host")==0)
        return Consumer(argv[1],argv[2],true);
    if(argc==4 && wcscmp(argv[3],L"--gpu-source")==0)
        return Consumer(argv[1],argv[2],true,false,true);
    if(argc==4 && wcscmp(argv[3],L"--gpu-auto")==0)
        return Consumer(argv[1],argv[2],true,false,true,true);
    if(argc==4 && wcscmp(argv[3],L"--gpu-auto-recovery")==0)
        return Consumer(argv[1],argv[2],true,true,false,true);
    if(argc==4 && wcscmp(argv[3],L"--recovery")==0)
        return Consumer(argv[1],argv[2],true,true);
    if(argc==3)return Consumer(argv[1],argv[2]);
    std::cerr<<"Usage: <hook.dll> <producer-test.exe> or --producer name\n";
    return 2;
}
