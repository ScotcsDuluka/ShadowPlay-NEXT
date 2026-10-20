# Native Hook tests

Existing smoke-test sources and Visual C++ projects are intentionally kept at their current paths during this structural pass so that local invocation paths are not silently broken.

- d3d9_smoke_test.cpp / NVIDIA OSC Adapter D3D9 Smoke Test.vcxproj: D3D9 and D3D9Ex composition path.
- smoke_test.cpp / NVIDIA OSC Adapter Smoke Test.vcxproj: DXGI/D3D11 Present, bridge and composition checks.

After the project builds from the moved source paths, rerun both smoke tests. Passing a standalone smoke test is not the same as proving that a real game uses the matching API path.
DX9 x86/x64 validation: run `Scripts/verify-dx9.ps1` from the repository root.
The DX9 test isolates both pixel and consumer-identity mappings from live OSC.
It draws only through the installed Present hooks, checks premultiplied alpha
and orientation, resets both device types, and requires exactly four draws for
four Presents. `--external-attach` waits for the attachment host to load the DLL
instead of loading it locally; used to test x64 routing into the x86 target.

Real ReShade validation: build both native architectures and run
`Scripts/verify-dx9-reshade.ps1 -ReShade32 <vendor-ReShade32.dll> -ReShade64 <vendor-ReShade64.dll>`.
The script creates isolated test folders, never edits a game installation, and
uses a blue-output shader from `tests/reshade/Isolation.fx`. Both D3D9 and 9Ex
must preserve the opaque green/red OSC markers, blend translucent red over the
shader's blue exactly once, and survive Reset/ResetEx. Each architecture renders
1680 frames. ReShade logs remain under `Build/Dx9Validation/ReShade/<arch>/`.
The larger 640x480 back buffer avoids ReShade's dummy-device exclusion.

A foreground test-window failure is an environmental failure, not a pass. Close
any foreground-locking overlay before running these tests.
