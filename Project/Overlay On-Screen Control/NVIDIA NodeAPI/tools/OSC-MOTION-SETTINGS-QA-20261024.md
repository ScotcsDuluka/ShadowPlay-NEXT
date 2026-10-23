# OSC animation preferences and input fail-open guard — 2026-10-24

## Implemented scope

- Existing Customize Main-menu panel: global Animation on/off, OSC Motion / Default, independent BG fade / Top OSC / Main-menu switches, open/close durations 50–2000 ms and separate Reset animation.
- Reset uses the previously selected 500/240 ms curves. Default delegates original NVIDIA display lifecycle; it does not imitate OSC Motion or replace the native controls.
- Disabled/inapplicable dependent controls are removed, while master and component switches needed to re-enable a feature remain reachable. Classic background hides particle-only options. Settings are retained when hidden.
- Native boxed selects have a reserved 3px green focus border and fixed 44px height. Header buttons use native hover-focus, fixed 240/120px widths, 50px height (38px short viewport), green/white native focus paint without size changes. Reset buttons are fixed 168x38px.
- New preference key is `DulukaOscMotionSettings`. Background and V5 Layout stores remain separate. Save failures roll back without notifying subscribers.

## User-reported freeze during live testing

The user reported unusable buttons, intermittent Alt+Z failure, and needing to restart OSC. Live mutation/testing was stopped. The last read found no running OSC/CEF endpoint after the user closed it. No Host/Hook/Capture/Container changes or service restarts were performed by this workstream.

Log at 06:47:43 contains `TypeError: Cannot read property '0' of undefined` in `X.getBroadcastPortalFromName` (`app.js:20:21907`, native Broadcast lookup). This is evidence of an existing native callback error, **not proof of the cause of the reported Alt+Z freeze**. Do not change Capture/Broadcast backend or dirty Host files from this task.

An independent reproducible Motion failure path existed: preparation or transition could await RAF forever if offscreen CEF stopped providing frames. Its temporary transforms and queued native close could remain. The adapter now bounds preparation and each transition, settles the latest requested state, clears input-covering CSS, and releases native snapshots even when the close callback throws. Reopening after completed close re-links current native nodes rather than reusing released records. These protections do not claim to repair an unverified native hotkey/Host failure.

## Verification

- Final selected offline regression **215/215 pass**: `build_test/OSC-Live-Preview/motion-guard-final-20261024.log`.
- Focused Motion/preferences/background/Settings regression **75/75 pass**, including timer-only tests that withhold all RAF, missing preparation frames, native close exceptions, subsequent reopening, preference rollback, and exact style restoration.
- Real CEF source preview rendered four intact Material switches and the new UI. Box height measured 44px. Native pointer hover moved activeElement onto both header buttons and their sizes remained unchanged. A subsequent source change removed focus-color transitions for immediate paint.
- Real CEF observed global-off removing mode/components/timings while leaving the master switch. Further menu/slider and final Runtime checks were interrupted by Host/CEF recreation and the freeze report. Do **not** represent the offline suite as full live Alt+Z verification.
- Evidence is preserved under `build_test/OSC-Live-Preview/motion-settings-20261024-*`, `motion-box-final-20261024-*`, `motion-box-verified-20261024-*`.
- Several captures show a closed/offscreen main menu; these are not successful whole-screen native UI proof. Read state JSON, not image names alone.

## Remaining live check

Use the user's normally started OSC, one tester, no source interception, no synthetic repeated show/route/reload loop. First read fresh state and preserve current storage. Inspect the boxed selector, hidden options, reset and header focus, then have the user test ordinary Alt+Z. If it still freezes, collect current Motion phase/classes and Host logs before a restart when possible. Do not force Host changes or declare the native issue solved.

During interrupted source tests the new Motion key may contain a test choice. The freshest pre-test snapshot is `motion-box-verified-20261024-1792799260930/before.json`; it held enabled=true, mode=default, all component flags=true, open500/close240. Compare fresh current values and only restore a recognized test-owned change, never newer user edits. Never restore an old V5 Layout snapshot over the current Game Profiles layout.
