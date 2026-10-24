# ShadowPlay NEXT — Appearance Modules v1 (.spmod)

> **Historical/developer reference, 24 October 2026:** The current Launcher quick UI no longer exposes Core configuration, theme/background editing, or the `.spmod` installer. Core Modules are **11 read-only built-in suite components**. The visible Add-ons page currently contains **zero installed add-ons**. This document preserves the **data-only package format and installer tooling**, not a claim that its buttons are still visible in the latest quick UI. The future Core catalog is **Server-owned by design**, but no remote catalog API is verified or connected. [Current Core contract](CORE-CATALOG-SERVER-CONTRACT.md) · [Timeline and superseded UI designs](MODULE-CONFIG-HANDOFF-20261024.md).



**CURRENT POLICY (2026-10-24): Core Modules are read-only and server-owned.** The quick Launcher no longer exposes Config Module, per-Core Settings, background/theme edit or .spmod management. Module names/descriptions currently use bundled fallback, runtime state comes from local Launcher, and the authoritative server catalog is NOT CONNECTED yet. Add-ons is empty. See `CORE-CATALOG-SERVER-CONTRACT.md` for the future server integration. The installation instructions below are HISTORICAL documentation of a preserved but currently hidden data-only appearance backend. They are NOT available in the quick Launcher UI.
This phase installs **data-only** visual modules. A .spmod is a ZIP file with
exactly two members:

```
module.json
assets/background.png
```

Example `module.json`:
```json
{
  "schemaVersion": 1,
  "type": "appearance",
  "id": "forest",
  "name": "Forest",
  "version": "1.0.0",
  "author": "My Name",
  "background": "assets/background.png",
  "accent": "#76B900",
  "accentBright": "#9BD532"
}
```

### Install and remove directly from the Launcher (Phase 2)
Open **Config Module → select a core module → Themes & backgrounds**. The native Launcher UI now
offers **Import Background**, **Install .SPMOD**, **Apply Profile**, and
**Remove Selected**. Import Background opens a local image picker for
PNG/JPG/WEBP, automatically packages the image as a safe appearance module,
and saves the export under `%LOCALAPPDATA%\ShadowPlay NEXT\ExportedModules`.

Install invokes the standard Windows Open dialog, uses only the bundled
PowerShell installer, and refreshes the catalog. Remove confirms first,
updates the catalog, and stores the previous image/catalog under
`%LOCALAPPDATA%\ShadowPlay NEXT\ModuleBackups`. The built-in
NVIDIA Classic profile is protected. User-provided DLL/EXE scripts are
never executed. This is not a signed third-party code/plugin ecosystem.

### Create and install a profile from an image

Run on your own Windows PC in PowerShell:
```powershell
$root = 'C:\My Project\NVIDIA-Shadowplay\Project\Launcher.Cef'
& "$root\Modules\new-appearance-package.ps1" -ImageFile 'C:\Pictures\my-wallpaper.png' -Id 'my-wallpaper' -Name 'My Wallpaper' -Output "$env:USERPROFILE\Desktop\my-wallpaper.spmod"
& "$root\Modules\install-appearance-module.ps1" -Package "$env:USERPROFILE\Desktop\my-wallpaper.spmod"
```

For independent integrity verification, specify
`-ExpectedSha256 <SHA-256>` when installing a package. To explicitly update a
module with the same ID, pass `-Replace`. The installer makes a rollback
backup of the old catalog/assets.

**After installation, reopen Launcher** so it reloads the optional registry.
Go to **Config Module**, select the target core module and background profile,
then press **Apply Profile**. The profile changes only that module's visual card/preview. The default is NVIDIA Classic; each module's selected background is
persisted in its own localhost cookie (shared across ephemeral local HTTP ports).

Installed data:
```
Build/NVIDIA ShadowPlay/NVIDIA Launcher/Cef/Resources/launcher/
  profiles/catalog.json
  profiles/assets/<module-id>/background.png
```
Demo:
`Build/DeploymentBackups/appearance-packages/next-grid-demo.spmod`.

### Security and limits
- Appearance modules are not scripts/plugins. No EXE, DLL, HTML, SVG,
  JavaScript, postinstall hooks or shell commands are loaded from archives.
- Accepts one fixed-format image (PNG/JPEG/WEBP), a declarative manifest,
  a strict lowercase module ID, #RRGGBB accents, and max 12 MB ZIP/10 MB
  unpacked data. Rejects traversal, symlinks, duplicate/missing fields, wrong
  SHA, unsupported types, and unsafe paths.
- Do not install profiles from untrusted sources: image decoders can contain
  vulnerabilities, particularly in the **old CEF 73 runtime**. Treat the
  installer validation as defense-in-depth, not as a digital signature.
- Native third-party module loading, dependency resolution, sandboxing,
  permission prompts, package signing, updates, uninstall UI, and automated
  activation are **NOT implemented** in this phase. They must be engineered
  and tested before external code is allowed to execute.
- No changes to NvContainer, NvCapture, NodeAPI or native OSC runtime.
- Isolated regression: `tests/appearance-installer.test.ps1` and
  `node --test tests/appearance-profile.test.cjs`. These tests do not
  touch deployed profiles.
