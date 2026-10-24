#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <cstdint>
#include <cstdio>
#include "../src/OscFramePublisher/OscD3D9ExFramePublisher/OscD3D9ExFramePublisher.h"
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
using namespace osc::d3d9ex_frame_publisher;
template<class T> void Drop(T*& p){if(p){p->Release();p=nullptr;}}
bool Ready(Mailbox* wire, LONG64 gen) {
 for(int i=0;i<250;++i){
    Poll();
    if(InterlockedCompareExchange64(&wire->readyGeneration,0,0)==gen)
       return true;
    Sleep(1); // isolated test only, never Game Present
 }
 return false;
}
int main() {
  SetEnvironmentVariableA("OSC_DX9EX_HOST_PREVIEW","1");
  ID3D11Device* device=nullptr;
  ID3D11DeviceContext* context=nullptr;
  IDXGIDevice* dxgi=nullptr;
  IDXGIAdapter* adapter=nullptr;
  DXGI_ADAPTER_DESC desc{};
  if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,
     D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,
     &device,nullptr,&context)))return 1;
  if(FAILED(device->QueryInterface(__uuidof(IDXGIDevice),
        reinterpret_cast<void**>(&dxgi))) ||
     FAILED(dxgi->GetAdapter(&adapter)) ||
     FAILED(adapter->GetDesc(&desc)))return 2;
  const LONG64 packed=static_cast<LONG64>(
     (static_cast<std::uint64_t>(static_cast<std::uint32_t>(
       desc.AdapterLuid.HighPart))<<32)|desc.AdapterLuid.LowPart);
  Poll();
  HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,MailboxName);
  if(!mapping)return 3;
  Mailbox* wire=static_cast<Mailbox*>(MapViewOfFile(
    mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Mailbox)));
  if(!wire)return 4;
  InterlockedExchange(&wire->consumerPid,static_cast<LONG>(GetCurrentProcessId()));
  InterlockedExchange(&wire->consumerCapabilities,1);
  InterlockedExchange64(&wire->consumerAdapterLuid,packed);
  std::uint32_t pixels[64*64]{};
  for(auto& p:pixels)p=0xFF123456u;
  if(!TryPublish(device,context,reinterpret_cast<const std::uint8_t*>(pixels),
      64*4,64,64,desc.AdapterLuid,GetCurrentProcessId()))return 5;
  const LONG64 gen1=InterlockedCompareExchange64(&wire->generation,0,0);
  if(gen1<=0||!Ready(wire,gen1))return 6;
  if(wire->format!=DXGI_FORMAT_B8G8R8A8_UNORM||
     wire->width!=64||wire->height!=64||!wire->legacySharedHandle)return 7;
  ID3D11Texture2D* opened=nullptr;
  if(FAILED(device->OpenSharedResource(
      reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(
        wire->legacySharedHandle)),__uuidof(ID3D11Texture2D),
      reinterpret_cast<void**>(&opened)))||!opened)return 8;
  D3D11_TEXTURE2D_DESC td{};
  opened->GetDesc(&td);
  td.MiscFlags=0;td.BindFlags=0;td.Usage=D3D11_USAGE_STAGING;
  td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
  ID3D11Texture2D* staging=nullptr;
  if(FAILED(device->CreateTexture2D(&td,nullptr,&staging)))return 9;
  context->CopyResource(staging,opened);
  D3D11_MAPPED_SUBRESOURCE mapped{};
  if(FAILED(context->Map(staging,0,D3D11_MAP_READ,0,&mapped)))return 10;
  const std::uint32_t got=*reinterpret_cast<const std::uint32_t*>(mapped.pData);
  context->Unmap(staging,0);
  Drop(staging);Drop(opened);
  if(got!=0xFF123456u)return 11;
  // A different frame cannot replace a staged but uncommitted resource.
  if(TryPublish(device,context,reinterpret_cast<const std::uint8_t*>(pixels),
      64*4,64,64,desc.AdapterLuid,GetCurrentProcessId()))return 12;
  InterlockedExchange64(&wire->stagedGeneration,gen1);
  InterlockedExchange64(&wire->committedGeneration,gen1);
  Poll();
  for(auto& p:pixels)p=0xFF789ABCu;
  if(!TryPublish(device,context,reinterpret_cast<const std::uint8_t*>(pixels),
      64*4,64,64,desc.AdapterLuid,GetCurrentProcessId()))return 13;
  const LONG64 gen2=InterlockedCompareExchange64(&wire->generation,0,0);
  if(gen2<=gen1||!Ready(wire,gen2))return 14;
  InterlockedExchange64(&wire->stagedGeneration,gen2);
  InterlockedExchange64(&wire->committedGeneration,gen2);
  Poll();
  // Before acking release of gen1, producer must retain that texture.
  if(TryPublish(device,context,reinterpret_cast<const std::uint8_t*>(pixels),
      64*4,64,64,desc.AdapterLuid,GetCurrentProcessId()))return 15;
  InterlockedExchange64(&wire->releasedGeneration,gen1);
  Poll();
  if(!TryPublish(device,context,reinterpret_cast<const std::uint8_t*>(pixels),
      64*4,64,64,desc.AdapterLuid,GetCurrentProcessId()))return 16;
  std::printf("PASS: complete GPU write, legacy BGRA8 handle, Stage/Commit ACK, retirement ACK\n");
  Stop();
  UnmapViewOfFile(wire);CloseHandle(mapping);
  Drop(adapter);Drop(dxgi);Drop(context);Drop(device);
  return 0;
}
