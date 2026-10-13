Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' The result of a single frame-acquisition attempt. (P1-A v1.3.1 §3.1)
    '''
    ''' A value type (Structure) — no allocation per result.
    '''
    ''' The wrapped IVideoFrame is non-null ONLY when Status = FrameAvailable.
    ''' Drop events are NOT a result type (§3.1) — they are observable via
    ''' IVideoBackendDiagnostics counters only.
    ''' </summary>
    Public Structure FrameAcquisitionResult
        Public ReadOnly Property Status As FrameAcquisitionStatus
        Public ReadOnly Property Frame As IVideoFrame          ' valid only when Status = FrameAvailable
        Public ReadOnly Property Sequence As Long               ' always populated (backend's monotonic counter)
        Public ReadOnly Property AttemptTimeTicks As Long       ' always populated — when the acquisition attempt began
        Public ReadOnly Property [Error] As Exception           ' valid only when Status = Error; Nothing otherwise

        Private Sub New(status As FrameAcquisitionStatus,
                        frame As IVideoFrame,
                        sequence As Long,
                        attemptTime As Long,
                        [error] As Exception)
            Me.Status = status
            Me.Frame = frame
            Me.Sequence = sequence
            Me.AttemptTimeTicks = attemptTime
            Me.Error = [error]
        End Sub

        ' Explicit factories — prevent invalid combinations.
        ' NOTE (v1.2/v1.3.1): there is NO Dropped factory. A dropped frame is NOT
        ' a result; it is a diagnostics counter event (§3.2).

        Public Shared Function Available(frame As IVideoFrame, sequence As Long, attemptTime As Long) As FrameAcquisitionResult
            If frame Is Nothing Then Throw New ArgumentNullException(NameOf(frame))
            Return New FrameAcquisitionResult(FrameAcquisitionStatus.FrameAvailable, frame, sequence, attemptTime, Nothing)
        End Function

        Public Shared Function NoFrame(sequence As Long, attemptTime As Long) As FrameAcquisitionResult
            Return New FrameAcquisitionResult(FrameAcquisitionStatus.NoFrame, Nothing, sequence, attemptTime, Nothing)
        End Function

        Public Shared Function FromError([error] As Exception, sequence As Long, attemptTime As Long) As FrameAcquisitionResult
            If [error] Is Nothing Then Throw New ArgumentNullException(NameOf([error]))
            Return New FrameAcquisitionResult(FrameAcquisitionStatus.Error, Nothing, sequence, attemptTime, [error])
        End Function
    End Structure
End Namespace
