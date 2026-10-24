// OSC Host REAL publisher module, cross-process integration fixture.
// Intended as child of Duluka's unmodified x64/x86 Hook mailbox IPC test.
// The child runs in its own process with a PRIVATE, per-test mailbox.
// No CEF, game, production mapping, or deployed Hook is modified.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <vector>
#include "../src/OscFramePublisher/OscD3D9ExFramePublisher/OscD3D9ExFramePublisher.h"

using namespace osc::d3d9ex_frame_publisher;
template<class T> void Drop(T*& p){if(p){p->Release();p=nullptr;}}
LONG64 Read64(volatile LONG64* p){return InterlockedCompareExchange64(p,0,0);}
LONG Read32(volatile LONG* p){return InterlockedCompareExchange(p,0,0);}
bool Wait(const std::function<bool()>& fn,DWORD timeout=8000) {
 const ULONGLONG end=GetTickCount64()+timeout;
 while(GetTickCount64()<end) { if(fn())return true;Sleep(2); }
 return false;
}
LONG64 Pack(const LUID& l){
 return static_cast<LONG64>((static_cast<std::uint64_t>(
   static_cast<std::uint32_t>(l.HighPart))<<32)|l.LowPart);
}
bool SubmitFrame(ID3D11Device* device, ID3D11DeviceContext* context,
    const std::vector<std::uint32_t>& pixels, UINT width, UINT height,
    const LUID& gameLuid, DWORD gamePid, bool directGpu) {
  if(!directGpu) {
    return TryPublish(device,context,
      reinterpret_cast<const std::uint8_t*>(pixels.data()),
      width*4,width,height,gameLuid,gamePid);
  }
  // Synthetic already-rendered CEF equivalent: source is a GPU texture.
  // The Host GPU publisher copies this to a legacy-shared GPU texture.
  D3D11_TEXTURE2D_DESC desc{};
  desc.Width=width;desc.Height=height;desc.MipLevels=1;desc.ArraySize=1;
  desc.SampleDesc.Count=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
  desc.Usage=D3D11_USAGE_DEFAULT;
  desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  D3D11_SUBRESOURCE_DATA init{};
  init.pSysMem=pixels.data(); init.SysMemPitch=width*4;
  ID3D11Texture2D* source=nullptr;
  if(FAILED(device->CreateTexture2D(&desc,&init,&source)))return false;
  const bool ok=TryPublishGpuTexture(device,context,source,gameLuid,gamePid);
  Drop(source);
  return ok;
}
void WriteTarget(Mailbox* wire,LONG64 target,LONG64 ready){
 InterlockedIncrement(&wire->sequence);
 InterlockedExchange64(&wire->targetAdapterLuid,target);
 InterlockedExchange64(&wire->readyGeneration,ready);
 MemoryBarrier();
 InterlockedIncrement(&wire->sequence);
}
int wmain(int argc,wchar_t** argv) {
 if(argc!=3 || wcscmp(argv[1],L"--producer")!=0)return 100;
 // Force publisher to use the PRIVATE mapping supplied by the Hook
 // test instead of its global production mailbox.
 SetEnvironmentVariableW(L"NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME",argv[2]);
 SetEnvironmentVariableA("OSC_DX9EX_HOST_PREVIEW","1");
 char gpuMode[8]{};
 const bool directGpu=GetEnvironmentVariableA(
     "OSC_HOST_TEST_DIRECT_GPU",gpuMode,sizeof(gpuMode))==1 &&
     gpuMode[0]=='1';
 std::printf("HOST_PUBLISHER_TEST_MODE=%s\\n",
     directGpu?"GPU_COPY":"CPU_UPLOAD");
 Poll(); // Host publisher maps the already-created private mailbox.
 HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,argv[2]);
 if(!mapping)return 101;
 auto* wire=static_cast<Mailbox*>(MapViewOfFile(
    mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Mailbox)));
 if(!wire){CloseHandle(mapping);return 102;}
 int result=0;
 ID3D11Device* device=nullptr;
 ID3D11DeviceContext* context=nullptr;
 IDXGIFactory1* factory=nullptr;
 IDXGIAdapter1* adapter=nullptr;
 do {
  if(!Wait([&](){return Read32(&wire->producerPid)==
      static_cast<LONG>(GetCurrentProcessId()) &&
      Read32(&wire->consumerPid)>0 &&
      (Read32(&wire->consumerCapabilities)&1)==1 &&
      Read64(&wire->consumerAdapterLuid)!=0;})){result=103;break;}
  const LONG64 target=Read64(&wire->consumerAdapterLuid);
  const DWORD gamePid=static_cast<DWORD>(Read32(&wire->consumerPid));
  if(FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1),
     reinterpret_cast<void**>(&factory)))){result=104;break;}
  LUID luid{};
  bool found=false;
  for(UINT index=0;index<32;++index) {
   IDXGIAdapter1* a=nullptr;
   if(factory->EnumAdapters1(index,&a)!=S_OK)break;
   DXGI_ADAPTER_DESC1 d{};
   if(SUCCEEDED(a->GetDesc1(&d)) && Pack(d.AdapterLuid)==target){
    adapter=a;luid=d.AdapterLuid;found=true;break;
   }
   Drop(a);
  }
  if(!found || FAILED(D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,
      nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
      D3D11_SDK_VERSION,&device,nullptr,&context))){result=105;break;}
  constexpr UINT w=32,h=24;
  std::vector<std::uint32_t> pixels(w*h,0xFF123456u);
  if(!SubmitFrame(device,context,pixels,w,h,luid,gamePid,directGpu)){
     result=106;break;
  }
  if(!Wait([&](){return wire->reserved==11;})){result=107;break;}
  if(!Wait([&](){Poll();return Read64(&wire->readyGeneration)==1;})){
      result=108;break;}
  if(!Wait([&](){Poll();return Read64(&wire->stagedGeneration)==1 &&
      Read64(&wire->committedGeneration)==1 && wire->reserved==1;})){
      result=109;break;}
  // Make sure Host has moved generation 1 from pending into active.
  Poll();
  for(auto& pixel:pixels)pixel=0xFF789ABCu;
  if(!SubmitFrame(device,context,pixels,w,h,luid,gamePid,directGpu)){
     result=110;break;
  }
  // Existing Hook fixture exercises a NEGATIVE LUID test before
  // committing frame 2. Temporarily corrupt only PRIVATE test metadata,
  // never a live Host endpoint; normal Host never does this.
  WriteTarget(wire,target^1,2);
  if(!Wait([&](){return wire->reserved==13;})){result=111;break;}
  WriteTarget(wire,target,0);
  if(!Wait([&](){return wire->reserved==12;})){result=112;break;}
  if(!Wait([&](){Poll();return Read64(&wire->readyGeneration)==2;})){
      result=113;break;}
  if(!Wait([&](){Poll();
      return Read64(&wire->stagedGeneration)==2 &&
             Read64(&wire->committedGeneration)==2 &&
             Read64(&wire->releasedGeneration)>=1 &&
             wire->reserved==2;})){result=114;break;}
  Poll(); // Free retired generation 1 AFTER Hook release ACK.
  if(!Withdraw()){result=115;break;}
  if(!Wait([&](){Poll();return Read64(&wire->releasedGeneration)>=2;})){
      result=116;break;}
  std::printf("OSC_HOST_PUBLISHER_TO_REAL_HOOK_RECEIVER=PASS\n");
 }while(false);
 Stop();
 Drop(context);Drop(device);Drop(adapter);Drop(factory);
 UnmapViewOfFile(wire);CloseHandle(mapping);
 if(result)std::fprintf(stderr,"HOST_REAL_PUBLISHER_TEST_ERROR=%d\n",result);
 return result;
}
