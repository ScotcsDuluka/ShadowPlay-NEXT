# Duluka Hook — API-specific architecture

## Goal

Each graphics API has an independent Hook path and Renderer path. Detection must identify the API actually used by a running process and report confidence/evidence; it must not infer support from a game name or executable name alone.

## Layout

    src/
      Detection/                 Runtime graphics-API detection
      Common/                    API-neutral utilities and contracts
      Hook/
        D3D9/D3D9Hook.cpp        D3D9/9Ex interception and Hook lifecycle
        D3D10/                   Planned
        D3D11/D3D11Hook.cpp      DXGI Present interception and Hook lifecycle
        D3D12/                   Planned
        Vulkan/                  Planned
        OpenGL/                  Planned
        DXGI/                    Shared DXGI notes/contracts; not a seventh graphics API
      Renderer/
        D3D9/                    Active D3D9 composition renderer implementation
        D3D10/                   Planned
        D3D11/                   Active D3D11 composition renderer implementation
        D3D12/                   Planned
        Vulkan/                  Planned
        OpenGL/                  Planned
    include/                     Shared frame-bridge and third-party headers
    tests/                       Existing smoke-test projects (kept in place for now)

## Current implementation status

| API | Hook implementation | Renderer separation | Runtime evidence |
|---|---|---|---|
| D3D9 / D3D9Ex | Factory-import and device-vtable Present/PresentEx interception in Hook/D3D9/D3D9Hook.cpp | CPU frame-bridge snapshot, texture upload, state-block preservation and composition extracted to Renderer/D3D9/D3D9Renderer.cpp | Post-split D3D9 smoke test passed: present=4, draws=4 |
| D3D10 | Not implemented | Not implemented | None confirmed |
| D3D11 | DXGI Present interception lives in Hook/D3D11/D3D11Hook.cpp | Shader/resource/shared-texture composition extracted to Renderer/D3D11/D3D11Renderer.cpp; Hook and ReShade event bridge remain in Hook layer | Post-split DXGI/D3D11 smoke test passed; prior SMASH LEGENDS test showed D3D11/ReShade drawing |
| D3D12 | Not implemented | Not implemented | GTA V Enhanced DXGI interception alone did not prove a working D3D12 renderer |
| Vulkan | Not implemented | Not implemented | None confirmed |
| OpenGL | Not implemented | Not implemented | None confirmed |

A folder or README is a planned slot, not a working implementation.

## Design boundaries

- Hook: API-specific interception, lifecycle, device/swap-chain observation, thread and shutdown safety.
- Renderer: only API-specific resource creation, GPU synchronization, composition, and cleanup.
- Detection: observes runtime modules, factories, devices and presentation interfaces and keeps an explicit Unknown/Unsupported result. Games may initialize more than one API, so detection should be runtime-evidence-based.
- Common: API-neutral contracts, logging, IDs and transport. Do not assume GPU resources can be shared directly between D3D9/11/12/Vulkan/OpenGL.
- DXGI: a presentation interface used by several Direct3D paths, not a universal renderer. The current DXGI implementation is D3D11-oriented and must not be advertised as D3D10 or D3D12 support without separate validation.

## Safe migration rule

D3D9 and D3D11 Hook/Renderer responsibilities have been extracted into independent translation units. Keep smoke tests green while adding one API at a time. Do not add empty .cpp stubs to the DLL build; unimplemented API folders must not masquerade as support.