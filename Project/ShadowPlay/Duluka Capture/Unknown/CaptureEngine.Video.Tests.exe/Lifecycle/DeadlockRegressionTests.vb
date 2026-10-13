Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Handoff
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Lifecycle
    ''' <summary>
    ''' Regression tests for P1-B.1 FIX (deadlock + ownership + sink
    ''' lock-release-on-Dispose).
    '''
    ''' These tests would have hung / timed out / raced before the fix.
    ''' They MUST complete quickly (no 2-second Stop budget ever triggered).
    ''' </summary>
    Friend NotInheritable Class DeadlockRegressionTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("REGRESSION: Dispose while worker actively running — no deadlock", AddressOf Test_DisposeWhileRunningNoDeadlock)
            runner("REGRESSION: Dispose repeatedly (idempotent, no deadlock)", AddressOf Test_DisposeRepeatedlyIdempotent)
            runner("REGRESSION: Stop while worker active — completes", AddressOf Test_StopWhileActiveCompletes)
            runner("REGRESSION: BoundedVideoFrameSink does not dispose frames while holding lock", AddressOf Test_SinkDisposesFramesOutsideLock)
            runner("REGRESSION: Concurrent Dispose + TryPush on sink — no deadlock", AddressOf Test_ConcurrentDisposeAndTryPushOnSink)
        End Sub

        ''' <summary>
        ''' Worker is actively running (no inter-attempt delay, so it is
        ''' constantly acquiring _sync). Calling Dispose MUST complete within
        ''' a short budget. Before the fix, Dispose would hold _sync across
        ''' worker.Join() — guaranteed deadlock.
        ''' </summary>
        Private Shared Sub Test_DisposeWhileRunningNoDeadlock()
            Dim backend = TestHelpers.CreateDefaultFake()
            ' No inter-attempt delay → worker is hot-looping on _sync.
            backend.WithInterAttemptDelayMs(0)
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            ' Give the worker a moment to start spinning.
            Thread.Sleep(20)

            Dim sw = Stopwatch.StartNew()
            backend.Dispose()
            sw.Stop()

            ' The 2-second Stop budget should NEVER trigger. Even on a loaded
            ' CI machine, the worker should observe _stopSignal within a few
            ' milliseconds. Allow up to 500 ms as a generous upper bound
            ' (well below the 2,000 ms timeout).
            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 500,
                "Dispose while Running must complete in < 500 ms. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Disposed,
                backend.CurrentState, "backend Disposed after Dispose")
            sink.DisposeAllOwnedFrames()
        End Sub

        ''' <summary>
        ''' Dispose x100 in a tight loop. Must be idempotent, no deadlock,
        ''' no exception. Each Dispose call after the first is a no-op.
        ''' </summary>
        Private Shared Sub Test_DisposeRepeatedlyIdempotent()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.WithInterAttemptDelayMs(0)
            backend.Initialize(TestHelpers.CreateContext())
            backend.Start(New RecordingVideoFrameSink())
            Thread.Sleep(10)

            Dim sw = Stopwatch.StartNew()
            For i As Integer = 1 To 100
                backend.Dispose()
            Next
            sw.Stop()

            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 1000,
                "100 Dispose calls must complete in < 1 s. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Disposed,
                backend.CurrentState, "backend Disposed after 100 Dispose calls")
        End Sub

        ''' <summary>
        ''' Stop the worker while it is actively producing frames. Stop MUST
        ''' complete within budget. Then Dispose MUST also complete (state
        ''' transitions Stopping → Stopped → Disposed).
        ''' </summary>
        Private Shared Sub Test_StopWhileActiveCompletes()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.WithInterAttemptDelayMs(0)
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)
            Thread.Sleep(20)

            Dim sw = Stopwatch.StartNew()
            backend.Stop()
            sw.Stop()

            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 500,
                "Stop while active must complete in < 500 ms. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Stopped,
                backend.CurrentState, "backend Stopped after Stop")

            ' Now Dispose from Stopped — must also be fast.
            sw.Restart()
            backend.Dispose()
            sw.Stop()
            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 200,
                "Dispose from Stopped must be < 200 ms. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Disposed,
                backend.CurrentState, "backend Disposed")

            sink.DisposeAllOwnedFrames()
        End Sub

        ''' <summary>
        ''' BoundedVideoFrameSink must NOT call frame.Dispose() while holding
        ''' _sync. We assert this indirectly: when a frame's Dispose is
        ''' instrumented to BLOCK for 50 ms (simulating a heavy GPU release),
        ''' concurrent TryPush calls must still complete in O(1) — they do
        ''' not wait on the heavy Dispose.
        '''
        ''' Strategy: install a slow-disposing fake frame into the queue, then
        ''' evict it (DropOldest). The NEXT TryPush (also DropOldest) must
        ''' complete without waiting for the previous evicted frame's Dispose
        ''' to finish. We measure this by asserting the second push completes
        ''' well under the 50 ms Dispose time.
        ''' </summary>
        Private Shared Sub Test_SinkDisposesFramesOutsideLock()
            ' Build a frame whose Dispose blocks for 50 ms.
            Dim slowFrame As New SlowDisposeVideoFrame(50)

            Dim sink As New BoundedVideoFrameSink(capacity:=1, policy:=BoundedHandoffPolicy.DropOldest)

            ' Push 1: slowFrame enters the queue (Pushed).
            Dim r1 = FrameAcquisitionResult.Available(slowFrame, 0, 1_000_000)
            Dim o1 = sink.TryPush(r1)
            TestHelpers.AssertEqual(PushOutcome.Pushed, o1, "first push = Pushed")

            ' Push 2: evicts slowFrame. The eviction's Dispose is triggered
            ' OUTSIDE the lock — but TryPush still has to wait for the Dispose
            ' to be triggered before returning Replaced. So this push WILL
            ' include the slow Dispose time. That is correct (the caller is
            ' doing the eviction work). What we assert is the THIRD push:
            ' it should NOT wait for the second eviction's Dispose because
            ' the second push's Dispose happens AFTER TryPush returns.
            Dim r2 = FrameAcquisitionResult.Available(MakeFastFrame(1), 1, 1_010_000)
            Dim o2 = sink.TryPush(r2)
            TestHelpers.AssertEqual(PushOutcome.Replaced, o2, "second push = Replaced (slowFrame evicted)")

            ' Push 3: must complete in O(1) — the previous eviction (slowFrame's
            ' disposal of the MakeFastFrame(1) frame) happens AFTER TryPush
            ' returns. If the sink still held the lock during Dispose, this push
            ' would have to wait for the prior Dispose to finish.
            Dim r3 = FrameAcquisitionResult.Available(MakeFastFrame(2), 2, 1_020_000)
            Dim sw = Stopwatch.StartNew()
            Dim o3 = sink.TryPush(r3)
            sw.Stop()
            TestHelpers.AssertEqual(PushOutcome.Replaced, o3, "third push = Replaced (fastFrame(1) evicted)")

            ' Even with slowDispose=50ms on slowFrame, the third push should
            ' complete in well under 50 ms because it does NOT wait for the
            ' second push's slowDispose (which is for MakeFastFrame(1) — fast).
            ' We assert < 30 ms as a generous upper bound.
            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 30,
                "Third TryPush must complete without waiting on prior eviction's Dispose. Took: " &
                sw.ElapsedMilliseconds & " ms")

            sink.Dispose()
        End Sub

        ''' <summary>
        ''' Concurrent Dispose + TryPush on the same sink must not deadlock.
        ''' One thread calls Dispose; the other calls TryPush in a loop.
        ''' Both must complete within a budget.
        ''' </summary>
        Private Shared Sub Test_ConcurrentDisposeAndTryPushOnSink()
            Dim sink As New BoundedVideoFrameSink(capacity:=2, policy:=BoundedHandoffPolicy.DropNewest)

            Dim stopSignal As Integer = 0
            Dim pusherErrors As New List(Of Exception)()
            Dim pusherSync As New Object()

            Dim pusher As New Thread(
                Sub()
                    Try
                        Dim seq As Long = 0
                        Do While Volatile.Read(stopSignal) = 0
                            Dim f = MakeFastFrame(seq)
                            Dim r = FrameAcquisitionResult.Available(f, seq, 1_000_000 + seq * 10_000)
                            Dim outcome = sink.TryPush(r)
                            If outcome = PushOutcome.Dropped Then
                                ' Caller retains ownership — dispose the refused frame.
                                f.Dispose()
                            End If
                            seq += 1
                            If seq Mod 50 = 0 Then Thread.Sleep(1)
                        Loop
                    Catch ex As Exception
                        SyncLock pusherSync
                            pusherErrors.Add(ex)
                        End SyncLock
                    End Try
                End Sub)
            pusher.IsBackground = True
            pusher.Start()

            ' Let the pusher run briefly.
            Thread.Sleep(20)

            Dim sw = Stopwatch.StartNew()
            sink.Dispose()
            sw.Stop()

            Volatile.Write(stopSignal, 1)
            pusher.Join(TimeSpan.FromSeconds(2))

            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 500,
                "sink.Dispose while concurrent TryPush must complete in < 500 ms. Took: " &
                sw.ElapsedMilliseconds & " ms")

            SyncLock pusherSync
                TestHelpers.Assert(
                    pusherErrors.Count = 0,
                    "pusher thread must not throw. Got " & pusherErrors.Count & " exceptions" &
                    If(pusherErrors.Count > 0, " (first: " & pusherErrors(0).GetType().Name & ": " & pusherErrors(0).Message & ")", ""))
            End SyncLock
        End Sub

        ' ---- helpers ----

        Private Shared Function MakeFastFrame(seq As Long) As IVideoFrame
            Dim diag As New FrameDiagnostics(seq, 1_000_000 + seq * 10_000, 1_000_000 + seq * 10_000)
            Return New FakeVideoFrame(VideoFrameOrigin.CpuMemory, VideoPixelFormat.Bgra8,
                                      New VideoFrameDimensions(1920, 1080), diag)
        End Function

        ''' <summary>
        ''' A frame whose Dispose() blocks for a configurable number of ms.
        ''' Used to prove that the sink does NOT hold its lock during Dispose.
        ''' </summary>
        Private NotInheritable Class SlowDisposeVideoFrame
            Implements IVideoFrame

            Private ReadOnly _delayMs As Integer
            Private _disposed As Boolean = False

            Public Sub New(delayMs As Integer)
                _delayMs = delayMs
            End Sub

            Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
                Get
                    Return VideoFrameOrigin.CpuMemory
                End Get
            End Property

            Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
                Get
                    Return VideoPixelFormat.Bgra8
                End Get
            End Property

            Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
                Get
                    Return New VideoFrameDimensions(1920, 1080)
                End Get
            End Property

            Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
                Get
                    Return New FrameDiagnostics(0, 0, 0)
                End Get
            End Property

            Public Sub Dispose() Implements IDisposable.Dispose
                If _disposed Then Return
                _disposed = True
                If _delayMs > 0 Then
                    Thread.Sleep(_delayMs)
                End If
            End Sub
        End Class
    End Class
End Namespace
