Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Video.Tests.Fakes
    ''' <summary>
    ''' A test sink whose TryPush blocks for a configurable number of milliseconds
    ''' before returning Pushed. Used to simulate a slow consumer for backpressure
    ''' / queue-full tests against the real BoundedVideoFrameSink.
    ''' </summary>
    Public NotInheritable Class BlockingVideoFrameSink
        Implements IVideoFrameSink

        Private ReadOnly _delayMs As Integer
        Private _pushCount As Integer = 0
        Private _sync As New Object()

        Public Sub New(delayMs As Integer)
            If delayMs < 0 Then Throw New ArgumentOutOfRangeException(NameOf(delayMs))
            _delayMs = delayMs
        End Sub

        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            If _delayMs > 0 Then
                Thread.Sleep(_delayMs)
            End If
            SyncLock _sync
                _pushCount += 1
            End SyncLock
            Return PushOutcome.Pushed
        End Function

        Public ReadOnly Property PushCount As Integer
            Get
                SyncLock _sync
                    Return _pushCount
                End SyncLock
            End Get
        End Property
    End Class
End Namespace
