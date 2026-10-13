Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Frame dimensions in pixels. (P1-A v1.3.1 §3.5)
    ''' </summary>
    Public Structure VideoFrameDimensions
        Public ReadOnly Property Width As Integer
        Public ReadOnly Property Height As Integer

        Public Sub New(width As Integer, height As Integer)
            If width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(width), "Width must be positive.")
            If height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(height), "Height must be positive.")
            Me.Width = width
            Me.Height = height
        End Sub
    End Structure
End Namespace
