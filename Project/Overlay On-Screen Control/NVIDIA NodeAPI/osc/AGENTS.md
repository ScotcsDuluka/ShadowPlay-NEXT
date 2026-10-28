# NVIDIA OSC UI — mandatory agent instructions

Scope: this directory (`osc/`) and every nested directory, including `mods/`.

## REQUIRED BEFORE ANY UI WORK

1. **Read `mods/อ่านก่อนทำ UI.md` and `mods/NVIDIA-OSC-UI-REVERSE-ENGINEERING.md` completely BEFORE editing, building, or designing any OSC UI** (`app.js`, `index.html`, `mods/*.js`, `mods/*.css`, routes, previews, templates). Treat its UI reuse, size/layout, state-truth, localization, test and deployment rules as mandatory instructions for this project.
2. For component/route/icon lookup, use `mods/NVIDIA-OSC-UI-COMPONENT-CATALOG.md` (generated static inventory) and inspect the actual target UI and original NVIDIA/Angular Material implementation in the current source. Prefer existing `md-*` and `nv-*` components, `osc-button` styling and native interactions. Do not fake or clone native controls when originals are suitable.
3. Never globally override or force fixed sizes onto `md-*` to hide overflow. Keep Main-menu native geometry separate from Settings responsive layout.
4. Preserve live API/Host state semantics, original keyboard/focus/hover/click behavior, localization and route transitions. Unsupported runtime features must not be represented as working.
5. Verify project source/tests AND actual deployed CEF for changes claimed to be deployed. Treat Project and installed Build as distinct. Do not deploy or restart for docs-only edits.
6. **For every new Menu, Settings, Submenu, Tab or Dialog:** follow section 8 of `mods/อ่านก่อนทำ UI.md` before writing code. Inspect the existing UI-Router state/menu owner, register the route and boot-loaded script correctly, use the native NVIDIA shell, preserve context-sensitive Back/focus/ESC, and test the full navigation cycle. Never create a visually fake menu without real routing and handlers.
7. At handoff, explicitly state that `mods/อ่านก่อนทำ UI.md` was read, what native components were reused, exceptions (if any), tests performed and whether deployment/CEF validation actually occurred.

If there is a conflict, follow higher-priority instructions and explain any deviation. The guide documents engineering conventions; it does not supersede user safety/security or licensing requirements.
