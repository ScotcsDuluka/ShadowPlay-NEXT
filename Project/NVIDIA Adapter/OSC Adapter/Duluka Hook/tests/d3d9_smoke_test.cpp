#include <windows.h>
#include <d3d9.h>
#include <strsafe.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

#include "../include/osc_frame_bridge.h"
#include "../include/d3d9_timing_diagnostics.h"
#include "../include/d3d9_pipeline_timing_diagnostics.h"

using GetD3D9Status = BOOL(WINAPI *)(std::uint64_t *, std::uint64_t *, BOOL *);
using RenderD3D9ForTest = BOOL(WINAPI *)(IDirect3DDevice9 *);
using GetD3D9AdapterStatus = BOOL(WINAPI *)(std::uint64_t *, BOOL *,
    BOOL *, std::uint64_t *);
using GetD3D9TimingSnapshot = BOOL(WINAPI *)(
    duluka::diagnostics::D3D9TimingSnapshotV1 *, DWORD);
using GetD3D9PipelineSnapshot = BOOL(WINAPI *)(
    duluka::diagnostics::D3D9PipelineTimingSnapshotV1 *, DWORD);

namespace
{
    bool g_reshadeTest = false;
    bool g_rendererOnly = false;
    bool g_hookCounterOnly = false;
    RenderD3D9ForTest g_renderForTest = nullptr;
    GetD3D9AdapterStatus g_getAdapterStatus = nullptr;

    bool VerifyRendererIdentity(bool expectedEx)
    {
        if (!g_getAdapterStatus)
            return false;
        std::uint64_t luid = 0, busySkips = 0;
        BOOL isEx = FALSE, gpuBound = FALSE;
        const bool ok = g_getAdapterStatus(&luid, &isEx, &gpuBound,
            &busySkips) && luid != 0 && (isEx != FALSE) == expectedEx &&
            gpuBound == FALSE;
        if (!ok)
            std::cerr << "D3D9 Phase-0 identity mismatch: luid="
                      << luid << " isEx=" << isEx
                      << " expectedEx=" << expectedEx
                      << " gpuBound=" << gpuBound << "\n";
        return ok;
    }
    std::uint64_t g_expectedDraws = 0;
    constexpr wchar_t WindowClassName[] = L"DulukaNvOscAdapterD3D9SmokeTest";
    constexpr std::uint32_t FrameWidth = 640;
    constexpr std::uint32_t FrameHeight = 480;
    constexpr std::uint32_t FramePitch = FrameWidth * 4;

    LRESULT CALLBACK WindowProc(HWND window, UINT message,
        WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool CheckPixel(IDirect3DDevice9 *device, std::uint32_t x,
        std::uint32_t y, const char *name, int expectedRed,
        int expectedGreen, int expectedBlue, int tolerance)
    {
        IDirect3DSurface9 *backBuffer = nullptr;
        IDirect3DSurface9 *readback = nullptr;
        D3DSURFACE_DESC backBufferDescription{};
        HRESULT result = device->GetBackBuffer(0, 0,
            D3DBACKBUFFER_TYPE_MONO, &backBuffer);
        if (SUCCEEDED(result))
            result = backBuffer->GetDesc(&backBufferDescription);
        if (SUCCEEDED(result))
            result = device->CreateOffscreenPlainSurface(
                backBufferDescription.Width, backBufferDescription.Height,
                backBufferDescription.Format,
                D3DPOOL_SYSTEMMEM, &readback, nullptr);
        if (SUCCEEDED(result))
            result = device->GetRenderTargetData(backBuffer, readback);

        D3DLOCKED_RECT locked{};
        if (SUCCEEDED(result))
            result = readback->LockRect(&locked, nullptr, D3DLOCK_READONLY);
        if (FAILED(result))
        {
            std::cerr << name << " readback failed: 0x" << std::hex
                      << static_cast<unsigned long>(result) << "\n";
            if (readback) readback->Release();
            if (backBuffer) backBuffer->Release();
            return false;
        }

        const auto *pixel = static_cast<const std::uint8_t *>(locked.pBits) +
            static_cast<std::size_t>(y) * locked.Pitch + x * 4;
        const int blue = pixel[0];
        const int green = pixel[1];
        const int red = pixel[2];
        readback->UnlockRect();
        readback->Release();
        backBuffer->Release();

        const auto closeEnough = [tolerance](int actual, int expected) {
            return actual >= expected - tolerance &&
                actual <= expected + tolerance;
        };
        if (!closeEnough(red, expectedRed) ||
            !closeEnough(green, expectedGreen) ||
            !closeEnough(blue, expectedBlue))
        {
            std::cerr << name << " pixel mismatch: got RGB("
                      << red << "," << green << "," << blue
                      << "), expected RGB(" << expectedRed << ","
                      << expectedGreen << "," << expectedBlue << ")\n";
            return false;
        }
        return true;
    }

    bool VerifyOutput(IDirect3DDevice9 *device)
    {
        return CheckPixel(device, FrameWidth / 2, 2, "top orientation",
                   0, 255, 0, 8) &&
            CheckPixel(device, FrameWidth / 2, FrameHeight - 3,
                   "bottom orientation", 255, 0, 0, 8) &&
            CheckPixel(device, FrameWidth / 2, FrameHeight / 2,
                   "alpha blend", g_reshadeTest ? 128 : 138, g_reshadeTest ? 0 : 15, g_reshadeTest ? 127 : 20, 10) &&
            (!g_reshadeTest || CheckPixel(device, FrameWidth - 4, FrameHeight / 2, "shader background", 0, 0, 255, 8));
    }

    bool ClearFrame(IDirect3DDevice9 *device)
    {
        const HRESULT clearResult = device->Clear(
            0, nullptr, D3DCLEAR_TARGET,
            D3DCOLOR_XRGB(20, 30, 40), 1.0f, 0);
        if (FAILED(clearResult))
        {
            std::cerr << "D3D9 Clear failed: 0x" << std::hex
                      << static_cast<unsigned long>(clearResult) << "\n";
            return false;
        }
        return true;
    }

    bool RenderAndVerify(IDirect3DDevice9Ex *device)
    {
        const int frames = g_reshadeTest ? 420 : 1;
        for (int frame = 0; frame < frames; ++frame)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            { TranslateMessage(&message); DispatchMessageW(&message); }
            if (!ClearFrame(device)) return false;
            if (g_rendererOnly)
            {
                if (!g_renderForTest || !g_renderForTest(device)) return false;
            }
            else if (FAILED(device->PresentEx(
                nullptr, nullptr, nullptr, nullptr, 0))) return false;
            ++g_expectedDraws;
            if (g_reshadeTest) Sleep(16);
        }
        return g_hookCounterOnly || VerifyOutput(device);
    }

    bool RenderAndVerify(IDirect3DDevice9 *device)
    {
        const int frames = g_reshadeTest ? 420 : 1;
        for (int frame = 0; frame < frames; ++frame)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            { TranslateMessage(&message); DispatchMessageW(&message); }
            if (!ClearFrame(device)) return false;
            if (g_rendererOnly)
            {
                if (!g_renderForTest || !g_renderForTest(device)) return false;
            }
            else if (FAILED(device->Present(
                nullptr, nullptr, nullptr, nullptr))) return false;
            ++g_expectedDraws;
            if (g_reshadeTest) Sleep(16);
        }
        return g_hookCounterOnly || VerifyOutput(device);
    }

    bool VerifyConsumerAdapterInfo()
    {
        wchar_t name[128]{};
        if (!GetEnvironmentVariableW(osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
                name, static_cast<DWORD>(std::size(name)))) return false;
        HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (!mapping)
            return false;
        auto *info = static_cast<const osc_frame_bridge::ConsumerAdapterInfo *>(
            MapViewOfFile(mapping, FILE_MAP_READ, 0, 0,
                sizeof(osc_frame_bridge::ConsumerAdapterInfo)));
        if (!info)
        {
            CloseHandle(mapping);
            return false;
        }
        const bool valid =
            info->magic == osc_frame_bridge::ConsumerAdapterMagic &&
            info->version == osc_frame_bridge::ConsumerAdapterVersion &&
            info->ownerProcessId == static_cast<LONG>(GetCurrentProcessId()) &&
            info->adapterLuid != 0 &&
            osc_frame_bridge::LoadConsumerRenderApi(*info) == static_cast<LONG>(
                osc_frame_bridge::ConsumerRenderApiD3D9);
        if (!valid) std::cerr << "Consumer identity invalid: pid=" << info->ownerProcessId
            << " api=" << info->renderApi << " luid=" << info->adapterLuid << "\n";
        UnmapViewOfFile(info);
        CloseHandle(mapping);
        return valid;
    }
}

int wmain(int argc, wchar_t **argv)
{
    const bool externalCounterOnly = argc == 4 &&
        std::wcscmp(argv[2], L"--external-attach") == 0 &&
        std::wcscmp(argv[3], L"--hook-counter-only") == 0;
    if (argc != 2 &&
        !(argc == 3 && std::wcscmp(argv[2], L"--external-attach") == 0) &&
        !(argc == 3 && std::wcscmp(argv[2], L"--renderer-only") == 0) &&
        !(argc == 3 && std::wcscmp(argv[2], L"--hook-counter-only") == 0) &&
        !externalCounterOnly &&
        !(argc == 4 && std::wcscmp(argv[2], L"--reshade") == 0))
    {
        std::cerr << "Usage: NVIDIA OSC Adapter D3D9 Smoke Test.exe <adapter-dll>\n";
        return 2;
    }

    g_rendererOnly = argc == 3 &&
        std::wcscmp(argv[2], L"--renderer-only") == 0;
    g_hookCounterOnly = (argc == 3 &&
        std::wcscmp(argv[2], L"--hook-counter-only") == 0) ||
        externalCounterOnly;
    const bool externalAttach = (argc == 3 &&
        std::wcscmp(argv[2], L"--external-attach") == 0) ||
        externalCounterOnly;
    wchar_t mappingName[128]{};
    if (FAILED(StringCchPrintfW(mappingName, std::size(mappingName),
            L"Local\\NVIDIAOSC.Frame.D3D9.Smoke.%lu", GetCurrentProcessId())) ||
        !SetEnvironmentVariableW(
            osc_frame_bridge::D3D9FrameMappingNameEnvironmentVariable,
            mappingName))
    {
        std::cerr << "Could not configure the private DX9 frame mapping.\n";
        return 3;
    }

    wchar_t identityName[128]{};
    if (FAILED(StringCchPrintfW(identityName, std::size(identityName),
            L"Local\\NVIDIAOSC.Identity.D3D9.Smoke.%lu", GetCurrentProcessId())) ||
        !SetEnvironmentVariableW(osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
            identityName)) return 3;

    const DWORD mappingHigh = static_cast<DWORD>(
        static_cast<std::uint64_t>(osc_frame_bridge::D3D9FrameMappingBytes) >> 32);
    const DWORD mappingLow = static_cast<DWORD>(
        osc_frame_bridge::D3D9FrameMappingBytes & 0xFFFFFFFF);
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr,
        PAGE_READWRITE, mappingHigh, mappingLow, mappingName);
    if (!mapping)
    {
        std::cerr << "CreateFileMappingW failed: " << GetLastError() << "\n";
        return 4;
    }
    auto *frame = static_cast<osc_frame_bridge::D3D9Frame *>(
        MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0,
            osc_frame_bridge::D3D9FrameMappingBytes));
    if (!frame)
    {
        std::cerr << "MapViewOfFile failed: " << GetLastError() << "\n";
        CloseHandle(mapping);
        return 5;
    }

    // Model the OSC CEF GPU readback (Y-inverted relative to screen).
    std::vector<std::uint8_t> pixels(
        static_cast<std::size_t>(FramePitch) * FrameHeight, 0);
    for (std::uint32_t y = FrameHeight - 8; y < FrameHeight; ++y)
        for (std::uint32_t x = 0; x < FrameWidth; ++x)
        {
            const std::size_t offset =
                static_cast<std::size_t>(y) * FramePitch + x * 4;
            pixels[offset + 1] = 255;
            pixels[offset + 3] = 255;
        }
    for (std::uint32_t y = 0; y < 8; ++y)
        for (std::uint32_t x = 0; x < FrameWidth; ++x)
        {
            const std::size_t offset =
                static_cast<std::size_t>(y) * FramePitch + x * 4;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = 255;
        }
    for (std::uint32_t y = FrameHeight / 2 - 8; y < FrameHeight / 2 + 8; ++y)
        for (std::uint32_t x = FrameWidth / 2 - 8; x < FrameWidth / 2 + 8; ++x)
        {
            const std::size_t offset =
                static_cast<std::size_t>(y) * FramePitch + x * 4;
            pixels[offset + 2] = 128;
            pixels[offset + 3] = 128;
        }

    frame->magic = osc_frame_bridge::D3D9FrameMagic;
    frame->version = osc_frame_bridge::D3D9FrameVersion;
    frame->sequence = 1;
    frame->ownerProcessId = static_cast<LONG>(GetCurrentProcessId());
    frame->width = FrameWidth;
    frame->height = FrameHeight;
    frame->rowPitch = FramePitch;
    frame->pixelBytes = static_cast<std::uint32_t>(pixels.size());
    // Counter-only testing deliberately advertises an invisible frame.
    // It verifies the actual D3D9 Present interception without requiring
    // foreground access or counting a renderer-only draw as a hooked frame.
    frame->frameNumber = g_hookCounterOnly ? 0 : 1;
    std::memcpy(frame->pixels, pixels.data(), pixels.size());
    MemoryBarrier();
    InterlockedIncrement(&frame->sequence);

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = WindowClassName;
    if (!RegisterClassExW(&windowClass))
    {
        std::cerr << "RegisterClassExW failed: " << GetLastError() << "\n";
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 6;
    }
    HWND window = CreateWindowExW(0, WindowClassName, L"D3D9 smoke test",
        WS_OVERLAPPEDWINDOW, 100, 100, 160, 160, nullptr, nullptr,
        windowClass.hInstance, nullptr);
    if (!window)
    {
        std::cerr << "CreateWindowExW failed: " << GetLastError() << "\n";
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 7;
    }
    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    HMODULE reshadeModule = nullptr;
    g_reshadeTest = argc == 4 &&
        std::wcscmp(argv[2], L"--reshade") == 0;
    if (g_reshadeTest)
    {
        reshadeModule = LoadLibraryW(argv[3]);
        if (!reshadeModule) { std::cerr << "Could not load ReShade: " << GetLastError() << "\n"; return 15; }
    }
    HMODULE hook = nullptr;
    if (externalAttach)
    {
        // The external x64 host must route to its x86 child and load this DLL.
        for (int attempt = 0; attempt < 1500 && !hook; ++attempt)
        {
            hook = GetModuleHandleW(argv[1]);
            if (!hook) Sleep(10);
        }
        if (hook) LoadLibraryW(argv[1]); // Own one reference for test cleanup.
    }
    else hook = LoadLibraryW(argv[1]);
    if (!hook)
    {
        std::cerr << "LoadLibraryW failed: " << GetLastError() << "\n";
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 8;
    }
    auto getStatus = reinterpret_cast<GetD3D9Status>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9Status"));
    auto getTiming = reinterpret_cast<GetD3D9TimingSnapshot>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9TimingSnapshot"));
    auto getPipeline = reinterpret_cast<GetD3D9PipelineSnapshot>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9PipelineSnapshot"));
    auto renderForTest = reinterpret_cast<RenderD3D9ForTest>(
        GetProcAddress(hook, "NvOscAdapterRenderD3D9ForTest"));
    g_renderForTest = renderForTest;
    g_getAdapterStatus = reinterpret_cast<GetD3D9AdapterStatus>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9AdapterStatus"));
    if (!getStatus || !renderForTest || !g_getAdapterStatus || !getTiming ||
        !getPipeline)
    {
        std::cerr << "D3D9 test exports are missing.\n";
        FreeLibrary(hook);
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 9;
    }

    std::uint64_t presentCount = 0;
    std::uint64_t drawCount = 0;
    BOOL hookInstalled = FALSE;
    for (int attempt = 0; attempt < 500 && !hookInstalled; ++attempt)
    {
        getStatus(&presentCount, &drawCount, &hookInstalled);
        if (!hookInstalled)
            Sleep(10);
    }
    if (!hookInstalled)
    {
        std::cerr << "D3D9 Present hooks were not installed.\n";
        FreeLibrary(hook);
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 10;
    }

    // External attachment starts another process; reacquire our test window
    // before asserting a foreground-only compositor draw.
    if (!g_rendererOnly && !g_hookCounterOnly)
    {
        SetForegroundWindow(window);
        for (int attempt = 0; attempt < 100 && GetForegroundWindow() != window; ++attempt)
        {
            SetForegroundWindow(window);
            Sleep(10);
        }
        if (GetForegroundWindow() != window)
        {
            std::cerr << "DX9 test window could not become foreground.\n";
            return 14;
        }
    }

    D3DPRESENT_PARAMETERS parameters{};
    parameters.BackBufferWidth = FrameWidth;
    parameters.BackBufferHeight = FrameHeight;
    parameters.BackBufferFormat = D3DFMT_UNKNOWN;
    parameters.BackBufferCount = 1;
    parameters.SwapEffect = D3DSWAPEFFECT_COPY;
    parameters.hDeviceWindow = window;
    parameters.Windowed = TRUE;
    parameters.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

    bool passed = true;
    auto create9 = g_reshadeTest
        ? reinterpret_cast<IDirect3D9 *(WINAPI *)(UINT)>(GetProcAddress(reshadeModule, "Direct3DCreate9"))
        : &Direct3DCreate9;
    auto create9Ex = g_reshadeTest
        ? reinterpret_cast<HRESULT(WINAPI *)(UINT, IDirect3D9Ex **)>(GetProcAddress(reshadeModule, "Direct3DCreate9Ex"))
        : &Direct3DCreate9Ex;
    if (!create9 || !create9Ex) return 16;
    IDirect3D9 *baseDirect3d = create9(D3D_SDK_VERSION);
    IDirect3DDevice9 *baseDevice = nullptr;
    HRESULT result = baseDirect3d
        ? baseDirect3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
            window, D3DCREATE_SOFTWARE_VERTEXPROCESSING,
            &parameters, &baseDevice)
        : D3DERR_NOTAVAILABLE;
    if (FAILED(result) || !baseDevice)
    {
        std::cerr << "Could not create a base D3D9 device: 0x" << std::hex
                  << static_cast<unsigned long>(result) << "\n";
        if (baseDevice) baseDevice->Release();
        if (baseDirect3d) baseDirect3d->Release();
        FreeLibrary(hook);
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 11;
    }
    passed = RenderAndVerify(baseDevice) &&
        (g_hookCounterOnly ||
            (VerifyConsumerAdapterInfo() && VerifyRendererIdentity(false)));
    if (passed)
    {
        result = baseDevice->Reset(&parameters);
        passed = SUCCEEDED(result) &&
            RenderAndVerify(baseDevice);
    }
    result = baseDevice->Reset(&parameters);
    baseDevice->Release();
    baseDirect3d->Release();

    IDirect3D9Ex *direct3d = nullptr;
    IDirect3DDevice9Ex *device = nullptr;
    result = create9Ex(D3D_SDK_VERSION, &direct3d);
    if (SUCCEEDED(result))
        result = direct3d->CreateDeviceEx(D3DADAPTER_DEFAULT,
            D3DDEVTYPE_HAL, window, D3DCREATE_SOFTWARE_VERTEXPROCESSING,
            &parameters, nullptr, &device);
    if (FAILED(result) || !device)
    {
        std::cerr << "Could not create a D3D9Ex device: 0x" << std::hex
                  << static_cast<unsigned long>(result) << "\n";
        if (device) device->Release();
        if (direct3d) direct3d->Release();
        FreeLibrary(hook);
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return 12;
    }
    if (passed)
        passed = RenderAndVerify(device);
    if (passed && !g_hookCounterOnly)
        passed = VerifyRendererIdentity(true);
    if (passed)
    {
        result = device->ResetEx(&parameters, nullptr);
        passed = SUCCEEDED(result) && RenderAndVerify(device);
    }
    if (g_rendererOnly && passed)
    {
        // Malformed producer metadata must never escape the mapping bounds.
        // The D3D9 renderer must reject the draw rather than dereference
        // (height-1)*rowPitch beyond the 64 MiB pixel buffer.
        std::uint64_t beforeDraws = 0;
        getStatus(&presentCount, &beforeDraws, &hookInstalled);
        InterlockedIncrement(&frame->sequence);
        frame->rowPitch = 0xFFFFFFFFu;
        frame->frameNumber = 2;
        MemoryBarrier();
        InterlockedIncrement(&frame->sequence);
        const BOOL badFrameDrew = g_renderForTest(device);
        std::uint64_t afterDraws = 0;
        getStatus(&presentCount, &afterDraws, &hookInstalled);
        if (badFrameDrew || afterDraws != beforeDraws)
        {
            std::cerr << "Malformed D3D9 pitch was not rejected.\n";
            passed = false;
        }

        // Recover from the malformed frame without a process restart.
        InterlockedIncrement(&frame->sequence);
        frame->rowPitch = FramePitch;
        frame->frameNumber = 3;
        MemoryBarrier();
        InterlockedIncrement(&frame->sequence);
        if (!ClearFrame(device) || !g_renderForTest(device) ||
            !VerifyOutput(device))
        {
            std::cerr << "D3D9 renderer could not recover a valid frame.\n";
            passed = false;
        }
        else
        {
            ++g_expectedDraws;
        }
    }
    if (g_rendererOnly && passed)
    {
        // An in-flight producer update should not flash the UI invisible:
        // keep the latest known-good texture without waiting for a write.
        std::uint64_t beforeBusy = 0, afterBusy = 0;
        getStatus(&presentCount, &beforeBusy, &hookInstalled);
        if (!ClearFrame(device)) passed = false;
        InterlockedIncrement(&frame->sequence);
        const ULONGLONG startMs = GetTickCount64();
        const BOOL incompleteDrew = g_renderForTest(device);
        const ULONGLONG elapsedMs = GetTickCount64() - startMs;
        InterlockedIncrement(&frame->sequence);
        getStatus(&presentCount, &afterBusy, &hookInstalled);
        if (!incompleteDrew || afterBusy != beforeBusy + 1 ||
            elapsedMs > 250 || !VerifyOutput(device))
        {
            std::cerr << "D3D9 cached frame failed during writer collision: "
                      << elapsedMs << " ms\n";
            passed = false;
        }
        else
        {
            ++g_expectedDraws;
        }

        // A permanently incomplete write must not leave a zombie overlay.
        if (passed)
        {
            if (!ClearFrame(device)) passed = false;
            InterlockedIncrement(&frame->sequence);
            Sleep(290);
            std::uint64_t beforeExpired = 0, afterExpired = 0;
            getStatus(&presentCount, &beforeExpired, &hookInstalled);
            const BOOL expiredDrew = g_renderForTest(device);
            getStatus(&presentCount, &afterExpired, &hookInstalled);
            InterlockedIncrement(&frame->sequence);
            if (expiredDrew || afterExpired != beforeExpired)
            {
                std::cerr << "Expired cached D3D9 frame remained visible.\n";
                passed = false;
            }
        }
    }
    if (g_rendererOnly && passed)
    {
        // A hidden overlay may come back with the SAME published frame ID.
        // The GPU texture must still be uploaded after visibility changed.
        std::uint64_t beforeHidden = 0, afterHidden = 0;
        getStatus(&presentCount, &beforeHidden, &hookInstalled);
        InterlockedIncrement(&frame->sequence);
        frame->frameNumber = 0;
        MemoryBarrier();
        InterlockedIncrement(&frame->sequence);
        const BOOL hiddenDrew = g_renderForTest(device);
        getStatus(&presentCount, &afterHidden, &hookInstalled);
        if (hiddenDrew || afterHidden != beforeHidden)
        {
            std::cerr << "Hidden D3D9 frame unexpectedly rendered.\n";
            passed = false;
        }

        // Change the opaque top marker from green to blue, but REUSE frame 3.
        // A stale texture upload would still show the old green marker.
        const std::size_t offset =
            static_cast<std::size_t>(FrameHeight - 3) * FramePitch + (FrameWidth / 2) * 4;
        InterlockedIncrement(&frame->sequence);
        frame->pixels[offset + 0] = 255;
        frame->pixels[offset + 1] = 0;
        frame->pixels[offset + 2] = 0;
        frame->pixels[offset + 3] = 255;
        frame->frameNumber = 3;
        MemoryBarrier();
        InterlockedIncrement(&frame->sequence);
        if (!ClearFrame(device) || !g_renderForTest(device) ||
            !CheckPixel(device, FrameWidth / 2, 2,
                "hidden-to-visible repeated frame ID", 0, 0, 255, 8))
        {
            std::cerr << "D3D9 repeated-ID frame was not reuploaded.\n";
            passed = false;
        }
        else
        {
            ++g_expectedDraws;
        }
    }
    getStatus(&presentCount, &drawCount, &hookInstalled);
    // Polling diagnostics must be read-only; formerly this data was
    // written synchronously to disk inside every 1,200th game Present.
    duluka::diagnostics::D3D9TimingSnapshotV1 timing{}, second{};
    const bool timingOK = getTiming(&timing, sizeof(timing)) &&
        getTiming(&second, sizeof(second)) &&
        !getTiming(nullptr, sizeof(timing)) &&
        !getTiming(&second, sizeof(second) - 1) &&
        timing.size == sizeof(timing) &&
        timing.version == duluka::diagnostics::D3D9TimingVersion &&
        timing.performanceFrequency != 0 &&
        timing.presents == presentCount &&
        second.presents == timing.presents &&
        timing.draws == drawCount &&
        timing.gamePresentSamples == presentCount &&
        timing.gamePresentTotalTicks >= timing.gamePresentMaxTicks;
    if (!timingOK)
        std::cerr << "D3D9 non-blocking timing snapshot ABI/counters failed.\n";
    passed = passed && timingOK;
    duluka::diagnostics::D3D9PipelineTimingSnapshotV1 pipeline{}, again{};
    const bool pipelineOK = getPipeline(&pipeline, sizeof(pipeline)) &&
        getPipeline(&again, sizeof(again)) &&
        !getPipeline(nullptr, sizeof(pipeline)) &&
        !getPipeline(&again, sizeof(again) - 1) &&
        pipeline.size == sizeof(pipeline) &&
        pipeline.version == duluka::diagnostics::D3D9PipelineTimingVersion &&
        pipeline.performanceFrequency != 0 &&
        pipeline.compositionSamples >= drawCount &&
        pipeline.compositionTotalTicks >= pipeline.compositionMaxTicks &&
        pipeline.snapshotTotalTicks >= pipeline.snapshotMaxTicks &&
        pipeline.uploadTotalTicks >= pipeline.uploadMaxTicks &&
        pipeline.cpuDraws == drawCount &&
        pipeline.gpuExDraws == 0 &&
        (drawCount == 0 || (pipeline.snapshotSamples > 0 &&
            pipeline.uploadAttempts > 0 && pipeline.uploadedBytes > 0 &&
            pipeline.cpuFrameCopiedBytes > 0));
    if (!pipelineOK)
        std::cerr << "D3D9 pipeline diagnostics ABI/counters failed.\n";
    passed = passed && pipelineOK;
    const std::uint64_t expectedHookPresents =
        g_rendererOnly ? 0ull : g_expectedDraws;
    const std::uint64_t expectedRendererDraws =
        g_hookCounterOnly ? 0ull : g_expectedDraws;
    passed = passed && drawCount == expectedRendererDraws &&
        (g_reshadeTest || presentCount == expectedHookPresents);
    if (!passed)
        std::cerr << "D3D9 smoke test failed; present=" << presentCount
                  << " draws=" << drawCount << "\n";
    else
        std::cout << (g_reshadeTest ? "D3D9 ReShade shader-isolation test passed; present=" :
                (g_rendererOnly ? "D3D9 renderer-only pixel/reset test passed; present=" :
                    (g_hookCounterOnly ? "D3D9 Present hook counter-only test passed; present=" :
                    "D3D9 smoke test passed; present="))) << presentCount
                  << " draws=" << drawCount << "\n";

    device->Release();
    direct3d->Release();
    FreeLibrary(hook);
    DestroyWindow(window);
    UnregisterClassW(WindowClassName, windowClass.hInstance);
    UnmapViewOfFile(frame);
    CloseHandle(mapping);
    return passed ? 0 : 13;
}
