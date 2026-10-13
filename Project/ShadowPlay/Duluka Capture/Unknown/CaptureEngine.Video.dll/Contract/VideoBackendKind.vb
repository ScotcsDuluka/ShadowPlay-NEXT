Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Identifies which capture backend to construct via the factory.
    ''' Single enum (NOT two booleans) — illegal states are unrepresentable.
    ''' (P1-A v1.3.1 §7.1)
    ''' </summary>
    Public Enum VideoBackendKind
        Ddagrab
        GfxCapture
    End Enum
End Namespace
