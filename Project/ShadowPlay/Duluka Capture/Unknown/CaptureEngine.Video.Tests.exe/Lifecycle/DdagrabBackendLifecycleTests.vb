Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video.Backends.Ddagrab
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Lifecycle
    ''' <summary>
    ''' DdagrabBackend lifecycle tests (GLM-1). Mirrors the 11 lifecycle cases
    ''' from BackendLifecycleTests (FakeBackend) plus a few Ddagrab-specific
    ''' cases (factory, BGRA8 baseline placeholder, skeleton-mode NoFrame).
    ''' </summary>
    Friend NotInheritable Class DdagrabBackendLifecycleTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("DDAGRAB LIFECYCLE: Initialize -> Initialized", AddressOf Test_Initialize)
            runner("DDAGRAB LIFECYCLE: Start before Initialize (negative)", AddressOf Test_StartBeforeInitialize)
            runner("DDAGRAB LIFECYCLE: Start twice (idempotent)", AddressOf Test_StartTwice)
            runner("DDAGRAB LIFECYCLE: Stop before Start (negative)", AddressOf Test_StopBeforeStart)
            runner("DDAGRAB LIFECYCLE: Stop twice (idempotent)", AddressOf Test_StopTwice)
            runner("DDAGRAB LIFECYCLE: Dispose while Running (stop-path invoked)", AddressOf Test_DisposeWhileRunning)
            runner("DDAGRAB LIFECYCLE: Dispose twice (idempotent)", AddressOf Test_DisposeTwice)
            runner("DDAGRAB LIFECYCLE: Start after Dispose (ObjectDisposedException)", AddressOf Test_StartAfterDispose)
            runner("DDAGRAB LIFECYCLE: Stop after Dispose (ObjectDisposedException)", AddressOf Test_StopAfterDispose)
            runner("DDAGRAB LIFECYCLE: Initialize after Dispose (ObjectDisposedException)", AddressOf Test_InitializeAfterDispose)
            runner("DDAGRAB LIFECYCLE: Initialize twice (second is failure)", AddressOf Test_InitializeTwice)
            runner("DDAGRAB LIFECYCLE: Initialize wrong BackendKind (negative)", AddressOf Test_InitializeWrongBackendKind)
            runner("DDAGRAB LIFECYCLE: Worker loop progresses (real DXGI)", AddressOf Test_NoFrameCountIncrements)
            runner("DDAGRAB LIFECYCLE: Factory returns DdagrabBackend for Ddagrab kind", AddressOf Test_FactoryReturnsDdagrabBackend)
            runner("DDAGRAB LIFECYCLE: Factory throws for non-Ddagrab kind", AddressOf Test_FactoryThrowsForNonDdagrabKind)
            runner("DDAGRAB LIFECYCLE: DdagrabFrame disposable + dispose-counted", AddressOf Test_DdagrabFrameDisposable)
            runner("DDAGRAB REGRESSION: Dispose while running — no deadlock", AddressOf Test_DisposeWhileRunningNoDeadlock)
            runner("DDAGRAB REGRESSION: 100 Dispose calls — idempotent + fast", AddressOf Test_DisposeRepeatedlyIdempotent)
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

        ' ---- tests ----

        Private Shared Sub Test_Initialize()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Initialized,
                backend.CurrentState, "state after Initialize")
            backend.Dispose()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "state after Dispose")
        End Sub

        Private Shared Sub Test_StartBeforeInitialize()
            Dim backend = CreateDefaultBackend()
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() backend.Start(New RecordingVideoFrameSink()),
                "Start before Initialize must throw InvalidOperationException")
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Created,
                backend.CurrentState, "state after failed Start")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StartTwice()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Running,
                backend.CurrentState, "state after first Start")
            ' Second Start: idempotent, no-op, no exception.
            backend.Start(sink)
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Running,
                backend.CurrentState, "state after second Start (idempotent)")
            backend.Stop()
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StopBeforeStart()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            ' Stop before Start: no-op, NO exception.
            backend.Stop()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Initialized,
                backend.CurrentState, "state after Stop before Start (no-op)")
            ' Start should still work after the no-op Stop.
            backend.Start(New RecordingVideoFrameSink())
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Running,
                backend.CurrentState, "state after Start")
            backend.Stop()
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StopTwice()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Start(New RecordingVideoFrameSink())
            backend.Stop()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Stopped,
                backend.CurrentState, "state after first Stop")
            ' Second Stop: no-op, NO exception.
            backend.Stop()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Stopped,
                backend.CurrentState, "state after second Stop (idempotent)")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_DisposeWhileRunning()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Start(New RecordingVideoFrameSink())
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Running,
                backend.CurrentState, "state after Start")
            ' Dispose while Running: routes through stop path internally.
            backend.Dispose()
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "state after Dispose while Running")
        End Sub

        Private Shared Sub Test_DisposeTwice()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Start(New RecordingVideoFrameSink())
            backend.Stop()
            backend.Dispose()
            ' Second Dispose: no-op, NO exception.
            backend.Dispose()
            ' Third too.
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StartAfterDispose()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Start(New RecordingVideoFrameSink()),
                "Start after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_StopAfterDispose()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Stop(),
                "Stop after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_InitializeAfterDispose()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Initialize(CreateDdagrabContext()),
                "Initialize after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_InitializeTwice()
            Dim backend = CreateDefaultBackend()
            backend.Initialize(CreateDdagrabContext())
            ' Second Initialize: failure (state is not Created).
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() backend.Initialize(CreateDdagrabContext()),
                "Initialize twice must throw InvalidOperationException")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_InitializeWrongBackendKind()
            Dim backend = CreateDefaultBackend()
            Dim wrongContext As New FakeVideoBackendContext(
                VideoBackendKind.GfxCapture,
                New EngineLogger("FakeBackendCtx", EngineLogger.LogLevel.Warning))
            TestHelpers.AssertThrows(Of VideoBackendConfigurationException)(
                Sub() backend.Initialize(wrongContext),
                "Initialize with BackendKind != Ddagrab must throw VideoBackendConfigurationException")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_NoFrameCountIncrements()
            ' Phase 12b: updated from the skeleton-era contract to REAL DXGI.
            ' The old asserts (NoFrameCount >= 5 fast-spin + EmittedFrames = 0)
            ' assumed the removed NoFrame skeleton worker. With real Output
            ' Duplication: an ACTIVE desktop delivers frames (EmittedFrames
            ' grows) while a QUIET desktop times out AcquireNextFrame(100ms)
            ' (NoFrameCount grows). Worker liveness = either counter moving.
            ' Windows evidence 2026-08-23: quiet assert failed with NoFrame=2
            ' BECAUSE frames were being emitted — then the leaked Running
            ' backend (no Try/Finally) caused E_INVALIDARG cascade (Phase-11
            ' lesson #2: one duplication per output per process).
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(1)
            backend.Initialize(CreateDdagrabContext())
            Dim sink As New RecordingVideoFrameSink()

            Dim progressed As Boolean
            Try
                backend.Start(sink)

                progressed = TestHelpers.SpinWaitFor(
                    Function() backend.Diagnostics.NoFrameCount + backend.Diagnostics.EmittedFrames >= 1,
                    3000)
                TestHelpers.Assert(
                    progressed,
                    "Worker loop must progress within 3 s (real DXGI: active desktop emits frames, " &
                    "quiet desktop accumulates NoFrame). Was: noFrame=" & backend.Diagnostics.NoFrameCount &
                    ", emitted=" & backend.Diagnostics.EmittedFrames)
            Finally
                ' Ownership rule (Phase-11 lesson #2): ALWAYS release the output
                ' duplication — even when an assert throws mid-test.
                Try : backend.Stop() : Catch : End Try
                Try : backend.Dispose() : Catch : End Try
            End Try

            ' Health: real DXGI interaction must be error-free.
            TestHelpers.AssertEqual(0L, backend.Diagnostics.ErrorCount, "ErrorCount must be 0 (healthy DXGI run)")
        End Sub

        Private Shared Sub Test_FactoryReturnsDdagrabBackend()
            Dim factory As New DdagrabBackendFactory()
            Dim backend = factory.Create(VideoBackendKind.Ddagrab)
            TestHelpers.Assert(backend IsNot Nothing, "factory must return a non-null backend")
            TestHelpers.Assert(
                TypeOf backend Is DdagrabBackend,
                "factory must return a DdagrabBackend instance for VideoBackendKind.Ddagrab")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_FactoryThrowsForNonDdagrabKind()
            Dim factory As New DdagrabBackendFactory()
            TestHelpers.AssertThrows(Of VideoBackendConfigurationException)(
                Sub() factory.Create(VideoBackendKind.GfxCapture),
                "factory.Create(GfxCapture) must throw VideoBackendConfigurationException")
        End Sub

        Private Shared Sub Test_DdagrabFrameDisposable()
            Dim diag As New FrameDiagnostics(42, 1_000_000, 1_000_000)
            Dim frame As New DdagrabFrame(
                VideoFrameOrigin.GpuD3D11Texture,
                VideoPixelFormat.Bgra8,
                New VideoFrameDimensions(1920, 1080),
                diag)

            TestHelpers.AssertEqual(VideoFrameOrigin.GpuD3D11Texture, frame.Origin, "frame origin")
            TestHelpers.AssertEqual(VideoPixelFormat.Bgra8, frame.PixelFormat, "frame pixel format")
            TestHelpers.AssertEqual(1920, frame.Dimensions.Width, "frame width")
            TestHelpers.AssertEqual(1080, frame.Dimensions.Height, "frame height")
            TestHelpers.AssertEqual(42L, frame.Diagnostics.Sequence, "frame sequence")
            TestHelpers.AssertEqual(0, frame.DisposeCount, "frame DisposeCount before Dispose")
            TestHelpers.Assert(Not frame.IsDisposed, "frame not yet disposed")

            frame.Dispose()
            TestHelpers.AssertEqual(1, frame.DisposeCount, "frame DisposeCount after one Dispose")
            TestHelpers.Assert(frame.IsDisposed, "frame is disposed")

            ' Second Dispose is a no-op (counter does NOT increment — frame.Dispose
            ' is not idempotent in this minimal placeholder; the sink / encoder
            ' contract requires exactly one Dispose). Tests for double-dispose
            ' detection belong to the sink/encoder, not the frame.
        End Sub

        Private Shared Sub Test_DisposeWhileRunningNoDeadlock()
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(0)  ' hot-loop the worker
            backend.Initialize(CreateDdagrabContext())
            Try
                backend.Start(New RecordingVideoFrameSink())
                Thread.Sleep(20)  ' let the worker spin briefly
            Finally
                ' Even if Start itself throws, release the duplication.
                Try : backend.Dispose() : Catch : End Try
            End Try

            Dim sw = Stopwatch.StartNew()
            backend.Dispose()
            sw.Stop()

            ' The 2-second Stop budget should NEVER trigger. Allow up to 500 ms
            ' as a generous upper bound (well below the 2,000 ms timeout).
            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 500,
                "Dispose while Running must complete in < 500 ms. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend Disposed after Dispose")
        End Sub

        Private Shared Sub Test_DisposeRepeatedlyIdempotent()
            Dim backend = CreateDefaultBackend()
            backend.WithInterAttemptDelayMs(0)
            backend.Initialize(CreateDdagrabContext())
            Try
                backend.Start(New RecordingVideoFrameSink())
                Thread.Sleep(10)
            Finally
                Try : backend.Dispose() : Catch : End Try
            End Try

            Dim sw = Stopwatch.StartNew()
            For i As Integer = 1 To 100
                backend.Dispose()
            Next
            sw.Stop()

            TestHelpers.Assert(
                sw.ElapsedMilliseconds < 1000,
                "100 Dispose calls must complete in < 1 s. Took: " & sw.ElapsedMilliseconds & " ms")
            TestHelpers.AssertEqual(
                DdagrabBackend.DdagrabBackendState.Disposed,
                backend.CurrentState, "backend Disposed after 100 Dispose calls")
        End Sub
    End Class
End Namespace
