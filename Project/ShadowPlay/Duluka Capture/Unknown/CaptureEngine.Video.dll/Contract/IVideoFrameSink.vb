Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Sink that the backend pushes FrameAcquisitionResult values into.
    ''' (P1-A v1.3.1 §6.3)
    '''
    ''' Non-blocking by contract. NEVER throws for queue-full conditions;
    ''' returns Dropped instead. The backend examines the outcome and reacts
    ''' (dispose the refused frame, increment DroppedFrames).
    '''
    ''' The sink owns the bounded queue, its capacity, and the
    ''' BoundedHandoffPolicy (§15.4). The backend has zero knowledge of
    ''' these — it only observes PushOutcome.
    ''' </summary>
    Public Interface IVideoFrameSink
        ''' <summary>
        ''' Attempt to push a result into the bounded sink. Returns the outcome.
        ''' NEVER blocks for an unbounded duration. NEVER throws for queue-full
        ''' conditions; returns Dropped instead.
        ''' </summary>
        Function TryPush(result As FrameAcquisitionResult) As PushOutcome
    End Interface
End Namespace
