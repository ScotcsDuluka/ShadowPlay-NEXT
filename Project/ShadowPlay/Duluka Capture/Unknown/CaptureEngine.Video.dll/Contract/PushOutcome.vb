Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Outcome of a non-blocking TryPush call into a bounded sink.
    ''' (P1-A v1.3.1 §6.3)
    ''' </summary>
    Public Enum PushOutcome
        ''' <summary>Result accepted; ownership of the wrapped frame transferred to sink.</summary>
        Pushed
        ''' <summary>Result accepted; an older result was evicted and disposed by the sink.</summary>
        Replaced
        ''' <summary>Result refused; caller retains ownership and MUST dispose the wrapped frame.</summary>
        Dropped
    End Enum
End Namespace
