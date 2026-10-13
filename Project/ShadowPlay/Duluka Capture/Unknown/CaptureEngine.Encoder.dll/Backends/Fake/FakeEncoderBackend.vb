Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video

Namespace CaptureEngine.Encoder.Backends.Fake
    ''' <summary>
    ''' FakeEncoderBackend — deterministic in-process encoder for contract/lifecycle/pipeline testing.
    ''' (P1-F §8)
    '''
    ''' PURPOSE:
    '''   - Prove the IEncoderBackend contract compiles + behaves correctly.
    '''   - Provide a deterministic target for lifecycle/concurrency/ownership tests.
    '''   - Generate deterministic synthetic EncodedPacket payloads for output testing.
    '''   - Validate timestamp/PTS propagation from IVideoFrame.Diagnostics to PacketMetadata.
    '''
    ''' DESIGN:
    '''   - Synchronous Encode (blocks until packet is ready — no async worker).
    '''   - 1 input frame → 1 output packet (Phase 1 contract; real NVENC may differ).
    '''   - Deterministic payload: byte[] filled with a predictable pattern derived
    '''     from Sequence + PTS — no random output, no time-dependent behavior.
    '''   - Deterministic keyframe cadence: every Nth packet is a keyframe, where
    '''     N = config.GopSize. First packet is ALWAYS a keyframe.
    '''   - Linux compatible: no GPU, no NVENC, no FFmpeg, no Windows APIs.
    '''   - Thread-safe: state mutations under _sync; counters via Interlocked.
    '''
    ''' LIFECYCLE (mirrors Foundation DdagrabBackend pattern):
    '''   Created → Initialize → Initialized → Start → Running ↔ Flushing →
    '''   Stopping → Stopped → Dispose → Disposed
    '''   (Faulted on failure — terminal until Dispose)
    '''
    ''' DISPOSE PATTERN (per P1-B.1 FIX #1):
    '''   - Capture state under lock
    '''   - Set stop signal + _disposed under lock
    '''   - RELEASE lock
    '''   - Join worker (if any) OUTSIDE lock
    '''   - Re-acquire lock ONLY to finalize state to Disposed
    '''   (FakeEncoderBackend has no worker thread — Join is a no-op here,
    '''    but the pattern is preserved for future real encoders.)
    ''' </summary>
    Public NotInheritable Class FakeEncoderBackend
        Implements IEncoderBackend
        Implements IEncoderDiagnostics

        ' ---- sync + state ----
        Private ReadOnly _sync As New Object()
        Private ReadOnly _logger As EngineLogger

        Private _state As EncoderState = EncoderState.Created
        Private _disposed As Boolean = False

        ' ---- config (captured at Initialize) ----
        Private _config As EncoderConfig

        ' ---- encoding state ----
        Private _nextSequence As Long = 0
        Private _framesSinceKeyframe As Integer = 0

        ' ---- diagnostics counters (Interlocked) ----
        Private _submittedFrames As Long = 0
        Private _encodedPackets As Long = 0
        Private _droppedFrames As Long = 0
        Private _flushCycles As Long = 0
        Private _errorCount As Long = 0
        Private _lastErrorIfAny As String = String.Empty
        Private _lastErrorType As String = String.Empty

        ' ---- in-flight queue (for Flush) ----
        Private ReadOnly _inFlight As New Queue(Of EncodedPacket)()

        Public Sub New(Optional logger As EngineLogger = Nothing)
            _logger = If(logger, New EngineLogger("FakeEncoderBackend"))
        End Sub

        ' ===== IEncoderBackend =====

        Public ReadOnly Property Diagnostics As IEncoderDiagnostics Implements IEncoderBackend.Diagnostics
            Get
                Return Me
            End Get
        End Property

        Public ReadOnly Property CurrentState As EncoderState Implements IEncoderBackend.CurrentState
            Get
                SyncLock _sync
                    Return _state
                End SyncLock
            End Get
        End Property

        Public Sub Initialize(config As EncoderConfig) Implements IEncoderBackend.Initialize
            If config Is Nothing Then
                Throw New ArgumentNullException(NameOf(config))
            End If

            SyncLock _sync
                ThrowIfDisposed()
                If _state <> EncoderState.Created Then
                    Throw New InvalidOperationException(
                        "Initialize cannot be called from state '" & _state.ToString() & "'. Expected 'Created'.")
                End If
                _state = EncoderState.Initialized ' transitional — validation below
            End SyncLock

            Try
                ValidateConfig(config)
                _config = config.Clone()
                _state = EncoderState.Initialized
                _logger.Info("FakeEncoderBackend: Initialize complete (codec=" & _config.CodecKey & ")")
            Catch ex As EncoderConfigurationException
                RecordError("config", ex.Message)
                SyncLock _sync
                    _state = EncoderState.Faulted
                End SyncLock
                Throw
            Catch ex As Exception
                RecordError("runtime", ex.Message)
                SyncLock _sync
                    _state = EncoderState.Faulted
                End SyncLock
                Throw New EncoderRuntimeException("Initialize failed unexpectedly", ex)
            End Try
        End Sub

        Public Sub Start() Implements IEncoderBackend.Start
            SyncLock _sync
                ThrowIfDisposed()
                Select Case _state
                    Case EncoderState.Running
                        _logger.Warning("FakeEncoderBackend: Start ignored, already Running.")
                        Return
                    Case EncoderState.Initialized, EncoderState.Stopped
                        _state = EncoderState.Running
                    Case Else
                        Throw New InvalidOperationException(
                            "Start cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock
            _logger.Info("FakeEncoderBackend: started")
        End Sub

        Public Function Encode(frame As IVideoFrame, ByRef packet As EncodedPacket) As Boolean Implements IEncoderBackend.Encode
            packet = Nothing
            If frame Is Nothing Then
                Throw New ArgumentNullException(NameOf(frame))
            End If

            SyncLock _sync
                ThrowIfDisposed()
                If _state <> EncoderState.Running Then
                    Throw New InvalidOperationException(
                        "Encode cannot be called from state '" & _state.ToString() & "'. Expected 'Running'.")
                End If
            End SyncLock

            ' Validate frame (per-frame validation — frames may vary)
            Try
                ValidateFrame(frame)
            Catch ex As EncoderRuntimeException
                RecordError("runtime", ex.Message)
                SyncLock _sync
                    _state = EncoderState.Faulted
                End SyncLock
                Throw
            End Try

            ' Accept the frame (BORROW — do NOT dispose)
            Interlocked.Increment(_submittedFrames)

            ' Synchronous encode: produce packet immediately
            Dim seq As Long = Interlocked.Increment(_nextSequence) - 1
            Dim isKey As Boolean = DetermineKeyframe(seq)
            Dim pts As Long = frame.Diagnostics.PresentationTimestampTicks
            Dim dts As Long = pts ' synchronous encoder: DTS == PTS
            Dim duration As Long = ComputeDurationTicks()

            Dim payload As Byte() = GenerateDeterministicPayload(seq, pts, isKey)
            Dim metadata As New PacketMetadata(
                sequence:=seq,
                presentationTimeTicks:=pts,
                decodingTimeTicks:=dts,
                durationTicks:=duration,
                isKeyFrame:=isKey,
                isReferenceFrame:=True,
                codecKey:=_config.CodecKey,
                codecSpecificFlags:=0)

            packet = New EncodedPacket(metadata, payload, payload.Length)
            Interlocked.Increment(_encodedPackets)
            Return True
        End Function

        Public Function Flush(sink As Action(Of EncodedPacket)) As Integer Implements IEncoderBackend.Flush
            If sink Is Nothing Then
                Throw New ArgumentNullException(NameOf(sink))
            End If

            Dim drained As New List(Of EncodedPacket)()

            SyncLock _sync
                ThrowIfDisposed()
                If _state <> EncoderState.Running Then
                    Throw New InvalidOperationException(
                        "Flush cannot be called from state '" & _state.ToString() & "'. Expected 'Running'.")
                End If
                _state = EncoderState.Flushing
                ' Snapshot in-flight packets under lock
                While _inFlight.Count > 0
                    drained.Add(_inFlight.Dequeue())
                End While
            End SyncLock

            ' Deliver OUTSIDE lock (sink may be slow / may throw)
            Dim delivered As Integer = 0
            For Each p As EncodedPacket In drained
                Try
                    sink(p)
                    delivered += 1
                Catch ex As Exception
                    ' Sink threw — dispose the packet to prevent leak, record error, transition to Faulted
                    p.Dispose()
                    RecordError("runtime", "Flush sink threw: " & ex.Message)
                    SyncLock _sync
                        _state = EncoderState.Faulted
                    End SyncLock
                    Throw New EncoderRuntimeException("Flush sink threw", ex)
                End Try
            Next

            Interlocked.Increment(_flushCycles)

            SyncLock _sync
                If _state = EncoderState.Flushing Then
                    _state = EncoderState.Running
                End If
            End SyncLock

            _logger.Info("FakeEncoderBackend: flushed " & delivered & " packets")
            Return delivered
        End Function

        Public Sub [Stop]() Implements IEncoderBackend.[Stop]
            Dim needFlush As Boolean = False

            SyncLock _sync
                ThrowIfDisposed()
                Select Case _state
                    Case EncoderState.Created, EncoderState.Initialized, EncoderState.Stopped
                        _logger.Warning("FakeEncoderBackend: Stop ignored, not Running (state=" & _state.ToString() & ").")
                        Return
                    Case EncoderState.Stopping
                        _logger.Warning("FakeEncoderBackend: Stop ignored, already Stopping.")
                        Return
                    Case EncoderState.Faulted
                        _logger.Warning("FakeEncoderBackend: Stop on Faulted state; no work to stop.")
                        Return
                    Case EncoderState.Running, EncoderState.Flushing
                        _state = EncoderState.Stopping
                        needFlush = True
                    Case Else
                        Throw New InvalidOperationException(
                            "Stop cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock

            ' Drain in-flight packets (OUTSIDE lock — sink callback may be slow)
            If needFlush Then
                Try
                    Flush(Sub(p)
                              ' Dispose immediately — we're stopping, no consumer
                              p.Dispose()
                          End Sub)
                Catch ex As Exception
                    _logger.Error("FakeEncoderBackend: drain during Stop failed", ex)
                End Try
            End If

            SyncLock _sync
                If _state = EncoderState.Stopping Then
                    _state = EncoderState.Stopped
                End If
            End SyncLock
            _logger.Info("FakeEncoderBackend: stopped")
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim workerToJoin As Thread = Nothing
            Dim needJoin As Boolean = False

            SyncLock _sync
                If _disposed Then Return
                _disposed = True

                If _state = EncoderState.Disposed Then
                    Return
                ElseIf _state = EncoderState.Running OrElse
                       _state = EncoderState.Flushing OrElse _state = EncoderState.Stopping Then
                    _logger.Info("FakeEncoderBackend: Dispose while " & _state.ToString() & " — invoking stop path.")
                    _state = EncoderState.Stopping
                    ' No worker thread in FakeEncoder — Join is a no-op
                Else
                    _logger.Info("FakeEncoderBackend: Dispose from state '" & _state.ToString() & "'.")
                End If
            End SyncLock

            ' Drain in-flight packets OUTSIDE lock
            Dim drained As New List(Of EncodedPacket)()
            SyncLock _sync
                While _inFlight.Count > 0
                    drained.Add(_inFlight.Dequeue())
                End While
            End SyncLock
            For Each p As EncodedPacket In drained
                Try
                    p.Dispose()
                Catch ex As Exception
                    _logger.Error("FakeEncoderBackend: in-flight packet Dispose failed during Dispose", ex)
                End Try
            Next

            SyncLock _sync
                _state = EncoderState.Disposed
                _logger.Info("FakeEncoderBackend: disposed")
            End SyncLock
        End Sub

        ' ===== IEncoderDiagnostics =====

        Public ReadOnly Property SubmittedFrames As Long Implements IEncoderDiagnostics.SubmittedFrames
            Get
                Return Interlocked.Read(_submittedFrames)
            End Get
        End Property

        Public ReadOnly Property EncodedPackets As Long Implements IEncoderDiagnostics.EncodedPackets
            Get
                Return Interlocked.Read(_encodedPackets)
            End Get
        End Property

        Public ReadOnly Property DroppedFrames As Long Implements IEncoderDiagnostics.DroppedFrames
            Get
                Return Interlocked.Read(_droppedFrames)
            End Get
        End Property

        Public ReadOnly Property FlushCycles As Long Implements IEncoderDiagnostics.FlushCycles
            Get
                Return Interlocked.Read(_flushCycles)
            End Get
        End Property

        Public ReadOnly Property ErrorCount As Long Implements IEncoderDiagnostics.ErrorCount
            Get
                Return Interlocked.Read(_errorCount)
            End Get
        End Property

        Public ReadOnly Property LastErrorIfAny As String Implements IEncoderDiagnostics.LastErrorIfAny
            Get
                Return _lastErrorIfAny
            End Get
        End Property

        Public ReadOnly Property LastErrorType As String Implements IEncoderDiagnostics.LastErrorType
            Get
                Return _lastErrorType
            End Get
        End Property

        ' ===== Test-visible state (Friend) =====

        ''' <summary>Snapshot the current backend state (for tests).</summary>
        Friend ReadOnly Property InternalState As EncoderState
            Get
                SyncLock _sync
                    Return _state
                End SyncLock
            End Get
        End Property

        ''' <summary>Snapshot the captured config (for tests).</summary>
        Friend ReadOnly Property CapturedConfig As EncoderConfig
            Get
                SyncLock _sync
                    Return _config
                End SyncLock
            End Get
        End Property

        ' ===== Private helpers =====

        Private Sub ThrowIfDisposed()
            If _disposed Then
                Throw New ObjectDisposedException(
                    NameOf(FakeEncoderBackend),
                    "FakeEncoderBackend has been disposed and can no longer be used.")
            End If
        End Sub

        Private Sub ValidateConfig(config As EncoderConfig)
            If String.IsNullOrEmpty(config.CodecKey) Then
                Throw New EncoderConfigurationException("CodecKey must be non-empty.")
            End If
            If config.BitrateBps <= 0 Then
                Throw New EncoderConfigurationException("BitrateBps must be > 0.")
            End If
            If config.GopSize <= 0 Then
                Throw New EncoderConfigurationException("GopSize must be > 0.")
            End If
            If config.RateControl <> "cbr" AndAlso config.RateControl <> "vbr" AndAlso config.RateControl <> "cq" Then
                Throw New EncoderConfigurationException("RateControl must be 'cbr', 'vbr', or 'cq'.")
            End If
            If config.MinrateBps > config.MaxrateBps Then
                Throw New EncoderConfigurationException("MinrateBps must be <= MaxrateBps.")
            End If
            If config.BufsizeBps < config.BitrateBps Then
                Throw New EncoderConfigurationException("BufsizeBps must be >= BitrateBps.")
            End If
            If config.MaxInFlightFrames <= 0 Then
                Throw New EncoderConfigurationException("MaxInFlightFrames must be > 0.")
            End If
            If config.FlushTimeoutMs <= 0 Then
                Throw New EncoderConfigurationException("FlushTimeoutMs must be > 0.")
            End If
            If config.StopTimeoutMs <= 0 Then
                Throw New EncoderConfigurationException("StopTimeoutMs must be > 0.")
            End If
        End Sub

        Private Sub ValidateFrame(frame As IVideoFrame)
            If _config.ExpectedWidth > 0 AndAlso frame.Dimensions.Width <> _config.ExpectedWidth Then
                Throw New EncoderRuntimeException(
                    "Frame width " & frame.Dimensions.Width &
                    " does not match expected " & _config.ExpectedWidth & ".")
            End If
            If _config.ExpectedHeight > 0 AndAlso frame.Dimensions.Height <> _config.ExpectedHeight Then
                Throw New EncoderRuntimeException(
                    "Frame height " & frame.Dimensions.Height &
                    " does not match expected " & _config.ExpectedHeight & ".")
            End If
            If frame.PixelFormat <> _config.ExpectedInputFormat Then
                Throw New EncoderRuntimeException(
                    "Frame pixel format " & frame.PixelFormat.ToString() &
                    " does not match expected " & _config.ExpectedInputFormat.ToString() & ".")
            End If
        End Sub

        Private Function DetermineKeyframe(seq As Long) As Boolean
            ' First packet is always a keyframe; every GopSize-th packet after that.
            If seq = 0 Then Return True
            Return (seq Mod _config.GopSize) = 0
        End Function

        Private Function ComputeDurationTicks() As Long
            ' Approximate: 1 frame duration = ticks_per_second / framerate
            ' FakeEncoder doesn't know framerate from config alone — use a
            ' fixed 166,667 ticks (~10ms at 100ns resolution, 100 FPS) as a
            ' deterministic placeholder. Real encoders compute from config.
            Return 166667L
        End Function

        Private Function GenerateDeterministicPayload(seq As Long, pts As Long, isKey As Boolean) As Byte()
            ' Deterministic payload: 32 bytes (small but non-trivial).
            ' Pattern: byte i = (seq * 31 + pts + i) XOR (isKey ? 0xFF : 0x00)
            ' This is NOT random — same inputs always produce same output.
            Dim size As Integer = If(isKey, 64, 32) ' keyframes slightly larger
            Dim payload(size - 1) As Byte
            Dim keyMask As Byte = If(isKey, CByte(&HFF), CByte(&H0))
            For i As Integer = 0 To size - 1
                payload(i) = CByte((seq * 31L + pts + i) And &HFF) Xor keyMask
            Next
            Return payload
        End Function

        Private Sub RecordError(errorType As String, message As String)
            Interlocked.Increment(_errorCount)
            _lastErrorIfAny = If(message, String.Empty)
            _lastErrorType = errorType
        End Sub
    End Class
End Namespace
