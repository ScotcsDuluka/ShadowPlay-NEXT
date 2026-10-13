Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading
Imports CaptureEngine.Encoder.Backends.Fake

Namespace CaptureEngine.Encoder.Tests.Concurrency
    ''' <summary>
    ''' Concurrency tests: only test guarantees the implementation actually promises.
    ''' FakeEncoderBackend uses _sync for state + Interlocked for counters.
    ''' </summary>
    Friend NotInheritable Class EncoderConcurrencyTests

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("CONCURRENCY: Start + Stop concurrent → no crash", AddressOf Test_StartStopConcurrent)
            runner("CONCURRENCY: Stop + Dispose concurrent → Disposed", AddressOf Test_StopDisposeConcurrent)
            runner("CONCURRENCY: 2 threads Encode → no corruption", AddressOf Test_ConcurrentEncode)
            runner("CONCURRENCY: Encode + Dispose → no deadlock", AddressOf Test_EncodeDisposeConcurrent)
        End Sub

        Private Shared Sub Test_StartStopConcurrent()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            Dim t1 As New Thread(Sub() enc.Start())
            Dim t2 As New Thread(Sub() enc.Stop())
            t1.Start()
            t2.Start()
            t1.Join(TimeSpan.FromSeconds(5))
            t2.Join(TimeSpan.FromSeconds(5))
            Dim s As EncoderState = enc.InternalState
            TestHelpers.Assert(s = EncoderState.Running OrElse s = EncoderState.Stopped,
                "state must be Running or Stopped after concurrent Start+Stop, got " & s.ToString())
            enc.Dispose()
        End Sub

        Private Shared Sub Test_StopDisposeConcurrent()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            enc.Start()
            Dim t1 As New Thread(Sub() enc.Stop())
            Dim t2 As New Thread(Sub() enc.Dispose())
            t1.Start()
            t2.Start()
            t1.Join(TimeSpan.FromSeconds(5))
            t2.Join(TimeSpan.FromSeconds(5))
            ' Final state should be Disposed (Dispose wins)
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState,
                "state must be Disposed after concurrent Stop+Dispose")
        End Sub

        Private Shared Sub Test_ConcurrentEncode()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            enc.Start()
            Dim packetsProduced As Integer = 0
            Dim lockObj As New Object()
            Dim threads(1) As Thread
            For i As Integer = 0 To 1
                threads(i) = New Thread(Sub()
                                           For j As Integer = 0 To 9
                                               Dim frame = TestHelpers.CreateFrame(j, j * 1000L)
                                               Dim packet As EncodedPacket = Nothing
                                               Try
                                                   If enc.Encode(frame, packet) Then
                                                       SyncLock lockObj
                                                           packetsProduced += 1
                                                       End SyncLock
                                                       packet.Dispose()
                                                   End If
                                               Catch ex As Exception
                                                   ' Encode may throw InvalidOperationException if state
                                                   ' transitions concurrently — acceptable; just dispose frame
                                               End Try
                                               frame.Dispose()
                                           Next
                                       End Sub)
                threads(i).Start()
            Next
            threads(0).Join(TimeSpan.FromSeconds(10))
            threads(1).Join(TimeSpan.FromSeconds(10))
            TestHelpers.Assert(packetsProduced > 0, "at least some packets must be produced")
            TestHelpers.AssertEqual(20L, enc.Diagnostics.SubmittedFrames,
                "SubmittedFrames must be 20 (2 threads × 10 frames)")
            enc.Stop()
            enc.Dispose()
        End Sub

        Private Shared Sub Test_EncodeDisposeConcurrent()
            Dim enc As New FakeEncoderBackend()
            enc.Initialize(TestHelpers.CreateDefaultConfig())
            enc.Start()
            Dim encodeDone As New ManualResetEvent(False)
            Dim disposeDone As New ManualResetEvent(False)
            Dim encodeThread As New Thread(Sub()
                                              For i As Integer = 0 To 99
                                                  Dim frame = TestHelpers.CreateFrame(i, i * 1000L)
                                                  Dim packet As EncodedPacket = Nothing
                                                  Try
                                                      enc.Encode(frame, packet)
                                                      If packet IsNot Nothing Then packet.Dispose()
                                                  Catch ex As Exception
                                                      ' Dispose racing with Encode may throw ObjectDisposedException
                                                      ' or InvalidOperationException — acceptable
                                                  End Try
                                                  frame.Dispose()
                                              Next
                                              encodeDone.Set()
                                          End Sub)
            Dim disposeThread As New Thread(Sub()
                                             Thread.Sleep(50) ' let encode start
                                             enc.Dispose()
                                             disposeDone.Set()
                                         End Sub)
            encodeThread.Start()
            disposeThread.Start()
            TestHelpers.Assert(encodeDone.WaitOne(TimeSpan.FromSeconds(10)), "Encode thread must complete")
            TestHelpers.Assert(disposeDone.WaitOne(TimeSpan.FromSeconds(10)), "Dispose thread must complete")
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.InternalState, "final state must be Disposed")
        End Sub
    End Class
End Namespace
