# Game filter / Photo mode preferences QA

Date: 2026-10-20 (Asia/Bangkok)

## Delivered UI

- Retained NVIDIA OSC tabs, boxed select, switches, buttons, typography, colors and focus states.
- Removed the nested dark content surface and duplicate heading. Labels/descriptions and controls now share aligned rows.
- Added layout-only responsive rules; no replacement theme or control artwork.
- Explicitly disabled and grayed out Install ReShade. No installer action or download is attached.
- Kept Duluka Freestyle, NVIDIA Freestyle and Photo mode honest about their unavailable status.
- Distinguished permission to connect from proof of a live ReShade connection.
- Added bounded 5000 ms load/save requests, validation of returned settings, rollback, retry and duplicate-save protection.
- Disposed page controllers ignore late responses after navigation. Opening/leaving/reopening does not accumulate panels/scopes.

## Automated tests

23 tests passed, 0 failures. Full output: `automated-tests.txt`.

Files:

- `tests/freestyleSettingsUi.test.js`: loading, invalid responses, timeout options, rollback/retry, duplicate saves, disposal, unavailable modes and native template semantics.
- `tests/gameFilterSettings.test.js`: persistence/remount, strict validation, disk failures, disabled bridge, in-flight races and queued native command permission checks.
- Existing `tests/gameFilter.test.js`, `tests/gameFilterUi.test.js`, `tests/anselClient.test.js`: regression coverage, including HTTP-to-native named pipe integration with explicitly fake effects.

An intentionally malformed settings file produces a warning in the test output; that failure-path test passed.

## Chromium visual and interaction tests

11 scenarios passed in the actual OSC Chromium renderer using real NVIDIA Angular components and an isolated mock HTTP model. Result details: `visual-results.json`.

1. Loading disables interactive settings; Install ReShade looks disabled.
2. Load failure displays an error and retry succeeds.
3. Labels and controls do not overlap.
4. Exactly three mode options appear; Escape dismisses the native select.
5. Unavailable modes hide ReShade switches and display the correct availability text.
6. Saving disables controls; failure restores saved values; retry sends the intended failed value.
7. Photo mode opens and remains unavailable.
8. Keyboard focus and Space operate a native switch exactly once.
9. A 960px CSS viewport has no horizontal overflow or intersecting labels/controls.
10. A 640px CSS viewport has no horizontal overflow or intersecting labels/controls.
11. Real backend settings are unchanged by visual QA.

Responsive tests used isolated iframes, leaving game/OSC display geometry unchanged. Mock errors and saves do not touch the real user's settings or shaders.

## Live page smoke test

Details: `live-results.json`.

- Actual deployed UI loads its real saved configuration and uses native NVIDIA controls.
- Three leave/reopen cycles retain one settings panel; root watcher count stayed at 136 before/after.
- Route-only hiding classes are cleaned up after leaving the page.
- Game filter and Photo mode tabs both render successfully.
- Preview images: `live-game-filter.png`, `live-photo-mode.png`.
- Additional screenshots cover loading, load/save failures, dropdown options, unavailable modes and narrow viewports.

## Deployment state

- Frontend JS/CSS copied to the active runtime and reloaded in OSC. Live UI smoke test passed.
- Extra backend race fixes passed the source test suite and were copied to the runtime files, but the existing Web Helper process has **not** reloaded them.
- Automatic approval review rejected the combined deployment command containing a Web Helper stop/start with `blocked by policy`; it gave no more specific reason. Deployment continued using file copies and a frontend page reload only.
- Backend race fixes will load on the next normal application/Web Helper startup. They prevent obsolete pending preferences, queued commands and late replies from reconnecting a disabled bridge. Already-sent native work cannot be unsent.
- No game process was stopped or restarted. No ReShade installation was attempted.
- Previous runtime files are preserved under `backup-files-25691020-145133/`.

## Reproduction

Run the five test files listed above with Node's `--test` runner. Run `visual-qa.cjs` and `live-smoke.cjs` from the workspace root while OSC's diagnostic page is available at localhost:59099. The visual harness removes its temporary UI and does not modify actual backend settings.
