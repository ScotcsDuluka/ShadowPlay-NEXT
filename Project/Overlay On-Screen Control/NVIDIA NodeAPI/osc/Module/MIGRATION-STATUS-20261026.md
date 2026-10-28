# ShadowPlay NEXT — Module Shadow Migration 2026-10-26

**SOURCE STAGED. NOT PRODUCTION DEPLOYED. CEF LIVE ACCEPTANCE PENDING.**

## Verified baseline
- Production Build, original Source index.html and legacy Source mods have not been altered by this migration.
- Source index SHA preserved: true
- Production index SHA preserved: true
- Original Source mods changed since snapshot: none
- Original Production mods changed since snapshot: none
- SHA evidence: D:/cef73-build/candidate/osc-ui-mod-reopen-analysis-20261026/MODULE-SOURCE-BASELINE-20261026.json

## New structure
- 59 Source JS/CSS/assets mirrored into Module/ with identical per-asset SHA-256.
- Module ownership groups: {"settings.capture":4,"gamefilter.ansel":1,"base.ui":5,"gallery.media":2,"settings.gpu-profiles":3,"settings.gpu-status":2,"base.hotkey":2,"base.localization":4,"mainmenu.group-editor":3,"mainmenu.nvidia":2,"mainmenu.legacy-v4":3,"settings.notifications":2,"notifier":6,"gamefilter.camera":1,"mainmenu.freestyle-v5":7,"overlay.background":3,"overlay.renderer":2,"settings.hook":1,"overlay.motion":4,"settings.reshade":2}
- Module manifest: Module/module-manifest.json
- Module loader/gate: Module/Core/; generator: Module/Core/build-profiles.cjs
- Original NVIDIA vendor.js, common.js, app.js and index.html remain legacy entrypoint.

## Diagnostic profile URLs for future controlled CEF session
- index.module.html — all static modules in existing NVIDIA original tag order.
- index.core-only.html — all direct modules removed; core-only script/link DOM insertion gate enabled.
- index.module-no-v5.html — only FreestyleV5 assets omitted.
- index.module-no-motion.html — only Motion assets omitted.

## Tests
- Module manifest/mirroring/ordering/gate: 9/9 pass.
- Existing OSC initialization + hotkey + Native dispatch: 33/33 pass.
- Combined: 42/42 pass.
- NodeAPI was not listening during URL verification. HTTP serving and CEF real UI test cannot be claimed.

## Important limitations
- The diagnostic dynamic gate does not intercept XHR/eval or server-side injected source. Check script and Network requests for every profile.
- Some route-injected extensions still resolve /mods; this migration intentionally did not change those routes.
- The manifest is ownership metadata, not yet a complete runtime module scheduler/hot-unplug implementation.
- FreestyleV5 and Motion share the lifecycle of Angular route transitions; only controlled A/B testing will establish the failing owner.
- No source edits to NVIDIA original UI, no Production Deploy or CEF restart were performed.

## A/B experiment sequence
1. Serve the Source diagnostic pages in a deliberate and reversible CEF test session.
2. Inspect index.core-only.html: Native state, Angular base/main-menu, Alt+Z, Settings, console, and window.__spnModuleAudit.snapshot().
3. Compare index.module.html under identical cadence.
4. Compare index.module-no-v5.html then index.module-no-motion.html to isolate ownership.
5. Re-enable only the suspect group and collect reproduction before changing its implementation.
6. Return to original index.html for rollback. Build directory remains separate and untouched.

Do not call this project stable until the real CEF A/B tests pass.
