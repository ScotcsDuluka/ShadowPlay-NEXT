// Late-attach implementation fallback, intentionally included after HookPresentEx.
// This implementation detours code entrypoints (MinHook x86/x64), not a dummy
// device's heap vtable. A cooperative owner may still register its own vtable.
bool InstallLateD3D9CodeDetours()
{
    HWND window = CreateWindowExW(0, L"STATIC", L"Duluka DX9 test device",
        WS_POPUP, -2000, -2000, 64, 64, nullptr, nullptr, g_module, nullptr);
    if (!window)
    {
        Log("Late D3D9 fallback could not create temporary window.");
        return false;
    }
    D3DPRESENT_PARAMETERS parameters{};
    parameters.Windowed = TRUE;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
    parameters.BackBufferWidth = 64;
    parameters.BackBufferHeight = 64;
    parameters.BackBufferCount = 1;
    parameters.BackBufferFormat = D3DFMT_UNKNOWN;
    parameters.hDeviceWindow = window;
    parameters.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

    IDirect3D9 *factory = Direct3DCreate9(D3D_SDK_VERSION);
    IDirect3DDevice9 *device = nullptr;
    if (factory)
        factory->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
            window, D3DCREATE_SOFTWARE_VERTEXPROCESSING |
            D3DCREATE_MULTITHREADED, &parameters, &device);
    if (!device)
    {
        if (factory) factory->Release();
        DestroyWindow(window);
        Log("Late D3D9 fallback could not create temporary device.");
        return false;
    }

    IDirect3D9Ex *factoryEx = nullptr;
    IDirect3DDevice9Ex *deviceEx = nullptr;
    if (SUCCEEDED(Direct3DCreate9Ex(D3D_SDK_VERSION, &factoryEx)) &&
        factoryEx)
        factoryEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
            window, D3DCREATE_SOFTWARE_VERTEXPROCESSING |
            D3DCREATE_MULTITHREADED, &parameters, nullptr, &deviceEx);

    void **vtable = *reinterpret_cast<void ***>(device);
    void **vtableEx = deviceEx
        ? *reinterpret_cast<void ***>(deviceEx) : nullptr;
    const MH_STATUS initialize = MH_Initialize();
    bool ok = initialize == MH_OK ||
        initialize == MH_ERROR_ALREADY_INITIALIZED;
    std::vector<void *> targets;
    auto create = [&targets, &ok](void *address, void *detour, void **original)
    {
        if (!ok || !address)
        {
            ok = false;
            return;
        }
        if (MH_CreateHook(address, detour, original) != MH_OK)
        {
            ok = false;
            return;
        }
        targets.push_back(address);
    };

    if (ok)
    {
        create(vtable[PresentSlot], reinterpret_cast<void *>(&HookPresent),
            reinterpret_cast<void **>(&g_originalPresent));
        create(vtable[ResetSlot], reinterpret_cast<void *>(&HookReset),
            reinterpret_cast<void **>(&g_originalReset));
        if (vtableEx)
        {
            if (g_exEndScenePumpPreview.load(std::memory_order_acquire))
            {
                create(vtableEx[EndSceneSlot],
                    reinterpret_cast<void *>(&HookEndScene),
                    reinterpret_cast<void **>(&g_originalEndScene));
            }
            create(vtableEx[PresentExSlot],
                reinterpret_cast<void *>(&HookPresentEx),
                reinterpret_cast<void **>(&g_originalPresentEx));
            create(vtableEx[ResetExSlot],
                reinterpret_cast<void *>(&HookResetEx),
                reinterpret_cast<void **>(&g_originalResetEx));
        }
    }

    // MinHook does not enable target code patching until all trampoline
    // pointers have been obtained. Enable as one queued transaction.
    if (ok)
    {
        for (void *address : targets)
        {
            if (MH_QueueEnableHook(address) != MH_OK)
            {
                ok = false;
                break;
            }
        }
        if (ok) ok = MH_ApplyQueued() == MH_OK;
    }
    if (!ok)
    {
        for (void *address : targets)
        {
            MH_DisableHook(address);
            MH_RemoveHook(address);
        }
        if (initialize == MH_OK) MH_Uninitialize();
        g_originalPresent = nullptr;
        g_originalReset = nullptr;
        g_originalEndScene = nullptr;
        g_originalPresentEx = nullptr;
        g_originalResetEx = nullptr;
    }
    else
    {
        g_codeDetourTargets = std::move(targets);
        g_codeDetoursInstalled = true;
    }

    // Do not keep the dummy objects: only the implementation code addresses
    // from the active d3d9.dll are detoured.
    if (deviceEx) deviceEx->Release();
    if (factoryEx) factoryEx->Release();
    device->Release();
    factory->Release();
    DestroyWindow(window);
    Log(ok
        ? (vtableEx ? "Late D3D9/D3D9Ex implementation detours installed."
                    : "Late D3D9 implementation detours installed.")
        : "Late D3D9 implementation detour failed.");
    return ok;
}