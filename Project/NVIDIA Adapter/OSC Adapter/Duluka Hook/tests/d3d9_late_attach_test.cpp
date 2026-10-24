// A real D3D9 device exists BEFORE the Hook DLL is loaded.
// This catches late-attachment regressions without needing foreground access.
#include <windows.h>
#include <d3d9.h>
#include <cstdint>
#include <iostream>

using GetD3D9Status = BOOL(WINAPI *)(std::uint64_t *, std::uint64_t *, BOOL *);
using RegisterD3D9Device = BOOL(WINAPI *)(IDirect3DDevice9 *);
using UnregisterD3D9Device = BOOL(WINAPI *)(IDirect3DDevice9 *);

LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int wmain(int argc, wchar_t **argv)
{
    const bool cooperative = argc == 3 &&
        (std::wcscmp(argv[2], L"--cooperative") == 0 ||
         std::wcscmp(argv[2], L"--cooperative-ex") == 0);
    const bool automatic = argc == 3 &&
        (std::wcscmp(argv[2], L"--automatic") == 0 ||
         std::wcscmp(argv[2], L"--automatic-ex") == 0);
    const bool useEx = argc == 3 &&
        (std::wcscmp(argv[2], L"--cooperative-ex") == 0 ||
         std::wcscmp(argv[2], L"--automatic-ex") == 0);
    if (argc != 2 && !cooperative && !automatic) return 2;
    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSW klass{};
    klass.hInstance = instance;
    klass.lpfnWndProc = WindowProc;
    klass.lpszClassName = L"DulukaD3D9LateAttachFixture";
    if (!RegisterClassW(&klass)) return 3;
    HWND window = CreateWindowExW(0, klass.lpszClassName, L"DX9 late attach",
        WS_OVERLAPPED, 10, 10, 96, 96, nullptr, nullptr, instance, nullptr);
    if (!window) return 4;

    IDirect3D9 *factory = nullptr;
    IDirect3D9Ex *factoryEx = nullptr;
    if (useEx)
    {
        if (FAILED(Direct3DCreate9Ex(D3D_SDK_VERSION, &factoryEx)))
            return 5;
        factory = factoryEx;
    }
    else factory = Direct3DCreate9(D3D_SDK_VERSION);
    if (!factory) return 5;
    D3DPRESENT_PARAMETERS pp{};
    pp.BackBufferWidth = 64;
    pp.BackBufferHeight = 64;
    pp.BackBufferCount = 1;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = window;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

    IDirect3DDevice9 *device = nullptr;
    IDirect3DDevice9Ex *deviceEx = nullptr;
    HRESULT hr = useEx
        ? factoryEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
            window, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, nullptr,
            &deviceEx)
        : factory->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
            window, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &device);
    if (useEx) device = deviceEx;
    if (FAILED(hr) || !device)
    {
        std::cerr << "Could not create early D3D9 fixture device: 0x"
                  << std::hex << static_cast<unsigned long>(hr) << "\n";
        factory->Release();
        DestroyWindow(window);
        return 6;
    }
    void **earlyVtable = *reinterpret_cast<void ***>(device);
    void *originalPresentSlot = earlyVtable[17];
    void *originalPresentExSlot = useEx ? earlyVtable[121] : nullptr;
    std::cout << "Early D3D9 device vtable=" << earlyVtable
              << " presentBefore=" << earlyVtable[17] << "\n";
    // This must happen AFTER CreateDevice. An import-hook-only design
    // cannot intercept Present on this already-existing device.
    if (automatic)
        SetEnvironmentVariableW(L"DULUKA_D3D9_FORCE_LATE_ATTACH", L"1");
    HMODULE hook = LoadLibraryW(argv[1]);

    if (!hook)
    {
        std::cerr << "Could not load late Hook DLL: " << GetLastError() << "\n";
        device->Release();
        factory->Release();
        DestroyWindow(window);
        return 7;
    }
    const auto status = reinterpret_cast<GetD3D9Status>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9Status"));
    if (!status)
    {
        std::cerr << "Missing adapter D3D9 status export\n";
        device->Release();
        factory->Release();
        FreeLibrary(hook);
        DestroyWindow(window);
        return 8;
    }

    std::uint64_t initialPresents = 0, draws = 0;
    BOOL installed = FALSE;
    for (int i = 0; i < 250; ++i)
    {
        status(&initialPresents, &draws, &installed);
        if (installed) break;
        Sleep(20);
    }
    if (automatic)
        SetEnvironmentVariableW(L"DULUKA_D3D9_FORCE_LATE_ATTACH", nullptr);
    if (!installed)
    {
        std::cerr << "D3D9 Hook did not initialize\n";
        device->Release();
        factory->Release();
        FreeLibrary(hook);
        DestroyWindow(window);
        return 9;
    }
    // A cooperating host can explicitly register its actual preexisting
    // device. This does not pretend to discover arbitrary game devices.
    if (cooperative)
    {
        const auto registerDevice = reinterpret_cast<RegisterD3D9Device>(
            GetProcAddress(hook, "NvOscAdapterRegisterD3D9Device"));
        if (!registerDevice || registerDevice(nullptr) ||
            !registerDevice(device) || !registerDevice(device))
        {
            std::cerr << "Cooperative device registration/idempotence failed\n";
            device->Release();
            factory->Release();
            FreeLibrary(hook);
            DestroyWindow(window);
            return 11;
        }
    }

    void **currentVtable = *reinterpret_cast<void ***>(device);
    std::cout << "After Hook DLL device vtable=" << currentVtable
              << " presentAfter=" << currentVtable[17] << "\n";
    constexpr std::uint64_t BeforeResetPresents = 3;
    constexpr std::uint64_t ExpectedPresents = 4;
    bool presentOk = true;
    for (unsigned i = 0; i < BeforeResetPresents; ++i)
    {
        hr = useEx
            ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
            : device->Present(nullptr, nullptr, nullptr, nullptr);
        if (FAILED(hr)) { presentOk = false; break; }
    }
    if (presentOk)
    {
        hr = useEx ? deviceEx->ResetEx(&pp, nullptr) : device->Reset(&pp);
        presentOk = SUCCEEDED(hr);
    }
    if (presentOk)
    {
        hr = useEx
            ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
            : device->Present(nullptr, nullptr, nullptr, nullptr);
        presentOk = SUCCEEDED(hr);
    }
    std::uint64_t finalPresents = 0;
    status(&finalPresents, &draws, &installed);
    bool pass = presentOk &&
        finalPresents - initialPresents == ExpectedPresents;
    if (cooperative)
    {
        const auto unregisterDevice = reinterpret_cast<UnregisterD3D9Device>(
            GetProcAddress(hook, "NvOscAdapterUnregisterD3D9Device"));
        const bool unregistered = unregisterDevice &&
            !unregisterDevice(nullptr) &&
            unregisterDevice(device) && !unregisterDevice(device);
        const bool restored =
            earlyVtable[17] == originalPresentSlot &&
            (!useEx || earlyVtable[121] == originalPresentExSlot);
        const HRESULT postDetachPresent = useEx
            ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
            : device->Present(nullptr, nullptr, nullptr, nullptr);
        std::uint64_t afterDetachCount = 0;
        status(&afterDetachCount, &draws, &installed);
        const bool stopped = SUCCEEDED(postDetachPresent) &&
            afterDetachCount == finalPresents;
        std::cout << "D3D9 cooperative unregister="
                  << (unregistered ? "PASS" : "FAIL")
                  << " restored=" << (restored ? "true" : "false")
                  << " no-extra-callback=" << (stopped ? "true" : "false")
                  << "\n";
        pass = pass && unregistered && restored && stopped;
    }
    std::cout << (cooperative
                ? (useEx ? "D3D9Ex cooperative late attach " :
                           "D3D9 cooperative late attach ")
                : (automatic ? "D3D9 implementation automatic late attach "
                             : "D3D9 legacy late attach "))
              << (pass ? "PASS" : "FAIL")
              << " presents=" << finalPresents - initialPresents
              << " expected=" << ExpectedPresents << "\n";

    device->Release();
    factory->Release();
    FreeLibrary(hook);
    DestroyWindow(window);
    UnregisterClassW(klass.lpszClassName, instance);
    return pass ? 0 : 10;
}
