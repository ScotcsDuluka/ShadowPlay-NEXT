Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Handoff
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Delivery
    ''' <summary>
    ''' Bounded handoff / backpressure tests against the real BoundedVideoFrameSink
    ''' (production-quality; not a fake). Verifies DropOldest and DropNewest
    ''' policies, queue capacity enforcement, eviction/disposal of evicted
    ''' frames, and that the sink is non-blocking.
    ''' </summary>
    Friend NotInheritable Class BoundedHandoffTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("HANDOFF: BoundedVideoFrameSink enforces capacity", AddressOf Test_CapacityEnforced)
            runner("HANDOFF: DropOldest evicts oldest + returns Replaced", AddressOf Test_DropOldest)
            runner("HANDOFF: DropNewest refuses + returns Dropped", AddressOf Test_DropNewest)
            runner("HANDOFF: TryPush is non-blocking (does not stall on full DropNewest queue)", AddressOf Test_NonBlockingPush)
            runner("HANDOFF: Capacity 0 / negative rejected at construction", AddressOf Test_CapacityValidation)
            runner("HANDOFF: Capacity > 16 hard cap rejected", AddressOf Test_CapacityHardCap)
            runner("HANDOFF: Disposed sink refuses via Dropped outcome", AddressOf Test_DisposedSinkRefuses)
        End Sub

        Private Shared Function MakeFrame(seq As Long) As IVideoFrame
            Dim diag As New FrameDiagnostics(seq, 1_000_000 + seq * 10_000, 1_000_000 + seq * 10_000)
            Return New FakeVideoFrame(VideoFrameOrigin.CpuMemory, VideoPixelFormat.Bgra8,
                                      New VideoFrameDimensions(1920, 1080), diag)
        End Function

        Private Shared Sub Test_CapacityEnforced()
            Dim sink As New BoundedVideoFrameSink(capacity:=3, policy:=BoundedHandoffPolicy.DropNewest)
            TestHelpers.AssertEqual(3, sink.Capacity, "capacity stored")
            TestHelpers.AssertEqual(BoundedHandoffPolicy.DropNewest, sink.Policy, "policy stored")
            TestHelpers.AssertEqual(0, sink.Count, "starts empty")

            Dim r1 = FrameAcquisitionResult.Available(MakeFrame(0), 0, 1_000_000)
            Dim r2 = FrameAcquisitionResult.Available(MakeFrame(1), 1, 1_010_000)
            Dim r3 = FrameAcquisitionResult.Available(MakeFrame(2), 2, 1_020_000)

            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r1), "push 1 = Pushed")
            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r2), "push 2 = Pushed")
            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r3), "push 3 = Pushed")
            TestHelpers.AssertEqual(3, sink.Count, "queue full at capacity 3")
            sink.Dispose()
        End Sub

        Private Shared Sub Test_DropOldest()
            Dim sink As New BoundedVideoFrameSink(capacity:=2, policy:=BoundedHandoffPolicy.DropOldest)
            Dim r1 = FrameAcquisitionResult.Available(MakeFrame(0), 0, 1_000_000)
            Dim r2 = FrameAcquisitionResult.Available(MakeFrame(1), 1, 1_010_000)
            Dim r3 = FrameAcquisitionResult.Available(MakeFrame(2), 2, 1_020_000)

            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r1), "push 1 = Pushed")
            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r2), "push 2 = Pushed")
            ' Queue full. Push 3 should evict r1 (oldest) and return Replaced.
            Dim outcome3 = sink.TryPush(r3)
            TestHelpers.AssertEqual(PushOutcome.Replaced, outcome3, "push 3 = Replaced (DropOldest evicted r1)")

            TestHelpers.AssertEqual(2, sink.Count, "queue still at capacity after eviction")
            TestHelpers.AssertEqual(1L, sink.ReplacedCount, "ReplacedCount = 1")
            TestHelpers.AssertEqual(0L, sink.DroppedCount, "DroppedCount = 0 (DropOldest never returns Dropped)")

            ' r1's frame should have been disposed by the sink on eviction.
            Dim f1 = DirectCast(r1.Frame, FakeVideoFrame)
            TestHelpers.AssertEqual(1, f1.DisposeCount, "evicted r1.Frame disposed by sink")

            ' r2 and r3 should still be alive.
            Dim f2 = DirectCast(r2.Frame, FakeVideoFrame)
            Dim f3 = DirectCast(r3.Frame, FakeVideoFrame)
            TestHelpers.AssertEqual(0, f2.DisposeCount, "r2.Frame still alive")
            TestHelpers.AssertEqual(0, f3.DisposeCount, "r3.Frame still alive")

            sink.Dispose()

            ' After Dispose, remaining queued frames are disposed by the sink.
            TestHelpers.AssertEqual(1, f2.DisposeCount, "r2.Frame disposed on sink Dispose")
            TestHelpers.AssertEqual(1, f3.DisposeCount, "r3.Frame disposed on sink Dispose")
        End Sub

        Private Shared Sub Test_DropNewest()
            Dim sink As New BoundedVideoFrameSink(capacity:=2, policy:=BoundedHandoffPolicy.DropNewest)
            Dim r1 = FrameAcquisitionResult.Available(MakeFrame(0), 0, 1_000_000)
            Dim r2 = FrameAcquisitionResult.Available(MakeFrame(1), 1, 1_010_000)
            Dim r3 = FrameAcquisitionResult.Available(MakeFrame(2), 2, 1_020_000)

            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r1), "push 1 = Pushed")
            TestHelpers.AssertEqual(PushOutcome.Pushed, sink.TryPush(r2), "push 2 = Pushed")
            ' Queue full. Push 3 should be refused (Dropped). Caller retains ownership of r3.Frame.
            Dim outcome3 = sink.TryPush(r3)
            TestHelpers.AssertEqual(PushOutcome.Dropped, outcome3, "push 3 = Dropped (DropNewest refused)")
            TestHelpers.AssertEqual(0L, sink.ReplacedCount, "ReplacedCount = 0")
            TestHelpers.AssertEqual(1L, sink.DroppedCount, "DroppedCount = 1")

            ' r3.Frame is NOT disposed by the sink (caller retains ownership).
            Dim f3 = DirectCast(r3.Frame, FakeVideoFrame)
            TestHelpers.AssertEqual(0, f3.DisposeCount, "r3.Frame NOT disposed by sink (caller owns it)")
            ' Caller must dispose r3.Frame:
            r3.Frame.Dispose()
            TestHelpers.AssertEqual(1, f3.DisposeCount, "r3.Frame disposed by caller")

            sink.Dispose()

            ' r1 and r2 were in the queue; sink disposes them on its Dispose.
            Dim f1 = DirectCast(r1.Frame, FakeVideoFrame)
            Dim f2 = DirectCast(r2.Frame, FakeVideoFrame)
            TestHelpers.AssertEqual(1, f1.DisposeCount, "r1.Frame disposed on sink Dispose")
            TestHelpers.AssertEqual(1, f2.DisposeCount, "r2.Frame disposed on sink Dispose")
        End Sub

        Private Shared Sub Test_NonBlockingPush()
            ' Even with capacity 1, DropNewest, pushing 1000 times in a tight loop
            ' should complete in well under 1 second — proving non-blocking.
            Dim sink As New BoundedVideoFrameSink(capacity:=1, policy:=BoundedHandoffPolicy.DropNewest)
            Dim sink2 As New BoundedVideoFrameSink(capacity:=1, policy:=BoundedHandoffPolicy.DropOldest)

            ' Pre-make 1000 frames. For DropNewest, after the first Pushed, the rest are Dropped
            ' and the caller disposes them. For DropOldest, after the first Pushed, each subsequent
            ' push evicts the previous — the sink disposes evicted frames.
            Dim framesDropNewest As New List(Of IVideoFrame)()
            For i As Integer = 0 To 999
                framesDropNewest.Add(MakeFrame(i))
            Next

            Dim start = DateTime.UtcNow
            For i As Integer = 0 To 999
                Dim result = FrameAcquisitionResult.Available(framesDropNewest(i), i, 1_000_000 + i * 10_000)
                Dim outcome = sink.TryPush(result)
                If outcome = PushOutcome.Dropped Then
                    ' Caller disposes refused frame.
                    framesDropNewest(i).Dispose()
                End If
            Next
            Dim elapsed = (DateTime.UtcNow - start).TotalMilliseconds
            TestHelpers.Assert(elapsed < 1000.0, "1000 pushes to DropNewest queue must be < 1s. Took: " & elapsed.ToString() & " ms")
            sink.Dispose()

            ' DropOldest: 1000 pushes with capacity 1.
            Dim framesDropOldest As New List(Of IVideoFrame)()
            For i As Integer = 0 To 999
                framesDropOldest.Add(MakeFrame(i))
            Next
            start = DateTime.UtcNow
            For i As Integer = 0 To 999
                Dim result = FrameAcquisitionResult.Available(framesDropOldest(i), i, 1_000_000 + i * 10_000)
                Dim outcome = sink2.TryPush(result)
                ' DropOldest returns Pushed or Replaced. Either way the sink took ownership.
            Next
            elapsed = (DateTime.UtcNow - start).TotalMilliseconds
            TestHelpers.Assert(elapsed < 1000.0, "1000 pushes to DropOldest queue must be < 1s. Took: " & elapsed.ToString() & " ms")
            sink2.Dispose()
        End Sub

        Private Shared Sub Test_CapacityValidation()
            TestHelpers.AssertThrows(Of ArgumentOutOfRangeException)(
                Sub()
                    Dim s As New BoundedVideoFrameSink(0, BoundedHandoffPolicy.DropOldest)
                End Sub,
                "capacity 0 must throw")
            TestHelpers.AssertThrows(Of ArgumentOutOfRangeException)(
                Sub()
                    Dim s As New BoundedVideoFrameSink(-1, BoundedHandoffPolicy.DropOldest)
                End Sub,
                "capacity -1 must throw")
        End Sub

        Private Shared Sub Test_CapacityHardCap()
            TestHelpers.AssertThrows(Of ArgumentOutOfRangeException)(
                Sub()
                    Dim s As New BoundedVideoFrameSink(17, BoundedHandoffPolicy.DropOldest)
                End Sub,
                "capacity 17 (above hard cap of 16) must throw")
            ' capacity 16 is allowed.
            Dim s16 As New BoundedVideoFrameSink(16, BoundedHandoffPolicy.DropOldest)
            s16.Dispose()
        End Sub

        Private Shared Sub Test_DisposedSinkRefuses()
            Dim sink As New BoundedVideoFrameSink(2, BoundedHandoffPolicy.DropNewest)
            sink.Dispose()
            Dim r = FrameAcquisitionResult.Available(MakeFrame(0), 0, 1_000_000)
            Dim outcome = sink.TryPush(r)
            TestHelpers.AssertEqual(PushOutcome.Dropped, outcome, "Disposed sink must return Dropped")
            TestHelpers.AssertEqual(1L, sink.DroppedCount, "DroppedCount incremented on disposed-sink push")
            ' Caller retains ownership — dispose the frame ourselves.
            r.Frame.Dispose()
            Dim f = DirectCast(r.Frame, FakeVideoFrame)
            TestHelpers.AssertEqual(1, f.DisposeCount, "caller disposed the refused frame")
        End Sub
    End Class
End Namespace
