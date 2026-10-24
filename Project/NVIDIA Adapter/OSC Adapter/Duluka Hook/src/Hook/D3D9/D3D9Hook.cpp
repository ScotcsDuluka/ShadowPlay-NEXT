#include <windows.h>
#include <d3d9.h>
#include <d3d9types.h>
#include <dxgi1_2.h>
#include <strsafe.h>
#include <tlhelp32.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string>
#include <vector>
#include <utility>

#include "../../../include/osc_frame_bridge.h"
#include "../../../include/d3d9_timing_diagnostics.h"
#include "../../../include/d3d9ex_gpu_mailbox.h"
#include "MinHook.h"
#include "../../Renderer/D3D9/D3D9Renderer.h"

#pragma comment(lib, "d3d9.lib")
#pragma comment(lib, "dxgi.lib")

// Defined in D3D11Hook.cpp; true once ReShade add-on composition is active
// and the raw D3D9 hook must stop drawing (single draw site, after effects).
bool ReShadeAddonDrawActive();

#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:NvOscAdapterRenderD3D9ForTest=_NvOscAdapterRenderD3D9ForTest@4")
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9Status=_NvOscAdapterGetD3D9Status@12")
#pragma comment(linker, "/EXPORT:NvOscAdapterRegisterD3D9Device=_NvOscAdapterRegisterD3D9Device@4")
#pragma comment(linker, "/EXPORT:NvOscAdapterUnregisterD3D9Device=_NvOscAdapterUnregisterD3D9Device@4")
#pragma comment(linker, "/EXPORT:NvOscAdapterBindD3D9ExGpuFrame=_NvOscAdapterBindD3D9ExGpuFrame@24")
#pragma comment(linker, "/EXPORT:NvOscAdapterUnbindD3D9ExGpuFrame=_NvOscAdapterUnbindD3D9ExGpuFrame@4")
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9AdapterStatus=_NvOscAdapterGetD3D9AdapterStatus@16")
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9TimingSnapshot=_NvOscAdapterGetD3D9TimingSnapshot@8")
#pragma comment(linker, "/EXPORT:NvOscAdapterStageD3D9ExGpuFrame=_NvOscAdapterStageD3D9ExGpuFrame@24")
#pragma comment(linker, "/EXPORT:NvOscAdapterCommitD3D9ExGpuFrame=_NvOscAdapterCommitD3D9ExGpuFrame@12")
#pragma comment(linker, "/EXPORT:NvOscAdapterDiscardD3D9ExPendingGpuFrame=_NvOscAdapterDiscardD3D9ExPendingGpuFrame@4")
#pragma comment(linker, "/EXPORT:NvOscAdapterPumpD3D9ExGpuMailbox=_NvOscAdapterPumpD3D9ExGpuMailbox@4")
#endif

namespace
{
    using PresentFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9 *, const RECT *, const RECT *, HWND, const RGNDATA *);
    using ResetFunction = HRESULT(STDMETHODCALLTYPE *)(
        IDirect3DDevice9 *, D3DPRESENT_PARAMETERS *);
    using EndSceneFunction = HRESULT(STDMETHODCALLTYPE *)(IDirect3DDevice9 *);
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
    constexpr std::size_t EndSceneSlot = 42;
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
    // Explicit preview only. Never enable for ordinary D3D9 or default games.
    std::atomic<bool> g_exEndScenePumpPreview{ false };
    std::atomic<std::uint64_t> g_exEndSceneCalls{ 0 };
    std::atomic<std::uint64_t> g_exEndSceneCommits{ 0 };
    std::atomic<std::uint64_t> g_presentCount{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackTicks{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_overlayCallbackSamples{ 0 };
    std::atomic<std::uint64_t> g_originalPresentTicks{ 0 };
    std::atomic<std::uint64_t> g_originalPresentMaxTicks{ 0 };
    std::atomic<std::uint64_t> g_originalPresentSamples{ 0 };

    PresentFunction g_originalPresent = nullptr;
    ResetFunction g_originalReset = nullptr;
    EndSceneFunction g_originalEndScene = nullptr;
    PresentExFunction g_originalPresentEx = nullptr;
    ResetExFunction g_originalResetEx = nullptr;
    HMODULE g_module = nullptr;
    Direct3DCreate9Function g_originalDirect3DCreate9 = nullptr;
    Direct3DCreate9ExFunction g_originalDirect3DCreate9Ex = nullptr;
    CreateDeviceFunction g_originalCreateDevice = nullptr;
    CreateDeviceExFunction g_originalCreateDeviceEx = nullptr;
    std::vector<HookedSlot> g_hookedSlots;
    std::mutex g_hookedSlotsMutex;
    bool g_codeDetoursInstalled = false;
    std::vector<void *> g_codeDetourTargets;
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

















    // On the real D3D9Ex game device's owning graphics thread, AFTER
    // EndScene returns. This avoids polling the GPU mailbox in Present.
    // The optional code detour may also see classic DX9 devices if the
    // implementation shares its method: QueryInterface rejects those.
    HRESULT STDMETHODCALLTYPE HookEndScene(IDirect3DDevice9 *device)
    {
        const auto original = g_originalEndScene;
        if (!original)
            return D3DERR_INVALIDCALL;
        const HRESULT result = original(device);
        if (!SUCCEEDED(result) || !device)
            return result;
        IDirect3DDevice9Ex *ex = nullptr;
        if (SUCCEEDED(device->QueryInterface(__uuidof(IDirect3DDevice9Ex),
                reinterpret_cast<void **>(&ex))) && ex)
        {
            g_exEndSceneCalls.fetch_add(1, std::memory_order_relaxed);
            if (duluka::d3d9ex_mailbox::Pump(ex))
                g_exEndSceneCommits.fetch_add(1, std::memory_order_relaxed);
            ex->Release();
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE HookReset(
        IDirect3DDevice9 *device, D3DPRESENT_PARAMETERS *parameters)
    {
        duluka::d3d9ex_mailbox::OnDeviceReset(device);
        duluka::d3d9::OnReset(device);
        return g_originalReset(device, parameters);
    }

    HRESULT STDMETHODCALLTYPE HookResetEx(
        IDirect3DDevice9Ex *device,
        D3DPRESENT_PARAMETERS *parameters,
        D3DDISPLAYMODEEX *fullscreenMode)
    {
        duluka::d3d9ex_mailbox::OnDeviceReset(device);
        duluka::d3d9::OnReset(device);
        return g_originalResetEx(device, parameters, fullscreenMode);
    }

    HRESULT STDMETHODCALLTYPE HookPresent(
        IDirect3DDevice9 *device,
        const RECT *sourceRect,
        const RECT *destinationRect,
        HWND destinationWindow,
        const RGNDATA *dirtyRegion)
    {
        g_presentCount.fetch_add(1, std::memory_order_relaxed);
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
        // Poll cumulative timing through the diagnostic export from a
        // non-render thread. Never write logs while forwarding Present.
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
        g_presentCount.fetch_add(1, std::memory_order_relaxed);
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
        // Do not touch filesystem from the game's PresentEx callback.
        return result;
    }

    #include "LateD3D9Detours.inc"
    bool InstallHookedSlot(void **slot, void *hook, void *&original)
    {
        std::lock_guard<std::mutex> lock(g_hookedSlotsMutex);
        void *current = *slot;
        if (current == hook)
            return original != nullptr;
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

    // Cooperative owners MUST unhook their real vtables before Release().
    // A freed D3D9 object can invalidate the heap slot before DLL cleanup.
    // Only restore exact slots we installed, and never overwrite another
    // component's replacement hook.
    bool RestoreOwnedSlot(void **slot, void *hook)
    {
        std::lock_guard<std::mutex> lock(g_hookedSlotsMutex);
        const auto it = std::find_if(g_hookedSlots.begin(),
            g_hookedSlots.end(), [slot, hook](const HookedSlot &entry) {
                return entry.slot == slot && entry.hook == hook;
            });
        if (it == g_hookedSlots.end())
            return false;
        DWORD oldProtection = 0;
        if (!VirtualProtect(slot, sizeof(void *), PAGE_EXECUTE_READWRITE,
                &oldProtection))
            return false;
        void *actual = InterlockedCompareExchangePointer(
            reinterpret_cast<PVOID volatile *>(slot), it->original, hook);
        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void *), oldProtection, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
        if (actual != hook)
            return false;
        g_hookedSlots.erase(it);
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
            const bool presentExInstalled = InstallHookedSlot(
                &vtable[PresentExSlot],
                reinterpret_cast<void *>(&HookPresentEx), originalPresentEx);
            // Even if ResetEx hooking fails, PresentEx needs a valid
            // forwarding target or the next PresentEx would dereference null.
            if (presentExInstalled)
                g_originalPresentEx =
                    reinterpret_cast<PresentExFunction>(originalPresentEx);
            const bool resetExInstalled = InstallHookedSlot(
                &vtable[ResetExSlot],
                reinterpret_cast<void *>(&HookResetEx), originalResetEx);
            if (resetExInstalled)
                g_originalResetEx =
                    reinterpret_cast<ResetExFunction>(originalResetEx);
            installed = installed && presentExInstalled && resetExInstalled;
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
        auto *ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS *>(
            base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE ||
            ntHeaders->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR_MAGIC)
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
            auto *names = reinterpret_cast<IMAGE_THUNK_DATA *>(
                base + originalThunkRva);
            auto *addresses = reinterpret_cast<IMAGE_THUNK_DATA *>(
                base + descriptor[index].FirstThunk);
            for (; names->u1.AddressOfData; ++names, ++addresses)
            {
                if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
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
        // Two independent opt-ins: never install extra graphics callbacks
        // merely because the settings UI selected GPU.
        char receiver[8]{}, endScene[8]{};
        const bool preview =
            GetEnvironmentVariableA(
                "NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW",
                receiver, sizeof(receiver)) == 1 && receiver[0] == '1' &&
            GetEnvironmentVariableA(
                "NVIDIA_OSC_ADAPTER_D3D9EX_ENDSCENE_AUTO_PUMP_PREVIEW",
                endScene, sizeof(endScene)) == 1 && endScene[0] == '1';
        g_exEndScenePumpPreview.store(preview, std::memory_order_release);
        if (preview)
            Log("D3D9Ex EndScene GPU mailbox Pump PREVIEW requested.");
        wchar_t overrideValue[4]{};
        const bool forceLateAttach =
            GetEnvironmentVariableW(L"DULUKA_D3D9_FORCE_LATE_ATTACH",
                overrideValue, static_cast<DWORD>(std::size(overrideValue))) == 1 &&
            overrideValue[0] == L'1';
        if (forceLateAttach)
        {
            const bool installed = InstallLateD3D9CodeDetours();
            g_hookInstalled.store(installed, std::memory_order_release);
            return installed;
        }
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
                // Never count our own D3D9 imports as a game factory hook.
                // The late-attach helper itself imports Direct3DCreate9.
                if (entry.hModule == g_module) continue;
                patched += PatchModuleImports(entry.hModule);
            } while (Module32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
        if (!patched)
        {
            Log("No imported D3D9 factory functions found; attempting code detours.");
            const bool installed = InstallLateD3D9CodeDetours();
            g_hookInstalled.store(installed, std::memory_order_release);
            return installed;
        }

        g_hookInstalled.store(true, std::memory_order_release);
        Log("D3D9 factory imports hooked; created devices will receive Present hooks.");
        return true;
    }

    void RemoveD3D9PresentHook()
    {
        if (!g_hookInstalled.exchange(false, std::memory_order_acq_rel))
            return;
        // Explicit detach requires the caller to quiesce device callbacks.
        // Normal process exit skips cleanup with DllMain(reserved != nullptr).
        if (g_codeDetoursInstalled)
        {
            for (void *target : g_codeDetourTargets)
                MH_DisableHook(target);
            for (void *target : g_codeDetourTargets)
                MH_RemoveHook(target);
            g_codeDetourTargets.clear();
            g_codeDetoursInstalled = false;
            MH_Uninitialize();
        }
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
        g_originalEndScene = nullptr;
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
    duluka::d3d9ex_mailbox::Shutdown();
    duluka::d3d9::Shutdown();
}

// Optional cooperative late attachment. A trusted in-process owner that
// already has a valid D3D9/9Ex device can register that exact device. Do not
// infer arbitrary live device addresses from a temporary D3D9 instance:
// Windows D3D9 implementations may allocate distinct per-instance vtables.
// This is NOT automatic discovery of pre-existing game devices.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterRegisterD3D9Device(
    IDirect3DDevice9 *device)
{
    if (!device || !g_hookInstalled.load(std::memory_order_acquire))
        return FALSE;

    IDirect3D9 *owner = nullptr;
    if (FAILED(device->GetDirect3D(&owner)) || !owner)
        return FALSE;
    owner->Release();

    IDirect3DDevice9Ex *deviceEx = nullptr;
    const bool isEx = SUCCEEDED(device->QueryInterface(
        __uuidof(IDirect3DDevice9Ex),
        reinterpret_cast<void **>(&deviceEx))) && deviceEx;
    if (deviceEx)
        deviceEx->Release();

    const bool installed = PatchDevice(device, isEx);
    Log(installed
        ? (isEx ? "Cooperative existing D3D9Ex device registered."
                : "Cooperative existing D3D9 device registered.")
        : "Cooperative D3D9 device registration failed.");
    return installed ? TRUE : FALSE;
}

// Unregister before the owner releases its D3D9 device. The cooperating
// caller must guarantee that no other thread is inside Present/Reset during
// this operation; removing vtable pointers alone is not a quiescence protocol.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterUnregisterD3D9Device(
    IDirect3DDevice9 *device)
{
    if (!device)
        return FALSE;
    auto vtable = *reinterpret_cast<void ***>(device);
    const bool presentRestored = RestoreOwnedSlot(
        &vtable[PresentSlot], reinterpret_cast<void *>(&HookPresent));
    const bool resetRestored = RestoreOwnedSlot(
        &vtable[ResetSlot], reinterpret_cast<void *>(&HookReset));
    IDirect3DDevice9Ex *deviceEx = nullptr;
    const bool isEx = SUCCEEDED(device->QueryInterface(
        __uuidof(IDirect3DDevice9Ex),
        reinterpret_cast<void **>(&deviceEx))) && deviceEx;
    if (deviceEx)
        deviceEx->Release();
    bool exRestored = true;
    if (isEx)
    {
        const bool p = RestoreOwnedSlot(
            &vtable[PresentExSlot], reinterpret_cast<void *>(&HookPresentEx));
        const bool r = RestoreOwnedSlot(
            &vtable[ResetExSlot], reinterpret_cast<void *>(&HookResetEx));
        exRestored = p && r;
    }
    const bool success = presentRestored && resetRestored && exRestored;
    Log(success ? "Cooperative D3D9 device unregistered before release."
                : "Cooperative D3D9 unregister incomplete or not owned.");
    return success ? TRUE : FALSE;
}

// Opt-in mailbox Pump is deliberately explicit. Call from a trusted
// game-device thread at a safe scheduling point, not from Present itself.
// Host writes readyGeneration after its D3D11 GPU completion query.
// By default the receiver is disabled and CPU Shared Memory remains active.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterPumpD3D9ExGpuMailbox(
    IDirect3DDevice9Ex *device)
{
    return duluka::d3d9ex_mailbox::Pump(device) ? TRUE : FALSE;
}

// Optional D3D9Ex GPU frame input. A trusted same-process bridge can
// pass a legacy, BGRA8 shared handle published by OSC Host in the future.
// This intentionally does not change the OSC Host or current CPU bridge.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterBindD3D9ExGpuFrame(
    IDirect3DDevice9Ex *device, HANDLE sharedHandle, std::uint32_t width,
    std::uint32_t height, std::uint64_t frameNumber)
{
    return duluka::d3d9::BindExSharedGpuTexture(device, sharedHandle,
        width, height, frameNumber) ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterUnbindD3D9ExGpuFrame(
    IDirect3DDevice9Ex *device)
{
    return duluka::d3d9::UnbindExSharedGpuTexture(device) ? TRUE : FALSE;
}

// Two-phase, opt-in GPU handoff. Stage merely opens the next texture.
// The caller must verify GPU writes have finished BEFORE Commit; Hook
// never waits for that event in game Present or on this API.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterStageD3D9ExGpuFrame(
    IDirect3DDevice9Ex *device, HANDLE sharedHandle, std::uint32_t width,
    std::uint32_t height, std::uint64_t frameNumber)
{
    return duluka::d3d9::StageExSharedGpuTexture(device, sharedHandle,
        width, height, frameNumber) ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterCommitD3D9ExGpuFrame(
    IDirect3DDevice9Ex *device, std::uint64_t frameNumber)
{
    return duluka::d3d9::CommitExSharedGpuTexture(device, frameNumber)
        ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterDiscardD3D9ExPendingGpuFrame(
    IDirect3DDevice9Ex *device)
{
    return duluka::d3d9::DiscardExPendingGpuTexture(device) ? TRUE : FALSE;
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

// Phase 0 read-only report: actual game device adapter and API.
// Keep the existing ConsumerAdapterInfo v2 wire layout/renderApi=9 intact.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9AdapterStatus(
    std::uint64_t *adapterLuid, BOOL *isD3D9Ex, BOOL *gpuFrameBound,
    std::uint64_t *busyFrameSkips)
{
    if (!adapterLuid || !isD3D9Ex || !gpuFrameBound || !busyFrameSkips)
        return FALSE;
    bool ex = false, gpu = false;
    duluka::d3d9::GetAdapterStatus(*adapterLuid, ex, gpu, *busyFrameSkips);
    *isD3D9Ex = ex ? TRUE : FALSE;
    *gpuFrameBound = gpu ? TRUE : FALSE;
    return TRUE;
}

// Read-only status for the optional D3D9Ex EndScene GPU receiver.
// A Pump callback is not proof that the frame appeared on the monitor.
#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:NvOscAdapterGetD3D9ExAutoPumpStatus=_NvOscAdapterGetD3D9ExAutoPumpStatus@12")
#endif
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9ExAutoPumpStatus(
    BOOL *enabled, std::uint64_t *calls, std::uint64_t *commits)
{
    if (!enabled || !calls || !commits)
        return FALSE;
    *enabled = g_exEndScenePumpPreview.load(std::memory_order_acquire)
        ? TRUE : FALSE;
    *calls = g_exEndSceneCalls.load(std::memory_order_relaxed);
    *commits = g_exEndSceneCommits.load(std::memory_order_relaxed);
    return TRUE;
}

// Read-only, non-destructive timing counters. This function is intended
// for out-of-band diagnostics: no logging, heap allocations or reset of
// counters occurs in either D3D9 Present callback.
extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetD3D9TimingSnapshot(
    duluka::diagnostics::D3D9TimingSnapshotV1 *output, DWORD outputBytes)
{
    using duluka::diagnostics::D3D9TimingSnapshotV1;
    if (!output || outputBytes != sizeof(D3D9TimingSnapshotV1))
        return FALSE;

    D3D9TimingSnapshotV1 snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = duluka::diagnostics::D3D9TimingVersion;
    snapshot.performanceFrequency = QueryPerformanceFrequencyValue();
    snapshot.presents = g_presentCount.load(std::memory_order_relaxed);
    snapshot.draws = duluka::d3d9::GetDrawCount();
    snapshot.overlaySamples = g_overlayCallbackSamples.load(
        std::memory_order_relaxed);
    snapshot.overlayTotalTicks = g_overlayCallbackTicks.load(
        std::memory_order_relaxed);
    snapshot.overlayMaxTicks = g_overlayCallbackMaxTicks.load(
        std::memory_order_relaxed);
    snapshot.gamePresentSamples = g_originalPresentSamples.load(
        std::memory_order_relaxed);
    snapshot.gamePresentTotalTicks = g_originalPresentTicks.load(
        std::memory_order_relaxed);
    snapshot.gamePresentMaxTicks = g_originalPresentMaxTicks.load(
        std::memory_order_relaxed);
    *output = snapshot;
    return TRUE;
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
