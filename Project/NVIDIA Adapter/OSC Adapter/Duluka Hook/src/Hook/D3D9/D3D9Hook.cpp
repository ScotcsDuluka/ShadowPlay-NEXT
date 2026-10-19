#include <windows.h>
#include <d3d9.h>
#include <d3d9types.h>
#include <dxgi1_2.h>
#include <strsafe.h>
#include <tlhelp32.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string>
#include <vector>

#include "../../../include/osc_frame_bridge.h"
#include "../../Renderer/D3D9/D3D9Renderer.h"

#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "dxgi.lib")

// Defined in D3D11Hook.cpp; true once ReShade add-on composition is active
// and the raw D3D9 hook must stop drawing (single draw site, after effects).
bool ReShadeAddonDrawActive();

namespace
{
    using PresentFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9 *, const RECT *, const RECT *, HWND, const RGNDATA *);
    using ResetFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9 *, D3DPRESENT_PARAMETERS *);
    using PresentExFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9Ex *, const RECT *, const RECT *, HWND,
        const RGNDATA *, DWORD);
    using ResetExFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9Ex *, D3DPRESENT_PARAMETERS *, D3DDISPLAYMODEEX *);
    using Direct3DCreate9Function = IDirect3D9 *(WINAPI *)(UINT);
    using Direct3DCreate9ExFunction = HRESULT(WINAPI *)(
        UINT, IDirect3D9Ex **);
    using CreateDeviceFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3D9 *, UINT, D3DDEVTYPE, HWND, DWORD,
        D3DPRESENT_PARAMETERS *, IDirect3DDevice9 **);
    using CreateDeviceExFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3D9Ex *, UINT, D3DDEVTYPE, HWND, DWORD,
        D3DPRESENT_PARAMETERS *, D3DDISPLAYMODEEX *, IDirect3DDevice9Ex **);

    constexpr std::size_t PresentSlot = 17;
    constexpr std::size_t ResetSlot = 16;
    constexpr std::size_t PresentExSlot = 121;
    constexpr std::size_t ResetExSlot = 132;
    constexpr std::size_t CreateDeviceSlot = 16;
    constexpr std::size_t CreateDeviceExSlot = 20;

    struct HookedSlot
    {
        void **slot;
        void *hook;
        void *original;
    };

    std::atomic<bool> g_hookInstalled{ false };
    std::atomic<std::uint64_t> g_presentCount{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackTicks{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackSamples{ 0 };
    std::atomic<std::uint64_t> g_originalPresentTicks{ 0 };
    std::atomic<std::uint64_t> g_originalPresentMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_originalPresentSamples{ 0 };

    PresentFunction g_originalPresent = nullptr;
    ResetFunction g_originalReset = nullptr;
    PresentExFunction g_originalPresentEx = nullptr;
    ResetExFunction g_originalResetEx = nullptr;
    HMODULE g_module = nullptr;
    Direct3DCreate9Function g_originalDirect3DCreate9 = nullptr;
    Direct3DCreate9ExFunction g_originalDirect3DCreate9Ex = nullptr;
    CreateDeviceFunction g_originalCreateDevice = nullptr;
    CreateDeviceExFunction g_originalCreateDeviceEx = nullptr;
    std::vector<HookedSlot> g_hookedSlots;
    std::mutex g_hookedSlotsMutex;
    void Log(const char *message);

    std::uint64_t QueryPerformanceFrequencyValue()
    {
        static const std::uint64_t frequency = [] {
            LARGE_INTEGER value{};
            return QueryPerformanceFrequency(&value) && value.QuadPart > 0
                ? static_cast<std::uint64_t>(value.QuadPart) : 1ull;
        }();
        return frequency;
    }

    void RecordDuration(LARGE_INTEGER start, LARGE_INTEGER end,
        std::atomic<std::uint64_t>& total,
        std::atomic<std::uint64_t>& maximum,
        std::atomic<std::uint64_t>& samples)
    {
        const std::uint64_t ticks = end.QuadPart > start.QuadPart
            ? static_cast<std::uint64_t>(end.QuadPart - start.QuadPart) : 0;
        total.fetch_add(ticks, std::memory_order_relaxed);
        samples.fetch_add(1, std::memory_order_relaxed);
        auto previous = maximum.load(std::memory_order_relaxed);
        while (ticks > previous && !maximum.compare_exchange_weak(
            previous, ticks, std::memory_order_relaxed)) {}
    }

    void LogPresentDiagnostics(const char* label)
    {
        const auto frequency = QueryPerformanceFrequencyValue();
        const auto overlaySamples = g_overlayCallbackSamples.exchange(
            0, std::memory_order_relaxed);
        const auto overlayTicks = g_overlayCallbackTicks.exchange(
            0, std::memory_order_relaxed);
        const auto overlayMaxTicks = g_overlayCallbackMaxTicks.exchange(
            0, std::memory_order_relaxed);
        const auto presentSamples = g_originalPresentSamples.exchange(
            0, std::memory_order_relaxed);
        const auto presentTicks = g_originalPresentTicks.exchange(
            0, std::memory_order_relaxed);
        const auto presentMaxTicks = g_originalPresentMaxTicks.exchange(
            0, std::memory_order_relaxed);
        const auto overlayAvgUs = overlaySamples
            ? overlayTicks * 1000000ull / frequency / overlaySamples : 0;
        const auto overlayMaxUs = overlayMaxTicks * 1000000ull / frequency;
        const auto presentAvgUs = presentSamples
            ? presentTicks * 1000000ull / frequency / presentSamples : 0;
        const auto presentMaxUs = presentMaxTicks * 1000000ull / frequency;

        char diagnostic[320]{};
        sprintf_s(diagnostic, sizeof(diagnostic),
            "%s: presents=%llu draws=%llu overlay_n=%llu overlay_us(avg/max)=%llu/%llu original_present_n=%llu original_present_us(avg/max)=%llu/%llu",
            label,
            static_cast<unsigned long long>(g_presentCount.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(duluka::d3d9::GetDrawCount()),
            static_cast<unsigned long long>(overlaySamples),
            static_cast<unsigned long long>(overlayAvgUs),
            static_cast<unsigned long long>(overlayMaxUs),
            static_cast<unsigned long long>(presentSamples),
            static_cast<unsigned long long>(presentAvgUs),
            static_cast<unsigned long long>(presentMaxUs));
        Log(diagnostic);
    }

    void Log(const char *message)
    {
        OutputDebugStringA("[NVIDIA OSC Adapter D3D9] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");

        wchar_t localAppData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
        if (!length || length >= std::size(localAppData))
            return;

        wchar_t path[MAX_PATH]{};
        if (FAILED(StringCchCopyW(path, std::size(path), localAppData)) ||
            FAILED(StringCchCatW(path, std::size(path),
                L"\\NVIDIA\\OSC Adapter\\Logs")))
            return;
        CreateDirectoryW((std::wstring(localAppData) + L"\\NVIDIA").c_str(), nullptr);
        CreateDirectoryW((std::wstring(localAppData) +
            L"\\NVIDIA\\OSC Adapter").c_str(), nullptr);
        CreateDirectoryW(path, nullptr);
        if (FAILED(StringCchCatW(path, std::size(path), L"\\adapter-d3d9-")))
            return;

        wchar_t processId[24]{};
        if (FAILED(StringCchPrintfW(processId, std::size(processId),
                L"%lu.log", GetCurrentProcessId())) ||
            FAILED(StringCchCatW(path, std::size(path), processId)))
            return;

        HANDLE file = CreateFileW(path, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;
        SYSTEMTIME time{};
        GetSystemTime(&time);
        char line[1024]{};
        const int lineLength = sprintf_s(line, sizeof(line),
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ PID=%lu %s\r\n",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
            time.wSecond, time.wMilliseconds, GetCurrentProcessId(), message);
        if (lineLength > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(lineLength), &written, nullptr);
        }
        CloseHandle(file);
    }

















    HRESULT STDMETHODCALLTYPE HookReset(
        IDirect3DDevice9 *device, D3DPRESENT_PARAMETERS *parameters)
    {
        duluka::d3d9::OnReset();
        return g_originalReset(device, parameters);
    }

    HRESULT STDMETHODCALLTYPE HookResetEx(
        IDirect3DDevice9Ex *device,
        D3DPRESENT_PARAMETERS *parameters,
        D3DDISPLAYMODEEX *fullscreenMode)
    {
        duluka::d3d9::OnReset();
        return g_originalResetEx(device, parameters, fullscreenMode);
    }

    HRESULT STDMETHODCALLTYPE HookPresent(
        IDirect3DDevice9 *device,
        const RECT *sourceRect,
        const RECT *destinationRect,
        HWND destinationWindow,
        const RGNDATA *dirtyRegion)
    {
        const auto count = g_presentCount.fetch_add(
            1, std::memory_order_relaxed) + 1;
        if (!ReShadeAddonDrawActive() &&
            osc_frame_bridge::IsGameOrNvidiaOverlayForeground(
                GetCurrentProcessId()))
        {
            LARGE_INTEGER overlayStart{}, overlayEnd{};
            QueryPerformanceCounter(&overlayStart);
            duluka::d3d9::OnPresent(device);
            QueryPerformanceCounter(&overlayEnd);
            RecordDuration(overlayStart, overlayEnd,
                g_overlayCallbackTicks, g_overlayCallbackMaxTicks,
                g_overlayCallbackSamples);
        }
        LARGE_INTEGER presentStart{}, presentEnd{};
        QueryPerformanceCounter(&presentStart);
        const HRESULT result = g_originalPresent(device, sourceRect, destinationRect,
            destinationWindow, dirtyRegion);
        QueryPerformanceCounter(&presentEnd);
        RecordDuration(presentStart, presentEnd, g_originalPresentTicks,
            g_originalPresentMaxTicks, g_originalPresentSamples);
        if (count == 1 || count % 1200 == 0)
            LogPresentDiagnostics("D3D9 Present diagnostics");
        return result;
    }

    HRESULT STDMETHODCALLTYPE HookPresentEx(
        IDirect3DDevice9Ex *device,
        const RECT *sourceRect,
        const RECT *destinationRect,
        HWND destinationWindow,
        const RGNDATA *dirtyRegion,
        DWORD flags)
    {
        const auto count = g_presentCount.fetch_add(
            1, std::memory_order_relaxed) + 1;
        if (!ReShadeAddonDrawActive() &&
            osc_frame_bridge::IsGameOrNvidiaOverlayForeground(
                GetCurrentProcessId()))
        {
            LARGE_INTEGER overlayStart{}, overlayEnd{};
            QueryPerformanceCounter(&overlayStart);
            duluka::d3d9::OnPresent(device);
            QueryPerformanceCounter(&overlayEnd);
            RecordDuration(overlayStart, overlayEnd,
                g_overlayCallbackTicks, g_overlayCallbackMaxTicks,
                g_overlayCallbackSamples);
        }
        LARGE_INTEGER presentStart{}, presentEnd{};
        QueryPerformanceCounter(&presentStart);
        const HRESULT result = g_originalPresentEx(device, sourceRect, destinationRect,
            destinationWindow, dirtyRegion, flags);
        QueryPerformanceCounter(&presentEnd);
        RecordDuration(presentStart, presentEnd, g_originalPresentTicks,
            g_originalPresentMaxTicks, g_originalPresentSamples);
        if (count == 1 || count % 1200 == 0)
            LogPresentDiagnostics("D3D9Ex PresentEx diagnostics");
        return result;
    }

    bool InstallHookedSlot(void **slot, void *hook, void *&original)
    {
        std::lock_guard<std::mutex> lock(g_hookedSlotsMutex);
        void *current = *slot;
        if (current == hook)
            return true;
        if (!original)
            original = current;
        else if (current != original)
            return false;

        DWORD oldProtection = 0;
        if (!VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE,
                &oldProtection))
            return false;
        void *replaced = InterlockedExchangePointer(
            reinterpret_cast<PVOID volatile *>(slot), hook);
        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void *), oldProtection, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
        if (replaced != original)
        {
            DWORD restoreProtection = 0;
            if (VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE,
                    &restoreProtection))
            {
                InterlockedCompareExchangePointer(
                    reinterpret_cast<PVOID volatile *>(slot), replaced, hook);
                VirtualProtect(slot, sizeof(void *), restoreProtection, &ignored);
            }
            return false;
        }
        g_hookedSlots.push_back({ slot, hook, original });
        return true;
    }

    bool PatchDevice(IDirect3DDevice9 *device, bool isEx)
    {
        if (!device)
            return false;
        auto vtable = *reinterpret_cast<void ***>(device);
        void *originalPresent = reinterpret_cast<void *>(g_originalPresent);
        void *originalReset = reinterpret_cast<void *>(g_originalReset);
        const bool presentInstalled = InstallHookedSlot(
            &vtable[PresentSlot], reinterpret_cast<void *>(&HookPresent),
            originalPresent);
        if (presentInstalled)
            g_originalPresent = reinterpret_cast<PresentFunction>(originalPresent);
        const bool resetInstalled = InstallHookedSlot(
            &vtable[ResetSlot], reinterpret_cast<void *>(&HookReset),
            originalReset);
        if (resetInstalled)
            g_originalReset = reinterpret_cast<ResetFunction>(originalReset);
        bool installed = presentInstalled && resetInstalled;

        if (isEx)
        {
            void *originalPresentEx =
                reinterpret_cast<void *>(g_originalPresentEx);
            void *originalResetEx =
                reinterpret_cast<void *>(g_originalResetEx);
            const bool exInstalled = InstallHookedSlot(
                &vtable[PresentExSlot],
                reinterpret_cast<void *>(&HookPresentEx), originalPresentEx) &&
                InstallHookedSlot(&vtable[ResetExSlot],
                    reinterpret_cast<void *>(&HookResetEx), originalResetEx);
            if (exInstalled)
            {
                g_originalPresentEx =
                    reinterpret_cast<PresentExFunction>(originalPresentEx);
                g_originalResetEx =
                    reinterpret_cast<ResetExFunction>(originalResetEx);
            }
            installed = installed && exInstalled;
        }
        return installed;
    }

    HRESULT STDMETHODCALLTYPE HookCreateDevice(
        IDirect3D9 *direct3d, UINT adapter, D3DDEVTYPE deviceType,
        HWND focusWindow, DWORD behaviorFlags,
        D3DPRESENT_PARAMETERS *parameters, IDirect3DDevice9 **device)
    {
        const HRESULT result = g_originalCreateDevice
            ? g_originalCreateDevice(direct3d, adapter, deviceType,
                focusWindow, behaviorFlags, parameters, device)
            : D3DERR_INVALIDCALL;
        if (SUCCEEDED(result) && device && *device && !PatchDevice(*device, false))
            Log("Could not hook the created D3D9 device vtable.");
        return result;
    }

    HRESULT STDMETHODCALLTYPE HookCreateDeviceEx(
        IDirect3D9Ex *direct3d, UINT adapter, D3DDEVTYPE deviceType,
        HWND focusWindow, DWORD behaviorFlags,
        D3DPRESENT_PARAMETERS *parameters,
        D3DDISPLAYMODEEX *fullscreenMode, IDirect3DDevice9Ex **device)
    {
        const HRESULT result = g_originalCreateDeviceEx
            ? g_originalCreateDeviceEx(direct3d, adapter, deviceType,
                focusWindow, behaviorFlags, parameters, fullscreenMode, device)
            : D3DERR_INVALIDCALL;
        if (SUCCEEDED(result) && device && *device &&
            !PatchDevice(*device, true))
            Log("Could not hook the created D3D9Ex device vtable.");
        return result;
    }

    void PatchFactory(IDirect3D9 *direct3d, bool isEx)
    {
        if (!direct3d)
            return;
        auto vtable = *reinterpret_cast<void ***>(direct3d);
        void *originalCreateDevice =
            reinterpret_cast<void *>(g_originalCreateDevice);
        const bool baseInstalled = InstallHookedSlot(
            &vtable[CreateDeviceSlot],
            reinterpret_cast<void *>(&HookCreateDevice),
            originalCreateDevice);
        if (baseInstalled)
            g_originalCreateDevice =
                reinterpret_cast<CreateDeviceFunction>(originalCreateDevice);
        if (!isEx)
            return;

        void *originalCreateDeviceEx =
            reinterpret_cast<void *>(g_originalCreateDeviceEx);
        if (InstallHookedSlot(&vtable[CreateDeviceExSlot],
                reinterpret_cast<void *>(&HookCreateDeviceEx),
                originalCreateDeviceEx))
            g_originalCreateDeviceEx =
                reinterpret_cast<CreateDeviceExFunction>(originalCreateDeviceEx);
    }

    IDirect3D9 *WINAPI HookDirect3DCreate9(UINT sdkVersion)
    {
        IDirect3D9 *direct3d = g_originalDirect3DCreate9
            ? g_originalDirect3DCreate9(sdkVersion) : nullptr;
        if (direct3d)
            PatchFactory(direct3d, false);
        return direct3d;
    }

    HRESULT WINAPI HookDirect3DCreate9Ex(
        UINT sdkVersion, IDirect3D9Ex **direct3d)
    {
        if (!g_originalDirect3DCreate9Ex)
            return D3DERR_INVALIDCALL;
        const HRESULT result =
            g_originalDirect3DCreate9Ex(sdkVersion, direct3d);
        if (SUCCEEDED(result) && direct3d && *direct3d)
            PatchFactory(*direct3d, true);
        return result;
    }

    std::size_t PatchModuleImports(HMODULE module)
    {
        if (!module)
            return 0;
        auto *base = reinterpret_cast<std::uint8_t *>(module);
        auto *dosHeader = reinterpret_cast<IMAGE_DOS_HEADER *>(base);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
            return 0;
        auto *ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS64 *>(
            base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE ||
            ntHeaders->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return 0;
        const auto &directory = ntHeaders->OptionalHeader.DataDirectory[
            IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!directory.VirtualAddress ||
            directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR))
            return 0;

        std::size_t patched = 0;
        auto *descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR *>(
            base + directory.VirtualAddress);
        const std::size_t descriptorCount =
            directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR);
        for (std::size_t index = 0; index < descriptorCount &&
                descriptor[index].Name; ++index)
        {
            const char *libraryName = reinterpret_cast<const char *>(
                base + descriptor[index].Name);
            if (_stricmp(libraryName, "d3d9.dll") != 0)
                continue;
            if (!descriptor[index].FirstThunk)
                continue;

            if (!descriptor[index].OriginalFirstThunk)
                continue;
            const DWORD originalThunkRva = descriptor[index].OriginalFirstThunk;
            auto *names = reinterpret_cast<IMAGE_THUNK_DATA64 *>(
                base + originalThunkRva);
            auto *addresses = reinterpret_cast<IMAGE_THUNK_DATA64 *>(
                base + descriptor[index].FirstThunk);
            for (; names->u1.AddressOfData; ++names, ++addresses)
            {
                if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal))
                    continue;
                auto *importName = reinterpret_cast<IMAGE_IMPORT_BY_NAME *>(
                    base + names->u1.AddressOfData);
                void **slot = reinterpret_cast<void **>(
                    &addresses->u1.Function);
                void *original = reinterpret_cast<void *>(
                    static_cast<std::uintptr_t>(addresses->u1.Function));
                bool matched = false;
                if (std::strcmp(reinterpret_cast<const char *>(importName->Name),
                        "Direct3DCreate9") == 0)
                {
                    if (!g_originalDirect3DCreate9)
                        g_originalDirect3DCreate9 =
                            reinterpret_cast<Direct3DCreate9Function>(original);
                    original = reinterpret_cast<void *>(g_originalDirect3DCreate9);
                    matched = InstallHookedSlot(slot,
                        reinterpret_cast<void *>(&HookDirect3DCreate9), original);
                }
                else if (std::strcmp(
                        reinterpret_cast<const char *>(importName->Name),
                        "Direct3DCreate9Ex") == 0)
                {
                    if (!g_originalDirect3DCreate9Ex)
                        g_originalDirect3DCreate9Ex =
                            reinterpret_cast<Direct3DCreate9ExFunction>(original);
                    original = reinterpret_cast<void *>(g_originalDirect3DCreate9Ex);
                    matched = InstallHookedSlot(slot,
                        reinterpret_cast<void *>(&HookDirect3DCreate9Ex), original);
                }
                if (matched)
                    ++patched;
            }
        }
        return patched;
    }

    bool InstallD3D9PresentHook()
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(
            TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            Log("Could not enumerate loaded modules for D3D9 factory imports.");
            return false;
        }

        MODULEENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        std::size_t patched = 0;
        if (Module32FirstW(snapshot, &entry))
        {
            do
            {
                patched += PatchModuleImports(entry.hModule);
            } while (Module32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
        if (!patched)
        {
            Log("No imported D3D9 factory functions were found to hook.");
            return false;
        }

        g_hookInstalled.store(true, std::memory_order_release);
        Log("D3D9 factory imports hooked; created devices will receive Present hooks.");
        return true;
    }

    void RemoveD3D9PresentHook()
    {
        if (!g_hookInstalled.exchange(false, std::memory_order_acq_rel))
            return;
        std::lock_guard<std::mutex> lock(g_hookedSlotsMutex);
        for (auto it = g_hookedSlots.rbegin(); it != g_hookedSlots.rend(); ++it)
        {
            DWORD oldProtection = 0;
            if (!VirtualProtect(it->slot, sizeof(void *),
                    PAGE_EXECUTE_READWRITE, &oldProtection))
                continue;
            InterlockedCompareExchangePointer(
                reinterpret_cast<PVOID volatile *>(it->slot),
                it->original, it->hook);
            DWORD ignored = 0;
            VirtualProtect(it->slot, sizeof(void *), oldProtection, &ignored);
            FlushInstructionCache(GetCurrentProcess(), it->slot, sizeof(void *));
        }
        g_hookedSlots.clear();
        g_originalDirect3DCreate9 = nullptr;
        g_originalDirect3DCreate9Ex = nullptr;
        g_originalCreateDevice = nullptr;
        g_originalCreateDeviceEx = nullptr;
        g_originalPresent = nullptr;
        g_originalReset = nullptr;
        g_originalPresentEx = nullptr;
        g_originalResetEx = nullptr;
        duluka::d3d9::OnReset();
        Log("D3D9 Present hooks removed.");
    }
}

void InitializeD3D9PresentHook(HMODULE module)
{
    g_module = module;
    Log("[compositor-d3d9] mode=raw-present (primary; ReShade-independent)");
    if (ReShadeAddonDrawActive())
        Log("[compositor-d3d9] enhancement=reshade-addon active; raw D3D9 "
            "hook stays as pass-through (single draw after ReShade effects).");
    if (!InstallD3D9PresentHook())
        Log("D3D9 hook initialization did not install.");
}

void ShutdownD3D9PresentHook()
{
    RemoveD3D9PresentHook();
    duluka::d3d9::Shutdown();
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterRenderD3D9ForTest(
    IDirect3DDevice9 *device)
{
    if (!device)
        return FALSE;
    const auto previousDrawCount = duluka::d3d9::GetDrawCount();
    {
        duluka::d3d9::RenderForTest(device);
    }
    return duluka::d3d9::GetDrawCount() > previousDrawCount
        ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9Status(
    std::uint64_t *presentCount, std::uint64_t *drawCount, BOOL *hookInstalled)
{
    if (!presentCount || !drawCount || !hookInstalled)
        return FALSE;
    *presentCount = g_presentCount.load(std::memory_order_relaxed);
    *drawCount = duluka::d3d9::GetDrawCount();
    *hookInstalled = g_hookInstalled.load(std::memory_order_acquire) ? TRUE : FALSE;
    return TRUE;
}
