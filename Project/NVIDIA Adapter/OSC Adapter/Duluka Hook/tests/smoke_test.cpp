#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <dxgi1_2.h>

#include <algorithm>
#include <cstdint>
#include <iostream>
#include <atomic>
#include <iterator>
#include <string>
#include <vector>
#include "../include/osc_frame_bridge.h"

using GetAdapterStatus = BOOL(WINAPI *)(std::uint64_t *, BOOL *);
using GetResizeStatus = BOOL(WINAPI *)(std::uint64_t *);
using GetOverlayStatus = BOOL(WINAPI *)(std::uint64_t *, BOOL *, BOOL *, long *);
using PresentFunction = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain *, UINT, UINT);
using ResizeBuffersFunction = HRESULT(STDMETHODCALLTYPE *)(
    IDXGISwapChain *, UINT, UINT, UINT, DXGI_FORMAT, UINT);

namespace
{
    constexpr wchar_t WindowClassName[] = L"DulukaNvOscAdapterSmokeTest";
    std::atomic<std::uint64_t> g_recursiveWrapperCalls{ 0 };

    HRESULT STDMETHODCALLTYPE RecursivePresentWrapper(
        IDXGISwapChain *swapChain,
        UINT syncInterval,
        UINT flags)
    {
        g_recursiveWrapperCalls.fetch_add(1, std::memory_order_relaxed);
        return swapChain->Present(syncInterval, flags);
    }

    bool SetBridgeFrameNumber(const wchar_t *bridgeName, std::uint64_t frameNumber)
    {
        HANDLE mapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, bridgeName);
        if (!mapping)
            return false;
        auto *frame = static_cast<osc_frame_bridge::Frame *>(
            MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0,
                osc_frame_bridge::MappingBytes));
        if (!frame)
        {
            CloseHandle(mapping);
            return false;
        }

        InterlockedIncrement(&frame->sequence);
        frame->frameNumber = frameNumber;
        std::atomic_thread_fence(std::memory_order_release);
        InterlockedIncrement(&frame->sequence);
        UnmapViewOfFile(frame);
        CloseHandle(mapping);
        return true;
    }

    int RunTexturePublisher(const wchar_t *bridgeName,
        const wchar_t *stopEventName, const wchar_t *readyEventName)
    {
        if (!SetEnvironmentVariableW(
                osc_frame_bridge::MappingNameEnvironmentVariable, bridgeName))
            return 20;

        HANDLE stopEvent = OpenEventW(SYNCHRONIZE, FALSE, stopEventName);
        HANDLE readyEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, readyEventName);
        if (!stopEvent || !readyEvent)
        {
            if (stopEvent) CloseHandle(stopEvent);
            if (readyEvent) CloseHandle(readyEvent);
            return 21;
        }

        ID3D11Device *device = nullptr;
        ID3D11DeviceContext *context = nullptr;
        D3D_FEATURE_LEVEL featureLevel{};
        HRESULT result = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, &device, &featureLevel, &context);
        if (FAILED(result))
            result = D3D11CreateDevice(
                nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                D3D11_SDK_VERSION, &device, &featureLevel, &context);
        if (FAILED(result))
        {
            CloseHandle(readyEvent);
            CloseHandle(stopEvent);
            return 22;
        }

        std::vector<std::uint8_t> pixels(64 * 64 * 4, 0);
        for (std::size_t i = 0; i < pixels.size(); i += 4)
        {
            pixels[i + 0] = 32;
            pixels[i + 1] = 64;
            pixels[i + 2] = 96;
            pixels[i + 3] = 128;
        }
        for (std::uint32_t y = 0; y < 8; ++y)
        {
            for (std::uint32_t x = 0; x < 64; ++x)
            {
                const std::size_t pixel = (y * 64 + x) * 4;
                pixels[pixel + 0] = 0;
                pixels[pixel + 1] = 0;
                pixels[pixel + 2] = 255;
                pixels[pixel + 3] = 255;
            }
        }
        for (std::uint32_t y = 56; y < 64; ++y)
        {
            for (std::uint32_t x = 0; x < 64; ++x)
            {
                const std::size_t pixel = (y * 64 + x) * 4;
                pixels[pixel + 0] = 255;
                pixels[pixel + 1] = 0;
                pixels[pixel + 2] = 0;
                pixels[pixel + 3] = 255;
            }
        }
        D3D11_TEXTURE2D_DESC textureDesc{};
        textureDesc.Width = 64;
        textureDesc.Height = 64;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        textureDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.Usage = D3D11_USAGE_DEFAULT;
        textureDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA initialData{};
        initialData.pSysMem = pixels.data();
        initialData.SysMemPitch = 64 * 4;
        ID3D11Texture2D *sourceTexture = nullptr;
        result = device->CreateTexture2D(&textureDesc, &initialData,
            &sourceTexture);

        textureDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
            D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        ID3D11Texture2D *sharedTexture = nullptr;
        if (SUCCEEDED(result))
            result = device->CreateTexture2D(&textureDesc, nullptr,
                &sharedTexture);

        IDXGIKeyedMutex *keyedMutex = nullptr;
        if (SUCCEEDED(result))
            result = sharedTexture->QueryInterface(__uuidof(IDXGIKeyedMutex),
                reinterpret_cast<void **>(&keyedMutex));
        if (SUCCEEDED(result))
            result = keyedMutex->AcquireSync(0, INFINITE);
        if (SUCCEEDED(result))
        {
            context->CopyResource(sharedTexture, sourceTexture);
            context->Flush();
            result = keyedMutex->ReleaseSync(0);
        }
        if (keyedMutex) keyedMutex->Release();

        IDXGIResource1 *sharedResource = nullptr;
        HANDLE sharedHandle = nullptr;
        constexpr std::uint64_t sharedResourceId = 1;
        wchar_t sharedResourceName[MAX_PATH]{};
        const bool haveSharedResourceName = osc_frame_bridge::MakeSharedTextureName(
            GetCurrentProcessId(), sharedResourceId, sharedResourceName,
            ARRAYSIZE(sharedResourceName));
        if (SUCCEEDED(result))
            result = sharedTexture->QueryInterface(__uuidof(IDXGIResource1),
                reinterpret_cast<void **>(&sharedResource));
        if (SUCCEEDED(result) && haveSharedResourceName)
            result = sharedResource->CreateSharedHandle(
                nullptr, DXGI_SHARED_RESOURCE_READ, sharedResourceName,
                &sharedHandle);
        else if (SUCCEEDED(result))
            result = E_INVALIDARG;
        if (sharedResource) sharedResource->Release();

        const DWORD mappingHigh = static_cast<DWORD>(
            osc_frame_bridge::MappingBytes >> 32);
        const DWORD mappingLow = static_cast<DWORD>(
            osc_frame_bridge::MappingBytes & 0xFFFFFFFF);
        HANDLE bridgeMapping = nullptr;
        osc_frame_bridge::Frame *bridgeFrame = nullptr;
        if (SUCCEEDED(result))
        {
            bridgeMapping = CreateFileMappingW(
                INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                mappingHigh, mappingLow, bridgeName);
            if (bridgeMapping)
                bridgeFrame = static_cast<osc_frame_bridge::Frame *>(
                    MapViewOfFile(bridgeMapping, FILE_MAP_WRITE, 0, 0,
                        osc_frame_bridge::MappingBytes));
            if (!bridgeFrame)
                result = HRESULT_FROM_WIN32(GetLastError());
        }
        if (FAILED(result) || !bridgeFrame || !sharedHandle)
        {
            if (bridgeFrame) UnmapViewOfFile(bridgeFrame);
            if (bridgeMapping) CloseHandle(bridgeMapping);
            if (sharedHandle) CloseHandle(sharedHandle);
            if (sharedTexture) sharedTexture->Release();
            if (sourceTexture) sourceTexture->Release();
            context->Release();
            device->Release();
            CloseHandle(readyEvent);
            CloseHandle(stopEvent);
            return 23;
        }

        context->Flush();
        bridgeFrame->magic = osc_frame_bridge::Magic;
        bridgeFrame->version = osc_frame_bridge::Version;
        bridgeFrame->sequence = 0;
        bridgeFrame->ownerProcessId = static_cast<LONG>(GetCurrentProcessId());
        InterlockedIncrement(&bridgeFrame->sequence);
        bridgeFrame->width = 64;
        bridgeFrame->height = 64;
        bridgeFrame->processId = GetCurrentProcessId();
        bridgeFrame->sharedResourceId = sharedResourceId;
        bridgeFrame->frameNumber = 1;
        std::atomic_thread_fence(std::memory_order_release);
        InterlockedIncrement(&bridgeFrame->sequence);

        const bool signaled = SetEvent(readyEvent) != FALSE;
        const DWORD waitResult = signaled
            ? WaitForSingleObject(stopEvent, 30000) : WAIT_FAILED;
        UnmapViewOfFile(bridgeFrame);
        CloseHandle(bridgeMapping);
        CloseHandle(sharedHandle);
        sharedTexture->Release();
        sourceTexture->Release();
        context->Release();
        device->Release();
        CloseHandle(readyEvent);
        CloseHandle(stopEvent);
        return waitResult == WAIT_OBJECT_0 ? 0 : 24;
    }

    class TexturePublisherProcess
    {
    public:
        ~TexturePublisherProcess()
        {
            Stop();
        }

        bool Start(const wchar_t *bridgeName)
        {
            const DWORD parentPid = GetCurrentProcessId();
            if (swprintf_s(m_stopEventName, std::size(m_stopEventName),
                    L"Local\\NVIDIAOSC.Frame.Test.Stop.%lu", parentPid) < 0 ||
                swprintf_s(m_readyEventName, std::size(m_readyEventName),
                    L"Local\\NVIDIAOSC.Frame.Test.Ready.%lu", parentPid) < 0)
                return false;

            m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, m_stopEventName);
            m_readyEvent = CreateEventW(nullptr, TRUE, FALSE, m_readyEventName);
            if (!m_stopEvent || !m_readyEvent)
                return false;

            wchar_t executablePath[MAX_PATH]{};
            const DWORD pathLength = GetModuleFileNameW(
                nullptr, executablePath, static_cast<DWORD>(std::size(executablePath)));
            if (!pathLength || pathLength >= std::size(executablePath))
                return false;

            std::wstring commandLine = L"\"" + std::wstring(executablePath) +
                L"\" --publish \"" + bridgeName + L"\" \"" +
                m_stopEventName + L"\" \"" + m_readyEventName + L"\"";
            std::vector<wchar_t> mutableCommandLine(
                commandLine.begin(), commandLine.end());
            mutableCommandLine.push_back(L'\0');
            STARTUPINFOW startupInfo{};
            startupInfo.cb = sizeof(startupInfo);
            PROCESS_INFORMATION processInfo{};
            if (!CreateProcessW(nullptr, mutableCommandLine.data(), nullptr,
                    nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr,
                    &startupInfo, &processInfo))
                return false;

            m_process = processInfo.hProcess;
            CloseHandle(processInfo.hThread);
            return WaitForSingleObject(m_readyEvent, 10000) == WAIT_OBJECT_0;
        }

        void Stop()
        {
            if (m_process)
            {
                if (m_stopEvent)
                    SetEvent(m_stopEvent);
                if (WaitForSingleObject(m_process, 5000) == WAIT_TIMEOUT)
                {
                    TerminateProcess(m_process, 1);
                    WaitForSingleObject(m_process, 5000);
                }
                CloseHandle(m_process);
                m_process = nullptr;
            }
            if (m_readyEvent)
            {
                CloseHandle(m_readyEvent);
                m_readyEvent = nullptr;
            }
            if (m_stopEvent)
            {
                CloseHandle(m_stopEvent);
                m_stopEvent = nullptr;
            }
        }

    private:
        HANDLE m_process = nullptr;
        HANDLE m_stopEvent = nullptr;
        HANDLE m_readyEvent = nullptr;
        wchar_t m_stopEventName[128]{};
        wchar_t m_readyEventName[128]{};
    };

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 5 && wcscmp(argv[1], L"--publish") == 0)
        return RunTexturePublisher(argv[2], argv[3], argv[4]);

    const bool gameDeviceMode = argc == 3 && wcscmp(argv[2], L"--game-device") == 0;
    const bool relayMode = argc == 3 && wcscmp(argv[2], L"--legacy-relay") == 0;
    if (relayMode) SetEnvironmentVariableW(L"NVIDIA_OSC_TEST_FORCE_TEXTURE_RELAY", L"1");
    const bool noBgraMode = gameDeviceMode || (argc == 3 && wcscmp(argv[2], L"--no-bgra") == 0);
    const bool liveBridgeMode = argc == 3 &&
        wcscmp(argv[2], L"--live-bridge") == 0;
    const bool recursiveChainMode = argc == 3 &&
        wcscmp(argv[2], L"--recursive-chain") == 0;
    const bool resizeBaselineMode = argc == 3 &&
        wcscmp(argv[2], L"--resize-baseline") == 0;
    if (argc != 2 && !relayMode && !noBgraMode && !liveBridgeMode && !recursiveChainMode &&
        !resizeBaselineMode)
    {
        std::cerr << "Usage: NVIDIA OSC Adapter Smoke Test.exe <adapter-dll> "
            << "[--live-bridge|--recursive-chain|--resize-baseline|--no-bgra]\n";
        return 2;
    }

    wchar_t bridgeName[128]{};
    if (liveBridgeMode)
    {
        if (!SetEnvironmentVariableW(
                osc_frame_bridge::MappingNameEnvironmentVariable, nullptr))
        {
            std::cerr << "Could not select the live OSC bridge: "
                << GetLastError() << "\n";
            return 2;
        }
    }
    else
    {
        if (swprintf_s(bridgeName, std::size(bridgeName),
                L"Local\\NVIDIAOSC.Frame.Test.%lu", GetCurrentProcessId()) < 0)
        {
            std::cerr << "Could not format an isolated test bridge name.\n";
            return 2;
        }
        if (!SetEnvironmentVariableW(
                osc_frame_bridge::MappingNameEnvironmentVariable, bridgeName))
        {
            std::cerr << "Could not configure an isolated test bridge: "
                << GetLastError() << "\n";
            return 2;
        }
    }

    wchar_t consumerAdapterMappingName[128]{};
    if (swprintf_s(consumerAdapterMappingName,
            std::size(consumerAdapterMappingName),
            L"Local\\NVIDIAOSC.Frame.Test.ConsumerAdapter.%lu",
            GetCurrentProcessId()) < 0 ||
        !SetEnvironmentVariableW(
            osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
            consumerAdapterMappingName))
    {
        std::cerr << "Could not configure an isolated test adapter identity map.\n";
        return 2;
    }

    TexturePublisherProcess publisher;
    if (!liveBridgeMode && !publisher.Start(bridgeName))
    {
        std::cerr << "Could not start the isolated shared-texture publisher.\n";
        return 2;
    }

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = WindowClassName;
    if (!RegisterClassExW(&windowClass))
    {
        std::cerr << "RegisterClassExW failed: " << GetLastError() << "\n";
        return 6;
    }

    HWND window = CreateWindowExW(
        0, WindowClassName, L"", WS_OVERLAPPED,
        0, 0, 64, 64, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (!window)
    {
        std::cerr << "CreateWindowExW failed: " << GetLastError() << "\n";
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 7;
    }

    DXGI_SWAP_CHAIN_DESC swapChainDesc{};
    swapChainDesc.BufferDesc.Width = 64;
    swapChainDesc.BufferDesc.Height = 64;
    swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapChainDesc.SampleDesc.Count = 1;
    swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDesc.BufferCount = 1;
    swapChainDesc.OutputWindow = window;
    swapChainDesc.Windowed = TRUE;
    swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_SEQUENTIAL;

    ID3D11Device *device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    IDXGISwapChain *swapChain = nullptr;
    D3D_FEATURE_LEVEL featureLevel{};
    const D3D_FEATURE_LEVEL gameFeatureLevel = D3D_FEATURE_LEVEL_11_1;
    HRESULT result = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, noBgraMode ? 0 : D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        gameDeviceMode ? &gameFeatureLevel : nullptr, gameDeviceMode ? 1 : 0, D3D11_SDK_VERSION, &swapChainDesc,
        &swapChain, &device, &featureLevel, &context);
    if (FAILED(result))
    {
        result = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr, noBgraMode ? 0 : D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &swapChainDesc,
            &swapChain, &device, &featureLevel, &context);
    }
    if (FAILED(result))
    {
        std::cerr << "D3D11CreateDeviceAndSwapChain failed: 0x"
            << std::hex << static_cast<unsigned long>(result) << "\n";
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 8;
    }

    void **testPresentSlot =
        &(*reinterpret_cast<void ***>(swapChain))[8];
    const auto originalTestPresent =
        reinterpret_cast<PresentFunction>(*testPresentSlot);
    void **testResizeBuffersSlot =
        &(*reinterpret_cast<void ***>(swapChain))[13];
    const auto originalTestResizeBuffers =
        reinterpret_cast<ResizeBuffersFunction>(*testResizeBuffersSlot);
    if (recursiveChainMode)
    {
        DWORD oldProtection = 0;
        if (!VirtualProtect(testPresentSlot, sizeof(void *),
                PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            std::cerr << "Could not prepare the recursive Present wrapper: "
                << GetLastError() << "\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 25;
        }
        const auto previous = InterlockedExchangePointer(
            reinterpret_cast<PVOID volatile *>(testPresentSlot),
            reinterpret_cast<void *>(&RecursivePresentWrapper));
        DWORD ignoredProtection = 0;
        VirtualProtect(testPresentSlot, sizeof(void *), oldProtection,
            &ignoredProtection);
        if (previous != reinterpret_cast<void *>(originalTestPresent))
        {
            std::cerr << "The test swap-chain Present slot changed unexpectedly.\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 25;
        }
    }

    HMODULE adapter = LoadLibraryW(argv[1]);
    if (!adapter)
    {
        std::cerr << "LoadLibraryW failed: " << GetLastError() << "\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 3;
    }

    auto getStatus = reinterpret_cast<GetAdapterStatus>(
        GetProcAddress(adapter, "NvOscAdapterGetStatus"));
    auto getResizeStatus = reinterpret_cast<GetResizeStatus>(
        GetProcAddress(adapter, "NvOscAdapterGetResizeStatus"));
    auto getOverlayStatus = reinterpret_cast<GetOverlayStatus>(
        GetProcAddress(adapter, "NvOscAdapterGetOverlayStatus"));
    if (!getStatus || !getResizeStatus || !getOverlayStatus)
    {
        std::cerr << "Adapter status exports are missing.\n";
        return 4;
    }

    BOOL installed = FALSE;
    std::uint64_t count = 0;
    for (int attempt = 0; attempt < 100 && !installed; ++attempt)
    {
        getStatus(&count, &installed);
        Sleep(50);
    }
    if (!installed)
    {
        std::cerr << "Adapter did not install the DXGI Present hook.\n";
        return 5;
    }

    if (resizeBaselineMode)
    {
        DWORD oldProtection = 0;
        if (!VirtualProtect(testResizeBuffersSlot,
                sizeof(void *), PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            std::cerr << "Could not prepare the unhooked ResizeBuffers baseline: "
                << GetLastError() << "\n";
            return 25;
        }
        InterlockedExchangePointer(
            reinterpret_cast<PVOID volatile *>(testResizeBuffersSlot),
            reinterpret_cast<void *>(originalTestResizeBuffers));
        DWORD ignoredProtection = 0;
        VirtualProtect(testResizeBuffersSlot, sizeof(void *),
            oldProtection, &ignoredProtection);
    }

    const wchar_t *activeBridgeName = liveBridgeMode
        ? osc_frame_bridge::MappingName : bridgeName;
    HANDLE bridgeMapping = nullptr;
    const osc_frame_bridge::Frame *bridgeFrame = nullptr;
    for (int attempt = 0; attempt < 100 && !bridgeFrame; ++attempt)
    {
        if (!bridgeMapping)
            bridgeMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, activeBridgeName);
        if (bridgeMapping)
            bridgeFrame = static_cast<const osc_frame_bridge::Frame *>(
                MapViewOfFile(bridgeMapping, FILE_MAP_READ, 0, 0,
                    osc_frame_bridge::MappingBytes));
        if (!bridgeFrame)
            Sleep(50);
    }
    if (!bridgeFrame)
    {
        std::cerr << "Could not read the "
            << (liveBridgeMode ? "live OSC" : "test publisher")
            << " bridge: " << GetLastError() << "\n";
        if (bridgeMapping) CloseHandle(bridgeMapping);
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 12;
    }
    DWORD publisherPid = 0;
    std::uint64_t publisherResourceId = 0;
    std::uint32_t bridgeWidth = 0;
    std::uint32_t bridgeHeight = 0;
    std::uint64_t bridgeFrameNumber = 0;
    for (int attempt = 0; attempt < 100; ++attempt)
    {
        const LONG before = bridgeFrame->sequence;
        if (!(before & 1))
        {
            std::atomic_thread_fence(std::memory_order_acquire);
            const DWORD candidatePid = bridgeFrame->processId;
            const LONG ownerPid = bridgeFrame->ownerProcessId;
            const auto candidateResourceId = bridgeFrame->sharedResourceId;
            const auto candidateWidth = bridgeFrame->width;
            const auto candidateHeight = bridgeFrame->height;
            const auto candidateFrameNumber = bridgeFrame->frameNumber;
            std::atomic_thread_fence(std::memory_order_acquire);
            if (before == bridgeFrame->sequence && candidatePid &&
                ownerPid == static_cast<LONG>(candidatePid) &&
                candidateResourceId && candidateWidth && candidateHeight)
            {
                publisherPid = candidatePid;
                publisherResourceId = candidateResourceId;
                bridgeWidth = candidateWidth;
                bridgeHeight = candidateHeight;
                bridgeFrameNumber = candidateFrameNumber;
                break;
            }
        }
        Sleep(50);
    }
    UnmapViewOfFile(bridgeFrame);
    CloseHandle(bridgeMapping);
    if (!publisherPid || !publisherResourceId)
    {
        std::cerr << "The selected bridge has no stable published frame.\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 12;
    }
    ID3D11Device1 *device1 = nullptr;
    ID3D11Resource *sharedResource = nullptr;
    wchar_t sharedResourceName[MAX_PATH]{};
    result = osc_frame_bridge::MakeSharedTextureName(publisherPid,
        publisherResourceId, sharedResourceName, ARRAYSIZE(sharedResourceName))
        ? device->QueryInterface(__uuidof(ID3D11Device1),
            reinterpret_cast<void **>(&device1))
        : E_INVALIDARG;
    if (SUCCEEDED(result))
        result = device1->OpenSharedResourceByName(sharedResourceName,
            DXGI_SHARED_RESOURCE_READ, __uuidof(ID3D11Resource),
            reinterpret_cast<void **>(&sharedResource));
    if (device1) device1->Release();
    if (FAILED(result) || !sharedResource)
    {
        std::cerr << "Consumer could not independently open publisher texture: 0x"
            << std::hex << static_cast<unsigned long>(result)
            << " pid=" << std::dec << publisherPid
            << " resourceId=" << publisherResourceId << "\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 16;
    }
    ID3D11Texture2D *publisherTexture = nullptr;
    result = sharedResource->QueryInterface(__uuidof(ID3D11Texture2D),
        reinterpret_cast<void **>(&publisherTexture));
    sharedResource->Release();
    IDXGIKeyedMutex *publisherMutex = nullptr;
    if (SUCCEEDED(result))
        publisherTexture->QueryInterface(__uuidof(IDXGIKeyedMutex),
            reinterpret_cast<void **>(&publisherMutex));
    bool publisherMutexAcquired = false;
    if (SUCCEEDED(result) && publisherMutex)
    {
        result = publisherMutex->AcquireSync(0, 1000);
        publisherMutexAcquired = SUCCEEDED(result);
    }
    D3D11_TEXTURE2D_DESC publisherTextureDesc{};
    if (SUCCEEDED(result))
        publisherTexture->GetDesc(&publisherTextureDesc);
    const bool supportedPixelFormat =
        publisherTextureDesc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS ||
        publisherTextureDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
        publisherTextureDesc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS ||
        publisherTextureDesc.Format == DXGI_FORMAT_R8G8B8A8_UNORM;
    if (SUCCEEDED(result) &&
        (!publisherTextureDesc.Width || !publisherTextureDesc.Height ||
            !supportedPixelFormat))
        result = E_INVALIDARG;
    D3D11_TEXTURE2D_DESC publisherReadbackDesc = publisherTextureDesc;
    publisherReadbackDesc.Usage = D3D11_USAGE_STAGING;
    publisherReadbackDesc.BindFlags = 0;
    publisherReadbackDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    publisherReadbackDesc.MiscFlags = 0;
    ID3D11Texture2D *publisherReadback = nullptr;
    if (SUCCEEDED(result))
        result = device->CreateTexture2D(
            &publisherReadbackDesc, nullptr, &publisherReadback);
    if (SUCCEEDED(result))
        context->CopyResource(publisherReadback, publisherTexture);
    D3D11_MAPPED_SUBRESOURCE publisherMapped{};
    if (SUCCEEDED(result))
        result = context->Map(publisherReadback, 0, D3D11_MAP_READ, 0,
            &publisherMapped);
    std::uint8_t publisherPixel[4]{};
    std::uint64_t nonTransparentPixels = 0;
    std::uint8_t minAlpha = 255;
    std::uint8_t maxAlpha = 0;
    std::uint32_t contentLeft = publisherTextureDesc.Width;
    std::uint32_t contentTop = publisherTextureDesc.Height;
    std::uint32_t contentRight = 0;
    std::uint32_t contentBottom = 0;
    if (SUCCEEDED(result))
    {
        const std::uint32_t sampleX = std::min(
            publisherTextureDesc.Width - 1, publisherTextureDesc.Width / 2);
        const std::uint32_t sampleY = std::min(
            publisherTextureDesc.Height - 1, publisherTextureDesc.Height / 2);
        const auto *pixel = static_cast<const std::uint8_t *>(
            publisherMapped.pData) +
            (sampleY * publisherMapped.RowPitch) + (sampleX * 4);
        std::copy_n(pixel, 4, publisherPixel);
        for (std::uint32_t y = 0; y < publisherTextureDesc.Height; ++y)
        {
            const auto *row = static_cast<const std::uint8_t *>(
                publisherMapped.pData) + (y * publisherMapped.RowPitch);
            for (std::uint32_t x = 0; x < publisherTextureDesc.Width; ++x)
            {
                const std::uint8_t alpha = row[x * 4 + 3];
                minAlpha = std::min(minAlpha, alpha);
                maxAlpha = std::max(maxAlpha, alpha);
                if (!alpha)
                    continue;
                ++nonTransparentPixels;
                contentLeft = std::min(contentLeft, x);
                contentTop = std::min(contentTop, y);
                contentRight = std::max(contentRight, x);
                contentBottom = std::max(contentBottom, y);
            }
        }
        context->Unmap(publisherReadback, 0);
    }
    if (publisherMutexAcquired)
        publisherMutex->ReleaseSync(0);
    if (publisherMutex) publisherMutex->Release();
    if (publisherReadback) publisherReadback->Release();
    if (publisherTexture) publisherTexture->Release();
    if (FAILED(result))
    {
        std::cerr << "Could not inspect the shared publisher texture: 0x"
            << std::hex << static_cast<unsigned long>(result) << "\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 17;
    }
    std::cout << "Publisher texture PID=" << publisherPid
        << " bridge=" << bridgeWidth << "x" << bridgeHeight
        << " frame=" << bridgeFrameNumber
        << " texture=" << publisherTextureDesc.Width << "x"
        << publisherTextureDesc.Height
        << " format=" << static_cast<unsigned>(publisherTextureDesc.Format)
        << " center BGRA="
        << static_cast<unsigned>(publisherPixel[0]) << ","
        << static_cast<unsigned>(publisherPixel[1]) << ","
        << static_cast<unsigned>(publisherPixel[2]) << ","
        << static_cast<unsigned>(publisherPixel[3])
        << " alphaRange=" << static_cast<unsigned>(minAlpha) << "-"
        << static_cast<unsigned>(maxAlpha)
        << " nonTransparent=" << nonTransparentPixels;
    if (nonTransparentPixels)
        std::cout << " bounds=(" << contentLeft << "," << contentTop << ")-("
            << contentRight << "," << contentBottom << ")";
    std::cout << "\n";
    if (!liveBridgeMode &&
        (publisherPixel[0] != 32 || publisherPixel[1] != 64 ||
            publisherPixel[2] != 96 || publisherPixel[3] != 128))
    {
        std::cerr << "Cross-process publisher texture pixels do not match.\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 18;
    }

    ID3D11Texture2D *backBuffer = nullptr;
    ID3D11RenderTargetView *backBufferView = nullptr;
    ID3D11Texture2D *readback = nullptr;
    result = swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
        reinterpret_cast<void **>(&backBuffer));
    D3D11_TEXTURE2D_DESC backDesc{};
    if (SUCCEEDED(result))
    {
        backBuffer->GetDesc(&backDesc);
        result = device->CreateRenderTargetView(backBuffer, nullptr, &backBufferView);
    }
    if (SUCCEEDED(result))
    {
        backDesc.Usage = D3D11_USAGE_STAGING;
        backDesc.BindFlags = 0;
        backDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        backDesc.MiscFlags = 0;
        result = device->CreateTexture2D(&backDesc, nullptr, &readback);
    }
    if (FAILED(result))
    {
        std::cerr << "Could not prepare swap-chain pixel verification.\n";
        if (readback) readback->Release();
        if (backBufferView) backBufferView->Release();
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 14;
    }

    for (int frame = 0; frame < 5; ++frame)
    {
        constexpr float clearColor[4] = { 0, 0, 0, 1 };
        context->ClearRenderTargetView(backBufferView, clearColor);
        result = swapChain->Present(0, 0);
        if (FAILED(result))
        {
            std::cerr << "Present failed: 0x"
                << std::hex << static_cast<unsigned long>(result) << "\n";
            readback->Release();
            backBufferView->Release();
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 9;
        }
    }

    context->CopyResource(readback, backBuffer);
    std::uint64_t overlayDrawCount = 0;
    BOOL bridgeConnected = FALSE;
    BOOL overlayTextureReady = FALSE;
    long lastOverlayError = 0;
    getOverlayStatus(&overlayDrawCount, &bridgeConnected,
        &overlayTextureReady, &lastOverlayError);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    result = context->Map(readback, 0, D3D11_MAP_READ, 0, &mapped);
    bool pixelMatches = false;
    bool orientationMatches = liveBridgeMode;
    std::uint8_t observedPixel[4]{};
    std::uint8_t topPixel[4]{};
    std::uint8_t bottomPixel[4]{};
    std::uint64_t visibleBackBufferPixels = 0;
    std::uint8_t maxBackBufferChannel = 0;
    if (SUCCEEDED(result))
    {
        const auto *pixel = static_cast<const std::uint8_t *>(mapped.pData) +
            (32 * mapped.RowPitch) + (32 * 4);
        std::copy_n(pixel, 4, observedPixel);
        if (!liveBridgeMode)
        {
            const auto *top = static_cast<const std::uint8_t *>(mapped.pData) +
                (4 * mapped.RowPitch) + (32 * 4);
            const auto *bottom = static_cast<const std::uint8_t *>(mapped.pData) +
                (59 * mapped.RowPitch) + (32 * 4);
            std::copy_n(top, 4, topPixel);
            std::copy_n(bottom, 4, bottomPixel);
            orientationMatches =
                topPixel[0] < 8 && topPixel[1] < 8 && topPixel[2] > 240 &&
                bottomPixel[0] > 240 && bottomPixel[1] < 8 && bottomPixel[2] < 8;
        }
        pixelMatches = !liveBridgeMode &&
            (pixel[0] >= 90 && pixel[0] <= 102 &&
                pixel[1] >= 58 && pixel[1] <= 70 &&
                pixel[2] >= 26 && pixel[2] <= 38);
        for (std::uint32_t y = 0; y < backDesc.Height; ++y)
        {
            const auto *row = static_cast<const std::uint8_t *>(
                mapped.pData) + (y * mapped.RowPitch);
            for (std::uint32_t x = 0; x < backDesc.Width; ++x)
            {
                const auto *rgba = row + x * 4;
                const std::uint8_t brightest = std::max(
                    rgba[0], std::max(rgba[1], rgba[2]));
                maxBackBufferChannel = std::max(maxBackBufferChannel, brightest);
                if (brightest > 2)
                    ++visibleBackBufferPixels;
            }
        }
        if (liveBridgeMode)
            pixelMatches = visibleBackBufferPixels > 0 &&
                maxBackBufferChannel > 8;
        pixelMatches = pixelMatches && orientationMatches;
        context->Unmap(readback, 0);
    }
    if (backBuffer) backBuffer->Release();
    readback->Release();
    backBufferView->Release();
    if (!pixelMatches)
    {
        std::cerr << "Composited test pixel mismatch: "
            << static_cast<unsigned>(observedPixel[0]) << ","
            << static_cast<unsigned>(observedPixel[1]) << ","
            << static_cast<unsigned>(observedPixel[2]) << ","
            << static_cast<unsigned>(observedPixel[3]) << "; connected="
            << bridgeConnected << " texture=" << overlayTextureReady
            << " draws=" << overlayDrawCount << " lastError=0x"
            << std::hex << lastOverlayError << std::dec
            << " visiblePixels=" << visibleBackBufferPixels
            << " maxChannel=" << static_cast<unsigned>(maxBackBufferChannel)
            << " topRGBA=" << static_cast<unsigned>(topPixel[0]) << ","
            << static_cast<unsigned>(topPixel[1]) << ","
            << static_cast<unsigned>(topPixel[2]) << ","
            << static_cast<unsigned>(topPixel[3])
            << " bottomRGBA=" << static_cast<unsigned>(bottomPixel[0]) << ","
            << static_cast<unsigned>(bottomPixel[1]) << ","
            << static_cast<unsigned>(bottomPixel[2]) << ","
            << static_cast<unsigned>(bottomPixel[3])
            << ".\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 15;
    }

    if (!liveBridgeMode)
    {
        if (!SetBridgeFrameNumber(bridgeName, 0) ||
            FAILED(swapChain->Present(0, 0)))
        {
            std::cerr << "Could not publish the hidden-frame test state.\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 21;
        }

        std::uint64_t drawsWhileHidden = 0;
        getOverlayStatus(&drawsWhileHidden, &bridgeConnected,
            &overlayTextureReady, &lastOverlayError);
        if (drawsWhileHidden != overlayDrawCount)
        {
            std::cerr << "Adapter rendered the cached OSC frame while hidden.\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 22;
        }

        if (!SetBridgeFrameNumber(bridgeName, 2) ||
            FAILED(swapChain->Present(0, 0)))
        {
            std::cerr << "Could not publish the visible-frame test state.\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 23;
        }
        std::uint64_t drawsAfterUnhide = 0;
        getOverlayStatus(&drawsAfterUnhide, &bridgeConnected,
            &overlayTextureReady, &lastOverlayError);
        if (drawsAfterUnhide <= drawsWhileHidden)
        {
            std::cerr << "Adapter did not resume composition after the frame became visible.\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 24;
        }
        overlayDrawCount = drawsAfterUnhide;
        std::cout << "PASS: hidden frames are skipped and composition resumes when visible.\n";
    }

    getStatus(&count, &installed);

    if (!installed || count < 5)
    {
        std::cerr << "Expected at least 5 hooked Present callbacks; got " << count << ".\n";
        return 10;
    }

    if (recursiveChainMode)
    {
        const auto wrapperCalls =
            g_recursiveWrapperCalls.load(std::memory_order_relaxed);
        if (wrapperCalls < 5 || count != wrapperCalls * 2)
        {
            std::cerr << "Recursive Present chain did not terminate through one "
                << "unhooked DXGI call per wrapper: callbacks=" << count
                << " wrappers=" << wrapperCalls << ".\n";
            return 26;
        }
        std::cout << "PASS: recursive wrapper chain forwarded each frame once "
            << "through DXGI without unbounded re-entry.\n";
    }

    if (!bridgeConnected || !overlayTextureReady || overlayDrawCount < 5)
    {
        std::cerr << "Overlay bridge did not draw the shared texture: connected="
            << bridgeConnected << " texture=" << overlayTextureReady
            << " draws=" << overlayDrawCount << " lastError=0x"
            << std::hex << lastOverlayError << ".\n";
        return 13;
    }

    if (!liveBridgeMode)
    {
        publisher.Stop();
        result = swapChain->Present(0, 0);
        if (FAILED(result))
        {
            std::cerr << "Present failed after publisher exit: 0x"
                << std::hex << static_cast<unsigned long>(result) << "\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 19;
        }
        std::uint64_t drawsAfterPublisherExit = 0;
        BOOL bridgeStillConnected = FALSE;
        BOOL textureStillReady = TRUE;
        long exitError = 0;
        getOverlayStatus(&drawsAfterPublisherExit, &bridgeStillConnected,
            &textureStillReady, &exitError);
        if (textureStillReady || drawsAfterPublisherExit != overlayDrawCount)
        {
            std::cerr << "Adapter did not release the texture after publisher exit: ready="
                << textureStillReady << " draws=" << drawsAfterPublisherExit << ".\n";
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            UnregisterClassW(WindowClassName, windowClass.hInstance);
            return 20;
        }
        std::cout << "PASS: adapter released the source texture after publisher exit.\n";
    }

    std::uint64_t resizeCountBefore = 0;
    std::uint64_t resizeCountAfter = 0;
    context->ClearState();
    const bool gotResizeCountBefore = getResizeStatus(&resizeCountBefore);
    const HRESULT resizeResult = swapChain->ResizeBuffers(
        1, 80, 80, DXGI_FORMAT_UNKNOWN, 0);
    const bool gotResizeCountAfter = getResizeStatus(&resizeCountAfter);
    const bool resizeHookObserved =
        resizeCountAfter == resizeCountBefore + 1;
    if (!gotResizeCountBefore || FAILED(resizeResult) ||
        !gotResizeCountAfter || resizeHookObserved)
    {
        std::cerr << "ResizeBuffers pass-through regression failed: "
            << "before=" << resizeCountBefore
            << " after=" << resizeCountAfter
            << " result=0x" << std::hex
            << static_cast<unsigned long>(resizeResult) << ".\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 27;
    }
    DXGI_SWAP_CHAIN_DESC resizedDesc{};
    if (FAILED(swapChain->GetDesc(&resizedDesc)) ||
        resizedDesc.BufferDesc.Width != 80 ||
        resizedDesc.BufferDesc.Height != 80 ||
        FAILED(swapChain->Present(0, 0)))
    {
        std::cerr << "The swap chain did not present after resizing.\n";
        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        UnregisterClassW(WindowClassName, windowClass.hInstance);
        return 28;
    }
    getStatus(&count, &installed);
    std::cout << "PASS: ResizeBuffers remained pass-through and the "
        << "swap chain resumed at 80x80.\n";

    if (recursiveChainMode)
    {
        DWORD oldProtection = 0;
        if (VirtualProtect(testPresentSlot, sizeof(void *),
                PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            InterlockedExchangePointer(
                reinterpret_cast<PVOID volatile *>(testPresentSlot),
                reinterpret_cast<void *>(originalTestPresent));
            DWORD ignoredProtection = 0;
            VirtualProtect(testPresentSlot, sizeof(void *), oldProtection,
                &ignoredProtection);
        }
    }
    swapChain->Release();
    context->Release();
    device->Release();
    DestroyWindow(window);
    UnregisterClassW(WindowClassName, windowClass.hInstance);

    FreeLibrary(adapter);
    std::cout << "PASS: hooked " << count << " Presents and composited "
        << overlayDrawCount << " shared-texture frames.\n";
    if (liveBridgeMode)
        std::cout << "PASS: live OSC surface produced "
            << visibleBackBufferPixels << " visible back-buffer pixels (max channel "
            << static_cast<unsigned>(maxBackBufferChannel) << ").\n";
    return 0;
}
