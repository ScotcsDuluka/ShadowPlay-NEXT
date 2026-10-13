Option Strict On
Option Explicit On
Option Infer On

' RecordingEngineHost.vb
'
' Partial class of UI_Engine that adds Phase 12b RecordingEngine integration.
' Replaces the legacy CaptureEngine (FFmpeg subprocess) with the new
' RecordingEngine (D3D11 + NVENC + NAudio sidecar + FFmpeg mux).
'
' Architecture:
'   RecordingEngine (process-lifetime, persistent GPU resources)
'       ↓ StartSession(config) — BLOCKS until duration or Stop()
'       ↓ Run on background thread (Task.Run) → UI thread not blocked
'   CaptureSession (per-session: audio sidecar + H.264 + wrap + mux → MP4)
'
' Phase 12b hardening (this file):
'   ✔ Initialize runs OFF the UI thread (D3D11 + DXGI + NVENC init can take
'     hundreds of ms — must never block form load / message pump)
'   ✔ FFmpeg resolution order per Phase-11 postmortem lesson #1:
'     settings path → deployment root (exe dir + API-Core) → exe dir → PATH
'   ✔ Full Config → SessionConfig/EngineStartupConfig mapping from
'     CaptureSettings (codec, bitrate, GOP=fps, audio enable, volume)
'   ✔ JobObjectGuard (KILL_ON_JOB_CLOSE) owns every spawned ffmpeg/ffprobe
'     via the OnProcessStarted hook → no orphan FFmpeg, even on host crash
'
' Integration:
'   HandleEngineRecordStart → start background task → respond "ok" immediately
'   HandleEngineRecordStop  → _recordingEngine.Stop() → await task → respond
'   HandleEngineGetStatus   → _recordingEngine.GetStatus() → respond

Imports System.IO
Imports System.Threading.Tasks
Imports CaptureEngine.Recording
Imports CaptureEngine.Diagnostics

Partial Public Class UI_Engine

    ' ─── RecordingEngine (Phase 12b) ─────────────────────────────────
    Private _recordingEngine As RecordingEngine
    Private _recordingTask As Task(Of SessionResult)
    Private _useNewEngine As Boolean = True  ' True = use RecordingEngine, False = legacy
    Private _engineReady As Boolean = False  ' set once off-thread Initialize succeeds
    Private _engineReconfiguring As Boolean = False  ' true while video config rebuilds the persistent encoder
    Private _rebuildPending As Boolean = False  ' config changed during recording; rebuild after stop
    ' ★ P13-AUDIO-TIMELINE: WHY the new engine is unavailable (shown on
    ' every legacy-pipeline record start — ends the "which pipeline ran?"
    ' mystery that kept the [speech][speech][apad-silence] bug invisible).
    Private _engineInitFailReason As String = ""

    ' ─── C/3 session-end broadcast contract ───────────────────────────
    ' A session can end three ways: manual stop (HandleRecordingStop owns
    ' the broadcast), natural expiry (DurationSeconds elapsed, nobody
    ' pressed stop), or async failure (StartSession faulted). Endings 2
    ' and 3 previously ended SILENTLY — the Overlay kept showing
    ' "Recording" until the next user action. WatchSessionEnd broadcasts
    ' for every UNOWNED ending; ClaimSessionEndBroadcast gives the stop
    ' path first refusal, and exactly one side wins under _sessionEndLock.
    Private ReadOnly _sessionEndLock As New Object()
    Private _sessionCounter As Long = 0
    Private _currentSessionId As Long = 0
    Private _sessionEndClaimed As Boolean = False

    ' Phase 12b: one job object owns every child ffmpeg this host spawns
    ' (recording pipeline + legacy path guards). KILL_ON_JOB_CLOSE means
    ' a host crash can never leave an orphan ffmpeg behind.
    Private _engineJobGuard As JobObjectGuard

    ' ─── L1 (UI/Host Recovery): host-side session truth ─────────────
    ' EngineStatus exposes no elapsed/output for the ACTIVE session, so the
    ' host records what IT truly knows: the wall-clock moment it ACCEPTED the
    ' record command (after every guard passed) and the output path it
    ' answered "ok" for. Nothing synthetic is manufactured — Idle/Faulted
    ' report state only. Both are cleared by the session-end watcher.
    Private ReadOnly _newEngineSessionClock As New System.Diagnostics.Stopwatch()
    Private _newEngineSessionOutputPath As String = ""

    ' L1: one persistent 1s progress broadcaster for new-engine sessions.
    ' The legacy pipeline pushes progress via CaptureEngine.ProgressUpdated
    ' (~1/s); the new engine had NO equivalent, so a restarted UI never got a
    ' ticking timer. Host-layer only: elapsed from the host session clock,
    ' size from the REAL output file on disk (FileInfo.Length). frames is not
    ' exposed at host layer and is sent as 0 (Overlay displays time + size).
    ' The tick no-ops unless a session is actually active, so it never fires
    ' alongside the legacy broadcast.
    Private _newEngineProgressTimer As System.Windows.Forms.Timer

    ''' <summary>
    ''' Initialize RecordingEngine on a BACKGROUND thread (Phase 12b fix —
    ''' was synchronous on the UI thread). Called from UI_Engine_Load.
    ''' Creates persistent D3D11 + DXGI + NVENC resources (process-lifetime)
    ''' and the shared JobObjectGuard.
    ''' </summary>
    Private Sub InitializeRecordingEngine()
        Try
            _engineJobGuard = New JobObjectGuard()
        Catch ex As Exception
            ' Best-effort orphan protection — engine still works without it.
            DebugLog($"[RecordingEngine] JobObjectGuard unavailable: {ex.Message}")
        End Try

        ' L1: host progress broadcaster (see field comment). Created on the UI
        ' thread here; the tick itself is a guarded no-op when no session runs.
        Try
            If _newEngineProgressTimer Is Nothing Then
                _newEngineProgressTimer = New System.Windows.Forms.Timer With {.Interval = 1000}
                AddHandler _newEngineProgressTimer.Tick, AddressOf OnNewEngineProgressTick
                _newEngineProgressTimer.Start()
            End If
        Catch ex As Exception
            DebugLog($"[RecordingEngine] progress broadcaster unavailable: {ex.Message}")
        End Try

        Dim settingsSnapshot As CaptureSettings = _settings
        Dim baseDir As String = AppLayout.Dir

        Task.Run(Sub()
                     Try
                         Dim logger As New EngineLogger("RecordingEngine", EngineLogger.LogLevel.Info, AddressOf DebugLog)
                         Dim engine As New RecordingEngine(logger)

                         ' Startup config from Overlay settings (persistent
                         ' encoder session — codec/bitrate/GOP are one-shot).
                         ' PHASE 1 VIDEO RUNTIME WIRING: the mapping moved
                         ' VERBATIM to NextRecordingConfig.MapStartupConfig so
                         ' Engine.ConfigTruth.Tests executes the SAME
                         ' composition on Linux (CT-4 / V-CT pattern).
                         ' ★ V-CT1 REGRESSION FIX: the pre-wiring mapping set
                         ' startup.GopSize = settingsSnapshot.FPS here
                         ' (RecordingEngineHost.vb:96-98) — an accidental
                         ' FPS→GOP coupling. MapStartupConfig keeps GOP at
                         ' the engine default and maps FPS independently.
                         Dim startup As EngineStartupConfig =
                             NextRecordingConfig.MapStartupConfig(settingsSnapshot)

                         If settingsSnapshot IsNot Nothing AndAlso
                            Not String.Equals(OverlayConfig.MapEncoderToInternal(settingsSnapshot.Encoder),
                                              settingsSnapshot.Encoder, StringComparison.OrdinalIgnoreCase) Then
                             DebugLog($"[RecordingEngine] encoder name normalized: '{settingsSnapshot.Encoder}' → '{startup.CodecKey}'")
                         End If

                         engine.Initialize(startup)

                         _recordingEngine = engine
                         _engineReady = True
                         DebugLog("[RecordingEngine] initialized — Idle (background thread)")
                     Catch ex As Exception
                         DebugLog($"[RecordingEngine] Initialize FAILED: {ex.Message}")
                         _engineInitFailReason = ex.Message
                         _recordingEngine = Nothing
                         _engineReady = False
                         _useNewEngine = False
                         DebugLog("[RecordingEngine] falling back to legacy CaptureEngine")
                     End Try
                 End Sub)
    End Sub

    ''' <summary>
    ''' L1: 1s broadcast of REAL session progress while (and only while) a
    ''' new-engine session is active: <elapsed_sec>|0|<file_size_bytes>.
    ''' Elapsed comes from the host session clock; size is measured from the
    ''' actual output file. This is what makes a rehydrated UI keep ticking
    ''' after reconnect — and gives the new-engine path the live progress the
    ''' legacy pipeline always had.
    ''' </summary>
    Private Sub OnNewEngineProgressTick(sender As Object, e As EventArgs)
        Try
            Dim taskRef As Task(Of SessionResult) = _recordingTask
            If taskRef Is Nothing OrElse taskRef.IsCompleted Then Return
            If Not _newEngineSessionClock.IsRunning Then Return
            If tcp Is Nothing OrElse Not tcp.IsConnected Then Return

            Dim sec As Integer = CInt(Math.Floor(_newEngineSessionClock.Elapsed.TotalSeconds))
            Dim sizeBytes As Long = 0
            Dim outPath As String = _newEngineSessionOutputPath
            If Not String.IsNullOrEmpty(outPath) Then
                Try
                    Dim fi As New IO.FileInfo(outPath)
                    If fi.Exists Then sizeBytes = fi.Length
                Catch
                    ' Size stays 0 — never fabricate a size.
                End Try
            End If

            tcp.Send("engine_recording_progress", $"{sec}|0|{sizeBytes}")
        Catch
            ' Progress must never take the host down.
        End Try
    End Sub

    ''' <summary>
    ''' Rebuild the persistent RecordingEngine from the current unified config.json.
    ''' The previous runtime stays alive until replacement initialization succeeds.
    ''' </summary>
    Private Sub ReinitializeRecordingEngineFromConfig()
        If _engineReconfiguring Then Return
        If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
            _rebuildPending = True
            DebugLog("[RecordingEngine] config changed during recording — rebuild queued after stop")
            Return
        End If

        _engineReconfiguring = True
        DebugLog("[RecordingEngine] rebuilding persistent runtime from fresh config.json...")

        Dim fresh As CaptureSettings = CaptureSettings.Load(_configPath)
        SyncWithOverlayConfig(fresh)
        Dim startup As EngineStartupConfig = NextRecordingConfig.MapStartupConfig(fresh)
        Dim previousEngine As RecordingEngine = _recordingEngine

        Task.Run(Sub()
                     Try
                         ' DXGI output duplication cannot be owned by two
                         ' backends at once. Release the old runtime before
                         ' initializing the replacement; otherwise
                         ' DuplicateOutput fails and the old encoder silently
                         ' remains active with stale bitrate/preset settings.
                         Try
                             previousEngine?.Dispose()
                         Catch ex As Exception
                             DebugLog($"[RecordingEngine] previous runtime dispose before rebuild: {ex.Message}")
                         End Try

                         Dim logger As New EngineLogger("RecordingEngine", EngineLogger.LogLevel.Info, AddressOf DebugLog)
                         Dim replacement As New RecordingEngine(logger)
                         replacement.Initialize(startup)
                         Me.Invoke(Sub()
                                       _recordingEngine = replacement
                                       _settings = fresh
                                       _engineReady = True
                                       ' ★ Flag restore: a startup failure permanently set
                                       ' _useNewEngine=False (legacy dispatch), so a user
                                       ' fixing the config got a healthy rebuilt runtime that
                                       ' every engine_record_* command still refused to use
                                       ' until an app restart. A successful rebuild means the
                                       ' new-engine path is healthy again.
                                       _useNewEngine = True
                                       _engineInitFailReason = ""
                                       _engineReconfiguring = False
                                       Try : previousEngine?.Dispose() : Catch ex As Exception : DebugLog($"[RecordingEngine] previous runtime dispose: {ex.Message}") : End Try
                                       DebugLog($"[RecordingEngine] runtime rebuilt: fps={startup.Fps}, bitrate={startup.BitrateBps}, preset={startup.Preset}")
                                       RefreshOverlayConfigUI()
                                       UpdateDiagnosticsPanel()
                                   End Sub)
                     Catch ex As Exception
                         Me.Invoke(Sub()
                                       _recordingEngine = Nothing
                                       _engineReady = False
                                       _useNewEngine = False
                                       _engineInitFailReason = $"Runtime rebuild failed: {ex.Message}"
                                       _engineReconfiguring = False
                                       DebugLog($"[RecordingEngine] runtime rebuild FAILED — recording disabled until runtime is rebuilt: {ex.Message}")
                                       UpdateDiagnosticsPanel()
                                   End Sub)
                     End Try
                 End Sub)
    End Sub
    ''' <summary>
    ''' Dispose RecordingEngine + job guard. Called from UI_Engine_FormClosing.
    ''' Order matters: engine first (stops any active session → mux finishes),
    ''' THEN the job guard (its handle close would kill ffmpeg children).
    ''' </summary>
    Private Sub DisposeRecordingEngine()
        Try
            _recordingEngine?.Dispose()
            DebugLog("[RecordingEngine] disposed")
        Catch ex As Exception
            DebugLog($"[RecordingEngine] dispose error: {ex.Message}")
        End Try

        ' L1: stop the host progress broadcaster with the engine and retire
        ' session truth — no session can outlive the host runtime.
        Try
            If _newEngineProgressTimer IsNot Nothing Then
                _newEngineProgressTimer.Stop()
                _newEngineProgressTimer.Dispose()
                _newEngineProgressTimer = Nothing
            End If
        Catch ex As Exception
            DebugLog($"[RecordingEngine] progress broadcaster dispose error: {ex.Message}")
        End Try
        _newEngineSessionClock.Reset()
        _newEngineSessionOutputPath = ""

        Try
            _engineJobGuard?.Dispose()
            DebugLog("[RecordingEngine] job guard disposed")
        Catch ex As Exception
            DebugLog($"[RecordingEngine] job guard dispose error: {ex.Message}")
        End Try
    End Sub

    ' ─── FFmpeg path resolution (Phase-11 postmortem lesson #1) ──────

    ''' <summary>
    ''' Resolve the real ffmpeg.exe path. The Overlay\API-Core directory is
    ''' part of the deployment contract — NEVER rely on PATH alone.
    ''' Resolution order:
    '''   1. CaptureSettings.FFmpegPath (if the file exists AND runs)
    '''   2. {exe dir}\FFmpeg\ffmpeg.exe     (layout root)
    '''   3. {exe dir}\API-Core\ffmpeg.exe   (deployment root)
    '''   4. {exe dir}\ffmpeg.exe
    '''   5. bare "ffmpeg" (PATH — last resort, logged loudly)
    ''' Each real-path candidate is VALIDATED (see FFmpegLocator): a corrupt
    ''' or stale copy must fall through to the next candidate instead of
    ''' dead-ending the session with Win32Exception 193 at spawn time
    ''' (2026-09-08 postmortem: broken FFmpeg\ffmpeg.exe aborted every
    ''' recording while API-Core\ffmpeg.exe one candidate later was fine).
    ''' </summary>
    Private Function ResolveFFmpegPath() As String
        Dim candidates As New List(Of String)

        Dim settingsPath As String = _settings?.FFmpegPath
        If Not String.IsNullOrEmpty(settingsPath) Then candidates.Add(settingsPath)

        Dim baseDir As String = AppLayout.Dir
        candidates.Add(AppLayout.P("FFmpeg", "ffmpeg.exe"))
        candidates.Add(Path.Combine(baseDir, "API-Core", "ffmpeg.exe"))
        candidates.Add(Path.Combine(baseDir, "ffmpeg.exe"))

        Dim found As String = FFmpegLocator.FirstUsableFFmpeg(candidates, AddressOf DebugLog)
        If found <> "" Then
            DebugLog($"[RecordingEngine] ffmpeg resolved: {found}")
            Return found
        End If

        DebugLog("[RecordingEngine] WARNING — no runnable ffmpeg in settings/deployment (see FFmpegLocator rejections above); falling back to PATH lookup")
        Return "ffmpeg"
    End Function

    ' ─── Recording command handlers (Phase 12b) ──────────────────────

    ''' <summary>
    ''' Start recording using RecordingEngine. Non-blocking — starts a
    ''' background task that runs StartSession() and responds immediately.
    ''' </summary>
    ' Async signature kept for handler-family symmetry with HandleRecordingStop
    ' (which truly Awaits the session task); this handler dispatches work via
    ' Task.Run and returns without awaiting anything — by design.
#Disable Warning BC42356 ' Deliberately await-less (see comment above)
    Private Async Function HandleRecordingStart(value As String, reqId As String, Optional restSettings As RecordSettingsSnapshot = Nothing) As Task
        Try
            If _engineReconfiguring Then
                SendResponse("engine_record_start", "error", "engine_reconfiguring", reqId)
                Return
            End If

            If Not _engineReady OrElse _recordingEngine Is Nothing Then
                SendResponse("engine_record_start", "error", "engine_not_ready", reqId)
                Return
            End If

            If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
                SendResponse("engine_record_start", "error", "already_recording", reqId)
                Return
            End If

            ' ── FIX-1 (PHASE 0 CONFIG TRUTH): reload effective config ────
            ' Pre-fix this handler built SessionConfig from UI_Engine._settings
            ' — a CaptureSettings loaded ONCE at form init (UI_Engine.vb:616,
            ' called from :85) — so any setting changed after process start
            ' never reached the new-engine path (PHASE 0 audit, HEAD fae0e6a:
            ' "change setting → record immediately" still used the OLD values).
            ' Mirror the legacy fresh-reload exactly (HandleEngineRecordStart,
            ' UI_Engine.vb:369-371): Load + SyncWithOverlayConfig + publish.
            ' The unified Overlay config.json WINS inside CaptureSettings.Load
            ' (CaptureSettings.vb:101-116), so the reloaded object IS the
            ' effective config. Runs on the UI thread (BeginUiInvoke
            ' marshaling — [Engine] Client.vb:216), same as the legacy path.
            Dim effective As CaptureSettings = CaptureSettings.Load(_configPath)
            SyncWithOverlayConfig(effective)
            _settings = effective

            ' Config echo (CONFIG TRUTH acceptance layer 2 — runtime log):
            ' one greppable line stating what the NEXT recording will use.
            Dim sepTracks As Boolean =
                effective.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SeparateTrack
            DebugLog($"[RecordingEngine] effective config (fresh reload): " &
                     $"audio={effective.SystemAudioCapture}, sysVol={effective.SystemAudioVolume}, " &
                     $"mic={effective.MicCapture}, micVol={effective.MicVolume}, " &
                     $"micId='{effective.MicDeviceId}', micName='{effective.MicDeviceName}', " &
                     $"separateTracks={sepTracks}, clock={effective.AudioClockMode}")

            ' Resolve FFmpeg path (deployment contract — Phase 11 lesson #1).
            ' AFTER the reload above — so a fresh FFmpegPath from config is
            ' honored, not the process-start snapshot.
            Dim ffmpegPath As String = ResolveFFmpegPath()
            If ffmpegPath = "ffmpeg" AndAlso _settings?.FFmpegPath IsNot Nothing AndAlso _settings.FFmpegPath.Length > 0 Then
                ' Settings named a path that does not exist — surface it.
                ' 2026-09-09: previously this rejection was sent silently — the
                ' engine log ended at the PATH-lookup warning and the record
                ' failure looked like a dead button. Log the exact reason.
                DebugLog($"[RecordingEngine] record start REJECTED — settings FFmpegPath is not runnable and no deployment/PATH candidate exists: {_settings.FFmpegPath}")
                SendResponse("engine_record_start", "error", "ffmpeg_not_found: " & _settings.FFmpegPath, reqId)
                Return
            End If

            ' Session config: the mapping moved VERBATIM to NextRecordingConfig
            ' (Engine\[API]\NextRecordingConfig.vb) so Engine.ConfigTruth.Tests
            ' (CT-4) executes the SAME composition on Linux. Only the settings
            ' SOURCE changed (process-start snapshot → fresh reload above);
            ' field-for-field mapping semantics are unchanged.
            ' P13.4: AudioClockMode plumbing — "Device" = hardware-stamped
            ' timeline (WasapiPositionCapture + AudioTapDeviceClock),
            ' "Legacy" = proven v2 path.
            Dim config As SessionConfig =
                NextRecordingConfig.MapSessionConfig(effective, value, ffmpegPath, AddressOf AssignChildToJob)

            ' ── CAPTURE-REST-PLAN phase 3: real quality settings ─────────
            ' The REST edge fetched the backend's live /Record/Settings —
            ' the REAL user quality (what the osc page shows). Applied as
            ' session-level overrides; RecordingEngine.StartSession
            ' reconciles FPS/bitrate/resolution via its per-session NVENC
            ' rebuild. Nothing here when the fetch failed — config.json
            ' remains the authority (fresh-reload semantics preserved).
            ' The legacy command path passes Nothing — untouched behavior.
            If restSettings IsNot Nothing Then
                If restSettings.Framerate > 0 Then config.TargetFps = restSettings.Framerate
                If restSettings.BitrateBps > 0 Then config.BitrateBps = restSettings.BitrateBps
                If restSettings.NativeResolution Then
                    config.UseNativeResolution = True
                ElseIf restSettings.Width > 0 AndAlso restSettings.Height > 0 Then
                    config.UseNativeResolution = False
                    config.RequestedWidth = restSettings.Width
                    config.RequestedHeight = restSettings.Height
                End If
                DebugLog($"[RecordingEngine] REST quality override: quality='{restSettings.Quality}', res='{restSettings.Resolution}'" &
                         If(restSettings.NativeResolution, " (native)", $" → {restSettings.Width}x{restSettings.Height}") &
                         $", fps={restSettings.Framerate}, bitrate={restSettings.BitrateBps} bps")
            End If

            ' ✅ PHASE 3: keep the session seam visible to the Effective
            ' Runtime panel (the engine stamps the ACTUAL encode dims into
            ' this object at session start — RecordingEngine.StartSession).
            _lastSessionConfig = config

            DebugLog($"[RecordingEngine] starting session: path={value}, audio={config.AudioEnabled}, mic={config.MicEnabled}")

            ' C/3: register the session end-broadcast contract for THIS
            ' session, then arm the completion watcher. Runs before any
            ' await so no ending can slip past the watcher.
            Dim sessionId As Long = System.Threading.Interlocked.Increment(_sessionCounter)
            SyncLock _sessionEndLock
                _currentSessionId = sessionId
                _sessionEndClaimed = False
            End SyncLock

            ' L1: host-side session truth starts here — every guard has
            ' passed and this session WILL be answered "ok". Cleared by the
            ' session-end watcher for every ending kind.
            _newEngineSessionOutputPath = If(value, "")
            _newEngineSessionClock.Restart()

            ' Start on background thread — StartSession blocks until done
            _recordingTask = Task.Run(Function() _recordingEngine.StartSession(config))
            WatchSessionEnd(_recordingTask, sessionId)

            ' Respond immediately — recording has started
            ' UI update on UI thread
            Me.Invoke(Sub()
                          lblStatus.Text = "Recording (Hub)..."
                          lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                          tmrRecording.Start()
                          btnRecord.Enabled = False
                          btnStop.Enabled = True
                      End Sub)

            SendResponse("engine_record_start", "ok", value, reqId)
        Catch ex As Exception
            DebugLog($"[RecordingEngine] start error: {ex.Message}")
            SendResponse("engine_record_start", "error", ex.Message, reqId)
        End Try
    End Function
#Enable Warning BC42356

    ''' <summary>
    ''' C/3: fires at EVERY session end — natural expiry, async fault, or
    ''' manual stop. The manual-stop path claims the broadcast first
    ''' (ClaimSessionEndBroadcast) and owns it end-to-end; whatever ending
    ''' remains (nobody pressed stop, or the session crashed) is broadcast
    ''' HERE as engine_recording_saved / engine_recording_error, exactly
    ''' once per session. Previously those endings were silent: the Overlay
    ''' kept showing "Recording" with no file and no error toast.
    ''' Fire-and-forget by design — never throws into the session task.
    ''' </summary>
    Private Sub WatchSessionEnd(task As Task(Of SessionResult), sessionId As Long)
#Disable Warning BC42358 ' Fire-and-forget watcher — the session task outlives this method
        task.ContinueWith(
            Sub(t)
                Try
                    Dim result As SessionResult = Nothing
                    Dim faultMessage As String = ""
                    If t.Status = TaskStatus.RanToCompletion Then
                        result = t.Result
                    ElseIf t.Exception IsNot Nothing Then
                        faultMessage = t.Exception.GetBaseException().Message
                    End If

                    ' Under one lock: read ownership, decide, and mark the
                    ' session's end as broadcast — so the stop path arriving
                    ' late (expiry → user presses stop moments later) cannot
                    ' double-toast.
                    Dim action As SessionEndAction
                    SyncLock _sessionEndLock
                        If _currentSessionId <> sessionId Then Return   ' stale watcher
                        If _sessionEndClaimed Then
                            action = SessionEndAction.None              ' stop path owns it
                        Else
                            action = SessionEndBroadcastPolicy.Decide(result, False)
                            If action <> SessionEndAction.None Then _sessionEndClaimed = True
                        End If
                    End SyncLock

                    ' L1: the session is over (any ending kind — manual stop,
                    ' expiry, fault). Retire host-side session truth so
                    ' engine_get_status goes back to state-only and the 1s
                    ' progress broadcast goes quiet. The stale-watcher return
                    ' above guarantees a NEWER session owns the clock.
                    _newEngineSessionClock.Reset()
                    _newEngineSessionOutputPath = ""

                    Select Case action
                        Case SessionEndAction.Saved
                            Try
                                If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                                    tcp.Send("engine_recording_saved", result.OutputPath)
                                    DebugLog($"[RecordingEngine] session end broadcast (unowned ending): saved {result.OutputPath}")
                                End If
                            Catch
                            End Try
                        Case SessionEndAction.[Error]
                            Dim why As String = SessionEndBroadcastPolicy.DescribeFailure(result, faultMessage)
                            Try
                                If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                                    tcp.Send("engine_recording_error", why)
                                    DebugLog($"[RecordingEngine] session end broadcast (unowned ending): {why}")
                                End If
                            Catch
                            End Try
                    End Select
                Catch ex As Exception
                    DebugLog($"[RecordingEngine] session-end watcher error: {ex.Message}")
                End Try
            End Sub, TaskScheduler.Default)
#Enable Warning BC42358
    End Sub

    ''' <summary>
    ''' C/3: the manual-stop path claims this session's end broadcast BEFORE
    ''' signalling Stop(). True = caller owns the saved/error broadcast;
    ''' False = the completion watcher already broadcast (expiry ending).
    ''' </summary>
    Private Function ClaimSessionEndBroadcast() As Boolean
        SyncLock _sessionEndLock
            If _sessionEndClaimed Then Return False
            _sessionEndClaimed = True
            Return True
        End SyncLock
    End Function

    ''' <summary>
    ''' Stop recording. Signals RecordingEngine.Stop() and waits for the
    ''' background task to complete. Returns the session result.
    ''' </summary>
    Private Async Function HandleRecordingStop(reqId As String) As Task
        Try
            If _recordingEngine Is Nothing OrElse _recordingTask Is Nothing Then
                SendResponse("engine_record_stop", "error", "not_recording", reqId)
                Return
            End If

            DebugLog("[RecordingEngine] stopping...")

            ' C/3: claim the session's end broadcast BEFORE stopping — the
            ' manual-stop path owns saved/error; if the completion watcher
            ' already broadcast (session expired/failed moments earlier),
            ' this path must not double-toast.
            Dim stopOwnsBroadcast As Boolean = ClaimSessionEndBroadcast()

            _recordingEngine.Stop()

            ' Wait for the session to complete (should be quick after Stop)
            Dim result As SessionResult = Await _recordingTask
            _recordingTask = Nothing

            If _rebuildPending AndAlso Not _engineReconfiguring Then
                _rebuildPending = False
                ReinitializeRecordingEngineFromConfig()
            End If

            DebugLog($"[RecordingEngine] stopped: pass={result.Pass}, file={result.OutputPath}")
            DebugLog($"[RecordingEngine] evidence: frames={result.FramesEncoded}, audioBytes={result.AudioBytes}, " &
                     $"dropped={result.AudioDroppedBytes}, accountingOk={result.AudioAccountingOk}, " &
                     $"offset={result.SystemOffsetSec:0.000}s, muxDur={result.MuxVideoDurationSec:0.000}s")

            ' UI update: a file with dropped audio or another failed session
            ' must never be presented as successfully saved.
            Me.Invoke(Sub()
                          tmrRecording.Stop()
                          lblTimer.Text = "00:00:00"
                          If result.Pass Then
                              lblStatus.Text = "Saved: " & Path.GetFileName(result.OutputPath)
                              lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                          Else
                              lblStatus.Text = "Recording failed"
                              lblStatus.ForeColor = Drawing.Color.OrangeRed
                          End If
                          btnRecord.Enabled = True
                          btnStop.Enabled = False
                      End Sub)

            ' ✅ PHASE 3: Output-layer truth (SessionResult) into the panel.
            UpdateDiagnosticsPanel()

            If result.Pass Then
                ' W2-2 contract: the stop toast fires on the REAL completion
                ' broadcast (engine_recording_saved), NOT on this response —
                ' the Overlay intentionally shows nothing on engine_record_stop
                ' ok. The legacy CaptureEngine fires RecordingStopped ->
                ' OnRecordingStopped -> the same tcp.Send; the Duluka path must
                ' match or the Overlay never shows the stop toast (W2-H1
                ' removed the optimistic Sub_Record toast).
                ' C/3: gated on broadcast ownership — if the session ended
                ' unowned (expiry) the watcher already broadcast saved.
                Try
                    If stopOwnsBroadcast AndAlso tcp IsNot Nothing AndAlso tcp.IsConnected Then
                        tcp.Send("engine_recording_saved", result.OutputPath)
                    End If
                Catch
                End Try
                SendResponse("engine_record_stop", "ok", result.OutputPath, reqId)
            Else
                Dim why As String = $"pass=False: frames={result.FramesEncoded}, file={result.FileExists}, " &
                                    $"video={result.VideoStreamFound}, audio={result.AudioStreamFound}" &
                                    If(String.IsNullOrEmpty(result.ErrorMessage), "", $", err={result.ErrorMessage}")
                SendResponse("engine_record_stop", "error", why, reqId)
            End If
        Catch ex As Exception
            DebugLog($"[RecordingEngine] stop error: {ex.Message}")
            SendResponse("engine_record_stop", "error", ex.Message, reqId)
        End Try
    End Function

    ''' <summary>
    ''' Get recording status.
    ''' </summary>
    Private Sub HandleRecordingGetStatus(reqId As String)
        Try
            If _recordingEngine Is Nothing Then
                SendResponse("engine_get_status", "ok", If(_engineReady, "Idle", "Initializing"), reqId)
                Return
            End If

            Dim status As EngineStatus = _recordingEngine.GetStatus()
            Dim stateName As String = status.State.ToString()

            ' L1 (UI/Host Recovery): while a session the host ACCEPTED is still
            ' alive, append <elapsed_sec>|<output_path> so a restarted UI can
            ' rehydrate REC state from engine truth. '|' is illegal in Windows
            ' file names → safe field delimiter. Every other state (Idle,
            ' Faulted, Disposed, Stopping after the clock was retired) reports
            ' state only — the host never invents session data.
            If status.State = RecordingEngineState.Recording AndAlso
               _newEngineSessionClock.IsRunning Then
                Dim elapsedSec As Integer = CInt(Math.Floor(_newEngineSessionClock.Elapsed.TotalSeconds))
                SendResponse("engine_get_status", "ok", $"{stateName}|{elapsedSec}|{_newEngineSessionOutputPath}", reqId)
                Return
            End If

            SendResponse("engine_get_status", "ok", stateName, reqId)
        Catch ex As Exception
            SendResponse("engine_get_status", "error", ex.Message, reqId)
        End Try
    End Sub

    ' ─── Job-object assignment (no-orphan-FFmpeg criterion) ──────────

    ''' <summary>
    ''' Assign a spawned child (ffmpeg/ffprobe) to the host's job object.
    ''' Passed into SessionConfig.OnProcessStarted and MuxCoordinator.
    ''' </summary>
    Private Sub AssignChildToJob(proc As System.Diagnostics.Process)
        If _engineJobGuard IsNot Nothing AndAlso proc IsNot Nothing Then
            Try
                ' ★ H2: Assign reports ownership honestly now — surface a
                ' failure loudly instead of pretending the child is protected.
                If _engineJobGuard.Assign(proc) Then
                    DebugLog($"[RecordingEngine] child → job object: {proc.ProcessName} (pid {proc.Id})")
                Else
                    DebugLog($"[RecordingEngine] job assign FAILED — child left unowned: {proc.ProcessName} (pid {proc.Id})")
                End If
            Catch ex As Exception
                DebugLog($"[RecordingEngine] job assign failed for pid {proc.Id}: {ex.Message}")
            End Try
        End If
    End Sub

End Class
