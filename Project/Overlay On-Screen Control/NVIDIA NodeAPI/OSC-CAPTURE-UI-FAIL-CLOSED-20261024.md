# OSC Capture Video Settings — Fail-closed UI state (2026-10-24)

Scope: NVIDIA NodeAPI OSC Video Settings module only. This task does **not** touch Duluka Capture/DXGI/NVENC backend, recording REST methods, OSC native Host or other agents' concurrent OSC files.

## Finding

The current `osc/mods/video-capture-options.js` mounted a custom Video Settings panel under the native NVIDIA Preferences/Video page. Before this fix, when the initial settings GET failed, `setUnavailableSettings()` called `defaultSettings()` **and set `captureSettingsReady=true`**. This enabled `md-select` and capture-cursor controls despite the actual saved settings being unknown; a user changing an option could POST the whole synthetic default state to `/DulukaCapture/v.1.0/settings` and overwrite real configuration values.

A separate display problem: the UI did not check HTTP status before showing the Capture runtime status JSON. An unsuccessful HTTP response with contradictory fields could be incorrectly displayed as `Ready / Recording`.

## Fix

- Unavailable settings **keep `captureSettingsReady=false`**, show a clear "Select Reload to retry" error, and never authorize a POST based on placeholder/default values.
- Each control's Angular `ng-change` handler and the central `save()` function also guard on `captureSettingsReady`. This avoids bypass through synthetic events.
- Reload disables editing while a fresh GET is pending. On failure it reuses the same unavailable/error posture, rather than leaving controls open.
- Initial load, Reload and successful save responses validate the expected Capture settings structure (engine/API/encoder, finite resolution/FPS/bitrate and selected preset) before accepting a response as confirmed state.
- Runtime status view treats non-200 or malformed `/DulukaCapture/v.1.0/status` as unavailable, even if the response happens to contain `ready: true`.
- No changes to native NVIDIA layout, `md-select` styles, CSS, backend REST routes or device/sound settings.

## Test evidence

New `tests/videoCaptureOptionsUi.test.js` exercises the actual OSC module in a minimal Node VM with test-only introspection added **in memory** (never shipped to CEF). It does not launch the browser, NodeAPI server or any capture device. Assertions:
1. GET failure disables settings, gives retry instruction, and disallows POST or control mutations.
2. Valid settings remain saveable; malformed/unsupported settings do not count as confirmed.
3. Invalid POST response is not shown as "Saved".
4. HTTP 503 with a misleading `ready=true` body is NOT shown as Recording.
5. HTTP 200 with correct fields still displays Recording.

Commands used (read-only tests):
```powershell
cd 'C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA NodeAPI'
node --check 'osc/mods/video-capture-options.js'
node --test 'tests/captureEngineStatus.test.js' 'tests/videoCaptureOptionsUi.test.js'
node --test 'tests/freestyleSettingsUi.test.js' 'tests/nvFreestyleSettings.test.js'
```

**Pass: 5/5 new Video Settings tests, 10/10 combined Capture Catalog + UI tests, and 7/7 adjacent NVIDIA Settings/Freestyle UI tests.** No test issued a real capture request or wrote actual configuration.

## Remaining validation gates

- This is **source-level/UI-state regression**, not a live CEF rendering or installed OSC deployment validation.
- Existing recording Start/Stop UI or the built-in NVIDIA Angular app is not changed by this patch. Investigate its actual symptoms separately before touching minified `app.js` or currently dirty native Host/Hook files.
- Do not restart the running NvCapture worker, capture the user's screen/music or overwrite other agents' dirty files without prior authorization and a scoped acceptance test.
- If testing a live deployed OSC UI, preserve existing desktop applications and current audio playback.
