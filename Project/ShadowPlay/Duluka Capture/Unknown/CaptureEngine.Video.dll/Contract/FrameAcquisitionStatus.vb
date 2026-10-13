Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Result status of a single frame-acquisition attempt. (P1-A v1.3.1 §3.1)
    '''
    ''' v1.2 removed the Dropped member — a dropped frame is NEVER pushed
    ''' downstream as a result. Drop events are tracked via
    ''' IVideoBackendDiagnostics.DroppedFrames only.
    ''' </summary>
    Public Enum FrameAcquisitionStatus
        ''' <summary>A real frame was acquired and is wrapped in the result.</summary>
        FrameAvailable
        ''' <summary>
        ''' No new frame is available right now (e.g. DXGI WAIT_TIMEOUT on a static
        ''' desktop; WGC frame pool momentarily empty). Not an error. Not a drop.
        ''' NOT pushed to the sink (§6.4).
        ''' </summary>
        NoFrame
        ''' <summary>
        ''' An error occurred while attempting to acquire the frame. The wrapped
        ''' Exception is populated. NOT used for fatal errors — fatal errors
        ''' propagate via the backend's normal exception path (§10.5).
        ''' </summary>
        [Error]
    End Enum
End Namespace
