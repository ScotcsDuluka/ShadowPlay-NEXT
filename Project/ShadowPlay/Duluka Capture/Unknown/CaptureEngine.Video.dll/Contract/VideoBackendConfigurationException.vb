Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Thrown by Initialize() when config values are invalid for this backend
    ''' (e.g. requested pixel format not supported; requested monitor index
    ''' out of range; host cannot satisfy Phase 1 baseline BGRA8 requirement).
    ''' (P1-A v1.3.1 §10.5)
    ''' </summary>
    Public NotInheritable Class VideoBackendConfigurationException
        Inherits VideoBackendException

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class
End Namespace
