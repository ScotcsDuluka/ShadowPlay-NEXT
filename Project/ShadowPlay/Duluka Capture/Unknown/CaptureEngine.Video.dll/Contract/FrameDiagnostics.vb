Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Per-frame diagnostics carried on every IVideoFrame. (P1-A v1.3.1 §3.7)
    '''
    ''' All timestamps are expressed in the Engine's chosen internal PTS unit
    ''' (§3.6.1 Option α / β / γ — P1-B implementation decision pending; for
    ''' P1-B.1 the FakeVideoCaptureBackend uses a deterministic synthetic
    ''' tick source so tests do not depend on a real clock).
    '''
    ''' IMPORTANT (§3.6.1): TimeSpan.Ticks (100-ns units) and
    ''' Stopwatch.GetTimestamp() (raw QPC counter ticks at Stopwatch.Frequency)
    ''' are DIFFERENT units and MUST NOT be subtracted directly. P1-B's
    ''' implementer MUST choose one internal unit and document the conversion.
    ''' </summary>
    Public Structure FrameDiagnostics
        Public ReadOnly Property Sequence As Long
        Public ReadOnly Property CaptureTimeTicks As Long
        Public ReadOnly Property PresentationTimestampTicks As Long

        Public Sub New(seq As Long, capTime As Long, pts As Long)
            Sequence = seq
            CaptureTimeTicks = capTime
            PresentationTimestampTicks = pts
        End Sub
    End Structure
End Namespace
