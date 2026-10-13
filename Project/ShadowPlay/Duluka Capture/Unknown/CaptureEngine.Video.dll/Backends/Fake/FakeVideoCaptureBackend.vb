Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Video.Backends.Fake
    ''' <summary>
    ''' FakeVideoCaptureBackend — a first-class implementation of
    ''' IVideoCaptureBackend used by tests. (P1-A v1.3.1 §1.3, §7.3, §8.5)
    '''
    ''' Does NOT depend on:
    '''   - ddagrab (DXGI Output Duplication)
    '''   - gfxcapture (Windows.Graphics.Capture)
    '''   - FFmpeg
    '''   - NVENC
    '''   - Audio
    '''   - UI
    '''   - real D3D11 capture
    '''
    ''' The fake is driven by a programmable script of result descriptors.
    ''' Each descriptor says what the next acquisition attempt should return
    ''' (FrameAvailable / NoFrame / Error / throw). The fake's worker thread
    ''' walks the script deterministically and pushes results into the sink
    ''' via TryPush, reacting to PushOutcome exactly as a real backend would
    ''' (dispose refused frames, increment DroppedFrames, etc.).
    '''
    ''' Timestamps are synthetic but deterministic: a configurable tick origin
    ''' (default 1_000_000) plus a configurable tick stride per acquisition
    ''' attempt (default 10_000). Tests can therefore assert exact
    ''' CaptureTimeTicks / PresentationTimestampTicks / Sequence values
    ''' without depending on a real clock.
    ''' </summary>
    Public NotInheritable Class FakeVideoCaptureBackend
        Implements IVideoCaptureBackend
        Implements IVideoBackendDiagnostics

        ' ---- backend state ----
        Private ReadOnly _sync As New Object()
        Private ReadOnly _logger As EngineLogger

        Private _state As FakeBackendState = FakeBackendState.Created
        Private _disposed As Boolean = False

        ' ---- script ----
        Private _script As IReadOnlyList(Of FakeFrameDescriptor) = New List(Of FakeFrameDescriptor)()
        Private _scriptIndex As Integer = 0

        ' ---- worker ----
        Private _workerThread As Thread
        Private _stopSignal As Boolean = False
        Private _sink As IVideoFrameSink

        ' ---- diagnostics (counters) ----
        Private _emittedFrames As Long = 0
        Private _droppedFrames As Long = 0
        Private _replacedFrames As Long = 0
        Private _noFrameCount As Long = 0
        Private _errorCount As Long = 0

        ' ---- deterministic timing ----
        Private _nextSequence As Long = 0
        Private _nextAttemptTicks As Long = 1_000_000
        Private ReadOnly _tickStride As Long = 10_000
        Private _interAttemptDelayMs As Integer = 0

        ' ---- frame factory hook (for tests that want to inspect frames) ----
        Private _frameFactory As Func(Of FrameDiagnostics, IVideoFrame)

        ' ---- config ----
        Private _pixelFormat As VideoPixelFormat = VideoPixelFormat.Bgra8
        Private _dimensions As VideoFrameDimensions = New VideoFrameDimensions(1920, 1080)
        Private _origin As VideoFrameOrigin = VideoFrameOrigin.CpuMemory
        Private _requireBgra8 As Boolean = True

        Public Sub New(Optional logger As EngineLogger = Nothing)
            _logger = If(logger, New EngineLogger("FakeVideoCaptureBackend"))
        End Sub

        ' ===== IVideoCaptureBackend =====

        Public ReadOnly Property Diagnostics As IVideoBackendDiagnostics Implements IVideoCaptureBackend.Diagnostics
            Get
                Return Me
            End Get
        End Property

        Public Sub Initialize(context As IVideoBackendContext) Implements IVideoCaptureBackend.Initialize
            If context Is Nothing Then
                Throw New ArgumentNullException(NameOf(context))
            End If
            ThrowIfDisposed()

            SyncLock _sync
                If _state <> FakeBackendState.Created Then
                    Throw New InvalidOperationException(
                        "Initialize cannot be called from state '" & _state.ToString() & "'. Expected 'Created'.")
                End If
                _state = FakeBackendState.Initializing
            End SyncLock

            Try
                ' Phase 1 baseline BGRA8 enforcement (§3.4, §15.1).
                If _requireBgra8 AndAlso _pixelFormat <> VideoPixelFormat.Bgra8 Then
                    Throw New VideoBackendConfigurationException(
                        "Phase 1 baseline requires Bgra8; requested format was " & _pixelFormat.ToString() & ".")
                End If

                ' Default frame factory: a minimal CPU-resident FakeVideoFrame.
                If _frameFactory Is Nothing Then
                    _frameFactory = AddressOf DefaultFrameFactory
                End If

                _state = FakeBackendState.Initialized
                _logger.Info("FakeVideoCaptureBackend: Initialize complete (pixelFormat=" & _pixelFormat.ToString() & ")")
            Catch ex As VideoBackendException
                _state = FakeBackendState.Faulted
                _logger.Error("FakeVideoCaptureBackend: Initialize failed", ex)
                Throw
            Catch ex As Exception
                _state = FakeBackendState.Faulted
                _logger.Error("FakeVideoCaptureBackend: Initialize failed (unexpected)", ex)
                Throw New VideoBackendRuntimeException("Initialize failed unexpectedly", ex)
            End Try
        End Sub

        Public Sub Start(sink As IVideoFrameSink) Implements IVideoCaptureBackend.Start
            If sink Is Nothing Then
                Throw New ArgumentNullException(NameOf(sink))
            End If
            ThrowIfDisposed()

            SyncLock _sync
                Select Case _state
                    Case FakeBackendState.Starting, FakeBackendState.Running
                        _logger.Warning("FakeVideoCaptureBackend: Start ignored, already '" & _state.ToString() & "'.")
                        Return
                    Case FakeBackendState.Initialized, FakeBackendState.Stopped
                        _state = FakeBackendState.Starting
                    Case Else
                        Throw New InvalidOperationException(
                            "Start cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock

            Try
                _sink = sink
                _stopSignal = False
                _scriptIndex = 0
                _workerThread = New Thread(AddressOf WorkerLoop) With {.IsBackground = True, .Name = "FakeVideoCaptureBackend.Worker"}
                _workerThread.Start()
                _state = FakeBackendState.Running
                _logger.Info("FakeVideoCaptureBackend: started")
            Catch ex As Exception
                _state = FakeBackendState.Faulted
                _logger.Error("FakeVideoCaptureBackend: Start failed", ex)
                Throw New VideoBackendRuntimeException("Start failed", ex)
            End Try
        End Sub

        Public Sub [Stop]() Implements IVideoCaptureBackend.Stop
            ThrowIfDisposed()

            SyncLock _sync
                Select Case _state
                    Case FakeBackendState.Created, FakeBackendState.Initialized, FakeBackendState.Stopped
                        _logger.Warning("FakeVideoCaptureBackend: Stop ignored, not running (state='" & _state.ToString() & "').")
                        Return
                    Case FakeBackendState.Stopping
                        _logger.Warning("FakeVideoCaptureBackend: Stop ignored, already stopping.")
                        Return
                    Case FakeBackendState.Faulted
                        _logger.Warning("FakeVideoCaptureBackend: Stop on Faulted state; no work to stop.")
                        Return
                    Case FakeBackendState.Running
                        _state = FakeBackendState.Stopping
                    Case Else
                        Throw New InvalidOperationException(
                            "Stop cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock

            Try
                _stopSignal = True
                Dim worker = _workerThread
                If worker IsNot Nothing Then
                    If Not worker.Join(TimeSpan.FromSeconds(2)) Then
                        _logger.Error("FakeVideoCaptureBackend: worker did not acknowledge stop within 2 s", Nothing)
                    End If
                End If
                _state = FakeBackendState.Stopped
                _logger.Info("FakeVideoCaptureBackend: stopped")
            Catch ex As Exception
                _state = FakeBackendState.Faulted
                _logger.Error("FakeVideoCaptureBackend: Stop failed", ex)
                Throw New VideoBackendShutdownException("Stop failed", ex)
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            ' P0 fix: NEVER wait for the worker thread while holding _sync.
            ' The worker itself needs _sync on every iteration (to read the script
            ' and to update diagnostics counters), so holding _sync across
            ' worker.Join() is a guaranteed deadlock.
            '
            ' Pattern (per GPT P1-B.1 FIX change #1):
            '   1. Capture state under lock; set the stop signal under lock;
            '      mark _disposed to make Dispose idempotent.
            '   2. Release the lock.
            '   3. Join the worker thread OUTSIDE the lock.
            '   4. Re-acquire the lock ONLY to finalize state to Disposed.

            Dim workerToJoin As Thread = Nothing
            Dim needJoin As Boolean = False

            SyncLock _sync
                If _disposed Then Return
                _disposed = True

                If _state = FakeBackendState.Disposed Then
                    Return
                ElseIf _state = FakeBackendState.Running OrElse _state = FakeBackendState.Starting Then
                    _logger.Info("FakeVideoCaptureBackend: Dispose while Running — invoking stop path.")
                    _state = FakeBackendState.Stopping
                    _stopSignal = True
                    workerToJoin = _workerThread
                    needJoin = True
                Else
                    _logger.Info("FakeVideoCaptureBackend: Dispose from state '" & _state.ToString() & "'.")
                End If
            End SyncLock

            ' JOIN OUTSIDE THE LOCK — the worker needs _sync on every iteration.
            If needJoin AndAlso workerToJoin IsNot Nothing Then
                Try
                    If Not workerToJoin.Join(TimeSpan.FromSeconds(2)) Then
                        If _logger IsNot Nothing Then
                            _logger.Error("FakeVideoCaptureBackend: worker did not acknowledge stop within 2 s", Nothing)
                        End If
                    End If
                Catch ex As Exception
                    If _logger IsNot Nothing Then
                        _logger.Error("FakeVideoCaptureBackend: stop path failed during Dispose (will still dispose)", ex)
                    End If
                End Try
            End If

            ' RE-ACQUIRE LOCK ONLY TO FINALIZE STATE.
            SyncLock _sync
                If _state = FakeBackendState.Stopping Then
                    _state = FakeBackendState.Stopped
                End If
                _state = FakeBackendState.Disposed
                _logger.Info("FakeVideoCaptureBackend: disposed")
            End SyncLock
        End Sub

        ' ===== IVideoBackendDiagnostics =====

        Public ReadOnly Property EmittedFrames As Long Implements IVideoBackendDiagnostics.EmittedFrames
            Get
                SyncLock _sync
                    Return _emittedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property DroppedFrames As Long Implements IVideoBackendDiagnostics.DroppedFrames
            Get
                SyncLock _sync
                    Return _droppedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ReplacedFrames As Long Implements IVideoBackendDiagnostics.ReplacedFrames
            Get
                SyncLock _sync
                    Return _replacedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property NoFrameCount As Long Implements IVideoBackendDiagnostics.NoFrameCount
            Get
                SyncLock _sync
                    Return _noFrameCount
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ErrorCount As Long Implements IVideoBackendDiagnostics.ErrorCount
            Get
                SyncLock _sync
                    Return _errorCount
                End SyncLock
            End Get
        End Property

        ' ===== Test configuration (fluent API) =====

        ''' <summary>
        ''' Set the pixel format the fake will declare on emitted frames.
        ''' Default: Bgra8. Phase 1 baseline requires Bgra8 (§15.1); any
        ''' other value causes Initialize() to throw
        ''' VideoBackendConfigurationException (unless RequireBgra8 is False).
        ''' </summary>
        Public Function WithPixelFormat(format As VideoPixelFormat) As FakeVideoCaptureBackend
            SyncLock _sync
                _pixelFormat = format
                Return Me
            End SyncLock
        End Function

        Public Function WithDimensions(width As Integer, height As Integer) As FakeVideoCaptureBackend
            SyncLock _sync
                _dimensions = New VideoFrameDimensions(width, height)
                Return Me
            End SyncLock
        End Function

        Public Function WithOrigin(origin As VideoFrameOrigin) As FakeVideoCaptureBackend
            SyncLock _sync
                _origin = origin
                Return Me
            End SyncLock
        End Function

        ''' <summary>
        ''' When True (default), Initialize throws if PixelFormat is not Bgra8.
        ''' Set False to test the BGRA8 baseline enforcement path itself.
        ''' </summary>
        Public Function WithRequireBgra8(value As Boolean) As FakeVideoCaptureBackend
            SyncLock _sync
                _requireBgra8 = value
                Return Me
            End SyncLock
        End Function

        Public Function WithScript(descriptors As IEnumerable(Of FakeFrameDescriptor)) As FakeVideoCaptureBackend
            SyncLock _sync
                _script = New List(Of FakeFrameDescriptor)(descriptors)
                Return Me
            End SyncLock
        End Function

        ''' <summary>
        ''' Inject a custom frame factory. Used by ownership/lifetime tests to
        ''' return disposable frames whose Dispose() calls are observable.
        ''' </summary>
        Public Function WithFrameFactory(factory As Func(Of FrameDiagnostics, IVideoFrame)) As FakeVideoCaptureBackend
            SyncLock _sync
                _frameFactory = factory
                Return Me
            End SyncLock
        End Function

        ''' <summary>Delay between acquisition attempts (worker loop cadence).</summary>
        Public Function WithInterAttemptDelayMs(delayMs As Integer) As FakeVideoCaptureBackend
            SyncLock _sync
                _interAttemptDelayMs = delayMs
                Return Me
            End SyncLock
        End Function

        Public Function WithTickOrigin(origin As Long) As FakeVideoCaptureBackend
            SyncLock _sync
                _nextAttemptTicks = origin
                Return Me
            End SyncLock
        End Function

        ''' <summary>Snapshot the current backend state (for tests).</summary>
        Friend ReadOnly Property CurrentState As FakeBackendState
            Get
                SyncLock _sync
                    Return _state
                End SyncLock
            End Get
        End Property

        ' ===== Internal worker =====

        Private Sub WorkerLoop()
            Do
                Dim shouldStop As Boolean
                SyncLock _sync
                    shouldStop = _stopSignal
                End SyncLock
                If shouldStop Then Exit Do

                Dim descriptor As FakeFrameDescriptor = Nothing
                Dim sequence As Long
                Dim attemptTime As Long
                SyncLock _sync
                    If _script Is Nothing OrElse _script.Count = 0 OrElse _scriptIndex >= _script.Count Then
                        ' Default: produce FrameAvailable forever.
                        descriptor = FakeFrameDescriptor.FrameAvailable()
                    Else
                        descriptor = _script(_scriptIndex)
                        _scriptIndex += 1
                    End If
                    sequence = _nextSequence
                    _nextSequence += 1
                    attemptTime = _nextAttemptTicks
                    _nextAttemptTicks += _tickStride
                End SyncLock

                Try
                    ProcessDescriptor(descriptor, sequence, attemptTime)
                Catch ex As Exception
                    _logger.Error("FakeVideoCaptureBackend: worker loop error", ex)
                    SyncLock _sync
                        _errorCount += 1
                    End SyncLock
                End Try

                If _interAttemptDelayMs > 0 Then
                    Thread.Sleep(_interAttemptDelayMs)
                End If
            Loop

            _logger.Info("FakeVideoCaptureBackend: worker exited")
        End Sub

        Private Sub ProcessDescriptor(descriptor As FakeFrameDescriptor, sequence As Long, attemptTime As Long)
            Select Case descriptor.Kind
                Case FakeFrameDescriptorKind.FrameAvailable
                    Dim diag As New FrameDiagnostics(sequence, attemptTime, attemptTime)
                    Dim frame = _frameFactory(diag)
                    Dim result = FrameAcquisitionResult.Available(frame, sequence, attemptTime)
                    PushResult(result)

                Case FakeFrameDescriptorKind.NoFrame
                    SyncLock _sync
                        _noFrameCount += 1
                    End SyncLock
                    ' NoFrame is NOT pushed to the sink (§6.4). The sink only
                    ' sees FrameAvailable (and optionally Error).

                Case FakeFrameDescriptorKind.Error
                    SyncLock _sync
                        _errorCount += 1
                    End SyncLock
                    Dim exToUse As Exception = If(descriptor.Error, New InvalidOperationException("Fake error"))
                    Dim result = FrameAcquisitionResult.FromError(exToUse, sequence, attemptTime)
                    PushResult(result)

                Case FakeFrameDescriptorKind.ThrowRuntime
                    Dim ex As Exception = If(descriptor.Error, New InvalidOperationException("Fake throw-runtime"))
                    Throw New VideoBackendRuntimeException("FakeVideoCaptureBackend: scripted runtime error", ex)

                Case Else
                    Throw New InvalidOperationException("Unknown FakeFrameDescriptorKind: " & descriptor.Kind.ToString())
            End Select
        End Sub

        Private Sub PushResult(result As FrameAcquisitionResult)
            Dim sink = _sink
            If sink Is Nothing Then Return

            Dim outcome = sink.TryPush(result)
            SyncLock _sync
                Select Case outcome
                    Case PushOutcome.Pushed
                        _emittedFrames += 1
                    Case PushOutcome.Replaced
                        _emittedFrames += 1
                        _replacedFrames += 1
                    Case PushOutcome.Dropped
                        ' Backend still owns the frame — dispose it.
                        _droppedFrames += 1
                        Dim frame = result.Frame
                        If frame IsNot Nothing Then
                            Try
                                frame.Dispose()
                            Catch ex As Exception
                                _logger.Error("FakeVideoCaptureBackend: error disposing dropped frame", ex)
                            End Try
                        End If
                End Select
            End SyncLock
        End Sub

        Private Function DefaultFrameFactory(diag As FrameDiagnostics) As IVideoFrame
            Return New FakeVideoFrame(_origin, _pixelFormat, _dimensions, diag)
        End Function

        Private Sub ThrowIfDisposed()
            SyncLock _sync
                If _disposed Then
                    Throw New ObjectDisposedException(
                        NameOf(FakeVideoCaptureBackend),
                        "FakeVideoCaptureBackend has been disposed and can no longer be used.")
                End If
            End SyncLock
        End Sub

        ''' <summary>
        ''' Internal fake-backend state. (P1-A v1.3.1 §4.4)
        ''' Friend = assembly-internal; not part of the public API.
        ''' </summary>
        Friend Enum FakeBackendState
            Created
            Initializing
            Initialized
            Starting
            Running
            Stopping
            Stopped
            Faulted
            Disposed
        End Enum
    End Class
End Namespace
