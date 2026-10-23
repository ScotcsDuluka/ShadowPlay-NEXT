Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading.Tasks

' Read-only evidence about the finalized media file. A file existing is NOT
' evidence that system audio, microphone audio or A/V sync were captured.
' Container timestamps are only a sync estimate, not an acoustic measurement.
Public NotInheritable Class CaptureMediaInspector
    Private Sub New()
    End Sub

    Public NotInheritable Class Evidence
        Public Property Verified As Boolean
        Public Property Reason As String = ""
        Public Property VideoStreams As Integer
        Public Property AudioStreams As Integer
        Public Property DurationSec As Double?
        Public Property VideoStartSec As Double?
        Public Property FirstAudioStartSec As Double?
        Public Property FirstAudioOffsetMs As Double?
        Public Property MaxAudioEndDriftMs As Double?
        Public Property AudioCodecs As String() = Array.Empty(Of String)()
        Public Property AudioChannels As Integer() = Array.Empty(Of Integer)()
        Public Property SyncBasis As String = "container_timestamps_only"
    End Class

    Public Shared Function ParseProbeJson(body As String) As Evidence
        Dim data As New Evidence()
        Try
            Using doc = JsonDocument.Parse(body)
                Dim root = doc.RootElement
                Dim streams As JsonElement
                If root.ValueKind <> JsonValueKind.Object OrElse
                   Not root.TryGetProperty("streams", streams) OrElse
                   streams.ValueKind <> JsonValueKind.Array Then
                    data.Reason = "streams_missing"
                    Return data
                End If

                Dim startVideo As Double? = Nothing
                Dim videoEnd As Double? = Nothing
                Dim firstAudio As Double? = Nothing
                Dim audioEnds As New System.Collections.Generic.List(Of Double)()
                Dim codecs As New System.Collections.Generic.List(Of String)()
                Dim channels As New System.Collections.Generic.List(Of Integer)()

                For Each stream As JsonElement In streams.EnumerateArray()
                    If stream.ValueKind <> JsonValueKind.Object Then Continue For
                    Dim kind = GetText(stream, "codec_type")
                    Dim startAt = GetNumber(stream, "start_time")
                    Dim duration = GetNumber(stream, "duration")
                    If kind = "video" Then
                        data.VideoStreams += 1
                        If startAt.HasValue Then
                            If Not startVideo.HasValue OrElse startAt.Value < startVideo.Value Then
                                startVideo = startAt.Value
                            End If
                            If duration.HasValue Then
                                Dim ending = startAt.Value + duration.Value
                                If Not videoEnd.HasValue OrElse ending > videoEnd.Value Then videoEnd = ending
                            End If
                        End If
                    ElseIf kind = "audio" Then
                        data.AudioStreams += 1
                        codecs.Add(GetText(stream, "codec_name"))
                        Dim count As Integer
                        If Not Integer.TryParse(GetText(stream, "channels"), count) Then count = 0
                        channels.Add(count)
                        If startAt.HasValue Then
                            If Not firstAudio.HasValue OrElse startAt.Value < firstAudio.Value Then
                                firstAudio = startAt.Value
                            End If
                            If duration.HasValue Then audioEnds.Add(startAt.Value + duration.Value)
                        End If
                    End If
                Next

                data.AudioCodecs = codecs.ToArray()
                data.AudioChannels = channels.ToArray()
                data.VideoStartSec = startVideo
                data.FirstAudioStartSec = firstAudio
                If startVideo.HasValue AndAlso firstAudio.HasValue Then
                    data.FirstAudioOffsetMs = Math.Round(
                        (firstAudio.Value - startVideo.Value) * 1000.0, 3)
                End If

                Dim container As JsonElement
                If root.TryGetProperty("format", container) AndAlso
                   container.ValueKind = JsonValueKind.Object Then
                    data.DurationSec = GetNumber(container, "duration")
                End If
                If videoEnd.HasValue AndAlso audioEnds.Count > 0 Then
                    data.MaxAudioEndDriftMs = Math.Round(
                        audioEnds.Max(Function(v) Math.Abs(v - videoEnd.Value)) * 1000.0, 3)
                End If
                data.Verified = data.VideoStreams > 0 AndAlso
                    data.DurationSec.HasValue AndAlso data.DurationSec.Value > 0
                data.Reason = If(data.Verified, "", "video_or_duration_missing")
            End Using
        Catch ex As Exception
            data.Reason = "ffprobe_json_invalid_" & ex.GetType().Name
        End Try
        Return data
    End Function

    Private Shared Function GetText(obj As JsonElement, name As String) As String
        Dim element As JsonElement
        If obj.ValueKind <> JsonValueKind.Object OrElse Not obj.TryGetProperty(name, element) Then
            Return ""
        End If
        If element.ValueKind = JsonValueKind.String Then Return If(element.GetString(), "")
        If element.ValueKind = JsonValueKind.Number Then Return element.GetRawText()
        Return ""
    End Function

    Private Shared Function GetNumber(obj As JsonElement, name As String) As Double?
        Dim v As Double
        If Double.TryParse(GetText(obj, name), NumberStyles.Float,
                           CultureInfo.InvariantCulture, v) AndAlso
           Not Double.IsNaN(v) AndAlso Not Double.IsInfinity(v) AndAlso
           (name <> "duration" OrElse v >= 0) Then
            ' AAC priming can report a negative start timestamp. Preserve it
            ' rather than silently replacing an early audio start with Nothing.
            Return v
        End If
        Return Nothing
    End Function

    ' ffprobe is run on a finalized MP4 only, never on the live mux output.
    ' All process output is bounded and both streams are drained concurrently.
    Public Shared Function Inspect(filePath As String, ffprobePath As String) As Evidence
        If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then
            Return New Evidence With {.Reason = "media_file_missing"}
        End If
        If String.IsNullOrWhiteSpace(ffprobePath) OrElse Not File.Exists(ffprobePath) Then
            Return New Evidence With {.Reason = "ffprobe_missing"}
        End If
        Try
            Dim info As New FileInfo(filePath)
            If info.Length < 128 Then Return New Evidence With {.Reason = "media_file_too_small"}
            Dim psi As New ProcessStartInfo(ffprobePath) With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True
            }
            For Each arg In {"-v", "error", "-show_entries",
                             "format=duration:stream=codec_type,codec_name,start_time,duration,channels",
                             "-of", "json", filePath}
                psi.ArgumentList.Add(arg)
            Next
            Using proc = Process.Start(psi)
                If proc Is Nothing Then Return New Evidence With {.Reason = "ffprobe_spawn_failed"}
                Dim output As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
                Dim errors As Task(Of String) = proc.StandardError.ReadToEndAsync()
                If Not proc.WaitForExit(8000) Then
                    Try : proc.Kill(True) : Catch : End Try
                    Return New Evidence With {.Reason = "ffprobe_timeout"}
                End If
                If proc.ExitCode <> 0 Then
                    Return New Evidence With {.Reason = "ffprobe_nonzero_exit"}
                End If
                Dim json = output.GetAwaiter().GetResult()
                errors.GetAwaiter().GetResult()
                If json.Length > 65536 Then Return New Evidence With {.Reason = "ffprobe_output_oversize"}
                Return ParseProbeJson(json)
            End Using
        Catch ex As Exception
            Return New Evidence With {.Reason = "ffprobe_error_" & ex.GetType().Name}
        End Try
    End Function
End Class
