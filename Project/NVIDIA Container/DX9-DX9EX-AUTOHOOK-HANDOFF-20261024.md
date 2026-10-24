# NVIDIA Container — DX9 / DX9Ex Auto-Hook Coordination
2026-10-24 | C:\My Project\NVIDIA-Shadowplay

## Status
Container source supports D3D9 family routing, but actual game Auto-Hook has NOT been verified or enabled. No real game was started or injected. No source changes to OSC Adapter Host or native Duluka Hook Engine. No active runtime restart or replacement.

## Verified live findings
- ApplicationOntology v1.2.8 / 8 games, GameWatcher enabled, Adapter enabled, activeCount=0.
- Floating Point.exe and 100_hidden_frogs.exe exist, PE machine 0x014c = x86.
- Both games have ZERO SHA-256 pins. Name-only matching is permitted only for explicitly opted-in local development when allowUnverifiedIdentity=true; not recommended for release.
- Live OSC Adapter configured executable: NVIDIA Adapter\CrossIntegrity\NVIDIA OSC Adapter.exe.
- Deployed managed Host DLL has no --render-api CLI contract; current OSC Adapter Host source DOES support optional --render-api auto|d3d9|d3d9ex|d3d11.
- x64 to x86 managed Host and native Hook binaries exist in the Runtime tree, but current deployed Host does not support the new contract.
- Current runtime config retains allowUnverifiedIdentity=true. This explicit development override now permits name-only game identity for DX9 as well as D3D11; strict SHA-256 is restored by disabling the override.
- Live config has no enableDx9AutoHook flag, so new code defaults it to false.

## Read-only game fingerprints for ApplicationOntology team to REVIEW
Floating Point:
- Steam 302380
- Path: D:\SteamLibrary\steamapps\common\Floating Point\Floating Point.exe
- Declared renderer: d3d9
- PE machine: 0x014C
- SHA256: 851740503CBC607B635357D45F51462430B0D8B3C4EEF2B80ACEE6CBDE1EA240

100 hidden frogs:
- Steam 1806840
- Path: D:\SteamLibrary\steamapps\common\100 hidden frogs\100_hidden_frogs.exe
- Declared renderer: d3d9; declared *variant* d3d9ex
- PE machine: 0x014C
- SHA256: D6192DD63743A14387A3FEC91593237EC34AAD319F48DC8D3005646EA4F280B4

These SHA-256 values are on-disk identity evidence only. They are not renderer proof, native Hook attestation, or permission to modify protected games. Game updates may change hashes. Ontology owner must validate and add pins only after provenance review.

## Container files
- Components/Runtime/OscAdapterLifecycle.cs — new fail-closed DX9 family selector and Host compatibility gate.
- NvContainer.cs — config enableDx9AutoHook default false.
- Tests/Dx9AutoHookRouting.Tests.csproj and Dx9AutoHookRoutingTests.cs — synthetic PE tests, no injection.
- Tests/OntologyIdentityTests.cs — validate 8-game v1.2.8 Catalog and unpinned DX9 identity.
- Project/NVIDIA Config/nvcontainer.json — source template flag false, no change to active Build config.

## API / Host contract
- Catalog renderApis=["d3d11"]: existing D3D11 route; no CLI changes.
- Catalog renderApis=["d3d9"]: DX9 family candidate only, never d3d9ex auto-select.
- Ambiguous renderApis, unknown, DX12, Vulkan, OpenGL: rejected.
- DX9 conditions: oscAdapter.enableDx9AutoHook=true explicitly; Ontology matched game via SHA-256 by default or executable name under explicit development override; matching deployed x64/x86 Host+Hook DLLs and managed Host DLLs with --render-api contract.
- Container sends standard --game-id, --game-name, --game-pid, --game-executable, plus --render-api d3d9.
- Managed Host inspects *loaded process modules* and refuses conflicting D3D11 evidence. Native Hook must confirm actual D3D9 / D3D9Ex device creation and Present. Module presence alone does not prove active graphics API. An early-attach hint must never count as a verified live renderer.
- Host contract gate checks binary compatibility, not digital signatures or anti-cheat policy.
- Updated Container status: dx9AutoHookEnabled, dx9HostContractReady, dx9AutoHookEligible, dx9RequiresVerifiedIdentity, dx9ExVerified=false. DX9Ex needs a real native telemetry path before it can ever report true.

## OSC Adapter team actions
1. Review and package matching managed x64 and x86 Host EXE/DLL/deps + native Hook DLLs in CrossIntegrity and CrossIntegrity\x86 (current deployed Host is old).
2. Verify --render-api d3d9 x64→x86 Host forwarding, PID/executable identity and module probe.
3. Perform isolated fixture/native tests and authorized, anti-cheat-free real-game tests.
4. Provide native evidence for D3D9Ex PresentEx and safe compositor/producer-to-consumer frames; no inference from Catalog.
5. Review safe detach/restarts and no double injection. Do not modify game binaries.
6. Only then authorize coordinated deployment with Container and Ontology teams.

## Ontology team actions
- Verify game versions/provenance and review hashes above. Add validated pins and refresh catalog; do not silently loosen identity checks.
- Keep 100 hidden frogs d3d9ex only as a variant hint pending native verification.

## Verification
- NvContainer isolated Release x64 Build: 0 errors, 0 warnings.
- Container DX9/Ex Routing synthetic regression: 23/23 passed after the DX9 development override change.
- Ontology Identity v1.2.8: 24/24 passed.
- Capture Catalog: 21/21 passed.
- Pairing Security: 14/14 passed.
- Adapter Host Render API Policy: 16/16 passed.
- Isolated x64 Host → x86 native Hook regression: d3d9 and auto both PASS, hostExit=0, fixtureExit=0, Present=4, draws=0. Counter-only fixture, not proof of OSC pixel compositing or live-game auto hook.
- Staged program bundle manifest: all four SHA-256 checks PASS; stage reason=StageOnly.
- No live DX9/Ex Auto-Hook verification was performed.

## Deployment / Restart / Rollback
- Preflight: activeCount=0, but deployed managed Host contract is old and Catalog pins are missing.
- Verified Container bundle is STAGED ONLY at:
  C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\NVIDIA Container\PendingUpdates\NextInstall
- Install-NvContainerBuild.ps1 -StageOnly returned expected DEFERRED exit 2 and wrote hashed manifest. Live NvContainer and other services were NOT restarted/deployed because all prerequisites are not met. User's preference is deploy/restart every round when safe; do not do an unsafe partial release.
- To activate later: validate pins, deploy matching Host and Hook packages, back up production binary bundle/DPAPI/config with restricted access, gracefully stop Launcher/Container, apply staged Container binaries and explicitly opt in DX9, restart and smoke test D3D11/Graphics/Capture/OSC, then allowed DX9 title.
- Rollback: disable enableDx9AutoHook, restore previous verified runtime/Host bundle, restart gracefully with no active injected game, check all service/API health.
- No current evidence certifies live DX9 auto-hook, DX9Ex native device, OSC pixels, anti-cheat compatibility or safe detach.
