Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Configuration
    ''' <summary>
    ''' Foundation-level configuration contract for CaptureEngine.
    '''
    ''' Intentionally minimal: only settings required by Phase 0 (Foundation)
    ''' live here. Video / Audio / Pipeline / Output configuration will be
    ''' added in later phases as separate contracts — they MUST NOT be dragged
    ''' back from any legacy config class.
    ''' </summary>
    Public NotInheritable Class EngineConfig
        Private _logLevel As EngineLogger.LogLevel = EngineLogger.LogLevel.Info

        Public Sub New()
        End Sub

        ''' <summary>
        ''' Minimum log severity to emit. Default is <see cref="EngineLogger.LogLevel.Info"/>.
        ''' </summary>
        Public Property LogLevel As EngineLogger.LogLevel
            Get
                Return _logLevel
            End Get
            Set(value As EngineLogger.LogLevel)
                _logLevel = value
            End Set
        End Property

        ''' <summary>Convenience factory that returns a config with default values.</summary>
        Public Shared Function CreateDefault() As EngineConfig
            Return New EngineConfig()
        End Function
    End Class
End Namespace
