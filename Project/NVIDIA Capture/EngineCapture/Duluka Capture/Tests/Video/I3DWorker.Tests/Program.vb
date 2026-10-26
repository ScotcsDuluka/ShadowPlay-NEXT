Option Strict On
Option Explicit On

' SPN-I3D-002: deterministic injected offline controller/worker tests only.
' NEVER invokes production Initialize/Start/WorkerLoop/AcquireNextFrame/DuplicateOutput.
Imports System
Imports System.Diagnostics
Imports System.Reflection
Imports System.Threading
Imports System.Threading.Tasks
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab

Module Program
    Private passed As Integer
    Private total As Integer
    Private ReadOnly flags As BindingFlags = BindingFlags.Instance Or BindingFlags.NonPublic

    Private Sub Check(ok As Boolean, message As String)
        If Not ok Then Throw New Exception(message)
    End Sub

    Private Function Field(b As DdagrabBackend, name As String) As FieldInfo
        Dim f = GetType(DdagrabBackend).GetField(name, flags)
        Check(f IsNot Nothing, "missing backend field " & name)
        Return f
    End Function

    Private Sub SetField(b As DdagrabBackend, name As String, value As Object)
        Field(b, name).SetValue(b, value)
    End Sub

    Private Function ReadField(b As DdagrabBackend, name As String) As Object
        Return Field(b, name).GetValue(b)
    End Function

    Private Function Invoke(b As DdagrabBackend, method As String, ParamArray arguments As Object()) As Object
        Dim m = GetType(DdagrabBackend).GetMethod(method, flags)
        Check(m IsNot Nothing, "missing method " & method)
        Try
            Return m.Invoke(b, arguments)
        Catch ex As TargetInvocationException When ex.InnerException IsNot Nothing
            Throw ex.InnerException
        End Try
    End Function

    Private Sub MustReject(action As Action, marker As String)
        Try
            action()
        Catch ex As Exception
            Check(ex.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase),
                  "unexpected rejection: " & ex.ToString())
            Return
        End Try
        Throw New Exception("expected rejection " & marker)
    End Sub

    Private Sub Scenario(name As String, action As Action)
        total += 1
        Try
            action()
            passed += 1
            Console.WriteLine("PASS " & name)
        Catch ex As Exception
            Console.WriteLine("FAIL " & name & " " & ex.ToString())
        End Try
    End Sub

    Private NotInheritable Class StubSink
        Implements IVideoFrameSink, ISourceGenerationLifecycleSink
        Public FaultCount As Integer
        Public LastGeneration As Long
        Public ReadOnly Entered As New ManualResetEventSlim(False)
        Public ReadOnly Release As New ManualResetEventSlim(False)
        Public Block As Boolean
        Public StopInCallback As Boolean
        Public ThrowInCallback As Boolean
        Public Target As DdagrabBackend
        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            Return PushOutcome.Dropped
        End Function
        Public Sub SourceFault(kind As SourceFaultKind, generation As Long) Implements ISourceGenerationLifecycleSink.SourceFault
            Interlocked.Increment(FaultCount)
            LastGeneration = generation
            Entered.Set()
            If ThrowInCallback Then Throw New InvalidOperationException("synthetic consumer failure")
            If StopInCallback Then Target.Stop()
            If Block AndAlso Not Release.Wait(7000) Then Throw New TimeoutException("fake callback barrier expired")
        End Sub
    End Class

    Private Sub ConfigureCallback(b As DdagrabBackend, sink As StubSink)
        SetField(b, "_state", DdagrabBackend.DdagrabBackendState.Running)
        SetField(b, "_sink", sink)
        SetField(b, "_callbacksEnabled", True)
        SetField(b, "_runId", 7L)
        SetField(b, "_sourceGeneration", 51L)
    End Sub

    Private Sub FaultGenerationAndStop()
        Dim b As New DdagrabBackend()
        Dim sink As New StubSink With {.Target = b}
        ConfigureCallback(b, sink)
        ' This fake models a RUNNING worker that has not relinquished its native lease.
        SetField(b, "_workerQuiescent", False)
        DirectCast(ReadField(b, "_quiescent"), ManualResetEventSlim).Reset()
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 6L, 51L)
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 7L, 50L)
        Check(sink.FaultCount = 0, "stale worker run/source generation emitted callback")
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 7L, 51L)
        Check(sink.FaultCount = 1 AndAlso sink.LastGeneration = 51L, "valid generation not forwarded")
        sink.StopInCallback = True
        Invoke(b, "NotifySourceFault", SourceFaultKind.InvalidFrame, 7L, 51L)
        Check(sink.FaultCount = 2, "reentrant Stop callback missing")
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 7L, 51L)
        Check(sink.FaultCount = 2, "callback emitted after Stop")
        Invoke(b, "CompleteWorkerExit")
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Stop callback left backend unreleasable")
    End Sub

    Private Sub CallbackCleanupLease()
        Dim b As New DdagrabBackend()
        Dim sink As New StubSink With {.Target = b, .Block = True}
        ConfigureCallback(b, sink)
        Dim notify = Task.Run(Sub() Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 7L, 51L))
        Check(sink.Entered.Wait(3000), "callback did not enter")
        Dim disposing = Task.Run(Sub() b.Dispose())
        Try
            Thread.Sleep(150)
            Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 0,
                "cleanup raced with admitted blocked callback")
        Finally
            sink.Release.Set()
        End Try
        Check(Task.WaitAll(New Task() {notify, disposing}, 7000), "callback or Dispose stalled")
        Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 1, "cleanup not once after callback")
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Dispose incomplete")
    End Sub

    Private Sub DisposeWorkerRace()
        Dim b As New DdagrabBackend()
        Dim go As New ManualResetEventSlim(False)
        Dim entered As New ManualResetEventSlim(False)
        Dim allowCompletion As New ManualResetEventSlim(False)
        Dim workerError As Exception = Nothing
        Dim worker As New Thread(Sub()
                                     Try
                                         go.Wait(7000)
                                         Invoke(b, "CompleteWorkerExit")
                                     Catch ex As Exception
                                         workerError = ex
                                     End Try
                                 End Sub)
        worker.IsBackground = True
        SetField(b, "_state", DdagrabBackend.DdagrabBackendState.Running)
        SetField(b, "_workerQuiescent", False)
        SetField(b, "_workerThread", worker)
        DirectCast(ReadField(b, "_quiescent"), ManualResetEventSlim).Reset()
        SetField(b, "BeforeDisposeCompletion", CType(Sub()
                                                         entered.Set()
                                                         If Not allowCompletion.Wait(7000) Then Throw New TimeoutException("dispose test hook stalled")
                                                     End Sub, Action))
        worker.Start()
        Dim closing = Task.Run(Sub() b.Dispose())
        Try
            Thread.Sleep(100)
            Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 0, "cleanup before worker returned")
            go.Set()
            Check(entered.Wait(4000), "Dispose did not reach final publication point")
            Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "worker cleanup didn't finalize")
        Finally
            go.Set()
            allowCompletion.Set()
        End Try
        Check(closing.Wait(7000) AndAlso worker.Join(7000), "worker/Dispose didn't finish")
        Check(workerError Is Nothing, "fake worker failed " & If(workerError?.ToString(), ""))
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Disposed regressed to Quarantined")
        Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 1, "cleanup not once")
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "repeat Dispose regressed")
    End Sub

    Private Sub InitializeDisposeLease()
        Dim b As New DdagrabBackend()
        Dim entered As New ManualResetEventSlim(False)
        Dim release As New ManualResetEventSlim(False)
        Dim init = Task.Run(Sub()
                                Invoke(b, "RunInitialize", CType(Sub()
                                                                     entered.Set()
                                                                     If Not release.Wait(7000) Then Throw New TimeoutException("init fake timed out")
                                                                 End Sub, Action))
                            End Sub)
        Check(entered.Wait(3000), "fake Initialize didn't enter")
        Dim disposeTask = Task.Run(Sub() b.Dispose())
        Try
            Thread.Sleep(100)
            Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 0, "cleanup with active Initialize")
        Finally
            release.Set()
        End Try
        Check(Task.WaitAll(New Task() {init, disposeTask}, 7000), "Initialize/Dispose didn't converge")
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Initialize/Dispose not final")
        Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 1, "Initialize/Dispose duplicate/missing cleanup")
    End Sub

    Private Sub StartDisposeLease()
        Dim b As New DdagrabBackend()
        SetField(b, "_state", DdagrabBackend.DdagrabBackendState.Initialized)
        Dim entered As New ManualResetEventSlim(False)
        Dim release As New ManualResetEventSlim(False)
        Dim startError As Exception = Nothing
        Dim starting = Task.Run(Sub()
                                    Try
                                        Invoke(b, "RunStart", New StubSink(),
                                            CType(Sub()
                                                      entered.Set()
                                                      If Not release.Wait(7000) Then Throw New TimeoutException("start fake timed out")
                                                  End Sub, Action),
                                            CType(Sub()
                                                  End Sub, ThreadStart))
                                    Catch ex As Exception
                                        startError = ex
                                    End Try
                                End Sub)
        Check(entered.Wait(3000), "fake Start didn't enter")
        Dim disposing = Task.Run(Sub() b.Dispose())
        Try
            Thread.Sleep(100)
            Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 0, "cleanup with active Start")
        Finally
            release.Set()
        End Try
        Check(Task.WaitAll(New Task() {starting, disposing}, 7000), "Start/Dispose didn't converge")
        Check(startError IsNot Nothing AndAlso TypeOf startError Is OperationCanceledException, "Start didn't fail closed")
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Start/Dispose not final")
        Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 1, "cleanup not once")
    End Sub

    Private Sub WorkerCrashAndRestartGuard()
        Dim b As New DdagrabBackend()
        SetField(b, "_state", DdagrabBackend.DdagrabBackendState.Initialized)
        Dim sink As New StubSink()
        Invoke(b, "RunStart", sink, CType(Sub() Invoke(b, "CompleteRecreation"), Action),
            CType(Sub()
                      Throw New InvalidOperationException("injected worker crash, no DXGI")
                  End Sub, ThreadStart))
        Check(SpinWait.SpinUntil(Function() b.CurrentState = DdagrabBackend.DdagrabBackendState.Faulted, 3000),
            "worker crash did not publish Faulted")
        MustReject(Sub() Invoke(b, "RunStart", sink, CType(Sub()
                                                         End Sub, Action), CType(Sub()
                                                                                 End Sub, ThreadStart)), "start_requires")
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "crashed worker not disposed")
    End Sub

    Private Sub SourceTimestampAndRecoveryBound()
        Dim b As New DdagrabBackend()
        Dim qpc = Stopwatch.Frequency * 5L
        Dim first = CLng(Invoke(b, "ValidateSourceTimestamp", qpc))
        Check(first > 0, "first valid synthetic QPC missing")
        MustReject(Sub() Invoke(b, "ValidateSourceTimestamp", qpc), "source_nonmonotonic")
        MustReject(Sub() Invoke(b, "ValidateSourceTimestamp", qpc - 1L), "source_nonmonotonic")
        MustReject(Sub() Invoke(b, "ValidateSourceTimestamp", 0L), "source_nonmonotonic")
        Dim second = CLng(Invoke(b, "ValidateSourceTimestamp", qpc + Stopwatch.Frequency))
        Check(second > first, "QPC did not advance")
        For i As Integer = 1 To 119
            Invoke(b, "RecordRecreationFailure", SourceFaultKind.AccessLost,
                New InvalidOperationException("synthetic access lost"))
        Next
        MustReject(Sub() Invoke(b, "RecordRecreationFailure", SourceFaultKind.AccessLost,
                                New InvalidOperationException("synthetic retry exhaustion")), "budget_exhausted")
        b.Dispose()
    End Sub

    Private Sub StopDuringInitialize()
        Dim b As New DdagrabBackend()
        Dim entered As New ManualResetEventSlim(False)
        Dim release As New ManualResetEventSlim(False)
        Dim initializing = Task.Run(Sub() Invoke(b, "RunInitialize",
            CType(Sub()
                      entered.Set()
                      If Not release.Wait(7000) Then Throw New TimeoutException("synthetic initialization stalled")
                  End Sub, Action)))
        Check(entered.Wait(3000), "Initialize lease not acquired")
        Dim stopping = Task.Run(Sub() b.Stop())
        Try
            Check(SpinWait.SpinUntil(Function() b.CurrentState = DdagrabBackend.DdagrabBackendState.Stopping, 3000),
                  "Stop did not publish stop during Initialize")
            Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 0, "cleanup while initializing")
        Finally
            release.Set()
        End Try
        Check(Task.WaitAll(New Task() {initializing, stopping}, 7000), "Stop/Initialize deadlock")
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Stopped, "Stop/Initialize wrong state")
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "Stop/Initialize disposal missed")
    End Sub

    Private Sub RestartStampsNewGeneration()
        Dim b As New DdagrabBackend()
        SetField(b, "_state", DdagrabBackend.DdagrabBackendState.Initialized)
        Dim sink As New StubSink()
        Dim worker As ThreadStart = Sub()
                                        Dim sw = Stopwatch.StartNew()
                                        While Not CBool(Invoke(b, "StopRequested"))
                                            If sw.ElapsedMilliseconds > 4000 Then Throw New TimeoutException("fake worker didn't observe Stop")
                                            Thread.Sleep(2)
                                        End While
                                    End Sub
        Dim recreate As Action = Sub() Invoke(b, "CompleteRecreation")
        Invoke(b, "RunStart", sink, recreate, worker)
        Dim firstGeneration = CLng(ReadField(b, "_sourceGeneration"))
        Dim firstRun = CLng(ReadField(b, "_runId"))
        Check(firstGeneration > 0 AndAlso firstRun > 0, "first worker stamp missing")
        b.Stop()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Stopped, "worker failed first Stop")
        Invoke(b, "RunStart", sink, recreate, worker)
        Dim nextGeneration = CLng(ReadField(b, "_sourceGeneration"))
        Dim nextRun = CLng(ReadField(b, "_runId"))
        Check(nextGeneration > firstGeneration AndAlso nextRun > firstRun, "restart reused old generation")
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, firstRun, firstGeneration)
        Check(sink.FaultCount = 0, "late callback after restart was accepted")
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, nextRun, nextGeneration)
        Check(sink.FaultCount = 1 AndAlso sink.LastGeneration = nextGeneration, "current callback rejected")
        b.Stop()
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "restart teardown incomplete")
        Check(CInt(ReadField(b, "_persistentCleanupClaimed")) = 1, "restart cleanup double released")
    End Sub

    Private Sub ThrowingFaultConsumerFailsClosed()
        Dim b As New DdagrabBackend()
        Dim sink As New StubSink With {.ThrowInCallback = True}
        ConfigureCallback(b, sink)
        Invoke(b, "NotifySourceFault", SourceFaultKind.InvalidFrame, 7L, 51L)
        Check(sink.FaultCount = 1, "throwing sink callback did not execute")
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Faulted, "consumer exception did not terminalize producer")
        Invoke(b, "NotifySourceFault", SourceFaultKind.AccessLost, 7L, 51L)
        Check(sink.FaultCount = 1, "callback delivered after faulted sink")
        b.Dispose()
        Check(b.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed, "throwing callback leaked lifecycle")
    End Sub

    Function Main(args As String()) As Integer
        Console.WriteLine("I3D AGENT A SYNTHETIC WORKER ONLY: NO DXGI DEVICES/PIXELS/AUDIO")
        Scenario("stale-generation faults and reentrant Stop", AddressOf FaultGenerationAndStop)
        Scenario("callback lease prevents early native cleanup", AddressOf CallbackCleanupLease)
        Scenario("worker Dispose publication is terminal", AddressOf DisposeWorkerRace)
        Scenario("Initialize/Dispose operation lease", AddressOf InitializeDisposeLease)
        Scenario("Start/Dispose operation lease", AddressOf StartDisposeLease)
        Scenario("worker crash requires rebuild", AddressOf WorkerCrashAndRestartGuard)
        Scenario("QPC rejection and bounded recreation failure", AddressOf SourceTimestampAndRecoveryBound)
        Scenario("Stop/Initialize operation lease", AddressOf StopDuringInitialize)
        Scenario("successful Stop/Start rebinding and stale callback", AddressOf RestartStampsNewGeneration)
        Scenario("throwing lifecycle callback fails closed", AddressOf ThrowingFaultConsumerFailsClosed)
        Console.WriteLine($"I3D_WORKER_SUMMARY Passed={passed} Failed={total - passed} Skipped=0")
        Return If(passed = total, 0, 1)
    End Function
End Module
