Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Video.Tests.Fakes
    ''' <summary>
    ''' A test sink that records every TryPush call (the result, the outcome,
    ''' and the order). Used by delivery / sequencing / replaceability tests.
    ''' Does NOT block the caller. Always returns Pushed unless the configured
    ''' outcome override is set.
    ''' </summary>
    Public NotInheritable Class RecordingVideoFrameSink
        Implements IVideoFrameSink

        Private ReadOnly _records As New List(Of PushRecord)()
        Private ReadOnly _sync As New Object()
        Private _outcomeOverride As PushOutcome? = Nothing
        Private _disposedFrames As New List(Of IVideoFrame)()

        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            SyncLock _sync
                Dim outcome As PushOutcome
                If _outcomeOverride.HasValue Then
                    outcome = _outcomeOverride.Value
                    If outcome = PushOutcome.Dropped Then
                        ' Caller retains ownership — do nothing with the frame.
                    End If
                Else
                    outcome = PushOutcome.Pushed
                    ' Take ownership of the frame (if any) for lifetime tracking.
                    If result.Frame IsNot Nothing Then
                        _disposedFrames.Add(result.Frame)
                    End If
                End If

                _records.Add(New PushRecord(result.Status, result.Sequence, outcome, result.Frame, result.AttemptTimeTicks))
                Return outcome
            End SyncLock
        End Function

        Public Sub SetOutcomeOverride(outcome As PushOutcome?)
            SyncLock _sync
                _outcomeOverride = outcome
            End SyncLock
        End Sub

        Public ReadOnly Property Records As IReadOnlyList(Of PushRecord)
            Get
                SyncLock _sync
                    Return _records.ToArray()
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property RecordedCount As Integer
            Get
                SyncLock _sync
                    Return _records.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>All frames the sink took ownership of (Pushed outcome).</summary>
        Public ReadOnly Property OwnedFrames As IReadOnlyList(Of IVideoFrame)
            Get
                SyncLock _sync
                    Return _disposedFrames.ToArray()
                End SyncLock
            End Get
        End Property

        Public Sub DisposeAllOwnedFrames()
            SyncLock _sync
                For Each f In _disposedFrames
                    Try
                        f.Dispose()
                    Catch
                        ' Swallow — test cleanup.
                    End Try
                Next
                _disposedFrames.Clear()
            End SyncLock
        End Sub
    End Class

    ''' <summary>One recorded TryPush event.</summary>
    Public NotInheritable Class PushRecord
        Public ReadOnly Property Status As FrameAcquisitionStatus
        Public ReadOnly Property Sequence As Long
        Public ReadOnly Property Outcome As PushOutcome
        Public ReadOnly Property Frame As IVideoFrame
        Public ReadOnly Property AttemptTimeTicks As Long

        Public Sub New(status As FrameAcquisitionStatus,
                       sequence As Long,
                       outcome As PushOutcome,
                       frame As IVideoFrame,
                       attemptTime As Long)
            Me.Status = status
            Me.Sequence = sequence
            Me.Outcome = outcome
            Me.Frame = frame
            Me.AttemptTimeTicks = attemptTime
        End Sub
    End Class
End Namespace
