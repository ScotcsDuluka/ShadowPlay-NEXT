Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Stop/Dispose path failed (rare; usually wrapped+logged by Dispose).
    ''' (P1-A v1.3.1 §10.5)
    ''' </summary>
    Public NotInheritable Class VideoBackendShutdownException
        Inherits VideoBackendException

        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class
End Namespace
