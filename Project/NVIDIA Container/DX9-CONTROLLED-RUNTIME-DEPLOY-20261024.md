# NVIDIA Container — DX9 Controlled Runtime Deploy Verification
Date: 2026-10-24 | Host: AORUS-DESKTOP
Project: C:\My Project\NVIDIA-Shadowplay
Status: DEPLOYED, RESTARTED, LIVE CONTAINER -> OSC ADAPTER HOST CLI OBSERVED.

## Scope and changes
- Source: Components/Runtime/OscAdapterLifecycle.cs
  - Explicit allowlist of test game IDs + fully qualified executable paths.
  - Re-check PID, actual process executable, process start time, and GameWatcher ProcessSignature immediately before starting Adapter Host.
  - Only d3d9-only Ontology entries qualify; dx9ex catalog variant is never treated as confirmed.
  - New status dx9TestTargetsConfigured and dx9AutoHookEligible includes non-empty allowlist and installed x64/x86 Host contract.
  - Existing development allowUnverifiedIdentity=true allows name-based identity, but does NOT bypass exact test allowlist or PID/path verification.
- Source: NvContainer.cs parses oscAdapter.dx9TestTargets; invalid/missing entries fail closed.
- Tests: Dx9AutoHookRoutingTests.cs checks wrong ID, wrong path, empty allowlist, valid/stale PID/start/path.
- Source template Project/NVIDIA Config/nvcontainer.json remains DX9 OFF by default with an empty allowlist.
- Active Build config oscAdapter.enableDx9AutoHook=true and exactly two test targets:
  1. suspicious-developments.floating-point -> D:\SteamLibrary\steamapps\common\Floating Point\Floating Point.exe
  2. anatoliy-loginovskikh.100-hidden-frogs -> D:\SteamLibrary\steamapps\common\100 hidden frogs\100_hidden_frogs.exe
- No other game is eligible for DX9 via this allowlist; no game EXE or Duluka Hook Engine source was modified.

## Actual deploy (not stage-only)
- Launcher owned root process gracefully closed.
- Old NvContainer PID 17264 stopped during maintenance only after Capture /state recording=false, active OSC Adapter=0 and no controlled game running.
- Installed verified four-file Container bundle using Scripts/Install-NvContainerBuild.ps1; all SHA-256 installed hashes match staged manifest.
- Started Launcher / NvContainer, new NvContainer PID 33360.
- Installer provisioned previously missing capture-engines.json and preserved other installed configuration.
- Backups:
  - C:\Users\ScotcsDuluka\AppData\Local\ShadowPlay NEXT\DeploymentBackups\DX9Controlled-20261024-105859 (original runtime config, user-restricted ACL)
  - C:\Users\ScotcsDuluka\AppData\Local\ShadowPlay NEXT\DeploymentBackups\ContainerRelease-20261024-110146 (pre-install runtime bundle/config, restricted)
  - build\NVIDIA ShadowPlay\NVIDIA Container\PreviousVersions\NvContainer-20261024-110151
- Scripts/Deploy-NvContainerControlledDx9.ps1 provides preflight, backup, install and rollback.

## Live API verification, AFTER install
GET /v1/game-watcher/status -> enabled=true, running=true, catalogVersion=1.2.8, catalogReady=true.
GET /v1/osc-adapter/status -> enabled=true, dx9AutoHookEnabled=true, dx9HostContractReady=true, dx9AutoHookEligible=true, dx9TestTargetsConfigured=2, dx9RequiresVerifiedIdentity=false (explicit development override).
NodeAPI health, NvCapture health and Graphics-through-Container health -> HTTP 200 after deploy.

## Host command-line proof (actual controlled game)
Ran Floating Point test binary briefly, permitted exact allowlisted path:
- Test game PID 4008
- Actual OSC Adapter Host PID 27256
- Inspected real Windows Win32_Process.CommandLine of Host PID:
  CLI_D3D9=True (--render-api d3d9)
  CLI_PID=True (--game-pid 4008)
  CLI_EXE_PATH=True (same game executable path compared OrdinalIgnoreCase)
  VERIFIED_CONTAINER_TO_REAL_HOST=True
- A previous probe mistakenly used case-sensitive string Contains and reported false for path, because Windows returned a canonical Steamapps\Common capitalization. Re-ran with Windows-appropriate OrdinalIgnoreCase comparison, yielding all true.
- Game only ran for this controlled test and was closed afterwards.
- After cleanup: adapter activeCount=0, watcher no matched test games, NvCapture recording=false.

## Offline regression
- 30/30 DX9 Container routing/identity tests passed.
- Isolated Release x64 NvContainer build succeeded with 0 errors and 0 warnings.
- Preexisting Host fixture test x64 to x86 d3d9/auto succeeded, D3D9 Present=4 callback count. This is independent of live game rendering.

## Limits
- Host CLI was actually delivered and active; this is NOT a claim of verified native PresentEx, successful OSC pixel compositing, or game-filter behavior.
- DX9Ex still requires Native Hook device/PresentEx telemetry, not catalog metadata.
- SHA-256 gate remains disabled only via development override in current runtime; disable allowUnverifiedIdentity to restore strict identity checking (requires restart).
- To disable test Auto-Hook, set enableDx9AutoHook=false in runtime config and perform a controlled Container restart.
- Hooking outside the two exact allowlisted game ID/path pairs is blocked by Container.
- Existing Capture write endpoints remain default-denied and no recording, screenshot or monitor change was initiated during this run.
- No Git commit or push.
