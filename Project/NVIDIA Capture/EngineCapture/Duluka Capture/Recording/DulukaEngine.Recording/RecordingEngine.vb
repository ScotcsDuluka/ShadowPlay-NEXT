Option Strict On
Option Explicit On
Option Infer On

' RecordingEngine.vb
'
' Process-lifetime orchestrator. Composes IVideoCaptureBackend (DdagrabBackend) +
' IEncoderBackend (NvencEncoderBackend) whose Initialize() happens ONCE per process.
' Per-session: creates CaptureSession (audio + FFmpeg + mux).
'
' RecordingEngine itself OWNS NO GPU resources — the backends do.
' It just constructs them, dispatches sessions, and disposes them.

Imports System.Threading
Imports DulukaEngine.Diagnostics
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab
Imports DulukaEngine.Encoder
Imports DulukaEngine.Video.Handoff

Namespace DulukaEngine.Recording

    Public NotInheritable Class RecordingEngine
        Implements IDisposable

        Private ReadOnly _sync As New Object()
        Private ReadOnly _logger As EngineLogger

        ' ─── Persistent backends (created in Initialize, disposed in Dispose) ─
        Private _capture As IVideoCaptureRuntime
        Private _encoder As IEncoderRuntimeBackend
        Private ReadOnly _backendFactory As IRecordingBackendFactory
        Private _composition As CaptureCompositionPlan
        Private _state As RecordingEngineState = RecordingEngineState.Created
        Private _disposed As Boolean = False

        ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT2): the effective startup values
        ' this engine was initialized with — the immutable per-session echo
        ' source for requested-vs-actual evidence.
        Private _startupEcho As EngineStartupConfig

        ' ─── Current session ─────────────────────────────────────────────
        Private _currentSession As CaptureSession
        Private _stopRequested As Boolean = False
        Private _disposeRequested As Boolean = False
        Private ReadOnly _sessionFinished As New ManualResetEventSlim(True)

        Public Sub New(logger As EngineLogger)
            Me.New(logger, New RecordingBackendFactory())
        End Sub

        Public Sub New(logger As EngineLogger, backendFactory As IRecordingBackendFactory)
            If backendFactory Is Nothing Then Throw New ArgumentNullException(NameOf(backendFactory))
            _logger = logger
            _backendFactory = backendFactory
        End Sub

        ''' <summary>
        ''' Reject cross-device texture handoff if DXGI capture and NVENC did
        ''' not initialize on exactly the same Windows adapter. In a multi-GPU
        ''' machine each backend independently enumerates the first NVIDIA
        ''' adapter; never assume their selections are identical.
        ''' Pure logic seam is device-free and regression-tested.
        ''' </summary>
        Public Shared Sub RequireMatchingAdapterLuid(captureLow As UInteger,
                                                      captureHigh As Integer,
                                                      encoderLow As UInteger,
                                                      encoderHigh As Integer)
            If (captureLow = 0UI AndAlso captureHigh = 0) OrElse
               (encoderLow = 0UI AndAlso encoderHigh = 0) Then
                Throw New InvalidOperationException(
                    "DXGI/NVENC adapter LUID unavailable; refusing unverified cross-device transfer.")
            End If
            If captureLow <> encoderLow OrElse captureHigh <> encoderHigh Then
                Throw New InvalidOperationException(
                    $"DXGI/NVENC adapter mismatch: capture={captureHigh:x8}:{captureLow:x8}, " &
                    $"encoder={encoderHigh:x8}:{encoderLow:x8}. Recording cannot start with cross-GPU texture handoff.")
            End If
        End Sub

        ''' <summary>
        ''' Initialize persistent GPU resources (D3D11 + DXGI + NVENC).
        ''' Called ONCE at process start. Subsequent calls throw.
        ''' Uses the proven default startup config (NVENC_H264 / 20 Mbps / GOP 60 / p4).
        ''' </summary>
        Public Sub Initialize()
            Initialize(New EngineStartupConfig())
        End Sub

        ''' <summary>
        ''' Phase 12b: initialize with host-provided startup options
        ''' (codec / bitrate / GOP / preset come from Overlay settings).
        ''' The encoder session is process-lifetime — these values cannot
        ''' change per session without an engine rebuild.
        ''' </summary>
        Public Sub Initialize(startup As EngineStartupConfig)
            If startup Is Nothing Then startup = New EngineStartupConfig()

            SyncLock _sync
                If _disposed Then Throw New ObjectDisposedException(NameOf(RecordingEngine))
                If _state <> RecordingEngineState.Created Then
                    Throw New InvalidOperationException($"Initialize() called from state {_state}")
                End If
                _state = RecordingEngineState.Initializing
            End SyncLock

            Try
                ' ─── Initialize DdagrabBackend (creates D3D11 + DXGI duplication) ─
                _composition = _backendFactory.Plan(startup)
                ' Planned Intel routes must fail before DXGI creates duplication.
                _composition.RequireWired()
                _capture = _backendFactory.CreateCapture(_composition, startup.CaptureCursor, _logger)
                Dim ctx As New BackendContext(_logger)
                _capture.Initialize(ctx)
                _logger.Info($"RecordingEngine: DdagrabBackend initialized — {_capture.OutputWidth}x{_capture.OutputHeight} @ {_capture.OutputRefreshRate}Hz")

                ' ─── Initialize NvencEncoderBackend (creates D3D11 + NVENC session) ─
                _encoder = _backendFactory.CreateEncoder(_composition, _logger)
                Dim gop As Integer = If(startup.GopSize > 0, startup.GopSize, 60)

                ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT2): resolution group.
                ' The capture backend grabs the DESKTOP at its native size;
                ' the encode size follows config.json:
                '   use_native_resolution=true            → desktop size
                '   use_native_resolution=false + w/h     → NVENC scales (GPU)
                ' Oversized requests fail LOUDLY (no silent desktop fallback).
                Dim encodeDims As Tuple(Of Integer, Integer) =
                    EngineStartupConfig.ResolveEncodeDimensions(
                        _capture.OutputWidth, _capture.OutputHeight,
                        startup.UseNativeResolution, startup.RequestedWidth, startup.RequestedHeight)
                If encodeDims.Item1 <> _capture.OutputWidth OrElse encodeDims.Item2 <> _capture.OutputHeight Then
                    _logger.Info($"[RecordingEngine] resolution: requested {startup.RequestedWidth}x{startup.RequestedHeight} (use_native_resolution=False) → NVENC encode {encodeDims.Item1}x{encodeDims.Item2} from capture {_capture.OutputWidth}x{_capture.OutputHeight}")
                Else
                    _logger.Info($"[RecordingEngine] resolution: native (use_native_resolution=True or unset) → encode {_capture.OutputWidth}x{_capture.OutputHeight}")
                End If

                Dim encConfig As New EncoderConfig() With {
                    .CodecKey = If(String.IsNullOrEmpty(startup.CodecKey), "NVENC_H264", startup.CodecKey),
                    .BitrateBps = If(startup.BitrateBps > 0, startup.BitrateBps, 20_000_000L),
                    .MinrateBps = If(startup.BitrateBps > 0, startup.BitrateBps, 20_000_000L),
                    .MaxrateBps = If(startup.BitrateBps > 0, startup.BitrateBps, 20_000_000L),
                    .BufsizeBps = If(startup.BitrateBps > 0, startup.BitrateBps * 2, 40_000_000L),
                    .GopSize = gop,
                    .RateControl = EngineStartupConfig.ResolveRateControl(startup.RateControl),
                    .Preset = If(String.IsNullOrEmpty(startup.Preset), "p4", startup.Preset),
                    .FrameRateFps = startup.Fps,
                    .ExpectedWidth = _capture.OutputWidth,
                    .ExpectedHeight = _capture.OutputHeight,
                    .EncodeWidth = encodeDims.Item1,
                    .EncodeHeight = encodeDims.Item2
                }
                EncoderConfigurationPolicy.Apply(encConfig, _composition.Provider.Id, startup)
                _encoder.Initialize(encConfig)
                ' Phase 7B: compare actual Windows adapter identities, not
                ' model names or index assumptions. A second GTX 1080 Ti may
                ' have the same display name but a different adapter LUID.
                ' The outer Initialize Catch disposes both backends on failure.
                RecordingBackendFactory.RequireIdentity(_composition, _capture.ActualTarget, _encoder.ActualAdapter)
                _backendFactory.ValidateTarget(_composition)
                _logger.Info("RecordingEngine: DXGI capture and NVENC adapter LUID MATCH")
                _startupEcho = startup

                ' ★ PHASE 1 (task §9): requested → selected → actual capture
                ' method. The New Engine has exactly ONE production backend;
                ' anything else requested is a recorded GAP, never a silent
                ' substitute and never a fake selector.
                Dim requestedMethod As String = If(startup.RequestedCaptureMethod, "").Trim()
                If requestedMethod.Length = 0 OrElse String.Equals(requestedMethod, "ddagrab", StringComparison.OrdinalIgnoreCase) Then
                    _logger.Info($"[RecordingEngine] capture method: requested='{If(requestedMethod.Length = 0, "(default)", requestedMethod)}' → selected=DdagrabBackend → actual=DdagrabBackend (DXGI Desktop Duplication)")
                Else
                    _logger.Warning($"[RecordingEngine] capture method: requested='{requestedMethod}' → GAP: '{requestedMethod}' is not implemented in the New Engine (only production backend = ddagrab) — running DdagrabBackend. Gap recorded, NOT silently accepted.")
                End If

                ' ★ EXCLUSIVE API (SPN-ADR §C/§D): requested→selected→actual
                ' truth line. Desktop Duplication IS the one implemented
                ' verified native source for exclusive presentations; source
                ' faults take the existing same-LUID recreate + generation
                ' path and DxgiDisplayModeMonitor supervises the pinned
                ' target (bounded poll, never per-frame). A game/present hook
                ' source is NOT implemented — an unimplemented request fails
                ' closed in the factory before any device is created.
                Dim exclusiveMethod As String = If(startup.RequestedExclusiveCaptureMethod, "").Trim()
                If exclusiveMethod.Length > 0 Then
                    _logger.Info($"[RecordingEngine] exclusive capture API: requested='{exclusiveMethod.ToLowerInvariant()}' → selected={_composition.ExclusiveApi} ({_composition.ExclusiveApiReason}); verification=ExclusiveVerificationGate(real frames on pinned target), monitor=DxgiDisplayModeMonitor, game-hook source=not_implemented")
                End If

                ' ★ PHASE 1 (task §8): pixel format truth line.
                ' Capture texture format is fixed by the Ddagrab backend (BGRA8).
                ' Encoder/output pixel-format hints are kept separate from the
                ' capture texture contract; native NVENC consumes the BGRA texture
                ' through its ARGB input path and the H.264 output is yuv420p.
                Dim requestedPixFmt As String = If(String.IsNullOrEmpty(startup.RequestedPixelFormat), "nv12", startup.RequestedPixelFormat.Trim().ToLowerInvariant())
                If requestedPixFmt = "nv12" Then
                    ' Current production path is BGRA8 capture → NVENC ARGB input.
                    ' nv12 is an output/codec pixel-format hint in the config schema;
                    ' no NV12 texture conversion stage exists in this pipeline.
                    _logger.Info($"[RecordingEngine] pixel format: config='{requestedPixFmt}' → runtime=BGRA8 (D3D11 capture) → NVENC input=ARGB; output codec produces yuv420p in MP4. No NV12 texture conversion requested by the current native NVENC path.")
                Else
                    _logger.Warning($"[RecordingEngine] pixel format: config='{requestedPixFmt}' → runtime=BGRA8 (D3D11 capture) → NVENC input=ARGB. Requested format is not a native capture texture format in this backend; no conversion stage is currently implemented.")
                End If

                Dim fpsEcho As String = If(startup.Fps > 0, startup.Fps.ToString(), "unset (60 default)")
                _logger.Info($"RecordingEngine: NvencEncoderBackend initialized ({encConfig.CodecKey}, {encConfig.BitrateBps} bps, GOP {encConfig.GopSize}, preset {encConfig.Preset})")
                _logger.Info($"[RecordingEngine] effective video config (startup): codec={encConfig.CodecKey}, fps={fpsEcho}, bitrate={encConfig.BitrateBps} bps, rc={encConfig.RateControl}, preset={encConfig.Preset}, gop={encConfig.GopSize} (GOP independent of FPS — PHASE 1)")

                _state = RecordingEngineState.Idle
                _logger.Info("RecordingEngine: ready (Idle)")
            Catch ex As Exception
                _state = RecordingEngineState.Faulted
                _logger.Error($"RecordingEngine: Initialize failed: {ex.Message}", ex)
                ' Cleanup partially-created backends
                Try : _encoder?.Dispose() : Catch : End Try
                Try : _capture?.Dispose() : Catch : End Try
                Throw
            End Try
        End Sub

        ''' <summary>
        ''' PHASE 3 UI diagnostics: the startup config this engine was
        ''' initialized with (init-time immutable). Nothing before
        ''' Initialize succeeds — callers must handle Nothing.
        ''' Read-only echo for the Effective Runtime panel (UI spec §12);
        ''' no runtime semantics.
        ''' </summary>
        Public ReadOnly Property StartupEcho As EngineStartupConfig
            Get
                Return _startupEcho
            End Get
        End Property

        ''' <summary>
        ''' PHASE 3 UI diagnostics: actual capture backend geometry
        ''' ("WxH @ NNNHz"), init-time immutable. Empty string before the
        ''' backend exists. Read-only echo, no runtime semantics.
        ''' </summary>
        Public ReadOnly Property CaptureGeometry As String
            Get
                If _capture Is Nothing Then Return ""
                Return $"{_capture.OutputWidth}x{_capture.OutputHeight} @ {_capture.OutputRefreshRate}Hz"
            End Get
        End Property

        ''' <summary>
        ''' Start a new recording session. Blocks until session completes
        ''' (duration elapsed or Stop() called). Returns SessionResult.
        ''' Only ONE active session at a time.
        ''' </summary>
        Public Function StartSession(config As SessionConfig) As SessionResult
            Return StartSessionCore(config, Nothing)
        End Function

        Public Function StartOwnedFrameSession(config As SessionConfig, frames As IEnumerable(Of SessionFrameInput)) As SessionResult
            If frames Is Nothing Then Throw New ArgumentNullException(NameOf(frames))
            Return StartSessionCore(config, frames)
        End Function
        Public Function StartSourceSession(config As SessionConfig, source As DulukaEngine.Video.Transport.D3D11.BgraSessionSource) As SessionResult
            If source Is Nothing OrElse config Is Nothing Then Throw New ArgumentNullException(NameOf(source))
            If _startupEcho.Color Is Nothing OrElse Not _startupEcho.Color.SameColor(source.Color) OrElse
                Not _capture.ActualTarget.SameOutput(source.Target) OrElse source.FrameRateFps <> config.TargetFps Then
                Throw New InvalidOperationException("source_session_plan_mismatch")
            End If
            config.Color = source.Color
            Return StartSessionCore(config, Nothing, source)
        End Function

        Private Function StartSessionCore(config As SessionConfig, frames As IEnumerable(Of SessionFrameInput),
            Optional source As DulukaEngine.Video.Transport.D3D11.BgraSessionSource = Nothing,
            Optional captureRebuildRetry As Boolean = True) As SessionResult
            If config Is Nothing Then Throw New ArgumentNullException(NameOf(config))

            ' ★ P1-A DISK HEADROOM GUARD (W1): refuse BEFORE any resource is
            ' spawned when the output drive cannot hold this session. Derived
            ' from the session's own numbers (video bitrate + audio streams ×
            ' duration), ×2 because the faststart remux holds the .frag and
            ' the final file on disk simultaneously. P3-B evidence: every
            ' long-run failure in the starvation matrix was triggered (or
            ' aggravated) by the drive hitting 100% mid-record — ENOSPC,
            ' stalled writes, drain discards. Failing here is cheap and loud.
            Dim effectiveBitrate As Long = If(_startupEcho IsNot Nothing AndAlso _startupEcho.BitrateBps > 0,
                                                  _startupEcho.BitrateBps, 20000000L)
            Dim headroomCheck = DiskHeadroom.Evaluate(config, effectiveBitrate, _logger)
            If Not headroomCheck.Ok Then
                Throw New ArgumentException(headroomCheck.FailureReason, NameOf(config))
            End If

            SyncLock _sync
                If _disposed OrElse _disposeRequested Then Throw New ObjectDisposedException(NameOf(RecordingEngine))
                If _state <> RecordingEngineState.Idle Then
                    Throw New InvalidOperationException($"StartSession() called from state {_state}")
                End If
                _stopRequested = False
                _sessionFinished.Reset()
                _state = RecordingEngineState.Recording
            End SyncLock

            Dim result As SessionResult = Nothing
            Try
                _backendFactory.ValidateTarget(_composition)
                RecordingBackendFactory.RequireIdentity(_composition, _capture.ActualTarget, _encoder.ActualAdapter)
                ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT2): stamp the ACTUAL
                ' encode dimensions the persistent encoder runs with, so the
                ' session evidence always shows requested vs actual.
                ' (The encoder dims are init-time; the per-session request is
                ' preserved in the echo fields.)
                Dim echo As EngineStartupConfig = If(_startupEcho, New EngineStartupConfig())

                ' ★ CAPTURE-REST-PLAN phase 3: the session may carry its own
                ' resolution request (REST /Record/Settings override). The
                ' startup echo fills the group ONLY when the session did not
                ' request anything — a session-level request WINS.
                If config.RequestedWidth <= 0 OrElse config.RequestedHeight <= 0 Then
                    config.UseNativeResolution = echo.UseNativeResolution
                    config.RequestedWidth = echo.RequestedWidth
                    config.RequestedHeight = echo.RequestedHeight
                End If

                If config.TargetFps <= 0 Then
                    config.TargetFps = If(echo.Fps > 0, echo.Fps, 60)
                    _logger.Warning($"[RecordingEngine] session FPS missing/invalid; using configured engine/startup FPS {config.TargetFps}fps")
                ElseIf echo.Fps > 0 AndAlso config.TargetFps <> echo.Fps Then
                    _logger.Info($"[RecordingEngine] session FPS {config.TargetFps} differs from startup FPS {echo.Fps}; NVENC will be reconciled before the session starts.")
                End If

                ' ★ phase 3: resolve the session's OWN encode dimensions
                ' (native, or a requested NVENC downscale). A manual upscale
                ' request is rejected without changing the user's selection or
                ' starting a capture session; no silent native substitution.
                Dim sessionDims As Tuple(Of Integer, Integer) =
                    EngineStartupConfig.ResolveEncodeDimensions(
                        _capture.OutputWidth, _capture.OutputHeight,
                        config.UseNativeResolution, config.RequestedWidth, config.RequestedHeight)
                Dim sessionEncodeW As Integer = sessionDims.Item1
                Dim sessionEncodeH As Integer = sessionDims.Item2
                config.EncodeWidth = sessionEncodeW
                config.EncodeHeight = sessionEncodeH

                ' ★ phase 3: per-session bitrate override (REST record
                ' settings). 0 = no override — the startup bitrate stays
                ' the authority for this session.
                Dim sessionBitrate As Long =
                    If(config.BitrateBps > 0, config.BitrateBps,
                       If(echo.BitrateBps > 0, echo.BitrateBps, 20_000_000L))
                If _composition.Provider.Id = "intel:qsv-h264" AndAlso echo.RateControl = "cqp" Then sessionBitrate = 0

                ' The NVENC frame rate is encoded into the native encoder session and
                ' therefore into the raw H.264 stream timing. A per-session FPS change
                ' MUST rebuild the encoder before any frame is submitted; merely pacing
                ' CaptureSession at a new FPS would otherwise produce a rate-mismatched
                ' stream (e.g. 120 submitted frames interpreted as 60fps).
                ' ★ phase 3: bitrate and encode dimensions are baked into the
                ' native session the same way — a change on ANY of the three
                ' triggers the rebuild below.
                Dim encoderFps As Integer = _encoder.FrameRateFps
                Dim needsRebuild As Boolean =
                    _encoder.CurrentState = EncoderState.Faulted OrElse encoderFps <> config.TargetFps OrElse
                    _encoder.BitrateBpsOutput <> sessionBitrate OrElse
                    _encoder.EncodeWidthOutput <> sessionEncodeW OrElse
                    _encoder.EncodeHeightOutput <> sessionEncodeH
                If needsRebuild Then
                    _logger.Warning($"[RecordingEngine] rebuilding persistent NVENC for session video config: encoder={encoderFps}fps/{_encoder.BitrateBpsOutput}bps/{_encoder.EncodeWidthOutput}x{_encoder.EncodeHeightOutput} → session={config.TargetFps}fps/{sessionBitrate}bps/{sessionEncodeW}x{sessionEncodeH}")

                    Dim startup As EngineStartupConfig = If(_startupEcho, New EngineStartupConfig())
                    Dim rebuiltConfig As New EncoderConfig() With {
                        .CodecKey = If(String.IsNullOrEmpty(startup.CodecKey), "NVENC_H264", startup.CodecKey),
                        .BitrateBps = sessionBitrate,
                        .MinrateBps = sessionBitrate,
                        .MaxrateBps = sessionBitrate,
                        .BufsizeBps = sessionBitrate * 2,
                        .GopSize = If(startup.GopSize > 0, startup.GopSize, 60),
                        .RateControl = EngineStartupConfig.ResolveRateControl(startup.RateControl),
                        .Preset = If(String.IsNullOrEmpty(startup.Preset), "p4", startup.Preset),
                        .FrameRateFps = config.TargetFps,
                        .ExpectedWidth = _capture.OutputWidth,
                        .ExpectedHeight = _capture.OutputHeight,
                        .EncodeWidth = config.EncodeWidth,
                        .EncodeHeight = config.EncodeHeight
                    }
                    EncoderConfigurationPolicy.Apply(rebuiltConfig, _composition.Provider.Id, startup)

                    ' ★ Leak fix: a failed Initialize (NVENC open failure, D3D11
                    ' creation failure, driver busy) used to leak the freshly
                    ' constructed backend — the NVENC session + D3D11 device it
                    ' already opened were never released (GPU memory accumulates
                    ' process-lifetime across FPS-mismatched sessions). Dispose
                    ' the half-initialized instance and rethrow; the previous
                    ' encoder stays the live authority for this failed session.
                    Dim rebuiltEncoder As IEncoderRuntimeBackend = _backendFactory.CreateEncoder(_composition, _logger)
                    Try
                        rebuiltEncoder.Initialize(rebuiltConfig)
                        RecordingBackendFactory.RequireIdentity(_composition, _capture.ActualTarget, rebuiltEncoder.ActualAdapter)
                        _backendFactory.ValidateTarget(_composition)
                    Catch ex As Exception
                        Try : rebuiltEncoder.Dispose() : Catch : End Try
                        Throw
                    End Try
                    ' ★ Dispose-race guard: the field swap is the only write to
                    ' _encoder outside _sync. A Dispose() landing between
                    ' Initialize and the swap would tear down whichever backend
                    ' the field names while this path still owns the other one
                    ' — the session below could then receive a disposed encoder.
                    ' Re-check the dispose gate UNDER _sync and swap atomically;
                    ' if Dispose won, discard the rebuilt backend instead.
                    Dim previousEncoder As IEncoderRuntimeBackend = Nothing
                    Dim disposeWon As Boolean = False
                    SyncLock _sync
                        If _disposeRequested OrElse _disposed Then
                            disposeWon = True
                        Else
                            previousEncoder = _encoder
                            _encoder = rebuiltEncoder
                        End If
                    End SyncLock
                    If disposeWon Then
                        Try : rebuiltEncoder.Dispose() : Catch : End Try
                        _logger.Warning("[RecordingEngine] NVENC FPS rebuild discarded — engine dispose won the race")
                    Else
                        Try
                            previousEncoder.Dispose()
                        Catch ex As Exception
                            _logger.Warning($"[RecordingEngine] previous NVENC dispose after FPS rebuild threw: {ex.Message}")
                        End Try
                    End If

                    _logger.Info($"[RecordingEngine] NVENC video authority now={_encoder.FrameRateFps}fps/{_encoder.BitrateBpsOutput}bps/{_encoder.EncodeWidthOutput}x{_encoder.EncodeHeightOutput} (session requested={config.TargetFps}fps/{sessionBitrate}bps/{sessionEncodeW}x{sessionEncodeH})")
                Else
                    _logger.Info($"[RecordingEngine] NVENC video authority verified={encoderFps}fps/{_encoder.BitrateBpsOutput}bps/{_encoder.EncodeWidthOutput}x{_encoder.EncodeHeightOutput}")
                End If

                Dim startStop As Boolean
                Dim session As CaptureSession
                SyncLock _sync
                    If _disposeRequested OrElse _disposed Then
                        _stopRequested = True
                    End If
                    _currentSession = New CaptureSession(_capture, _encoder, config, _logger)
                    session = _currentSession
                    startStop = _stopRequested
                End SyncLock
                If startStop Then session?.[Stop]()
                If source IsNot Nothing Then
                    result = session.RunSourceFrames(source)
                Else
                    result = If(frames Is Nothing, session.Run(), session.RunOwnedFrames(frames, 1))
                End If
            Catch ex As InvalidOperationException When captureRebuildRetry AndAlso
                    ex.Message.Contains("start_requires_initialized_or_stopped")
                ' The capture backend state machine has no path from Faulted
                ' back to Initialized (DeviceRemoved churn exhausting its
                ' rebuild budget bricks it for the process lifetime). The
                ' encoder has a rebuild seam — the capture backend gets the
                ' same treatment: fresh instance, identity re-verified, one
                ' bounded retry so a record press never dead-ends on stale
                ' backend state.
                _logger.Warning("[RecordingEngine] capture backend not startable (Faulted) — rebuilding capture backend and retrying the session once")
                RebuildCaptureBackend()
                Return StartSessionCore(config, frames, source, False)
            Catch ex As Exception
                _logger.Error($"RecordingEngine: session failed: {ex.Message}", ex)
                result = New SessionResult() With {
                    .OutputPath = config.OutputPath,
                    .RequestedDurationSec = config.DurationSeconds,
                    .ErrorMessage = ex.Message
                }
            Finally
                If result IsNot Nothing Then result.VideoIdentity = VideoIdentity()
                SyncLock _sync
                    ' Publish the result for BOTH success and failure, and
                    ' never expose a phantom active session after an exception.
                    ' Previous success-only assignment left GetStatus().LastSessionResult
                    ' stale when initialization or the session itself failed.
                    _currentSession = Nothing
                    _lastSessionResult = result
                    ' A timed-out Dispose leaves disposal pending while the
                    ' session unwinds. Do not publish Idle: StartSession rejects
                    ' that state through _disposeRequested, so Idle would lie
                    ' about the engine being reusable. A later Dispose call can
                    ' finish cleanup once this session has signaled completion.
                    _state = If(_disposeRequested,
                                RecordingEngineState.Stopping,
                                RecordingEngineState.Idle)
                    ' Set completion atomically with the Idle publication:
                    ' a new StartSession cannot Reset the event before an old
                    ' session signals completion.
                    _sessionFinished.Set()
                End SyncLock
            End Try

            Return result
        End Function

        ''' <summary>Signal early stop. The running session will return shortly.</summary>
        Public Sub [Stop]()
            Dim session As CaptureSession = Nothing
            SyncLock _sync
                If _disposed Then Return
                If _state = RecordingEngineState.Recording Then
                    _stopRequested = True
                    session = _currentSession
                Else
                    Return
                End If
            End SyncLock
            session?.[Stop]()
        End Sub

        Public Function GetStatus() As EngineStatus
            SyncLock _sync
                Return New EngineStatus() With {
                    .SourceLifecycleState = If(_currentSession Is Nothing, Nothing, _currentSession.SourceLifecycleState),
                    .State = _state,
                    .VideoIdentity = VideoIdentity(),
                    .CurrentSessionId = If(_currentSession IsNot Nothing, _currentSession.SessionId, Nothing),
                    .RealFramesCapturedThisSession = If(_currentSession Is Nothing, 0, _currentSession.RealFramesCapturedThisSession),
                    .FramesSubmittedThisSession = If(_currentSession Is Nothing, 0, _currentSession.FramesSubmittedThisSession),
                    .FramesEncodedThisSession = If(_currentSession Is Nothing, 0, _currentSession.FramesEncodedThisSession),
                    .PacketsQueuedForMuxThisSession = If(_currentSession Is Nothing, 0, _currentSession.PacketsQueuedForMuxThisSession),
                    .LastSessionResult = _lastSessionResult
                }
            End SyncLock
        End Function

        ''' <summary>
        ''' A Faulted capture backend has no path back to Initialized in the
        ''' backend state machine — the only recovery is a fresh backend
        ''' instance. Mirrors the proven encoder-rebuild seam: dispose the
        ''' stale backend, create via the factory, initialize, re-verify
        ''' capture↔encoder adapter identity, swap under the lifecycle lock.
        ''' </summary>
        Private Sub RebuildCaptureBackend()
            Dim stale As IVideoCaptureRuntime = Nothing
            SyncLock _sync
                stale = _capture
                _capture = Nothing
            End SyncLock
            If stale IsNot Nothing Then
                Try
                    stale.Dispose()
                Catch ex As Exception
                    _logger.Warning($"[RecordingEngine] stale capture backend dispose: {ex.Message}")
                End Try
            End If
            Dim fresh = _backendFactory.CreateCapture(_composition, If(_startupEcho?.CaptureCursor, True), _logger)
            fresh.Initialize(New BackendContext(_logger))
            SyncLock _sync
                If _disposed OrElse _disposeRequested Then
                    fresh.Dispose()
                    Throw New ObjectDisposedException(NameOf(RecordingEngine))
                End If
                RecordingBackendFactory.RequireIdentity(_composition, fresh.ActualTarget, _encoder.ActualAdapter)
                _capture = fresh
            End SyncLock
            _logger.Info($"[RecordingEngine] capture backend rebuilt after Faulted — {fresh.OutputWidth}x{fresh.OutputHeight} @ {fresh.OutputRefreshRate}Hz")
        End Sub

        Private Function VideoIdentity() As RecordingVideoIdentity
            Dim capture = _capture?.ActualTarget
            Dim encoder = _encoder?.ActualAdapter
            If capture Is Nothing OrElse encoder Is Nothing OrElse _composition Is Nothing Then Return Nothing
            Return New RecordingVideoIdentity With {
                .CaptureLuidLow = capture.Adapter.Low, .CaptureLuidHigh = capture.Adapter.High,
                .CaptureVendorId = capture.Adapter.VendorId, .OutputIndex = capture.OutputIndex,
                .EncoderLuidLow = encoder.Low, .EncoderLuidHigh = encoder.High,
                .EncoderVendorId = encoder.VendorId, .EncoderProvider = _composition.Provider.Id}
        End Function

        Private _lastSessionResult As SessionResult = Nothing

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim session As CaptureSession = Nothing
            Dim waitForSession As Boolean = False

            SyncLock _sync
                If _disposed Then Return
                _disposeRequested = True
                _stopRequested = True
                session = _currentSession
                waitForSession = (_state = RecordingEngineState.Recording)
            End SyncLock

            session?.[Stop]()

            If waitForSession Then
                If Not _sessionFinished.Wait(TimeSpan.FromSeconds(30)) Then
                    ' State truth: the session task is STILL alive, so the state
                    ' stays Recording (a session does exist). _disposeRequested
                    ' remains set, so StartSession is rejected from now on — the
                    ' engine is unusable until the process restarts. Saying
                    ' "safe retry" here was a lie: there is no retry path.
                    _logger.Warning("RecordingEngine: timed out waiting for the active session during Dispose — backends left alive, session still running; StartSession is rejected until the process restarts.")
                    Return
                End If
            End If

            SyncLock _sync
                If _disposed Then Return
                _disposed = True
                _state = RecordingEngineState.Disposed
                session = _currentSession
                _currentSession = Nothing
            End SyncLock

            Try : session?.[Stop]() : Catch : End Try
            Try : session?.Dispose() : Catch : End Try

            ' Dispose persistent backends only after any active StartSession has
            ' fully unwound. Read the refs UNDER _sync so an FPS-rebuild swap
            ' cannot publish a different backend between the read and the dispose.
            Dim encoderToDispose As IEncoderRuntimeBackend = Nothing
            Dim captureToDispose As IVideoCaptureRuntime = Nothing
            SyncLock _sync
                encoderToDispose = _encoder
                captureToDispose = _capture
            End SyncLock
            Try : encoderToDispose?.Dispose() : Catch ex As Exception : _logger.Warning($"encoder.Dispose: {ex.Message}") : End Try
            Try : captureToDispose?.Dispose() : Catch ex As Exception : _logger.Warning($"capture.Dispose: {ex.Message}") : End Try

            _sessionFinished.Dispose()
            _logger.Info("RecordingEngine: disposed")
        End Sub

        ' ─── Minimal IVideoBackendContext ──────────────────────────────────
        Private NotInheritable Class BackendContext
            Implements IVideoBackendContext

            Private ReadOnly _logger As EngineLogger

            Public Sub New(logger As EngineLogger)
                _logger = logger
            End Sub

            Public ReadOnly Property Logger As EngineLogger Implements IVideoBackendContext.Logger
                Get
                    Return _logger
                End Get
            End Property

            Public ReadOnly Property BackendKind As VideoBackendKind Implements IVideoBackendContext.BackendKind
                Get
                    Return VideoBackendKind.Ddagrab
                End Get
            End Property
        End Class

    End Class

End Namespace
