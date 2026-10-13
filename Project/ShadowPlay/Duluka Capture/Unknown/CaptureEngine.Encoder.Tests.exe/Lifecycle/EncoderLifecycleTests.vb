Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Encoder.Backends.Fake

Namespace CaptureEngine.Encoder.Tests.Lifecycle
    ''' <summary>
    ''' Encoder lifecycle tests: Initialize/Start/Stop/Dispose state transitions
    ''' and idempotency.
    ''' </summary>
    Friend NotInheritable Class EncoderLifecycleTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("LIFECYCLE: Initialize → Initialized", AddressOf Test_Initialize)
            runner("LIFECYCLE: Initialize twice → InvalidOperationException", AddressOf Test_InitializeTwice)
            runner("LIFECYCLE: Start before Initialize → InvalidOperationException", AddressOf Test_StartBeforeInitialize)
            runner("LIFECYCLE: Start twice (idempotent)", AddressOf Test_StartTwice)
            runner("LIFECYCLE: Stop before Start (no-op)", AddressOf Test_StopBeforeStart)
            runner("LIFECYCLE: Stop twice (idempotent)", AddressOf Test_StopTwice)
            runner("LIFECYCLE: Dispose without Start", AddressOf Test_DisposeWithoutStart)
            runner("LIFECYCLE: Dispose while Running (stop path)", AddressOf Test_DisposeWhileRunning)
            runner("LIFECYCLE: Dispose twice (idempotent)", AddressOf Test_DisposeTwice)
            runner("LIFECYCLE: Encode before Initialize → InvalidOperationException", AddressOf Test_EncodeBeforeInitialize)
            runner("LIFECYCLE: Encode after Stop → InvalidOperationException", AddressOf Test_EncodeAfterStop)
            runner("LIFECYCLE: Encode after Dispose → ObjectDisposedException", AddressOf Test_EncodeAfterDispose)
            runner("LIFECYCLE: Start after Dispose → ObjectDisposedException", AddressOf Test_StartAfterDispose)
            runner("LIFECYCLE: Initialize after Dispose → ObjectDisposedException", AddressOf Test_InitializeAfterDispose)
            runner("LIFECYCLE: Restart after Stop (Stopped → Running)", AddressOf Test_RestartAfterStop)
        End Sub

        Private Shared Function CreateAndInitialize() As FakeEncoderBackend
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            Return enc
        End Function

        Private Shared Sub Test_Initialize()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            TestHelpers.AssertEqual(EncoderState.Initialized, enc.InternalState, "state after Initialize")
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState, "state after Dispose")
        End Sub

        Private Shared Sub Test_InitializeTwice()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() enc.Initialize(TestHelpers.CreateDefaultConfig()),
                "Initialize twice must throw InvalidOperationException")
            enc.Dispose()
        End Sub

        Private Shared Sub Test_StartBeforeInitialize()
            Dim enc As New FakeEncoderBackend()
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() enc.Start(),
                "Start before Initialize must throw InvalidOperationException")
            enc.Dispose()
        End Sub

        Private Shared Sub Test_StartTwice()
            Dim enc = CreateAndInitialize()
            enc.Start()
            TestHelpers.AssertEqual(EncoderState.Running, enc.InternalState, "state after first Start")
            enc.Start() ' idempotent — no exception
            TestHelpers.AssertEqual(EncoderState.Running, enc.InternalState, "state after second Start")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_StopBeforeStart()
            Dim enc = CreateAndInitialize()
            enc.Stop() ' no-op, no exception
            TestHelpers.AssertEqual(EncoderState.Initialized, enc.InternalState, "state after Stop before Start")
            enc.Dispose()
        End Sub

        Private Shared Sub Test_StopTwice()
            Dim enc = CreateAndInitialize()
            enc.Start()
            enc.Stop()
            TestHelpers.AssertEqual(EncoderState.Stopped, enc.InternalState, "state after first Stop")
            enc.Stop() ' idempotent, no exception
            TestHelpers.AssertEqual(EncoderState.Stopped, enc.InternalState, "state after second Stop")
            enc.Dispose()
        End Sub

        Private Shared Sub Test_DisposeWithoutStart()
            Dim enc = CreateAndInitialize()
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState, "state after Dispose")
        End Sub

        Private Shared Sub Test_DisposeWhileRunning()
            Dim enc = CreateAndInitialize()
            enc.Start()
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState, "state after Dispose while Running")
        End Sub

        Private Shared Sub Test_DisposeTwice()
            Dim enc = CreateAndInitialize()
            enc.Dispose()
            enc.Dispose() ' idempotent, no exception
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState, "state after double Dispose")
        End Sub

        Private Shared Sub Test_EncodeBeforeInitialize()
            Dim enc As New FakeEncoderBackend()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() enc.Encode(frame, packet),
                "Encode before Initialize must throw InvalidOperationException")
            enc.Dispose()
            frame.Dispose()
        End Sub

        Private Shared Sub Test_EncodeAfterStop()
            Dim enc = CreateAndInitialize()
            enc.Start()
            enc.Stop()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of InvalidOperationException)(
                Sub() enc.Encode(frame, packet),
                "Encode after Stop must throw InvalidOperationException")
            enc.Dispose()
            frame.Dispose()
        End Sub

        Private Shared Sub Test_EncodeAfterDispose()
            Dim enc = CreateAndInitialize()
            enc.Dispose()
            Dim frame = TestHelpers.CreateFrame(0, 1000)
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() enc.Encode(frame, packet),
                "Encode after Dispose must throw ObjectDisposedException")
            frame.Dispose()
        End Sub

        Private Shared Sub Test_StartAfterDispose()
            Dim enc = CreateAndInitialize()
            enc.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() enc.Start(),
                "Start after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_InitializeAfterDispose()
            Dim enc As New FakeEncoderBackend()
            enc.Dispose()
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() enc.Initialize(TestHelpers.CreateDefaultConfig()),
                "Initialize after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_RestartAfterStop()
            Dim enc = CreateAndInitialize()
            enc.Start()
            enc.Stop()
            TestHelpers.AssertEqual(EncoderState.Stopped, enc.InternalState, "state after Stop")
            enc.Start() ' restart allowed
            TestHelpers.AssertEqual(EncoderState.Running, enc.InternalState, "state after restart")
            enc.Stop()
            enc.Dispose()
        End Sub
    End Class
End Namespace
