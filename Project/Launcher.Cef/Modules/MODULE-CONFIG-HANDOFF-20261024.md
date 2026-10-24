# ShadowPlay NEXT — Core Modules / Config Module (2026-10-24)

Project: `C:\My Project\NVIDIA-Shadowplay` on `Stable`
Owner: `Project\Launcher.Cef\ui`

## Web Github-inspired Core UI — latest visual update (2026-10-24)

- Design reference only: `C:\My Project\NVIDIA-Shadowplay\Web Github\index.html` and `CSS\index\style.css`. The website was not modified and Launcher uses its existing local GeForce/MiSansThai fonts.
- Core page now has a full-width dark/green hero with GeForce typography, neon runway/angled corners, perspective grid, and a two-column grid of eleven premium module cards. Cards show a number, icon, module name, locally obtained status and description. No editable controls or fake release versions were added.
- Each card remains `data-lifecycle="suite-managed"`. Server metadata is NOT connected: source stays explicitly `bundled-fallback`, runtime state `local-launcher`, and the UI says so once in the hero information bar.
- Source edits: `ui/index.html`, `ui/quick-ui.css`, `ui/i18n.js`; regression added to `tests/quick-ui.test.cjs`. Quick UI JavaScript and Native service lifecycle are unchanged.
- Launcher test suite **39/39 PASS** before the final cosmetic CSS adjustment; final preflight passed and `git diff --check` was clean. Initial deploy 3 files (SHA-256 3/3), final CSS-only polish deployed 1 file (SHA-256 1/1), both backed up in `build/DeploymentBackups`.
- Launcher gracefully restarted successfully after both deploys. Loopback-served assets verified against installed Build: **5/5 SHA-256 PASS**, including additional Core design DOM/CSS proof from `build_test/Launcher ModuleLayout_Test/verify-runtime.cjs`.
- Pixel-level Core screenshot remains unverified: attempts to generate an isolated Chrome/Edge headless screenshot on this Windows device exited without a screenshot. No misleading screenshot claims. Temporary QA HTML was cleaned up automatically.

## Latest authoritative requirement: Core READ-ONLY / Server-owned

This section supersedes all older Config Module / theme management notes below.
User confirmed that users must NOT edit anything in Core Modules. The server will own Core catalog metadata. The browser must only display Core cards and local read-only runtime telemetry. Core Settings buttons, Layout editing control, Config detail page, and appearance/theme profile script were removed from the visible quick Launcher. No Core mutation is supported. Current catalog is a bundled fallback, clearly labelled until a verified read-only Server API exists. The Server application was NOT changed and remote catalog data is NOT yet connected.

Use CORE-CATALOG-SERVER-CONTRACT.md as the integration handoff. Add-ons retain the separate future module-manager design; zero are installed today.

## Core vs Add-ons — lifecycle policy (historical, superseded)
**Core / built-in modules (11):** belong to the ShadowPlay NEXT suite; they are NOT independently enabled, disabled, uninstalled, or updated as separate plug-ins. The authoritative ShadowPlay system/supervisor manages their lifecycle. Individual module detail/settings pages are allowed for module-specific preferences/appearance, NOT independent lifecycle power switches. Inventory status is read-only telemetry.

**Add-ons:** independent optional modules. Future supported add-ons may have individual install/update/enable/disable/remove actions, but only when actual validated host APIs, safety restrictions, and lifecycle ownership are implemented. The Add-ons page currently has zero installed add-ons and does not expose fake switches.

UI enforcement: each of the 11 core cards uses `data-lifecycle="suite-managed"` and contains a Settings action rather than an enable/disable control. New EN/TH help text clarifies these rules in the Core, per-module Settings, and Add-ons views. 35 Launcher regressions passed; the UI was deployed (3 files, SHA-256 3/3), Launcher restarted gracefully, and loopback HTTP verified all served UI files against Build with 11 policy-marked core cards.

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
