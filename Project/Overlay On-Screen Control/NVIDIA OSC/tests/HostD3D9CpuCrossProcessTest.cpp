// Real OSC Host DX9 CPU publisher -> real runtime Duluka Hook D3D9 render
// in separate Windows processes. Private mapping; no game or deployment.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d9.h>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>
#include "../src/OscFramePublisher/OscD3D9FramePublisher/OscD3D9FramePublisher.h"
#include "../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"
#pragma comment(lib,"d3d9.lib")
void Probe(const char* msg) { std::fprintf(stderr,"[dx9-test] %s\n",msg); std::fflush(stderr); }
void Log(const char* message) { std::fprintf(stderr,"[host] %s\n",message); }
using namespace osc::d3d9_frame_publisher;
static bool AlwaysVisible() { return true; }
template <class T> void Drop(T*& p) { if(p) {p->Release();p=nullptr;} }
static bool CheckPixel(IDirect3DDevice9* dev,UINT x,UINT y,int red,int green,int blue,int tol) {
    IDirect3DSurface9 *back=nullptr,*stage=nullptr;
    D3DSURFACE_DESC info{};
    HRESULT hr=dev->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back);
    if(SUCCEEDED(hr)) hr=back->GetDesc(&info);
    if(SUCCEEDED(hr)) hr=dev->CreateOffscreenPlainSurface(
        info.Width,info.Height,info.Format,D3DPOOL_SYSTEMMEM,&stage,nullptr);
    if(SUCCEEDED(hr)) hr=dev->GetRenderTargetData(back,stage);
    D3DLOCKED_RECT lock{};
    if(SUCCEEDED(hr)) hr=stage->LockRect(&lock,nullptr,D3DLOCK_READONLY);
    bool ok=false;
    if(SUCCEEDED(hr)) {
        auto* p=static_cast<const std::uint8_t*>(lock.pBits)+
            static_cast<size_t>(y)*lock.Pitch+x*4;
        const int gotR=p[2],gotG=p[1],gotB=p[0];
        ok=abs(gotR-red)<=tol && abs(gotG-green)<=tol &&
            abs(gotB-blue)<=tol;
        if(!ok)std::printf("PIXEL_FAIL %u,%u got=%d,%d,%d want=%d,%d,%d\n",
            x,y,gotR,gotG,gotB,red,green,blue);
        stage->UnlockRect();
    } else std::printf("PIXEL_READ_FAIL=0x%08lx\n",
        static_cast<unsigned long>(hr));
    Drop(stage);Drop(back);
    return ok;
}
static int Producer(const wchar_t* mapName,const wchar_t* readyName,
                    const wchar_t* doneName) {
    if(!SetEnvironmentVariableW(L"NVIDIA_OSC_HOST_D3D9_FRAME_MAPPING_NAME",
                                mapName))return 11;
    HANDLE ready=OpenEventW(EVENT_MODIFY_STATE,FALSE,readyName);
    HANDLE done=OpenEventW(SYNCHRONIZE,FALSE,doneName);
    if(!ready||!done) return 12;
    constexpr UINT w=640,h=480;
    std::vector<std::uint8_t> bgra(static_cast<size_t>(w)*h*4,0);
    auto paint=[&](UINT x,UINT y, std::uint8_t b,std::uint8_t g,
                   std::uint8_t r,std::uint8_t a){
        const size_t i=(static_cast<size_t>(y)*w+x)*4;
        bgra[i]=b;bgra[i+1]=g;bgra[i+2]=r;bgra[i+3]=a;
    };
    // Real CEF accelerated texture arrives bottom-up for the D3D9 CPU
    // bridge. The Hook flips V when compositing: memory's TOP is screen BOTTOM.
    // Build a synthetic bottom-up image, not an upright bitmap fixture.
    for(UINT y=0;y<8;y++)for(UINT x=0;x<w;x++)paint(x,y,0,0,255,255);
    for(UINT y=h-8;y<h;y++)for(UINT x=0;x<w;x++)paint(x,y,0,255,0,255);
    for(UINT y=h/2-8;y<h/2+8;y++)
        for(UINT x=w/2-8;x<w/2+8;x++)paint(x,y,0,0,128,128);
    SetVisibilityProvider(&AlwaysVisible);
    const bool published=PublishD3D9FramePixels(
        bgra.data(),w*4,{},w,h,true);
    if(!published){std::puts("HOST_CPU_PUBLISH_FAILED");return 13;}
    std::puts("HOST_CPU_PUBLISH_TRUE");
    SetEvent(ready);
    const DWORD status=WaitForSingleObject(done,15000);
    CloseHandle(done);CloseHandle(ready);
    return status==WAIT_OBJECT_0?0:14;
}
using Render=BOOL(WINAPI*)(IDirect3DDevice9*);
static int Consumer(const wchar_t* hookDll,const wchar_t* producerExe) {
    Probe("consumer-start");
    const DWORD pid=GetCurrentProcessId();
    const std::wstring suffix=std::to_wstring(pid);
    const std::wstring mapping=L"Local\\NVIDIAOSC.Frame.D3D9.HostCpuTest."+suffix;
    const std::wstring readyName=L"Local\\NVIDIAOSC.Test.DX9.Ready."+suffix;
    const std::wstring doneName=L"Local\\NVIDIAOSC.Test.DX9.Done."+suffix;
    if(!SetEnvironmentVariableW(
        osc_frame_bridge::D3D9FrameMappingNameEnvironmentVariable,
        mapping.c_str()))return 21;
    HANDLE ready=CreateEventW(nullptr,TRUE,FALSE,readyName.c_str());
    HANDLE done=CreateEventW(nullptr,TRUE,FALSE,doneName.c_str());
    if(!ready||!done)return 22;
    std::wstring command=L"\""+std::wstring(producerExe)+L"\" --producer \""+
        mapping+L"\" \""+readyName+L"\" \""+doneName+L"\"";
    std::vector<wchar_t> mutableCommand(command.begin(),command.end());
    mutableCommand.push_back(L'\0');
    STARTUPINFOW si{};si.cb=sizeof(si);
    PROCESS_INFORMATION pi{};
    Probe("before-CreateProcessW");
    if(!CreateProcessW(producerExe,mutableCommand.data(),nullptr,nullptr,
                       FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi))return 23;
    Probe("CreateProcessW-success");
    int outcome=0;
    HMODULE hook=nullptr;
    IDirect3D9* d3d=nullptr;IDirect3DDevice9* device=nullptr;
    HWND window=nullptr;
    do {
        Probe("waiting-for-producer-ready");
        if(WaitForSingleObject(ready,12000)!=WAIT_OBJECT_0) {
            outcome=24;break;
        }
        Probe("producer-ready");
        HANDLE map=OpenFileMappingW(FILE_MAP_READ,FALSE,mapping.c_str());
        if(!map) {outcome=25;break;}
        auto* f=static_cast<const osc_frame_bridge::D3D9Frame*>(
            MapViewOfFile(map,FILE_MAP_READ,0,0,
                          osc_frame_bridge::D3D9FrameMappingBytes));
        if(!f){CloseHandle(map);outcome=26;break;}
        const bool wireOk=
            f->magic==osc_frame_bridge::D3D9FrameMagic &&
            f->version==osc_frame_bridge::D3D9FrameVersion &&
            !(f->sequence&1) && f->frameNumber>0 &&
            f->ownerProcessId==static_cast<LONG>(pi.dwProcessId) &&
            f->width==640 && f->height==480 && f->pixelBytes==640*480*4;
        UnmapViewOfFile(f);CloseHandle(map);
        if(!wireOk){outcome=27;break;}
        Probe("wire-validated");
        window=CreateWindowExW(0,L"STATIC",L"Private D3D9 Host CPU fixture",
            WS_OVERLAPPEDWINDOW,100,100,640,480,nullptr,nullptr,
            GetModuleHandleW(nullptr),nullptr);
        if(!window){outcome=28;break;}
        Probe("window-created");
        // Normal attach: load the verified Hook before game device creation.
        // Late-attach/unregister is already verified in its dedicated fixture.
        hook=LoadLibraryW(hookDll);
        if(!hook){outcome=31;break;}
        Probe("hook-loaded");
        d3d=Direct3DCreate9(D3D_SDK_VERSION);
        if(!d3d){outcome=29;break;}
        D3DPRESENT_PARAMETERS pp{};
        pp.BackBufferWidth=640;pp.BackBufferHeight=480;
        pp.BackBufferFormat=D3DFMT_UNKNOWN;pp.BackBufferCount=1;
        pp.SwapEffect=D3DSWAPEFFECT_COPY;pp.hDeviceWindow=window;
        pp.Windowed=TRUE;pp.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
        Probe("before-create-device");
        HRESULT hr=d3d->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,
            window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&pp,&device);
        if(FAILED(hr)){std::printf("CREATE_DEVICE_FAIL=0x%08lx\n",
            static_cast<unsigned long>(hr));outcome=30;break;}
        Probe("device-created");
        auto draw=reinterpret_cast<Render>(
            GetProcAddress(hook,"NvOscAdapterRenderD3D9ForTest"));
        if(!draw){outcome=32;break;}
        device->Clear(0,nullptr,D3DCLEAR_TARGET,D3DCOLOR_XRGB(20,30,40),1,0);
        Probe("before-render");
        if(!draw(device)){outcome=33;break;}
        Probe("render-success");
        if(!CheckPixel(device,320,2,0,255,0,8) ||
           !CheckPixel(device,320,477,255,0,0,8) ||
           !CheckPixel(device,320,240,138,15,20,11)){
           outcome=34;break;
        }
        std::puts("REAL_HOST_DX9_CPU_TO_RUNTIME_HOOK_PIXELS_PASS");
    }while(false);
    Probe("begin-cleanup");
    if(device)Drop(device);
    if(d3d)Drop(d3d);
    Probe("before-free-library");
    if(hook)FreeLibrary(hook);
    Probe("after-free-library");
    if(window)DestroyWindow(window);
    SetEvent(done);
    Probe("waiting-child-stop");
    if(WaitForSingleObject(pi.hProcess,8000)!=WAIT_OBJECT_0) {
        TerminateProcess(pi.hProcess,87);if(!outcome)outcome=35;
    } else if(!outcome && pi.hProcess && pi.dwProcessId) {
        DWORD childCode=0;
        if(!GetExitCodeProcess(pi.hProcess,&childCode)||childCode!=0)
            outcome=36;
    }
    CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
    CloseHandle(ready);CloseHandle(done);
    if(outcome)std::printf("HOST_CPU_TO_DX9_TEST_FAIL=%d\n",outcome);
    return outcome;
}
int wmain(int argc,wchar_t** argv) {
    if(argc==5 && wcscmp(argv[1],L"--producer")==0)
        return Producer(argv[2],argv[3],argv[4]);
    if(argc==4 && wcscmp(argv[1],L"--consumer")==0)
        return Consumer(argv[2],argv[3]);
    std::puts("Usage: --producer <map> <ready> <done> OR --consumer <hook dll> <x64 producer exe>");
    return 2;
}
