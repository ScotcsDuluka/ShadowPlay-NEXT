# OSC Adapter — Host / Controller

> **Current scope (24 October 2026):** Managed Host/Controller source is separate from the native Duluka Hook DLL. This document contains a historical *source-only split* note; later controlled deployment/build steps exist and must be checked against the installed runtime instead of treating that older note as a global "never deployed" claim. DX9/9Ex automatic routing and the GPU mailbox have isolated tests but **do not certify real-game smoothness, safe detach or general API compatibility**. See [DX9 coordination](AUTOHOOK-DX9-COORDINATION-20261024.md) and [Hook test history](Duluka%20Hook/tests/README.md).



Source root: `Project/NVIDIA Adapter/OSC Adapter`.

OSC Adapter is the **managed coordinator / controller**. **Duluka Hook** is the separate native **Hook Engine**. They share a deployment contract, but do not own each other's source code.

## Source layout

```text
OSC Adapter/
├── Program.cs                          Managed adapter host / process coordinator
├── NVIDIA OSC Adapter Host.csproj      .NET 10 x64 and x86 host
├── src/Detection/GraphicsApiProbe.cs   Passive runtime evidence
├── Archive/                           Preserved old managed host source
└── Duluka Hook/                       Native Hook Engine
    ├── NVIDIA OSC Adapter.vcxproj      C++ DX9/DX11 native DLL project
    ├── src/Hook/                      DXGI and D3D9 interception
    ├── src/Renderer/                  Graphics API composition
    ├── include/                       Frame ABI, telemetry, ReShade contracts
    └── tests/                         Native/GPU IPC regression
```

## Stable binary interface

The .NET host remains `NVIDIA OSC Adapter.exe` and the engine remains `NVIDIA OSC Adapter Hook.dll` (both x64 and x86). The DLL name, exported functions, game-facing IPC layouts, and DXGI Present owner are **unchanged**. The managed host resolves its engine DLL from its **deployed executable folder**, not the source directory. The x64 host routes x86 games to the sibling `x86/NVIDIA OSC Adapter.exe` and matching x86 DLL.

The runtime target remains `Build/NVIDIA ShadowPlay/NVIDIA Adapter`, including its `x86` and CrossIntegrity copies. This change is a **source-only separation**; it does not deploy or restart programs, change game GPU selection, or modify `nvcontainer.json`.

## Ownership

**Adapter host:** process identity validation, architecture routing, run/elevation lifecycle, read-only API observation, execution of authorized attachment request, and host logs.

**Duluka Hook Engine:** interception lifecycle for D3D9/D3D11, ReShade callbacks, renderer composition, GPU texture resource lifetime, mailbox/ABI synchronization, and native regression tests.

## Build

- Managed host: `dotnet build "NVIDIA OSC Adapter Host.csproj" -c Release -p:Platform=x64` (also `x86`).
- Native engine: MSBuild `Duluka Hook/NVIDIA OSC Adapter.vcxproj` Release x64/Win32.
- Regression: `Duluka Hook/tests/verify-adapter.ps1`.
- Bundling: `Scripts/build-dev.ps1` takes managed outputs from `OSC Adapter/bin`, and native binaries from `OSC Adapter/Duluka Hook/bin`.

Don't rename deployed artifacts or mix architectures without updating the runtime contract.

## Optional DX9 / DX9Ex / DX11 Host routing (2026-10-24)

The managed Host accepts an optional `--render-api auto|d3d9|d3d9ex|d3d11`.
Omission preserves the existing D3D11/Container CLI behavior. `auto` needs
unambiguous loaded runtime API evidence. Explicit API requests reject conflicting
runtime evidence but permit early attachment before a renderer DLL appears,
provided module inspection succeeded and the upstream caller authenticated
the game. DX9Ex device status is verified by native Hook, never guessed from
the presence of d3d9.dll.

Host validates PID and executable path before launch AND revalidates the
path using the exact process HANDLE used for native DLL load, to guard against
PID reuse. This does not bypass game protections or certify safe DLL unload.

Run Host-only policy tests: `dotnet run --project tests/RenderApiPolicy.Tests.csproj -c Release`.
Run isolated x64→x86 native test: `& "./tests/verify-dx9-routing.ps1"`.
For test results and required NVIDIA Container / Duluka Hook handoffs, see
`AUTOHOOK-DX9-COORDINATION-20261024.md`. This is Source-only; NO live deploy.