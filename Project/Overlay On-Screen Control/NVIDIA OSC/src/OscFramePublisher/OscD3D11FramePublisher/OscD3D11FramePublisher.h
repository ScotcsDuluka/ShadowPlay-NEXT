#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
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
bool PublishMetadataLocked(bool forceHidden = false);
void PublishFrameLocked(ID3D11Texture2D*, const std::vector<CefRect>&);
bool PublishMetadata(bool forceHidden = false);
void Stop();
}
