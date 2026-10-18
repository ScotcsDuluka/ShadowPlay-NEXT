# Backend — NVIDIA ShadowPlay Portable (clean-room Web Helper replacement)

Node.js backend that replaces `NVIDIA Web Helper.exe` (NvNode). One process
serves the osc overlay frontend (same-origin) + the full REST/socket surface
the page talks to, on the REAL Web Helper port:

```
http://127.0.0.1:59011        (NvContainer nodeApiPort and OSC backend port)
```

## Layout

```
Backend/
├── package.json          express ^4 + socket.io 2.5.1 (v2 — the page ships
│                         socket.io-client 2.5.0, EIO=3 wire; v3/v4 NEVER works)
├── index.js              entry: boot chain, CORS *, lenient JSON body reader,
│                         static osc/, route mounting, 404/error mirrors, listen
├── config.js             env > config.json > defaults (port/oscDir/dulukaServer/...)
├── socket.js             socket.io v2 push channel: connection logging,
│                         WindowState emit, hotkey edge debounce (500ms)
├── routes/
│   ├── shadowplay.js     /ShadowPlay/v.1.0/* state store → data/state.json
│   │                     (+ /SDK/v.1.0, /FramerateLimiter/v.0.1, OSC lifecycle)
│   ├── hardware.js       GET /HardwareInformation/v.0.1+v.0.2 (machine floor)
│   │                     + v.1.0//SystemInfo 404 mirrors
│   ├── customize.js      POST /ShadowPlay/v1.0/OSC/GetCustomize/<Name> ×3
│   │                     (v1.0 + v.1.0 spellings, 4-field validation, slider
│   │                     bounds) + Resolutions/FrameRates/BitRates
│   ├── duluka.js         /Duluka/v.1.0/* + /DulukaCapture/v.1.0/* +
│   │                     /Account/v.1.0/* + /PiplConfig/v.1.0/data
│   │                     (jarvis.server → Duluka Server :5115) + boot misc
│   └── debug.js          POST /ShadowPlay/v.1.0/Debug/PageLog (+ /Debug/PageLog
│                         alias), /beta, /gfe/bp, /Backend/v.1.0/health
├── lib/
│   ├── defaults.js       default responses: real-floor captures + DulukaFloor
│   │                     constants (slider bounds, hotkey table, HW shape)
│   ├── floor-defaults.json  merged snapshot of docs/osc/real-floor/*.json
│   ├── store.js          sectioned JSON persistence (atomic tmp+rename)
│   ├── hardwareProbe.js  boot-time WMI probe → data/hardware-floor.json
│   └── logger.js         console + data/logs/backend.log
└── data/                 runtime state (generated) — see data/README.md
```

## Run

```
cd Backend
npm install
npm start            # node index.js — listens http://127.0.0.1:59011
```

Environment overrides: `NVSP_PORT`, `NVSP_HOST`, `OSC_DIR`, `DULUKA_SERVER`,
`NVSP_SECURITY_CHECK=1`, `HARDWARE_REPROBE=1`, `NVSP_LOG_LEVEL`.

Health probe: `GET /Backend/v.1.0/health`.

## Provenance (why the shapes are what they are)

- `lib/floor-defaults.json` — REAL backend response captures, 46 endpoints
  (commit 01b6a02, proven live on the Intel floor 2026-09-19/22). Every
  default response mirrors these byte-shapes, including the deliberate
  500s (`InstantReplay/BufferLength`, `Record/Concurrency/Manual`) and 404s
  (`HardwareInformation/v.1.0/*`, `Localization/...`) — the page takes its
  normal boot/error path through them.
- `socket.js` hotkey edge debounce — the WM_HOTKEY auto-repeat flicker fix
  (DulukaFloor.js, proven): 500ms cooldown per hotkey, first POST wins.
- `Resolutions` returns a plain string array — object arrays were the root
  cause of stuck `initInProgress` (a263b4d).
- `GetCustomize` is POST `/ShadowPlay/v1.0/OSC/GetCustomize/<Name>` (v1.0,
  no dot) AND the v.1.0 spelling; body MUST carry
  quality+resolution+framerate+bitrateBps or the backend 500s with
  `Argument doesn't have '<field>' property` (native contract).
- PiplConfig `jarvis.server` points at the Duluka Server (`DULUKA_SERVER`,
  default `http://127.0.0.1:5115`) — the account provider backend.

## Alt+Z wire-through

`hotkey-listener.ps1` owns Alt+Z via RegisterHotKey (driverless machines)
and POSTs `/ShadowPlay/v.1.0/Hotkey/Toggle` → this backend emits
`/ShadowPlay/v.1.0/WindowState {windowMsg:"overlayToggle"}` to every
connected osc page (docs/osc/11-window-flow.md contract).

## Deployment

`deploy/install.ps1` installs the canonical staged owner tree produced by
`scripts/layout.proj` + `deploy/deploy-shadowplay-layout.ps1`:

```
C:\Program Files\NVIDIA Corporation\NVIDIA GeForce Experience\
├── Launcher.exe
├── NvContainer\
├── NvOverlay\WinForm\
├── NvOverlay\CEF\
├── NvCapture\
├── NvBackend\
├── NvAudio\
├── NvGraphics\
├── Runtime\
├── FFmpeg\
├── NvConfig\
├── Data\
├── Languages\
├── Resources\
├── .NET Deployment\
└── Logs\
```

The historical 4-instance installer is preserved under
`deploy/legacy/install-gfe-4instance.ps1`.
