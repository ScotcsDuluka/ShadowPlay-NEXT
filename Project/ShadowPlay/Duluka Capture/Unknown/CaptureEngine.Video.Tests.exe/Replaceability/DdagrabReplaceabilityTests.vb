Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video.Backends.Ddagrab
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Replaceability
    ''' <summary>
    ''' Replaceability tests for DdagrabBackend (GLM-1). The central proof
    ''' (P1-A v1.3.1 §8.4) that DdagrabBackend is interchangeable with
    ''' FakeVideoCaptureBackend modulo Sequence values.
    '''
    ''' Phase 12b: updated from the SKELETON contract to the REAL DXGI
    ''' contract (Phase 12a replaced the NoFrame worker with real Output
    ''' Duplication; these tests were not updated with it — first Windows
    ''' run 2026-08-23 exposed the mismatch and a duplication leak).
    '''
    ''' Real-DXGI semantics under test:
    '''   - Worker liveness: an ACTIVE desktop delivers frames (EmittedFrames
    '''     grows); a QUIET desktop times out AcquireNextFrame(100ms)
    '''     (NoFrameCount grows). Liveness = either counter moving.
    '''   - Emitted frames reach the sink (push path works).
    '''   - The backend survives refusing/evicting sinks (Dropped/Replaced
    '''     accounting) — exact counters depend on desktop activity and are
    '''     therefore NOT asserted to fixed values.
    '''   - Same backend across Start/Stop cycles (production reuse model).
    '''   - Diagnostics surface readable cross-thread.
    '''
    ''' Ownership (Phase-11 lesson #2 — one duplication per output per
    ''' process): every test releases the backend in Finally, even when an
    ''' assert throws, or every subsequent Initialize in this process fails
    ''' with DXGI E_INVALIDARG.
    ''' </summary>
    Friend NotInheritable Class DdagrabReplaceabilityTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("DDAGRAB REPLACEABILITY: Real DXGI worker progresses and reaches sink", AddressOf Test_WorkerProgressesToSink)
            runner("DDAGRAB REPLACEABILITY: Backend survives refusing sink (DropNewest-like)", AddressOf Test_SurvivesRefusingSink)
            runner("DDAGRAB REPLACEABILITY: Backend survives evicting sink (DropOldest-like)", AddressOf Test_SurvivesEvictingSink)
            runner("DDAGRAB REPLACEABILITY: Same backend across Start/Stop cycles", AddressOf Test_SameBackendAcrossCycles)
            runner("DDAGRAB REPLACEABILITY: Diagnostics surface contract", AddressOf Test_DiagnosticsSurfaceContract)
        End Sub

        ' ---- helpers ----

        Private Shared Function CreateDefaultBackend() As DdagrabBackend
            Return New DdagrabBackend(
                New EngineLogger("DdagrabBackend", EngineLogger.LogLevel.Warning))
        End Function

        Private Shared Function CreateDdagrabContext() As IVideoBackendContext
            Return New FakeVideoBackendContext(
                VideoBackendKind.Ddagrab,
                New EngineLogger("FakeBackendCtx", EngineLogger.LogLevel.Warning))
        End Function

        ''' <summary>
        ''' Liveness predicate for real DXGI: worker demonstrably progressed
        ''' (frames emitted OR acquire timeouts accumulated).
        ''' </summary>
        Private Shared Function WorkerProgressed(backend As DdagrabBackend) As Boolean
            Return backend.Diagnostics.NoFrameCount +
                   backend.Diagnostics.EmittedFrames +
                   backend.Diagnostics.DroppedFrames >= 1
        End Function

        Private Shared Sub SafeRelease(backend As DdagrabBackend)
            Try : backend.Stop() : Catch : End Try
            Try : backend.Dispose() : Catch : End Try
        End Sub

        ' ---- tests ----

        Private Shared Sub Test_WorkerProgressesToSink()
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(1)
            backend.Initialize(CreateDdagrabContext())
            Dim sink As New RecordingVideoFrameSink()

            Try
                backend.Start(sink)

                Dim progressed As Boolean = TestHelpers.SpinWaitFor(
                    Function() WorkerProgressed(backend), 3000)
                TestHelpers.Assert(
                    progressed,
                    "Worker must progress within 3 s (active desktop → emitted, quiet → noFrame). " &
                    "Was: noFrame=" & backend.Diagnostics.NoFrameCount &
                    ", emitted=" & backend.Diagnostics.EmittedFrames)

                ' If the desktop delivered frames, they must reach the sink
                ' (the push path is the production handoff).
                If backend.Diagnostics.EmittedFrames > 0 Then
                    TestHelpers.Assert(
                        sink.RecordedCount > 0,
                        "EmittedFrames > 0 (" & backend.Diagnostics.EmittedFrames &
                        ") but sink received nothing — push path broken")
                End If
            Finally
                SafeRelease(backend)
            End Try

            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend Disposed cleanly")
            TestHelpers.AssertEqual(0L, backend.Diagnostics.ErrorCount, "ErrorCount = 0 (healthy DXGI run)")
        End Sub

        Private Shared Sub Test_SurvivesRefusingSink()
            ' Real contract: with a sink that refuses every push, the backend
            ' must keep the worker alive, account drops, and Stop/Dispose
            ' cleanly. Exact drop counts depend on desktop activity.
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(1)
            backend.Initialize(CreateDdagrabContext())
            Dim sink As New RefusingVideoFrameSink()

            Try
                backend.Start(sink)

                Dim progressed As Boolean = TestHelpers.SpinWaitFor(
                    Function() WorkerProgressed(backend), 3000)
                TestHelpers.Assert(
                    progressed,
                    "Worker must progress under refusing sink. Was: noFrame=" &
                    backend.Diagnostics.NoFrameCount & ", emitted=" & backend.Diagnostics.EmittedFrames)
            Finally
                SafeRelease(backend)
            End Try

            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend Disposed cleanly after refusing sink")
            TestHelpers.AssertEqual(0L, backend.Diagnostics.ErrorCount, "ErrorCount = 0 (refusing sink)")
        End Sub

        Private Shared Sub Test_SurvivesEvictingSink()
            ' Same contract with an evicting sink (DropOldest-like).
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(1)
            backend.Initialize(CreateDdagrabContext())
            Dim sink As New EvictingVideoFrameSink()

            Try
                backend.Start(sink)

                Dim progressed As Boolean = TestHelpers.SpinWaitFor(
                    Function() WorkerProgressed(backend), 3000)
                TestHelpers.Assert(
                    progressed,
                    "Worker must progress under evicting sink. Was: noFrame=" &
                    backend.Diagnostics.NoFrameCount & ", emitted=" & backend.Diagnostics.EmittedFrames)
            Finally
                SafeRelease(backend)
            End Try

            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend Disposed cleanly after evicting sink")
            TestHelpers.AssertEqual(0L, backend.Diagnostics.ErrorCount, "ErrorCount = 0 (evicting sink)")
        End Sub

        Private Shared Sub Test_SameBackendAcrossCycles()
            ' Production reuse model (mirrors RecordingEngine): ONE backend,
            ' 3 Start → Stop cycles, counters accumulate monotonically.
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(0)
            backend.Initialize(CreateDdagrabContext())

            Try
                For cycle As Integer = 1 To 3
                    Dim sink As New RecordingVideoFrameSink()
                    backend.Start(sink)

                    ' At least one new worker iteration per cycle (counter is
                    ' cumulative — quiet desktop: noFrame grows; active:
                    ' emitted grows).
                    Dim before As Long = backend.Diagnostics.NoFrameCount +
                                         backend.Diagnostics.EmittedFrames
                    Dim progressed As Boolean = TestHelpers.SpinWaitFor(
                        Function() (backend.Diagnostics.NoFrameCount +
                                    backend.Diagnostics.EmittedFrames) > before, 3000)
                    TestHelpers.Assert(
                        progressed,
                        "Cycle " & cycle & ": worker must make progress. Total was: " & before)

                    backend.Stop()
                Next
            Finally
                SafeRelease(backend)
            End Try

            TestHelpers.Assert(
                backend.Diagnostics.NoFrameCount + backend.Diagnostics.EmittedFrames >= 3,
                "Across 3 cycles the worker must have iterated at least 3 times. Was: " &
                (backend.Diagnostics.NoFrameCount + backend.Diagnostics.EmittedFrames))
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend disposed cleanly after 3 cycles")
            TestHelpers.AssertEqual(0L, backend.Diagnostics.ErrorCount, "ErrorCount = 0 (3 cycles)")
        End Sub

        Private Shared Sub Test_DiagnosticsSurfaceContract()
            ' The IVideoBackendDiagnostics surface MUST be readable from any
            ' thread — read all 5 counters from a second thread while the
            ' worker runs. Values depend on desktop activity; the contract
            ' under test is safe cross-thread READABILITY, not fixed values.
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(1)
            backend.Initialize(CreateDdagrabContext())

            Dim readerFinished As Boolean = False
            Dim capturedEmitted As Long = 0
            Dim capturedDropped As Long = 0
            Dim capturedReplaced As Long = 0
            Dim capturedNoFrame As Long = 0
            Dim capturedError As Long = 0

            Try
                backend.Start(New RecordingVideoFrameSink())
                Thread.Sleep(20)

                Dim reader As New Thread(
                    Sub()
                        Dim d = backend.Diagnostics
                        capturedEmitted = d.EmittedFrames
                        capturedDropped = d.DroppedFrames
                        capturedReplaced = d.ReplacedFrames
                        capturedNoFrame = d.NoFrameCount
                        capturedError = d.ErrorCount
                        readerFinished = True
                    End Sub)
                reader.IsBackground = True
                reader.Start()
                reader.Join(TimeSpan.FromSeconds(2))
            Finally
                SafeRelease(backend)
            End Try

            TestHelpers.Assert(readerFinished, "cross-thread diagnostics read completed within 2 s")
            TestHelpers.Assert(capturedNoFrame >= 0, "NoFrameCount readable from other thread")
            TestHelpers.Assert(capturedEmitted >= 0, "EmittedFrames readable from other thread")
            TestHelpers.Assert(capturedDropped >= 0, "DroppedFrames readable from other thread")
            TestHelpers.Assert(capturedReplaced >= 0, "ReplacedFrames readable from other thread")
            TestHelpers.AssertEqual(0L, capturedError, "ErrorCount = 0 read from other thread")
        End Sub
    End Class
End Namespace
