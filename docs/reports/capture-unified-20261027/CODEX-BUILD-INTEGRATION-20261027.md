# Build integration and UI stabilization — 2026-10-27

Scope: Stable checkout on AORUS; existing uncommitted changes preserved. This report distinguishes isolated testing from the live runtime. No desktop, game, microphone or loopback capture was initiated for this work.

## Current runtime and root cause

At preflight, the processes listening on 59077, 59012 and 59011 were the product-root NvCapture, NvContainer and NvNode respectively. NvCapture `/health`, `/state` and `/engines` returned 200, `ready=true`, `recording=false`, configured Duluka/DXGI/NVENC, and an empty actual session backend. The old worker did not report `truthSchemaVersion`; `enabled=false` describes the recording session, not worker enablement. The three requested Container routes and the NodeAPI status facade returned 404.

The production `NvContainer.dll` contains `/v1/status` but not the three `/v1/capture/*` route strings; the current source registers them. Production NodeAPI `index.js` does not mount the Capture shim, and the shim and related Capture dependencies are absent. The current source contains registration. Thus the immediate 404 cause is runtime artifact drift at both layers, not proof that the worker is disabled. Historical 200 responses do not establish present readiness.

| Artifact | SHA-256 |
|---|---|
| Live NvCapture.dll | `DE04FDBB1936B6A45FA1770574D0F574AD9F1C3AA03C12826FB6860EF2E1CE76` |
| Live NvContainer.dll | `10C13880939A4F35BB60B7DF06472F9045D3B2A6549DA539D00B5B66C2CFD8A8` |
| Live NodeAPI index.js | `CC5E4B75C6CC92781544E5D1CC62345B67ACB9AF5AB033C68D6B14C5BB3ADACA` |
| Isolated new NvCapture.dll | `7FBCDCA93D03EC0F3E631ED48CAD5DDA1A46731CBC44930694F3E576F8CE3801` |
| Isolated new NvContainer.dll | `351C0CC477336BBAA00A142725A95C1312B3197F8608246CC488B741180BFF26` |

## Build, stage and deployment ownership

`Scripts/build-dev.ps1 -Module Capture` delegates to `Build-CaptureUnified.ps1`, which builds the real ProjectReference graph into a unique artifacts directory, checks dependency closure, stages FFmpeg and the validated native QSV package, and records hashes and source digest. Its manifest deliberately records hardware/media/deployment as unverified. Full `build-dev.ps1` defaults to the product tree and includes cleanup: it was not used for this task. The Capture stage is `build_test/NVIDIA Capture Unified_Test/codex-integration-20261027-a`; its `-NoBuild` source and file hash verification passed. Container artifacts are under `build_test/NVIDIA Capture Integration_Test-20261027/container-artifacts`. Its project reference also wrote `build/NvGraphics-ContainerRef`, outside the production runtime tree.

Existing `deploy-capture-unified.ps1` originally copied only the Node status shim; it could not activate a route absent from runtime index. A paired Node bundle was prepared from the exact current production index, with guarded Capture registration and six dependencies, under `build_test/NVIDIA Capture Node Bundle_Test/20261027-master-01`. The six dependency paths were absent in the runtime. Its manifest binds the base index and bundle hashes. Registration-block tests with production NvNode v11.13.0 and a mock upstream passed, including cross-origin rejection. Full isolated Node startup was not attempted: the original index uses hard-coded live ports, native modules and product config/appdata. This test does not certify the whole process or UI.

Production config contains one `captureRuntime` block with `enabled=true` and port 59077, but no explicit write flags. Current source defaults the three gates to false; the stricter deployment gate rejects absent explicit flags. A separate candidate adds only `allowRecordCommands=false`, `allowDisplayCommands=false`, and `allowScreenshotCommands=false`; semantic comparison passed. The current runtime config was not changed. Candidate and all config contents remain local.

The deployment script now accepts the staged Node bundle and, optionally, the candidate config only with its exact baseline hash and unchanged remaining values. The final dry run generated `deployment-preflight-codex-02/deployment-plan.json`: 62 planned files, 31 different, including the config; no production files or processes were changed. Backup and restoration are verified with isolated fixtures (including rejection of a corrupt backup); a real production backup has not yet been made. Changes to the runtime or stage after planning require fresh checks. The new helper/build/test files in `Scripts/` and changes to the existing untracked deployment scripts are part of this work; their pre-existing uncommitted content was retained.

## Validation

| Check | Result and boundary |
|---|---|
| Unified Capture Release and QSV package ABI | PASS, 2 existing obsolete API warnings, 0 errors; native ABI 3 probed without hardware initialization; no Intel hardware or recording claim |
| Container Release | PASS, 0 warnings/errors; compiled route harness passed status/engines/displays/contract/capabilities with fake callbacks on a temporary port |
| Node Capture tests | 23 passed against test dependencies; isolated bundle status and origin guard passed using shipped NvNode |
| Container gateway / display / catalog | 59 / 26 / 24 passed |
| Control request / pairing security | 13 / 14 passed; an initial pairing no-restore attempt failed due to shared test obj assets and a restored retry passed |
| Capture RouteGuard | 67 passed; optional FFmpeg fixture checks not exercised |
| Session contract and synthetic media | 35 / 43 passed; generated test media was decoded; no real source or audio devices |
| Recovery | 15 passed with fault-injected engine, no hardware or worker restart |
| Native QSV packaging fault matrix | 12 passed; ABI packaging only |
| Deploy readiness and config unit checks | 18 / 9 passed using fixtures |
| Candidate config, backup and rollback | 8 passed with isolated fixtures; live rollback and restart untested |
| OSC targeted source regressions | 165 passed after fixes; these are not CEF visual acceptance |
| Module profile diagnostic | 8/10 after test isolation fix: two failures remain for source/mirror hashes and index snapshot drift; these results do not certify current module loading |

The prior retained `Saved=true` isolated recording is a baseline, not evidence that the newly staged or production build records correctly. No new live recording was attempted.

## UI observations and source changes

CEF was observed closed at `#/base`; the process and loaded script tags do not prove Main Menu, Settings or Capture rendering. No actual before/after image or user interaction acceptance is available. The page had Gallery script loaded through two paths; a source change makes its observer replace the previous owned observer on reinjection. Existing observers in the already loaded old page cannot be detached by this change until a controlled reload. Motion source changes restore settings/disabled behavior, duration limits, bounded preparation/transition recovery, visual cleanup and base-route reopen while retaining the newer V5 owner wait and latest intent handling. Related Motion and Gallery regressions were added. NVIDIA layout and native controls were retained; no visual theme was redesigned.

UI files changed in this work: `osc/mods/osc-motion.js`, `osc-motion-engine.js`, `osc-motion.css`, `gallery-media-source.js`, and `tests/oscMotion.test.js`, `galleryMediaSource.test.js`. Source changes are not deployed.

The Module profile test unexpectedly wrote `Module/Core/module-map.js` on import and regenerated four untracked diagnostic `index.*.html` files. It ran once during this audit, changing those source-side diagnostic artifacts; production was untouched. No attempt was made to overwrite unknown prior contents by blindly restoring them. `Module/Core/build-profiles.cjs` and `test-profiles.cjs` now construct test profiles in memory; an isolated fixture verified the five current files' hashes stayed unchanged, and the explicit CLI still generates files. The optional Module snapshot is stale: four mirrored assets drift and its index baseline already differed. The live page uses `index.html` plus `/mods/list` and the native hotloader; the diagnostic profile pages returned 404. Consequently the 8/10 diagnostic result is neither a current production module-loading acceptance nor a reason to overwrite the snapshot.

## Deployment and remaining gates

No production deployment or restart has occurred in this task. The user explicitly requires coordination before a restart that can affect Overlay/Launcher. OSC and its CEF processes were running during preflight. The existing deployment routine stops the mother, worker and Node host and restarts the mother, so this gate applies even if OSC itself is preserved. No production `READY`, post-restart route success, visual before/after, module loading, actual encoder, media or user acceptance is claimed. Hashes and idle status must be refreshed immediately before any eventual deployment; recording and display/screenshot gates must remain default-deny. Backup/rollback must include any config activation, and a verified backup is required before replacement.

## Pre-deployment preparation — restart on hold

PowerShell 7.6.5 is available. Fresh preflight `deployment-preflight-codex-04` ran without `-Deploy`: the 62-file plan and 31 changes match the prior plan. Config candidate validation passed with all three command flags explicitly false and all other parsed values unchanged. None of the UI Motion/Gallery files are in the plan.

Actual predeployment backup is in `predeployment-backup-codex-01`. It contains 49 existing files from the deployment plan and five additional configuration files, and records the 13 plan paths absent from production. All 54 backup files were rehashed against runtime; the resulting manifest has SHA-256 `775E6A4A27A420F95BD58FD1F3F2C6CC668A9D2099EBA517E868AB5F99A92D071`. The full 67-entry runtime/backup inventory was independently rechecked. The deploy script will create and verify its own additional backup when eventually authorized; this predeployment snapshot is separate recovery evidence.

Rollback safety was strengthened: target processes must be confirmed stopped before replacement or restore, and a failed stop or file restore does not start a potentially mixed installation. The simulated rollback checks passed 9/9, candidate/backup corruption cases 8/8, readiness checks 18/18. File restoration and actual service recovery are separate outcomes; production rollback and restart have not been exercised. A partial stop failure can leave services stopped pending operator assessment, rather than risking an automatic restart over a mixed state.

Ownership check found the original product Capture, Container, Node and OSC processes and no matching active build/deploy command at that instant. Other task state can be unavailable, so this is not proof of exclusive ownership. Recheck all processes, activity, idle state, plan, and hashes immediately before any future deployment. The user has held production restart pending a separately confirmed Overlay downtime window.

An independent Container rebuild from an isolated source copy succeeded with zero warnings/errors. Six of nine staged files were byte-identical; the three managed DLLs differed in hashes, MVID and PDB build paths. Comparison of IL bytes, method signatures, stack/local signatures and exception regions found parity for 1,687 Container, 90 Graphics.Contracts and 1,019 Graphics methods. Raw metadata differs by 16 bytes in each of the first two DLLs and 20 bytes in Graphics, including four bytes whose meaning was not established. The comparison did not resolve metadata-token semantics, resources or native payload. It does not establish byte-identical metadata, constants or resources, or prove every difference is solely a path. The original staged set and 62-file plan were not replaced; this reproducibility limitation remains open.

## Refresh — 2026-10-28, production restart still on hold

PowerShell 7.6.5 is available. Current staged and production SHA-256 values and absent paths match the prior 62-file, 31-change plan; the candidate still adds only the three explicit false command gates. Capture source digest and QSV package ABI verification passed without hardware initialization. Container, Graphics and build-config source files checked against the prior isolated-build snapshot matched 388/388; the three-DLL reproducibility limitation above is not resolved.

Fresh production backup: `predeployment-backup-20261028-01/backup-manifest.json`. All 54 existing files, including the config, were copied and independently rehashed; 13 absent plan paths were recorded. Manifest SHA-256: `2C02393F93CA32588E381B0793AFF75077C92E57A633F60E6ED0663CD43223FC`. This backup is evidence for recovery and does not prove a production rollback was executed.

Fresh ownership observation: no product listeners on 59077, 59012 or 59011 and no product runtime process with a readable matching executable path. Two processes had unreadable paths; unrelated NVIDIA system container is not the product. No matching build/deploy process was observed; some chats are `notLoaded`, so exclusive ownership remains unconfirmed. Worker `/health` and `/state` are unavailable; recording/idle state is unknown. No process was started or stopped.

Actual `deploy-capture-unified.ps1` preflight was run without `-Deploy`, with evidence directories `deployment-preflight-20261028-01` and `deployment-preflight-20261028-02` (the latter after the final script update). Both exited 1 at `AssertIdle` because port 59077 refused the connection; the preflight did **not** pass and did not create a new deployment plan. Earlier successful preflight must not be treated as current readiness. No service was started to bypass this check.

Post-deployment guard was corrected to require Container engines and displays responses with checked schema and truth consistency, in addition to worker, Container status and Node status. Isolated checks passed 38 readiness, 9 rollback and 8 candidate/backup cases. Live route verification, production deploy and production rollback remain untested. Runtime restart is still on hold until the user confirms an Overlay downtime window; the unavailable worker must be resolved without treating its absence as idle.
