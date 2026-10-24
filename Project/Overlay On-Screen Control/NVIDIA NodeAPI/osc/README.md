# OSC web UI — source assets and mods

**Status: active UI source, 24 October 2026.** This is the web layer served through NodeAPI and displayed by the native OSC CEF Host. Modifying these files on `Project/` **does not** automatically change the CEF page already served from the installed `Build/` tree.

## Main owners

- `index.html`, `app.js`, `config.js` — original-style Angular/CEF OSC content and integration entry points.
- `mods/` — project-specific Main-menu/Customize, Gallery, Hook settings, Game Profiles and notifier extensions.
- `l10n/` — UI locale resources, including English and Thai; verify key completeness and actual CEF rendering separately.
- [Notifier ownership](mods/notifier/README.md) — single toast system with adapter-specific routing.

**Do not equate a visible menu with a working recording engine, Game Filter native runtime, or Hook attachment.** Test source behavior, deployed web assets, native CEF interaction and actual game composition as separate gates.

Current layout work references the original NVIDIA-style design, but game compatibility, authorization and redistribution of upstream assets require review. See [NodeAPI backend](../README.md) and [Native OSC Host](../../NVIDIA%20OSC/README.md).
