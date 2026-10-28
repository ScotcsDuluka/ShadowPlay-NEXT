Option Infer On

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
    Private _directRestSeparateAudioTracksRequested As Boolean = False
    Private _directRestRequestedOutputPath As String = ""
    Private _restLastRecordingResult As Object
    Private _restRequestedConfiguration As Object
    Private _restObservedLegacyEngine As LegacyCaptureEngine
    Private _restLegacyFrames As Long
    Private _restLegacyFrameHandler As Action(Of Long)

    ' Route selection is evaluated from unified config on demand, not by
    ' guessing from whether the DXGI encoder happened to initialize.
    Private Function RestConfiguredRoute() As CaptureBackendRoute.Decision
        Dim current As CaptureSettings = CaptureSettings.Load(_configPath)
        Return CaptureBackendRoute.Decide(
            OverlayConfig.GetEngineMode(), current.CaptureMethod, current.Encoder)
    End Function

    Private Function RestLifecycleOwner() As String
        If _nvRecording Then Return "InstalledNvidia"
        If _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted Then Return "Duluka"
        If _legacyCaptureEngine IsNot Nothing AndAlso _legacyCaptureEngine.IsRecordingLifecycleBusy Then Return "FFmpeg"
        Return ""
    End Function

    Private Function RestTruth() As CaptureRuntimeTruth
        Dim legacy = _legacyCaptureEngine
        SyncLock _restLock
            If Not Object.ReferenceEquals(legacy, _restObservedLegacyEngine) Then
                If _restObservedLegacyEngine IsNot Nothing AndAlso _restLegacyFrameHandler IsNot Nothing Then
                    RemoveHandler _restObservedLegacyEngine.FrameCaptured, _restLegacyFrameHandler
                End If
                _restObservedLegacyEngine = legacy
                Interlocked.Exchange(_restLegacyFrames, 0)
                If legacy IsNot Nothing Then
                    _restLegacyFrameHandler = Sub(frames)
                        If Object.ReferenceEquals(_restObservedLegacyEngine, legacy) Then
                            Interlocked.Exchange(_restLegacyFrames, Math.Max(0, frames))
                        End If
                    End Sub
                    AddHandler legacy.FrameCaptured, _restLegacyFrameHandler
                End If
            End If
        End SyncLock
        Dim native = _recordingEngine?.GetStatus()
        Return CaptureRuntimeTruth.Observe(
            _recordingTask IsNot Nothing AndAlso Not _recordingTask.IsCompleted,
            If(native?.CurrentSessionId, ""), If(native?.SourceLifecycleState, ""),
            If(native Is Nothing, 0, native.RealFramesCapturedThisSession),
            If(native Is Nothing, 0, native.FramesEncodedThisSession),
            legacy IsNot Nothing AndAlso legacy.IsRecordingLifecycleBusy,
            legacy IsNot Nothing AndAlso legacy.IsRecording,
            Interlocked.Read(_restLegacyFrames), _nvRecording)
    End Function

    Private Function RestActiveRoute() As String
        Return RestTruth().actualSessionBackend
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
                Dim authorization = CaptureRestAuthorization.Authorize(method, req, RestApiPort,
                    If(method = "POST", CaptureRestAuthorization.ReadProtectedToken(), ""))
                Dim respText As String
                Dim status As String = "200 OK"
                If authorization <> 0 Then
                    status = If(authorization = 503, "503 Service Unavailable",
                                If(authorization = 405, "405 Method Not Allowed", "403 Forbidden"))
                    respText = JsonSerializer.Serialize(New With {
                        .error = If(authorization = 503, "capture_write_authorization_unavailable",
                                    If(authorization = 405, "method_not_allowed", "capture_request_denied"))})
                Else
                    respText = HandleRestRequest(method, path, body)
                End If
                Dim payload As Byte() = System.Text.Encoding.UTF8.GetBytes(respText)
                Dim head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}{vbCr}{vbLf}Content-Type: application/json{vbCr}{vbLf}Cache-Control: no-store{vbCr}{vbLf}Content-Length: {payload.Length}{vbCr}{vbLf}Connection: close{vbCr}{vbLf}{vbCr}{vbLf}")
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
        If RestRecording() Then Return False
        Dim current = RestConfiguredRoute()
        If Not current.Allowed OrElse current.Kind <> route.Kind OrElse
           current.CaptureApi <> route.CaptureApi OrElse current.Encoder <> route.Encoder Then
            Throw New InvalidOperationException("capture_configuration_changed_before_start")
        End If
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
                _directRestSeparateAudioTracksRequested = _settings IsNot Nothing AndAlso
                    _settings.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SeparateTrack
                _directRestRequestedOutputPath = outputPath
                ' The background Task can exist well BEFORE CaptureSession.Run
                ' owns an actual DXGI/NVENC session (NVENC rebuild is expensive).
                ' Returning started=true merely for a queued task caused a 2s
                ' client-side record window to produce only 0.63s of video.
                For attempt As Integer = 1 To 100
                    If _recordingTask.IsCompleted Then Return False
                    Dim truth = _recordingEngine.GetStatus()
                    If Not String.IsNullOrWhiteSpace(truth.CurrentSessionId) AndAlso
                       truth.RealFramesCapturedThisSession > 0 AndAlso truth.FramesEncodedThisSession > 0 Then
                        Return True
                    End If
                    Await Task.Delay(100)
                Next
                Throw New TimeoutException("native_session_start_pending_check_state")
            Case "FFmpeg"
                Await HandleEngineRecordStart(outputPath, "", LoadOscSnapshot())
                If _legacyCaptureEngine Is Nothing OrElse Not _legacyCaptureEngine.IsRecording Then Return False
                _directRestSystemAudioRequested = _settings IsNot Nothing AndAlso _settings.SystemAudioCapture
                _directRestMicrophoneRequested = _settings IsNot Nothing AndAlso _settings.MicCapture
                _directRestSeparateAudioTracksRequested = _settings IsNot Nothing AndAlso
                    _settings.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SeparateTrack
                _directRestRequestedOutputPath = outputPath
                For attempt As Integer = 1 To 100
                    If RestTruth().isCapturing Then Return True
                    If _legacyCaptureEngine Is Nothing OrElse Not _legacyCaptureEngine.IsRecording Then Return False
                    Await Task.Delay(100)
                Next
                Throw New TimeoutException("ffmpeg_frame_start_pending_check_state")
            Case Else
                Throw New NotSupportedException("direct_http_backend_not_supported")
        End Select
    End Function

    Private Async Function StopRestOnUi() As Task(Of Boolean)
        Dim owner As String = RestLifecycleOwner()
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
                    Return completed IsNot Nothing
                Catch
                    Return False
                End Try
            Case "FFmpeg"
                If _legacyCaptureEngine Is Nothing OrElse Not _legacyCaptureEngine.IsRecording Then Return False
                Await HandleEngineRecordStop("")
                Return _legacyCaptureEngine IsNot Nothing AndAlso
                       Not _legacyCaptureEngine.IsRecordingLifecycleBusy
            Case Else
                Return False
        End Select
    End Function

    Private Function HandleRestRequest(method As String, path As String, body As String) As String
        Try
            If path = "/health" AndAlso method = "GET" Then
                Dim truth = RestTruth()
                Return JsonSerializer.Serialize(New With {
                    .ok = True, .engine = "NvCapture", .ready = _engineReady,
                    .recording = truth.busy, .busy = truth.busy,
                    .isCapturing = truth.isCapturing, .runtimeTruth = truth})
            End If

            ' Hardware verification is explicit: GET never performs GPU work.
            ' Test source is one synthetic color frame; no screen or file capture.
            If path = "/encoders/verify" AndAlso method = "POST" Then
                If RestRecording() Then Return "{""started"":false,""reason"":""recording_active""}"
                Dim probeSettings As CaptureSettings = CaptureSettings.Load(_configPath)
                Dim advertised = FFmpegRuntimeProbe.CachedOrStart(probeSettings.FFmpegPath)
                If Not advertised.BinaryRunnable Then
                    Return JsonSerializer.Serialize(New With {.started = False, .reason = "ffmpeg_probe_not_ready"})
                End If
                Dim outcome As String = FFmpegNvencVerification.Start(probeSettings.FFmpegPath, advertised.Encoders)
                Return JsonSerializer.Serialize(New With {.started = outcome = "started", .reason = outcome})
            End If

            ' Report both requested route and actual session owner. A codec
            ' is never advertised as active just because a config selected it.
            If (path = "/engines" OrElse path = "/capabilities") AndAlso method = "GET" Then
                Dim requested = RestConfiguredRoute()
                Dim ffmpegSettings As CaptureSettings = CaptureSettings.Load(_configPath)
                Dim ffmpeg = FFmpegRuntimeProbe.CachedOrStart(ffmpegSettings.FFmpegPath)
                Dim nativeCodec As String = ""
                Dim truth = RestTruth()
                If _engineReady AndAlso _recordingEngine IsNot Nothing AndAlso
                   _recordingEngine.StartupEcho IsNot Nothing Then
                    nativeCodec = If(_recordingEngine.StartupEcho.CodecKey, "")
                End If
                Return JsonSerializer.Serialize(New With {
                    .schemaVersion = 2,
                    .truthSchemaVersion = 1,
                    .readOnly = True,
                    .configured = New With {
                        .backend = requested.Kind,
                        .captureApi = requested.CaptureApi,
                        .encoder = requested.Encoder,
                        .allowed = requested.Allowed,
                        .error = requested.ErrorCode
                    },
                    .requestedConfiguration = _restRequestedConfiguration,
                    .requestedConfigurationSource = "last_direct_rest_start_or_unobserved",
                    .savedConfiguration = New With {
                        .engine = OverlayConfig.GetEngineMode(),
                        .captureApi = ffmpegSettings.CaptureMethod, .encoder = ffmpegSettings.Encoder,
                        .restOverrides = If(File.Exists(OscStorePath()), LoadOscSettings(), Nothing)
                    },
                    .selectedEngine = If(requested.Allowed, requested.Kind, ""),
                    .actualSessionBackend = truth.actualSessionBackend,
                    .actualRunningEngine = truth.actualRunningEngine,
                    .runtimeTruth = truth,
                    .verification = New With {
                        .hardwareVerified = False, .mediaVerified = False,
                        .scope = "current_session_unverified",
                        .nativeDeviceIdentity = _recordingEngine?.GetStatus()?.VideoIdentity
                    },
                    .availableCapabilities = New Object() {
                        New With {.engine = "Duluka", .implemented = True, .available = _engineReady,
                                  .reason = If(_engineReady, "live_media_unverified", "native_runtime_not_ready")},
                        New With {.engine = "FFmpeg", .implemented = True, .available = ffmpeg.BinaryRunnable,
                                  .reason = "binary_metadata_only_hardware_and_media_unverified"},
                        New With {.engine = "OBS", .implemented = False, .available = False, .reason = "obs_capture_not_implemented"},
                        New With {.engine = "NVIDIA", .implemented = False, .available = False, .reason = "external_recorder_status_unverified"}
                    },
                    .nativeRuntimeReady = _engineReady,
                    .nativeRuntimeCodec = nativeCodec,
                    .ffmpegBinary = New With {
                        .binaryRunnable = ffmpeg.BinaryRunnable,
                        .version = ffmpeg.Version,
                        .captureApis = ffmpeg.CaptureApis,
                        .encoders = ffmpeg.Encoders,
                        .reason = ffmpeg.Reason,
                        .nvencSyntheticValidation = FFmpegNvencVerification.Snapshot(ffmpegSettings.FFmpegPath),
                        .hardwareEncoderVerified = False
                    },
                    .recording = truth.busy,
                    .otherProvidersReady = False
                })
            End If

            If path = "/state" AndAlso method = "GET" Then
                Dim truth = RestTruth()
                Dim owner As String = truth.lifecycleOwner
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
                Dim active As Boolean = truth.busy
                Return JsonSerializer.Serialize(New With {
                    .ready = _engineReady,
                    .nativeRuntimeReady = _engineReady,
                    .failReason = If(_engineInitFailReason, ""),
                    .recording = active,
                    .enabled = active,
                    .actualSessionBackend = truth.actualSessionBackend,
                    .actualRunningEngine = truth.actualRunningEngine,
                    .busy = truth.busy, .isCapturing = truth.isCapturing,
                    .runtimeTruth = truth,
                    .elapsedSec = elapsed,
                    .outputPath = outPath,
                    .lastFile = _restLastFile,
                    .lastRecordingResult = _restLastRecordingResult
                })
            End If

            If path = "/record/start" AndAlso method = "POST" Then
                If Not _directRestCommandGate.Wait(0) Then
                    Return "{""started"":false,""error"":""direct_rest_command_busy""}"
                End If
                Try
                Dim requested = RestConfiguredRoute()
                Dim savedSettings = CaptureSettings.Load(_configPath)
                _restRequestedConfiguration = New With {
                    .engine = OverlayConfig.GetEngineMode(),
                    .captureApi = savedSettings.CaptureMethod, .encoder = savedSettings.Encoder,
                    .restOverrides = If(File.Exists(OscStorePath()), LoadOscSettings(), Nothing)
                }
                If Not requested.Allowed Then
                    Return JsonSerializer.Serialize(New With {.started = False, .error = requested.ErrorCode})
                End If
                If requested.Kind <> "Duluka" AndAlso requested.Kind <> "FFmpeg" Then
                    Return "{""started"":false,""error"":""direct_http_backend_not_supported""}"
                End If
                If RestRecording() Then
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
                Dim owner As String = RestLifecycleOwner()
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
                Dim mediaEvidence As CaptureMediaInspector.Evidence = Nothing
                Dim nativeResult = If(owner = "Duluka", _recordingEngine?.GetStatus()?.LastSessionResult, Nothing)
                Dim legacyCompletion = _legacyCaptureEngine?.LastCompletion
                Dim ownerPassed = stopped AndAlso If(owner = "Duluka", nativeResult IsNot Nothing AndAlso nativeResult.Pass,
                    legacyCompletion IsNot Nothing AndAlso legacyCompletion.Succeeded AndAlso
                    String.Equals(legacyCompletion.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
                If File.Exists(outputPath) Then
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
                    ' Full decode is bounded to short acceptance recordings.
                    ' Longer files remain explicitly pending; a packet scan is not full decode.
                    If mediaEvidence.Verified AndAlso mediaEvidence.DurationSec.HasValue AndAlso
                       mediaEvidence.DurationSec.Value <= 30 Then
                        Dim ffmpegPath = If(_settings?.FFmpegPath, AppLayout.P("FFmpeg", "ffmpeg.exe"))
                        mediaEvidence = CaptureMediaInspector.InspectWithVideoDecode(outputPath, probePath, ffmpegPath)
                    End If
                End If
                Dim mediaVerified = mediaEvidence IsNot Nothing AndAlso mediaEvidence.Verified AndAlso
                    ((mediaEvidence.VideoDecodeChecked AndAlso mediaEvidence.VideoDecodeVerified) OrElse
                     (nativeResult IsNot Nothing AndAlso nativeResult.FullDecodeVerified))
                Dim audioRequestKnown = String.Equals(outputPath, _directRestRequestedOutputPath, StringComparison.OrdinalIgnoreCase)
                Dim expectedAudioStreams = If(_directRestSeparateAudioTracksRequested,
                    If(_directRestSystemAudioRequested, 1, 0) + If(_directRestMicrophoneRequested, 1, 0),
                    If(_directRestSystemAudioRequested OrElse _directRestMicrophoneRequested, 1, 0))
                Dim audioPolicyVerified = audioRequestKnown AndAlso mediaEvidence IsNot Nothing AndAlso
                    mediaEvidence.AudioStreams = expectedAudioStreams
                Dim saved = ownerPassed AndAlso mediaVerified AndAlso audioPolicyVerified
                If saved Then _restLastFile = outputPath
                Dim finalization = If(saved, "Saved", If(File.Exists(outputPath), "Partial", "Failed"))
                If nativeResult IsNot Nothing AndAlso nativeResult.FinalizationState = "Cancelled" Then finalization = "Cancelled"
                Dim response = New With {
                    .stopped = stopped, .backend = owner,
                    .file = If(File.Exists(outputPath), outputPath, ""),
                    .saved = saved, .finalizationState = finalization,
                    .hardwareVerified = False, .mediaVerified = mediaVerified,
                    .audioPolicyVerified = audioPolicyVerified, .audioRequestKnown = audioRequestKnown,
                    .expectedAudioStreams = If(audioRequestKnown, CType(expectedAudioStreams, Integer?), Nothing),
                    .verificationState = If(saved, "full_video_decode_and_audio_stream_policy",
                        If(mediaEvidence IsNot Nothing AndAlso mediaEvidence.Verified AndAlso Not mediaVerified,
                           "full_decode_pending", "media_or_session_failed")),
                    .systemAudioRequestedAtStart = _directRestSystemAudioRequested,
                    .microphoneRequestedAtStart = _directRestMicrophoneRequested,
                    .media = mediaEvidence,
                    .nativeSessionResult = nativeResult,
                    .legacyCompletion = If(owner = "FFmpeg", legacyCompletion, Nothing),
                    .error = If(saved, "", If(stopped, "recording_not_verified_saved", "stop_or_finalize_failed"))
                }
                _restLastRecordingResult = response
                Return JsonSerializer.Serialize(response)
                Finally
                    _directRestCommandGate.Release()
                End Try
            End If

            ' Screenshot / Record / Replay must share ONE actual display
            ' selection owned by NvCapture. Container and NodeAPI only relay.
            If path = "/displays" AndAlso method = "GET" Then
                Dim displays = CaptureDisplayCatalog.Enumerate()
                Dim selected As CaptureDisplayCatalog.Display = Nothing
                Dim selectionError As String = ""
                Try
                    selected = CaptureDisplayCatalog.Resolve(AppLayout.Dir, displays)
                Catch ex As Exception
                    selectionError = ex.Message
                End Try
                Return JsonSerializer.Serialize(New With {
                    .schemaVersion = 1, .readOnly = True, .source = "NvCapture",
                    .displays = displays,
                    .selected = selected,
                    .selectionValid = selected IsNot Nothing,
                    .error = selectionError,
                    .appliesOnEngineRebuild = True,
                    .recording = RestRecording()
                })
            End If

            If path = "/display/select" AndAlso method = "POST" Then
                If RestRecording() OrElse _engineReconfiguring Then
                    Return "{""saved"":false,""error"":""capture_busy""}"
                End If
                Try
                    Using parsed = JsonDocument.Parse(body)
                        Dim root = parsed.RootElement
                        Dim confirmed As JsonElement = Nothing
                        Dim idx As JsonElement = Nothing
                        Dim low As JsonElement = Nothing
                        Dim high As JsonElement = Nothing
                        If root.ValueKind <> JsonValueKind.Object OrElse
                           Not root.TryGetProperty("confirmed", confirmed) OrElse
                           confirmed.ValueKind <> JsonValueKind.True OrElse
                           Not root.TryGetProperty("outputIndex", idx) OrElse
                           Not root.TryGetProperty("adapterLuidLow", low) OrElse
                           Not root.TryGetProperty("adapterLuidHigh", high) Then
                            Return "{""saved"":false,""error"":""invalid_display_selection_request""}"
                        End If
                        Dim candidate As New CaptureDisplayCatalog.Choice With {
                            .OutputIndex = idx.GetInt32(),
                            .AdapterLuidLow = low.GetUInt32(),
                            .AdapterLuidHigh = high.GetInt32()
                        }
                        Dim display = CaptureDisplayCatalog.SaveSelection(
                            AppLayout.Dir, candidate, CaptureDisplayCatalog.Enumerate())
                        Return JsonSerializer.Serialize(New With {
                            .saved = True, .selected = display,
                            .appliesOnEngineRebuild = True,
                            .effectiveNow = False
                        })
                    End Using
                Catch ex As Exception
                    DebugLog("[CaptureRestApi] display selection rejected: " & ex.Message)
                    Return JsonSerializer.Serialize(New With {
                        .saved = False, .error = "capture_display_invalid"
                    })
                End Try
            End If

            If path = "/screenshot" AndAlso method = "POST" Then
                If RestRecording() OrElse _engineReconfiguring Then
                    Return "{""saved"":false,""error"":""capture_busy""}"
                End If
                Try
                    Using doc = JsonDocument.Parse(body)
                        Dim root = doc.RootElement
                        Dim confirmed As JsonElement = Nothing
                        If root.ValueKind <> JsonValueKind.Object OrElse
                           Not root.TryGetProperty("confirmed", confirmed) OrElse
                           confirmed.ValueKind <> JsonValueKind.True Then
                            Return "{""saved"":false,""error"":""screenshot_confirmation_required""}"
                        End If
                    End Using
                    Dim display = CaptureDisplayCatalog.Resolve(
                        AppLayout.Dir, CaptureDisplayCatalog.Enumerate())
                    Dim ffmpeg = AppLayout.P("FFmpeg", "ffmpeg.exe")
                    If _settings IsNot Nothing AndAlso
                       File.Exists(_settings.FFmpegPath) Then
                        ffmpeg = _settings.FFmpegPath
                    End If
                    Dim image = CaptureScreenshotEngine.TakeOne(display, ffmpeg)
                    Return JsonSerializer.Serialize(image)
                Catch ex As Exception
                    DebugLog("[CaptureRestApi] screenshot refused: " & ex.Message)
                    Return JsonSerializer.Serialize(New With {
                        .saved = False, .error = "screenshot_display_or_runtime_unavailable"
                    })
                End Try
            End If

            If path = "/settings" AndAlso method = "GET" Then
                Return OscSettingsJson()
            End If

            If path = "/settings" AndAlso method = "POST" Then
                If Not ApplyOscSettings(body) Then Return "{""saved"":false,""error"":""invalid_recording_settings""}"
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
        Return RestTruth().busy
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

    ' Explicit REST overrides; absent store reads canonical saved values.
    Private Shared ReadOnly OscStoreLock As New Object()

    Private Function OscStorePath() As String
        Return AppLayout.P("Config", "capture-osc.json")
    End Function

    Private Function LoadOscSettings() As Dictionary(Of String, String)
        SyncLock OscStoreLock
            Dim current = CaptureSettings.Load(_configPath)
            Dim resolution = "In-game"
            If Not current.UseNativeResolution Then
                Select Case current.CustomHeight
                    Case 2160 : resolution = "2160p 4K"
                    Case 1440 : resolution = "1440p HD"
                    Case 1080 : resolution = "1080p HD"
                    Case 720 : resolution = "720p HD"
                    Case 480 : resolution = "480p"
                    Case 360 : resolution = "360p"
                    Case 240 : resolution = "240p"
                    Case Else : resolution = "Custom"
                End Select
            End If
            Dim d As New Dictionary(Of String, String) From {
                {"quality", "Custom"}, {"resolution", resolution},
                {"framerate", Math.Max(1, current.FPS).ToString()},
                {"bitrateBps", Math.Max(1L, current.Bitrate).ToString()}
            }
            Try
                Using doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(OscStorePath()))
                    Dim r = doc.RootElement
                    For Each k In New String() {"quality", "resolution", "framerate", "bitrateBps"}
                        Dim pr As JsonElement
                        If r.ValueKind = System.Text.Json.JsonValueKind.Object AndAlso r.TryGetProperty(k, pr) Then
                            d(k) = If(pr.ValueKind = System.Text.Json.JsonValueKind.Number, pr.GetRawText(), pr.GetString())
                        End If
                    Next
                End Using
                If Not CaptureRestSettingsPolicy.Valid(d) Then Throw New InvalidDataException("invalid_rest_settings_store")
            Catch ex As Exception When Not File.Exists(OscStorePath())
                ' No override is the normal canonical-config path.
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

    Private Function ApplyOscSettings(body As String) As Boolean
        Try
            Dim d = LoadOscSettings()
            Using doc = System.Text.Json.JsonDocument.Parse(If(body, "{}"))
                Dim r = doc.RootElement
                If r.ValueKind <> JsonValueKind.Object Then Return False
                If r.ValueKind = System.Text.Json.JsonValueKind.Object Then
                    Dim pr As System.Text.Json.JsonElement = Nothing
                    If r.TryGetProperty("quality", pr) Then d("quality") = pr.GetString()
                    If r.TryGetProperty("resolution", pr) Then d("resolution") = pr.GetString()
                    If r.TryGetProperty("framerate", pr) Then d("framerate") = pr.GetInt32().ToString()
                    If r.TryGetProperty("bitrateBps", pr) Then d("bitrateBps") = pr.GetInt64().ToString()
                End If
            End Using
            If Not CaptureRestSettingsPolicy.Valid(d) Then Return False
            SaveOscSettings(d)
            Return True
        Catch ex As Exception
            DebugLog($"[CaptureRestApi] osc settings parse: {ex.Message}")
            Return False
        End Try
    End Function

    ' Snapshot ที่ override session config จริง (NextRecordingConfig seam) — "engine config จริง" ของ OSC plane
    Private Function LoadOscSnapshot() As RecordSettingsSnapshot
        ' An absent OSC override store must not silently replace canonical
        ' recording configuration with the old hard-coded 60fps/50Mbps defaults.
        If Not File.Exists(OscStorePath()) Then Return Nothing
        Dim d = LoadOscSettings()
        Dim snap As New RecordSettingsSnapshot With {
            .Quality = d("quality"), .Resolution = d("resolution"),
            .Framerate = Integer.Parse(d("framerate"), Globalization.CultureInfo.InvariantCulture),
            .BitrateBps = Integer.Parse(d("bitrateBps"), Globalization.CultureInfo.InvariantCulture)
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
                Case "Custom"
                    Dim current = CaptureSettings.Load(_configPath)
                    If current.UseNativeResolution OrElse current.CustomWidth <= 0 OrElse current.CustomHeight <= 0 Then
                        Throw New InvalidDataException("custom_resolution_not_configured")
                    End If
                    snap.Width = current.CustomWidth : snap.Height = current.CustomHeight
                Case Else : Throw New InvalidDataException("unsupported_recording_resolution")
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
