Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Video.Tests.Fakes
    ''' <summary>
    ''' A test sink that simulates DropOldest behaviour: returns Replaced on
    ''' every push after the first (which gets Pushed). Used to exercise the
    ''' backend's "Replaced → EmittedFrames++ AND ReplacedFrames++" path.
    '''
    ''' Note: in the real BoundedVideoFrameSink, only pushes that actually
    ''' evict an older result return Replaced. This test double simplifies
    ''' that to "always Replaced after the first" so tests can deterministically
    ''' trigger the Replaced path.
    ''' </summary>
    Public NotInheritable Class EvictingVideoFrameSink
        Implements IVideoFrameSink

        Private _evictedFrames As New List(Of IVideoFrame)()
        Private _pushCount As Integer = 0
        Private _sync As New Object()

        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            SyncLock _sync
                _pushCount += 1
                If _pushCount = 1 Then
                    Return PushOutcome.Pushed
                Else
                    If result.Frame IsNot Nothing Then
                        _evictedFrames.Add(result.Frame)
                    End If
                    Return PushOutcome.Replaced
                End If
            End SyncLock
        End Function

        Public ReadOnly Property EvictedFrames As IReadOnlyList(Of IVideoFrame)
            Get
                SyncLock _sync
                    Return _evictedFrames.ToArray()
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property PushCount As Integer
            Get
                SyncLock _sync
                    Return _pushCount
                End SyncLock
            End Get
        End Property
    End Class
End Namespace
