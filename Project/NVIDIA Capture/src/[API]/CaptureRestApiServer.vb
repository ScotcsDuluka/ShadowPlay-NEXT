' CaptureRestApiServer.vb — Phase 3 (§21.2): capture boundary REST ของ NvCapture.exe
'
' NvCapture.exe เป็น "เจ้าของการอัด" ตามสถาปัตยกรรม §20 — node JS boundary
' (NvShadowPlayAPI.js middleware, [NvCapture-boundary §21.2]) ยิงมาที่นี่:
'
'   GET  /health         → {"ok":true,"ready":...,"recording":...}
'   GET  /state          → {"ready","recording","enabled","elapsedSec","outputPath","lastFile","failReason"}
'   POST /record/start   → StartRecording ผ่าน HandleRecordingStart (engine path เดียวกับ TCP)
'   POST /record/stop    → HandleRecordingStop + เก็บ result.OutputPath
'   GET  /settings       → {"quality","resolution","framerate","bitrateBps"} จาก engine config จริง
'   POST /settings       → เขียน CaptureSettings + Save(config) (record ถัดไปใช้ค่าใหม่ — fresh reload)
'
' หลักการ: ไม่เปิดพอร์ตสาธารณะ (127.0.0.1 เท่านั้น) · fail-soft ทุก catch ·
' ไม่เดาสถานะ — รายงานจาก _recordingEngine/_recordingTask จริงเท่านั้น
Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

Partial Public Class UI_Engine

    Private ReadOnly Property RestApiPort As Integer
        Get
            Return CaptureIsolation.ApiPort
        End Get
    End Property
    Private _restApiListener As TcpListener
    Private _restApiThread As Thread
    Private _restApiRunning As Boolean
    Private _restLastFile As String = ""
    Private ReadOnly _restLock As New Object()
    Private ReadOnly _directRestCommandGate As New SemaphoreSlim(1, 1)
    Private _directRestSystemAudioRequested As Boolean = False
    Private _directRestMicrophoneRequested As Boolean = False

    ' Route selection is evaluated from unified config on demand, not by
    ' guessing from whether the DXGI encoder happened to initialize.
    Private Function RestConfiguredRoute() As CaptureBackendRoute.Decision
        Dim current As CaptureSettings = CaptureSettings.Load(_configPath)
        Return CaptureBackendRoute.Decide(
            OverlayConfig.GetEngineMode(), current.CaptureMethod, current.Encoder)
    End Function

    Private Function RestActiveRoute() As String
        If _nvRecording Then Return "InstalledNvidia"
        If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then Return "Duluka"
        If _legacyCaptureEngine IsNot Nothing AndAlso _legacyCaptureEngine.IsRecordingLifecycleActive Then Return "FFmpeg"
        Return ""
    End Function

    Public Sub StartCaptureRestApi()
        If _restApiRunning Then Return
        _restApiRunning = True
        _restApiListener = New TcpListener(IPAddress.Loopback, RestApiPort)
        Try
            _restApiListener.Start()
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] listener start failed: {ex.Message}")
            _restApiRunning = False
            Return
        End Try
        _restApiThread = New Thread(AddressOf RestApiLoop) With {.IsBackground = True}
        _restApiThread.Start()
        DebugLog($"[CaptureRestApi] listening on 127.0.0.1:{RestApiPort}")
    End Sub

    Private Sub RestApiLoop()
        While _restApiRunning
            Try
                Dim client = _restApiListener.AcceptTcpClient()
                Task.Run(Sub() ServeRestClient(client))
            Catch ex As Exception
                If _restApiRunning Then DebugLog($"[CaptureRestApi] accept: {ex.Message}")
            End Try
        End While
    End Sub

    Private Sub ServeRestClient(client As TcpClient)
        Try
            client.ReceiveTimeout = 3000 : client.SendTimeout = 3000
            Using c = client
                Dim stream = c.GetStream()
                Dim req = ReadHttpRequest(stream)
                If req Is Nothing Then Return
                Dim method = req("method").ToString().ToUpperInvariant()
                Dim path = req("path").ToString()
                Dim body = req("body").ToString()
                Dim respText As String = HandleRestRequest(method, path, body)
                Dim payload As Byte() = System.Text.Encoding.UTF8.GetBytes(respText)
                Dim head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK{vbCr}{vbLf}Content-Type: application/json{vbCr}{vbLf}Content-Length: {payload.Length}{vbCr}{vbLf}Connection: close{vbCr}{vbLf}{vbCr}{vbLf}")
                stream.Write(head, 0, head.Length)
                stream.Write(payload, 0, payload.Length)
                stream.Flush()
            End Using
        Catch
        End Try
    End Sub

    ' Shared bounded reader is regression-tested with fragmented TCP payloads.
    Private Function ReadHttpRequest(stream As NetworkStream) As Dictionary(Of String, String)
        Return CaptureHttpRequestReader.Read(stream)
    End Function

    ' The HTTP listener runs on ThreadPool threads while existing recording
    ' handlers own WinForms state. Marshal commands onto that same UI thread,
    ' otherwise REST start/stop races the event bus and touches UI controls.
    Private Function OnCaptureUiAsync(work As Func(Of Task(Of Boolean))) As Task(Of Boolean)
        Dim result As New TaskCompletionSource(Of Boolean)(
            TaskCreationOptions.RunContinuationsAsynchronously)
        Try
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then
                result.SetException(New InvalidOperationException("capture_ui_unavailable"))
            Else
                Me.BeginInvoke(New Action(
                    Async Sub()
                        Try
                            result.TrySetResult(Await work())
                        Catch ex As Exception
                            result.TrySetException(ex)
                        End Try
                    End Sub))
            End If
        Catch ex As Exception
            result.TrySetException(ex)
        End Try
        Return result.Task
    End Function

    Private Async Function StartRestOnUi(route As CaptureBackendRoute.Decision,
                                          outputPath As String) As Task(Of Boolean)
        If RestActiveRoute().Length > 0 Then Return False
        ' Handler reloads the effective CaptureSettings from config.json.
        ' Snapshot audio intent AFTER that reload, not from a stale form
        ' state left over from process startup.
        If Not NextRecordingConfig.IsSafeRecordingOutputPath(outputPath) Then
            Throw New ArgumentException("invalid_output_path")
        End If
        Select Case route.Kind
            Case "Duluka"
                If Not _engineReady OrElse _recordingEngine Is Nothing OrElse Not _useNewEngine Then
                    Throw New InvalidOperationException("native_engine_not_ready")
                End If
                Await HandleRecordingStart(outputPath, "", LoadOscSnapshot())
                If _recordingTask Is Nothing Then Return False
                _directRestSystemAudioRequested = _settings IsNot Nothing AndAlso _settings.SystemAudioCapture
                _directRestMicrophoneRequested = _settings IsNot Nothing AndAlso _settings.MicCapture
                ' The background Task can exist well BEFORE CaptureSession.Run
                ' owns an actual DXGI/NVENC session (NVENC rebuild is expensive).
                ' Returning started=true merely for a queued task caused a 2s
                ' client-side record window to produce only 0.63s of video.
                For attempt As Integer = 1 To 100
                    If _recordingTask.IsCompleted Then Return False
                    Dim truth = _recordingEngine.GetStatus()
                    If Not String.IsNullOrWhiteSpace(truth.CurrentSessionId) Then
                        Return True
                    End If
                    Await Task.Delay(100)
                Next
                Throw New TimeoutException("native_session_start_pending_check_state")
            Case "FFmpeg"
                Await HandleEngineRecordStart(outputPath, "")
                If _legacyCaptureEngine Is Nothing OrElse Not _legacyCaptureEngine.IsRecording Then Return False
                _directRestSystemAudioRequested = _settings IsNot Nothing AndAlso _settings.SystemAudioCapture
                _directRestMicrophoneRequested = _settings IsNot Nothing AndAlso _settings.MicCapture
                Return True
            Case Else
                Throw New NotSupportedException("direct_http_backend_not_supported")
        End Select
    End Function

    Private Async Function StopRestOnUi() As Task(Of Boolean)
        Dim owner As String = RestActiveRoute()
        Select Case owner
            Case "Duluka"
                ' HandleRecordingStop clears _recordingTask after awaiting it.
                ' Retain this session's task locally so REST returns the
                ' committed SessionResult instead of treating a cleared field
                ' as failure after a successful MP4 finalize.
                Dim ownerTask As Task(Of DulukaEngine.Recording.SessionResult) = _recordingTask
                If ownerTask Is Nothing Then Return False
                Await HandleRecordingStop("")
                If Not ownerTask.IsCompletedSuccessfully Then Return False
                Try
                    Dim completed = Await ownerTask
                    Return completed IsNot Nothing AndAlso completed.Pass AndAlso
                           Not String.IsNullOrWhiteSpace(completed.OutputPath) AndAlso
                           IO.File.Exists(completed.OutputPath)
                Catch
                    Return False
                End Try
            Case "FFmpeg"
                If _legacyCaptureEngine Is Nothing OrElse Not _legacyCaptureEngine.IsRecording Then Return False
                Await HandleEngineRecordStop("")
                Return _legacyCaptureEngine IsNot Nothing AndAlso
                       Not _legacyCaptureEngine.IsRecordingLifecycleActive AndAlso
                       IO.File.Exists(_legacyCaptureEngine.OutputFile)
            Case Else
                Return False
        End Select
    End Function

    Private Function HandleRestRequest(method As String, path As String, body As String) As String
        Try
            If path = "/health" Then
                Return $"{{""ok"":true,""engine"":""NvCapture"",""ready"":{_engineReady.ToString().ToLowerInvariant()},""recording"":{RestRecording().ToString().ToLowerInvariant()}}}"
            End If

            ' Report both requested route and actual session owner. A codec
            ' is never advertised as active just because a config selected it.
            If path = "/engines" AndAlso method = "GET" Then
                Dim requested = RestConfiguredRoute()
                Dim ffmpegSettings As CaptureSettings = CaptureSettings.Load(_configPath)
                Dim ffmpeg = FFmpegRuntimeProbe.CachedOrStart(ffmpegSettings.FFmpegPath)
                Dim nativeCodec As String = ""
                If _engineReady AndAlso _recordingEngine IsNot Nothing AndAlso
                   _recordingEngine.StartupEcho IsNot Nothing Then
                    nativeCodec = If(_recordingEngine.StartupEcho.CodecKey, "")
                End If
                Return JsonSerializer.Serialize(New With {
                    .schemaVersion = 2,
                    .readOnly = True,
                    .configured = New With {
                        .backend = requested.Kind,
                        .captureApi = requested.CaptureApi,
                        .encoder = requested.Encoder,
                        .allowed = requested.Allowed,
                        .error = requested.ErrorCode
                    },
                    .actualSessionBackend = RestActiveRoute(),
                    .nativeRuntimeReady = _engineReady,
                    .nativeRuntimeCodec = nativeCodec,
                    .ffmpegBinary = New With {
                        .binaryRunnable = ffmpeg.BinaryRunnable,
                        .version = ffmpeg.Version,
                        .captureApis = ffmpeg.CaptureApis,
                        .encoders = ffmpeg.Encoders,
                        .reason = ffmpeg.Reason,
                        .hardwareEncoderVerified = False
                    },
                    .recording = RestRecording(),
                    .otherProvidersReady = False
                })
            End If

            If path = "/state" Then
                Dim owner As String = RestActiveRoute()
                Dim elapsed As Integer = 0
                Dim outPath As String = ""
                If owner = "Duluka" Then
                    If _newEngineSessionClock.IsRunning Then
                        elapsed = CInt(_newEngineSessionClock.Elapsed.TotalSeconds)
                    End If
                    outPath = If(_newEngineSessionOutputPath, "")
                ElseIf owner = "FFmpeg" AndAlso _legacyCaptureEngine IsNot Nothing Then
                    elapsed = CInt(_legacyCaptureEngine.RecordingDuration.TotalSeconds)
                    outPath = If(_legacyCaptureEngine.OutputFile, "")
                ElseIf owner = "InstalledNvidia" Then
                    elapsed = _nvRecorder.ElapsedSeconds
                End If
                Dim active As Boolean = owner.Length > 0
                Return JsonSerializer.Serialize(New With {
                    .ready = _engineReady,
                    .nativeRuntimeReady = _engineReady,
                    .failReason = If(_engineInitFailReason, ""),
                    .recording = active,
                    .enabled = active,
                    .actualSessionBackend = owner,
                    .elapsedSec = elapsed,
                    .outputPath = outPath,
                    .lastFile = _restLastFile
                })
            End If

            If path = "/record/start" AndAlso method = "POST" Then
                If Not _directRestCommandGate.Wait(0) Then
                    Return "{""started"":false,""error"":""direct_rest_command_busy""}"
                End If
                Try
                Dim requested = RestConfiguredRoute()
                If Not requested.Allowed Then
                    Return JsonSerializer.Serialize(New With {.started = False, .error = requested.ErrorCode})
                End If
                If requested.Kind <> "Duluka" AndAlso requested.Kind <> "FFmpeg" Then
                    Return "{""started"":false,""error"":""direct_http_backend_not_supported""}"
                End If
                If RestActiveRoute().Length > 0 Then
                    Return "{""started"":false,""error"":""already_recording""}"
                End If
                If requested.Kind = "FFmpeg" Then
                    Dim effective As CaptureSettings = CaptureSettings.Load(_configPath)
                    Dim capabilities As FFmpegRuntimeProbe.Result = FFmpegRuntimeProbe.Inspect(effective.FFmpegPath)
                    If Not capabilities.Supports(requested) Then
                        Return JsonSerializer.Serialize(New With {
                            .started = False, .error = "ffmpeg_capability_unavailable",
                            .probeReason = capabilities.Reason,
                            .captureApi = requested.CaptureApi,
                            .encoder = FFmpegRuntimeProbe.CanonicalEncoder(requested.Encoder)
                        })
                    End If
                End If
                Dim outPath As String = ResolveRecordOutputPath()
                Dim work As Task(Of Boolean) = OnCaptureUiAsync(Function() StartRestOnUi(requested, outPath))
                If Not work.Wait(TimeSpan.FromSeconds(15)) Then
                    Return "{""started"":false,""error"":""start_status_pending_timeout""}"
                End If
                Dim started As Boolean = work.GetAwaiter().GetResult()
                Return JsonSerializer.Serialize(New With {
                    .started = started,
                    .backend = If(started, RestActiveRoute(), ""),
                    .outputPath = If(started, If(requested.Kind = "FFmpeg",
                        If(_legacyCaptureEngine?.OutputFile, ""), _newEngineSessionOutputPath), ""),
                    .error = If(started, "", "start_failed")
                })
                Finally
                    _directRestCommandGate.Release()
                End Try
            End If

            If path = "/record/stop" AndAlso method = "POST" Then
                If Not _directRestCommandGate.Wait(0) Then
                    Return "{""stopped"":false,""error"":""direct_rest_command_busy""}"
                End If
                Try
                Dim owner As String = RestActiveRoute()
                If owner = "" Then Return "{""stopped"":false,""error"":""not_recording""}"
                If owner = "InstalledNvidia" Then
                    Return "{""stopped"":false,""error"":""installed_nvidia_uses_event_bus""}"
                End If
                Dim outputPath As String = If(owner = "FFmpeg",
                    If(_legacyCaptureEngine?.OutputFile, ""), _newEngineSessionOutputPath)
                Dim work As Task(Of Boolean) = OnCaptureUiAsync(Function() StopRestOnUi())
                If Not work.Wait(TimeSpan.FromSeconds(30)) Then
                    Return "{""stopped"":false,""error"":""stop_status_pending_timeout""}"
                End If
                Dim stopped As Boolean = work.GetAwaiter().GetResult()
                If stopped Then _restLastFile = outputPath
                Dim mediaEvidence As CaptureMediaInspector.Evidence = Nothing
                If stopped Then
                    Dim probePath = ""
                    Try
                        If _settings IsNot Nothing AndAlso
                           Not String.IsNullOrWhiteSpace(_settings.FFmpegPath) Then
                            probePath = IO.Path.Combine(IO.Path.GetDirectoryName(_settings.FFmpegPath), "ffprobe.exe")
                        End If
                    Catch
                    End Try
                    If Not File.Exists(probePath) Then probePath = AppLayout.P("FFmpeg", "ffprobe.exe")
                    mediaEvidence = CaptureMediaInspector.Inspect(outputPath, probePath)
                End If
                Return JsonSerializer.Serialize(New With {
                    .stopped = stopped, .backend = owner,
                    .file = If(stopped, outputPath, ""),
                    .systemAudioRequestedAtStart = _directRestSystemAudioRequested,
                    .microphoneRequestedAtStart = _directRestMicrophoneRequested,
                    .media = mediaEvidence,
                    .error = If(stopped, "", "stop_or_finalize_failed")
                })
                Finally
                    _directRestCommandGate.Release()
                End Try
            End If

            If path = "/settings" AndAlso method = "GET" Then
                Return OscSettingsJson()
            End If

            If path = "/settings" AndAlso method = "POST" Then
                ApplyOscSettings(body)
                DebugLog("[CaptureRestApi] OSC settings stored (applied to next session via snapshot)")
                Return OscSettingsJson()
            End If

            Return "{""error"":""not_found""}"
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] {method} {path}: {ex.Message}")
            Return "{""error"":""internal""}"
        End Try
    End Function

    ' ── truth: any real owner is active (not the configured selector). ──
    Private Function RestRecording() As Boolean
        Return RestActiveRoute().Length > 0
    End Function

    ' ── output path: videos จาก genuine /RecordPaths → fallback OutputDirectory/MyVideos ──
    Private Function ResolveRecordOutputPath() As String
        If CaptureIsolation.Requested Then
            Dim testDir = CaptureIsolation.RecordingDirectory(AppLayout.Dir)
            Return Path.Combine(testDir, "NvCapture_sandbox_" &
                DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") & ".mp4")
        End If
        Dim dir As String = ""
        Try
            Dim req = DirectCast(Net.WebRequest.Create("http://127.0.0.1:59001/ShadowPlay/v.1.0/RecordPaths"), Net.HttpWebRequest)
            req.Method = "GET" : req.Timeout = 2500 : req.Proxy = Nothing
            Using rsp = DirectCast(req.GetResponse(), Net.HttpWebResponse)
                Dim body As String = ""
                Using sr As New System.IO.StreamReader(CType(rsp.GetResponseStream(), System.IO.Stream))
                    body = sr.ReadToEnd()
                End Using
                Using doc = System.Text.Json.JsonDocument.Parse(body)
                    Dim p As System.Text.Json.JsonElement = Nothing
                    If doc.RootElement.TryGetProperty("videos", p) Then dir = p.GetString()
                End Using
            End Using
        Catch
        End Try
        If String.IsNullOrWhiteSpace(dir) OrElse Not Directory.Exists(dir) Then
            dir = _settings.OutputDirectory
            If String.IsNullOrWhiteSpace(dir) OrElse Not Directory.Exists(dir) Then dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        End If
        Try : Directory.CreateDirectory(dir) : Catch : End Try
        Return Path.Combine(dir, "NvCapture_" & DateTime.Now.ToString("yyyy-MM-dd_HHmmss") & ".mp4")
    End Function

    ' ── OSC settings store (Config\capture-osc.json) — defaults = genuine harvest (VeryGood=50Mbps, 8 res, fps 60/30) ──
    Private Shared ReadOnly OscStoreLock As New Object()

    Private Function OscStorePath() As String
        Return AppLayout.P("Config", "capture-osc.json")
    End Function

    Private Function LoadOscSettings() As Dictionary(Of String, String)
        SyncLock OscStoreLock
            Dim d As New Dictionary(Of String, String) From {
                {"quality", "VeryGood"}, {"resolution", "In-game"}, {"framerate", "60"}, {"bitrateBps", "50000000"}
            }
            Try
                Using doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(OscStorePath()))
                    Dim r = doc.RootElement
                    For Each k In New String() {"quality", "resolution", "framerate", "bitrateBps"}
                        If r.ValueKind = System.Text.Json.JsonValueKind.Object AndAlso r.TryGetProperty(k, pr) Then
                            d(k) = If(pr.ValueKind = System.Text.Json.JsonValueKind.Number, pr.GetRawText(), pr.GetString())
                        End If
                    Next
                End Using
            Catch
            End Try
            Return d
        End SyncLock
    End Function

    Private Sub SaveOscSettings(d As Dictionary(Of String, String))
        SyncLock OscStoreLock
            Dim j = "{""quality"":" & JStr(d("quality")) & ",""resolution"":" & JStr(d("resolution")) &
                    ",""framerate"":" & d("framerate") & ",""bitrateBps"":" & d("bitrateBps") & "}"
            File.WriteAllText(OscStorePath(), j)
        End SyncLock
    End Sub

    Private Function OscSettingsJson() As String
        Dim d = LoadOscSettings()
        Return "{""quality"":" & JStr(d("quality")) & ",""resolution"":" & JStr(d("resolution")) &
               ",""framerate"":" & d("framerate") & ",""bitrateBps"":" & d("bitrateBps") & "}"
    End Function

    Private Sub ApplyOscSettings(body As String)
        Try
            Dim d = LoadOscSettings()
            Using doc = System.Text.Json.JsonDocument.Parse(If(body, "{}"))
                Dim r = doc.RootElement
                If r.ValueKind = System.Text.Json.JsonValueKind.Object Then
                    Dim pr As System.Text.Json.JsonElement = Nothing
                    If r.TryGetProperty("quality", pr) Then d("quality") = pr.GetString()
                    If r.TryGetProperty("resolution", pr) Then d("resolution") = pr.GetString()
                    If r.TryGetProperty("framerate", pr) Then d("framerate") = pr.GetInt32().ToString()
                    If r.TryGetProperty("bitrateBps", pr) Then d("bitrateBps") = pr.GetInt64().ToString()
                End If
            End Using
            SaveOscSettings(d)
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] osc settings parse: {ex.Message}")
        End Try
    End Sub

    ' Snapshot ที่ override session config จริง (NextRecordingConfig seam) — "engine config จริง" ของ OSC plane
    Private Function LoadOscSnapshot() As RecordSettingsSnapshot
        Dim d = LoadOscSettings()
        Dim snap As New RecordSettingsSnapshot With {
            .Quality = d("quality"), .Resolution = d("resolution"),
            .Framerate = CInt(Val(d("framerate"))), .BitrateBps = CInt(Val(d("bitrateBps")))
        }
        If d("resolution") = "In-game" Then
            snap.NativeResolution = True
        Else
            snap.NativeResolution = False
            Select Case d("resolution")
                Case "2160p 4K" : snap.Width = 3840 : snap.Height = 2160
                Case "1440p HD" : snap.Width = 2560 : snap.Height = 1440
                Case "1080p HD" : snap.Width = 1920 : snap.Height = 1080
                Case "720p HD" : snap.Width = 1280 : snap.Height = 720
                Case "480p" : snap.Width = 854 : snap.Height = 480
                Case "360p" : snap.Width = 640 : snap.Height = 360
                Case "240p" : snap.Width = 426 : snap.Height = 240
                Case Else : snap.NativeResolution = True
            End Select
        End If
        DebugLog($"[CaptureRestApi] snapshot: q={snap.Quality} res={snap.Resolution} fps={snap.Framerate} bps={snap.BitrateBps}")
        Return snap
    End Function

    Private Function JStr(v As String) As String
        If v Is Nothing Then Return """"""
        Return """" & v.Replace("\", "\\").Replace("""", "\""").Replace(vbCr, "").Replace(vbLf, "") & """"
    End Function
End Class
