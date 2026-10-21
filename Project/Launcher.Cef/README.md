# Launcher.Cef — CEF launcher lane (root Launcher.exe + NvLauncher\Cef\Launcher.dll)

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
