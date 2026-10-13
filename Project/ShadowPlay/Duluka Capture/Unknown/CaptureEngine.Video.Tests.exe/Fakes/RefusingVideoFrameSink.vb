Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Video.Tests.Fakes
    ''' <summary>
    ''' A test sink that always returns Dropped. Used to exercise the
    ''' backend's "dispose the refused frame, increment DroppedFrames"
    ''' code path.
    ''' </summary>
    Public NotInheritable Class RefusingVideoFrameSink
        Implements IVideoFrameSink

        Private _refusedCount As Integer = 0
        Private _sync As New Object()

        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            SyncLock _sync
                _refusedCount += 1
            End SyncLock
            Return PushOutcome.Dropped
        End Function

        Public ReadOnly Property RefusedCount As Integer
            Get
                SyncLock _sync
                    Return _refusedCount
                End SyncLock
            End Get
        End Property
    End Class
End Namespace
