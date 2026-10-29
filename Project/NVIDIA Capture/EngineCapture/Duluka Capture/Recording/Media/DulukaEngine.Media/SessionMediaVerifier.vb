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
        Public Property VerificationPending As Boolean
        Public Property PacketTimelineVerified As Boolean
    End Class
    Public NotInheritable Class SessionMediaVerifier
        ' AAC frame padding and small encoder priming are permitted only if
        ' every requested audio stream has measured start/end timestamps.
        Private Const MaxNativeAudioSkewMs As Double = 200.0R
        Public Shared Function Verify(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, Optional processStarted As Action(Of Process) = Nothing) As SessionMediaEvidence
            Return VerifyWithColor(path, ffmpeg, frames, width, height, fps, processStarted, Nothing)
        End Function
        Public Shared Function VerifyWithColor(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, processStarted As Action(Of Process),
                                      expectedColor As DulukaEngine.Video.VideoColorDescriptor) As SessionMediaEvidence
            Return VerifyCore(path, ffmpeg, frames, width, height, fps, processStarted, expectedColor, 0, True)
        End Function
        Public Shared Function VerifyNative(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, audioStreams As Integer,
                                      Optional processStarted As Action(Of Process) = Nothing) As SessionMediaEvidence
            Return VerifyCore(path, ffmpeg, frames, width, height, fps, processStarted, Nothing, audioStreams, False)
        End Function
        Private Shared Function VerifyCore(path As String, ffmpeg As String, frames As Long, width As Integer, height As Integer,
                                      fps As Integer, processStarted As Action(Of Process),
                                      expectedColor As DulukaEngine.Video.VideoColorDescriptor, audioStreams As Integer,
                                      strictCfr As Boolean) As SessionMediaEvidence
            Dim result As New SessionMediaEvidence()
            Try
                If frames <= 0 OrElse (strictCfr AndAlso frames > 3600) OrElse fps <= 0 OrElse width <= 0 OrElse height <= 0 Then
                    Throw New InvalidDataException("session_expected_frame_budget_required")
                End If
                Dim probe = IO.Path.Combine(IO.Path.GetDirectoryName(IO.Path.GetFullPath(ffmpeg)), "ffprobe.exe")
                Dim deep = Global.CaptureMediaInspector.InspectWithVideoDecode(path, probe, ffmpeg)
                If Not deep.Verified OrElse Not deep.VideoDecodeVerified Then
                    If deep.Reason = "ffprobe_timeout" OrElse deep.Reason = "ffmpeg_decode_timeout" Then
                        result.VerificationPending = True
                        result.Reason = "session_verification_pending:" & deep.Reason
                        Return result
                    End If
                    Throw New InvalidDataException("session_full_decode_failed:" & deep.Reason)
                End If
                If deep.VideoStreams <> 1 OrElse deep.AudioStreams <> audioStreams Then
                    Throw New InvalidDataException("session_requested_stream_count_mismatch")
                End If
                If audioStreams > 0 Then
                    If Not deep.AudioTimingComplete OrElse Not deep.FirstAudioOffsetMs.HasValue OrElse
                        Not deep.MaxAudioEndDriftMs.HasValue Then
                        Throw New InvalidDataException("session_audio_timing_evidence_missing")
                    End If
                    If Math.Abs(deep.FirstAudioOffsetMs.Value) > MaxNativeAudioSkewMs OrElse
                        deep.MaxAudioEndDriftMs.Value > MaxNativeAudioSkewMs Then
                        Throw New InvalidDataException("session_audio_timing_mismatch")
                    End If
                End If
                Dim psi As New ProcessStartInfo(probe) With {.UseShellExecute = False, .CreateNoWindow = True,
                    .RedirectStandardOutput = True, .RedirectStandardError = True}
                ' Long native recordings need complete aggregate frame/packet
                ' counts, not a JSON object per packet retained in memory.
                Dim scanTimeline = strictCfr OrElse frames <= 3600
                For Each arg In {"-v", "error", "-select_streams", "v:0", "-count_frames", "-count_packets", "-show_streams", "-show_format",
                    "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,nb_read_frames,nb_read_packets,nb_frames,time_base,color_range,color_space,color_transfer,color_primaries:format=duration" &
                        If(scanTimeline, ":packet=pts,dts,duration", ""),
                    "-of", "json", path}
                    psi.ArgumentList.Add(arg)
                Next
                If scanTimeline Then psi.ArgumentList.Insert(0, "-show_packets")
                Using child = Process.Start(psi)
                    Try : processStarted?.Invoke(child) : Catch
                        ' This verifier remains the owner even if the host's
                        ' optional job-registration hook fails.
                    End Try
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
                            Not CadenceMatches(video, fps, strictCfr) Then
                            Throw New InvalidDataException("session_video_descriptor_mismatch")
                        End If
                        result.DecodedFrames = Long.Parse(video.GetProperty("nb_read_frames").GetString(), Globalization.CultureInfo.InvariantCulture)
                        result.Packets = Long.Parse(video.GetProperty("nb_read_packets").GetString(), Globalization.CultureInfo.InvariantCulture)
                        Dim declared = Long.Parse(video.GetProperty("nb_frames").GetString(), Globalization.CultureInfo.InvariantCulture)
                        If declared <> frames OrElse result.DecodedFrames <> frames OrElse result.Packets <> frames Then
                            Throw New InvalidDataException("session_frame_packet_count_mismatch")
                        End If
                        If scanTimeline Then
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
                            If packetDuration <= 0 OrElse (strictCfr AndAlso (pts <> dts OrElse
                                CDec(pts) * numerator * fps <> CDec(i) * denominator OrElse
                                CDec(packetDuration) * numerator * fps <> denominator)) Then
                                Throw New InvalidDataException("session_mp4_pts_dts_or_duration_mismatch")
                            End If
                            If i > 0 AndAlso dts <= packets(i - 1).GetProperty("dts").GetInt64() Then
                                Throw New InvalidDataException("session_mp4_dts_not_monotonic")
                            End If
                        Next
                        result.PacketTimelineVerified = True
                        End If
                        Dim duration = Double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(), Globalization.CultureInfo.InvariantCulture)
                        ' format.duration is the longest track. For audio+video,
                        ' validate each audio track above, then allow bounded AAC padding.
                        ' Video-only/strict-CFR acceptance keeps the one-frame limit.
                        Dim durationTolerance As Double = If(audioStreams > 0 AndAlso Not strictCfr,
                            MaxNativeAudioSkewMs / 1000.0R, 1.0R / fps)
                        If Not Double.IsFinite(duration) OrElse duration <= 0 OrElse
                            Math.Abs(duration - CDbl(frames) / fps) > durationTolerance Then Throw New InvalidDataException("session_media_duration_mismatch")
                    End Using
                End Using
                result.Verified = True
            Catch ex As Exception
                result.Reason = ex.Message
                result.VerificationPending = TypeOf ex Is TimeoutException
            End Try
            Return result
        End Function
        Private Shared Function CadenceMatches(video As JsonElement, fps As Integer, strictCfr As Boolean) As Boolean
            Dim rate = video.GetProperty("avg_frame_rate").GetString()
            If strictCfr Then Return rate = fps.ToString(Globalization.CultureInfo.InvariantCulture) & "/1"
            Dim average = rate.Split("/"c)
            Dim timeBase = video.GetProperty("time_base").GetString().Split("/"c)
            If average.Length <> 2 OrElse timeBase.Length <> 2 Then Return False
            Dim rateNumerator = Decimal.Parse(average(0), Globalization.CultureInfo.InvariantCulture)
            Dim rateDenominator = Decimal.Parse(average(1), Globalization.CultureInfo.InvariantCulture)
            Dim tickNumerator = Decimal.Parse(timeBase(0), Globalization.CultureInfo.InvariantCulture)
            Dim tickDenominator = Decimal.Parse(timeBase(1), Globalization.CultureInfo.InvariantCulture)
            If rateNumerator <= 0 OrElse rateDenominator <= 0 OrElse tickNumerator <= 0 OrElse tickDenominator <= 0 Then Return False
            ' Native raw-H264 remux can round each sample's duration to the
            ' container timebase. Permit at most ONE such tick per frame;
            ' submitted/decoded counts and total duration remain exact gates.
            Return Math.Abs(rateDenominator * fps - rateNumerator) * tickDenominator <=
                rateNumerator * fps * tickNumerator
        End Function
    End Class
End Namespace
