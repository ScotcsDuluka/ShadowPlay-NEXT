Option Strict On
Imports System.Threading
Imports System.Threading.Tasks
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab
Imports DulukaEngine.Video.Transport.D3D11

Namespace DulukaEngine.Recording
    ' I3-C1: adapter contract for an already-initialized, verified frame producer.
    ' Does NOT initialize DXGI or create a duplication/encoder. The production
    ' Ddagrab worker has no implementation of this interface in this phase.
    Public Interface ISourceCompositionProducer
        Inherits IDisposable
        ReadOnly Property ActualTarget As CaptureTarget
        ReadOnly Property IsTestOwnedSynthetic As Boolean
        Function PrepareSourceGeneration() As Long
        Sub Start(sink As IVideoFrameSink)
        Sub [Stop]()
    End Interface

    ' Dormant composition boundary. Existing RecordingEngine/CaptureSession still
    ' own encoder, packet stream, session clock, media verification and mux.
    ' ONLY test-owned producers are accepted until a separate live authorization.
    Public NotInheritable Class SourceCompositionDriver
        Implements IDisposable

        Private ReadOnly _gate As New Object()
        Private ReadOnly _exited As New ManualResetEventSlim(True)
        Private ReadOnly _engine As RecordingEngine
        Private ReadOnly _producer As ISourceCompositionProducer
        Private ReadOnly _source As BgraSessionSource
        Private _started As Boolean
        Private _running As Boolean
        Private _stopped As Boolean
        Private _disposed As Boolean

        Public ReadOnly Property Source As BgraSessionSource
            Get
                Return _source
            End Get
        End Property
        Public ReadOnly Property ReservedGeneration As Long
            Get
                Return _source.InitialGeneration
            End Get
        End Property

        Private Sub New(engine As RecordingEngine, producer As ISourceCompositionProducer, source As BgraSessionSource)
            _engine = engine : _producer = producer : _source = source
        End Sub

        Private Shared Sub RequireExactTarget(selected As CaptureTarget, actual As CaptureTarget)
            If selected Is Nothing OrElse actual Is Nothing OrElse Not selected.Eligible OrElse Not actual.Eligible OrElse
                Not selected.SameOutput(actual) OrElse selected.TopologyId <> actual.TopologyId OrElse
                Not String.Equals(selected.Adapter.DevicePath, actual.Adapter.DevicePath, StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidOperationException("composition_target_identity_mismatch")
            End If
        End Sub

        Public Shared Function Prepare(engine As RecordingEngine, producer As ISourceCompositionProducer,
                                       selected As CaptureTarget, width As Integer, height As Integer, fps As Integer,
                                       color As VideoColorDescriptor, Optional capacity As Integer = 4,
                                       Optional waitTimeoutMs As Integer = 2000) As SourceCompositionDriver
            If engine Is Nothing Then Throw New ArgumentNullException(NameOf(engine))
            If producer Is Nothing Then Throw New ArgumentNullException(NameOf(producer))
            ' Deliberately closed: this phase cannot arm production DXGI capture.
            If Not producer.IsTestOwnedSynthetic Then Throw New InvalidOperationException("intel_native_live_input_not_verified")
            RequireExactTarget(selected, producer.ActualTarget)
            Dim current = CaptureTarget.RequireCurrent(selected, DxgiDisplayInventory.Collect().Outputs)
            ' TopologyId is an inventory-SNAPSHOT nonce (a new GUID on each
            ' Collect), not a persistent CCD display identifier. Compare all
            ' stable target fields to the CURRENT snapshot, then preserve the
            ' producer's original epoch token for stamped-frame equality.
            If Not selected.SameOutput(current) OrElse
                Not String.Equals(selected.Adapter.DevicePath, current.Adapter.DevicePath, StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidOperationException("composition_target_stale_or_ambiguous")
            End If
            ' Ddagrab's reservation convention: initialized/idle only; this
            ' call must be metadata-only, never Start/Initialize/AcquireNextFrame.
            Dim reserved = producer.PrepareSourceGeneration()
            If reserved <= 0 Then Throw New InvalidOperationException("composition_invalid_source_generation")
            RequireExactTarget(selected, producer.ActualTarget)
            Dim source As New BgraSessionSource(producer.ActualTarget, reserved, width, height, fps, color, capacity, waitTimeoutMs)
            Return New SourceCompositionDriver(engine, producer, source)
        End Function

        ' Caller uses the original initialized RecordingEngine. Run is one-shot,
        ' and the producer must explicitly mark its finite synthetic stream
        ' complete. The session can drain while Start invokes the fake worker.
        Public Function Run(config As SessionConfig) As SessionResult
            If config Is Nothing Then Throw New ArgumentNullException(NameOf(config))
            SyncLock _gate
                If _disposed OrElse _stopped OrElse _started Then Throw New InvalidOperationException("composition_not_prepared")
                _started = True : _running = True : _exited.Reset()
            End SyncLock
            Dim session As Task(Of SessionResult) = Nothing
            Try
                session = Task.Run(Function() _engine.StartSourceSession(config, _source))
                _producer.Start(_source)
                Return session.GetAwaiter().GetResult()
            Catch
                Me.[Stop]()
                If session IsNot Nothing AndAlso Not session.Wait(30000) Then
                    Throw New TimeoutException("composition_session_join_timeout")
                End If
                Throw
            Finally
                ' Always release the join signal, including on Stop failure.
                Try
                    Me.[Stop]()
                Finally
                    SyncLock _gate
                        _running = False : _exited.Set()
                    End SyncLock
                End Try
            End Try
        End Function

        Public Sub [Stop]()
            SyncLock _gate
                If _stopped Then Return
                _stopped = True
            End SyncLock
            ' Cancel conversions first, then stop producer, then session.
            _source.Stop()
            Try
                _producer.Stop()
            Finally
                _engine.Stop()
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _gate
                If _disposed Then Return
            End SyncLock
            Me.[Stop]()
            If Not _exited.Wait(30000) Then
                ' Fail closed: do not release a backend under a wedged worker.
                Throw New TimeoutException("composition_dispose_wait_for_session_timeout")
            End If
            SyncLock _gate
                If _disposed Then Return
                _source.Dispose()
                _producer.Dispose()
                _disposed = True
            End SyncLock
        End Sub
    End Class
End Namespace
