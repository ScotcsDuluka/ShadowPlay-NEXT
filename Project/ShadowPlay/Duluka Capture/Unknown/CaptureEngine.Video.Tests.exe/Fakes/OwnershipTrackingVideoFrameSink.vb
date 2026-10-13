Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Video.Tests.Fakes
    ''' <summary>
    ''' A test sink that wraps another sink and counts Dispose() calls on
    ''' every frame it observes. Used by ownership / lifetime tests.
    ''' </summary>
    Public NotInheritable Class OwnershipTrackingVideoFrameSink
        Implements IVideoFrameSink

        Private ReadOnly _inner As IVideoFrameSink
        Private ReadOnly _disposeCounts As New Dictionary(Of IVideoFrame, Integer)()
        Private ReadOnly _sync As New Object()

        Public Sub New(inner As IVideoFrameSink)
            If inner Is Nothing Then Throw New ArgumentNullException(NameOf(inner))
            _inner = inner
        End Sub

        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            Dim outcome = _inner.TryPush(result)
            SyncLock _sync
                If result.Frame IsNot Nothing Then
                    If Not _disposeCounts.ContainsKey(result.Frame) Then
                        _disposeCounts(result.Frame) = 0
                    End If
                End If
            End SyncLock
            Return outcome
        End Function

        Public Sub RecordDisposal(frame As IVideoFrame)
            SyncLock _sync
                If _disposeCounts.ContainsKey(frame) Then
                    _disposeCounts(frame) += 1
                Else
                    _disposeCounts(frame) = 1
                End If
            End SyncLock
        End Sub

        Public Function GetDisposeCount(frame As IVideoFrame) As Integer
            SyncLock _sync
                Dim count As Integer = 0
                If _disposeCounts.TryGetValue(frame, count) Then
                    Return count
                End If
                Return 0
            End SyncLock
        End Function

        Public ReadOnly Property TrackedFrameCount As Integer
            Get
                SyncLock _sync
                    Return _disposeCounts.Count
                End SyncLock
            End Get
        End Property
    End Class
End Namespace
