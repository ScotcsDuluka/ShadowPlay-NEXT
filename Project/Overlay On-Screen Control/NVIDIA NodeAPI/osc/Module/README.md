# ShadowPlay NEXT — Shadow Module Architecture v1

**Scope:** Source-only diagnostic migration. Original NVIDIA source index.html/app.js/vendor.js/common.js, legacy mods/ and the existing Production Build remain unchanged by this migration.

## Folders
- Module/Base/ — shared UI, keyboard/hotkeys, language services.
- Module/MainMenu/ — NVIDIA original layout integration, Freestyle V5, Group Editor and Legacy V4 model.
- Module/Overlay/ — Motion, Background, FPS, Renderer. Motion is optional and must not own actual native window visibility.
- Module/Settings/ — Capture, Hook, GPU, notifications, ReShade. Each has its own route ownership.
- Module/Gallery/, GameFilter/, Notifier/ — feature owners.
- Module/Core/ — manifest-driven profile generator and an optional diagnostic gate. Core/ itself must never depend on optional modules.
- Module/module-manifest.json — SHA-256 and owner mapping for every mirrored asset.

## Profiles generated FROM Source (not from Production)
- index.html — untouched existing behavior. **Never overwrite with a generated profile.**
- index.module.html — all statically referenced Source mods rewritten to mirrored Module paths, original NVIDIA asset load order preserved.
- index.core-only.html — no statically referenced extension modules. Includes a diagnostic-only interception of DOM-inserted extension script/link elements. Non-DOM loading techniques are NOT guaranteed blocked; verify loaded assets in runtime.
- index.module-no-v5.html — all groups except MainMenu/FreestyleV5.
- index.module-no-motion.html — all groups except Overlay/Motion.

Generator: node Module/Core/build-profiles.cjs
Tests: node --test Module/Core/test-profiles.cjs

Do not edit any generated index.*.html pages manually. If Source index changes, first inspect/diff it, then regenerate. Profiles are independent URLs, not toggle switches on the currently running CEF page. Opening a diagnostic URL requires a controlled test session and can disrupt the visible overlay.

## Meaning of Base vs NVIDIA Core
NVIDIA Core means vendor.js + common.js + app.js + cefquery-shim.js + user-config.js + config.js. Base/ is SHADOWPLAY NEXT shared extension code and is detachable in Core Only. Do not put route-specific feature code into Base just because it is loaded at startup.

## Migration guarantees and limitations
1. All .js, .css, and browser assets copied from Source mods/ into Module/ have identical SHA-256, and each manifest asset has one owner. Markdown docs and old backups were NOT copied.
2. No legacy files have been moved or deleted. Existing dynamically injected features may still reference /mods/ paths; source files MUST remain until all dynamic paths are mapped.
3. Certain AngularJS config/decorator/run registrations happen before Angular bootstrap; generated static pages preserve original vendor/common/app and extension tag order. This is NOT general lazy-loading.
4. The diagnostic interception only intercepts DOM appendChild/insertBefore/replaceChild for scripts and links, not all JavaScript execution paths. Check window.__spnModuleAudit.snapshot() in the CEF debugger and the real Network log.
5. True baseline acceptance requires native Alt+Z OPEN/CLOSE/REOPEN, rapid Toggle tests, MainMenu + Settings + Gallery, hotkey focus, l10n and Capture503 fallback for EVERY promoted profile.
6. FreestyleV5 vs LegacyV4 execution exclusion remains under feature-level flags. V4 model is loaded in the current Source profile even when V5 is active; that alone is not proof of a double layout owner.
7. No source-to-Production promotion has occurred. The existing Production build is preserved as an independent investigative snapshot.

## Controlled isolation protocol
1. Keep current live index.html untouched while performing the initial A/B checks.
2. Verify the Module manifest, pass tests, and verify NodeAPI actually serves diagnostic URLs before any CEF navigation.
3. In an agreed test session, run core-only first and collect Angular route, Native state, CEF console/network errors and window.__spnModuleAudit.snapshot().
4. Compare all; then no-motion and no-v5 using identical test scripts and rapid-toggle cadence. Do not inject real screen recordings to test UI.
5. Only once the faulty module is identified should individual module internals be modified; keep the original profile reproducible and compare against source hashes.
6. If Core Only itself fails, investigate NVIDIA app initialization, native page convergence, original CEF and backend instead of re-enabling every extension.

## Source and Production forensic baseline
D:\cef73-build\candidate\osc-ui-mod-reopen-analysis-20261026\MODULE-SOURCE-BASELINE-20261026.json
D:\cef73-build\candidate\osc-ui-mod-reopen-analysis-20261026\OSC-MODS-FORENSIC-READONLY-20261026.json

This v1 scaffolding is NOT a completed migration of backend dynamic feature injection and not a proof that UI bugs are fixed.
