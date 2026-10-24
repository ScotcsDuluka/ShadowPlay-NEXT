#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "OscD3D9ExFramePublisher.h"
#include <dxgi1_2.h>
#include <atomic>
#include <cstring>
namespace osc::d3d9ex_frame_publisher {
namespace {
HANDLE g_handle=nullptr;
Mailbox* g_wire=nullptr;
bool g_checkedEnv=false, g_enabled=false;
LONG64 g_nextGeneration=0;
bool g_withdrawn=false;
DWORD g_receiverPid=0;
LONG64 g_receiverLuid=0;
struct Resource {
  ID3D11Texture2D* texture=nullptr;
  ID3D11Query* completion=nullptr;
  ID3D11DeviceContext* context=nullptr;
  HANDLE legacy=nullptr; // opaque DXGI legacy handle; DO NOT CloseHandle
  LONG64 generation=0;
  bool ready=false;
  void Reset() {
    if(completion){completion->Release();completion=nullptr;}
    if(texture){texture->Release();texture=nullptr;}
    if(context){context->Release();context=nullptr;}
    legacy=nullptr; generation=0; ready=false;
  }
};
Resource g_active{},g_pending{},g_retiring{};
LONG64 Read64(volatile LONG64* p) {return InterlockedCompareExchange64(p,0,0);}
LONG Read32(volatile LONG* p) {return InterlockedCompareExchange(p,0,0);}
bool Alive(DWORD pid) {
  if (!pid) return false;
  HANDLE h=OpenProcess(SYNCHRONIZE,FALSE,pid);
  if(!h)return false;
  const bool alive=WaitForSingleObject(h,0)==WAIT_TIMEOUT;
  CloseHandle(h);return alive;
}
LONG64 PackedLuid(const LUID& luid) {
  return static_cast<LONG64>((static_cast<std::uint64_t>(
    static_cast<std::uint32_t>(luid.HighPart))<<32)|luid.LowPart);
}
bool Ensure() {
  if(!g_checkedEnv) {
    g_checkedEnv=true;
    char value[8]{};
    const DWORD n=GetEnvironmentVariableA("OSC_DX9EX_HOST_PREVIEW",value,sizeof(value));
    g_enabled=n==1 && value[0]=='1';
  }
  if(!g_enabled)return false;
  if(g_wire) {
    return g_wire->magic==MailboxMagic &&
      g_wire->version==MailboxVersion &&
      Read32(&g_wire->producerPid)==static_cast<LONG>(GetCurrentProcessId());
  }
  wchar_t overrideName[128]{};
  const DWORD chars=GetEnvironmentVariableW(
    L"NVIDIA_OSC_HOST_D3D9EX_MAILBOX_NAME",overrideName,
    static_cast<DWORD>(_countof(overrideName)));
  if(chars>=_countof(overrideName))return false;
  const wchar_t* mailboxName=chars?overrideName:MailboxName;
  SetLastError(ERROR_SUCCESS);
  g_handle=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,
    0,sizeof(Mailbox),mailboxName);
  if(!g_handle)return false;
  const bool created=GetLastError()!=ERROR_ALREADY_EXISTS;
  g_wire=static_cast<Mailbox*>(MapViewOfFile(g_handle,FILE_MAP_ALL_ACCESS,
    0,0,sizeof(Mailbox)));
  if(!g_wire){CloseHandle(g_handle);g_handle=nullptr;return false;}
  if(created) {
    std::memset(g_wire,0,sizeof(Mailbox));
    g_wire->version=MailboxVersion;
    MemoryBarrier();
    g_wire->magic=MailboxMagic;
  }
  if(g_wire->magic!=MailboxMagic||g_wire->version!=MailboxVersion)return false;
  const LONG self=static_cast<LONG>(GetCurrentProcessId());
  LONG owner=InterlockedCompareExchange(&g_wire->producerPid,self,0);
  if(owner!=0 && owner!=self) {
    if(Alive(static_cast<DWORD>(owner)))return false;
    if(InterlockedCompareExchange(&g_wire->producerPid,self,owner)!=owner)
      return false;
    // Fresh process cannot reuse a stale generation or old handshakes.
    InterlockedExchange64(&g_wire->generation,0);
    InterlockedExchange64(&g_wire->readyGeneration,0);
    InterlockedExchange64(&g_wire->stagedGeneration,0);
    InterlockedExchange64(&g_wire->committedGeneration,0);
    InterlockedExchange64(&g_wire->releasedGeneration,0);
    InterlockedExchange(&g_wire->consumerCapabilities,0);
  }
  return true;
}
void ClearOffer() {
  if(!g_wire)return;
  InterlockedIncrement(&g_wire->sequence);
  InterlockedExchange64(&g_wire->readyGeneration,0);
  InterlockedExchange64(&g_wire->generation,0);
  g_wire->legacySharedHandle=0;
  g_wire->width=g_wire->height=0;
  MemoryBarrier();
  InterlockedIncrement(&g_wire->sequence);
}
void PublishOffer(const Resource& resource,LONG64 luid,UINT w,UINT h) {
  InterlockedIncrement(&g_wire->sequence);
  InterlockedExchange64(&g_wire->targetAdapterLuid,luid);
  InterlockedExchange64(&g_wire->generation,resource.generation);
  InterlockedExchange64(&g_wire->readyGeneration,0);
  g_wire->legacySharedHandle=
    static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(resource.legacy));
  g_wire->width=w;g_wire->height=h;
  g_wire->format=DXGI_FORMAT_B8G8R8A8_UNORM;
  MemoryBarrier();
  InterlockedIncrement(&g_wire->sequence);
}
bool Handshake(DWORD pid,LONG64 luid) {
  return pid && g_wire && Alive(pid) &&
    Read32(&g_wire->consumerPid)==static_cast<LONG>(pid) &&
    (Read32(&g_wire->consumerCapabilities)&1)==1 &&
    Read64(&g_wire->consumerAdapterLuid)==luid;
}
void RetireDeadProcess() {
  if(g_receiverPid && !Alive(g_receiverPid)){
    g_pending.Reset();g_active.Reset();g_retiring.Reset();
    g_receiverPid=0;g_receiverLuid=0;g_withdrawn=false;ClearOffer();
  }
}
} // anonymous namespace
void Poll() {
  if(!Ensure())return;
  RetireDeadProcess();
  if(g_retiring.texture && Read64(&g_wire->releasedGeneration)>=
      g_retiring.generation)g_retiring.Reset();
  if(g_withdrawn && g_active.texture &&
     Read64(&g_wire->releasedGeneration)>=g_active.generation) {
    g_active.Reset();
    g_withdrawn=false;
  }
  if(!g_pending.texture)return;
  if(!g_pending.ready) {
    const HRESULT result=g_pending.context->GetData(g_pending.completion,
      nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if(result==S_OK) {
      g_pending.ready=true;
      // This is the ONLY Stage-eligible state: GPU has finished writing.
      InterlockedExchange64(&g_wire->readyGeneration,g_pending.generation);
    } else if(FAILED(result)) {
      ClearOffer();g_pending.Reset();
    }
    return;
  }
  // The trusted game-process receiver calls Stage/Commit. It only writes
  // acknowledgements after each API call succeeds. Host never calls D3D9Ex
  // device methods from a foreign thread/process.
  if(g_retiring.texture ||
     Read64(&g_wire->stagedGeneration)!=g_pending.generation ||
     Read64(&g_wire->committedGeneration)!=g_pending.generation)return;
  if(g_active.texture) {g_retiring=g_active;g_active={};}
  g_active=g_pending;g_pending={};
}
bool Withdraw() {
  if(!Ensure() || g_pending.texture || g_retiring.texture)return false;
  ClearOffer();
  g_withdrawn=g_active.texture!=nullptr;
  return true;
}
bool CanPublish(DWORD pid,const LUID& luid) {
  if(!Ensure())return false;
  Poll();
  const LONG64 packed=PackedLuid(luid);
  return Handshake(pid,packed) && !g_withdrawn &&
    !g_pending.texture &&
    !g_retiring.texture && (!g_receiverPid ||
      (g_receiverPid==pid && g_receiverLuid==packed));
}
bool TryPublish(ID3D11Device* device,ID3D11DeviceContext* context,
    const std::uint8_t* bgra,UINT pitch,UINT w,UINT h,
    const LUID& luid,DWORD pid) {
  if(!Ensure())return false;
  Poll();
  const LONG64 packed=PackedLuid(luid);
  if(!device||!context||!bgra||!w||!h||!pid||w>8192||h>8192||
     static_cast<std::uint64_t>(w)*h*4>64ull*1024*1024||
     pitch<w*4||!Handshake(pid,packed))return false;
  // Old resources cannot be stolen while a different game still uses them.
  if(g_receiverPid && (g_receiverPid!=pid||g_receiverLuid!=packed))
    return false;
  if(g_withdrawn||g_pending.texture||g_retiring.texture)return false;
  g_receiverPid=pid;g_receiverLuid=packed;
  D3D11_TEXTURE2D_DESC desc{};
  desc.Width=w;desc.Height=h;desc.MipLevels=1;desc.ArraySize=1;
  desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
  desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;
  desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
  desc.MiscFlags=D3D11_RESOURCE_MISC_SHARED; // legacy, NOT NT/keyed
  Resource candidate{};
  if(FAILED(device->CreateTexture2D(&desc,nullptr,&candidate.texture)))
    return false;
  IDXGIResource* shared=nullptr;
  HRESULT result=candidate.texture->QueryInterface(
    __uuidof(IDXGIResource),reinterpret_cast<void**>(&shared));
  if(SUCCEEDED(result))result=shared->GetSharedHandle(&candidate.legacy);
  if(shared)shared->Release();
  if(FAILED(result)||!candidate.legacy){candidate.Reset();return false;}
  D3D11_QUERY_DESC query{};query.Query=D3D11_QUERY_EVENT;
  if(FAILED(device->CreateQuery(&query,&candidate.completion))){
    candidate.Reset();return false;
  }
  context->AddRef();candidate.context=context;
  context->UpdateSubresource(candidate.texture,0,nullptr,bgra,pitch,0);
  context->End(candidate.completion);
  context->Flush(); // submit asynchronously; NEVER wait on Game Present
  candidate.generation=++g_nextGeneration;
  g_pending=candidate;
  PublishOffer(g_pending,packed,w,h);
  return true;
}
bool TryPublishGpuTexture(ID3D11Device* device,
    ID3D11DeviceContext* context, ID3D11Texture2D* source,
    const LUID& gameLuid, DWORD gamePid) {
  if(!Ensure())return false;
  Poll();
  if(!device||!context||!source||!gamePid)return false;
  const LONG64 packed=PackedLuid(gameLuid);
  if(!Handshake(gamePid,packed)||g_withdrawn||
     g_pending.texture||g_retiring.texture ||
     (g_receiverPid && (g_receiverPid!=gamePid ||
                        g_receiverLuid!=packed)))return false;

  // CopyResource must NOT use a texture from an unrelated GPU/device.
  // A CEF handle may be opened on the Host device only when supported.
  ID3D11Device* sourceOwner=nullptr;
  source->GetDevice(&sourceOwner);
  const bool sameDevice=sourceOwner==device;
  if(sourceOwner)sourceOwner->Release();
  if(!sameDevice)return false;
  IDXGIDevice* dxgi=nullptr;
  IDXGIAdapter* adapter=nullptr;
  DXGI_ADAPTER_DESC adapterDesc{};
  HRESULT status=device->QueryInterface(__uuidof(IDXGIDevice),
      reinterpret_cast<void**>(&dxgi));
  if(SUCCEEDED(status))status=dxgi->GetAdapter(&adapter);
  if(SUCCEEDED(status))status=adapter->GetDesc(&adapterDesc);
  if(adapter)adapter->Release();
  if(dxgi)dxgi->Release();
  if(FAILED(status)||PackedLuid(adapterDesc.AdapterLuid)!=packed)
    return false;

  D3D11_TEXTURE2D_DESC srcDesc{};
  source->GetDesc(&srcDesc);
  if(!srcDesc.Width||!srcDesc.Height||
     srcDesc.Width>8192||srcDesc.Height>8192||
     static_cast<std::uint64_t>(srcDesc.Width)*srcDesc.Height*4 >
       64ull*1024*1024 ||
     srcDesc.SampleDesc.Count!=1 ||
     srcDesc.MipLevels!=1 || srcDesc.ArraySize!=1 ||
     (srcDesc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM &&
      srcDesc.Format!=DXGI_FORMAT_B8G8R8A8_TYPELESS))
    return false;

  D3D11_TEXTURE2D_DESC dstDesc{};
  dstDesc.Width=srcDesc.Width; dstDesc.Height=srcDesc.Height;
  dstDesc.MipLevels=1; dstDesc.ArraySize=1;
  dstDesc.SampleDesc.Count=1;
  dstDesc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
  dstDesc.Usage=D3D11_USAGE_DEFAULT;
  dstDesc.BindFlags=D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
  dstDesc.MiscFlags=D3D11_RESOURCE_MISC_SHARED; // legacy DX9Ex handle
  Resource candidate{};
  if(FAILED(device->CreateTexture2D(&dstDesc,nullptr,&candidate.texture)))
    return false;
  IDXGIResource* shared=nullptr;
  HRESULT result=candidate.texture->QueryInterface(
      __uuidof(IDXGIResource),reinterpret_cast<void**>(&shared));
  if(SUCCEEDED(result))result=shared->GetSharedHandle(&candidate.legacy);
  if(shared)shared->Release();
  if(FAILED(result)||!candidate.legacy){candidate.Reset();return false;}
  D3D11_QUERY_DESC query{};query.Query=D3D11_QUERY_EVENT;
  if(FAILED(device->CreateQuery(&query,&candidate.completion))) {
    candidate.Reset();return false;
  }
  context->AddRef();
  candidate.context=context;
  context->CopyResource(candidate.texture,source); // GPU to GPU, no Map/CPU.
  context->End(candidate.completion);
  context->Flush();
  candidate.generation=++g_nextGeneration;
  g_receiverPid=gamePid;
  g_receiverLuid=packed;
  g_pending=candidate;
  PublishOffer(g_pending,packed,srcDesc.Width,srcDesc.Height);
  return true;
}

void Stop() {
  // Only after the OSC publish worker exits. CPU Bridge remains independent.
  if(g_wire) {
    ClearOffer();
    if(Read32(&g_wire->producerPid)==static_cast<LONG>(GetCurrentProcessId()))
      InterlockedExchange(&g_wire->producerPid,0);
  }
  g_pending.Reset();g_retiring.Reset();g_active.Reset();
  if(g_wire){UnmapViewOfFile(g_wire);g_wire=nullptr;}
  if(g_handle){CloseHandle(g_handle);g_handle=nullptr;}
  g_receiverPid=0;g_receiverLuid=0;g_withdrawn=false;
}
} // namespace osc::d3d9ex_frame_publisher
