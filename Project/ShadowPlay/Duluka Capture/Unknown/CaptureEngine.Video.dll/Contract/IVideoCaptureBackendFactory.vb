Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Factory that constructs a backend by VideoBackendKind.
    ''' (P1-A v1.3.1 §7.1, §11)
    ''' Single enum (NOT two booleans) — illegal states are unrepresentable.
    ''' </summary>
    Public Interface IVideoCaptureBackendFactory
        Function Create(kind As VideoBackendKind) As IVideoCaptureBackend
    End Interface
End Namespace
