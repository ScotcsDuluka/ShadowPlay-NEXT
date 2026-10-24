# NVIDIA Ansel Adapter

This directory is the **Ansel Adapter boundary** in ShadowPlay NEXT. It is not the effect engine.

## Layout

- `Ansel Adapter/README.md` — adapter ownership and interface contract (this file)
- `Ansel Adapter/ReShade Ansel/` — **{Engine}: ReShade Ansel**
  - `NVIDIA Ansel Adapter.vcxproj` — native engine add-on build
  - `src/AnselAdapter.cpp` — registers with ReShade, receives D3D11 effect-runtime callbacks, serves its native named pipe
  - `src/FilterSession.h`, `src/GamePresetRoot.h`, `src/ReShadeUiMetadata.h` — engine/session contracts
  - `include/` — versioned ReShade API headers and JSON dependency
  - `tests/` — native engine tests

## Adapter -> Engine -> OSC

1. The D3D11 OSC hook loads `NVIDIA Ansel Adapter.dll` from the **same deployed runtime directory** as the hook. The hook source remains under `NVIDIA Adapter/OSC Adapter/Duluka Hook/`.
2. **ReShade Ansel (Engine)** registers its ReShade callbacks and exposes the live `NVIDIAAnsel.<pid>.<serial>` named pipe inside a running D3D11 game.
3. **Ansel Adapter boundary** consumes native runtime state via `Overlay On-Screen Control/NVIDIA NodeAPI/shims/ansel-client.js` and `game-filter-route.js`. The UI integration stays at `osc/mods/ansel-game-filter.js`.
4. The engine remains ReShade-specific. The adapter interface does not fabricate a connected state when a game is missing.

The filesystem refactor changes **source ownership, not the runtime ABI**: DLL name, exports (`StartNVIDIAAnselAdapter`), named-pipe protocol, deployed paths, Game Filter REST endpoints and preset format remain unchanged.

## Build and test

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Scripts/build-ansel.ps1
```

`Scripts/build-ansel.ps1` resolves `ReShade Ansel` as the engine source root, executes the native/Node integration tests, and optionally stages runtime files when invoked with `-Stage`. The all-project development build `Scripts/build-dev.ps1` uses the same engine source root. Do not deploy native DLLs while a game is running.

Native engine details and preset semantics: [ReShade Ansel/README.md](ReShade%20Ansel/README.md).