# OSC web UI — source assets and Module extensions

**Status: active UI source, 29 October 2026.** This is the web layer served through NodeAPI and displayed by the native OSC CEF Host. Modifying these files on `Project/` **does not** automatically change the CEF page already served from the installed `Build/` tree.

## Main owners

- `index.html`, `app.js`, `config.js` — original-style Angular/CEF OSC content and integration entry points. `index.html` is the generated production page; the pristine pre-cutover page is `index.legacy.html`.
- `Module/` — project-specific extensions organized by owner (Base, MainMenu, Overlay, Settings, Gallery, GameFilter, Notifier, Core). The legacy flat `mods/` tree was retired on 2026-10-29; the production page loads everything from `/Module/` (static bootstrap + `Module/Core/late-extensions.js`).
- **Mandatory UI guide:** [อ่านก่อนทำ UI](Module/docs/อ่านก่อนทำ%20UI.md) — read before changing OSC UI, **including section 8 on creating Menu/Settings/Submenu/Tab/Dialog routes and NVIDIA page shells**; [NVIDIA OSC UI Reverse Engineering Atlas](Module/docs/NVIDIA-OSC-UI-REVERSE-ENGINEERING.md) inventories routes, components, UI owners, loader, CSS, icons, input and CEF; the [complete Component/Route/Icon Catalog](Module/docs/NVIDIA-OSC-UI-COMPONENT-CATALOG.md) lists the names extracted from Source. [`AGENTS.md`](AGENTS.md) enforces this workflow for agents operating in `osc/`.
- `l10n/` — UI locale resources, including English and Thai; verify key completeness and actual CEF rendering separately.
- [Notifier ownership](Module/docs/notifier-README.md) — single toast system with adapter-specific routing.

### Capture UI refresh v77 — 25 October 2026

The **Video Capture** and native-route **Advanced Capture** panels now use scoped NVIDIA-native controls and an explicit split between live settings and the non-persisted Pipeline Design preview. Version 77 UI assets were staged to Build and **Source/Build hashes matched 4/4**. The targeted Capture UI suite recorded **32/32 PASS**. An actual CEF 73 **Advanced** view at 1920x1200 showed two section groups, two columns and no measured overflow into the action area. **Video Capture CEF visual/geometry is not yet signed off**; none of these UI results validates actual GPU capture or recording.

[Capture UI v77 deploy and remaining acceptance](Module/docs/CAPTURE-UI-REDESIGN-20261025.md) · [OSC native component/route inventory](Module/docs/NVIDIA-OSC-UI-COMPONENT-CATALOG.md) · [Source/runtime cross-plane architecture audit](../../../../docs/osc/OSC-BACKEND-FULL-ARCHITECTURE-AUDIT-20261025.md).

### Newly integrated UI surfaces (24 October 2026)

- **ReShade Manager:** guarded guided Setup handoff, read-only inspection evidence and no game-file mutation from NodeAPI. The CEF click-through and actual game effect are not yet confirmed. [Inspection and safety report](../NODEAPI-RESHADE-MANAGER-20261024.md).
- **Advanced Capture (native Settings route):** opened from Video capture using `osc-button` and registered as `main.osc-capture-advanced` before Angular bootstrap. Its six Pipeline Design selects remain non-persistent previews (Recovery disabled); live Encoder/Preset controls load actual Capture settings and permitted Container catalog routes before allowing Save. The main Video page stays lightweight, NVIDIA `md-*` components retain native dimensions, and Back restores `main.preferences.video`. [UI layout and v75 migration](../../../../docs/UI-CAPTURE-PIPELINE-SIZE-LOCK-20261024.md). CEF visual click-through is a separate validation gate.
- **Hook Transport settings:** CPU/GPU selections depend on the actual native per-API transport policy. D3D9 Windowed GPU is a preview; D3D9Ex GPU mailbox tests do not enable the current production GPU selector. [Native Host README](../../NVIDIA%20OSC/README.md).

**Do not equate a visible menu with a working recording engine, Game Filter native runtime, or Hook attachment.** Test source behavior, deployed web assets, native CEF interaction and actual game composition as separate gates.

Current layout work references the original NVIDIA-style design, but game compatibility, authorization and redistribution of upstream assets require review. See [NodeAPI backend](../README.md) and [Native OSC Host](../../NVIDIA%20OSC/README.md).
