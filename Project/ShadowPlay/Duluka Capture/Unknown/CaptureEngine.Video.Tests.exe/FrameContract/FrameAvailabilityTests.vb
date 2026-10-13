Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.FrameContract
    ''' <summary>
    ''' Frame-availability tests covering FrameAvailable / NoFrame / Error
    ''' result types, sequence gap detection, and the diagnostics surface
    ''' (IVideoBackendDiagnostics).
    ''' </summary>
    Friend NotInheritable Class FrameAvailabilityTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("FRAME: FrameAvailable pushes frame to sink", AddressOf Test_FrameAvailable)
            runner("FRAME: NoFrame NOT pushed to sink (internal only)", AddressOf Test_NoFrameNotPushed)
            runner("FRAME: Error pushed to sink with exception", AddressOf Test_ErrorPushed)
            runner("FRAME: Sequence numbers are monotonic", AddressOf Test_SequenceMonotonic)
            runner("FRAME: Diagnostics counters track emitted/no-frame/error", AddressOf Test_DiagnosticsCounters)
            runner("FRAME: BGRA8 baseline enforcement — non-BGRA8 throws", AddressOf Test_Bgra8BaselineEnforcement)
            runner("FRAME: BGRA8 default — Initialize succeeds", AddressOf Test_Bgra8DefaultSucceeds)
        End Sub

        Private Shared Sub Test_FrameAvailable()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            ' Wait for at least 3 frames to be pushed.
            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 3, 1000),
                "Expected at least 3 FrameAvailable results within 1 s")
            backend.Stop()
            backend.Dispose()

            ' Validate first record.
            Dim first = sink.Records(0)
            TestHelpers.AssertEqual(FrameAcquisitionStatus.FrameAvailable, first.Status, "first record status")
            TestHelpers.Assert(first.Frame IsNot Nothing, "first record must carry a frame")
            TestHelpers.AssertEqual(0L, first.Sequence, "first record sequence (starts at 0)")
            TestHelpers.AssertEqual(PushOutcome.Pushed, first.Outcome, "first record outcome (recording sink)")
            sink.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_NoFrameNotPushed()
            Dim backend = TestHelpers.CreateDefaultFake()
            ' Script: emit 5 NoFrame, then 3 FrameAvailable.
            Dim script As New List(Of FakeFrameDescriptor)()
            For i As Integer = 0 To 4
                script.Add(FakeFrameDescriptor.NoFrame())
            Next
            For i As Integer = 0 To 2
                script.Add(FakeFrameDescriptor.FrameAvailable())
            Next
            backend.WithScript(script)
            backend.Initialize(TestHelpers.CreateContext())

            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            ' Worker walks script; once it runs out it continues emitting FrameAvailable forever.
            ' Wait for at least 3 FrameAvailable results.
            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 3, 1000),
                "Expected 3 FrameAvailable results (NoFrame must NOT be pushed)")

            backend.Stop()
            backend.Dispose()

            ' Diagnostics should record NoFrameCount >= 5.
            TestHelpers.Assert(
                backend.Diagnostics.NoFrameCount >= 5,
                "NoFrameCount should be >= 5 (scripted 5 NoFrame descriptors). Was: " & backend.Diagnostics.NoFrameCount)
            ' Sink should ONLY have received FrameAvailable (not NoFrame).
            For Each r In sink.Records
                TestHelpers.AssertEqual(
                    FrameAcquisitionStatus.FrameAvailable, r.Status,
                    "all sink records should be FrameAvailable (NoFrame is internal)")
            Next
            sink.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_ErrorPushed()
            Dim backend = TestHelpers.CreateDefaultFake()
            Dim script As New List(Of FakeFrameDescriptor)()
            script.Add(FakeFrameDescriptor.FrameAvailable())
            script.Add(FakeFrameDescriptor.FromError("scripted error 1"))
            script.Add(FakeFrameDescriptor.FrameAvailable())
            backend.WithScript(script)
            backend.Initialize(TestHelpers.CreateContext())

            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            ' Wait for at least 3 records to arrive.
            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 3, 1000),
                "Expected 3 results (FrameAvailable, Error, FrameAvailable)")

            backend.Stop()
            backend.Dispose()

            Dim records = sink.Records
            TestHelpers.AssertEqual(FrameAcquisitionStatus.FrameAvailable, records(0).Status, "record 0 = FrameAvailable")
            TestHelpers.AssertEqual(FrameAcquisitionStatus.Error, records(1).Status, "record 1 = Error")
            TestHelpers.Assert(records(1).Frame Is Nothing, "Error record must NOT carry a frame")
            ' records(1) wraps the error in the FrameAcquisitionResult — but the recording sink stored
            ' a copy; the Error property is on the original result struct. The recording stores Status
            ' only, so we verify the diagnostics ErrorCount.
            TestHelpers.Assert(
                backend.Diagnostics.ErrorCount >= 1,
                "ErrorCount should be >= 1 (scripted Error descriptor). Was: " & backend.Diagnostics.ErrorCount)
            sink.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_SequenceMonotonic()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 5, 1000),
                "Expected 5 FrameAvailable results for sequence check")

            backend.Stop()
            backend.Dispose()

            Dim records = sink.Records
            For i As Integer = 1 To records.Count - 1
                TestHelpers.Assert(
                    records(i).Sequence > records(i - 1).Sequence,
                    "sequence must be monotonically increasing at index " & i)
            Next
            sink.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_DiagnosticsCounters()
            Dim backend = TestHelpers.CreateDefaultFake()
            Dim script As New List(Of FakeFrameDescriptor)()
            script.Add(FakeFrameDescriptor.FrameAvailable())
            script.Add(FakeFrameDescriptor.NoFrame())
            script.Add(FakeFrameDescriptor.FrameAvailable())
            script.Add(FakeFrameDescriptor.FromError("diag-error"))
            script.Add(FakeFrameDescriptor.FrameAvailable())
            backend.WithScript(script)
            backend.Initialize(TestHelpers.CreateContext())

            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)

            TestHelpers.Assert(
                TestHelpers.SpinWaitFor(Function() sink.RecordedCount >= 4, 1000),
                "Expected 4 records (3 FrameAvailable + 1 Error; the 1 NoFrame is NOT pushed)")

            backend.Stop()
            backend.Dispose()

            Dim d = backend.Diagnostics
            TestHelpers.Assert(d.EmittedFrames >= 3, "EmittedFrames >= 3. Was: " & d.EmittedFrames)
            TestHelpers.Assert(d.NoFrameCount >= 1, "NoFrameCount >= 1. Was: " & d.NoFrameCount)
            TestHelpers.Assert(d.ErrorCount >= 1, "ErrorCount >= 1. Was: " & d.ErrorCount)
            TestHelpers.AssertEqual(0L, d.DroppedFrames, "DroppedFrames = 0 (no DropNewest in this test)")
            TestHelpers.AssertEqual(0L, d.ReplacedFrames, "ReplacedFrames = 0 (no DropOldest in this test)")
            sink.DisposeAllOwnedFrames()
        End Sub

        Private Shared Sub Test_Bgra8BaselineEnforcement()
            Dim backend = TestHelpers.CreateDefaultFake()
            ' Force non-BGRA8 — Initialize MUST throw VideoBackendConfigurationException.
            backend.WithPixelFormat(VideoPixelFormat.Nv12)
            TestHelpers.AssertThrows(Of VideoBackendConfigurationException)(
                Sub() backend.Initialize(TestHelpers.CreateContext()),
                "Initialize with non-BGRA8 must throw VideoBackendConfigurationException")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_Bgra8DefaultSucceeds()
            Dim backend = TestHelpers.CreateDefaultFake()
            ' Default is Bgra8; Initialize MUST succeed.
            backend.Initialize(TestHelpers.CreateContext())
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Initialized,
                backend.CurrentState, "Initialize with default Bgra8 must succeed")
            backend.Dispose()
        End Sub
    End Class
End Namespace
