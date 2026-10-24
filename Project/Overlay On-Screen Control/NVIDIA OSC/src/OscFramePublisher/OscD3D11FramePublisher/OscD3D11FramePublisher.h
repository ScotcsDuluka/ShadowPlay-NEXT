#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>
class CefRect;
namespace osc_frame_bridge { struct ConsumerAdapterInfo; }
namespace osc::d3d11_frame_publisher {
struct HostBindings {
 ID3D11Device* (*gpuDevice)() = nullptr;
 ID3D11DeviceContext* (*gpuContext)() = nullptr;
 ID3D11Texture2D* (*sharedTexture)() = nullptr;
 HWND (*window)() = nullptr;
 UINT (*viewWidth)() = nullptr;
 UINT (*viewHeight)() = nullptr;
 bool (*gameFocused)() = nullptr;
 bool (*overlayVisible)() = nullptr;
 void (*log)(const std::string&) = nullptr;
};
void Initialize(const HostBindings& bindings);
std::mutex& Mutex();
osc_frame_bridge::ConsumerAdapterInfo* GetConsumerAdapterInfo();
bool ForceFullReadbackLocked();
void SetForceFullReadbackLocked(bool force);
bool HasPendingReadbackLocked();
bool HasCrossAdapterDeviceLocked();
bool QueueFrameReadbackLocked(ID3D11Texture2D*, const std::vector<CefRect>&);
bool PublishMetadataLocked(bool forceHidden = false,
    std::uint64_t completedCefContentSerial = 0);
// Under Mutex(): mark a new CEF accelerated-view paint, not idle/metadata.
void MarkCefContentUpdatedLocked();
void PublishFrameLocked(ID3D11Texture2D*, const std::vector<CefRect>&);
// Called only while the CEF GPU handle is valid, under Mutex(). Copies to
// a bounded, Host-owned staging texture without waiting for GPU completion.
// OFF by default; requires OSC_DX9EX_CEF_GPU_PREVIEW=1.
bool QueueD3D9ExGpuFrameLocked(ID3D11Texture2D* cefTexture);
// Explicit D3D9Ex CPU preference disables both GPU upload and direct
// CEF -> legacy shared-texture preview; CPU Shared Memory v1 stays active.
void SetD3D9ExCpuOnly(bool cpuOnly);
bool PublishMetadata(bool forceHidden = false);
void Stop();
}
