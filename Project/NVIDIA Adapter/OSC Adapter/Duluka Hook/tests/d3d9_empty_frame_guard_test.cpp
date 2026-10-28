// Isolated actual-Present regression for the empty-frame short circuit.
// Private mappings only. Requires the fixture's own foreground window.
// Run separately against the preserved candidate and the reviewed build.
#include <windows.h>
#include <d3d9.h>
#include <cstdint>
#include <cwchar>
#include <iostream>
#include <iterator>
#include "../include/osc_frame_bridge.h"

namespace
{
    using GetStatus = BOOL(WINAPI *)(std::uint64_t *, std::uint64_t *, BOOL *);
    using GetSkips = BOOL(WINAPI *)(std::uint64_t *);
    using GetAdapter = BOOL(WINAPI *)(std::uint64_t *, BOOL *, BOOL *, std::uint64_t *);
    using RegisterDevice = BOOL(WINAPI *)(IDirect3DDevice9 *);
    using UnregisterDevice = BOOL(WINAPI *)(IDirect3DDevice9 *);
    constexpr wchar_t WindowClass[] = L"DulukaD3D9EmptyFrameGuardFixture";
    constexpr UINT FrameWidth = 8, FrameHeight = 8;

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
    {
        return DefWindowProcW(window, message, wparam, lparam);
    }

    struct Counts
    {
        std::uint64_t presents = 0, draws = 0, skips = 0;
    };

    struct Fixture
    {
        HWND previousForeground = GetForegroundWindow();
        HWND window = nullptr;
        HANDLE frameMapping = nullptr, identityMapping = nullptr;
        osc_frame_bridge::D3D9Frame *frame = nullptr;
        osc_frame_bridge::ConsumerAdapterInfo *identity = nullptr;
        IDirect3D9 *factory = nullptr;
        IDirect3DDevice9 *device = nullptr;
        IDirect3DDevice9Ex *deviceEx = nullptr;
        HMODULE hook = nullptr;
        GetStatus getStatus = nullptr;
        GetSkips getSkips = nullptr;
        GetAdapter getAdapter = nullptr;
        UnregisterDevice unregisterDevice = nullptr;
        D3DPRESENT_PARAMETERS parameters{};
        HRESULT originalPresentResult = E_FAIL;
        std::uint64_t expectedLuid = 0;
        unsigned failures = 0;
        bool registered = false, classRegistered = false;

        ~Fixture()
        {
            if (device && registered)
            {
                // Release default-pool OSC resources before releasing device.
                if (deviceEx) deviceEx->ResetEx(&parameters, nullptr);
                else device->Reset(&parameters);
                if (unregisterDevice) unregisterDevice(device);
            }
            if (device) device->Release();
            if (factory) factory->Release();
            if (GetForegroundWindow() == window && IsWindow(previousForeground))
                SetForegroundWindow(previousForeground);
            if (window) DestroyWindow(window);
            if (classRegistered) UnregisterClassW(WindowClass, GetModuleHandleW(nullptr));
            if (frame) UnmapViewOfFile(frame);
            if (identity) UnmapViewOfFile(identity);
            if (frameMapping) CloseHandle(frameMapping);
            if (identityMapping) CloseHandle(identityMapping);
            // The loaded Hook lives until this fixture exits. This test makes
            // no claim about arbitrary dynamic DLL unload safety.
        }

        bool Check(bool condition, const char *label)
        {
            std::cout << (condition ? "PASS " : "FAIL ") << label << '\n';
            if (!condition) ++failures;
            return condition;
        }

        bool ReadCounts(Counts &out) const
        {
            BOOL installed = FALSE;
            return getStatus(&out.presents, &out.draws, &installed) && installed &&
                getSkips(&out.skips);
        }

        bool IdentityMatches() const
        {
            return identity->magic == osc_frame_bridge::ConsumerAdapterMagic &&
                identity->version == osc_frame_bridge::ConsumerAdapterVersion &&
                identity->ownerProcessId == static_cast<LONG>(GetCurrentProcessId()) &&
                static_cast<std::uint64_t>(identity->adapterLuid) == expectedLuid &&
                identity->renderApi ==
                    static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9);
        }

        void RestorePrivateIdentity()
        {
            identity->magic = osc_frame_bridge::ConsumerAdapterMagic;
            identity->version = osc_frame_bridge::ConsumerAdapterVersion;
            InterlockedExchange64(&identity->adapterLuid, static_cast<LONG64>(expectedLuid));
            InterlockedExchange(&identity->renderApi,
                static_cast<LONG>(osc_frame_bridge::ConsumerRenderApiD3D9));
            InterlockedExchange(&identity->ownerProcessId,
                static_cast<LONG>(GetCurrentProcessId()));
        }

        void PublishFrame(LONG64 number)
        {
            InterlockedIncrement(&frame->sequence);
            frame->width = FrameWidth;
            frame->height = FrameHeight;
            frame->rowPitch = FrameWidth * 4;
            frame->pixelBytes = FrameWidth * FrameHeight * 4;
            for (UINT pixel = 0; pixel < FrameWidth * FrameHeight; ++pixel)
            {
                frame->pixels[pixel * 4] = 0;
                frame->pixels[pixel * 4 + 1] = 255;
                frame->pixels[pixel * 4 + 2] = 0;
                frame->pixels[pixel * 4 + 3] = 255;
            }
            InterlockedExchange64(&frame->frameNumber, number);
            MemoryBarrier();
            InterlockedIncrement(&frame->sequence);
        }

        bool PresentOne(const char *label)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            { TranslateMessage(&message); DispatchMessageW(&message); }
            if (GetForegroundWindow() != window)
            {
                std::cout << "UNVERIFIED foreground lost during " << label << '\n';
                return false;
            }
            Counts before{}, after{};
            if (!ReadCounts(before)) return false;
            if (FAILED(device->Clear(0, nullptr, D3DCLEAR_TARGET,
                    D3DCOLOR_XRGB(0, 0, 0), 1.0f, 0))) return false;
            const HRESULT result = deviceEx
                ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
                : device->Present(nullptr, nullptr, nullptr, nullptr);
            if (!ReadCounts(after)) return false;
            return Check(result == originalPresentResult &&
                after.presents == before.presents + 1, label);
        }

        int Initialize(const wchar_t *dllPath, bool useEx)
        {
            wchar_t frameName[128]{}, identityName[128]{};
            swprintf_s(frameName, std::size(frameName),
                L"Local\\NVIDIAOSC.Frame.D3D9.EmptyGuard.%lu", GetCurrentProcessId());
            swprintf_s(identityName, std::size(identityName),
                L"Local\\NVIDIAOSC.Identity.D3D9.EmptyGuard.%lu", GetCurrentProcessId());
            if (!SetEnvironmentVariableW(
                    osc_frame_bridge::D3D9FrameMappingNameEnvironmentVariable, frameName) ||
                !SetEnvironmentVariableW(
                    osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable, identityName))
                return 3;
            frameMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                0, static_cast<DWORD>(osc_frame_bridge::D3D9FrameMappingBytes), frameName);
            if (!frameMapping || GetLastError() == ERROR_ALREADY_EXISTS) return 4;
            identityMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                0, static_cast<DWORD>(sizeof(*identity)), identityName);
            if (!identityMapping || GetLastError() == ERROR_ALREADY_EXISTS) return 4;
            frame = static_cast<osc_frame_bridge::D3D9Frame *>(MapViewOfFile(
                frameMapping, FILE_MAP_ALL_ACCESS, 0, 0, osc_frame_bridge::D3D9FrameMappingBytes));
            identity = static_cast<osc_frame_bridge::ConsumerAdapterInfo *>(MapViewOfFile(
                identityMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(*identity)));
            if (!frame || !identity) return 5;
            frame->magic = osc_frame_bridge::D3D9FrameMagic;
            frame->version = osc_frame_bridge::D3D9FrameVersion;
            frame->ownerProcessId = static_cast<LONG>(GetCurrentProcessId());
            PublishFrame(0);
            WNDCLASSW klass{};
            klass.hInstance = GetModuleHandleW(nullptr);
            klass.lpfnWndProc = WindowProc;
            klass.lpszClassName = WindowClass;
            classRegistered = RegisterClassW(&klass) != 0;
            if (!classRegistered) return 6;
            window = CreateWindowExW(0, WindowClass, L"D3D9 empty-frame guard isolated test",
                WS_OVERLAPPEDWINDOW, 100, 100, 192, 160, nullptr, nullptr, klass.hInstance, nullptr);
            if (!window) return 6;
            ShowWindow(window, SW_SHOW);
            UpdateWindow(window);
            for (int attempt = 0; attempt < 100 && GetForegroundWindow() != window; ++attempt)
            { SetForegroundWindow(window); Sleep(10); }
            if (GetForegroundWindow() != window)
            {
                std::cout << "UNVERIFIED fixture foreground unavailable\n";
                return 77;
            }
            parameters.BackBufferWidth = 64;
            parameters.BackBufferHeight = 64;
            parameters.BackBufferCount = 1;
            parameters.BackBufferFormat = D3DFMT_UNKNOWN;
            parameters.Windowed = TRUE;
            parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
            parameters.hDeviceWindow = window;
            parameters.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
            HRESULT result = E_FAIL;
            if (useEx)
            {
                IDirect3D9Ex *factoryEx = nullptr;
                result = Direct3DCreate9Ex(D3D_SDK_VERSION, &factoryEx);
                factory = factoryEx;
                if (SUCCEEDED(result) && factoryEx)
                    result = factoryEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
                        window, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, nullptr, &deviceEx);
                device = deviceEx;
            }
            else
            {
                factory = Direct3DCreate9(D3D_SDK_VERSION);
                if (factory)
                    result = factory->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
                        window, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, &device);
            }
            if (FAILED(result) || !device) return 7;
            originalPresentResult = deviceEx
                ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
                : device->Present(nullptr, nullptr, nullptr, nullptr);
            if (FAILED(originalPresentResult)) return 7;
            hook = LoadLibraryW(dllPath);
            if (!hook) return 8;
            getStatus = reinterpret_cast<GetStatus>(GetProcAddress(hook, "NvOscAdapterGetD3D9Status"));
            getSkips = reinterpret_cast<GetSkips>(GetProcAddress(hook, "NvOscAdapterGetD3D9EmptyFrameSkips"));
            getAdapter = reinterpret_cast<GetAdapter>(GetProcAddress(hook, "NvOscAdapterGetD3D9AdapterStatus"));
            const auto registerDevice = reinterpret_cast<RegisterDevice>(
                GetProcAddress(hook, "NvOscAdapterRegisterD3D9Device"));
            unregisterDevice = reinterpret_cast<UnregisterDevice>(
                GetProcAddress(hook, "NvOscAdapterUnregisterD3D9Device"));
            if (!getStatus || !getSkips || !getAdapter || !registerDevice || !unregisterDevice) return 9;
            BOOL installed = FALSE;
            std::uint64_t presents = 0, draws = 0;
            for (int attempt = 0; attempt < 500 && !installed; ++attempt)
            { getStatus(&presents, &draws, &installed); if (!installed) Sleep(10); }
            if (!installed || !registerDevice(device)) return 10;
            registered = true;
            return 0;
        }

        int Run()
        {
            for (int frameIndex = 0; frameIndex < 4; ++frameIndex)
                if (!PresentOne("hidden warmup original Present forwarding")) return 77;
            BOOL isEx = FALSE, gpuBound = FALSE;
            std::uint64_t busySkips = 0;
            if (!getAdapter(&expectedLuid, &isEx, &gpuBound, &busySkips)) return 11;
            Counts initial{};
            if (!ReadCounts(initial)) return 11;
            if (!Check(expectedLuid != 0 && IdentityMatches() && initial.skips > 0 &&
                    initial.draws == 0 && !gpuBound && (isEx != FALSE) == (deviceEx != nullptr),
                    "actual Present established adapter identity and empty-frame skips")) return 11;

            // Each mutation models a competing renderer. It affects only the
            // fixture's private mapping and is restored between negative cases.
            for (int field = 0; field < 4; ++field)
            {
                Counts before{}, after{};
                if (!ReadCounts(before)) return 11;
                if (field == 0) InterlockedExchange(&identity->ownerProcessId, 0);
                if (field == 1) InterlockedExchange(&identity->ownerProcessId,
                    GetCurrentProcessId() == 1 ? 2 : 1);
                if (field == 2) InterlockedExchange(&identity->renderApi, 11);
                if (field == 3) InterlockedExchange64(&identity->adapterLuid,
                    static_cast<LONG64>(expectedLuid ^ 0x1000ull));
                const char *label = field == 0 ? "repair cleared identity owner" :
                    field == 1 ? "repair competing identity owner" :
                    field == 2 ? "repair identity API" : "repair identity LUID";
                if (!PresentOne(label) || !ReadCounts(after)) return 77;
                Check(IdentityMatches() && after.skips == before.skips &&
                    after.draws == before.draws, label);
                RestorePrivateIdentity();
            }

            Counts before{}, after{};
            if (!ReadCounts(before)) return 11;
            PublishFrame(7);
            if (!PresentOne("nonzero CPU frame original Present forwarding") || !ReadCounts(after)) return 77;
            Check(after.skips == before.skips && after.draws == before.draws + 1,
                "nonzero CPU frame exits empty-frame guard and draws");
            PublishFrame(0);
            if (!PresentOne("hide clears cached CPU frame") || !ReadCounts(before)) return 77;
            if (!PresentOne("hidden cache cleared original Present forwarding") || !ReadCounts(after)) return 77;
            Check(after.skips == before.skips + 1 && after.draws == before.draws,
                "hidden frame skips only after cached frame retirement");
            before = after;
            InterlockedIncrement(&frame->sequence);
            const bool oddPresented = PresentOne("odd sequence original Present forwarding");
            InterlockedIncrement(&frame->sequence);
            if (!oddPresented || !ReadCounts(after)) return 77;
            Check(after.skips == before.skips && after.draws == before.draws,
                "odd producer sequence never takes empty-frame shortcut");

            const HRESULT resetResult = deviceEx
                ? deviceEx->ResetEx(&parameters, nullptr) : device->Reset(&parameters);
            std::uint64_t resetLuid = 1;
            const bool resetStatus = getAdapter(&resetLuid, &isEx, &gpuBound, &busySkips) != FALSE;
            Check(SUCCEEDED(resetResult) && resetStatus && resetLuid == 0 &&
                identity->ownerProcessId == 0 && identity->adapterLuid == 0 && identity->renderApi == 0,
                "Reset forwards and invalidates cached/shared identity");
            if (FAILED(resetResult) || !ReadCounts(before)) return 12;
            if (!PresentOne("first post-Reset original Present forwarding") || !ReadCounts(after)) return 77;
            Check(IdentityMatches() && after.skips == before.skips && after.draws == before.draws,
                "first post-Reset Present republishes identity before shortcut");
            before = after;
            if (!PresentOne("second post-Reset original Present forwarding") || !ReadCounts(after)) return 77;
            Check(after.skips == before.skips + 1 && IdentityMatches(),
                "second post-Reset hidden Present resumes shortcut");
            std::cout << "D3D9_EMPTY_GUARD_RESULT=" << (failures ? "FAIL" : "PASS")
                << " failures=" << failures << " presents=" << after.presents
                << " draws=" << after.draws << " skips=" << after.skips << '\n';
            return failures ? 13 : 0;
        }
    };
}

int wmain(int argc, wchar_t **argv)
{
    if (argc != 2 && !(argc == 3 && std::wcscmp(argv[2], L"--ex") == 0))
    {
        std::cerr << "Usage: D3D9 Empty Frame Guard Test.exe <candidate-dll> [--ex]\n";
        return 2;
    }
    Fixture fixture;
    const int initialized = fixture.Initialize(argv[1], argc == 3);
    if (initialized)
    {
        std::cout << "D3D9_EMPTY_GUARD_INITIALIZE=" << initialized << '\n';
        return initialized;
    }
    return fixture.Run();
}
