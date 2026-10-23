# ShadowPlay NEXT Layout V5 — Native Adapter Phase 2

State: integrated behind a disabled-by-default owner flag. This phase does
not claim the full Drag/Drop/Context Menu/Settings editor is finished.

Source: Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc on Git Stable.
Behavior/style reference: NVIDIA ShadowPlay.7z. Real runtime lives in Build
and is updated only through the SHA-256 backed-up OSC preview utility.

## New files

- mods/nv-layout-v5-core.js — tested six-Group model from Phase 1.
- mods/nv-next-v5-native.js — new NEXT owner: reflows the ORIGINAL NVIDIA
  md-menu and menu-button nodes, with no clones or fake visuals.
- mods/nv-next-v5-native.css — scoped V5 styles; Normal Mode has no editor UI.
- tests/nvNextV5Native.test.js — ownership/native identity tests.

index.html loads NEXT model and adapter BEFORE osc-motion.js. Motion preflight
calls NEXT first only if enabled; otherwise V4 retains its existing behavior.
V4 and experimental Group Layout A refuse ownership when NEXT is enabled.
Their saved keys are preserved.

## Feature gate

LocalStorage key: DulukaNextNativeLayoutV5Enabled. OFF by default.
A clean OSC CEF page reload is required on owner switch.

To enable in authorized OSC DevTools console:

    window.__nvNextLayoutV5.enable(true); location.reload();

Rollback:

    window.__nvNextLayoutV5.enable(false); location.reload();

Read-only inspection:

    window.__nvNextLayoutV5.get()
    window.__nvNextLayoutV5.sync()
    window.__nvNextLayoutV5.model.geometry(window.__nvNextLayoutV5.get(),'normal',1920)

NEXT model supports staged beginEdit/edit/undo/done/cancel transactions.
Persistence occurs on Done. The user-facing 80/20 editor, drag/drop, context
menu and native input guards are NOT ready yet; do not use this Editor mode
in a production build.

## Guardrails

- Never copy the .7z archive over all of Project/ (would lose Language Sync).
- Do not merge the experimental Group Layout A geometry into NEXT V5.
- No change to Capture, Hook, NodeAPI backend, NvContainer, Launcher, OSC host.
- Verify actual CEF via 59011/59099 instead of inferring from unit tests.
- Do not turn NEXT V5 ON by default before full Edit Mode is validated.
