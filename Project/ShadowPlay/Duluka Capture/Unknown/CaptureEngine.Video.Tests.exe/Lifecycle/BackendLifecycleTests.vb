Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Video.Backends.Fake
Imports CaptureEngine.Video.Tests.Fakes

Namespace CaptureEngine.Video.Tests.Lifecycle
    ''' <summary>
    ''' Backend lifecycle tests. Mirrors the 9 negative cases from P1-A v1.3.1 §4.3
    ''' plus positive cases, all driven by the FakeVideoCaptureBackend.
    ''' </summary>
    Friend NotInheritable Class BackendLifecycleTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("LIFECYCLE: Initialize -> Initialized", AddressOf Test_Initialize)
            runner("LIFECYCLE: Start before Initialize (negative)", AddressOf Test_StartBeforeInitialize)
            runner("LIFECYCLE: Start twice (idempotent)", AddressOf Test_StartTwice)
            runner("LIFECYCLE: Stop before Start (negative)", AddressOf Test_StopBeforeStart)
            runner("LIFECYCLE: Stop twice (idempotent)", AddressOf Test_StopTwice)
            runner("LIFECYCLE: Dispose while Running (stop-path invoked)", AddressOf Test_DisposeWhileRunning)
            runner("LIFECYCLE: Dispose twice (idempotent)", AddressOf Test_DisposeTwice)
            runner("LIFECYCLE: Start after Dispose (ObjectDisposedException)", AddressOf Test_StartAfterDispose)
            runner("LIFECYCLE: Stop after Dispose (ObjectDisposedException)", AddressOf Test_StopAfterDispose)
            runner("LIFECYCLE: Initialize after Dispose (ObjectDisposedException)", AddressOf Test_InitializeAfterDispose)
            runner("LIFECYCLE: Initialize twice (second is failure)", AddressOf Test_InitializeTwice)
        End Sub

        Private Shared Sub Test_Initialize()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Initialized,
                backend.CurrentState, "state after Initialize")
            backend.Dispose()
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Disposed,
                backend.CurrentState, "state after Dispose")
        End Sub

        Private Shared Sub Test_StartBeforeInitialize()
            Dim backend = TestHelpers.CreateDefaultFake()
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() backend.Start(New RecordingVideoFrameSink()),
                "Start before Initialize must throw InvalidOperationException")
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Created,
                backend.CurrentState, "state after failed Start")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StartTwice()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Running,
                backend.CurrentState, "state after first Start")
            ' Second Start: idempotent, no-op, no exception.
            backend.Start(sink)
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Running,
                backend.CurrentState, "state after second Start (idempotent)")
            backend.Stop()
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StopBeforeStart()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            ' Stop before Start: no-op, NO exception.
            backend.Stop()
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Initialized,
                backend.CurrentState, "state after Stop before Start (no-op)")
            ' Start should still work after the no-op Stop.
            backend.Start(New RecordingVideoFrameSink())
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Running,
                backend.CurrentState, "state after Start")
            backend.Stop()
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StopTwice()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            backend.Start(New RecordingVideoFrameSink())
            backend.Stop()
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Stopped,
                backend.CurrentState, "state after first Stop")
            ' Second Stop: no-op, NO exception.
            backend.Stop()
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Stopped,
                backend.CurrentState, "state after second Stop (idempotent)")
            backend.Dispose()
        End Sub

        Private Shared Sub Test_DisposeWhileRunning()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            Dim sink As New RecordingVideoFrameSink()
            backend.Start(sink)
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Running,
                backend.CurrentState, "state after Start")

            ' Dispose while Running: routes through stop path internally.
            backend.Dispose()
            TestHelpers.AssertEqual(
                FakeVideoCaptureBackend.FakeBackendState.Disposed,
                backend.CurrentState, "state after Dispose while Running")
        End Sub

        Private Shared Sub Test_DisposeTwice()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            backend.Start(New RecordingVideoFrameSink())
            backend.Stop()
            backend.Dispose()
            ' Second Dispose: no-op, NO exception.
            backend.Dispose()
            ' Third too.
            backend.Dispose()
        End Sub

        Private Shared Sub Test_StartAfterDispose()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Start(New RecordingVideoFrameSink()),
                "Start after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_StopAfterDispose()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Stop(),
                "Stop after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_InitializeAfterDispose()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            backend.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() backend.Initialize(TestHelpers.CreateContext()),
                "Initialize after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_InitializeTwice()
            Dim backend = TestHelpers.CreateDefaultFake()
            backend.Initialize(TestHelpers.CreateContext())
            ' Second Initialize: failure (state is not Created).
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() backend.Initialize(TestHelpers.CreateContext()),
                "Initialize twice must throw InvalidOperationException")
            backend.Dispose()
        End Sub
    End Class
End Namespace
