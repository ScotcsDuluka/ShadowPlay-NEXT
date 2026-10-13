' UI_Engine.vb
' ShadowPlay Engine - Main Form Logic
' TCP Hub Client Mode — เชื่อม API Hub (port 5000) รับ engine_* commands
'
' ✅ P2: Engine UI ตอนนี้อ่านค่าจาก config.json + video.json ของ Overlay
' (source of truth) แสดงผลแบบ read-only และ refresh ทุก 2 วินาที
' ถ้า Overlay เปลี่ยนค่า → Engine UI จะอัปเดทตาม

Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Windows.Forms
Imports System.Xml
Imports CaptureEngine.Recording

Partial Public Class UI_Engine
    Const WS_EX_TRANSPARENT As Integer = &H20



    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function SetWindowLong(hWnd As IntPtr, nIndex As Integer, dwNewLong As Integer) As Integer
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function GetWindowLong(hWnd As IntPtr, nIndex As Integer) As Integer
    End Function

    Protected Overrides Sub WndProc(ByRef m As Message)

        Const WM_NCHITTEST As Integer = &H84
        Const HTTRANSPARENT As Integer = -1

        If m.Msg = WM_NCHITTEST Then
            Dim pos As Point = Me.PointToClient(Cursor.Position)


            If Me.GetChildAtPoint(pos) Is Nothing Then
                m.Result = CType(HTTRANSPARENT, IntPtr)
                Return
            End If
        End If

        MyBase.WndProc(m)

    End Sub

    Private Const GWL_EXSTYLE As Integer = -20
    Private Const WS_EX_TOOLWINDOW As Integer = &H80
    Private Const WS_EX_APPWINDOW As Integer = &H40000
    Private Sub HideFromAltTab()
        Dim style As Integer = GetWindowLong(Me.Handle, GWL_EXSTYLE)
        SetWindowLong(Me.Handle, GWL_EXSTYLE, style Or WS_EX_TOOLWINDOW And Not WS_EX_APPWINDOW)
    End Sub
    Private Sub hub_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        HideFromAltTab()
    End Sub
    ' ── Fields ─────────────────────────────────────────────────

    Private _settings As CaptureSettings
    Private _captureEngine As CaptureEngine
    Private _audioForm As AudioSettingsForm
    Private _encoderDetector As EncoderDetector
    Private _hotkeyStartId As Integer = -1
    Private _hotkeyStopId As Integer = -1
    Private _configPath As String
    Private _isLoaded As Boolean = False

    ' ✅ P2: cache last-loaded Overlay config so we can detect changes
    Private _overlayConfig As OverlayConfig.AppConfig
    Private _overlayVideo As OverlayConfig.VideoConfig
    Private _lastConfigWrite As DateTime = DateTime.MinValue
    Private _lastVideoWrite As DateTime = DateTime.MinValue

    ' ── PHASE 3 UI CONTRACT (docs/UI_CONFIG_ARCHITECTURE.md §9/§14.1) ──
    ' Engine WinForms = DIAGNOSTIC/OPERATOR console. It no longer persists
    ' engine.json from UI controls — the second-writer divergence is
    ' resolved (CONFIG_RUNTIME_CONTRACT v1.0 §1: config.json is the
    ' user-facing store; engine.json = engine-internal + declared compat
    ' writers only). Remaining engine.json writers: SyncWithOverlayConfig
    ' legacy-branch fallback + PREWARM — both declared compat writers.
    ' Diagnostics panel state (UI spec §12):
    Private _lastSessionConfig As SessionConfig
    Private _lastMeasuredFps As Double = 0.0
    Private _lastActualMbps As Double = 0.0

    ' ── Form Load / Close ──────────────────────────────────────

    ' โหมด headless (--rest): ไม่มีหน้าต่าง/TCP diagnostic — รันเป็น capture worker ล้วน
    ' (NvContainer.exe เป็น bus: spawn ตัวนี้ไว้, ตัวนี้ poll /Duluka plane เอง)
    Private ReadOnly _headless As Boolean =
        Environment.GetCommandLineArgs().Any(Function(a) String.Equals(a.Trim(), "--rest", StringComparison.OrdinalIgnoreCase))

    Private Sub UI_Engine_Load(sender As Object, e As EventArgs) Handles Me.Load
        _configPath = AppLayout.P("Config", "engine.json")

        ' ✅ P2: locate Overlay's config.json + video.json first.
        ' lblConfigSource will show where they were found.
        RefreshOverlayConfigUI()
        lblConfigSource.Text = "Config: " & If(OverlayConfig.IsAvailable,
                                               OverlayConfig.ConfigPath,
                                               "(not found — using Engine defaults)")

        LoadSettings()
        InitializeEngine()
        InitializeRecordingEngine()  ' Phase 12b: initialize new RecordingEngine
        DetectEncoders()

        ' เชื่อมกับ API Hub (headless: ข้าม TCP diagnostic — ต้นตอ busy-loop เดิม)
        If Not _headless Then StartHubClient()

        ' CAPTURE-REST-PLAN phase 1 (wire-through): REST command channel on
        ' :59001 -- the same API surface the osc page drives. The old TCP
        ' :5001 engine path stays for the diagnostic window, but record
        ' commands now arrive through the poller (state machine edges).
        StartRestChannel()

        ' Phase 3 (§21.2): capture boundary REST — node JS boundary (NvShadowPlayAPI.js
        ' middleware) ยิงมาที่ 127.0.0.1:59077 — NvCapture = เจ้าของการอัดตามสถาปัตยกรรม §20
        StartCaptureRestApi()

        ' ✅ PHASE 3 UI CONTRACT: the mirror controls (nudFPS, nudBitrate,
        ' chkNativeRes, cboResolution, cboCaptureMethod, cboEncoder,
        ' nudReplayDuration, txtOutputDir, txtFFmpegPath) are READ-ONLY
        ' (Designer: Enabled=False / ReadOnly=True). Their change handlers and
        ' the browse-button write paths were removed — config.json is the only
        ' user-writable store (contract v1.0 §1); this window is diagnostic.
        AddHandler btnRecord.Click, AddressOf OnRecordClick
        AddHandler btnStop.Click, AddressOf OnStopClick
        AddHandler btnStressTest.Click, AddressOf OnStressTestClick
        AddHandler btnDetect.Click, AddressOf OnDetectClick
        AddHandler tmrRecording.Tick, AddressOf OnTimerTick

        ' PHASE 3: dead write paths — the browse buttons wrote engine.json
        ' (OutputDirectory was silently NOT persisted by CaptureSettings.Save
        ' at all; FFmpegPath duplicated the canonical Paths.FFmpegPath).
        ' Hide the buttons; the textboxes stay as read-only effective-value mirrors.
        btnBrowse.Visible = False
        btnFFmpegBrowse.Visible = False

        ' ✅ P2: refresh Overlay config every 2s to detect changes
        AddHandler tmrRefresh.Tick, AddressOf OnRefreshTick
        tmrRefresh.Start()

        _isLoaded = True

        ' ── Start AudioSettingsForm in background ──
        ' Form starts invisible (Opacity=0). OPEN_UI timer polls for Audio.UI marker.
        ' Overlay creates Audio.UI → form shows. BT_Back deletes Audio.UI → form hides.
        ' ✅ PHASE 3: the form is now operator-view + legacy audio.json fallback
        ' only (it no longer writes engine.json/video.json — triple-write killed).
        Try
            Dim s As CaptureSettings = CaptureSettings.Load(_configPath)
            SyncWithOverlayConfig(s)
            _audioForm = New AudioSettingsForm(s)
            _audioForm.Show(Me)  ' Show() แค่เริ่ม form — Opacity=0 อยู่เพราะยังไม่มี Audio.UI
        Catch
        End Try

        ' headless: ซ่อนทุกหน้าต่าง — รันเงียบ ๆ เป็น capture worker
        If _headless Then
            Me.Hide()   ' ⚠ ห้ามแตะ ShowInTaskbar ใน Load — recreate handle = Application.Run ออกทันที
            If _audioForm IsNot Nothing Then _audioForm.Hide()
            DebugLog("[headless] --rest mode: form hidden, TCP skipped, poller active")
        End If
    End Sub

    Private Sub UI_Engine_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If _captureEngine IsNot Nothing AndAlso _captureEngine.IsRecording Then
            _captureEngine.ForceStop()
        End If
        DisposeRecordingEngine()  ' Phase 12b: dispose new RecordingEngine
        ' ✅ PHASE 3: SaveSettings() removed — the Engine UI no longer persists
        ' engine.json from UI controls (second-writer divergence resolved;
        ' CONFIG_RUNTIME_CONTRACT v1.0 §1 stores law).
        If tcp IsNot Nothing Then
            tcp.Disconnect()
            tcp.Dispose()
        End If
        If _restPoller IsNot Nothing Then _restPoller.Stop()
        If _captureEngine IsNot Nothing Then _captureEngine.Dispose()
        ' ✅ P1: flush all queued log writers so no log lines are lost on exit.
        BackgroundLogger.ShutdownAll()
    End Sub

    ' ── Hub Client (uses shared TcpClientHelper — see [Overlay] Client.vb) ──

    Private Sub StartHubClient()
        StartTcpClient()
        ' Give the TCP helper a moment to connect, then broadcast engine_ready.
        ' If not connected yet, the TCP helper will fire OnReconnecting; we'll
        ' broadcast engine_ready on the next successful register attempt via
        ' a one-shot timer.
        Dim t As New System.Windows.Forms.Timer With {.Interval = 500}
        AddHandler t.Tick, Sub(s, ev)
                               t.Stop()
                               t.Dispose()
                               BroadcastEngineReady()
                               ' Update UI
                               If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                               End If
                           End Sub
        t.Start()
    End Sub

    Private Sub OnHubLog(sender As Object, message As String)
        DebugLog(message)
    End Sub

    ' ═══════════════════════════════════════════════════════════════════════
    ' CAPTURE-REST-PLAN phase 1 (wire-through): REST command channel :59001.
    ' Polls /Duluka/v.1.0/State recordState edges (the plane the osc page
    ' drives), confirms actuals via /Duluka/v.1.0/Actual so the backend
    ' completes its PROVEN state machine and pushes to the osc page.
    ' No pixels yet -- log-only pipeline; frame grabber lands in phase 2.
    ' ═══════════════════════════════════════════════════════════════════════

    Private _rest As ShadowPlayRestClient
    Private _restPoller As RestCommandPoller

    Private Sub StartRestChannel()
        _rest = New ShadowPlayRestClient()
        Dim boot As New System.Threading.Thread(Sub()
                                                    Try
                                                        Dim up As Boolean = _rest.WaitForBackend(TimeSpan.FromSeconds(10))
                                                        DebugLog(If(up, "[REST] backend :59001 up -- command poller starting (phase 1 wire-through, log-only)",
                                                                       "[REST] backend :59001 not up after 10s -- poller keeps retrying (standalone rule)"))
                                                    Catch
                                                        DebugLog("[REST] boot probe error -- poller keeps retrying")
                                                    End Try
                                                    _restPoller = New RestCommandPoller(_rest)
                                                    AddHandler _restPoller.RecordStartRequested, AddressOf OnRestRecordStartRequested
                                                    AddHandler _restPoller.RecordStopRequested, AddressOf OnRestRecordStopRequested
                                                    AddHandler _restPoller.InstantReplayStartRequested, AddressOf OnRestInstantReplayStartRequested
                                                    AddHandler _restPoller.InstantReplayStopRequested, AddressOf OnRestInstantReplayStopRequested
                                                    _restPoller.Start()
                                                End Sub)
        boot.IsBackground = True
        boot.Name = "RestBoot"
        boot.Start()
    End Sub

    ' ── phase 2 (CAPTURE-REST-PLAN): edges dispatch into the PROVEN new-engine
    ' session path (HandleRecordingStart/Stop — the exact seam the record
    ' button uses, reqId="" like OnStopClick). Marshaled onto the UI thread
    ' via SafeInvoke (BeginInvoke) — same pattern as the TCP-triggered path.
    ' The backend actual-confirm now happens AFTER the engine accept point.

    Private Sub OnRestRecordStartRequested(savePath As String)
        DebugLog("[REST] RecordStart edge (recordState=Starting) savePath=" & savePath & " -- phase 3: real capture dispatch")
        ' ★ CAPTURE-REST-PLAN phase 3: fetch the REAL quality settings the
        ' backend holds right now (GET /Record/Settings — the osc page's
        ' authority: quality/resolution/framerate/bitrate). Runs on the
        ' poller thread, 3s request timeout — safe here. On any failure
        ' Nothing flows through and config.json remains the authority.
        Dim restSettings As RecordSettingsSnapshot = Nothing
        Try
            restSettings = _rest?.TryGetRecordSettings()
        Catch
            restSettings = Nothing
        End Try
        If restSettings IsNot Nothing Then
            DebugLog($"[REST] record settings: quality='{restSettings.Quality}', res='{restSettings.Resolution}'" &
                     If(restSettings.NativeResolution, " (native)", $" → {restSettings.Width}x{restSettings.Height}") &
                     $", fps={restSettings.Framerate}, bitrate={restSettings.BitrateBps} bps")
        Else
            DebugLog("[REST] record settings unavailable — config.json remains the authority")
        End If
        Try
            ' REST savePath may be a directory hint (e.g. the Videos root) —
            ' only honor it when it looks like a real file target; otherwise
            ' generate a proper timestamped filename from settings.
            Dim outPath As String = ""
            If Not String.IsNullOrEmpty(savePath) AndAlso IO.Path.HasExtension(savePath) AndAlso Not IO.Directory.Exists(savePath) Then
                outPath = savePath
            End If
            If String.IsNullOrEmpty(outPath) Then outPath = _settings.GenerateOutputFilename()
            SafeInvoke(Sub() StartRestRecording(outPath, restSettings))
        Catch ex As Exception
            DebugLog("[REST] start dispatch FAILED: " & ex.Message)
        End Try
    End Sub

    Private Async Sub StartRestRecording(outPath As String, restSettings As RecordSettingsSnapshot)
        Try
            ' Same regime dispatch as OnRecordClick: nvidia = installed
            ' ShadowPlay external recorder; everything else = process-lifetime
            ' RecordingEngine (Ddagrab + NVENC + ffmpeg mux).
            If String.Equals(OverlayConfig.GetEngineMode(), "nvidia", StringComparison.OrdinalIgnoreCase) Then
                HandleNvidiaRecordStart(outPath)
            Else
                Await HandleRecordingStart(outPath, "", restSettings)
            End If
            _rest.ConfirmRecordStarted()
            DebugLog("[REST] confirmed actual=Recording -- backend pushes to osc page")
        Catch ex As Exception
            DebugLog("[REST] confirm start FAILED: " & ex.Message)
        End Try
    End Sub

    Private Sub OnRestRecordStopRequested()
        DebugLog("[REST] RecordStop edge (recordState=Stopping) -- phase 2: stop session")
        SafeInvoke(Sub() StopRestRecording())
    End Sub

    Private Async Sub StopRestRecording()
        Try
            If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
                Await HandleRecordingStop("")
            Else
                DebugLog("[REST] no active session task -- confirming Idle")
            End If
            _rest.ConfirmRecordStopped()
            DebugLog("[REST] confirmed actual=Idle -- backend pushes to osc page")
        Catch ex As Exception
            DebugLog("[REST] confirm stop FAILED: " & ex.Message)
        End Try
    End Sub

    Private Sub OnRestInstantReplayStartRequested(savePath As String)
        DebugLog("[REST] InstantReplayStart edge savePath=" & savePath & " -- phase 1 log-only")
    End Sub

    Private Sub OnRestInstantReplayStopRequested()
        DebugLog("[REST] InstantReplayStop edge -- phase 1 log-only")
    End Sub

    ' ═══════════════════════════════════════════════════════════════════════
    ' ✅ P2: Overlay config synchronization
    ' ═══════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Refresh the UI from Overlay's config.json + video.json.
    ''' Called on Form Load and every 2s by tmrRefresh.
    ''' </summary>
    Private Sub RefreshOverlayConfigUI()
        Try
            ' Re-resolve path in case Overlay just started after Engine
            If Not OverlayConfig.IsAvailable Then
                OverlayConfig.ResetResolvedPath()
            End If

            _overlayConfig = OverlayConfig.LoadConfig()
            _overlayVideo = OverlayConfig.LoadVideoConfig()

            ' ─── video.json → UI ───
            If _overlayVideo IsNot Nothing Then
                ' Encoder (display label)
                lblPresetValue.Text = If(_overlayVideo.active_preset, "(none)")
                lblNvencPreset.Text = "NVENC: " & OverlayConfig.MapNvencPreset(_overlayVideo.current.encoder_preset)

                ' FPS / Bitrate (read-only mirror)
                If _overlayVideo.current.fps > 0 AndAlso _overlayVideo.current.fps <= 240 Then
                    nudFPS.Value = _overlayVideo.current.fps
                End If
                ' ✅ P2.6: clamp to nudBitrate's min/max (1-200 Mbps) to avoid
                ' ArgumentOutOfRangeException if Overlay's bitrate is out of range.
                If _overlayVideo.current.bitrate > 0 Then
                    Dim mbps As Integer = CInt(_overlayVideo.current.bitrate / 1000)
                    mbps = Math.Max(1, Math.Min(200, mbps))
                    nudBitrate.Value = mbps
                End If

                ' Resolution
                chkNativeRes.Checked = _overlayVideo.current.use_native_resolution
                ' ✅ PHASE 3: cboResolution is a read-only mirror (no longer
                ' enabled/disabled by the native flag — the control cannot be
                ' edited from this window at all).
                If Not _overlayVideo.current.use_native_resolution AndAlso _overlayVideo.current.width > 0 Then
                    lblNvencPreset.Text &= $" | {_overlayVideo.current.width}x{_overlayVideo.current.height}"
                End If

                ' Replay duration
                If _overlayVideo.replay_duration >= 15 AndAlso _overlayVideo.replay_duration <= 1200 Then
                    nudReplayDuration.Value = _overlayVideo.replay_duration
                End If

                ' Audio
                chkSysAudio.Checked = _overlayVideo.audio.system_enabled
                chkMic.Checked = _overlayVideo.audio.mic_enabled
                trkSysVol.Value = CInt(Math.Max(0, Math.Min(100, _overlayVideo.audio.system_volume * 100)))
                trkMicVol.Value = CInt(Math.Max(0, Math.Min(100, _overlayVideo.audio.mic_volume * 100)))
                lblSysVol.Text = "Sys Vol: " & trkSysVol.Value & "%"
                lblMicVol.Text = "Mic Vol: " & trkMicVol.Value & "%"
                If Not String.IsNullOrEmpty(_overlayVideo.audio.mic_device) Then
                    lblMicDevice.Text = "Mic: " & _overlayVideo.audio.mic_device
                Else
                    lblMicDevice.Text = "Mic: (default)"
                End If

                ' Encoder dropdown
                UpdateEncoderDropdownFromOverlay(_overlayVideo.encoder)
            End If

            ' ─── config.json → UI ───
            If _overlayConfig IsNot Nothing Then
                ' Output directory
                Dim galleryPath As String = _overlayConfig.Paths.GalleryPath
                If String.IsNullOrEmpty(galleryPath) Then galleryPath = _overlayConfig.Paths.SavePath
                txtOutputDir.Text = If(String.IsNullOrEmpty(galleryPath), "(not set)", galleryPath)

                ' FFmpeg path
                txtFFmpegPath.Text = If(_overlayConfig.Paths.FFmpegPath, "(not set)")
                ValidateFFmpegPath()

                ' GitHub user
                If _overlayConfig.GitHubUser.IsLoggedIn AndAlso Not String.IsNullOrEmpty(_overlayConfig.GitHubUser.Username) Then
                    lblGitHubUser.Text = _overlayConfig.GitHubUser.Username
                    lblGitHubStatus.Text = "Status: logged in"
                    lblGitHubStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                Else
                    lblGitHubUser.Text = "(not logged in)"
                    lblGitHubStatus.Text = "Status: not logged in"
                    lblGitHubStatus.ForeColor = Drawing.Color.FromArgb(160, 160, 160)
                End If
            End If

            ' Config source label
            lblConfigSource.Text = "Config: " & If(OverlayConfig.IsAvailable,
                                                   OverlayConfig.ConfigPath,
                                                   "(not found)")

        Catch ex As Exception
            DebugLog("RefreshOverlayConfigUI error: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Update the encoder dropdown to reflect the Overlay-selected encoder.
    ''' Called from RefreshOverlayConfigUI.
    ''' </summary>
    Private Sub UpdateEncoderDropdownFromOverlay(overlayEncoder As String)
        If String.IsNullOrEmpty(overlayEncoder) Then Return
        Dim ffmpegEncoder As String = OverlayConfig.MapEncoderToFfmpeg(overlayEncoder)
        ' Find the dropdown item that starts with this encoder id
        For i As Integer = 0 To cboEncoder.Items.Count - 1
            Dim item As String = cboEncoder.Items(i).ToString()
            If item.IndexOf(ffmpegEncoder, StringComparison.OrdinalIgnoreCase) >= 0 Then
                If cboEncoder.SelectedIndex <> i Then
                    cboEncoder.SelectedIndex = i
                End If
                Exit For
            End If
        Next
    End Sub

    ''' <summary>
    ''' Timer tick: check if Overlay's config files changed on disk.
    ''' Uses LastWriteTime to avoid re-reading + re-parsing on every tick.
    ''' </summary>
    Private Sub OnRefreshTick(sender As Object, e As EventArgs)
        Try
            Dim configChanged As Boolean = False
            Dim videoChanged As Boolean = False

            Dim configPath As String = OverlayConfig.ConfigPath
            Dim videoPath As String = OverlayConfig.VideoConfigPath

            If configPath.Length > 0 AndAlso File.Exists(configPath) Then
                Dim wt As DateTime = File.GetLastWriteTime(configPath)
                If wt <> _lastConfigWrite Then
                    _lastConfigWrite = wt
                    configChanged = True
                End If
            End If

            If videoPath.Length > 0 AndAlso File.Exists(videoPath) Then
                Dim wt As DateTime = File.GetLastWriteTime(videoPath)
                If wt <> _lastVideoWrite Then
                    _lastVideoWrite = wt
                    videoChanged = True
                End If
            End If

            ' If config was not found before, retry path resolution every tick
            If Not OverlayConfig.IsAvailable Then
                OverlayConfig.ResetResolvedPath()
                If OverlayConfig.IsAvailable Then
                    configChanged = True
                    videoChanged = True
                End If
            End If

            If configChanged OrElse videoChanged Then
                DebugLog($"[Engine] Overlay config changed (config={configChanged}, video={videoChanged}) → refreshing UI")
                RefreshOverlayConfigUI()
            End If

            ' Update Hub status panel
            UpdateHubStatusUI()

            ' ✅ PHASE 3: Effective Runtime panel (UI spec §12) — refreshed on
            ' the same 2s cadence so init-completion and config edits show up.
            UpdateDiagnosticsPanel()
        Catch ex As Exception
            ' Don't let timer exceptions crash the form
            DebugLog("OnRefreshTick error: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Update the Hub status panel from the TcpClientHelper.
    ''' </summary>
    Private Sub UpdateHubStatusUI()
        If tcp Is Nothing Then
            lblHubStatus.Text = "not started"
            lblHubClients.Text = "Engine: offline"
            Return
        End If

        If tcp.IsConnected Then
            lblHubStatus.Text = "connected (port 5001)"
            lblHubStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
            lblHubClients.Text = "Engine: online"
        Else
            lblHubStatus.Text = "disconnected — reconnecting..."
            lblHubStatus.ForeColor = Drawing.Color.FromArgb(255, 200, 50)
            lblHubClients.Text = "Engine: offline"
        End If
    End Sub

    ' ═══════════════════════════════════════════════════════════════════════
    ' ✅ P2.3: command dispatch moved to [Overlay] Client.vb (DispatchEngineCommand)
    ' — uses the shared TcpClientHelper, same as Overlay.
    ' ═══════════════════════════════════════════════════════════════════════

    ' ── NVIDIA Engine (installed ShadowPlay recorder) ──────────
    ' OverlayConfig.GetEngineMode() = "nvidia" routes here: the INSTALLED
    ' GFE/ShadowPlay recorder is driven via its own manual-record hotkey
    ' (Alt+F9) and the finished file is adopted via the verified
    ' engine_recording_saved broadcast. NVIDIA records what ITS rules allow
    ' (supported games / desktop capture enabled in GFE) — identical to real
    ' GFE behavior.

    Private Shared ReadOnly _nvRecorder As New NvShadowPlayRecorder()
    Private _nvRecording As Boolean

    Private Sub HandleNvidiaRecordStart(reqId As String)
        If Not NvShadowPlayRecorder.IsAvailable() Then
            SendResponse("engine_record_start", "error", "nvidia_gfe_not_installed", reqId)
            Return
        End If
        If _nvRecorder.IsRecording OrElse _nvRecording Then
            SendResponse("engine_record_start", "error", "already_recording", reqId)
            Return
        End If
        _nvRecorder.Start()
        _nvRecording = True
        OnEngineStateChanged(CaptureEngine.CaptureState.Recording)
        SendResponse("engine_record_start", "ok", "nvidia_shadowplay", reqId)
    End Sub

    Private Sub HandleNvidiaRecordStop(reqId As String)
        If Not _nvRecording Then
            SendResponse("engine_record_stop", "error", "not_recording", reqId)
            Return
        End If
        OnEngineStateChanged(CaptureEngine.CaptureState.Stopping)
        Dim file As String = _nvRecorder.StopRecording()
        _nvRecording = False
        OnEngineStateChanged(CaptureEngine.CaptureState.Idle)
        If file.Length > 0 Then
            tcp.Send("engine_recording_saved", file)
            SendResponse("engine_record_stop", "ok", file, reqId)
        Else
            ' NVIDIA produced nothing: unsupported target, hotkey remapped,
            ' or overlay disabled in GFE — surface honestly.
            SendResponse("engine_record_stop", "error", "nvidia_no_file_produced", reqId)
        End If
    End Sub

    ' ── Engine Command Handlers (TCP แค่ on/off) ──────────

    Private Async Function HandleEngineRecordStart(value As String, reqId As String) As Task
        Try
            DebugLog($"[Engine] HandleEngineRecordStart: path={value}, reqId={If(String.IsNullOrEmpty(reqId), "(none)", reqId)}")

    ' Engine 2 / Duluka is a separate runtime path. Honor the persisted
    ' engine_mode instead of always constructing the legacy CaptureEngine.
            If OverlayConfig.GetEngineMode() = "ddagrab" Then
                Await HandleRecordingStart(value, reqId)
                Return
            End If

            ' NVIDIA Engine: drive the INSTALLED ShadowPlay recorder
            ' (nvspcap64 via its own hotkey) — full ShadowPlay feature set on
            ' GFE machines. Selected with engine_mode = "nvidia".
            If OverlayConfig.GetEngineMode() = "nvidia" Then
                HandleNvidiaRecordStart(reqId)
                Return
            End If

            ' ★ C/5 H1: reject paths that cannot be real Windows file paths
            ' (quote / control characters) BEFORE they reach the legacy FFmpeg
            ' command line (FFmpegArgumentBuilder embeds -y ""{outputFile}""
            ' quoted but unescaped). Same contract the new-engine path enforces
            ' inside NextRecordingConfig.MapSessionConfig.
            If Not NextRecordingConfig.IsSafeRecordingOutputPath(value) Then
                DebugLog($"[Engine] RECORD_START rejected: invalid output path ({If(value, "(null)")})")
                SendResponse("engine_record_start", "error", "invalid_output_path", reqId)
                Return
            End If

            ' ✅ P2.6: load settings from Overlay's config.json + video.json
            ' (source of truth). Old code loaded from Engine's shadowplay-config.json
            ' which drifted out of sync with Overlay → wrong encoder/fps/bitrate.
            ' Now we merge Overlay's video.json into CaptureSettings so the
            ' recording uses exactly what the user picked in Overlay's UI.
            Dim s As CaptureSettings = CaptureSettings.Load(_configPath)
            SyncWithOverlayConfig(s)
            _settings = s
            DebugLog($"[Engine] config loaded (synced with Overlay): FFmpegPath={If(_settings.FFmpegPath, "(empty)")}, Encoder={If(_settings.Encoder, "(empty)")}, CaptureMethod={_settings.CaptureMethod}, FPS={_settings.FPS}, Bitrate={_settings.Bitrate}")

            ' ✅ P1.6: if FFmpegPath is empty or doesn't exist, respond with
            ' a clear error immediately. Old code let StartRecordingAsync run
            ' and fail inside Validate() — but the error path was unreliable
            ' (Async Sub + event-based ErrorOccurred). Now we fail fast and
            ' send the response ourselves.
            If String.IsNullOrEmpty(_settings.FFmpegPath) OrElse Not IO.File.Exists(_settings.FFmpegPath) Then
                DebugLog($"[Engine] RECORD_START rejected: FFmpegPath invalid ('{_settings.FFmpegPath}')")
                SendResponse("engine_record_start", "error", "ffmpeg_not_found", reqId)
                Return
            End If

            If String.IsNullOrEmpty(_settings.Encoder) Then
                DebugLog("[Engine] RECORD_START rejected: Encoder not set")
                SendResponse("engine_record_start", "error", "no_encoder_selected", reqId)
                Return
            End If

            If Not IO.Directory.Exists(_settings.OutputDirectory) Then
                IO.Directory.CreateDirectory(_settings.OutputDirectory)
            End If

            ' ★ Overlapped-start guard: BeginInvoke keeps the UI pump running
            ' while a previous handler is inside Await — a second
            ' engine_record_start (double hotkey, Overlay retry) used to hit
            ' Dispose() below and KILL the in-flight recording (ffmpeg killed
            ' mid-encode → truncated moov-less file, temp files deleted). The
            ' new-engine path already answers already_recording; mirror it here.
            ' ★ H1 fix: the guard now uses IsRecordingLifecycleActive so the
            ' stop flow's MUXING phase is protected too — the old check
            ' (IsRecording/Stopping) let a start request dispose the old
            ' engine mid-mux: jobGuard.Dispose killed the mux ffmpeg and
            ' ForceStop raced the mux's temp-file fallback → recording lost.
            If _captureEngine IsNot Nothing AndAlso
               _captureEngine.IsRecordingLifecycleActive Then
                DebugLog("[Engine] RECORD_START rejected: recording lifecycle active")
                SendResponse("engine_record_start", "error", "already_recording", reqId)
                Return
            End If

            _captureEngine?.Dispose()
            _captureEngine = New CaptureEngine(_settings)
            AddHandler _captureEngine.StateChanged, AddressOf OnEngineStateChanged
            AddHandler _captureEngine.RecordingStarted, AddressOf OnRecordingStarted
            AddHandler _captureEngine.RecordingStopped, AddressOf OnRecordingStopped
            AddHandler _captureEngine.ErrorOccurred, AddressOf OnEngineError
            AddHandler _captureEngine.ProgressUpdated, AddressOf OnEngineProgress

            DebugLog($"[Engine] starting CaptureEngine with override path: {value}")
            Dim ok As Boolean = Await _captureEngine.StartRecordingAsync(value)
            DebugLog($"[Engine] StartRecordingAsync returned: {ok}")

            If ok Then
                ' ✅ P2.6: caller already marshals via BeginUiInvoke, so we're
                ' on the UI thread. Direct UI access is safe.
                lblStatus.Text = "Recording (Hub)..."
                lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                tmrRecording.Start()
                btnRecord.Enabled = False
                btnStop.Enabled = True
                SendResponse("engine_record_start", "ok", _captureEngine.OutputFile, reqId)
            Else
                SendResponse("engine_record_start", "error", "start_failed", reqId)
            End If
        Catch ex As Exception
            DebugLog("RecordStart error: " & ex.Message)
            SendResponse("engine_record_start", "error", ex.Message, reqId)
        End Try
    End Function

    ''' <summary>
    ''' ✅ P2.6: copy values from Overlay's video.json + config.json into
    ''' Engine's CaptureSettings. This is what makes Engine actually use the
    ''' encoder/fps/bitrate the user picked in Overlay's UI.
    ''' </summary>
    Private Sub SyncWithOverlayConfig(s As CaptureSettings)
        Try
            ' GLM/6 UNIFIED: Overlay's config.json is the single user-facing
            ' config — apply it directly (encoder/fps/bitrate/resolution/audio/
            ' paths, incl. MicDeviceId + TrackMode). Legacy video.json is only
            ' consulted when config.json is unavailable (old installs).
            If OverlayConfig.ApplyUnifiedToCaptureSettings(s) Then
                Return
            End If

            Dim video As OverlayConfig.VideoConfig = OverlayConfig.LoadVideoConfig()
            Dim appCfg As OverlayConfig.AppConfig = OverlayConfig.LoadConfig()

            If video IsNot Nothing Then
                ' Encoder: Overlay uses 'NVENC_H264' etc., FFmpeg uses 'h264_nvenc'.
                s.Encoder = OverlayConfig.MapEncoderToFfmpeg(video.encoder)

                ' FPS / Bitrate from current preset values.
                If video.current.fps > 0 AndAlso video.current.fps <= 240 Then
                    s.FPS = video.current.fps
                End If
                If video.current.bitrate > 0 Then
                    ' Overlay stores bitrate in kbps, CaptureSettings expects bps.
                    s.Bitrate = video.current.bitrate * 1000L
                End If

                ' Resolution.
                s.UseNativeResolution = video.current.use_native_resolution
                If Not video.current.use_native_resolution Then
                    s.CustomWidth = video.current.width
                    s.CustomHeight = video.current.height
                End If

                ' Capture method from api_capture (Overlay's name).
                If Not String.IsNullOrEmpty(video.api_capture) Then
                    s.CaptureMethod = video.api_capture.ToLowerInvariant()
                End If

                ' Audio.
                s.SystemAudioCapture = video.audio.system_enabled
                s.MicCapture = video.audio.mic_enabled
                s.SystemAudioVolume = video.audio.system_volume
                s.MicVolume = video.audio.mic_volume
                s.MicDeviceName = video.audio.mic_device
                s.MicDeviceId = video.audio.mic_device_id
                s.AudioCapture = s.SystemAudioCapture OrElse s.MicCapture
            End If

            If appCfg IsNot Nothing Then
                ' FFmpegPath: prefer Overlay's path (it's the bundler).
                If Not String.IsNullOrEmpty(appCfg.Paths.FFmpegPath) AndAlso IO.File.Exists(appCfg.Paths.FFmpegPath) Then
                    s.FFmpegPath = appCfg.Paths.FFmpegPath
                End If

                ' OutputDirectory: prefer GalleryPath, fallback to SavePath.
                Dim outDir As String = appCfg.Paths.GalleryPath
                If String.IsNullOrEmpty(outDir) Then outDir = appCfg.Paths.SavePath
                If Not String.IsNullOrEmpty(outDir) Then
                    s.OutputDirectory = outDir
                End If
            End If

            ' Persist back to shadowplay-config.json so old code paths still work.
            s.Save(_configPath)
        Catch ex As Exception
            DebugLog($"SyncWithOverlayConfig error: {ex.Message}")
        End Try
    End Sub

    Private Async Function HandleEngineRecordStop(reqId As String) As Task
        Try
            ' NVIDIA Engine owns the active session in nvidia mode.
            If OverlayConfig.GetEngineMode() = "nvidia" Then
                HandleNvidiaRecordStop(reqId)
                Return
            End If

            ' Stop the runtime that actually owns the active session. Do not
            ' infer this from stale UI state: _recordingTask is the authoritative
            ' marker for the new Duluka path.
            If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
                Await HandleRecordingStop(reqId)
                Return
            End If

            If _captureEngine Is Nothing OrElse Not _captureEngine.IsRecording Then
                SendResponse("engine_record_stop", "error", "not_recording", reqId)
                Return
            End If

            ' ✅ P1: Await instead of task.Wait().
            Await _captureEngine.StopRecordingAsync()

            Me.Invoke(Sub()
                          tmrRecording.Stop()
                          lblTimer.Text = "00:00:00"
                          lblStatus.Text = "Saved: " & Path.GetFileName(_captureEngine.OutputFile)
                          lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                          btnRecord.Enabled = True
                          btnStop.Enabled = False
                      End Sub)

            SendResponse("engine_record_stop", "ok", _captureEngine.OutputFile, reqId)
        Catch ex As Exception
            SendResponse("engine_record_stop", "error", ex.Message, reqId)
        End Try
    End Function

    Private Sub HandleEngineReplayStart(value As String, reqId As String)
        SendResponse("engine_replay_start", "error", "not_implemented", reqId)
    End Sub

    Private Sub HandleEngineReplayStop(reqId As String)
        SendResponse("engine_replay_stop", "error", "not_implemented", reqId)
    End Sub

    Private Sub HandleEngineReplaySave(value As String, reqId As String)
        SendResponse("engine_replay_save", "error", "not_implemented", reqId)
    End Sub

    Private Sub HandleEngineGetStatus(reqId As String)
        ' NVIDIA Engine: status = hotkey-driven session state.
        If OverlayConfig.GetEngineMode() = "nvidia" Then
            Dim nvState As String = If(_nvRecorder.IsRecording,
                $"Recording|{_nvRecorder.ElapsedSeconds}", "Idle")
            SendResponse("engine_get_status", "ok", nvState, reqId)
            Return
        End If
        Dim state As String = "Idle"
        If _captureEngine IsNot Nothing AndAlso _captureEngine.IsRecording Then
            ' L1 (UI/Host Recovery): while a session is really alive, append
            ' <elapsed_sec>|<output_path> so a restarted UI can rehydrate REC
            ' state from ENGINE truth (its own recording clock + output file).
            ' '|' is illegal in Windows file names → safe field delimiter.
            ' No session → state-only answer; the host never invents data.
            Dim elapsedSec As Integer = CInt(Math.Floor(_captureEngine.RecordingDuration.TotalSeconds))
            Dim outputFile As String = If(_captureEngine.OutputFile, "")
            SendResponse("engine_get_status", "ok", $"Recording|{elapsedSec}|{outputFile}", reqId)
            Return
        End If
        SendResponse("engine_get_status", "ok", state, reqId)
    End Sub

    Private Sub HandleEngineLoadConfig(reqId As String)
        Try
            _settings = CaptureSettings.Load(_configPath)
            SendResponse("engine_load_config", "ok", Nothing, reqId)
        Catch ex As Exception
            SendResponse("engine_load_config", "error", ex.Message, reqId)
        End Try
    End Sub

    Private Sub HandleEngineSetEncoder(value As String, reqId As String)
        SendResponse("engine_set_encoder", "error", "use_config_json", reqId)
    End Sub

    ''' <summary>
    ''' ✅ P1.5: Overlay sends PREWARM_FFMPEG with the path to ffmpeg.exe in
    ''' Overlay's api-core folder. Engine should use this path if it doesn't
    ''' have its own ffmpeg.exe. Without this, Engine's CaptureSettings might
    ''' point to a non-existent ffmpeg.exe → StartRecordingAsync fails →
    ''' "กดอัด ขึ้นอัด แต่ไม่ได้อัดจริง".
    '''
    ''' ✅ P2.6: simplified — caller (OnTcpMessage) now marshals via BeginUiInvoke
    ''' so this method always runs on the UI thread. Removed the inner Me.Invoke.
    ''' </summary>
    Private Sub HandleEnginePrewarmFFmpeg(ffmpegPath As String)
        Try
            DebugLog($"[Engine] PREWARM_FFMPEG received: {ffmpegPath}")

            If String.IsNullOrEmpty(ffmpegPath) OrElse Not IO.File.Exists(ffmpegPath) Then
                DebugLog($"[Engine] PREWARM_FFMPEG: path does not exist: {ffmpegPath}")
                Return
            End If

            ' ✅ 2026-09-09: File.Exists alone accepted a corrupt ffmpeg.exe
            ' (the exact 2026-09-08 Win32Exception-193 postmortem pattern) and
            ' the dead path was persisted into config.json — every record
            ' start then rejected with ffmpeg_not_found even though a healthy
            ' API-Core\ffmpeg.exe existed. Validate BEFORE trusting the path;
            ' the probe result is cached per (length, last-write-time) and an
            ' existing-but-broken file fails the spawn probe in milliseconds
            ' (Win32Exception), so this never blocks the UI thread meaningfully.
            If Not FFmpegLocator.IsUsableFFmpeg(ffmpegPath) Then
                DebugLog($"[Engine] PREWARM_FFMPEG: path exists but is NOT a runnable ffmpeg — ignored: {ffmpegPath}")
                Return
            End If

            ' ✅ P2.6: caller marshals via BeginUiInvoke, so we're on the UI thread.
            ' Load current settings, update FFmpegPath, save back.
            Dim s As CaptureSettings = CaptureSettings.Load(_configPath)
            If String.IsNullOrEmpty(s.FFmpegPath) OrElse Not FFmpegLocator.IsUsableFFmpeg(s.FFmpegPath) Then
                ' Self-heal: empty OR stored-but-broken path gets replaced by
                ' the validated incoming one (2026-09-09 — previously a broken
                ' stored path was kept forever and shadowed every candidate).
                If Not String.IsNullOrEmpty(s.FFmpegPath) Then
                    DebugLog($"[Engine] PREWARM_FFMPEG: stored FFmpegPath is not runnable — replacing: {s.FFmpegPath}")
                End If
                s.FFmpegPath = ffmpegPath
                s.Save(_configPath)
                _settings = s
                txtFFmpegPath.Text = ffmpegPath
                ValidateFFmpegPath()
                DebugLog($"[Engine] PREWARM_FFMPEG: updated FFmpegPath → {ffmpegPath}")
                ' Re-detect encoders with the new ffmpeg path.
                DetectEncoders()
            Else
                DebugLog($"[Engine] PREWARM_FFMPEG: already have valid FFmpegPath: {s.FFmpegPath}")
            End If
        Catch ex As Exception
            DebugLog($"[Engine] PREWARM_FFMPEG error: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' ✅ P2: Overlay broadcast engine_config_changed after saving video.json
    ''' or config.json. Engine reloads its UI from the files immediately.
    ''' value = "video" | "config" | "" (empty = reload both)
    ''' ✅ P2.6: caller marshals via BeginUiInvoke, so we're already on UI thread.
    ''' </summary>
    Private Sub HandleEngineConfigChanged(scope As String)
        Try
            DebugLog($"[Engine] engine_config_changed received (scope={scope})")
            ' Force path re-resolution in case Overlay moved files
            OverlayConfig.ResetResolvedPath()

            ' ── FIX-2 (PHASE 0 CONFIG TRUTH): reload _settings for real ──
            ' Pre-fix this handler refreshed ONLY the UI mirror
            ' (_overlayConfig/_overlayVideo via RefreshOverlayConfigUI);
            ' _settings kept the process-start snapshot until restart, so
            ' every _settings consumer stayed stale (PHASE 0 audit finding).
            ' Same fresh-reload the legacy record path uses
            ' (UI_Engine.vb:369-371: Load + SyncWithOverlayConfig + publish).
            ' We are on the UI thread (BeginUiInvoke marshaling —
            ' [Engine] Client.vb:181), matching the legacy threading model.
            Dim fresh As CaptureSettings = CaptureSettings.Load(_configPath)
            SyncWithOverlayConfig(fresh)
            _settings = fresh
            DebugLog($"[Engine] effective config reloaded (engine_config_changed): " &
                     $"audio={fresh.SystemAudioCapture}, mic={fresh.MicCapture}, " &
                     $"clock={fresh.AudioClockMode}, encoder={If(fresh.Encoder, "(empty)")}, bitrate={fresh.Bitrate}")

            ' Refresh UI (we're on the UI thread already).
            RefreshOverlayConfigUI()
            UpdateDiagnosticsPanel()

            ' Video settings are initialization-time inputs to the persistent NVENC
            ' session. Rebuild while idle so the next recording uses the new values.
            If String.Equals(scope, "video", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(scope, "config", StringComparison.OrdinalIgnoreCase) OrElse
               String.IsNullOrWhiteSpace(scope) Then
                ReinitializeRecordingEngineFromConfig()
            End If
        Catch ex As Exception
            DebugLog($"[Engine] engine_config_changed error: {ex.Message}")
        End Try
    End Sub

    ' ── Init ──────────────────────────────────────────────────

    Private Sub LoadSettings()
        _settings = CaptureSettings.Load(_configPath)

        ' ✅ PHASE 3: this is now a pure mirror fill — the controls are
        ' read-only (Designer) and nothing here writes engine.json.
        chkNativeRes.Checked = _settings.UseNativeResolution
        nudFPS.Value = Math.Max(1, Math.Min(240, _settings.FPS))
        nudBitrate.Value = Math.Max(1, Math.Min(200, CLng(_settings.Bitrate / 1000000)))
        txtOutputDir.Text = _settings.OutputDirectory
        txtFFmpegPath.Text = _settings.FFmpegPath

        Select Case _settings.CaptureMethod.ToLower()
            Case "ddagrab" : cboCaptureMethod.SelectedIndex = 0
            Case "gdigrab" : cboCaptureMethod.SelectedIndex = 1
            Case "gfxcapture" : cboCaptureMethod.SelectedIndex = 2
            Case Else : cboCaptureMethod.SelectedIndex = 0
        End Select

        ValidateFFmpegPath()
    End Sub

    ' ✅ PHASE 3 UI CONTRACT: SaveSettings() REMOVED (was at :652-667).
    ' It copied control values (FPS/Bitrate/UseNativeResolution/OutputDirectory/
    ' FFmpegPath/CaptureMethod) into _settings and persisted engine.json —
    ' making this window a second writer for values config.json already owns
    ' (CONFIG_RUNTIME_CONTRACT v1.0 §1 stores law; UI spec §9 REMOVE list).
    ' engine.json writes that remain are the declared compat writers only:
    ' SyncWithOverlayConfig legacy-branch fallback + PREWARM handler.

    Private Sub InitializeEngine()
        _captureEngine = New CaptureEngine(_settings)
        AddHandler _captureEngine.StateChanged, AddressOf OnEngineStateChanged
        AddHandler _captureEngine.RecordingStarted, AddressOf OnRecordingStarted
        AddHandler _captureEngine.RecordingStopped, AddressOf OnRecordingStopped
        AddHandler _captureEngine.ErrorOccurred, AddressOf OnEngineError
        AddHandler _captureEngine.ProgressUpdated, AddressOf OnEngineProgress
    End Sub

    ' ── Detect Encoders ────────────────────────────────────────

    Private Sub DetectEncoders()
        lblStatus.Text = "Detecting encoders..."
        lblStatus.ForeColor = Drawing.Color.FromArgb(255, 200, 50)

        _encoderDetector = New EncoderDetector(_settings.FFmpegPath)

        Task.Run(Sub()
                     Try
                         Dim encOk As Boolean = _encoderDetector.DetectEncoders()
                         _encoderDetector.DetectCaptureDevices()

                         Me.Invoke(Sub()
                                       UpdateEncoderDropdown()

                                       If encOk Then
                                           lblStatus.Text = "Idle - Hub Client (" & _encoderDetector.VideoEncoders.Count.ToString() & " encoders)"
                                           lblStatus.ForeColor = Drawing.Color.FromArgb(160, 160, 160)
                                       Else
                                           lblStatus.Text = "Detection: " & If(String.IsNullOrEmpty(_encoderDetector.LastDetectionError), "No encoders", _encoderDetector.LastDetectionError)
                                           lblStatus.ForeColor = Drawing.Color.FromArgb(255, 200, 50)
                                       End If
                                   End Sub)
                     Catch ex As Exception
                         Me.Invoke(Sub()
                                       lblStatus.Text = "Detection failed: " & ex.Message
                                       lblStatus.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
                                   End Sub)
                     End Try
                 End Sub)
    End Sub

    Private Sub UpdateEncoderDropdown()
        cboEncoder.Items.Clear()

        Dim nvenc As List(Of EncoderDetector.EncoderInfo) = _encoderDetector.GetNVENCEncoders()
        Dim qsv As List(Of EncoderDetector.EncoderInfo) = _encoderDetector.GetQSVEncoders()
        Dim amf As List(Of EncoderDetector.EncoderInfo) = _encoderDetector.GetAMFEncoders()
        Dim cpu As List(Of EncoderDetector.EncoderInfo) = _encoderDetector.GetCPUEncoders()
        Dim fallbackTag As String = ""
        If _encoderDetector.UsedFallback Then fallbackTag = " [fb]"

        If nvenc.Count > 0 Then
            cboEncoder.Items.Add("-- NVIDIA NVENC --")
            For Each enc In nvenc
                cboEncoder.Items.Add("  " & enc.ID & " - " & enc.CodecFamily & fallbackTag)
            Next
        End If
        If qsv.Count > 0 Then
            cboEncoder.Items.Add("-- Intel QuickSync --")
            For Each enc In qsv
                cboEncoder.Items.Add("  " & enc.ID & " - " & enc.CodecFamily & fallbackTag)
            Next
        End If
        If amf.Count > 0 Then
            cboEncoder.Items.Add("-- AMD AMF --")
            For Each enc In amf
                cboEncoder.Items.Add("  " & enc.ID & " - " & enc.CodecFamily & fallbackTag)
            Next
        End If
        If cpu.Count > 0 Then
            cboEncoder.Items.Add("-- CPU Software --")
            For Each enc In cpu
                cboEncoder.Items.Add("  " & enc.ID & " - " & enc.CodecFamily & fallbackTag)
            Next
        End If

        If cboEncoder.Items.Count = 0 Then
            cboEncoder.Items.Add("No encoders available")
            cboEncoder.SelectedIndex = 0
            Return
        End If

        Dim recommended As String = _encoderDetector.GetRecommendedEncoder()
        For i As Integer = 0 To cboEncoder.Items.Count - 1
            If cboEncoder.Items(i).ToString().IndexOf(recommended, StringComparison.OrdinalIgnoreCase) >= 0 Then
                cboEncoder.SelectedIndex = i
                ' ✅ FIX: set _settings.Encoder directly here. Previously this fired OnEncoderChanged
                ' which early-returned when _isLoaded=False (race: detection completes before line 46
                ' sets _isLoaded=True), so first Record click errored with "No encoder selected".
                If _settings IsNot Nothing Then
                    Dim parts As String() = cboEncoder.Items(i).ToString().Trim().Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
                    If parts.Length > 0 Then _settings.Encoder = parts(0).Trim()
                End If
                Exit For
            End If
        Next
    End Sub

    ' ── Record / Stop ──────────────────────────────────────────

    ' ✅ C4 FIX: convert OnRecordClick/OnStopClick to Async Sub so we can
    ' Await StartRecordingAsync/StopRecordingAsync instead of .Wait().
    ' .Wait() is sync-over-async: burns a threadpool thread for the duration
    ' and wraps exceptions in AggregateException (misleading error messages).

    Private Async Sub OnRecordClick(sender As Object, e As EventArgs)
        ' ✅ PHASE 3: SaveSettings() call removed — record path reloads the
        ' effective config from disk below (Load + SyncWithOverlayConfig).
        btnRecord.Enabled = False
        btnStop.Enabled = True

        Try
            ' ✅ P2.6: sync with Overlay config before recording
            ' (same as HandleEngineRecordStart does for TCP-triggered recordings).
            Dim s As CaptureSettings = CaptureSettings.Load(_configPath)
            SyncWithOverlayConfig(s)
            _settings = s

            ' Honor the same persisted engine regime as TCP-triggered recording.
            ' Duluka uses the process-lifetime RecordingEngine; FFmpeg keeps the
            ' existing Legacy CaptureEngine path untouched. NVIDIA drives the
            ' installed ShadowPlay recorder.
            If OverlayConfig.GetEngineMode() = "nvidia" Then
                HandleNvidiaRecordStart("")
                Return
            End If
            If OverlayConfig.GetEngineMode() = "ddagrab" Then
                Await HandleRecordingStart(s.GenerateOutputFilename(), "")
                Return
            End If

            Dim engine As New CaptureEngine(_settings)
            AddHandler engine.StateChanged, AddressOf OnEngineStateChanged
            AddHandler engine.RecordingStarted, AddressOf OnRecordingStarted
            AddHandler engine.RecordingStopped, AddressOf OnRecordingStopped
            AddHandler engine.ErrorOccurred, AddressOf OnEngineError
            ' ✅ C2 FIX: wire ProgressUpdated so button-triggered recordings also
            ' update the status panel + broadcast to Overlay. Was missing — only
            ' TCP-triggered recordings had it.
            AddHandler engine.ProgressUpdated, AddressOf OnEngineProgress

            ' ★ Overlapped-start guard (mirrors HandleEngineRecordStart): a
            ' click while a recording/stop is still unwinding used to Dispose
            ' the live engine — destroying the in-flight recording silently.
            ' ★ H1 fix: IsRecordingLifecycleActive also covers Muxing —
            ' disposing during the stop flow's mux phase killed the mux
            ' ffmpeg (job close) and deleted the temp files it was reading.
            If _captureEngine IsNot Nothing AndAlso
               _captureEngine.IsRecordingLifecycleActive Then
                DebugLog("[Engine] Record click rejected: recording lifecycle active")
                engine.Dispose()
                btnStop.Enabled = _captureEngine.IsRecording
                Return
            End If

            _captureEngine?.Dispose()
            _captureEngine = engine

            Await _captureEngine.StartRecordingAsync()
        Catch ex As Exception
            lblStatus.Text = "Error: " & ex.Message
            lblStatus.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
            btnRecord.Enabled = True
            btnStop.Enabled = False
        End Try
    End Sub

    Private Async Sub OnStopClick(sender As Object, e As EventArgs)
        btnStop.Enabled = False

        Try
            If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then
                Await HandleRecordingStop("")
            Else
                Await _captureEngine.StopRecordingAsync()
            End If
            btnRecord.Enabled = True
        Catch
            btnRecord.Enabled = True
        End Try
    End Sub

    ' ── Stress Test ────────────────────────────────────────────

    ''' <summary>
    ''' Run the 10-scenario stress test matrix.
    ''' Uses current _settings (FFmpegPath, MicDeviceId, etc.) as base, then
    ''' each scenario clones + modifies specific fields (FPS, audio flags, etc.).
    '''
    ''' Pre-test warnings:
    '''   - Scenario 09 requires SILENT system audio (mute YouTube/notifications)
    '''   - Scenarios 02-08 expect audio data (play audio + talk into mic)
    '''
    ''' Output: written to {OutputDirectory}\StressTest\stress_results_*.txt
    ''' </summary>
    Private Async Sub OnStressTestClick(sender As Object, e As EventArgs)
        If _captureEngine IsNot Nothing AndAlso _captureEngine.IsRecording Then
            MessageBox.Show("Cannot run stress test while recording is active. Stop recording first.",
                          "Stress Test", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim outputDir As String = ""
        Try
            btnStressTest.Enabled = False
            btnStressTest.Text = "Running stress test…"

            ' Use current settings as base (FFmpegPath, MicDeviceId, Encoder, etc.)
            ' CRITICAL: _settings.FFmpegPath might be empty if user never synced with Overlay.
            ' Sync from OverlayConfig if needed (same as HandleEngineRecordStart does).
            If String.IsNullOrEmpty(_settings.FFmpegPath) OrElse Not File.Exists(_settings.FFmpegPath) Then
                Try
                    Dim appCfg As OverlayConfig.AppConfig = OverlayConfig.LoadConfig()
                    If appCfg IsNot Nothing AndAlso
                       Not String.IsNullOrEmpty(appCfg.Paths.FFmpegPath) AndAlso
                       File.Exists(appCfg.Paths.FFmpegPath) Then
                        _settings.FFmpegPath = appCfg.Paths.FFmpegPath
                        DebugLog($"[StressTest] synced FFmpegPath from OverlayConfig: {_settings.FFmpegPath}")
                    End If
                Catch ex As Exception
                    DebugLog($"[StressTest] failed to sync FFmpegPath from OverlayConfig: {ex.Message}")
                End Try
            End If

            ' Final check — if still empty, can't proceed
            If String.IsNullOrEmpty(_settings.FFmpegPath) OrElse Not File.Exists(_settings.FFmpegPath) Then
                MessageBox.Show($"FFmpeg not found!{vbCrLf}{vbCrLf}" &
                               "Current FFmpegPath: '{_settings.FFmpegPath}'{vbCrLf}{vbCrLf}" &
                               "Please set FFmpegPath in the UI settings first, or ensure Overlay config has it.",
                               "Stress Test Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End If

            outputDir = Path.Combine(_settings.OutputDirectory, "StressTest")
            If Not Directory.Exists(outputDir) Then
                Directory.CreateDirectory(outputDir)
            End If

            ' Show pre-test instructions
            Dim proceed As DialogResult = MessageBox.Show(
                "Stress test will run 10 scenarios (~5 minutes total)." & vbCrLf & vbCrLf &
                "PRE-TEST INSTRUCTIONS:" & vbCrLf &
                "  • Scenarios 02-08: PLAY AUDIO + TALK INTO MIC" & vbCrLf &
                "  • Scenario 09: MUTE YouTube/notifications (silence required)" & vbCrLf &
                "  • Scenario 06: 10 rapid start/stop cycles (automatic)" & vbCrLf & vbCrLf &
                "Output will be saved to:" & vbCrLf & outputDir & vbCrLf & vbCrLf &
                "Continue?",
                "Stress Test Matrix", MessageBoxButtons.YesNo, MessageBoxIcon.Question)

            If proceed <> DialogResult.Yes Then
                Return
            End If

            ' Build runner — pass Nothing for engine (RunSingleCycleAsync creates its own)
            Dim runner As New StressTestRunner(Nothing, outputDir)
            ' Pass current UI settings as base — has real FFmpegPath/Encoder/MicDeviceId
            Dim baseSettings As CaptureSettings = CloneSettingsForStress(_settings)
            Dim scenarios As List(Of StressTestRunner.TestScenario) = runner.BuildDefaultMatrix(baseSettings)

            Console.WriteLine($"Running {scenarios.Count} scenarios…")

            ' Write progress to file so user can see what's happening
            Dim progressLogPath As String = Path.Combine(outputDir, "stress_progress.txt")
            Dim stressResults As List(Of StressTestRunner.TestResult) = Nothing

            Using progressWriter As New StreamWriter(progressLogPath, False)
                progressWriter.WriteLine($"Stress test started: {DateTime.Now}")
                progressWriter.WriteLine($"Output directory: {outputDir}")
                progressWriter.WriteLine($"FFmpegPath: {_settings.FFmpegPath}")
                progressWriter.WriteLine($"Encoder: {_settings.Encoder}")
                progressWriter.WriteLine($"CaptureMethod: {_settings.CaptureMethod}")
                progressWriter.WriteLine()

                stressResults =
                    Await runner.RunMatrixAsync(scenarios,
                        Sub(r As StressTestRunner.TestResult, done As Integer, total As Integer)
                            Dim line As String = $"  [{done}/{total}] {r}"
                            Console.WriteLine(line)
                            Try
                                progressWriter.WriteLine(line)
                                progressWriter.Flush()
                            Catch
                            End Try
                            ' Update status label in UI
                            Try
                                Me.Invoke(Sub()
                                              lblStatus.Text = $"Stress: [{done}/{total}] {r.Name} — {If(r.Pass, "PASS", "FAIL")}"
                                          End Sub)
                            Catch
                            End Try
                        End Sub)
                progressWriter.WriteLine()
                progressWriter.WriteLine(StressTestRunner.FormatResultTable(stressResults))
            End Using

            Dim table As String = StressTestRunner.FormatResultTable(stressResults)
            Console.WriteLine(table)

            ' Save to file
            Dim timestamp As String = DateTime.Now.ToString("yyyyMMdd_HHmmss")
            Dim logPath As String = Path.Combine(outputDir, $"stress_results_{timestamp}.txt")
            File.WriteAllText(logPath, table)

            ' Show summary
            ' Note: use Enumerable.Count() extension method explicitly to avoid
            ' ambiguity with List(Of T).Count property (which has no predicate).
            Dim passCount As Integer = Enumerable.Count(stressResults, Function(r) r.Pass)
            Dim failCount As Integer = stressResults.Count - passCount
            Dim summary As String = $"Stress test complete!{vbCrLf}{vbCrLf}" &
                                   $"Total: {stressResults.Count}  |  Pass: {passCount}  |  Fail: {failCount}{vbCrLf}{vbCrLf}" &
                                   $"Full results saved to:{vbCrLf}{logPath}"
            MessageBox.Show(summary, "Stress Test Complete",
                          MessageBoxButtons.OK,
                          If(failCount = 0, MessageBoxIcon.Information, MessageBoxIcon.Warning))

        Catch ex As Exception
            MessageBox.Show($"Stress test failed: {ex.Message}{vbCrLf}{vbCrLf}{ex.StackTrace}",
                          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            btnStressTest.Enabled = True
            btnStressTest.Text = "Run Stress Test Matrix (10 scenarios)"
        End Try
    End Sub

    ' ── Engine Events ────────────────────────────────────────────

    Private Sub OnEngineStateChanged(state As CaptureEngine.CaptureState)
        ' ✅ P2.9: broadcast state change to Overlay so its UI stays in sync.
        ' Format: engine_state_changed:<state_name>
        ' Overlay uses this to reconcile its _isRecordingLocal flag.
        Dim stateName As String = state.ToString()
        Try
            If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                tcp.Send("engine_state_changed", stateName)
            End If
        Catch
        End Try

        ' ✅ C6 FIX: guard Me.Invoke with try/catch. This handler is called from
        ' FFmpeg's Exited threadpool thread; if the form is closing (IsDisposed=True
        ' or handle being destroyed), Me.Invoke throws InvalidOperationException
        ' which would propagate out and crash the app.
        SafeInvoke(Sub()
                       Select Case state
                           Case CaptureEngine.CaptureState.Idle
                               lblStatus.Text = "Idle - Hub Client"
                               lblStatus.ForeColor = Drawing.Color.FromArgb(160, 160, 160)
                               tmrRecording.Stop()
                               ' ✅ P2.10: update status panel
                               lblRecState.Text = "● Idle"
                               lblRecState.ForeColor = Drawing.Color.FromArgb(160, 160, 160)

                           Case CaptureEngine.CaptureState.Recording
                               lblStatus.Text = "Recording..."
                               lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                               lblTimer.ForeColor = Drawing.Color.Red
                               tmrRecording.Start()
                               ' ✅ P2.10: update status panel
                               lblRecState.Text = "● Recording"
                               lblRecState.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                               ' Show target bitrate so user can compare with actual.
                               If _settings IsNot Nothing AndAlso _settings.Bitrate > 0 Then
                                   lblRecBitrate.Text = $"Target: {(_settings.Bitrate / 1000000.0):F1} Mbps · {_settings.FPS} FPS · p{_settings.NvencPreset}"
                               End If

                           Case CaptureEngine.CaptureState.Stopping
                               lblStatus.Text = "Stopping..."
                               lblStatus.ForeColor = Drawing.Color.FromArgb(255, 200, 50)
                               tmrRecording.Stop()
                               ' ✅ P2.10: update status panel
                               lblRecState.Text = "● Stopping..."
                               lblRecState.ForeColor = Drawing.Color.FromArgb(255, 200, 50)

                           Case CaptureEngine.CaptureState.HasError
                               lblStatus.Text = "Error"
                               lblStatus.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
                               tmrRecording.Stop()
                               btnRecord.Enabled = True
                               btnStop.Enabled = False
                               ' ✅ P2.10: update status panel
                               lblRecState.Text = "● Error"
                               lblRecState.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
                       End Select
                   End Sub)
    End Sub

    Private Sub OnRecordingStarted(filename As String)
        SafeInvoke(Sub()
                       lblStatus.Text = "Recording: " & Path.GetFileName(filename)
                   End Sub)
    End Sub

    Private Sub OnRecordingStopped(filename As String)
        ' ✅ P2.9: broadcast saved file path to Overlay.
        Try
            If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                tcp.Send("engine_recording_saved", filename)
            End If
        Catch
        End Try

        SafeInvoke(Sub()
                       tmrRecording.Stop()
                       lblTimer.Text = "00:00:00"
                       lblTimer.ForeColor = _accentGreen
                       btnRecord.Enabled = True
                       lblStatus.Text = "Saved: " & Path.GetFileName(filename)
                       lblStatus.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
                   End Sub)
    End Sub

    Private Sub OnEngineError(message As String)
        ' ✅ P2.9: broadcast error to Overlay so it can show a toast.
        Try
            If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                tcp.Send("engine_recording_error", message)
            End If
        Catch
        End Try

        SafeInvoke(Sub()
                       lblStatus.Text = "Error: " & message
                       lblStatus.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
                       btnRecord.Enabled = True
                       btnStop.Enabled = False
                   End Sub)
    End Sub

    ''' <summary>
    ' ✅ C6 FIX: safe Me.Invoke wrapper. Uses BeginInvoke (fire-and-forget) so
    ' the calling thread (FFmpeg Exited, etc.) doesn't block. Catches
    ' InvalidOperationException (form handle not created) and ObjectDisposedException
    ' (form already disposed) silently — these happen during shutdown and are
    ' expected, not errors.
    ''' </summary>
    Private Sub SafeInvoke(action As Action)
        Try
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            Me.BeginInvoke(action)
        ' Most-derived exception first: ObjectDisposedException INHERITS
        ' InvalidOperationException, so the reverse order was unreachable (BC42029).
        Catch ex As ObjectDisposedException
            ' Form already disposed — drop the update.
        Catch ex As InvalidOperationException
            ' Form handle not created yet — drop the update.
        Catch
        End Try
    End Sub

    Private Sub OnHotkeyPressed(id As Integer)
        If id = _hotkeyStartId AndAlso _captureEngine.State = CaptureEngine.CaptureState.Idle Then
            OnRecordClick(Nothing, Nothing)
        ElseIf id = _hotkeyStopId AndAlso _captureEngine.IsRecording Then
            OnStopClick(Nothing, Nothing)
        End If
    End Sub

    Private Sub OnTimerTick(sender As Object, e As EventArgs)
        If _captureEngine IsNot Nothing Then
            lblTimer.Text = _captureEngine.RecordingDuration.ToString("hh\:mm\:ss")
        End If
    End Sub

    ' ✅ P2.9: progress event from CaptureEngine — fired every FFmpeg stderr
    ' progress line (~1/sec at 60fps). Broadcast to Overlay so it can show
    ' real-time timer + file size in its UI.
    ' Format: engine_recording_progress:<duration_sec>|<frames>|<size_bytes>
    Private Sub OnEngineProgress(frames As Long, duration As TimeSpan, sizeBytes As Long)
        Try
            ' ✅ P2.10: update local UI status panel.
            If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then
                Me.BeginInvoke(Sub()
                                   ' Timer
                                   lblTimer.Text = duration.ToString("hh\:mm\:ss")
                                   ' File size
                                   Dim sizeStr As String
                                   If sizeBytes >= 1024 * 1024 * 1024 Then
                                       sizeStr = (sizeBytes / (1024.0 * 1024 * 1024)).ToString("F2") & " GB"
                                   ElseIf sizeBytes >= 1024 * 1024 Then
                                       sizeStr = (sizeBytes / (1024.0 * 1024)).ToString("F1") & " MB"
                                   ElseIf sizeBytes >= 1024 Then
                                       sizeStr = (sizeBytes / 1024.0).ToString("F0") & " KB"
                                   Else
                                       sizeStr = sizeBytes & " B"
                                   End If
                                   lblRecSize.Text = sizeStr
                                   ' Frame count
                                   lblRecFrames.Text = $"{frames:N0} frames"
                                   ' Actual bitrate (size / duration)
                                   If duration.TotalSeconds > 0 Then
                                       Dim actualMbps As Double = (sizeBytes * 8.0) / (duration.TotalSeconds * 1000000.0)
                                       _lastMeasuredFps = frames / duration.TotalSeconds
                                       _lastActualMbps = actualMbps
                                       Dim targetStr As String = ""
                                       If _settings IsNot Nothing AndAlso _settings.Bitrate > 0 Then
                                           targetStr = $" / target {(_settings.Bitrate / 1000000.0):F1}"
                                       End If
                                       lblRecBitrate.Text = $"Actual: {actualMbps:F1} Mbps{targetStr}"
                                   End If
                               End Sub)
            End If

            ' ✅ PHASE 3: live Actual telemetry into the diagnostics panel (~1/s).
            UpdateDiagnosticsPanel()

            ' Broadcast to Overlay.
            If tcp IsNot Nothing AndAlso tcp.IsConnected Then
                Dim sec As Integer = CInt(Math.Floor(duration.TotalSeconds))
                Dim value As String = $"{sec}|{frames}|{sizeBytes}"
                tcp.Send("engine_recording_progress", value)
            End If
        Catch
        End Try
    End Sub

    ' ── UI Change Handlers ─────────────────────────────────────

    ' ✅ PHASE 3 UI CONTRACT: removed the control write-handlers
    ' (OnNativeResChanged / OnEncoderChanged / OnCaptureMethodChanged /
    ' OnBrowseOutput / OnBrowseFFmpeg). The mirror controls are read-only;
    ' user settings are edited in the Overlay and persisted to config.json
    ' via AppSettings.Save (UI spec §10.1 canonical chain; contract v1.0 §1).

    Private Sub OnDetectClick(sender As Object, e As EventArgs)
        ' ✅ PHASE 3: SaveSettings() call removed (no user-config writes from
        ' this window). Re-running detection is a legitimate operator action.
        DetectEncoders()
    End Sub

    ''' <summary>
    ''' ✅ PHASE 3: implemented honestly (was an empty body — both branches
    ''' empty, UI spec §9). Colors the read-only effective-path mirror:
    ''' green = file exists, red = missing. Pure display — no config writes.
    ''' </summary>
    Private Sub ValidateFFmpegPath()
        Try
            If File.Exists(txtFFmpegPath.Text) Then
                txtFFmpegPath.ForeColor = Drawing.Color.FromArgb(118, 185, 0)
            Else
                txtFFmpegPath.ForeColor = Drawing.Color.FromArgb(200, 50, 50)
            End If
        Catch
        End Try
    End Sub

    Private Sub DebugLog(message As String)
        Try
            Dim logDir As String = AppLayout.P("Logs")
            Dim logPath As String = IO.Path.Combine(logDir, "ui-engine.log")
            Dim logLine As String = "[" & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "] " & message
            ' ✅ P1: route through BackgroundLogger instead of File.AppendAllText per line.
            BackgroundLogger.Log(logPath, logLine)
        Catch
        End Try
    End Sub
    Private Sub BT_Back_Click(sender As Object, e As EventArgs) Handles BT_Back.Click
        Dim uiFile = AppLayout.P("Flags", "Engine.UI")

        Try
            If File.Exists(uiFile) Then
                File.Delete(uiFile)
            End If
        Catch ex As IOException
        End Try
    End Sub

    Private Sub OPEN_UI_Tick(sender As Object, e As EventArgs) Handles OPEN_UI.Tick
        HideFromAltTab()

        Dim uiFile = AppLayout.P("Flags", "Engine.UI")

        If File.Exists(uiFile) Then
            Me.WindowState = FormWindowState.Maximized
            Me.Opacity = 1
        Else
            Me.Opacity = 0
            Me.WindowState = FormWindowState.Minimized
        End If
    End Sub

    Private Sub BT_Back_MouseMove(sender As Object, e As MouseEventArgs) Handles BT_Back.MouseMove
        BT_Back.BackColor = Color.Green
    End Sub

    Private Sub BT_Back_MouseLeave(sender As Object, e As EventArgs) Handles BT_Back.MouseLeave
        BT_Back.BackColor = Color.FromArgb(118, 185, 0)
    End Sub

    Private Sub btnOpenAudioSettings_Click(sender As Object, e As EventArgs) Handles btnOpenAudioSettings.Click
        ' AudioSettingsForm runs in background via OPEN_UI timer.
        ' This button creates Audio.UI marker → timer shows the form.
        ' Same pattern as Engine.UI marker system.
        Try
            Dim uiFile As String = AppLayout.P("Flags", "Audio.UI")
            AppLayout.EnsureParentDir(uiFile)   ' Flags\ is runtime-created
            IO.File.WriteAllText(uiFile, DateTime.Now.ToString())
        Catch ex As Exception
            MessageBox.Show(Me, "Failed to open audio settings: " & ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Clone current UI settings for stress test base.
    ''' Copies all fields needed by scenarios (FFmpegPath, Encoder, MicDeviceId, etc.).
    ''' </summary>
    Private Function CloneSettingsForStress(src As CaptureSettings) As CaptureSettings
        Dim clone As New CaptureSettings()
        clone.UseNativeResolution = src.UseNativeResolution
        clone.Encoder = src.Encoder
        clone.FPS = src.FPS
        clone.Bitrate = src.Bitrate
        clone.CaptureMethod = src.CaptureMethod
        clone.OutputDirectory = src.OutputDirectory
        clone.SystemAudioCapture = src.SystemAudioCapture
        clone.MicCapture = src.MicCapture
        clone.SystemAudioVolume = src.SystemAudioVolume
        clone.MicVolume = src.MicVolume
        clone.MicDeviceName = src.MicDeviceName
        clone.MicDeviceId = src.MicDeviceId
        clone.AudioTrackMode = src.AudioTrackMode
        clone.PixelFormat = src.PixelFormat
        clone.Preset = src.Preset
        clone.NvencPreset = src.NvencPreset
        clone.RateControl = src.RateControl
        clone.FileFormat = src.FileFormat
        clone.FFmpegPath = src.FFmpegPath
        clone.HotkeyStart = src.HotkeyStart
        clone.HotkeyStop = src.HotkeyStop
        clone.HotkeyToggle = src.HotkeyToggle
        clone.CustomWidth = src.CustomWidth
        clone.CustomHeight = src.CustomHeight
        clone.ConfigVersion = src.ConfigVersion
        Return clone
    End Function

    ' ═══════════════════════════════════════════════════════════════════════
    ' ✅ PHASE 3: Effective Runtime / Diagnostics panel
    ' (docs/UI_CONFIG_ARCHITECTURE.md §12 — "open one panel and know what the
    '  Engine is actually recording with right now")
    '
    ' Rows carry the truth layers where they exist in-process:
    '   REQUESTED  = config.json value (the Overlay's mirror caches)
    '   EFFECTIVE  = post-mapper CaptureSettings / startup echo / SessionConfig
    '   ACTUAL     = runtime telemetry (init echo, live progress, SessionResult)
    '   OUTPUT     = last SessionResult file truth (ffprobe confirmation stays
    '                the acceptance-layer job — the panel only reports it)
    '
    ' READ-ONLY by design: this panel manufactures no state and writes no
    ' config. Regime labels come from CONFIG_RUNTIME_CONTRACT v1.0 §4 (locked
    ' regime table); gap texts (P1-PIXFMT / Q4, aspirational bitrate) are
    ' quoted verbatim from the runtime truth lines the contract registers.
    ' ═══════════════════════════════════════════════════════════════════════

    ''' <summary>Refresh the read-only diagnostics textbox. Safe from any thread via SafeInvoke.</summary>
    Private Sub UpdateDiagnosticsPanel()
        SafeInvoke(Sub()
                       Try
                           txtDiagnostics.Text = BuildDiagnosticsText()
                           txtDiagnostics.SelectionStart = txtDiagnostics.Text.Length
                           txtDiagnostics.ScrollToCaret()
                       Catch
                           ' Panel must never crash the host window.
                       End Try
                   End Sub)
    End Sub

    Private Function BuildDiagnosticsText() As String
        Dim sb As New Text.StringBuilder()

        ' ── shared state ──
        Dim status As EngineStatus = Nothing
        If _recordingEngine IsNot Nothing Then
            Try : status = _recordingEngine.GetStatus() : Catch : End Try
        End If
        Dim echo As EngineStartupConfig = Nothing
        If _recordingEngine IsNot Nothing Then
            Try : echo = _recordingEngine.StartupEcho : Catch : End Try
        End If
        Dim geometry As String = ""
        If _recordingEngine IsNot Nothing Then
            Try : geometry = _recordingEngine.CaptureGeometry : Catch : End Try
        End If
        Dim lastResult As SessionResult = If(status?.LastSessionResult, Nothing)

        ' ══ Engine pipeline ══
        sb.AppendLine("== ENGINE PIPELINE ==")
        sb.AppendLine(" requested : (auto — no canonical key; contract v1.0 Q1: open OWNER decision)")
        If _useNewEngine Then
            sb.AppendLine(" effective : New Engine (native D3D11 + NVENC, in-proc)")
            sb.AppendLine($" actual    : {(If(_engineReady, "READY", "initializing..."))}" &
                          If(status IsNot Nothing, $" — state={status.State}", ""))
        Else
            sb.AppendLine(" effective : LEGACY CaptureEngine (FFmpeg subprocess)")
            sb.AppendLine($" actual    : fallback — init failed: {_engineInitFailReason}")
        End If
        sb.AppendLine()

        ' ══ Capture API ══
        Dim reqApi As String = "" ' config.json Recording.api_capture (nested mirror first, flat fallback)
        If _overlayVideo IsNot Nothing AndAlso Not String.IsNullOrEmpty(_overlayVideo.api_capture) Then
            reqApi = _overlayVideo.api_capture
        ElseIf _overlayConfig?.Recording IsNot Nothing AndAlso Not String.IsNullOrEmpty(_overlayConfig.Recording.APICapture) Then
            reqApi = _overlayConfig.Recording.APICapture
        End If
        sb.AppendLine("== CAPTURE API (contract v1.0 §4: regime C · echo-only) ==")
        sb.AppendLine($" requested : {If(If(reqApi, "").Length = 0, "(default/auto)", reqApi)}   (config.json Recording.api_capture — no UI writer; Q2 open)")
        sb.AppendLine($" effective : {If(_settings IsNot Nothing, _settings.CaptureMethod, "?")}   (CaptureSettings.CaptureMethod)")
        sb.AppendLine($" actual    : DdagrabBackend (DXGI duplication){If(geometry.Length > 0, $" — {geometry}", "")}")
        sb.AppendLine("           : single production backend; non-ddagrab request = recorded GAP, never silently accepted")
        sb.AppendLine()

        ' ══ FPS ══
        Dim reqFps As Integer = If(_overlayVideo?.current?.fps, 0)
        sb.AppendLine("== FPS (regime A — live per record, V-CT1) ==")
        sb.AppendLine($" requested : {If(reqFps > 0, reqFps.ToString(), "(default 60)")}   (config.json Recording.current.fps)")
        sb.AppendLine($" effective : {If(_settings IsNot Nothing, _settings.FPS.ToString(), "?")}   (CaptureSettings.FPS)")
        Dim targetFps As Integer = If(_lastSessionConfig?.TargetFps, 0)
        sb.AppendLine($" actual    : SessionConfig.TargetFps={If(targetFps > 0, targetFps.ToString(), "(no session yet)")}" &
                      If(_lastMeasuredFps > 0, $" — measured {_lastMeasuredFps:F1} fps", ""))
        If lastResult IsNot Nothing AndAlso lastResult.WrapFps > 0 Then
            sb.AppendLine($"           : last session wrap {lastResult.WrapFps:F1} fps (measured, not display rate)")
        End If
        sb.AppendLine()

        ' ══ Resolution ══
        Dim cur As OverlayConfig.VideoCurrentValues = If(_overlayVideo?.current, Nothing)
        sb.AppendLine("== RESOLUTION (engine init contract — V-CT2) ==")
        If cur IsNot Nothing Then
            If cur.use_native_resolution Then
                sb.AppendLine(" requested : native")
            Else
                sb.AppendLine($" requested : {cur.width}x{cur.height} (custom)")
            End If
        End If
        If echo IsNot Nothing Then
            sb.AppendLine($" effective : use_native={echo.UseNativeResolution}" &
                          If(Not echo.UseNativeResolution AndAlso echo.RequestedWidth > 0,
                             $" → {echo.RequestedWidth}x{echo.RequestedHeight}", ""))
        End If
        Dim encW As Integer = If(_lastSessionConfig?.EncodeWidth, 0)
        Dim encH As Integer = If(_lastSessionConfig?.EncodeHeight, 0)
        sb.AppendLine($" actual    : {If(encW > 0, $"encode {encW}x{encH}", If(geometry.Length > 0, $"capture {geometry}", "(no session yet)"))}")
        sb.AppendLine()

        ' ══ Encoder ══
        Dim reqEnc As String = If(_overlayVideo?.encoder, "")
        sb.AppendLine("== ENCODER (regime B — engine init; restart required) ==")
        sb.AppendLine($" requested : {If(reqEnc.Length > 0, reqEnc, "(default NVENC_H264)")}   (config.json Recording.encoder)")
        sb.AppendLine($" effective : {If(_settings IsNot Nothing, _settings.Encoder, "?")} → internal {OverlayConfig.MapEncoderToInternal(If(_settings?.Encoder, ""))}")
        sb.AppendLine($" actual    : {If(echo IsNot Nothing, echo.CodecKey, "(pending init)")}   (NVENC_H264 = only implemented codec; others → legacy fallback)")
        sb.AppendLine()

        ' ══ Pixel Format ══
        sb.AppendLine("== PIXEL FORMAT (BLOCKER P1-PIXFMT — contract v1.0 Q4) ==")
        sb.AppendLine($" requested : {If(_settings IsNot Nothing AndAlso Not String.IsNullOrEmpty(_settings.PixelFormat), _settings.PixelFormat, "(default nv12)")}   (engine.json PixelFormat — legacy key)")
        sb.AppendLine(" actual    : BGRA8 (D3D11 capture) → NVENC ARGB — config NOT honored (no conversion layer)")
        sb.AppendLine()

        ' ══ Preset ══
        Dim reqPreset As Integer = If(cur?.encoder_preset, 0)
        sb.AppendLine("== NVENC PRESET (regime B payload — V-CT4 single mapper) ==")
        sb.AppendLine($" requested : {If(reqPreset >= 1 AndAlso reqPreset <= 7, reqPreset.ToString(), "(default 4)")}   (config.json Recording.current.encoder_preset)")
        sb.AppendLine($" effective : {If(_settings IsNot Nothing, OverlayConfig.MapNvencPreset(_settings.NvencPreset), "?")}   (MapNvencPreset; engine.json Preset = fallback only)")
        sb.AppendLine($" actual    : {If(echo IsNot Nothing, echo.Preset, "(pending init)")}")
        sb.AppendLine()

        ' ══ Bitrate ══
        Dim reqBitrate As Integer = If(cur?.bitrate, 0)
        sb.AppendLine("== BITRATE (regime B payload — aspirational until NVENC RC lands) ==")
        sb.AppendLine($" requested : {If(reqBitrate > 0, reqBitrate.ToString() & " kbps", "(default 20000)")}   (config.json Recording.current.bitrate)")
        sb.AppendLine($" effective : {If(_settings IsNot Nothing AndAlso _settings.Bitrate > 0, (_settings.Bitrate \ 1000).ToString() & " kbps (" & _settings.Bitrate.ToString() & " bps)", "?")}")
        sb.AppendLine($" actual    : {If(_lastActualMbps > 0, _lastActualMbps.ToString("F1") & " Mbps (live, size/duration)", If(lastResult IsNot Nothing AndAlso lastResult.TotalVideoBytes > 0, "see last session", "(no session yet)"))}")
        sb.AppendLine()

        ' ══ Audio ══
        sb.AppendLine("== AUDIO (regime A — fresh per record) ==")
        Dim a As OverlayConfig.AudioSettings = If(_overlayConfig?.Audio, Nothing)
        If a IsNot Nothing Then
            sb.AppendLine($" requested : sys={a.SystemAudioEnabled} ({a.SystemAudioVolume * 100.0F:F0}%), mic={a.MicEnabled} ({a.MicVolume * 100.0F:F0}%), tracks={If(a.TrackMode = 1, "separate", "single")}, clock={a.AudioClockMode}")
        End If
        If _settings IsNot Nothing Then
            sb.AppendLine($" effective : sys={_settings.SystemAudioCapture} ({_settings.SystemAudioVolume * 100.0F:F0}%), mic={_settings.MicCapture} ({_settings.MicVolume * 100.0F:F0}%), clock={_settings.AudioClockMode}")
        End If
        If lastResult IsNot Nothing Then
            sb.AppendLine($" actual    : sysAudio={lastResult.AudioBytes} B, mic={lastResult.MicBytes} B, accountingOk={lastResult.AudioAccountingOk}, dropped={lastResult.AudioDroppedBytes}")
        Else
            sb.AppendLine(" actual    : (no session yet)")
        End If
        sb.AppendLine()

        ' ══ Output ══
        sb.AppendLine("== OUTPUT (dir decided by Overlay at record start) ==")
        Dim paths As OverlayConfig.PathSettings = If(_overlayConfig?.Paths, Nothing)
        If paths IsNot Nothing Then
            Dim outDir As String = paths.GalleryPath
            If String.IsNullOrEmpty(outDir) Then outDir = paths.SavePath
            sb.AppendLine($" requested : {If(String.IsNullOrEmpty(outDir), "(not set)", outDir)}")
        End If
        If _lastSessionConfig IsNot Nothing AndAlso Not String.IsNullOrEmpty(_lastSessionConfig.OutputPath) Then
            sb.AppendLine($" effective : {_lastSessionConfig.OutputPath}")
        End If
        If lastResult IsNot Nothing Then
            sb.AppendLine($" output    : pass={lastResult.Pass}, file={If(lastResult.FileExists, lastResult.FileSize.ToString() & " B", "MISSING")}, frames={lastResult.FramesEncoded}")
        End If

        Return sb.ToString()
    End Function
End Class