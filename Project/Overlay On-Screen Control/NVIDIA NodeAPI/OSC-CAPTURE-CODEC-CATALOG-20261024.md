# OSC Capture Codec menu — NVIDIA Container catalog wiring

Date: 2026-10-24. Source project: `C:\My Project\NVIDIA-Shadowplay`.
Scope: `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.js` and `.css`, with focused UI tests. No edits to NVIDIA Container source, Capture backend or the concurrently edited OSC Host/Hook.

## User-visible issue and evidence

Screenshot of OSC Codec Encoder dropdown showed a hardcoded mixed list:
- NVIDIA NVENC H.264 / HEVC / AV1
- Intel Quick Sync H.264 / HEVC
- AMD AMF H.264 / HEVC
- Software x264 / x265

The highlighted Intel Quick Sync row was an Angular Material focus/hover presentation, NOT reliable evidence that Intel encoder hardware was selected. The source actually created all nine `encoderChoices` unconditionally, without consulting any actual route or codec runtime.

The LIVE NVIDIA Container read-only endpoint `GET http://127.0.0.1:59012/v1/capture/engines` returned HTTP 200 with:
- `providers[Duluka]`: `routeWired=true`, `encoders=["NVENC_H264"]`, `runtimeReady=false`.
- `providers[FFmpeg]`: `routeWired=true`, `encoders=["h264_nvenc","libx264"]`, `runtimeReady=false`.
- OBS and NVIDIA/NvFBC providers reported `routeWired=false`.
- `policyValid=false`, `policyError="Capture routing policy unavailable: FileNotFoundException"`; `selected.policyOnly=true`.
- Separate production NvCapture `GET /health`: `ready=true, recording=false`.

**Do not interpret a listed encoder or routeWired flag as proof of a successful runtime hardware encoder initialization.** The routing policy was unavailable on this machine during the check, even though the separate worker /health succeeded.

## Fix: only advertise routes, preserve real settings

The OSC Video Settings module now fetches the existing read-only NodeAPI proxy `/DulukaCapture/v.1.0/engines` **in parallel with** `/DulukaCapture/v.1.0/settings`, validates both before unlocking editing, and maps Container identifiers to legacy OSC configuration keys:

| Engine | Container codec | OSC settings value / label |
| --- | --- | --- |
| Duluka | NVENC_H264 | `NVENC_H264` — NVIDIA NVENC H.264 |
| FFmpeg | h264_nvenc | `NVENC_H264` — NVIDIA NVENC H.264 |
| FFmpeg | libx264 | `LibX264` — Software x264 |

The nine-codec list remains an **internal display-name dictionary only**, not the actual selectable menu. `md-select` sees only choices with a matching wired route. Intel/AMD/HEVC/AV1 are not shown as selectable based solely on static names.

A previously saved codec that is not in the selected engine's catalog is **preserved** and shown disabled as `(saved; unsupported for <Engine>)`. Loading does not silently rewrite the saved config. A POST with that unsupported codec is prevented with an explicit on-screen warning; the user can explicitly select a supported codec. When switching engine, an incompatible codec is reset to the first supported choice as a consequence of the **user's explicit engine-change action**, not as a side effect of simply opening Video Settings.

If settings or the Container catalog cannot be fetched/validated, the menu is not populated with guessed encoders and settings remain locked. An existing Reload button retries both requests. A small understated note clarifies these are **route choices, not an assertion of live hardware readiness**, and flags the Container policy not being verified.

Kept the existing NVIDIA Angular `md-select`, styling, dimensions and `ng-disabled` semantics; added only a small descriptive text note. The internal panel version increment ensures the DOM will be rebuilt on a new UI load. No native Host, Hook, CEF GPU transport, NvContainer or Capture code changed.

## Verified regressions

- `node --check osc/mods/video-capture-options.js` and JS test source — PASS.
- `node --test tests/videoCaptureOptionsUi.test.js` — 10/10 PASS (existing UI failure handling plus catalog filtering, missing catalog fail-closed, incompatible saved codec preservation and no invalid POST).
- Combined `node --test tests/videoCaptureOptionsUi.test.js tests/captureEngineStatus.test.js tests/freestyleSettingsUi.test.js tests/nvFreestyleSettings.test.js` — **22/22 PASS**.
- No record/start or record/stop REST calls made. No sound, microphone, screen, movie or game capture. No live services restarted.
- The live NodeAPI/OSC HTTP service did **not respond** on probed ports (59001, 59012, 58001, 58002), and no project NodeAPI process appeared in the read-only process inventory. Accordingly, **this is verified Source/Unit Test behavior, not a confirmed deployed CEF screen rendering**.
- The Container is running at 59012 and was accessed **read-only**. Its unrelated dirty source files were deliberately not edited.

## Remaining gates

1. When the actual NodeAPI + OSC CEF UI is running from updated project files, check visually that Duluka shows only H.264 and FFmpeg shows the two catalog routes, with the disabled previously-saved choice if present.
2. Container capture routing policy missing/invalid remains a separate runtime readiness investigation; do not mask it by pretending `routeWired` means `runtimeReady`.
3. Actual hardware encode/Duluka Capture was intentionally not restarted or tested in this UI-only change. Any real desktop/audio capture requires separately scoped consent.
