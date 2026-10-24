#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <cstddef>
#include <cstdint>
namespace osc::d3d9ex_frame_publisher {
// Host Phase 0 opt-in mailbox. The Hook-side receiver is NOT connected yet.
inline constexpr wchar_t MailboxName[] = L"Local\\NVIDIAOSC.Frame.D3D9Ex.Gpu.v1";
inline constexpr std::uint32_t MailboxMagic = 0x4F584739;
inline constexpr std::uint32_t MailboxVersion = 1;
#pragma pack(push, 8)
struct alignas(8) Mailbox {
  std::uint32_t magic, version;
  volatile LONG sequence, producerPid;
  volatile LONG consumerPid, consumerCapabilities; // bit 0: Stage/Commit API
  volatile LONG64 consumerAdapterLuid; // consumer handshake
  volatile LONG64 targetAdapterLuid;   // producer metadata
  volatile LONG64 generation;          // offered resource generation
  volatile LONG64 readyGeneration;     // GPU completion query passed
  volatile LONG64 stagedGeneration;    // receiver acks successful Stage
  volatile LONG64 committedGeneration; // receiver acks successful Commit
  volatile LONG64 releasedGeneration;  // receiver acks retired alias release
  std::uint64_t legacySharedHandle;    // DXGI legacy, NOT NT and not CloseHandle
  std::uint32_t width, height, format, reserved;
};
#pragma pack(pop)
static_assert(sizeof(Mailbox)==104);
static_assert(offsetof(Mailbox, consumerAdapterLuid)==24);
static_assert(offsetof(Mailbox, legacySharedHandle)==80);
void Poll();
// Graceful end-of-overlay: announce Offer=0 and retain the committed
// legacy resource until the game acknowledges alias release.
bool Withdraw();
bool CanPublish(DWORD gamePid, const LUID& gameLuid);
bool TryPublish(ID3D11Device* gameDevice, ID3D11DeviceContext* context,
    const std::uint8_t* premultipliedBgra, UINT rowPitch,
    UINT width, UINT height, const LUID& gameLuid, DWORD gamePid);
// No CPU readback: copy a fully rendered BGRA8 texture into the
// game-adapter legacy texture and publish only after a GPU completion query.
// Only works when source and destination devices are the SAME D3D11 device;
// cross-adapter copying is a separate, fallback-aware path.
bool TryPublishGpuTexture(ID3D11Device* gameDevice,
    ID3D11DeviceContext* context, ID3D11Texture2D* source,
    const LUID& gameLuid, DWORD gamePid);
void Stop();
}
