#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <strsafe.h>
#include <psapi.h>

#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string>
#include "../../../include/osc_frame_bridge.h"
#include "../../Renderer/D3D11/D3D11Renderer.h"
#include <reshade.hpp>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

void InitializeD3D9PresentHook(HMODULE module);
void ShutdownD3D9PresentHook();

namespace
{
    using PresentFunction = HRESULT(STDMETHODCALLTYPE *)(IDXGISwapChain *, UINT, UINT);
    using Present1Function = HRESULT(STDMETHODCALLTYPE *)(
        IDXGISwapChain1 *, UINT, UINT, const DXGI_PRESENT_PARAMETERS *);
    constexpr wchar_t WindowClassName[] = L"DulukaNvOscAdapterProbe";
    std::atomic<std::uint64_t> g_presentCount{ 0 };
    std::atomic<std::uint64_t> g_contendedPresentCount{ 0 };
    std::atomic<std::uint64_t> g_reentrantPresentCount{ 0 };
    // Kept for exported ABI compatibility. ResizeBuffers is deliberately not
    // intercepted; the compositor reacquires/releases the back buffer per Present.
    std::atomic<std::uint64_t> g_resizeBuffersCount{ 0 };
    std::atomic<bool> g_hookInstalled{ false };
    std::atomic<bool> g_reshadeAddonRegistered{ false };
    std::atomic<bool> g_firstPresentLogged{ false };
    std::atomic<bool> g_recursiveFallbackLogged{ false };
    thread_local unsigned int g_presentDepth = 0;
    thread_local bool g_recursiveFallbackActive = false;
    PresentFunction g_originalPresent = nullptr;
    void **g_presentSlot = nullptr;
    HMODULE g_module = nullptr;






































    void Log(const char *message);
    HRESULT STDMETHODCALLTYPE HookPresent(
        IDXGISwapChain *swapChain,
        UINT syncInterval,
        UINT flags);

    bool GetModulePathForAddress(const void *address, char *path, std::size_t pathSize)
    {
        HMODULE owner = nullptr;
        if (!address || !path || pathSize == 0 ||
            !GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(address),
                &owner))
        {
            return false;
        }

        const DWORD length = GetModuleFileNameA(owner, path, static_cast<DWORD>(pathSize));
        if (length == 0 || length >= pathSize)
        {
            path[0] = '\0';
            return false;
        }
        return true;
    }

    bool IsSystemDxgiModulePath(const char *path)
    {
        if (!path || !*path)
            return false;

        char systemDirectory[MAX_PATH]{};
        const UINT systemDirectoryLength = GetSystemDirectoryA(
            systemDirectory, static_cast<UINT>(std::size(systemDirectory)));
        if (systemDirectoryLength == 0 ||
            systemDirectoryLength >= std::size(systemDirectory))
            return false;

        char systemDxgiPath[MAX_PATH]{};
        const int pathLength = sprintf_s(
            systemDxgiPath, sizeof(systemDxgiPath), "%s%c%s",
            systemDirectory, static_cast<char>(92), "dxgi.dll");
        return pathLength > 0 && _stricmp(path, systemDxgiPath) == 0;
    }

    HRESULT ForwardRecursivePresent(
        IDXGISwapChain *swapChain,
        UINT syncInterval,
        UINT flags)
    {
        if (g_recursiveFallbackActive)
            return DXGI_ERROR_INVALID_CALL;

        struct RecursiveFallbackScope
        {
            RecursiveFallbackScope() { g_recursiveFallbackActive = true; }
            ~RecursiveFallbackScope() { g_recursiveFallbackActive = false; }
        } fallbackScope;

        IDXGISwapChain1 *swapChain1 = nullptr;
        const HRESULT queryResult = swapChain->QueryInterface(
            __uuidof(IDXGISwapChain1),
            reinterpret_cast<void **>(&swapChain1));
        if (FAILED(queryResult) || !swapChain1)
        {
            if (!g_recursiveFallbackLogged.exchange(true, std::memory_order_relaxed))
            {
                Log("Recursive Present detected, but IDXGISwapChain1 is unavailable; "
                    "the recursive chain was stopped to prevent stack exhaustion.");
            }
            return DXGI_ERROR_INVALID_CALL;
        }

        auto vtable = *reinterpret_cast<void ***>(swapChain1);
        const auto present1 = reinterpret_cast<Present1Function>(vtable[22]);
        char ownerPath[MAX_PATH]{};
        const bool hasOwner = GetModulePathForAddress(
            reinterpret_cast<const void *>(present1), ownerPath, sizeof(ownerPath));
        const char *ownerName = hasOwner
            ? std::strrchr(ownerPath, '\\')
            : nullptr;
        ownerName = ownerName ? ownerName + 1 : ownerPath;
        const bool isDxgiImplementation =
            hasOwner && _stricmp(ownerName, "dxgi.dll") == 0;

        if (present1 && isDxgiImplementation &&
            reinterpret_cast<void *>(present1) != reinterpret_cast<void *>(&HookPresent) &&
            reinterpret_cast<void *>(present1) != reinterpret_cast<void *>(g_originalPresent))
        {
            if (!g_recursiveFallbackLogged.exchange(true, std::memory_order_relaxed))
            {
                char diagnostic[512]{};
                sprintf_s(diagnostic, sizeof(diagnostic),
                    "Recursive Present detected; forwarding once through the unhooked "
                        "IDXGISwapChain1::Present1 entry at %p (%s).",
                    reinterpret_cast<void *>(present1), ownerName);
                Log(diagnostic);
            }
            const DXGI_PRESENT_PARAMETERS parameters{};
            const HRESULT result = present1(
                    swapChain1, syncInterval, flags, &parameters);
            swapChain1->Release();
            return result;
        }

        if (!g_recursiveFallbackLogged.exchange(true, std::memory_order_relaxed))
        {
            char diagnostic[512]{};
            sprintf_s(diagnostic, sizeof(diagnostic),
                "Recursive Present detected, but no independent DXGI "
                "IDXGISwapChain1::Present1 entry was available "
                "(target=%p module=%s); stopping the recursive chain.",
                reinterpret_cast<void *>(present1),
                hasOwner ? ownerName : "unknown");
            Log(diagnostic);
        }
        swapChain1->Release();
        return DXGI_ERROR_INVALID_CALL;
    }

    void Log(const char *message)
    {
        OutputDebugStringA("[NVIDIA OSC Adapter] ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");

        wchar_t localAppData[MAX_PATH]{};
        const DWORD pathLength = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
        if (!pathLength || pathLength >= std::size(localAppData))
            return;

        wchar_t directory[MAX_PATH]{};
        if (FAILED(StringCchCopyW(directory, std::size(directory), localAppData)) ||
            FAILED(StringCchCatW(directory, std::size(directory), L"\\NVIDIA")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\OSC Adapter")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\Logs")))
            return;
        CreateDirectoryW(directory, nullptr);
        if (FAILED(StringCchCatW(directory, std::size(directory), L"\\adapter-hook-")))
            return;

        wchar_t filePath[MAX_PATH]{};
        if (FAILED(StringCchCopyW(filePath, std::size(filePath), directory)))
            return;
        wchar_t processId[16]{};
        if (FAILED(StringCchPrintfW(processId, std::size(processId), L"%lu.log",
                GetCurrentProcessId())) ||
            FAILED(StringCchCatW(filePath, std::size(filePath), processId)))
            return;

        HANDLE file = CreateFileW(filePath, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME time{};
        GetSystemTime(&time);
        char line[2048]{};
        const int length = sprintf_s(line, sizeof(line),
            "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ PID=%lu %s\r\n",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
            time.wSecond, time.wMilliseconds, GetCurrentProcessId(), message);
        if (length > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        }
        CloseHandle(file);
    }





















    bool IsGameProcessForeground()
    {
        wchar_t testMappingName[128]{};
        wchar_t expectedTestMappingName[128]{};
        if (GetEnvironmentVariableW(
                osc_frame_bridge::ConsumerAdapterMappingNameEnvironmentVariable,
                testMappingName, static_cast<DWORD>(std::size(testMappingName))) > 0 &&
            swprintf_s(expectedTestMappingName,
                std::size(expectedTestMappingName),
                L"Local\\NVIDIAOSC.Frame.Test.ConsumerAdapter.%lu",
                GetCurrentProcessId()) >= 0 &&
            wcscmp(testMappingName, expectedTestMappingName) == 0)
            return true;
        return osc_frame_bridge::IsGameOrNvidiaOverlayForeground(
            GetCurrentProcessId());
    }

    void OnReShadePresent(reshade::api::effect_runtime *runtime)
    {
        if (!runtime)
            return;
        reshade::api::device *reshadeDevice = runtime->get_device();
        if (!reshadeDevice ||
            reshadeDevice->get_api() != reshade::api::device_api::d3d11)
            return;
        auto *device = reinterpret_cast<ID3D11Device *>(
            static_cast<std::uintptr_t>(reshadeDevice->get_native()));
        if (!device)
            return;
        const auto backBufferHandle = runtime->get_current_back_buffer().handle;
        auto *backBuffer = reinterpret_cast<ID3D11Texture2D *>(
            static_cast<std::uintptr_t>(backBufferHandle));
        if (!backBuffer)
            return;
        duluka::d3d11::OnReShadePresent(device, backBuffer, IsGameProcessForeground());
    }

    // Defined below UnregisterReShadeAddon; declared here so the failure
    // reporting inside RegisterReShadeAddon can classify the reason (4.3).
    bool ProbeReShadeModule(bool *moduleFound, bool *addonApiAvailable);

    bool RegisterReShadeAddon()
    {
        if (g_reshadeAddonRegistered.exchange(true, std::memory_order_acq_rel))
            return true;
        if (!reshade::register_addon(g_module))
        {
            g_reshadeAddonRegistered.store(false, std::memory_order_release);
            bool moduleFound = false, addonApiAvailable = false;
            ProbeReShadeModule(&moduleFound, &addonApiAvailable);
            if (!moduleFound)
                Log("ReShade not present in this process; staying on the "
                    "ReShade-independent DXGI compositor.");
            else if (!addonApiAvailable)
                Log("ReShade present but without the add-on/ImGui exports; staying "
                    "on the ReShade-independent DXGI compositor.");
            else
                Log("ReShade add-on registration failed unexpectedly; staying on "
                    "the ReShade-independent DXGI compositor.");
            return false;
        }

        reshade::register_event<reshade::addon_event::reshade_present>(
            &OnReShadePresent);
        Log("[compositor] enhancement=reshade-addon active; OSC renders after "
            "ReShade effects (raw hook is pass-through).");
        return true;
    }

    void UnregisterReShadeAddon()
    {
        if (!g_reshadeAddonRegistered.exchange(false, std::memory_order_acq_rel))
            return;

        reshade::unregister_event<reshade::addon_event::reshade_present>(
            &OnReShadePresent);
        reshade::unregister_addon(g_module);
    }

    // ReShade independence: probe loaded modules without going through the
    // add-on registration itself, so the reason can be reported (4.3).
    bool ProbeReShadeModule(bool *moduleFound, bool *addonApiAvailable)
    {
        HMODULE modules[1024]{};
        DWORD needed = 0;
        *moduleFound = false;
        *addonApiAvailable = false;
        if (!EnumProcessModules(GetCurrentProcess(), modules,
                sizeof(modules), &needed))
            return false;
        const DWORD loaded = needed / sizeof(HMODULE);
        const DWORD count = loaded < 1024u ? loaded : 1024u;
        for (DWORD i = 0; i < count; ++i)
        {
            const bool hasRegister =
                GetProcAddress(modules[i], "ReShadeRegisterAddon") != nullptr;
            const bool hasImGui =
                GetProcAddress(modules[i], "ReShadeGetImGuiFunctionTable") != nullptr;
            if (hasRegister && hasImGui)
            {
                *moduleFound = true;
                *addonApiAvailable = true;
                return true;
            }
            if (hasRegister)
            {
                *moduleFound = true;
                return true;
            }
            char path[MAX_PATH]{};
            if (GetModuleFileNameA(modules[i], path, MAX_PATH))
            {
                const char *name = std::strrchr(path, '\\');
                name = name ? name + 1 : path;
                if (_stricmp(name, "ReShade64.dll") == 0 ||
                    _stricmp(name, "ReShade32.dll") == 0)
                {
                    *moduleFound = true;
                    return true;
                }
            }
        }
        return false;
    }

    std::atomic<bool> g_reshadeLateWatchStarted{ false };

    DWORD WINAPI ReShadeLateRegisterWatch(void *)
    {
        for (int attempt = 1; attempt <= 30; ++attempt)
        {
            Sleep(2000);
            if (g_reshadeAddonRegistered.load(std::memory_order_acquire))
                return 0;
            bool moduleFound = false, addonApiAvailable = false;
            ProbeReShadeModule(&moduleFound, &addonApiAvailable);
            if (!moduleFound)
                continue; // ReShade still not loaded; keep waiting
            if (!addonApiAvailable)
            {
                Log("ReShade present but built without add-on support; staying on "
                    "the ReShade-independent DXGI compositor.");
                return 0;
            }
            if (RegisterReShadeAddon())
            {
                Log("[compositor] enhancement=reshade-addon active (late register); "
                    "raw hook stays as pass-through.");
            }
            return 0;
        }
        Log("[compositor] no ReShade module appeared within 60s; staying on the "
            "ReShade-independent DXGI compositor.");
        return 0;
    }

    void StartReShadeLateRegisterWatch()
    {
        if (g_reshadeLateWatchStarted.exchange(true, std::memory_order_acq_rel))
            return;
        HANDLE thread = CreateThread(
            nullptr, 0, ReShadeLateRegisterWatch, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
        else
            OutputDebugStringA("[NVIDIA OSC Adapter] Failed to start the ReShade "
                "late register watch thread.\n");
    }

    HRESULT STDMETHODCALLTYPE HookPresent(
        IDXGISwapChain *swapChain,
        UINT syncInterval,
        UINT flags)
    {
        struct PresentDepthScope
        {
            PresentDepthScope() { ++g_presentDepth; }
            ~PresentDepthScope() { --g_presentDepth; }
        } depthScope;
        const bool isReentrant = g_presentDepth > 1;
        const auto presentCount = g_presentCount.fetch_add(1, std::memory_order_relaxed) + 1;
        if (!g_firstPresentLogged.exchange(true, std::memory_order_relaxed))
            Log("DXGI Present hook received its first frame callback.");
        if (isReentrant)
        {
            g_reentrantPresentCount.fetch_add(1, std::memory_order_relaxed);
            return ForwardRecursivePresent(swapChain, syncInterval, flags);
        }
        const bool lockAcquired = duluka::d3d11::OnPresent(
            swapChain, IsGameProcessForeground(),
            g_reshadeAddonRegistered.load(std::memory_order_acquire));
        if (!lockAcquired)
            g_contendedPresentCount.fetch_add(1, std::memory_order_relaxed);
        else if (presentCount == 1 || presentCount % 1200 == 0)
        {
            const auto status = duluka::d3d11::GetStatus();
            char diagnostic[512]{};
            sprintf_s(diagnostic, sizeof(diagnostic),
                "Present diagnostics: presents=%llu draws=%llu busy=%llu reentrant=%llu "
                "bridge=%s texture=%s stage=%s hr=0x%08lX swap=%ux%u error=0x%08lX",
                static_cast<unsigned long long>(presentCount),
                static_cast<unsigned long long>(status.drawCount),
                static_cast<unsigned long long>(g_contendedPresentCount.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(g_reentrantPresentCount.load(std::memory_order_relaxed)),
                status.bridgeConnected ? "connected" : "disconnected",
                status.textureReady ? "ready" : "not-ready",
                status.diagnosticStage ? status.diagnosticStage : "unknown",
                static_cast<unsigned long>(status.diagnosticHresult),
                status.swapWidth, status.swapHeight,
                static_cast<unsigned long>(status.lastError));
            Log(diagnostic);
        }
        return g_originalPresent(swapChain, syncInterval, flags);
    }

    // ResizeBuffers is intentionally not intercepted. The in-game compositor
    // reacquires/releases its back-buffer view during each Present; letting the
    // existing DXGI/overlay hook chain own ResizeBuffers avoids recursive
    // vtable re-entry through non-recursive render synchronization.

    bool PatchVtableSlot(void **slot, void *expected, void *replacement)
    {
        DWORD oldProtection = 0;
        if (!VirtualProtect(slot, sizeof(void *),
                PAGE_EXECUTE_READWRITE, &oldProtection))
            return false;

        const auto previous = InterlockedCompareExchangePointer(
            reinterpret_cast<PVOID volatile *>(slot), replacement, expected);
        DWORD ignoredProtection = 0;
        const BOOL protectionRestored = VirtualProtect(
            slot, sizeof(void *), oldProtection, &ignoredProtection);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
        return previous == expected && protectionRestored;
    }

    void RestoreVtableSlot(void **slot, void *hook, void *original)
    {
        if (!slot)
            return;
        DWORD oldProtection = 0;
        if (!VirtualProtect(slot, sizeof(void *),
                PAGE_EXECUTE_READWRITE, &oldProtection))
        {
            Log("VirtualProtect failed while removing a DXGI vtable hook.");
            return;
        }

        const auto current = InterlockedCompareExchangePointer(
            reinterpret_cast<PVOID volatile *>(slot), original, hook);
        DWORD ignoredProtection = 0;
        VirtualProtect(slot, sizeof(void *), oldProtection, &ignoredProtection);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
        if (current != hook && current != original)
            Log("A DXGI vtable slot changed before hook removal; leaving the newer hook intact.");
    }

    LRESULT CALLBACK ProbeWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool InstallDxgiPresentHook()
    {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = ProbeWindowProc;
        windowClass.hInstance = g_module;
        windowClass.lpszClassName = WindowClassName;

        const ATOM classAtom = RegisterClassExW(&windowClass);
        if (classAtom == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            Log("RegisterClassExW failed while locating the DXGI Present slot.");
            return false;
        }

        HWND window = CreateWindowExW(
            0, WindowClassName, L"", WS_OVERLAPPED,
            0, 0, 2, 2, nullptr, nullptr, g_module, nullptr);
        if (!window)
        {
            Log("CreateWindowExW failed while locating the DXGI Present slot.");
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        DXGI_SWAP_CHAIN_DESC swapChainDesc{};
        swapChainDesc.BufferDesc.Width = 2;
        swapChainDesc.BufferDesc.Height = 2;
        swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swapChainDesc.SampleDesc.Count = 1;
        swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapChainDesc.BufferCount = 1;
        swapChainDesc.OutputWindow = window;
        swapChainDesc.Windowed = TRUE;
        swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ID3D11Device *device = nullptr;
        ID3D11DeviceContext *context = nullptr;
        IDXGISwapChain *swapChain = nullptr;
        D3D_FEATURE_LEVEL featureLevel{};
        HRESULT result = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &swapChainDesc,
            &swapChain,
            &device,
            &featureLevel,
            &context);

        if (FAILED(result))
        {
            result = D3D11CreateDeviceAndSwapChain(
                nullptr,
                D3D_DRIVER_TYPE_WARP,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                &swapChainDesc,
                &swapChain,
                &device,
                &featureLevel,
                &context);
        }

        if (FAILED(result) || !swapChain)
        {
            Log("Could not create a temporary DXGI swapchain to resolve its Present slot.");
            if (context) context->Release();
            if (device) device->Release();
            if (swapChain) swapChain->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        auto vtable = *reinterpret_cast<void ***>(swapChain);
        void **presentSlot = &vtable[8];
        auto originalPresent = reinterpret_cast<PresentFunction>(*presentSlot);
        if (originalPresent == &HookPresent)
        {
            Log("DXGI Present is already hooked; refusing a recursive second installation.");
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }
        g_originalPresent = originalPresent;
        g_presentSlot = presentSlot;
        char originalOwnerPath[MAX_PATH]{};
        const bool hasOriginalOwner = GetModulePathForAddress(
            reinterpret_cast<const void *>(originalPresent),
            originalOwnerPath,
            sizeof(originalOwnerPath));
        char chainDiagnostic[512]{};
        sprintf_s(chainDiagnostic, sizeof(chainDiagnostic),
            "DXGI Present chain target=%p module=%s.",
            reinterpret_cast<void *>(originalPresent),
            hasOriginalOwner ? originalOwnerPath : "unknown");
        Log(chainDiagnostic);

        const char *originalOwnerName = hasOriginalOwner
            ? std::strrchr(originalOwnerPath, static_cast<char>(92))
            : nullptr;
        originalOwnerName = originalOwnerName ? originalOwnerName + 1
            : (hasOriginalOwner ? originalOwnerPath : "");
        if (!IsSystemDxgiModulePath(originalOwnerPath))
        {
            char ownershipDiagnostic[512]{};
            sprintf_s(ownershipDiagnostic, sizeof(ownershipDiagnostic),
                "[compositor] Present entry is owned by a non-system module (%s); "
                "hooking with steal-and-forward so the previous owner keeps running "
                "through the saved original (ReShade-independent).",
                hasOriginalOwner ? originalOwnerName : "unknown");
            Log(ownershipDiagnostic);
        }

        if (!PatchVtableSlot(
                presentSlot,
                reinterpret_cast<void *>(originalPresent),
                reinterpret_cast<void *>(&HookPresent)))
        {
            g_originalPresent = nullptr;
            g_presentSlot = nullptr;
            Log("Could not install the DXGI Present vtable hook.");
            swapChain->Release();
            context->Release();
            device->Release();
            DestroyWindow(window);
            if (classAtom) UnregisterClassW(WindowClassName, g_module);
            return false;
        }

        // Leave IDXGISwapChain::ResizeBuffers untouched. Existing hook chains
        // may call back through that shared vtable slot; the adapter must not
        // wrap the same entry a second time.
        g_hookInstalled.store(true, std::memory_order_release);

        swapChain->Release();
        context->Release();
        device->Release();
        DestroyWindow(window);
        if (classAtom) UnregisterClassW(WindowClassName, g_module);

        Log("DXGI IDXGISwapChain::Present hook installed (x64); ResizeBuffers remains pass-through.");
        return true;
    }

    DWORD WINAPI InitializeHook(void *)
    {
        duluka::d3d11::Initialize(&Log);
        // Primary compositor: raw DXGI Present vtable hook. Zero dependencies:
        // works with no ReShade, proxy ReShade, or add-on-support ReShade.
        if (InstallDxgiPresentHook())
            Log("[compositor] mode=raw-present (primary; ReShade-independent)");
        else
            Log("[compositor] mode=none (raw hook failed to install)");
        // Opportunistic enhancement: add-on mode draws AFTER ReShade effects.
        if (RegisterReShadeAddon())
        {
            // raw hook stays installed; OnPresent skips its draw while the
            // add-on event handles composition (single draw, on top).
        }
        else
        {
            StartReShadeLateRegisterWatch();
        }
        InitializeD3D9PresentHook(g_module);
        return 0;
    }

    void RemoveDxgiPresentHook()
    {
        if (!g_hookInstalled.exchange(false, std::memory_order_acq_rel))
            return;

        RestoreVtableSlot(
            g_presentSlot,
            reinterpret_cast<void *>(&HookPresent),
            reinterpret_cast<void *>(g_originalPresent));
        Log("DXGI Present hook removed (ResizeBuffers was left untouched).");
    }
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetStatus(
    std::uint64_t *presentCount,
    BOOL *hookInstalled)
{
    if (!presentCount || !hookInstalled)
        return FALSE;
    *presentCount = g_presentCount.load(std::memory_order_relaxed);
    *hookInstalled = g_hookInstalled.load(std::memory_order_acquire) ? TRUE : FALSE;
    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetResizeStatus(
    std::uint64_t *resizeCount)
{
    if (!resizeCount)
        return FALSE;
    *resizeCount = g_resizeBuffersCount.load(std::memory_order_relaxed);
    return TRUE;
}

extern "C" __declspec(dllexport) BOOL WINAPI NvOscAdapterGetOverlayStatus(
    std::uint64_t *drawCount,
    BOOL *bridgeConnected,
    BOOL *textureReady,
    long *lastError)
{
    if (!drawCount || !bridgeConnected || !textureReady || !lastError)
        return FALSE;
    const auto status = duluka::d3d11::GetStatus();
    *drawCount = status.drawCount;
    *bridgeConnected = status.bridgeConnected ? TRUE : FALSE;
    *textureReady = status.textureReady ? TRUE : FALSE;
    *lastError = status.lastError;
    return TRUE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, InitializeHook, nullptr, 0, nullptr);
        if (thread)
            CloseHandle(thread);
        else
            OutputDebugStringA("[NVIDIA OSC Adapter] Failed to start hook initialization thread.\n");
    }
    else if (reason == DLL_PROCESS_DETACH && reserved == nullptr)
    {
        // Only explicit FreeLibrary reaches this cleanup path. Process
        // termination supplies a non-null reserved pointer and skips this
        // branch; OS teardown reclaims process-owned resources without ReShade
        // calls or vtable edits under loader lock.
        UnregisterReShadeAddon();
        RemoveDxgiPresentHook();
        ShutdownD3D9PresentHook();
    }
    return TRUE;
}
