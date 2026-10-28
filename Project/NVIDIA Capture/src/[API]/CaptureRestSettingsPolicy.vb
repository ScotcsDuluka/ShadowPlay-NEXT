Option Strict On
Option Explicit On
Option Infer On
Imports System
Imports System.Collections.Generic
Imports System.Globalization

' Validation of explicit REST-only overrides; does not select a backend or
' change canonical saved configuration. Unknown resolution must not become native.
Public NotInheritable Class CaptureRestSettingsPolicy
    Private Sub New()
    End Sub

    Public Shared Function Valid(settings As IReadOnlyDictionary(Of String, String)) As Boolean
        If settings Is Nothing Then Return False
        Dim fps As String = "", bitrate As String = "", resolution As String = "", quality As String = ""
        If Not settings.TryGetValue("framerate", fps) OrElse Not settings.TryGetValue("bitrateBps", bitrate) OrElse
           Not settings.TryGetValue("resolution", resolution) OrElse Not settings.TryGetValue("quality", quality) Then Return False
        Dim parsedFps As Integer, parsedBitrate As Integer
        If Not Integer.TryParse(fps, NumberStyles.None, CultureInfo.InvariantCulture, parsedFps) OrElse
           parsedFps < 1 OrElse parsedFps > 240 OrElse
           Not Integer.TryParse(bitrate, NumberStyles.None, CultureInfo.InvariantCulture, parsedBitrate) OrElse
           parsedBitrate < 1 Then Return False
        If String.IsNullOrWhiteSpace(quality) OrElse quality.Length > 64 Then Return False
        For Each character In quality
            If Char.IsControl(character) Then Return False
        Next
        Select Case resolution
            Case "In-game", "2160p 4K", "1440p HD", "1080p HD", "720p HD", "480p", "360p", "240p", "Custom"
                Return True
            Case Else
                Return False
        End Select
    End Function
End Class
