#pragma once
#include <windows.h>
#include <d3d9.h>
#include <cstdint>
namespace duluka::d3d9
{
    void OnPresent(IDirect3DDevice9 *device);
    void OnReset();
    void RenderForTest(IDirect3DDevice9 *device);
    std::uint64_t GetDrawCount();
    void Shutdown();
}