Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Base type for all backend exceptions. (P1-A v1.3.1 §10.5)
    ''' MustInherit so callers can write a single Catch ex As VideoBackendException
    ''' handler if they want.
    ''' </summary>
    Public MustInherit Class VideoBackendException
        Inherits Exception

        Protected Sub New(message As String, inner As Exception)
            MyBase.New(message, inner)
        End Sub
    End Class
End Namespace
