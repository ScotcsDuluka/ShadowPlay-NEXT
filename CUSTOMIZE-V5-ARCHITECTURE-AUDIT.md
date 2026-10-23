# Customize Main-menu — structural root-cause audit (read-only)

Scope: NVIDIA OSC Customize Main-menu UI, not the entirety of the Capture backend. Audited production **Source** in `C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc`, without changing Source or Runtime.

## Confirmed architectural conflicts

1. **Three different layout representations:** `main-menu-live.js` still renders a V2 Angular `nv-live-editor` and binds inline `ng-style` for `margin-top` and `max-height`; `main-menu-unified-native.js` layers V4 native geometry and a separate persisted layout; V5 `main-menu-group-editor.js` loads its own persisted layout, initially converted from V4, while the native renderer delegates to V5. These should not independently own screen geometry and storage.
2. **Competing CSS declarations:** `main-menu-live.css` declares a legacy `.nv-live-editor` near `top:448px` and later `top:390px` including `!important` variants before a subsequent fullscreen 40px shell. `main-menu-unified-native.css` also overrides padding in multiple blocks. `main-menu-group-editor.css` then switches layout to flex and alters scrollbar behavior. Cascade order can change appearance and click reachability across route transitions, window sizes and flags.
3. **Several owners of route/motion timing:** the V4 native bridge explicitly defers geometry during OSC Motion entrance/exit and unstable zero-sized menu rails; V5 triggers its own V4 sync after a persisted update. Editing/route lifecycle must be governed by one coordinator rather than each layer compensating with independent timing/fallbacks.
4. **Multiple scroll/layout policies:** the legacy editor uses absolute positioning and auto overflow, V4 has grid/panel scroll rules, V5 introduces full-screen flex and independent Group/Background scroll. The user reported repeated overflow and visual inconsistency well before the most recent temporary CEF CSS audit.

## Consequences / fixes by priority

- **P0 ownership:** establish one canonical Customize state and renderer, with one optional migration path from V2/V4 to V5. Keep the legacy storage untouched until migration is explicitly confirmed. Do not parallel-write old/new models.
- **P0 geometry:** one file must own the 40px shell, real NVIDIA native Top Bar, preview band, content region, and scrolling. Legacy CSS must have clean opt-in/off isolation, not important overrides stacked over each other.
- **P1 lifecycle:** one route/motion transition coordinator waits for a stable native anchor before geometry updates; tests must include opening, closing, Settings return, repeated navigation and window resize.
- **P1 interaction:** hit-test native tiles and settings controls, keyboard focus, editor drag/drop, undo, empty/duplicate Controls and persistence failures; ensure original capture actions are not fired by editing hit tests.
- **P2 visual acceptance:** use real CEF on 1920x1200, 1366x768, 2560x1440, compare screenshots; do not equate Node green tests with native visual acceptance.

## Status

- This report is a structural diagnosis from existing source code, **not** proof that every symptom has the same cause.
- Sandbox Clean Rebuild remains un-deployed. It has a scoped CSS shell, but all live CEF behaviors—including the user's long-standing visual concerns—must pass acceptance before replacing Runtime.
- The production source and `Stable` remain unchanged by this audit.