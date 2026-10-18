# Backend/data — runtime state (generated, do not hand-edit)

| File | Written by | Content |
|---|---|---|
| `state.json` | lib/store.js | sectioned settings/state persistence (`recordSettings`, `instantReplaySettings`, `broadcastSettings`, `recordPaths`, `hotkey:*`, `account`, `dulukaCapture`, `piplConfig`, `audio`, `desktopCapture`, `language`, ...) |
| `hardware-floor.json` | lib/hardwareProbe.js | per-machine WMI probe cache (boot-time; `HARDWARE_REPROBE=1` forces refresh) |
| `logs/backend.log` | lib/logger.js | backend log |
| `logs/page.log` | routes/debug.js | osc page console bridge lines (`POST /ShadowPlay/v.1.0/Debug/PageLog`) |

Delete `state.json` to factory-reset every settings screen.
Delete `hardware-floor.json` to force a fresh hardware probe on next boot.
