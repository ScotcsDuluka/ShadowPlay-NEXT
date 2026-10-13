# Engine REST integration — NVIDIA Capture.exe ต่อ REST :59001 (replaces TCP :5001)

`Engine/Rest/ShadowPlayRestClient.vb` is a dependency-free shared source
(mirror it into any vbproj with one `<Compile Include>` line — the same
pattern `Overlay.Engine` uses for `TcpClientHelper.vb`).

## Command channel mapping

| Old (TCP :5001 hub) | New (REST :59001 — same API the osc page uses) | Client call |
|---|---|---|
| `RECORD_START:<path>` | `POST /ShadowPlay/v.1.0/Record/Enable {status:true}` → edge → `RecordStartRequested(savePath)` | `RestCommandPoller` |
| `RECORD_STOP` | `POST /ShadowPlay/v.1.0/Record/Enable {status:false}` → `RecordStopRequested()` | `RestCommandPoller` |
| `INSTANTREPLAY_*` | `POST /ShadowPlay/v.1.0/InstantReplay/Enable {status:…}` → `InstantReplayStart/StopRequested()` | `RestCommandPoller` |
| hub `/state` polls | `GET /ShadowPlay/v.1.0/Record/Enable` + `/Running` (1s poll) | `GetRecordEnabled()` |
| page readback via hub push | `POST /ShadowPlay/v.1.0/Record/Running {running:…}` (engine publishes live truth) | `PublishRecordRunning()` |
| save path discovery | `GET /ShadowPlay/v.1.0/RecordPaths` → `videos` | `GetRecordSavePath()` |
| backend presence wait | `GET /Backend/v.1.0/health` until `ok:true` | `WaitForBackend(30s)` |

## Wire-in points (NVIDIA Capture.exe)

1. **Boot** (after capture devices init, before the idle loop):
   ```vb
   Dim rest As New ShadowPlayRestClient()                  ' env override: NVSP_BACKEND_URL
   rest.WaitForBackend(TimeSpan.FromSeconds(30))
   Dim poller As New RestCommandPoller(rest)
   AddHandler poller.RecordStartRequested, Sub(path) StartRecording(path)   ' existing RECORD_START handler body
   AddHandler poller.RecordStopRequested,  Sub()     StopRecording()        ' existing RECORD_STOP handler body
   AddHandler poller.InstantReplayStartRequested, Sub(path) StartReplay(path)
   AddHandler poller.InstantReplayStopRequested,  Sub()     StopReplay()
   poller.Start()
   ```
2. **Recording lifecycle** — at START success and STOP success:
   ```vb
   rest.PublishRecordRunning(True)    ' osc page Status tile flips live
   rest.PublishRecordRunning(False)
   ```
3. **Remove the TCP hub listener/client** for engine commands: `TcpClientHelper.vb`
   stays linked ONLY where the Forms overlay still needs it; the engine
   command path (`RECORD_START/STOP` via `[Notifier] Client.vb` / `[APP] Client.vb`)
   is superseded by the poller. No port of our own is opened — the backend
   is the single authority.

## Why poll instead of push

- The osc page already drives record through these exact endpoints — the
  engine becomes just another API client (the plan's "รับคำสั่งอัดผ่าน
  API เดียวกับ osc page").
- Zero listening ports in the engine → nothing for other processes to
  collide with, no firewall prompts, no port allocation races (:5001 died
  with its owner and left the engine deaf; Enable state survives in
  `Backend/data/state.json` even across backend restarts).
- 1s poll = 2 tiny loopback GETs; a 3s retry on failure means a backend
  restart NEVER takes the engine down (standalone mode rule).

## Backend routes added for this (Backend/routes/shadowplay.js)

- `POST /ShadowPlay/v.1.0/Record|InstantReplay/Running` — engine publish
  (the osc page only ever GETs it; POSTing is engine-only by convention)
- `POST /ShadowPlay/v.1.0/Record|InstantReplay/Enable` — persists + emits
  `/ShadowPlay/v.1.0/<Feature>/update` on socket.io (pages see the flip
  even while the poll cycle runs)
