# Phase 8 — One primary Capture display for Record / Replay / Screenshot

Date: 2026-10-24. Project: `C:\My Project\NVIDIA-Shadowplay`.
Architecture: **NvCapture owns screen selection and capture; Container communicates; NodeAPI relays; OSC displays truth.**

## Implemented in NvCapture source

- **Read-only display inventory:** `GET /displays` enumerates IDXGIFactory1 → the first NVIDIA adapter → outputs that are actually `AttachedToDesktop`. This reads only DXGI metadata and never calls `DuplicateOutput` or reads pixels. Each display includes the actual adapter LUID (high/low), DXGI output index, dimensions, desktop origin and primary flag. `selected` is the current persisted selection or the backward-compatible first attached output.
- **Persist selection:** `POST /display/select` requires `confirmed:true`, an exact `outputIndex` and two-part `adapterLuid`. It rejects recording/busy state, missing/invalid displays and a different GPU, then atomically saves `Config/capture-display.json`. Monitor unplug / wrong LUID is **fail-closed**: no silent fallback to a different screen.
- **Native Record:** `DdagrabBackend.RequestedOutputIndex` now selects a specific connected DXGI output, while -1 preserves previous first-attached behavior. `RecordingEngineHost` loads the same persisted selection at **engine Initialize or Rebuild**. A selection changed while the engine stays initialized is NOT effective until rebuild/restart. The API reports `appliesOnEngineRebuild:true`, and POST reports `effectiveNow:false`; no false promise of hot-swapping the actual capture display.
- **FFmpeg Record:** Legacy argument builder reads that same persisted selection before each launch: `ddagrab=output_idx=N`, `gdigrab` offsets + size. For `gfxcapture`, monitor numbering has NOT been proven to align with DXGI; an explicitly selected display is rejected instead of possibly recording a different display.
- **One-frame Screenshot:** `POST /screenshot` requires `confirmed:true`, refuses active Record or engine rebuild and selects the same saved display. It invokes bundled FFmpeg's DXGI `ddagrab` with `-frames:v 1`, hardware frame download and PNG encoder — **no NVENC or audio**. Has a 10-second timeout, kills only its owned subprocess on timeout, writes a private temp file under user's Pictures/ShadowPlay NEXT/Screenshots, checks PNG signature/IHDR/IEND and atomically moves to a finished filename. Explicit errors for unavailable display, missing binary, DXGI failure, invalid/empty PNG or timeout.
- **Exclusive fullscreen:** DXGI is attempted, but can fail when a game/display changes modes or protected content denies duplication. There is no claim that DXGI always succeeds. Game Hook fallback is **not implemented yet**.

NvCapture's runtime REST endpoints `/displays`, `/display/select` and `/screenshot` are **source additions only**, not deployed to the existing running worker. The running worker was neither restarted nor asked to screenshot the user's actual desktop.

### NvCapture wire contract

```http
GET /displays
```
Response HTTP 200 JSON (example shapes):
```json
{"schemaVersion":1,"readOnly":true,"source":"NvCapture",
 "displays":[{"OutputIndex":0,"AdapterLuidLow":123,"AdapterLuidHigh":7,
 "Width":1680,"Height":1050,"Left":0,"Top":0,"Primary":true,
 "Label":"Display 1 - 1680x1050 (Primary)"}],
 "selected":{"OutputIndex":0,"AdapterLuidLow":123,"AdapterLuidHigh":7},
 "selectionValid":true,"error":"","recording":false,
 "appliesOnEngineRebuild":true}
```

```http
POST /display/select
Content-Type: application/json

{"confirmed":true,"outputIndex":0,"adapterLuidLow":123,"adapterLuidHigh":7}
```
Response: `{"saved":true,"selected":{...},"appliesOnEngineRebuild":true,"effectiveNow":false}`; or `{"saved":false,"error":"capture_busy|capture_display_invalid"}`. `saved` is a property of NvCapture, not a claim that a GPU already changed its active output.

```http
POST /screenshot
Content-Type: application/json

{"confirmed":true}
```
Response: `{"Saved":true,"File":"...png","ErrorCode":"","DisplayIndex":0}` or `{"Saved":false,"ErrorCode":"..."}`. The **worker's result fields here are PascalCase**; Container should normalize or document its response shape and not mistake a 200 transport status for a successful screenshot.

## REQUIRED NVIDIA Container handoff (owned by another agent)

The existing 2026-10-24 Container gateway has **NOT implemented** display/screenshot forwarding and deliberately returns 501 for screenshot. Add these new routes **without touching the recording pipeline**:

| Container gateway | Forward to NvCapture |
| --- | --- |
| `GET /v1/capture/displays` | `GET /displays` |
| `POST /v1/capture/display/select` | `POST /display/select` |
| `POST /v1/capture/screenshot` | `POST /screenshot` |

Validate schema, exact LUID/index, captured worker `/health` idle, strict `confirmed:true`, and trusted local caller/authentication before enabling writes. Return `Unavailable` / 501 and **do not invent displays** until an updated worker is installed. Distinguish screenshot result `Saved` from transport HTTP 200. Match NodeAPI's normalized `saved:boolean` contract (lowercase) or explicitly coordinate the normalization.

NodeAPI shim `shims/capture-display-routing.js` is wired via the existing `capture-engine-status.js` registration point. It exposes:
- `GET /DulukaCapture/v.1.0/displays`
- `POST /DulukaCapture/v.1.0/display/select`
- `POST /DulukaCapture/v.1.0/screenshot`

OSC `osc/mods/video-capture-options.js` adds a native-styled **Primary Capture display** `md-select`, loading its choices from NodeAPI and saving the exact LUID+DXGI output pair. It is **disabled/unavailable** until the Container gateway and compatible worker answer with valid data. On success it shows **restart/rebuild required**, not immediate GPU changes. No fake monitors are shown.

## Replay truth

Legacy `UI_Engine.HandleEngineReplayStart/Stop/Save` are explicitly `not_implemented`; there is no verified Replay capture engine or native REST route. The selected monitor's identity can be reused when Replay is implemented, **but Replay does not become enabled or ready just because this display selector exists**. Container's existing 501 Replay status must stay unchanged.

## Evidence and limitations

- **16/16** synthetic/display selection tests passed; test uses isolated temp config, synthetic PNG-like bytes and pure FFmpeg argument construction. No desktop screenshot or audio device opened.
- **15/15** RecordingEngine offline Recovery tests passed, no actual GPU initialization.
- **24/24** NodeAPI + OSC UI/capture catalog/Freestyle tests passed using mocked Container HTTP and simulated Angular state.
- Main `NVIDIA Capture.vbproj` Release builds with **0 Errors** and **3 pre-existing Warnings**. Replaced the stale compiled Recording DLL reference with a `ProjectReference` to the real Recording project to compile the new monitor selection correctly. Production worker is unaffected until a coordinated deployment.
- No real DDA Screenshot/PNG save, full exclusive-screen game, actual OS display switch, running CEF rendering, or deployed Container end-to-end relay validated. The output PNG integrity check is **structural**, not a complete PNG CRC/decode audit.

## Safe scope

Other agents have uncommitted `Project/NVIDIA Container/*` and several OSC Host/Hook files. They were not changed, reset or staged here. Do not push the shared Stable branch together with their unpublished work.
