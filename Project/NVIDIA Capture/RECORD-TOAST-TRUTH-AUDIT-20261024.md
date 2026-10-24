# ShadowPlay NEXT — Record toast / actual state audit (2026-10-24, 13:48 ICT)

## Trigger
OSC screenshot showed Thai "Recording has been saved" toast while NvCapture actual-state API was idle. Overlay translucency revealed chat underneath; translucency alone is not a recording issue.

## Verified runtime observations (read-only)
- NvCapture GET /health: ready=true, recording=false.
- NvCapture GET /state: actualSessionBackend="", recording=false, outputPath="", lastFile="".
- Container GET /v1/capture/status: runtimeReady=true, recording=false.
- NodeAPI legacy GET /ShadowPlay/v.1.0/Record/Enable: status=false.
- NodeAPI legacy GET /ShadowPlay/v.1.0/Record/Running: running=true (STALE).
- NodeAPI legacy GET /ShadowPlay/v.1.0/Capture/State: recordingState=1 (STALE).
- NodeAPI GET /Duluka/v.1.0/State: recordState=Idle, irState=Idle.
- ui-engine.log: 2026-10-24 13:47:57 RecordStart edge, HandleRecordingStart fired, reports "actual route started: Duluka" and "confirmed actual=Recording". No matching ConfirmRecordStopped in last captured log lines.
- Requested file name: ShadowPlay_2569-10-24_13-47-57.mp4 (Buddhist year in configured timestamp). No corresponding file found beneath the user's Videos directory in a filename-only recursive check, and worker lastFile is empty. This does NOT establish the absence of files elsewhere.

## Source cause candidates, verified directly
- `Project/Overlay On-Screen Control/NVIDIA NodeAPI/shims/route-floor.js`: /Duluka/v.1.0/Actual writes `state.recordRunning` locally and emits /ShadowPlay/v.1.0/Record/Enable with {status: body.running}. The POST /Record/Enable also emits this event on desired-state toggles. Legacy GET /Record/Running returns the cached local state rather than Container's authoritative /v1/capture/status.
- NVIDIA OSC bundled `osc/app.js` notification handler interprets `Record/Enable status=false` as `RECORD_STOPPED`; localized string in `osc/l10n/en-US.json` is "Recording has been saved" (Thai localized equivalent in th-TH). This is NOT conditioned on a verified successful file.
- `Project/NVIDIA Capture/src/[API]/RecordingEngineHost.vb`: WatchSessionEnd watches natural completion/fault, clears host clock, publishes saved/error only via TCP when connected. In headless mode TCP is intentionally skipped; this watcher does not call NodeAPI Actual(false) on natural failure/completion. Only `src/[UI]/UI_Engine.vb` manual StopRestRecording sends ConfirmRecordStopped(). As a result NodeAPI may remain running=true after a completed/faulted session.
- Prior startup-idle reconciliation (UI_Engine.vb, already deployed) fixes stale state only on worker startup, not when a session ends later.

## Important distinctions
1. Desired enabled=false is NOT synonymous with recording stopped successfully.
2. Actual session running=false is NOT synonymous with a saved video file.
3. A successful saved notification requires verified `SessionResult.Pass` and an output file / finalized muxer, not merely a POST or a boolean toggle.
4. The screenshot does not prove the recording succeeded.

## Coordinated fix needed
- **Capture owner:** ensure every session-end path (normal, stop, expiry, error) reconciles actual-state Idle with NodeAPI exactly once, without races against newer session IDs. Preserve failure reason and `SessionResult.Pass`.
- **NodeAPI owner:** source legacy /Record/Running and /Capture/State from authoritative Container worker state (or bounded cache with fail-closed behavior), not a permanently stale boolean.
- **OSC/notification owner:** stop mapping generic `Record/Enable status=false` to a *saved* toast. Emit saved only from verified successful session completion, error toast on failed session, and no saved toast on startup reconciliation.
- **Test plan:** inject simulated SessionResult pass/fail/no-file in unit tests, prove no false saved toast and stale-state self-healing; then perform controlled recording E2E only with scope and screen contents approved by the user. Do not turn on public record gateway casually.

No recording/stop commands were sent during this audit; no source was edited, built, deployed or restarted for this audit.

## Implemented fix and deployment (2026-10-24, 14:15 ICT)

**Completed code changes**

- Capture `src/[API]/RecordingEngineHost.vb`: native session completion watcher reconciles NodeAPI `Actual(false)` on completed/failed/expired headless sessions. Sends a saved indication only when `SessionResult.Pass`, `SessionResult.FileExists`, and an actual output file all agree; errors do not imply saved.
- Capture `src/[Integration]/ShadowPlayRest.vb`: `ConfirmActual(running, Optional savedOutputPath)` serializes `running`, `recordingSaved` and `outputPath`; file-existence check is performed inside the client too.
- Capture `src/[UI]/UI_Engine.vb`: native Stop path defers verified completion to watcher instead of publishing an optimistic `false` first; reconciles race where session fails immediately after confirming Start.
- NodeAPI `shims/record-notification-policy.js`: desired-state toggles emit **no** success notification; actual failure/stop sends only `Record/Running=false`; successful finalized output may emit the legacy `Record/Enable=false` saved notification, deduplicated on state transition.
- NodeAPI `shims/route-floor.js`: minimal integration at `/Duluka/v.1.0/Actual` and `/ShadowPlay/v.1.0/Record/Enable`, preserved preexisting uncommitted status refactor. Existing OSC app.js/l10n untouched.
- New isolated tests: `tests/recordNotificationPolicy.test.js` (6/6) and `tests/recordActualRouteTruth.test.js` (3/3); these do not invoke the live Record API.

**Tests and deploy**

- Capture Release: Build PASS, 0 errors, 3 existing warnings; Capture Routing 13/13.
- NodeAPI Notification + runtime + Video Settings test group: 33/33, and isolated real route integration 9/9.
- Capture Recovery.NoHardware: 15/15; Hardware Readiness dry run: 24/24; live idle contract: 9/9.
- NodeAPI deployed and restarted under Web Helper, resulting `NvNode.exe` PID 13244. Initial deploy attempt rolled back correctly because the supervisor *also* rotated the parent Web Helper PID; fixed the deploy script to validate the new live parent by executable ownership, not stale PID. Second deploy passed.
- NodeAPI backup: `build/NVIDIA ShadowPlay/Overlay On-Screen Control/NVIDIA NodeAPI/shims/route-floor.js.backup-20261024-141442`.
- Capture atomic staged deploy and restart succeeded with backup `build/NVIDIA ShadowPlay/NVIDIA Capture.backup-20261024-141502`. Resulting NvCapture PID 15044 (NvContainer PID 22960).
- Container-owned NVIDIA OSC restarted to reload UI/event subscriptions; resulting main OSC PID 28112.
- Deployed hashes: NvCapture.dll `DE04FDBB1936B6A45FA1770574D0F574AD9F1C3AA03C12826FB6860EF2E1CE76`; NodeAPI route-floor.js `8CE171CF6EEEE702F8AC999112F94CE7DE07052203593007A857286D5007B078`; policy shim `6F4A8C75DC7F10D6D52370823D2447E40BD6BD70C113BF67B00CEE9A4566278C`.
- Verified runtime GET-only: Capture /state recording=false, Container /v1/capture/status recording=false, NodeAPI /Record/Running running=false, /Capture/State recordingState=0. NodeAPI /Record/Enable desired=false; no recording start/stop requests issued. Containers and shared unrelated modules were not modified. No commit/push.

**Residual limitations**

- NOT proof of real end-to-end recording success. No screen, audio or MP4 recording was performed in this task.
- Native session ending via abrupt process termination cannot run the in-process completion watcher; legacy NodeAPI cached `Record/Running` may still be stale until worker restart reconciliation. A future source-of-truth read-through path from NvContainer should replace this cache for robust process-crash semantics.
- The old bundled OSC uses `Record/Enable(false)` as a saved toast. The new policy only publishes it on verified `Pass+file`, but no actual CEF screenshot or live Record/Stop toast test was run.
- The new `recordingSaved` field is accepted by the loopback legacy Actual route. This is not authenticated against untrusted same-user local clients; keep record gateway disabled until trust boundaries are reviewed.
