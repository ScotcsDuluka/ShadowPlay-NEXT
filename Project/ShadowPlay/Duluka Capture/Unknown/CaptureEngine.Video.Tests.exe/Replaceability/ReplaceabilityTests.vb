Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Replaceability
    ''' <summary>
    ''' Replaceability tests — the central proof that two backends are
    ''' interchangeable (P1-A v1.3.1 §8.4). In P1-B.1 only the FakeVideoCaptureBackend
    ''' exists, so this test asserts that the fake behaves consistently across
    ''' multiple Start→Stop cycles with the same fake Encoder (recording sink).
    '''
    ''' When DdagrabBackend / GfxCaptureBackend are added in later phases, this
    ''' same test parameterizes over them and asserts identical observable
    ''' behaviour modulo Sequence values and texture handles.
    ''' </summary>
    Friend NotInheritable Class ReplaceabilityTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("REPLACEABILITY: Two FakeBackend instances produce interchangeable results", AddressOf Test_TwoFakesInterchangeable)
            runner("REPLACEABILITY: Same fake across Start/Stop cycles is consistent", AddressOf Test_SameFakeAcrossCycles)
            runner("REPLACEABILITY: Backend runs unchanged under DropOldest vs DropNewest sink", AddressOf Test_BackendUnawareOfPolicy)
        End Sub

        Private Shared Sub Test_TwoFakesInterchangeable()
            ' Two fake backends, both with default config (Bgra8, 1920x1080). Run each for
            ' enough frames to observe the contract; assert both produce identical result
            ' shapes modulo Sequence (each backend has its own counter).
            Dim fake1 = TestHelpers.CreateDefaultFake()
            Dim fake2 = TestHelpers.CreateDefaultFake()

            Dim sink1 As New RecordingVideoFrameSink()
            Dim sink2 As New RecordingVideoFrameSink()

            fake1.Initialize(TestHelpers.CreateContext())
            fake2.Initialize(TestHelpers.CreateContext())
            fake1.Start(sink1)
            fake2.Start(sink2)

            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink1.RecordedCount >= 10 AndAlso sink2.RecordedCount >= 10, 1000),
                "Both fakes should produce at least 10 frames")

            fake1.Stop()
            fake2.Stop()

            Dim r1 = sink1.Records
            Dim r2 = sink2.Records

            ' Both must have only FrameAvailable results (default script).
            For i As Integer = 0 To Math.Min(9, Math.Min(r1.Count, r2.Count) - 1)
                TestHelpers.AssertEqual(FrameAcquisitionStatus.FrameAvailable, r1(i).Status,
                    "fake1 record " & i & " status")
                TestHelpers.AssertEqual(FrameAcquisitionStatus.FrameAvailable, r2(i).Status,
                    "fake2 record " & i & " status")
                ' Sequence starts at 0 for both (each backend has its own counter).
                TestHelpers.AssertEqual(r1(i).Sequence, r2(i).Sequence,
                    "Sequence values should match for default-scripted fakes at index " & i)
                ' Outcomes should match (both Pushed).
                TestHelpers.AssertEqual(r1(i).Outcome, r2(i).Outcome,
                    "Outcome should match at index " & i)
                ' Frame shape: both Bgra8, 1920x1080, CpuMemory.
                Dim f1 = DirectCast(r1(i).Frame, FakeVideoFrame)
                Dim f2 = DirectCast(r2(i).Frame, FakeVideoFrame)
                TestHelpers.AssertEqual(VideoPixelFormat.Bgra8, f1.PixelFormat, "fake1 pixel format")
                TestHelpers.AssertEqual(VideoPixelFormat.Bgra8, f2.PixelFormat, "fake2 pixel format")
                TestHelpers.AssertEqual(1920, f1.Dimensions.Width, "fake1 width")
                TestHelpers.AssertEqual(1920, f2.Dimensions.Width, "fake2 width")
                TestHelpers.AssertEqual(1080, f1.Dimensions.Height, "fake1 height")
                TestHelpers.AssertEqual(1080, f2.Dimensions.Height, "fake2 height")
                TestHelpers.AssertEqual(VideoFrameOrigin.CpuMemory, f1.Origin, "fake1 origin")
                TestHelpers.AssertEqual(VideoFrameOrigin.CpuMemory, f2.Origin, "fake2 origin")
            Next

            fake1.Dispose()
            fake2.Dispose()
            sink1.DisposeAllOwnedFrames()
            sink2.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_SameFakeAcrossCycles()
            Dim fake = TestHelpers.CreateDefaultFake()
            fake.Initialize(TestHelpers.CreateContext())

            Dim firstRecordedCount As Integer = 0

            For cycle As Integer = 1 To 3
                Dim sink As New RecordingVideoFrameSink()
                fake.Start(sink)
                TestHelpers.Assert(
                    TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 5, 1000),
                    "Cycle " & cycle & ": expected 5 frames")
                fake.Stop()

                ' All records in every cycle must be FrameAvailable.
                For Each r In sink.Records
                    TestHelpers.AssertEqual(
                        FrameAcquisitionStatus.FrameAvailable, r.Status,
                        "cycle " & cycle & ": every record must be FrameAvailable")
                Next

                If cycle = 1 Then
                    firstRecordedCount = sink.RecordedCount
                End If

                sink.DisposeAllOwnedFrames()
            Next

            fake.Dispose()
        End Sub

        Private Shared Sub Test_BackendUnawareOfPolicy()
            ' The backend MUST run unchanged whether the sink is configured for
            ' DropOldest or DropNewest. We exercise both: a sink that always
            ' returns Dropped (DropNewest semantics) and a sink that always
            ' returns Replaced (DropOldest eviction). The backend's EmittedFrames
            ' should differ, but the backend should NOT crash, NOT throw, and
            ' NOT touch frames after pushing them.

            ' Setup 1: RefusingVideoFrameSink (simulates DropNewest-with-full-queue).
            Dim fake1 = TestHelpers.CreateDefaultFake()
            fake1.WithInterAttemptDelayMs(2)
            fake1.Initialize(TestHelpers.CreateContext())
            Dim sink1 As New RefusingVideoFrameSink()
            fake1.Start(sink1)
            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() fake1.Diagnostics.DroppedFrames >= 3, 1000),
                "fake1 with refusing sink: DroppedFrames >= 3")
            fake1.Stop()
            ' Backend survived the DropNewest-like sink — proves it handles Dropped outcome.
            TestHelpers.AssertEqual(0L, fake1.Diagnostics.EmittedFrames, "fake1 EmittedFrames must be 0 under refusing sink")
            TestHelpers.Assert(fake1.Diagnostics.DroppedFrames >= 3, "fake1 DroppedFrames >= 3")
            fake1.Dispose()

            ' Setup 2: EvictingVideoFrameSink (simulates DropOldest eviction).
            Dim fake2 = TestHelpers.CreateDefaultFake()
            fake2.WithInterAttemptDelayMs(2)
            fake2.Initialize(TestHelpers.CreateContext())
            Dim sink2 As New EvictingVideoFrameSink()
            fake2.Start(sink2)
            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink2.PushCount >= 5, 1000),
                "fake2 with evicting sink: PushCount >= 5")
            fake2.Stop()
            ' Backend survived the DropOldest-like sink — proves it handles Replaced outcome.
            TestHelpers.Assert(fake2.Diagnostics.EmittedFrames >= 5, "fake2 EmittedFrames >= 5 (Pushed + Replaced)")
            TestHelpers.Assert(fake2.Diagnostics.ReplacedFrames >= 4, "fake2 ReplacedFrames >= 4")
            TestHelpers.AssertEqual(0L, fake2.Diagnostics.DroppedFrames, "fake2 DroppedFrames = 0 (no Dropped outcome under EvictingVideoFrameSink)")
            fake2.Dispose()

            For Each f In sink2.EvictedFrames
                Try
                    f.Dispose()
                Catch
                End Try
            Next
        End Sub
    End Class
End Namespace
