Option Strict On
Option Explicit On

Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Object passed to a backend at Initialize time. Gives the backend
    ''' ONLY what it needs — a logger, the BackendKind, and (optionally,
    ''' per §5 device-ownership decision) the D3D11 device to use.
    ''' Does NOT expose CaptureEngine's state machine or its sync lock.
    ''' (P1-A v1.3.1 §11)
    ''' </summary>
    Public Interface IVideoBackendContext
        ReadOnly Property Logger As EngineLogger
        ReadOnly Property BackendKind As VideoBackendKind
    End Interface
End Namespace
