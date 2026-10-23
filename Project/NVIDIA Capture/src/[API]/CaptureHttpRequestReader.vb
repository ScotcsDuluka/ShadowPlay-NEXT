Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text

' HTTP/1.1 parser for the loopback Capture REST worker.
' Standalone so split-packet and oversized-request regressions can be tested
' without opening NvCapture's production port or starting its GPU runtime.
Public NotInheritable Class CaptureHttpRequestReader
    Private Sub New()
    End Sub

    Public Shared Function Read(stream As Stream) As Dictionary(Of String, String)
        Try
            Const MaxRequestBytes As Integer = 65536
            Dim buffer(8191) As Byte
            Using bytes As New MemoryStream()
                Dim headerEnd As Integer = -1
                While headerEnd < 0 AndAlso bytes.Length < MaxRequestBytes
                    Dim count = stream.Read(buffer, 0,
                        Math.Min(buffer.Length, MaxRequestBytes - CInt(bytes.Length)))
                    If count <= 0 Then Return Nothing
                    bytes.Write(buffer, 0, count)
                    Dim current = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, CInt(bytes.Length))
                    headerEnd = current.IndexOf(vbCr & vbLf & vbCr & vbLf, StringComparison.Ordinal)
                End While
                If headerEnd < 0 Then Return Nothing
                Dim currentHeader = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, headerEnd)
                Dim lines = currentHeader.Split({vbCr & vbLf}, StringSplitOptions.None)
                If lines.Length = 0 Then Return Nothing
                Dim reqLine = lines(0).Split(" "c)
                If reqLine.Length < 2 Then Return Nothing
                Dim bodyLength As Integer = 0
                For Each ln In lines
                    If ln.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase) Then
                        If Not Integer.TryParse(ln.Substring(15).Trim(), bodyLength) Then Return Nothing
                    End If
                Next
                Dim bodyStart = headerEnd + 4
                If bodyLength < 0 OrElse bodyLength > MaxRequestBytes - bodyStart Then Return Nothing
                Dim needed = bodyStart + bodyLength
                While bytes.Length < needed
                    Dim count = stream.Read(buffer, 0,
                        Math.Min(buffer.Length, needed - CInt(bytes.Length)))
                    If count <= 0 Then Return Nothing
                    bytes.Write(buffer, 0, count)
                End While
                Return New Dictionary(Of String, String) From {
                    {"method", reqLine(0)},
                    {"path", reqLine(1)},
                    {"body", Encoding.UTF8.GetString(bytes.GetBuffer(), bodyStart, bodyLength)}
                }
            End Using
        Catch
            Return Nothing
        End Try
    End Function
End Class
