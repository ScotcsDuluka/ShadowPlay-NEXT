Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Encoder
Imports CaptureEngine.Encoder.Nvenc
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Encoder.Tests.Lifecycle

    ''' <summary>
    ''' C/6 — NvencEncoderBackend lifecycle matrix that runs WITHOUT an NVIDIA
    ''' GPU (deterministic on any machine). Covers: state convergence on failed
    ''' Initialize, forbidden transitions, Dispose idempotency/race, retry
    ''' semantics after config-validation failure.
    '''
    ''' Hardware-only paths (real session open, real Encode, the Initialize
    ''' generic-Catch unwind between session-open and InitializeEncoder) are
    ''' BLOCKED on non-NVIDIA machines — the GPU branch below initializes real
    ''' hardware when present so the same assertions hold on both machine
    ''' classes. No NVIDIA success is faked anywhere.
    ''' </summary>
    Friend NotInheritable Class NvencLifecycleTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("NVENC-LC: fresh backend state = Created", AddressOf Test_FreshState)
            runner("NVENC-LC: Encode before Initialize → InvalidOperationException", AddressOf Test_EncodeBeforeInit)
            runner("NVENC-LC: Flush before Initialize → InvalidOperationException", AddressOf Test_FlushBeforeInit)
            runner("NVENC-LC: Stop before Initialize → no-op, stays Created", AddressOf Test_StopBeforeInit)
            runner("NVENC-LC: config-validation failure stays Created (retryable)", AddressOf Test_ConfigFailureRetryable)
            runner("NVENC-LC: encode-dims-above-input rejected, stays Created", AddressOf Test_UpscaleRejected)
            runner("NVENC-LC: Initialize failure converges to Faulted (or Initialized on NVIDIA)", AddressOf Test_InitConverges)
            runner("NVENC-LC: re-Initialize after Faulted → InvalidOperationException", AddressOf Test_ReInitAfterFault)
            runner("NVENC-LC: retry after config failure reaches real init path", AddressOf Test_RetryAfterConfigFailure)
            runner("NVENC-LC: Dispose after failure → Disposed, idempotent, fast", AddressOf Test_DisposeAfterFailure)
            runner("NVENC-LC: Encode after Dispose → ObjectDisposedException", AddressOf Test_EncodeAfterDispose)
            runner("NVENC-LC: concurrent Dispose ×2 → single convergence, no hang", AddressOf Test_ConcurrentDispose)
        End Sub

        Private Shared Sub DiscardLog(message As String)
            ' discard
        End Sub

        Private Shared Sub DiscardPacket(packet As EncodedPacket)
            ' discard
        End Sub

        Private Shared Function NewBackend() As NvencEncoderBackend
            Return New NvencEncoderBackend(New EngineLogger("nvenc-lifecycle", EngineLogger.LogLevel.Info, AddressOf DiscardLog))
        End Function

        ''' <summary>Valid NVENC config — small dims, CBR, standard GOP.</summary>
        Private Shared Function ValidConfig() As EncoderConfig
            Dim c As New EncoderConfig()
            c.CodecKey = "NVENC_H264"
            c.RateControl = "cbr"
            c.BitrateBps = 1000000L
            c.GopSize = 60
            c.FrameRateFps = 30
            c.ExpectedWidth = 640
            c.ExpectedHeight = 480
            Return c
        End Function

        ''' <summary>
        ''' Shared post-Initialize convergence contract: either the backend
        ''' initialized on real hardware (Initialized — caller must Dispose) or
        ''' it failed at the first hardware seam (device/function-table/session)
        ''' and MUST be in the terminal Faulted state with nothing acquired.
        ''' </summary>
        Private Shared Function TryInitializeAndAssertConvergence(enc As NvencEncoderBackend) As Boolean
            Dim initialized As Boolean = False
            Try
                enc.Initialize(ValidConfig())
                initialized = True
            Catch ex As EncoderConfigurationException
                Throw New InvalidOperationException(
                    "Valid config must not fail validation: " & ex.Message, ex)
            Catch ex As Exception
                ' Hardware seam failure — state must be terminal Faulted.
                TestHelpers.AssertEqual(EncoderState.Faulted, enc.CurrentState,
                    "failed Initialize must converge to Faulted (was: " & ex.Message & ")")
            End Try
            Return initialized
        End Function

        Private Shared Sub Test_FreshState()
            Dim enc = NewBackend()
            Try
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState, "fresh backend state")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_EncodeBeforeInit()
            Dim enc = NewBackend()
            Try
                Dim packet As EncodedPacket = Nothing
                TestHelpers.AssertThrows(Of InvalidOperationException)(
                    Sub() enc.Encode(TestHelpers.CreateFrame(0, 0L, 640, 480), packet),
                    "Encode before Initialize must throw")
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState, "state unchanged after Encode rejection")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_FlushBeforeInit()
            Dim enc = NewBackend()
            Try
                TestHelpers.AssertThrows(Of InvalidOperationException)(
                    Sub() enc.Flush(AddressOf DiscardPacket),
                    "Flush before Initialize must throw")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_StopBeforeInit()
            Dim enc = NewBackend()
            Try
                enc.Stop() ' contract: no-op from Created
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState, "Stop from Created is a no-op")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_ConfigFailureRetryable()
            Dim enc = NewBackend()
            Try
                Dim bad As New EncoderConfig()
                bad.CodecKey = "" ' validation failure — no resources acquired
                TestHelpers.AssertThrows(Of EncoderConfigurationException)(
                    Sub() enc.Initialize(bad),
                    "empty CodecKey must fail validation")
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState,
                    "config-validation failure must stay Created (retryable)")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_UpscaleRejected()
            Dim enc = NewBackend()
            Try
                Dim bad As New EncoderConfig()
                bad.ExpectedWidth = 640
                bad.ExpectedHeight = 480
                bad.EncodeWidth = 1280  ' larger than input → loud config error
                TestHelpers.AssertThrows(Of EncoderConfigurationException)(
                    Sub() enc.Initialize(bad),
                    "encode dims above input must fail validation")
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState,
                    "upscale rejection must stay Created")
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_InitConverges()
            Dim enc = NewBackend()
            Dim initialized As Boolean = TryInitializeAndAssertConvergence(enc)
            If initialized Then
                TestHelpers.AssertEqual(EncoderState.Initialized, enc.CurrentState,
                    "successful hardware Initialize must be Initialized")
            End If
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState, "post-dispose state")
        End Sub

        Private Shared Sub Test_ReInitAfterFault()
            Dim enc = NewBackend()
            Try
                Dim initialized As Boolean = TryInitializeAndAssertConvergence(enc)
                If initialized Then
                    ' NVIDIA machine: Initialized→Initialized re-init is forbidden too.
                    TestHelpers.AssertThrows(Of InvalidOperationException)(
                        Sub() enc.Initialize(ValidConfig()),
                        "re-Initialize from Initialized must throw")
                Else
                    TestHelpers.AssertThrows(Of InvalidOperationException)(
                        Sub() enc.Initialize(ValidConfig()),
                        "re-Initialize from Faulted must throw (Faulted is terminal until Dispose)")
                End If
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_RetryAfterConfigFailure()
            Dim enc = NewBackend()
            Try
                Dim bad As New EncoderConfig()
                bad.CodecKey = ""
                TestHelpers.AssertThrows(Of EncoderConfigurationException)(
                    Sub() enc.Initialize(bad), "first attempt fails validation")
                TestHelpers.AssertEqual(EncoderState.Created, enc.CurrentState, "still Created after validation failure")
                ' Retry with a valid config must be accepted by the state gate and
                ' reach the hardware seam (success on NVIDIA / Faulted elsewhere).
                TryInitializeAndAssertConvergence(enc)
            Finally
                enc.Dispose()
            End Try
        End Sub

        Private Shared Sub Test_DisposeAfterFailure()
            Dim enc = NewBackend()
            TryInitializeAndAssertConvergence(enc) ' Faulted (or Initialized on NVIDIA)
            Dim sw As Stopwatch = Stopwatch.StartNew()
            enc.Dispose()
            enc.Dispose() ' idempotent — must not throw
            sw.Stop()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState, "state after double Dispose")
            TestHelpers.Assert(sw.ElapsedMilliseconds < 2000,
                "Dispose with no in-flight encode must not hit the 5s drain budget (took " &
                sw.ElapsedMilliseconds & "ms)")
        End Sub

        Private Shared Sub Test_EncodeAfterDispose()
            Dim enc = NewBackend()
            enc.Dispose()
            Dim packet As EncodedPacket = Nothing
            TestHelpers.AssertThrows(Of ObjectDisposedException)(
                Sub() enc.Encode(TestHelpers.CreateFrame(0, 0L, 640, 480), packet),
                "Encode after Dispose must throw ObjectDisposedException")
        End Sub

        Private Shared Sub Test_ConcurrentDispose()
            Dim enc = NewBackend()
            TryInitializeAndAssertConvergence(enc)
            Dim done(1) As ManualResetEvent
            done(0) = New ManualResetEvent(False)
            done(1) = New ManualResetEvent(False)
            Dim t0 As New Thread(Sub()
                                     Try : enc.Dispose() : Catch : End Try
                                     done(0).Set()
                                 End Sub)
            Dim t1 As New Thread(Sub()
                                     Try : enc.Dispose() : Catch : End Try
                                     done(1).Set()
                                 End Sub)
            t0.Start()
            t1.Start()
            TestHelpers.Assert(done(0).WaitOne(TimeSpan.FromSeconds(10)), "Dispose thread 0 must complete")
            TestHelpers.Assert(done(1).WaitOne(TimeSpan.FromSeconds(10)), "Dispose thread 1 must complete")
            t0.Join(TimeSpan.FromSeconds(5))
            t1.Join(TimeSpan.FromSeconds(5))
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState, "state after concurrent Dispose ×2")
            done(0).Dispose()
            done(1).Dispose()
        End Sub

    End Class

End Namespace
