# OSC Adapter Host — DX9/DX9Ex/DX11 coordination (2026-10-24)

## Ownership and deployment status
OSC Adapter Host only. This file does not authorize editing native Duluka Hook
or NVIDIA Container owned files. Changes remain in Source and local Host bin.
NOT deployed to live `Build/NVIDIA ShadowPlay/NVIDIA Adapter` / CrossIntegrity.
No real game was injected. Running NVIDIA / Windows processes untouched.

## Host CLI contract (optional extension)
`--render-api auto|d3d9|d3d9ex|d3d11`
Existing arguments and DLL filename are unchanged. Omission retains legacy
Container/D3D11 launch semantics. The argument is forwarded unchanged from x64
to sibling x86 Host. No native exported symbols, shared-memory layouts, ABI or
renderer resources have been changed.

Routing policy:
- `auto` requires one unique supported runtime graphics module: d3d9.dll OR
  d3d11.dll. No evidence, conflicting modules or failed enumeration => refuse.
- Explicit `d3d9` / `d3d9ex` requires no conflicting D3D11 evidence. If
  modules were successfully inspected and neither module was loaded, a trusted
  caller can request EARLY attachment to avoid missing D3D9 device creation.
- `d3d9ex` is NOT independently detectable from d3d9.dll; native device
  creation / PresentEx is authoritative. Log family as D3D9, not proven Ex.
- Explicit `d3d11` similarly rejects conflicting D3D9 evidence.
- A failed module scan denies all explicit modes. Probe-only remains read-only
  and retains existing API_PROBE log ordering.
- Before native DLL load the game executable path is verified twice, including
  against the same process HANDLE actually used for attachment (PID reuse guard).
- This policy checks module presence, NOT active presented API, anti-cheat
  compatibility, safe detach, or rendered OSC pixels. Do not claim those tests.

## Verified on AORUS-DESKTOP this session
- Managed Host net10 Windows Release x64 + x86: PASS, zero warnings/errors.
- Policy suite: 16/16 PASS, legacy/default, explicit, auto, mismatches and fail-closed.
- Existing x64/x86 passive Host probes / wrong-image identity: PASS.
- Native D3D11 existing isolated prebuilt smoke: PASS, 9 Present, 6 composed.
- New OSC Adapter-owned x64 Host -> x86 native fixture: `--render-api d3d9`
  and `--render-api auto` each PASS, fixture and Host exit=0, 4 hooked
  Present callbacks. This validates attachment/counts, not combined live OSC
  pixel rendering in an actual game.
- Both test game executables exist under
  `D:\SteamLibrary\steamapps\common`. Neither was launched for these tests.
- Live Container reports Catalog v1.2.8, eight games, matchedGames=[], and
  Adapter enabled, CrossIntegrity executable, activeCount=0. Current runtime
  explicitly sets allowUnverifiedIdentity=true (development exception).
- Native engine BUILD was NOT invoked and its Source was NOT edited.

## NVIDIA Container owner — required handoff (NOT applied)
1. In `Components/Runtime/OscAdapterLifecycle.cs`, keep the existing SHA-256
   identity gate. Do NOT enable unknown/name-only production injection.
2. Replace D3D11-only `SupportsCurrentAdapter` policy with an explicit
   eligible-family policy AFTER review. Candidate D3D9 / D3D9Ex MUST be
   constrained by identity, signed/allowed build provenance, known compatible
   graphics API and game security/anti-cheat policy.
3. Pass `--render-api` to the Host. For catalogs listing d3d9 AND d3d9ex,
   use family `d3d9` as a tentative early-attach hint only after verification;
   never assume Ex simply from a catalog variant. The native Hook confirms
   D3D9Ex at device creation/presentation.
4. Add bounded retry/backoff and PID+creation-signature checking for module
   evidence not yet ready; avoid duplicate injection or blind restarts.
5. Preserve all existing D3D11, GameWatcher, process ownership, and allowlist
   regressions. Revisit runtime `allowUnverifiedIdentity=true` before release.
6. Do not make auto-attach the default until actual game opt-in, anti-cheat
   compatibility, safe unload/detach and end-to-end foreground OSC tests pass.

## Duluka Hook owner — required handoff (NOT applied)
- Revalidate native D3D9/9Ex device-create interception in real test games.
  Late injection after device creation is NOT automatically supported.
- D3D9Ex mailbox / optional GPU bridge is distinct from DX11 shared NT
  handle protocol. Prove live GPU producer→consumer, foreground pixels, safe
  resource retirement, CPU fallback, cross-GPU and reset behavior before
  enabling a default. No API/ABI changes requested by OSC Adapter.
- Safe thread-quiesced detachment/unload remains a release blocker.
- Never attempt protected game-process bypass.

## Repro
```powershell
dotnet run --project "Project/NVIDIA Adapter/OSC Adapter/tests/RenderApiPolicy.Tests.csproj" -c Release
dotnet build "Project/NVIDIA Adapter/OSC Adapter/NVIDIA OSC Adapter Host.csproj" -c Release -p:Platform=x64
dotnet build "Project/NVIDIA Adapter/OSC Adapter/NVIDIA OSC Adapter Host.csproj" -c Release -p:Platform=x86
& "Project/NVIDIA Adapter/OSC Adapter/Duluka Hook/tests/verify-host-probe.ps1"
& "Project/NVIDIA Adapter/OSC Adapter/tests/verify-dx9-routing.ps1"
```
D3D11 smoke was separately invoked using the prebuilt native test fixture
and prebuilt native Hook DLL. No engine rebuild or live deploy is required.
