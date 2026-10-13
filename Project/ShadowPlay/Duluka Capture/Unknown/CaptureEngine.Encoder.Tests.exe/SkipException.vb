Option Strict On
Option Explicit On
Option Infer On

' SkipException.vb — C/1: environment-not-capable signal for the Encoder
' contract suite, mirroring CaptureEngine.Video.Tests' HardwareGate contract
' (F-01/C-4): a test that REQUIRES real hardware throws this instead of
' failing, the runner counts it as SKIP (never PASS, never a swallowed
' failure), and it never affects the exit code.

Imports System

Namespace CaptureEngine.Encoder.Tests

    ''' <summary>Thrown by a test to report ENVIRONMENT NOT CAPABLE.</summary>
    Friend Class SkipException
        Inherits Exception

        Public Sub New(reason As String)
            MyBase.New(reason)
        End Sub
    End Class

End Namespace
