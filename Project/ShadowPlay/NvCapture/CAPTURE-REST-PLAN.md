# Engine capture — nvsphelper64 role, REST wiring plan

Status: PLANNING (this doc sets the contract; capture wiring lands in
CaptureEngine branches). Read together with `README-REST-INTEGRATION.md`
(the REST client that already exists) and `docs/GFE-PROCESS-MAP.md`.

## Role

The real GFE host runs `nvsphelper64.exe` as the capture helper: it owns
the encoder pipeline, reacts to ShadowPlay state, and talks to the Web
Helper service. Our Engine takes that identity (AssemblyName
`nvsphelper64`) and replaces the private TCP :5001 engine channel with
the NEW API on :59001 — the same surface the osc page uses.

## What already exists

- `Engine/Rest/ShadowPlayRestClient.vb` — dependency-free REST client:
  polls `/Record/Enable` edges (the API the osc page drives), publishes
  `/Record/Running` + `/RecordPaths`, waits for the backend.
- `Engine/NVIDIA Capture.vbproj` — builds (`NVIDIA Capture.exe`); the
  capture internals live on the `CaptureEngine` branches (NVENC pipeline,
  WGC/dxgi grabber, audio mixer, gallery writer).

## Wiring contract (REST-only, no legacy TCP)

```
Engine boot ──► WaitForBackend :59001 (/Backend/v.1.0/health, X_LOCAL_SECURITY_COOKIE)
loop ────────► GET /Record/Enable      (poll 500 ms; edge detect)
  edge true ─► start capture pipeline
             ► POST /Record/Running {"running":true}   (+ /RecordPaths on start)
  edge false► stop pipeline, finalize file
             ► POST /Record/Running {"running":false}
```

Auth: every request carries the pinned `X_LOCAL_SECURITY_COOKIE`
(header or query), same as the osc page and the Coordinator health
checks. Backend down = Engine idles and re-polls; never crashes.

## Build phases (CaptureEngine branches)

1. **Wire-through pass** — Engine process boots, REST client live,
   state machine reacts to Enable edges (no pixels yet): log-only
   RecordStart/RecordStop events prove the loop.
2. **Frame grabber** — WGC/dxgi desktop duplication into the existing
   frame queue (reuse the W1 forensics pipeline; no new capture code).
3. **Encoder** — NVENC session via the P1-F contract docs; bitrate/
   framerate from `/GetCustomize` bounds already captured in
   `hardware-floor.json`.
4. **Audio + mux** — NAudio loopback mix (already a dependency), ffmpeg
   mux from `API-Core` binaries (FFmpegLocator.vb).
5. **Gallery handoff** — write into the Gallery media store paths and
   publish `/RecordPaths`; Notifier already balloons on Running edges.

## Explicit non-goals for this slot

- No hooking/injection — that is Share #4 (`Product/Hook`), reserved.
- No overlay UI — that is #2/#3.
- No autostart of its own — the Coordinator adopts/spawns it later the
  same way it spawns the other children (path + `--parent-pid`).
