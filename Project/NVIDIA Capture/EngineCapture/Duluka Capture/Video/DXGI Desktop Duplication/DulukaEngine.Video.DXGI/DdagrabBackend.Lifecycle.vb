Option Strict On
Option Explicit On

Imports System.Diagnostics
Imports System.Threading
Imports System.Collections.Generic

Namespace DulukaEngine.Video.Backends.Ddagrab
    Partial Public NotInheritable Class DdagrabBackend
        ' All ownership transitions use _sync; native work and bounded waits never do.
        ' Quiescence means the last native use has returned, not Thread.IsAlive=False.
        Private _activeOperations As Integer
        Private _operationThread As Thread
        ' External lifecycle callbacks must drain before native cleanup, too.
        Private _activeCallbacks As Integer
        ' Per-backend callback owners, avoiding cross-backend ThreadStatic aliasing.
        Private ReadOnly _callbackThreads As New Dictionary(Of Integer, Integer)()
        Private _workerQuiescent As Boolean = True
        Private ReadOnly _quiescent As New ManualResetEventSlim(True)
        Private _cleanupAfterInitFailure As Boolean
        Private _persistentCleanupCompleted As Boolean
        Private _callbacksEnabled As Boolean
        Private _runId As Long
        ' Offline barrier only; production leaves this unset.
        Friend BeforeDisposeCompletion As Action

        Friend Sub RunInitialize(initializeResources As Action)
            SyncLock _sync
                ThrowIfDisposed()
                If _state <> DdagrabBackendState.Created Then Throw New InvalidOperationException("initialize_requires_created")
                _state = DdagrabBackendState.Initializing
                BeginOperationLocked()
            End SyncLock
            Try
                initializeResources()
                SyncLock _sync
                    If Not _disposed AndAlso Not _stopSignal Then _state = DdagrabBackendState.Initialized
                End SyncLock
            Catch
                SyncLock _sync
                    _cleanupAfterInitFailure = True
                    If Not _disposed Then _state = DdagrabBackendState.Faulted
                End SyncLock
                Throw
            Finally
                EndOperation()
            End Try
        End Sub

        Friend Sub RunStart(sink As IVideoFrameSink, recreate As Action, workerBody As ThreadStart)
            If sink Is Nothing Then Throw New ArgumentNullException(NameOf(sink))
            Dim run As Long
            SyncLock _sync
                ThrowIfDisposed()
                If _state = DdagrabBackendState.Starting OrElse _state = DdagrabBackendState.Running Then Return
                If _state <> DdagrabBackendState.Initialized AndAlso _state <> DdagrabBackendState.Stopped Then
                    Throw New InvalidOperationException("start_requires_initialized_or_stopped")
                End If
                _state = DdagrabBackendState.Starting
                BeginOperationLocked()
                _sink = sink
                _stopSignal = False
                _callbacksEnabled = True
                _runId += 1
                run = _runId
                _lastSourceQpcTicks = 0
                _lastSourceQpc100ns = -1
                _timestampFallbackCount = 0
                _duplicationRecreateFailures = 0
                _recoveryStarted = 0
            End SyncLock
            Try
                recreate()
                SyncLock _sync
                    If _disposed OrElse _stopSignal Then Throw New OperationCanceledException("source_start_cancelled")
                    _workerQuiescent = False
                    _state = DdagrabBackendState.Running
                    Try
                        _workerThread = New Thread(Sub() RunWorker(workerBody, run)) With {
                            .IsBackground = True, .Name = "DdagrabBackend.Worker"
                        }
                        _workerThread.Start()
                    Catch
                        _workerQuiescent = True
                        _workerThread = Nothing
                        _state = DdagrabBackendState.Faulted
                        Throw
                    End Try
                End SyncLock
            Catch
                SyncLock _sync
                    If Not _disposed AndAlso Not _stopSignal Then _state = DdagrabBackendState.Faulted
                    _callbacksEnabled = False
                End SyncLock
                Throw
            Finally
                EndOperation()
            End Try
        End Sub

        Private Sub BeginOperationLocked()
            _activeOperations += 1
            _operationThread = Thread.CurrentThread
            _quiescent.Reset()
        End Sub

        Private Sub EndOperation()
            SyncLock _sync
                _activeOperations -= 1
                _operationThread = Nothing
            End SyncLock
            FinishLifecycle()
        End Sub

        Private Sub RunWorker(body As ThreadStart, run As Long)
            Try
                body()
            Catch ex As Exception
                ' A crash cannot silently authorize a new producer on the same device.
                Interlocked.Increment(_errorCount)
                SyncLock _sync
                    If Not _disposed AndAlso run = _runId Then
                        _terminalFault = If(TypeOf ex Is SourceFailureException,
                            DirectCast(ex, SourceFailureException).Kind.ToString(), "InvalidFrame") & ":" & ex.Message
                        _state = DdagrabBackendState.Faulted
                    End If
                End SyncLock
                NotifySourceFault(If(TypeOf ex Is SourceFailureException,
                    DirectCast(ex, SourceFailureException).Kind, SourceFaultKind.InvalidFrame), run, SourceGeneration)
                Try : _logger.Error("DdagrabBackend: worker fault; rebuild required", ex) : Catch : End Try
            Finally
                CompleteWorkerExit()
            End Try
        End Sub

        Public Sub [Stop]() Implements IVideoCaptureBackend.Stop
            Dim self As Boolean
            SyncLock _sync
                ThrowIfDisposed()
                If _state = DdagrabBackendState.Created OrElse _state = DdagrabBackendState.Initialized OrElse
                    _state = DdagrabBackendState.Stopped Then Return
                _stopSignal = True
                _callbacksEnabled = False
                If _state <> DdagrabBackendState.Faulted Then _state = DdagrabBackendState.Stopping
                self = Thread.CurrentThread Is _workerThread OrElse Thread.CurrentThread Is _operationThread OrElse
                    _callbackThreads.ContainsKey(Thread.CurrentThread.ManagedThreadId)
            End SyncLock
            FinishLifecycle()
            ' Every overlapping Stop observes the same quiescence signal. A callback
            ' on the worker/initialization thread must never wait for itself.
            If Not self AndAlso Not _quiescent.Wait(TimeSpan.FromSeconds(2)) Then
                SyncLock _sync
                    ' Recheck under the lifecycle lock: a worker can exit just
                    ' as the bounded wait expires. A still-owned worker makes
                    ' recording success unsafe even if it later shuts down.
                    If Not _quiescent.IsSet Then
                        If String.IsNullOrEmpty(_terminalFault) Then _terminalFault = "source_stop_timeout_not_quiescent"
                        If Not _disposed Then _state = DdagrabBackendState.Faulted
                    End If
                End SyncLock
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim self As Boolean
            SyncLock _sync
                If _disposed Then Return
                _disposed = True
                _stopSignal = True
                _callbacksEnabled = False
                _state = DdagrabBackendState.Stopping
                self = Thread.CurrentThread Is _workerThread OrElse Thread.CurrentThread Is _operationThread OrElse
                    _callbackThreads.ContainsKey(Thread.CurrentThread.ManagedThreadId)
            End SyncLock
            FinishLifecycle()
            If Not self Then _quiescent.Wait(TimeSpan.FromSeconds(5))
            Try
                BeforeDisposeCompletion?.Invoke()
            Finally
                FinishLifecycle()
                SyncLock _sync
                    ' Disposed is terminal, including worker-exit during our finalization.
                    ' Only incomplete quiescence/cleanup can be quarantined.
                    If _state <> DdagrabBackendState.Disposed Then _state = DdagrabBackendState.Quarantined
                End SyncLock
            End Try
        End Sub

        Private Sub CompleteWorkerExit()
            SyncLock _sync
                _workerQuiescent = True
                _callbacksEnabled = False
            End SyncLock
            FinishLifecycle()
        End Sub

        Private Sub FinishLifecycle()
            Dim cleanup As Boolean
            SyncLock _sync
                If _activeOperations <> 0 OrElse _activeCallbacks <> 0 OrElse Not _workerQuiescent Then Return
                If _disposed OrElse _cleanupAfterInitFailure Then
                    If _persistentCleanupClaimed <> 0 Then
                        If _persistentCleanupCompleted AndAlso _disposed Then _state = DdagrabBackendState.Disposed
                        Return
                    End If
                    _persistentCleanupClaimed = 1
                    cleanup = True
                Else
                    If _state = DdagrabBackendState.Stopping OrElse _state = DdagrabBackendState.Running Then
                        _state = DdagrabBackendState.Stopped
                    End If
                    _sink = Nothing
                    _quiescent.Set()
                End If
            End SyncLock
            If cleanup Then
                Try
                    CleanupPersistentResources()
                Finally
                    SyncLock _sync
                        _sink = Nothing
                        _persistentCleanupCompleted = True
                        _state = If(_disposed, DdagrabBackendState.Disposed, DdagrabBackendState.Faulted)
                        _quiescent.Set()
                    End SyncLock
                End Try
            End If
        End Sub

        Friend Sub NotifySourceFault(kind As SourceFaultKind, run As Long, generation As Long)
            Dim receiver As IVideoFrameSink
            Dim callbackThreadId = Thread.CurrentThread.ManagedThreadId
            SyncLock _sync
                If Not _callbacksEnabled OrElse _disposed OrElse _stopSignal OrElse
                    run <> _runId OrElse generation <> _sourceGeneration Then Return
                receiver = _sink
                If receiver Is Nothing Then Return
                _activeCallbacks += 1
                Dim depth As Integer
                _callbackThreads.TryGetValue(callbackThreadId, depth)
                _callbackThreads(callbackThreadId) = depth + 1
                _quiescent.Reset()
            End SyncLock
            Try
                Dim stamped = TryCast(receiver, ISourceGenerationLifecycleSink)
                If stamped IsNot Nothing Then
                    stamped.SourceFault(kind, generation)
                Else
                    TryCast(receiver, ISourceLifecycleSink)?.SourceFault(kind)
                End If
            Catch
                Interlocked.Increment(_errorCount)
                SyncLock _sync
                    _terminalFault = "source_fault_callback_failed"
                    _callbacksEnabled = False
                    _stopSignal = True
                    If Not _disposed Then _state = DdagrabBackendState.Faulted
                End SyncLock
            Finally
                SyncLock _sync
                    _activeCallbacks -= 1
                    Dim remaining = _callbackThreads(callbackThreadId) - 1
                    If remaining = 0 Then
                        _callbackThreads.Remove(callbackThreadId)
                    Else
                        _callbackThreads(callbackThreadId) = remaining
                    End If
                End SyncLock
                FinishLifecycle()
            End Try
        End Sub
    End Class
End Namespace
