# OSC Source & Live Preview Contract — Stable

**Source authority:** `C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\osc`

**Git authority:** `C:\My Project\NVIDIA-Shadowplay` on `Stable`. Confirm the current commit before changing files. Other features (Capture, Hook, Filter) may be edited concurrently in the same worktree.

**Main complete runtime:** `C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay`

**Runtime OSC frontend:** `build\NVIDIA ShadowPlay\Overlay On-Screen Control\NVIDIA NodeAPI\osc`

## Three different things previously called "preview"

1. **Project source:** Files in `Project/.../NVIDIA NodeAPI/osc`. This is where code must be developed and reviewed.
2. **Actual OSC/CEF:** Usually `http://localhost:59011/index.html`. In `NVIDIA NodeAPI/index.js` the Express static root is `path.join(__dirname,'osc')`. If the executable/server runs from **Build**, this serves **Build** files, *not* Project files.
3. **Sandbox/browser mockup:** `_ShadowPlay-Classic-V5-Sandbox/preview.html` is only a simulated frontend QA page. Passing these checks does not prove integration with real OSC/CEF.

No automatic Project -> Build sync exists. Editing Project alone does not update the live Build frontend. An explicit, backed-up OSC-only preview command is now available in tools/osc-live-preview.cjs (see README-OSC-LIVE-PREVIEW.md). A Chrome refresh does not change the source directory selected by the serving process.

## Required development flow

1. Modify **only Project/ on Stable** as primary source. Avoid copying an entire tree or archive over it.
2. Run tests from `Project/.../NVIDIA NodeAPI/tests`, including Customize, Language Sync, OSC Motion and any affected features.
3. Use `tools/osc-preview-audit.ps1` (read-only) to check HEAD, source hashes, runtime hashes, HTTP listener, and the actual content served on 59011.
4. When ready for a real OSC/CEF test, use an **explicit, backed-up, source-to-runtime frontend sync** with a manifest of the source commit and file SHA-256 hashes. Never treat edits inside Build as source work. Do not enable hidden or automatic syncing until a safe rollback path is in place.
5. Refresh/reopen the real OSC/CEF window and rerun the audit; confirm `liveOrigin` shows the expected source-mirrored bytes. If 59011 is offline, no real CEF test has been performed.
6. Integrate and test changes in Stable; maintain Build as the known-working runtime outside deliberate preview/deploy operations.

## Important constraints

- `NVIDIA ShadowPlay.7z` is a **Customize UI reference**, not a replacement for all the newer Project source.
- Preserve NVIDIA native `md-menu`, `md-icon` / `share-icon`, their existing control handlers, and OSC Host scaling.
- OSC Motion in Project includes V4 native-position **preflight before motion capture**; Build/.7z has an older Motion file. Verify their interaction before combining them.
- Keep **one owner of native positions** (do not allow V2, V4, V5 to reflow the same `md-menu` during animation).
- Keep the Launcher locale publisher and `launcher-language-sync.js` / localization files. Never overwrite the runtime-generated `mods/launcher-language.json`.
- Do not mirror Capture, Hook, NodeAPI backend, Launcher, or NVIDIA Container from a different branch merely to test an OSC frontend change.
- Preserve changes from concurrent Stable editors; scope tests and commits to OSC paths and never stage all files blindly.

## Read-only verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-preview-audit.ps1"
```

For exact SHA-256 evidence:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI\tools\osc-preview-audit.ps1" -AsJson
```

**Audit status as of 2026-10-23:** Branch Stable; real OSC port 59011 was offline at the time of verification. No deployment or startup is implied by this document.
