Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' A single captured video frame. (P1-A v1.3.1 §3.1)
    '''
    ''' An IVideoFrame ALWAYS represents a real frame — there is no "dropped"
    ''' or "empty" variant. Drop events are surfaced via
    ''' IVideoBackendDiagnostics counters (§3.2), not via the result stream.
    '''
    ''' Single-owner at all times (§3.8). The owner MUST call Dispose()
    ''' when done with the frame. The owner MAY move the frame between
    ''' threads (§6.5).
    ''' </summary>
    Public Interface IVideoFrame
        Inherits IDisposable

        ReadOnly Property Origin As VideoFrameOrigin
        ReadOnly Property PixelFormat As VideoPixelFormat
        ReadOnly Property Dimensions As VideoFrameDimensions
        ReadOnly Property Diagnostics As FrameDiagnostics
    End Interface
End Namespace
