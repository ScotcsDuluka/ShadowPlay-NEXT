# NVIDIA Capture — FFmpeg NVENC Synthetic Verification (2026-10-24)

## Scope and ownership

Owner: NVIDIA Capture. No Container/NodeAPI/OSC source files were changed.
Branch: Stable. No commit or push was made. Preserve concurrent uncommitted changes.

## Implementation

- `src/[API]/FFmpegNvencVerification.vb` — explicit rate-limited synthetic encode test. Uses FFmpeg's lavfi black-color input (128×128, one frame) and null muxer, not screen input or a video file.
- `src/[API]/CaptureRestApiServer.vb` — adds `POST /encoders/verify` to trigger the test when not recording; additive `GET /engines.ffmpegBinary.nvencSyntheticValidation`. GET never starts the test.
- `tests/verify-nvenc-runtime.ps1` — 19 live contract checks (Capture, Container, NodeAPI, and status honesty).

## Runtime result

- NvCapture: `http://127.0.0.1:59077`
- Container: `http://127.0.0.1:59012/v1/capture/engines`
- NodeAPI: `http://127.0.0.1:59011/DulukaCapture/v.1.0/engines`
- Two NVIDIA GTX 1080 Ti GPUs, NVIDIA driver 582.78.
- Per-GPU one-frame synthetic encode: h264_nvenc PASS, hevc_nvenc PASS, av1_nvenc FAIL (both GPUs).
- 9 FFmpeg-declared encoder names; listing does NOT prove hardware support.
- `realRecordingVerified=false` for every result. No actual Record / Replay / Screenshot test was performed.
- JSON `nvencSyntheticValidation.status`: `not_run`, `running`, `complete`, or `error`. The GPU verification is process-local and resets on restart; do not treat `not_run` as failure.
- `hardwareEncoderVerified` at the generic FFmpeg level remains false (NOT a blanket certification).
- Test results are only applicable to tested GPU ordinal + FFmpeg build + test moment. Do not imply other GPUs, modes, bitrate/presets, formats or real-world recording are verified.

## Verification

- Release Build: latest incremental run succeeded, 0 errors, 0 warnings (earlier clean build reported 3 legacy warnings).
- Routing regression: 13/13 pass.
- Live verification script: 20/20 pass after handling expected async `ffmpeg_probe_pending` during startup.
- Updated `build/Deploy-NvCapture-FFmpeg-20261024.ps1` to respect the NvContainer-owned worker and avoid manually starting a duplicate NvCapture; both deploy and rollback paths now use owner-aware restart.
- Atomic runtime directory-swap deploy and restart succeeded on the corrected script, with rollback armed but not needed.
- Post-deploy owner check: exactly one NvCapture PID 14412; parent NvContainer PID 22960; REST port 59077 owned by 14412.
- Runtime DLL SHA-256: `F1F06D6DDC906AAD0C8FF4C1C437F94070B4B87772C19310A3AA6372F4B2ED32`
- Latest backup: `build/NVIDIA ShadowPlay/NVIDIA Capture.backup-20261024-130555`
- Final post-deploy synthetic verification: 20/20, 2 GPUs. No actual Record/Replay/Screenshot was started.

## Record Idle Truth Repair (2026-10-24)

- Found reproducible mismatch before repair: GET NvCapture /state recording=false and Container /v1/capture/status recording=false, while the NodeAPI legacy GET /ShadowPlay/v.1.0/Record/Running returned running=true and /ShadowPlay/v.1.0/Capture/State returned recordingState=1. NodeAPI desired /Record/Enable was false, /Duluka/v.1.0/State was Idle.
- Semantics clarification: NvCapture /state enabled=false means no active session, whereas NodeAPI /DulukaCapture/v.1.0/status enabled=true means the worker monitoring service is enabled. Those fields are NOT the same concept.
- Fixed only Capture-owned source: `src/[UI]/UI_Engine.vb` now confirms `Actual(false)` to NodeAPI on worker startup, **only** when its backend is reachable and NvCapture is actually idle; this reconciles stale NodeAPI in-memory Record state after NvContainer restarts Capture. NodeAPI/Container sources remain untouched.
- Release build passed (0 errors, 3 pre-existing warnings). Routing regression 13/13 passed. Deployed Capture with Container-owned lifecycle and backup, then independently checked all live statuses.
- Current NvCapture worker PID 15688, parent NvContainer PID 22960; exactly one process.
- All live states now agree on Idle: NvCapture recording=false, Container recording=false, NodeAPI /Record/Running=false, /Capture/State recordingState=0, NodeAPI /Record/Enable=false.
- Added `tests/verify-record-idle-contract.ps1` (read-only 9/9 checks) and reran `tests/verify-nvenc-runtime.ps1` (20/20).
- Runtime NvCapture.dll SHA256: `173B570FBE0B313183DEE85B9C9A500FED0C7E6EDDAA7863E62B0C952588DD1C`. Backup: `build/NVIDIA ShadowPlay/NVIDIA Capture.backup-20261024-133424`.
- **Known limitation:** this is startup reconciliation. The legacy NodeAPI GET /Record/Running still reads NodeAPI-local memory rather than fetching Container's authoritative worker state on every GET. Owner of NodeAPI should design ongoing reconciliation, accounting for backend errors and security, before declaring it fully authoritative.
- **E2E recording not tested.** Container /v1/capture/record/start|stop gateway is opt-in and defaults to disabled; never blindly enable or send start/stop without an explicit controlled recording test scope. Current Config/NVIDIA Config/nvcontainer.json did not specify captureRuntime.allowRecordCommands. Replay/Screenshot worker gateway reports unavailable.

## OSC UI integration completed (2026-10-24)

- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.js` now consumes `workerRuntime.ffmpegBinary.nvencSyntheticValidation` from the existing Container/NodeAPI catalog.
- FFmpeg NVENC codecs that fail synthetic encoding on every fully enumerated GPU remain visible as disabled in NVIDIA `md-select`. Saved unsupported selections are preserved and blocked from POST. Incomplete/not-run evidence remains unverified rather than being assumed supported or unsupported.
- Native Duluka routes remain separate; synthetic encode passes never count as end-to-end recording verification.
- `videoCaptureOptionsUi.test.js`: 21/21; adjacent NodeAPI regression: 19/19.
- Only the Video Settings JS asset was deployed with atomic replacement and backup; served over NodeAPI HTTP as UI version 70. Container-managed OSC restarted successfully (PID 37500). NodeAPI and Container Source not changed.
- Runtime asset SHA-256: `0C8A77A1A823DA6D66C7C10243F54C640AA8758BFD70149C1A1A0BA820A30098`.
- UI backup: `build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/osc/mods/video-capture-options.js.backup-20261024-131319`.

## Previously scoped OSC requirements (implemented)

Prior to the OSC update, `osc/mods/video-capture-options.js` built its `FFmpeg` codec options from `providers[FFmpeg].encoders` (binary *advertisement*) only, which included `av1_nvenc` even though hardware validation failed on both installed GTX 1080 Ti GPUs. This mismatch is now handled by the UI verification layer.

Safely consume `workerRuntime.ffmpegBinary.nvencSyntheticValidation.results` from existing `/DulukaCapture/v.1.0/engines` catalog. Distinguish:
1. Listed in binary (advertised).
2. Synthetic encode PASSED for a specific GPU.
3. Actual recording end-to-end verified (currently false).
4. Current selected route ready to record (not inferred from this test).

If verification is `not_run`, do not silently mark supported/unsupported. When every tested GPU fails an NVENC codec, show unavailable or disabled with its reason while preserving the saved selection visibly. Never silently rewrite config on a read-only UI refresh. Do not assume Intel QSV/AMD AMF support from FFmpeg listings.

Do not modify Nvidia Container shared source or push Stable as part of the Capture work.
