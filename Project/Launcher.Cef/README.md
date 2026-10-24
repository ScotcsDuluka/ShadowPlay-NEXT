# Launcher.Cef — CEF launcher lane (root Launcher.exe + NVIDIA Launcher/Cef/Launcher.dll)

## Current quick UI: Core READ-ONLY (24 October 2026)

**Account Portal integration (source update):** The visible Launcher includes an **Account** button backed by the trusted-frame `LAUNCHER_OPEN_ACCOUNT_PORTAL` native command. It opens a fixed HTTPS Duluka Account Portal URL using the operating system's **external browser**; the Launcher does not collect passwords or own login sessions. The shared-account Portal currently disables new public registration and CBT1 submission. The external link and its source regression do **not** prove remote account login or enrollment acceptance. See [Duluka Account Server](../../Server/README.md).

**This section supersedes the historical UI and Config Module descriptions below.** The current visible quick Launcher has **ShadowPlay ON/OFF**, **Core Modules** (11 built-in, suite-managed and *read-only*), and **Add-ons** (zero installed). The Core cards show local inventory/runtime telemetry but provide **no user editing, layout controls, per-Core settings/background customization, or independent enable/disable/uninstall**. Module-specific options, where supported, remain owned by the corresponding modules—not the Core catalog UI.

The Core catalog is currently a clearly labelled **bundled fallback**. A **Server-owned read-only catalog** is designed, but **no verified remote endpoint or authentication flow is connected**. Never label the fallback as a live Server response or invent remote version numbers. The existing appearance `.spmod` data-only installer remains in source/on disk but is **not loaded by the visible Core quick UI**. See [the latest owner contract](Modules/CORE-CATALOG-SERVER-CONTRACT.md) and [handoff with superseded historical sections](Modules/MODULE-CONFIG-HANDOFF-20261024.md).

**Release gating:** CEF 73/no-sandbox security, current quick-mode end-to-end CEF behavior and module telemetry require version-specific validation. Earlier deployed UI screenshots and 11-card/config-button tests below are **dated observations, not a promise of today's visible controls**.


## Simple Launcher — three destinations (2026-10-24)

The default Launcher is intentionally limited to:
1. **ShadowPlay** — ON/OFF using the existing `tglOverlay` /
   `LAUNCHER_SET_OVERLAY` bridge, plus Open Overlay.
2. **Core modules** — Container, OSC, NodeAPI, Capture, Graphics, Helper.
3. **Add-on modules** — OSC Adapter, Duluka Hook, Ansel Adapter, FFmpeg,
   and the existing data-only Appearance/Background module manager.

The interface uses `ui/quick-ui.js`, `ui/quick-ui.css` and new markup
in `ui/index.html`. The former full dashboard remains hidden but retains
existing IDs/RPC event handlers for compatibility and rollback. The native
Supervisor's `modules` inventory is still the source of truth: process
presence and package-installed state are not treated as engine readiness.
No new process installation, DLL injection, or arbitrary third-party code
execution was introduced. The Exit All and OBT3 buttons are hidden in this
simple presentation, not deleted from the native backend. The appearance
manager is moved into the visible Add-ons tab without duplicating its IDs.
Its selected image is applied to the new visible main panel.

Validation: `node --test tests/quick-ui.test.cjs` — 4/4 PASS, existing
Launcher UI/localization/appearance tests — 25/25 PASS, Git diff check PASS.
Launcher-only staging completed with a backup; real CEF UI rendered at
1152x648. The live power-off action is intentionally NOT exercised during
this UI acceptance pass, to avoid disrupting the user's running overlay.
Previous historical UI descriptions remain below for project history.

## All-module navigation — 2026-10-24

The sidebar exposes the active product modules: NVIDIA Capture, NVIDIA
Graphics, On-Screen Control, NVIDIA NodeAPI, NVIDIA Container, OSC Adapter,
Duluka Hook, Ansel Adapter, ShadowPlay Helper, FFmpeg and NVIDIA Config.
Launcher itself remains the Overview page; Services remains a separate
system summary. The module list scrolls independently of the content.

`LAUNCHER_GET_STATE` and native pushes now include a `modules` object with
fixed IDs `Capture`, `Graphics`, `Overlay`, `NodeApi`, `Container`, `Adapter`,
`Hook`, `Ansel`, `Helper`, `Ffmpeg`, `Config`. Each reports `installed`,
`processTracking` and `running`. Process identity is checked against this
product's exact executable paths; Adapter also recognizes its x86 sibling.
Hook, Ansel, FFmpeg and Config report package presence, not running state.
FFmpeg requires both `ffmpeg.exe` and `ffprobe.exe`.

Module navigation changes only the visible page. It does not start a
module, attach a Hook, change settings, or certify capture/game readiness.
Existing Open Overlay and Settings controls retain their original RPCs.
Older hosts without module inventory display an unknown status.

## Command Center UI — 2026-10-24

Replaced the previous single-page hero/services/features layout with a
CEF 73-compatible NVIDIA-inspired **five-view sidebar UI**:
Overview, Capture, Graphics, Services, Settings.

- Files: `ui/index.html`, `ui/app.css`, `ui/app.js`, `ui/i18n.js`.
- Overview: ShadowPlay NEXT hero, existing `LAUNCHER_OPEN_OVERLAY` action,
  live *Launcher* service indicator, and navigation cards. Status explicitly
  does **not** claim game injection or recording readiness.
- Capture and Graphics are informational navigation views for now, not
  fabricated native capture/GPU controls. Capture's Open Overlay reuses the
  existing command instead of adding a new backend contract.
- Services retains the supervisor's four status dots and actual dynamic
  `RUNNING/STOPPED/LOADING` fields.
- Settings retains the exact `LAUNCHER_SET_OVERLAY` and
  `LAUNCHER_SET_ENGINE_OVERLAY` RPC switch IDs and behavior.
- All new UI labels have English and Thai translations; 28 selectable OSC
  locale codes remain supported with English fallback for new strings.
- CEF 73 safe CSS: native square edges, grid-gap rather than flex-gap,
  supported :focus selectors, MiSansThai, and GeForce display fonts.
- Regression: `node --test Project/Launcher.Cef/tests/command-center-ui.test.cjs`
  4/4 pass; existing Launcher localization 7/7 and OSC sync 2/2 pass.
- Deployed through `deploy-launcher.ps1 -NoBuild -RestartLauncher`,
  without changing any shared CEF, Capture, OSC, Container or GPU binaries.
  Deployment artifact hashes were verified and actual Launcher CEF rendered
  the new page at 1152x648, with HTTP 200.

## Launcher-only maintenance — 2026-10-24

The deployed product layout is now `Build/NVIDIA ShadowPlay/NVIDIA Launcher/Cef`,
not the older `NvLauncher/Cef` name in historical examples below. Keep
configuration under `NVIDIA Config` and DO NOT use the all-project build
script for Launcher-only updates.

- `deploy-launcher.ps1 -NoBuild -RestartLauncher` stages only the root
  Launcher.exe, Launcher.dll and `NVIDIA Launcher/Cef/Resources/launcher`
  assets. It first closes ONLY the exact-root Launcher browser gracefully,
  makes timestamped backups under `Build/DeploymentBackups/launcher-*`,
  replaces changed artifacts with SHA-256 verification and restarts Launcher.
  If the Launcher refuses to close, deployment aborts instead of forcing it.
  Without `-NoBuild`, `CEF_ROOT` is required and only the two native
  Launcher projects are compiled. `-Clean` is rejected.
- `tests/run-shutdown-policy-tests.ps1`: native isolated fake-process
  EXIT ALL regression (seven scenarios and owned-path manifest) plus HTTP
  request policy regression (eleven cases), without touching live processes.
- EXIT ALL now requires successful Overlay-disabled config persistence,
  exact executable-name/full-path identity, and a bounded 1.5-second
  graceful UI WM_CLOSE interval before a same-handle force fallback.
  Headless children lacking a polite service protocol still use the
  bounded fallback: actual recording finalization is NOT guaranteed.
  Production EXIT ALL while recording remains a separate live acceptance
  gate. The procedure was not executed against the active runtime.
- The Launcher loopback UI server now checks same-port localhost Host
  headers, bounds HTTP headers to 16 KiB, uses read/send timeouts,
  caps concurrency at 16 requests, and waits for workers on shutdown.
  Its CEF bridge accepts privileged queries only from the trusted
  Launcher-served main frame; `--url` overrides have no privileged RPC.
- Status for NodeAPI and OSC combines exact-path service presence with a
  bounded HTTP GET / returning 200. This indicates HTTP UI availability,
  NOT confirmed game Hook rendering, recording or shader readiness.
- CEF 73 remains the pinned upstream runtime, built with `no_sandbox=1`.
  Origin gating mitigates one direct privileged-RPC path, but DOES NOT make
  this outdated CEF secure against malicious web content. A separate
  coordinated CEF/OSC upgrade and sandbox enablement plan is required.

2026-10-24 evidence: MSVC Release x64 Launcher.dll/Launcher.exe PASS
without new warnings; isolated shutdown and HTTP tests PASS; existing
Launcher localization 7/7 and OSC language sync 2/2 PASS. Safe deployment
reported LAUNCHER_STAGE_PASS, and the running UI returned HTTP 200 on
`/index.html`; foreign Host rejected 400, POST rejected 405.
Source/live hashes matched after staging. No other service was restarted
or killed, no config file was modified by the deployment process.


UI style (owner call 2026-09-25 "ขอแนว Web Github"): mirrors the
github.io site design language — sharp 90° corners everywhere, corner
brackets + runway + sheen on the SERVICES banner (OBT3-banner grammar),
GeForce-Bold-Alt display font + Inter, animated gradient subtitle,
feature-card hover grammar, green-on-black palette from
`Web Github\CSS\index\style.css` (#76B900 / #0A0A0A / border white 10%).

The NEW Launcher replaces the WinForm `Launcher.exe` (VB,
`Project\Launcher.exe\`) with a native CEF host and a GFE-styled web UI.
Launcher and NVIDIA OSC use one shared CEF 73 runtime directory. The
Launcher keeps its own host DLL, UI bundle, and browser profile, while
loading CEF runtime binaries/resources from the OSC runtime slot:

```
<NVIDIA ShadowPlay>\                     (product root)
├── Launcher.exe                         ← thin bootstrap (root slot)
├── NvLauncher\Cef\
│   ├── Launcher.dll                     ← host body (THIS lane's build)
│   ├── Resources\launcher\              ← the launcher web UI bundle
│   └── Data\                            ← Launcher-only browser profile
└── Overlay OSC\NVIDIA OSC\
    ├── NVIDIA OSC.exe                   ← OSC host
    ├── libcef.dll + cef.pak + locales\  ← single shared CEF 73 runtime
    └── swiftshader\                     ← shared CEF software-rendering assets
```

The bootstrap resolves `NvLauncher\Cef\Launcher.dll` by ABSOLUTE path (CWD
independent), sets the DLL search directory to
`Overlay OSC\NVIDIA OSC` so the body's `libcef.dll` import resolves from the
same runtime as OSC, and calls the `NvLauncherCefMain` export — which
implements the whole CEF process split (subprocess relaunches of the root
Launcher.exe re-enter the same export). CEF resources and locales also load
from that shared OSC runtime directory; the Launcher cache and user data
remain under `NvLauncher\Cef\Data`.

## Build + stage

```
$env:CEF_ROOT = '<CEF 73 SDK directory>'
powershell -File Scripts\build-dev.ps1
powershell -File Project\Launcher.Cef\deploy-launcher.ps1 -NoBuild
```

The deploy script is a compatibility wrapper for the canonical Project-only
Build Tree script. It does not stop running processes; `-NoBuild` stages
existing Project outputs and `-Dest <path>` selects an alternate output root.
Set `CEF_ROOT` to the CEF 73 SDK directory. MSBuild is discovered through
Visual Studio Build Tools or the `MSBUILD_EXE` environment variable.
The OSC lane's CEF 73 wrapper library must be built at
`Project\Overlay OSC\NVIDIA OSC\obj\wrapper73\libcef_dll_wrapper73.lib`;
the launcher links that same wrapper instead of rebuilding a second copy.
The build stages CEF DLLs/resources/locales once into
`Overlay OSC\NVIDIA OSC`; Launcher does not keep a duplicate CEF runtime.

## UI ↔ host contract

Page origin: loopback HTTP (ephemeral port) serving `Resources\launcher`
(production model parity: never file://). RPC = the osc page contract
`window.cefQuery` / `window.cefQueryCancel`, command namespace
`LAUNCHER_*` (no overlap with the osc `QUERY_*` namespace):

The UI names services by role rather than exposing the mixed legacy
executable prefixes (`Nv*`, `NVIDIA*`) as labels:

| UI label | Product executable / role |
|---|---|
| CONTROLLER | `NvContainer\NvContainer.exe` — supervises product services |
| PRESENTER | `Overlay OSC\NVIDIA OSC\NVIDIA OSC.exe` — renders the CEF overlay |
| API SERVICE | Active local API lane; CEF uses `Overlay OSC\NVIDIA NodeAPI\NVIDIA Web Helper.exe` on :59011, while the WinForm hub is a separate executable |

These image names are retained where configuration and process ownership
depend on them. In particular, the bundled managed Web Helper and the
genuine NVIDIA helper under `NvNode\` are different binaries despite sharing
a display filename; the genuine NVIDIA files are not renamed or replaced.

The two feature-card controls are independent: **NVIDIA ShadowPlay** turns
the selected overlay system and its service family on/off via
`Overlay.UseOverlayEnabled`; **OVERLAY ENGINE** only selects WINFORM or CEF
via `Overlay.EngineOverlayMode`. Selecting an engine never powers the
overlay or shows its presenter. **OPEN OVERLAY** explicitly shows the
presenter while the system is enabled.

| command | payload | effect |
|---|---|---|
| `LAUNCHER_GET_STATE` | — | state snapshot JSON |
| `LAUNCHER_SET_OVERLAY` | `value:bool` | config `Overlay.UseOverlayEnabled`; powers the selected engine's complete overlay service family on/off |
| `LAUNCHER_SET_ENGINE_OVERLAY` | `value:bool` | **Overlay Mode (WINFORM ⟷ CEF)** — config `Overlay.EngineOverlayMode`; CEF=true waits for NvContainer's API/OSC services, CEF=false hides the managed OSC surface |
| `LAUNCHER_OPEN_OVERLAY` | — | CEF `/show` → :59013, or WINFORM hub frame `[Send] NVIDIA  APP\|open_overlay` → :5001 |
| `LAUNCHER_OPEN_OBT3` | — | opens the OBT3 page (legacy banner link) |
| `LAUNCHER_DRAG` | — | borderless-window drag (WM_NCLBUTTONDOWN/HTCAPTION) |
| `LAUNCHER_MINIMIZE` | — | SW_MINIMIZE |
| `LAUNCHER_CLOSE` | — | close the window; processes keep running |
| `LAUNCHER_EXIT_ALL` | — | `UseOverlayEnabled=false` + kill family + close (old "Installer Mode") |

State pushes: the supervisor polls every 1s (Main.vb `IF_APP` parity) and
pushes through the augmentation dispatcher
`window.__LauncherState(<json>)` (`launcher_script.h`); the page also
pulls `LAUNCHER_GET_STATE` every 2s as a fallback.

## Supervision contract (port of Main.vb)

- Base chain ALWAYS: `NvContainer\NvContainer.exe`. The WinForm TCP hub is
  supervised only when the WinForm engine is selected; CEF mode uses the
  managed Web Helper API instead and does not require `NvBackend.exe`.
- CEF overlay service family is active only when both
  `Overlay.UseOverlayEnabled` and `Overlay.EngineOverlayMode` are true.
  NvContainer starts or stops the managed Web Helper API, OSC presenter, and
  hotkey helper as those settings change; the controller itself remains up.
- With the CEF engine selected and overlay power ON:
  NvContainer owns the managed
  `Overlay OSC\NVIDIA NodeAPI\NVIDIA Web Helper.exe` API on
  :59011 and `Overlay OSC\NVIDIA OSC\NVIDIA OSC.exe` presenter on :59013.
  Launcher waits for services when power is enabled and sends `/show` or
  `/hide` only for explicit show/hide actions; it never starts the genuine
  `NVIDIA Share.exe`.
- The genuine NVIDIA helper in `NvNode\` is left intact; it hardcodes the
  installed NVIDIA backend path and cannot host this product's portable API.
  The Project NodeAPI and its managed Web Helper are staged together under
  `Overlay OSC\NVIDIA NodeAPI`, independently from the WinForm TCP hub.
- 1s status: API availability / active-engine overlay (+`Flags\Ready` →
  OVERLAY API "LOADING") / NVIDIA Notifier; stale `Flags\Ready` removed
  when the WinForm notifier lane is down (the flag is not consulted in CEF
  mode, where the OSC/API readiness pair is authoritative).
- Config writes are read-modify-write on the CURRENT file
  (`launcher_json.h` order-preserving serializer; atomic tmp + `.bak`
  swap) — the AppConfigShared.vb contract.
- `NvLauncher\Cef\` holds the root Launcher's host DLL, UI bundle, and user
  data. Both Launcher and OSC load the CEF runtime from
  `Overlay OSC\NVIDIA OSC\`.
- The shared overlay config is `<root>\NvConfig\config.json`, alongside the
  controller and OSC service configuration.
- Status dots and lane chips reflect process/config state; switches are
  keyboard accessible and failed actions are reported in the footer.
- EXIT ALL stops only matching product executables by full image path, so
  same-named NVIDIA applications outside this product remain untouched.

## Switches (smoke/proof runs)

`--no-supervise` (no process starts) · `--self-exit-ms=N` (watchdog) ·
`--hidden` · `--width=W --height=H` · `--ui-root=<dir>` · `--url=<url>`.
Log: `<root>\Logs\launcher-cef.log` (+ CEF debug log beside it).

## Smoke evidence (2026-09-25)

- 3× Launcher.exe (browser + subprocesses), page `httpStatus=200`,
  `BRIDGE LIVE`, state dots rendered, watchdog close clean.
- LIVE owner interaction: ENGINE OVERLAY ON → managed chain readiness
  (NvContainer, Web Helper :59011, NVIDIA OSC :59013);
  toggle writes correct; EXIT ALL killed the family and closed cleanly.
- Pixel check of the captured window: titlebar/footer `#1A1B1D`, cards
  `#1E2023`, NVIDIA eye `#76B900` — theme renders as designed.
- Family-name parity fixes proven in the supervised run: the WinForm hub starts
  as `NvBackend\NvBackend.exe` (staged name; legacy "NVIDIA Backend"
  kept as candidate + status alias + kill-list entry), Toolhelp32
  process matching accepts the `.exe` suffix (`adopt running: NvContainer`
  — no duplicate/startup loop).

## CEF 73 CSS constraints (learned the hard way)

The pinned CEF 73 = Chromium 73 (2019). The page must avoid:
- **flexbox `gap`** (Chrome 84+) — every flex row spaces children with
  `> * + * { margin-left: ... }` instead; grid `gap` is fine (66+).
- **`inset` shorthand** (Chrome 87+) — use top/right/bottom/left.
- `clamp()/min()/max()`, `:is()/:where()`, `aspect-ratio` — not used.
Also: JS must not overwrite className strings that drifted from the
stylesheet (the lane chips lost `.lane-chip` when render() still wrote
`.meta-chip` — the run-together "ENGINE LANECEF LANEAPI HUB" bug).

## Known notes

- EXIT ALL (owner-pressed during live testing) rewrote
  `NvConfig\config.json` `Overlay.UseOverlayEnabled=false` — the
  documented RadioButton2 contract. Re-enable via the OVERLAY ENABLED
  toggle.
- The WinForm launcher project (`Project\Launcher.exe\`) is untouched —
  rollback = restage its bin\ output to the root (or run
  `Scripts\build-dev.ps1` with the Launcher.Cef bin removed).

## Launcher UI languages (2026-10-22)

Launcher has its own language control in the title bar: **English (en-US) first**,
or **ไทย (th-TH)**. Localization is intentionally separate from
`Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/l10n` because Launcher is
plain CEF JavaScript rather than OSC Angular Translate. The shared locale codes
and translation style are aligned, but selecting a Launcher language does not
change the OSC language or the service configuration.

- UI owner: `ui/i18n.js` (language dictionary and DOM translation).
- Switch: `ui/index.html` `#launcherLanguage`; `ui/app.js` renders dynamic
  status labels, service messages, and button feedback through the same map.
- English is the default and fallback. The language selection is stored in a
  namespaced host cookie (cross-port persistence for the Launcher's ephemeral
  loopback HTTP server) and also in localStorage as a same-origin fallback.
  Neither mechanism sends a command to the overlay backend.
- Thai font: `ui/fonts/MiSansThai.ttf` copied from the owner-supplied
  `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/MiSansThai.ttf`.
  The @font-face is used when the document language is Thai, while English
  GeForce branding stays unchanged. No font is fetched from the internet.
- Compatible with CEF 73: no JS optional chaining or unsupported flex gap.
- UI asset-only stage: copy `index.html`, `app.js`, `app.css`, `i18n.js`
  and `fonts/MiSansThai.ttf` into
  `build/NVIDIA ShadowPlay/NVIDIA Launcher/Cef/Resources/launcher/`.
  No native C++ rebuild is required for these UI-only changes.

Validation: `node --test "build_test/Launcher Localization_Test/launcher-i18n.test.cjs"`.
The tests cover English default, live status translation, Thai dropdown, safe
switching without service RPC, cross-port cookie persistence, invalid locale
fallback, and static translation coverage. Full CEF visual testing is separate
from the simulated DOM test.

## Launcher → OSC language synchronization (2026-10-22)

- The Launcher language selector now includes all **28 locale codes** found in
  `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/l10n`.
- `ui/osc-locales.js` is generated by
  `build_test/Launcher Localization_Test/generate-osc-locales.cjs` and
  reuses existing OSC names and semantically matching translated phrases.
  Launcher-specific strings without translations fall back to English;
  **28 selectable languages does not mean every Launcher phrase is translated**.
  The curated English and Thai Launcher dictionaries remain unchanged.
- When Launcher starts and when its language changes, `ui/app.js` sends
  `LAUNCHER_SET_LANGUAGE` to the native query handler. The handler validates
  the locale code against the bundled OSC `l10n/<locale>.json` file and
  atomically writes the local language state into
  `build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/launcher-language.json`.
  The static OSC service makes this available at
  `http://localhost:59011/mods/launcher-language.json`; no CORS or cookie sharing.
- `osc/mods/launcher-language-sync.js` is registered from `osc/index.html`
  and checks the state (initial 1.5 seconds, then every 3 seconds). OSC's
  existing `$translate.use(locale)` switches the on-screen language.
  If the file/API is missing or the locale is invalid, OSC leaves its
  current language intact. Neither game processes nor overlay controls
  are touched.
- This is **Launcher → OSC** synchronization; changing the language inside OSC
  does not overwrite Launcher preference.
- Native `Launcher.dll` was built Release x64 and staged into the product
  after closing/reopening only Launcher, then checked by SHA-256.
  The running OSC CEF was reloaded when at `#/base` and the active
  `$translate.use()` returned `th-TH` while the published Launcher choice
  was `{"language":"th-TH"}`.
- Tests: `node --test "build_test/Launcher Localization_Test/launcher-i18n.test.cjs"`
  and `node --test tests/launcherLanguageSync.test.js` in NodeAPI.
