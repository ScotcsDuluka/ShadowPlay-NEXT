# DX9 live test: Hook DLL loaded, no Overlay (2026-10-24)

## Runtime proof
- Floating Point (Unity 4.3.4f1, x86) output_log.txt confirms Direct3D 9.0c.
- NvContainer auto-hook with --render-api d3d9 launched x64 Adapter then x86 Adapter, loaded native Hook DLL into the real game process.
- x86 process inspection confirmed NVIDIA OSC Adapter Hook.dll in CrossIntegrity\x86.
- Native logs for real-game PIDs 35756, 4008, 21556, 36124 all say:
  No imported D3D9 factory functions were found to hook.
  D3D9 hook initialization did not install.
- Present / PresentEx and visible OSC are NOT validated.

## Root cause
D3D9Hook.cpp InstallD3D9PresentHook() scans the existing module Import Address Tables for Direct3DCreate9[Ex] and installs factory hooks only when those IAT entries exist. Floating Point imports LoadLibrary/GetProcAddress and creates D3D9 device before NvContainer can attach. No D3D9 IAT entry means no Present hook. The Hook's D3D9 README and d3d9_late_attach_test.cpp explicitly document the known failing late-attach gap (0 callbacks).

Do NOT patch a temporary dummy D3D9 device vtable to access existing game devices: the fixture confirms separate per-instance heap vtables even when Present code addresses match. Cooperative NvOscAdapterRegisterD3D9Device requires the true game device pointer from its owner; it is not an automatic discovery method.

## Remediation acceptance
1. Native Duluka Hook owner: implement a reviewed automatic late-attach strategy for existing game D3D9/D3D9Ex devices, without arbitrary COM pointer scanning or unsafe inline patches.
2. Regression fixture: create game device before DLL load, then verify real Present/PresentEx count >0, Reset/Release safety, no per-Present disk IO, and non-blocking frame path. Keep x86/x64 and existing IAT tests.
3. Stage/deploy updated Hook DLL transactionally only when fixture passes and game is no longer holding DLL; confirm real-game native counters and visible OSC.
4. Separate OSC owner task: NVIDIA OSC.exe repeatedly APPCRASHed in libcef.dll (0x80000003) at 11:15-11:18; diagnose independently before claiming stable overlay; also verify OSC D3D9 CPU frame producer and visibility after native Present is intercepted.

## Changes performed
Investigation and this report only. No native source changes, game-file modifications, DLL deployments or process terminations.
## 2026-10-24 implementation progress
MinHook 1.3.4 BSD-2-Clause pinned at commit c3fcafdc10146beb5919319d0683e44e3c30d537.
Added no-import late D3D9 implementation-code detour fallback.
Native Win32 and x64 Release DLL builds succeeded, with upstream MinHook HDE
C4701 compiler warnings. Isolated automatic pre-existing-device fixtures:
D3D9 and D3D9Ex on Win32/x64 all PASS (4 of 4 Presents).
Cooperative fixtures on Win32/x64 still PASS. Live DLL not yet deployed;
game OSC image and D3D9 CPU frame publisher remain unconfirmed.