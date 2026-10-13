Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Runtime backend failure: device lost, desktop duplication invalidated,
    ''' WGC session closed, worker thread crashed, etc.
    ''' (P1-A v1.3.1 §10.5)
    ''' </summary>
    Public NotInheritable Class VideoBackendRuntimeException
        Inherits VideoBackendException

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class
End Namespace
