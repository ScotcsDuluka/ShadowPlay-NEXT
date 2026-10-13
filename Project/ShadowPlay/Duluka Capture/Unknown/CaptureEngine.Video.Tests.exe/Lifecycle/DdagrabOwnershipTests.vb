Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video
Imports CaptureEngine.Video.Backends.Ddagrab
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Lifecycle

    ''' <summary>
    ''' C-1/C-2 ownership &amp; concurrency pass — real D3D11/DXGI backend.
    '''
    ''' C-1: backend Start atomicity. A throw INSIDE Start() (injected at
    ''' the backend's own logger sink — the post-commit "started" line)
    ''' must leave the state HONEST: Running with a live worker (caller
    ''' Stop recovers), never Faulted-with-live-worker.
    ''' C-2: no permanent Running/Stopping without a live worker.
    '''
    ''' NOT runtime-injectable without a real desktop/DXGI failure and
    ''' therefore NOT faked here: worker-crash-while-Running (the exit
    ''' tail handles it — Running→Stopped with an Error log),
    ''' Stop-timeout wedging, Start-during-Stopping, Dispose-during-Stop-
    ''' timeout. Those paths are code-reviewed contract extensions of the
    ''' same SyncLock-serialized state machine these tests exercise.
    ''' </summary>
    Friend NotInheritable Class DdagrabOwnershipTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("DDAGRAB C-1: throw after Start commit → state stays Running, Stop recovers",
                   AddressOf Test_StartThrowAfterCommit)
            runner("DDAGRAB C-2: Stop while worker live → Stopped + worker quiesces",
                   AddressOf Test_StopQuiescesWorker)
            runner("DDAGRAB C-2: 5 repeated Start/Stop cycles → textures balanced, state honest",
                   AddressOf Test_RepeatedCycles)
            runner("DDAGRAB C-2C: worker crash while Running → no zombie, next session works (real NVIDIA)",
                   AddressOf Test_WorkerCrashWhileRunning)
            runner("DDAGRAB C-2: 50-cycle mixed stress (stop/slow/timeout/crash) — workers, textures, state",
                   AddressOf Test_MixedStress50)
        End Sub

        ' ---- helpers ----

        Private Shared Function CreateBackend(logger As EngineLogger) As DdagrabBackend
            Return New DdagrabBackend(logger)
        End Function

        Private Shared Function CreateContext() As IVideoBackendContext
            Return New FakeVideoBackendContext(
                VideoBackendKind.Ddagrab,
                New EngineLogger("FakeBackendCtx", EngineLogger.LogLevel.Warning))
        End Function

        ''' <summary>Logger sink that throws exactly when Start() reaches its
        ''' post-commit "started" line — the C-1 failure injection point
        ''' (EngineLogger.Write invokes the sink with no try/catch).</summary>
        Private Shared Sub ThrowOnStartComplete(line As String)
            If line IsNot Nothing AndAlso line.Contains("started (real DXGI capture)") Then
                Throw New InvalidOperationException("injected logger failure (C-1 repro)")
            End If
        End Sub

        Private Shared Function WaitTrue(predicate As Func(Of Boolean), timeoutMs As Integer) As Boolean
            Dim deadline As Long = DateTime.UtcNow.Ticks + timeoutMs * 10000L
            While DateTime.UtcNow.Ticks < deadline
                Try
                    If predicate() Then Return True
                Catch
                End Try
                Thread.Sleep(20)
            End While
            Return False
        End Function

        ' ---- tests ----

        ''' <summary>Deterministic C-1 reproduction: with the OLD order
        ''' (spawn worker → commit Running last) the injected throw hit
        ''' BEFORE the commit → catch marked Faulted with a LIVE worker →
        ''' the session's captureRunning flag (set after the call) missed
        ''' the unwind → orphan worker + backend bricked. The fixed order
        ''' commits Running first, so the SAME injection leaves an honest
        ''' Running that Stop() fully recovers.</summary>
        Private Shared Sub Test_StartThrowAfterCommit()
            Dim logger As New EngineLogger("DdagrabC1", EngineLogger.LogLevel.Info,
                                           AddressOf ThrowOnStartComplete)
            Dim backend = CreateBackend(logger)
            backend.Initialize(CreateContext())

            Dim threw As Boolean = False
            Try
                backend.Start(New RecordingVideoFrameSink())
            Catch ex As Exception
                threw = True
            End Try
            TestHelpers.Assert(threw, "Start threw (injected post-commit logger failure)")

            ' THE contract: the worker is live, so the state must say so.
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Running,
                backend.CurrentState,
                "state after post-commit throw must be Running (honest), not Faulted")

            ' The session unwind (captureRunning=True set BEFORE Start) then
            ' Stop()s the backend — must fully recover.
            backend.[Stop]()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Stopped,
                backend.CurrentState, "Stop recovers the committed-Running backend")

            backend.Dispose()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "Dispose after recovery")
        End Sub

        Private Shared Sub Test_StopQuiescesWorker()
            Dim backend = CreateBackend(New EngineLogger("DdagrabC2", EngineLogger.LogLevel.Warning))
            backend.Initialize(CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            ' Worker proven live: it observed at least one desktop frame.
            TestHelpers.Assert(
                WaitTrue(Function() backend.Diagnostics.NoFrameCount > 0 OrElse
                                    backend.Diagnostics.EmittedFrames > 0,
                         10000),
                "worker processed at least one capture attempt")

            backend.[Stop]()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Stopped,
                backend.CurrentState, "state after Stop")

            ' Worker quiesced: no further capture activity after Stop.
            Dim noFrameAtStop As Long = backend.Diagnostics.NoFrameCount
            Dim emittedAtStop As Long = backend.Diagnostics.EmittedFrames
            Thread.Sleep(400)
            TestHelpers.Assert(backend.Diagnostics.NoFrameCount = noFrameAtStop,
                               "NoFrameCount frozen after Stop (worker dead)")
            TestHelpers.Assert(backend.Diagnostics.EmittedFrames = emittedAtStop,
                               "EmittedFrames frozen after Stop (worker dead)")

            backend.Dispose()
        End Sub

        Private Shared Sub Test_RepeatedCycles()
            Dim backend = CreateBackend(New EngineLogger("DdagrabC2c", EngineLogger.LogLevel.Warning))
            backend.Initialize(CreateContext())

            ' Sink override Dropped → the backend itself disposes every frame
            ' → after each Stop the texture lifecycle counters must balance
            ' (no stranded staging texture, no double-count).
            Dim sink As New RecordingVideoFrameSink()
            sink.SetOutcomeOverride(PushOutcome.Dropped)

            For cycle As Integer = 1 To 5
                backend.Start(sink)
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Running,
                    backend.CurrentState, $"cycle {cycle}: state after Start")

                ' Activity probe (evidence, not a precondition — see the
                ' note in Test_StopQuiescesWorker). The state + texture
                ' assertions below are the actual contract under test.
                WaitTrue(Function() backend.Diagnostics.NoFrameCount > 0 OrElse
                                    backend.Diagnostics.EmittedFrames > 0 OrElse
                                    backend.Diagnostics.ErrorCount > 0, 10000)

                backend.[Stop]()
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Stopped,
                    backend.CurrentState, $"cycle {cycle}: state after Stop")

                TestHelpers.AssertEqual(
                    backend.TexturesCreated, backend.TexturesDisposed,
                    $"cycle {cycle}: every staging texture disposed")
            Next

            backend.Dispose()
        End Sub

        ' ───────────────────────────────────────────────────────────────
        ' C-2C — WORKER CRASH WHILE RUNNING (real NVIDIA, deterministic)
        '
        ' A natural crash needs a DXGI/driver fault that cannot be forced
        ' safely, so the backend exposes a Friend seam that throws from the
        ' TOP of a worker iteration — no COM object, no frame held — and the
        ' exception travels the identical termination path as a real crash:
        ' outer catch → worker-exit tail → Running→Stopped.
        ' ───────────────────────────────────────────────────────────────
        Private Shared Sub Test_WorkerCrashWhileRunning()
            Dim proc As Process = Process.GetCurrentProcess()
            Dim backend = CreateBackend(New EngineLogger("DdagrabCrash", EngineLogger.LogLevel.Warning))
            backend.Initialize(CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            sink.SetOutcomeOverride(PushOutcome.Dropped)   ' backend disposes every frame

            Try
                backend.Start(sink)
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Running,
                    backend.CurrentState, "state after Start")

                ' Activity is EVIDENCE, not a precondition: the crash seam
                ' fires from the worker loop top regardless of what the
                ' desktop delivers (under concurrent duplication load the
                ' counters may stay at zero — that is environmental).
                Dim sawActivity As Boolean =
                    WaitTrue(Function() backend.Diagnostics.NoFrameCount > 0 OrElse
                                        backend.Diagnostics.EmittedFrames > 0 OrElse
                                        backend.Diagnostics.ErrorCount > 0, 10000)
                If Not sawActivity Then
                    Console.WriteLine("      note: no capture activity observed (DXGI contention) — crash contract still asserted")
                End If
                Dim threadsWithWorker As Integer = proc.Threads.Count

                ' ── Running → worker terminates unexpectedly ──
                backend.RequestWorkerCrashOnce()

                Dim deadline As Long = DateTime.UtcNow.Ticks + 100000000L   ' 10s
                While backend.CurrentState = DdagrabBackend.DdagrabBackendState.Running AndAlso
                      DateTime.UtcNow.Ticks < deadline
                    Thread.Sleep(25)
                End While
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Stopped,
                    backend.CurrentState,
                    "crashed worker must complete Running→Stopped — no zombie Running")

                ' Worker thread really exited (thread count back to pre-worker).
                Thread.Sleep(300)
                Dim threadsAfterCrash As Integer = proc.Threads.Count
                TestHelpers.Assert(threadsAfterCrash <= threadsWithWorker,
                                   $"worker thread leaked: {threadsWithWorker} → {threadsAfterCrash}")

                ' ── Next session on the SAME backend must work (no brick) ──
                Dim sink2 As New RecordingVideoFrameSink()
                sink2.SetOutcomeOverride(PushOutcome.Dropped)
                backend.Start(sink2)
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Running,
                    backend.CurrentState, "Start after crash must be allowed")
                backend.[Stop]()
                TestHelpers.AssertEqual(
                    DdagrabBackend.DdagrabBackendState.Stopped,
                    backend.CurrentState, "post-crash session stop")

                TestHelpers.AssertEqual(
                    backend.TexturesCreated, backend.TexturesDisposed,
                    "texture leak across crash + recovery")
            Finally
                ' NEVER leave a live duplication behind — this process gets
                ' exactly ONE duplication per output; an abandoned backend
                ' would cascade E_INVALIDARG into every later Initialize.
                Try : backend.[Stop]() : Catch : End Try
                Try : backend.Dispose() : Catch : End Try
            End Try
        End Sub

        ' ───────────────────────────────────────────────────────────────
        ' 50-CYCLE MIXED STRESS — Start/Stop, slow stop, Stop-timeout,
        ' worker failure (crash seam). Per cycle: state + texture balance.
        ' Periodic + final: process thread count (worker-leak detector).
        ' ───────────────────────────────────────────────────────────────
        Private Shared Sub Test_MixedStress50()
            Dim proc As Process = Process.GetCurrentProcess()
            Dim threadsBefore As Integer = proc.Threads.Count

            Dim backend = CreateBackend(New EngineLogger("DdagrabStress", EngineLogger.LogLevel.Warning))
            backend.Initialize(CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            sink.SetOutcomeOverride(PushOutcome.Dropped)   ' backend disposes every frame

            Dim crashCycles As Integer = 0
            Dim timeoutCycles As Integer = 0

            For i As Integer = 1 To 50
                Dim kind As Integer = i Mod 5
                If kind = 1 OrElse kind = 3 Then
                    ' ── normal Start/Stop ──
                    backend.Start(sink)
                    TestHelpers.AssertEqual(DdagrabBackend.DdagrabBackendState.Running,
                                            backend.CurrentState, "cycle " & i & ": post-Start")
                    backend.[Stop]()
                ElseIf kind = 2 Then
                    ' ── slow stop (join still succeeds) ──
                    backend.Start(sink)
                    WaitTrue(Function() backend.Diagnostics.NoFrameCount > 0 OrElse
                                        backend.Diagnostics.EmittedFrames > 0, 5000)
                    Thread.Sleep(40)
                    backend.[Stop]()
                ElseIf kind = 0 Then
                    ' ── worker failure: injected crash mid-Running ──
                    backend.Start(sink)
                    WaitTrue(Function() backend.Diagnostics.NoFrameCount > 0 OrElse
                                        backend.Diagnostics.EmittedFrames > 0 OrElse
                                        backend.Diagnostics.ErrorCount > 0, 5000)
                    backend.RequestWorkerCrashOnce()
                    crashCycles += 1
                Else
                    ' ── Stop timeout: worker parked in a gated sink push ──
                    Dim gate As New ManualResetEvent(False)
                    Dim gated As New GatedSinkStub(gate)
                    backend.Start(gated)
                    Dim sw As Stopwatch = Stopwatch.StartNew()
                    While Not gated.Entered AndAlso sw.ElapsedMilliseconds < 5000
                        Thread.Sleep(10)
                    End While
                    backend.[Stop]()
                    TestHelpers.AssertEqual(DdagrabBackend.DdagrabBackendState.Stopping,
                                            backend.CurrentState, "cycle " & i & " (timeout): must hold Stopping")
                    timeoutCycles += 1
                    gate.Set()
                    Dim s4 As Stopwatch = Stopwatch.StartNew()
                    While backend.CurrentState <> DdagrabBackend.DdagrabBackendState.Stopped AndAlso
                          s4.ElapsedMilliseconds < 10000
                        Thread.Sleep(25)
                    End While
                End If

                ' every cycle ends Stopped (normal + slow + timeout + crash)
                Dim s5 As Stopwatch = Stopwatch.StartNew()
                While backend.CurrentState <> DdagrabBackend.DdagrabBackendState.Stopped AndAlso
                      s5.ElapsedMilliseconds < 10000
                    Thread.Sleep(25)
                End While
                TestHelpers.AssertEqual(DdagrabBackend.DdagrabBackendState.Stopped,
                                        backend.CurrentState, "cycle " & i & ": final state")
                TestHelpers.AssertEqual(backend.TexturesCreated, backend.TexturesDisposed,
                                        "cycle " & i & ": texture balance")
            Next

            TestHelpers.Assert(crashCycles >= 5, "expected >=5 worker-failure cycles (got " & crashCycles & ")")
            TestHelpers.Assert(timeoutCycles >= 5, "expected >=5 timeout cycles (got " & timeoutCycles & ")")

            Try
                ' Dispose contract + leak audit
                backend.Dispose()
                Dim disposeRejected As Boolean = False
                Try
                    backend.Start(sink)
                Catch ex As ObjectDisposedException
                    disposeRejected = True
                End Try
                TestHelpers.Assert(disposeRejected, "Start after Dispose was accepted")

                Thread.Sleep(300)
                Dim threadsAfter As Integer = proc.Threads.Count
                Console.WriteLine("      threads before=" & threadsBefore & " after=" & threadsAfter &
                                  "; crash cycles=" & crashCycles & "; timeout cycles=" & timeoutCycles)
                TestHelpers.Assert(threadsAfter <= threadsBefore + 8,
                                   "thread count grew " & threadsBefore & " → " & threadsAfter & " — worker leak")
            Finally
                Try : backend.[Stop]() : Catch : End Try
                Try : backend.Dispose() : Catch : End Try
            End Try
        End Sub

        ' Minimal sink that parks the worker inside TryPush until
        ' the gate opens (deterministic Stop-timeout generator).
        Private NotInheritable Class GatedSinkStub
            Implements IVideoFrameSink

            Private ReadOnly _gate As ManualResetEvent
            Public Entered As Boolean = False

            Public Sub New(gate As ManualResetEvent)
                _gate = gate
            End Sub

            Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
                Entered = True
                _gate.WaitOne()
                If result.Frame IsNot Nothing Then
                    Try : result.Frame.Dispose() : Catch : End Try
                End If
                Return PushOutcome.Pushed
            End Function
        End Class

    End Class

End Namespace
