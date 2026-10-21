# ShadowPlay NEXT — V4 Live UI Updater

- Experimental V4 code is present in Stable, but disabled by default. It must never be silently enabled for users.
- For local design iteration on an authorized running OSC, launch `START_V4_LIVE.cmd` and use the native `Alt+Z` hotkey to view the overlay.
- The updater redirects six CEF static requests to the checked-out source files. The NVIDIA native NodeAPI and injector are not duplicated.
- CSS changes update directly in the CEF view. JS/HTML changes set an `Apply JS` action that should run only when not recording and the overlay is in a permitted route.
- Use `STOP_V4_LIVE.cmd` to stop. The old CEF is restored after disabling asset interception and reloading. Local V2 saved layout keys are never modified by the V4 editor.
- Files and status: `preview-status.json`, `preview-actual-cef.png`, `preview-last-layout.json`. These are locally ignored by Git.
- If CEF disconnects before successful restoration, the watcher cannot reliably reload an offline CEF. Confirm no updater is running, reopen OSC, inspect the local V4 feature flag and restore it from `preview-localstorage-backup.json` before resuming. Do not delete the V2 keys.
- This is a development-only tool. Do not use during gameplay, recording, or system-sensitive work; use a dedicated test session to assess animation and layout.
