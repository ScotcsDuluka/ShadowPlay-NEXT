Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Read-only diagnostic counters exposed by a backend.
    ''' (P1-A v1.3.1 §3.2)
    '''
    ''' This is the SOLE channel for drop/replaced metrics. Because Dropped
    ''' is no longer a downstream result (§3.1), the consumer learns about
    ''' drops via these counters.
    '''
    ''' All counters are read-only and safe to poll from any thread.
    ''' </summary>
    Public Interface IVideoBackendDiagnostics
        ''' <summary>Incremented when TryPush returned Pushed.</summary>
        ReadOnly Property EmittedFrames As Long
        ''' <summary>Incremented when TryPush returned Dropped (caller disposed the refused frame).</summary>
        ReadOnly Property DroppedFrames As Long
        ''' <summary>Incremented when TryPush returned Replaced (sink evicted an older result).</summary>
        ReadOnly Property ReplacedFrames As Long
        ''' <summary>Incremented when the backend's poll returned NoFrame (internal; NOT pushed to sink).</summary>
        ReadOnly Property NoFrameCount As Long
        ''' <summary>Incremented when the backend's poll returned Error.</summary>
        ReadOnly Property ErrorCount As Long
    End Interface
End Namespace
