Option Strict On
Option Explicit On

' SPN-I3C-004: isolated offline harness. NEVER calls backend.Initialize,
' backend.Start, real WorkerLoop, DuplicateOutput or AcquireNextFrame.
Imports System
Imports System.Diagnostics
Imports System.Reflection
Imports System.Threading
Imports System.Threading.Tasks
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab

Module Program
    Private successes As Integer

    Function Main(args As String()) As Integer
        Console.WriteLine("SPN-I3C-004 B1 FAKE WORKER / NO DXGI")
        Run("Blocked Dispose quarantines resources and worker", AddressOf BlockedDispose)
        Run("Normal completed worker cleans once", AddressOf NormalDispose)
        Run("Overlapping Stop and Dispose is bounded", AddressOf ConcurrentStopDispose)
        Console.WriteLine($"B1 SUMMARY Passed={successes} Failed={3 - successes} Skipped=0")
        Return If(successes = 3, 0, 1)
    End Function

    Private Sub Run(name As String, test As Action)
        Try
            test()
            successes += 1
            Console.WriteLine("PASS: " & name)
        Catch ex As Exception
            Console.WriteLine("FAIL: " & name & " -> " & ex.ToString())
        End Try
    End Sub

    Private Sub Check(ok As Boolean, detail As String)
        If Not ok Then Throw New Exception(detail)
    End Sub

    Private NotInheritable Class NoopSink
        Implements IVideoFrameSink
        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            Return PushOutcome.Dropped
        End Function
    End Class

    Private NotInheritable Class FakeWorkerRig
        Implements IDisposable

        Public ReadOnly Backend As New DdagrabBackend()
        Public ReadOnly Worker As Thread
        Private ReadOnly ready As New ManualResetEventSlim(False)
        Private ReadOnly release As New ManualResetEventSlim(False)
        Public WorkerError As Exception

        Public Sub New()
            Dim exitMethod = GetType(DdagrabBackend).GetMethod("CompleteWorkerExit", BindingFlags.Instance Or BindingFlags.NonPublic)
            Check(exitMethod IsNot Nothing, "worker exit cleanup hook missing")
            Worker = New Thread(Sub()
                                    Try
                                        ready.Set()
                                        If Not release.Wait(TimeSpan.FromSeconds(20)) Then Throw New TimeoutException("fake worker fixture deadline")
                                        ' This is the SAME completion method used at WorkerLoop's real exit tail.
                                        ' No DXGI resource is created, accessed or captured.
                                        exitMethod.Invoke(Backend, Nothing)
                                    Catch ex As Exception
                                        WorkerError = ex
                                    End Try
                                End Sub)
            Worker.IsBackground = True
            Worker.Name = "Synthetic-Blocked-Worker-B1"
            Field("_state").SetValue(Backend, DdagrabBackend.DdagrabBackendState.Running)
            Field("_workerThread").SetValue(Backend, Worker)
            ' Astra lifecycle tracks native quiescence independently of Thread.IsAlive.
            ' A fake active worker must explicitly own the non-quiescent lease.
            Field("_workerQuiescent").SetValue(Backend, False)
            Dim readyEvent = DirectCast(Field("_quiescent").GetValue(Backend), ManualResetEventSlim)
            readyEvent.Reset()
            Worker.Start()
            Check(ready.Wait(3000), "fake worker did not enter")
        End Sub

        Private Function Field(name As String) As FieldInfo
            Dim f = GetType(DdagrabBackend).GetField(name, BindingFlags.Instance Or BindingFlags.NonPublic)
            Check(f IsNot Nothing, "missing private field " & name)
            Return f
        End Function

        Public ReadOnly Property CleanupClaims As Integer
            Get
                Return CInt(Field("_persistentCleanupClaimed").GetValue(Backend))
            End Get
        End Property

        Public Sub UnblockAndVerify()
            release.Set()
            Check(Worker.Join(8000), "fake worker did not exit")
            If WorkerError IsNot Nothing Then Throw New Exception("fake worker threw", WorkerError)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            release.Set()
            Worker.Join(8000)
        End Sub
    End Class

    Private Sub BlockedDispose()
        Using f As New FakeWorkerRig()
            Dim sw = Stopwatch.StartNew()
            f.Backend.Dispose()
            sw.Stop()
            Check(sw.ElapsedMilliseconds >= 4000 AndAlso sw.ElapsedMilliseconds < 7000,
                  "Dispose must have bounded 2s+3s timeout; elapsed=" & sw.ElapsedMilliseconds)
            Check(f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Quarantined,
                  "Dispose timeout should report Quarantined")
            Check(f.CleanupClaims = 0, "cleanup attempted before worker exited")
            Try
                f.Backend.Start(New NoopSink())
                Throw New Exception("Start accepted quarantined worker")
            Catch ex As ObjectDisposedException
                ' expected: no new producer on stale device
            End Try
            f.Backend.Dispose()
            Check(f.CleanupClaims = 0, "repeated Dispose prematurely cleaned worker resources")
            f.UnblockAndVerify()
            Check(f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed,
                  "final worker exit should complete dispose")
            Check(f.CleanupClaims = 1, "cleanup must claim exactly once after exit")
            f.Backend.Dispose()
            Check(f.CleanupClaims = 1, "third Dispose double-cleaned resources")
        End Using
    End Sub

    Private Sub NormalDispose()
        Using f As New FakeWorkerRig()
            f.UnblockAndVerify()
            Check(f.CleanupClaims = 0, "no Dispose requested; worker must not cleanup device")
            f.Backend.Dispose()
            Check(f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed,
                  "normal Dispose not final")
            Check(f.CleanupClaims = 1, "normal Dispose did not clean exactly once")
        End Using
    End Sub

    Private Sub ConcurrentStopDispose()
        Using f As New FakeWorkerRig()
            Dim stopTask = Task.Run(Sub() f.Backend.Stop())
            Check(SpinWait.SpinUntil(Function() f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Stopping, 3000),
                  "Stop did not enter Stopping")
            f.Backend.Dispose()
            Check(stopTask.Wait(5000), "Stop failed to complete its 2s budget")
            Check(f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Quarantined,
                  "Stop/Dispose must leave timeout quarantined")
            Check(f.CleanupClaims = 0, "Stop/Dispose prematurely cleaned")
            f.UnblockAndVerify()
            Check(f.Backend.CurrentState = DdagrabBackend.DdagrabBackendState.Disposed,
                  "Stop/Dispose final cleanup state missing")
            Check(f.CleanupClaims = 1, "Stop/Dispose should cleanup once")
        End Using
    End Sub
End Module
