Option Strict On
Option Explicit On
Option Infer On

Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Video.Backends.Fake
    ''' <summary>
    ''' Simple IVideoBackendContext implementation used by the fake backend
    ''' and tests. (P1-A v1.3.1 §11)
    ''' Real D3D11 device injection is NOT in scope for P1-B.1 — the contract
    ''' leaves device-ownership open per §5 (gated on V1, not yet implemented).
    ''' </summary>
    Public NotInheritable Class FakeVideoBackendContext
        Implements IVideoBackendContext

        Private ReadOnly _logger As EngineLogger
        Private ReadOnly _backendKind As VideoBackendKind

        Public Sub New(backendKind As VideoBackendKind, Optional logger As EngineLogger = Nothing)
            _backendKind = backendKind
            _logger = If(logger, New EngineLogger("FakeVideoBackendContext"))
        End Sub

        Public ReadOnly Property Logger As EngineLogger Implements IVideoBackendContext.Logger
            Get
                Return _logger
            End Get
        End Property

        Public ReadOnly Property BackendKind As VideoBackendKind Implements IVideoBackendContext.BackendKind
            Get
                Return _backendKind
            End Get
        End Property
    End Class
End Namespace
