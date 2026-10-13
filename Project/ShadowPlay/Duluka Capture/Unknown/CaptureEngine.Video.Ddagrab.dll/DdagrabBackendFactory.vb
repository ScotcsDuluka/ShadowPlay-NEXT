Option Strict On
Option Explicit On
Option Infer On

Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Video.Backends.Ddagrab
    ''' <summary>
    ''' DdagrabBackendFactory — constructs DdagrabBackend instances.
    ''' (P1-A v1.3.1 §7.1, §11)
    '''
    ''' Implements the IVideoCaptureBackendFactory contract. Create(kind)
    ''' returns a new DdagrabBackend when kind = VideoBackendKind.Ddagrab;
    ''' throws VideoBackendConfigurationException for any other kind.
    '''
    ''' This factory is the production factory's case for
    ''' VideoBackendKind.Ddagrab. A future production VideoLayer will
    ''' dispatch to this factory (or to GfxCaptureBackendFactory) based
    ''' on the configured VideoBackendKind.
    ''' </summary>
    Public NotInheritable Class DdagrabBackendFactory
        Implements IVideoCaptureBackendFactory

        Private ReadOnly _logger As EngineLogger

        Public Sub New(Optional logger As EngineLogger = Nothing)
            _logger = If(logger, New EngineLogger("DdagrabBackendFactory"))
        End Sub

        ''' <summary>
        ''' Create a backend by VideoBackendKind. Returns a new DdagrabBackend
        ''' when kind = Ddagrab; throws VideoBackendConfigurationException
        ''' for any other kind.
        ''' </summary>
        Public Function Create(kind As VideoBackendKind) As IVideoCaptureBackend Implements IVideoCaptureBackendFactory.Create
            Select Case kind
                Case VideoBackendKind.Ddagrab
                    Return New DdagrabBackend(_logger)
                Case Else
                    Throw New VideoBackendConfigurationException(
                        "DdagrabBackendFactory.Create: expected VideoBackendKind.Ddagrab, but got " &
                        kind.ToString() & ".")
            End Select
        End Function
    End Class
End Namespace
