# ShadowPlay NEXT — Layout V5 implementation contract

Status: **Phase 1, pure layout model only**. This is NOT a release, and it must not
be confused with the current experimental V4 / Group Layout A preview.

## Canonical inputs
* Behavior, NVIDIA styling, Motion, icons and working OSC host are referenced from
  C:\Users\ScotcsDuluka\Downloads\NVIDIA ShadowPlay.7z
  (read-only extraction in C:\My Project\_ShadowPlay-Archive-Clean-Audit-20261023).
* The SOURCE to change/commit is Project/ on Git Stable. Do not copy entire archive
  onto Project because Project contains newer Launcher Language Sync and other fixes.
* The existing Build runtime (Capture / Hook / Launcher / Container / NodeAPI)
  is preserved. Preview deploy, if needed, is explicit and backed up.

## Phase 1: Model in Project/Stable

    osc/mods/nv-layout-v5-core.js
    tests/nvLayoutV5Core.test.js

The new model does **not** import or depend on the experimental NvGroupLayoutA or
NvUnifiedLayoutV4. It does not install a native DOM adapter, reflow md-menu, write
localStorage, create CSS overlays, load into index.html or invoke Capture commands.

Rules implemented:
* Global max six 240x240 Groups across unrestricted left/center/right Panels.
* Native-looking 1 left / 4 center / 1 right default with twelve unique Controls.
* Each Group: up to three Small or Side 80px rows, or one Big 240px Block.
* Small: triple, wide-left, wide-right, single. Side: single, detail-left,
  detail-right. Big: single. One Control per Slot.
* Group Auto-Fill ON = 240/n rows (n=1/2/3), OFF = 80px rows. Icon scale stays 1.
* Empty Groups remain in data/quota but are hidden from Normal geometry; visible
  in Edit geometry. Nonempty Groups compact together automatically in Normal.
* Swap assigned Controls, place from palette into free Slot, Group swap/insert/
  move cross Panels, pure insertion preview proposal.
* Dropping/removing Group/Block or reducing Slot count releases non-Settings
  Controls to the unassigned catalog.
* Settings cannot be removed, duplicated, released by reducing Slots or deleted
  along with its Group.
* In-memory Edit session: preview state immediately, undo, cancel, and Done
  persisting via a supplied callback only after validation.

## Phase 2 integration requirements (NOT implemented)

* First disable legacy V4/Group Layout A **for the V5 session only**, but preserve
  their saved user layout keys for rollback. Have exactly ONE native positioning owner.
* Connect the model to the existing NVIDIA md-menu tiles and real click handlers;
  edit UI should not dispatch native click commands while dragging.
* Edit Mode only: right-click md-menu context menu for Block Type / Layout;
  empty-Group and free-area drop targets, +Group preview, timed insertion preview,
  animations tied to the existing OSC Motion's idle/entrance lifecycle.
* Keep **normal mode** indistinguishable from the .7z native NVIDIA menu and
  do not display Group guides, Slot borders, or editor controls.
* Existing NVIDIA Settings panel instead of Group Layout A fullscreen dashboard;
  Background Settings 80%, Controls 20%, and original NV Material components.
* Ensure Undo/Done/Cancel/Escape, persisted Auto-Fill, and no stale native
  coordinates after OSC Motion, Settings navigation, or route transitions.
* Run both existing OSC regressions and visual/integration tests on real CEF,
  then explicit backed-up Project -> Build OSC preview; do not alter Capture.
