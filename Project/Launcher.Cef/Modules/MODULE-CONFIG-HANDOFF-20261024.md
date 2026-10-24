# ShadowPlay NEXT — Core Modules / Config Module (2026-10-24)

Project: `C:\My Project\NVIDIA-Shadowplay` on `Stable`
Owner: `Project\Launcher.Cef\ui`

## Magisk-style cards — latest update
- Launcher now has **three primary destinations**: ShadowPlay, Core Modules, and Add-ons. There is **no separate Config navigation item**.
- Each of the 11 Core modules has a Magisk-inspired card: icon, name, built-in metadata (no invented version), real runtime status from Launcher inventory, description, and bottom Settings action.
- Selecting Settings on a card opens the detail page already targeted at that module; its title includes the module name. Back returns to Core Modules.
- The module layout List/Grid picker sits above the cards, not on an unrelated Config page.
- The per-module appearance profile manager remains on the detail page and still affects the selected card, **not the Launcher background**.
- UI text uses EN/TH i18n keys. The cards never claim to switch off modules that lack a verified host API.
- Deployment verified via `build_test/Launcher ModuleLayout_Test/LIVE-VERIFIED.json` against the live Launcher localhost server; 11 cards, three navigation tabs, zero Add-ons; source and Build SHA-256 aligned. The latest source CSS additionally corrects description inset priority over legacy generic `.quick-module p`.
- A screenshot of the updated live Core page before the last description-alignment adjustment is stored at `build_test/Launcher ModuleLayout_Test/launcher-magisk-live.png`. A safe attempt at a post-adjustment screenshot was refused by Windows because the Launcher was not foreground; no unintended click occurred.

## Scope delivered
- The visible quick-mode Launcher shell now has 4 destinations: ShadowPlay, **Core Modules**, **Config Module**, and **Add-ons**.
- **All 11 built-in modules belong in Core**, not Add-ons: NVIDIA Container, On-Screen Control, NVIDIA NodeAPI, NVIDIA Capture, NVIDIA Graphics, ShadowPlay Helper, OSC Adapter, Duluka Hook, Ansel Adapter, FFmpeg, NVIDIA Config.
- Magisk-style full-width rows by default; a compact grid option is persisted in a host-wide loopback cookie.
- Each Core row has **CONFIG**. The Config Module page can be opened from its own navigation entry, and selecting an individual module changes its visual settings target and its *real* host inventory status (unknown/installed/stopped/running/missing).
- **Add-ons is intentionally empty**. Installed background *profiles* are visual data; they are not displayed as executable add-on modules.
- Existing Native `LAUNCHER_SET_OVERLAY`, `LAUNCHER_SET_ENGINE_OVERLAY` and module inventory remain the original owner. Config Module does not fabricate ability to enable/disable hook DLLs, game capture pipelines, or native modules.

## Background profiles (module scoped)
- Existing `.spmod` safe data-only import/install/remove RPCs and validations were reused, not duplicated.
- `module-profiles.js` now selects backgrounds **per core module**, via `spnext_module_appearance_v1_<ModuleId>` cookie. Other module's preferences are not replaced, nor is the entire Launcher background or root accent modified.
- Built-in Classic profile requires no downloaded files. Imported profile art uses the validated `profiles/assets/<id>/(...)png|jpg|jpeg|webp` same-origin path.
- The theme is applied to the selected module card and Config preview, *not* the native module UI (OSC/Capture/Windows Graphics), which has its own independent settings and visual runtime.
- The installed `profiles/catalog.json` and assets are not overwritten during code deployment.

## Files / QA / rollout
Source changes: `ui/index.html`, `ui/quick-ui.css`, `ui/quick-ui.js`, `ui/module-profiles.js`, `ui/i18n.js`.
Tests: `tests/quick-ui.test.cjs`, `tests/appearance-profile.test.cjs` plus existing Launcher tests.

Tests:
```powershell
node --test "Project\Launcher.Cef\tests\*.cjs" "build_test\Launcher Localization_Test\launcher-i18n.test.cjs"
```
**35/35 pass.**

Deploy:
```powershell
node "Project\Launcher.Cef\Modules\deploy-module-layout.cjs"
node "Project\Launcher.Cef\Modules\deploy-module-layout.cjs" --deploy
powershell -NoProfile -ExecutionPolicy Bypass -File "Project\Launcher.Cef\Modules\restart-launcher-ui-safe.ps1"
node "build_test\Launcher ModuleLayout_Test\verify-runtime.cjs"
```
Build location: `build/NVIDIA ShadowPlay/NVIDIA Launcher/Cef/Resources/launcher`.
Backup: `build/DeploymentBackups/ModuleConfig-2026-10-24T12-05-43-880Z`.
Proof: `build/DeploymentBackups/module-config-deploy-latest.json` and `build_test/Launcher ModuleLayout_Test/LIVE-VERIFIED.json`.
Five files deployed, hashes verified 5/5; controlled graceful Launcher UI close/reopen succeeded. Launcher logged `page loaded ... HTTP 200` at 19:07:54 local, live port 57186 served the 5 new files with identical SHA-256.

## Scope limits / next phase
- Native module-specific configuration (e.g. Hook CPU/GPU routing, Capture codecs, service policy) must be connected to each module's real authoritative backend, not fabricated by cosmetic controls.
- No 3rd party binary module manager has been implemented. Add-on install/remove for executable modules is intentionally not exposed.
- Pixels of the live CEF 73 window have **not** been visually compared with a screenshot yet. HTTP + DOM asset verification and test coverage are not the same as real pixel-level validation.
- Preserve all existing worktree changes from other teams; no bulk Git commit/reset.
