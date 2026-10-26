Option Strict On
Imports System.Diagnostics
Imports System.IO
Imports System.Text.Json

Namespace DulukaEngine.Media
    Public NotInheritable Class SessionMediaEvidence
        Public Property Verified As Boolean
        Public Property Reason As String = ""
        Public Property DecodedFrames As Long
        Public Property Packets As Long
    End Class
    Public NotInheritable Class SessionMediaVerifier
        Public Shared Function Verify(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, Optional processStarted As Action(Of Process) = Nothing) As SessionMediaEvidence
            Return VerifyWithColor(path, ffmpeg, frames, width, height, fps, processStarted, Nothing)
        End Function
        Public Shared Function VerifyWithColor(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, processStarted As Action(Of Process),
                                      expectedColor As DulukaEngine.Video.VideoColorDescriptor) As SessionMediaEvidence
            Dim result As New SessionMediaEvidence()
            Try
                If frames <= 0 OrElse fps <= 0 Then Throw New InvalidDataException("session_expected_frame_budget_required")
                Dim probe = IO.Path.Combine(IO.Path.GetDirectoryName(IO.Path.GetFullPath(ffmpeg)), "ffprobe.exe")
                Dim deep = Global.CaptureMediaInspector.InspectWithVideoDecode(path, probe, ffmpeg)
                If Not deep.Verified OrElse Not deep.VideoDecodeVerified Then Throw New InvalidDataException("session_full_decode_failed:" & deep.Reason)
                Dim psi As New ProcessStartInfo(probe) With {.UseShellExecute = False, .CreateNoWindow = True,
                    .RedirectStandardOutput = True, .RedirectStandardError = True}
                For Each arg In {"-v", "error", "-count_frames", "-count_packets", "-show_streams", "-show_format", "-show_packets", "-of", "json", path}
                    psi.ArgumentList.Add(arg)
                Next
                Using child = Process.Start(psi)
                    processStarted?.Invoke(child)
                    Dim output = child.StandardOutput.ReadToEndAsync()
                    Dim errors = child.StandardError.ReadToEndAsync()
                    If Not child.WaitForExit(30000) Then
                        child.Kill(True) : child.WaitForExit(2000)
                        Throw New TimeoutException("session_frame_scan_timeout")
                    End If
                    Dim body = output.GetAwaiter().GetResult()
                    If child.ExitCode <> 0 OrElse body.Length > 4 * 1024 * 1024 OrElse errors.GetAwaiter().GetResult().Length > 0 Then
                        Throw New InvalidDataException("session_frame_scan_failed")
                    End If
                    Using doc = JsonDocument.Parse(body)
                        Dim streams = doc.RootElement.GetProperty("streams")
                        If streams.GetArrayLength() <> 1 Then Throw New InvalidDataException("session_video_only_stream_count_mismatch")
                        Dim video = streams(0)
                        If expectedColor IsNot Nothing Then
                            If video.GetProperty("color_range").GetString() <> If(expectedColor.FullRange, "pc", "tv") OrElse
                                video.GetProperty("color_space").GetString() <> "bt709" OrElse
                                video.GetProperty("color_transfer").GetString() <> "bt709" OrElse
                                video.GetProperty("color_primaries").GetString() <> "bt709" Then
                                Throw New InvalidDataException("session_colorimetry_mismatch")
                            End If
                        End If
                        If video.GetProperty("codec_type").GetString() <> "video" OrElse video.GetProperty("codec_name").GetString() <> "h264" OrElse
                            video.GetProperty("width").GetInt32() <> width OrElse video.GetProperty("height").GetInt32() <> height OrElse
                            video.GetProperty("avg_frame_rate").GetString() <> fps.ToString(Globalization.CultureInfo.InvariantCulture) & "/1" Then
                            Throw New InvalidDataException("session_video_descriptor_mismatch")
                        End If
                        result.DecodedFrames = Long.Parse(video.GetProperty("nb_read_frames").GetString(), Globalization.CultureInfo.InvariantCulture)
                        result.Packets = Long.Parse(video.GetProperty("nb_read_packets").GetString(), Globalization.CultureInfo.InvariantCulture)
                        Dim declared = Long.Parse(video.GetProperty("nb_frames").GetString(), Globalization.CultureInfo.InvariantCulture)
                        If declared <> frames OrElse result.DecodedFrames <> frames OrElse result.Packets <> frames Then
                            Throw New InvalidDataException("session_frame_packet_count_mismatch")
                        End If
                        Dim timeBase = video.GetProperty("time_base").GetString().Split("/"c)
                        Dim numerator = Decimal.Parse(timeBase(0), Globalization.CultureInfo.InvariantCulture)
                        Dim denominator = Decimal.Parse(timeBase(1), Globalization.CultureInfo.InvariantCulture)
                        Dim packets = doc.RootElement.GetProperty("packets")
                        If packets.GetArrayLength() <> frames OrElse numerator <= 0 OrElse denominator <= 0 Then
                            Throw New InvalidDataException("session_packet_timeline_missing")
                        End If
                        For i = 0 To packets.GetArrayLength() - 1
                            Dim packet = packets(i)
                            Dim pts = packet.GetProperty("pts").GetInt64()
                            Dim dts = packet.GetProperty("dts").GetInt64()
                            Dim packetDuration = packet.GetProperty("duration").GetInt64()
                            If pts <> dts OrElse CDec(pts) * numerator * fps <> CDec(i) * denominator OrElse
                                CDec(packetDuration) * numerator * fps <> denominator Then
                                Throw New InvalidDataException("session_mp4_pts_dts_or_duration_mismatch")
                            End If
                        Next
                        Dim duration = Double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(), Globalization.CultureInfo.InvariantCulture)
                        If Math.Abs(duration - CDbl(frames) / fps) > 1.0 / fps Then Throw New InvalidDataException("session_media_duration_mismatch")
                    End Using
                End Using
                result.Verified = True
            Catch ex As Exception
                result.Reason = ex.Message
            End Try
            Return result
        End Function
    End Class
End Namespace
