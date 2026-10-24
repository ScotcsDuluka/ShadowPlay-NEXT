Imports System
Imports System.IO

Module Program
    Private _passed As Integer

    Private Sub Check(name As String, ok As Boolean)
        If Not ok Then Throw New Exception("FAIL: " & name)
        _passed += 1
        Console.WriteLine("PASS: " & name)
    End Sub

    Private Const TwoAudio As String =
        "{""format"":{""duration"":""3.025000""},""streams"":[" &
        "{""codec_type"":""video"",""codec_name"":""h264"",""start_time"":""0.000000"",""duration"":""3.000000""}," &
        "{""codec_type"":""audio"",""codec_name"":""aac"",""start_time"":""0.021000"",""duration"":""2.980000"",""channels"":2}," &
        "{""codec_type"":""audio"",""codec_name"":""aac"",""start_time"":""0.221000"",""duration"":""2.780000"",""channels"":1}]}"

    Sub Main(args As String())
        Dim parsed = CaptureMediaInspector.ParseProbeJson(TwoAudio)
        Check("Two audio streams detected", parsed.Verified AndAlso parsed.AudioStreams = 2)
        Check("Audio codecs reported", parsed.AudioCodecs.Length = 2 AndAlso
              parsed.AudioCodecs(0) = "aac" AndAlso parsed.AudioCodecs(1) = "aac")
        Check("Audio channel count", parsed.AudioChannels(0) = 2 AndAlso parsed.AudioChannels(1) = 1)
        Check("First audio start offset 21ms", parsed.FirstAudioOffsetMs.HasValue AndAlso
              Math.Abs(parsed.FirstAudioOffsetMs.Value - 21.0) < 0.01)
        Dim primed = CaptureMediaInspector.ParseProbeJson(
            "{""format"":{""duration"":""2""},""streams"":[" &
            "{""codec_type"":""video"",""start_time"":""0.000"",""duration"":""2.000""}," &
            "{""codec_type"":""audio"",""start_time"":""-0.021"",""duration"":""2.000""}]}")
        Check("AAC negative priming timestamp preserved",
              primed.Verified AndAlso primed.FirstAudioOffsetMs.HasValue AndAlso
              Math.Abs(primed.FirstAudioOffsetMs.Value + 21.0) < 0.01)
        Check("End drift measured, not invented", parsed.MaxAudioEndDriftMs.HasValue AndAlso
              Math.Abs(parsed.MaxAudioEndDriftMs.Value - 1.0) < 0.01)
        Check("Duration from container", parsed.DurationSec.HasValue AndAlso
              Math.Abs(parsed.DurationSec.Value - 3.025) < 0.001)
        Check("Sync labeled timestamp-only", parsed.SyncBasis = "container_timestamps_only")

        Dim mute = CaptureMediaInspector.ParseProbeJson(
            "{""streams"":[{""codec_type"":""video"",""start_time"":""0.0"",""duration"":""2.0""}],""format"":{""duration"":""2.0""}}")
        Check("Silent MP4 is video-only, not audio", mute.Verified AndAlso
              mute.AudioStreams = 0 AndAlso Not mute.FirstAudioOffsetMs.HasValue)
        Dim broken = CaptureMediaInspector.ParseProbeJson("{""streams"":[], ""format"":{""duration"":""2""}}")
        Check("No video stream refuses verified", Not broken.Verified)
        Dim missing = CaptureMediaInspector.ParseProbeJson("{""format"":{""duration"":""2""}}")
        Check("Missing stream table refuses verified", Not missing.Verified)
        Dim malformed = CaptureMediaInspector.ParseProbeJson("invalid")
        Check("Broken JSON refuses verified", Not malformed.Verified)
        Dim unknown = CaptureMediaInspector.ParseProbeJson(
            "{""streams"":[{""codec_type"":""video"",""start_time"":""N/A""}]," &
            """format"":{""duration"":""N/A""}}")
        Check("Unknown FFprobe durations are not fabricated", Not unknown.Verified AndAlso
              Not unknown.DurationSec.HasValue)
        ' Phase 6E: a faststart MP4 keeps its moov-advertised duration
        ' even after most mdat packets are missing from disk.
        Dim completePackets = "{""format"":{""duration"":""3.0""},""streams"":[" &
            "{""codec_type"":""video"",""start_time"":""0"",""duration"":""3.0""," &
            """nb_frames"":""90"",""nb_read_packets"":""90""}]}"
        Dim readAll = CaptureMediaInspector.ParseProbeJson(
            completePackets, requirePacketValidation:=True)
        Check("Complete video packet count accepted",
              readAll.Verified AndAlso readAll.PacketCountChecked AndAlso
              readAll.VideoFramesDeclared.HasValue AndAlso
              readAll.VideoPacketsRead.HasValue AndAlso
              readAll.VideoFramesDeclared.Value = 90 AndAlso
              readAll.VideoPacketsRead.Value = 90)
        Dim truncatedPackets = completePackets.Replace(
            """nb_read_packets"":""90""", """nb_read_packets"":""10""")
        Dim insufficient = CaptureMediaInspector.ParseProbeJson(
            truncatedPackets, requirePacketValidation:=True)
        Check("Truncated video packet count rejects metadata-only success",
              Not insufficient.Verified AndAlso insufficient.Reason = "video_packet_count_mismatch")
        Dim unknownFrames = completePackets.Replace(
            """nb_frames"":""90""", """nb_frames"":""N/A""")
        Dim unavailable = CaptureMediaInspector.ParseProbeJson(
            unknownFrames, requirePacketValidation:=True)
        Check("Unknown declared frame total refuses false verification",
              Not unavailable.Verified AndAlso unavailable.Reason = "video_frame_total_unavailable")
        Dim missingCount = completePackets.Replace(
            """nb_read_packets"":""90""", """nb_read_packets"":""N/A""")
        Dim missingPackets = CaptureMediaInspector.ParseProbeJson(
            missingCount, requirePacketValidation:=True)
        Check("Missing actual packet count refuses false verification",
              Not missingPackets.Verified AndAlso missingPackets.Reason = "video_packet_scan_unavailable")

        Dim noPath = CaptureMediaInspector.Inspect("C:\no-media-exists-" &
            Guid.NewGuid().ToString() & ".mp4", "")
        Check("Missing MP4 path refuses verified", Not noPath.Verified AndAlso
              noPath.Reason = "media_file_missing")

        If args.Length >= 2 Then
            For i As Integer = 1 To args.Length - 1
                Dim path = args(i)
                Dim result = CaptureMediaInspector.Inspect(path, args(0))
                Check("Real FFprobe verifies " & IO.Path.GetFileName(path),
                      result.Verified AndAlso result.VideoStreams = 1)
                If IO.Path.GetFileName(path).StartsWith("single", StringComparison.OrdinalIgnoreCase) Then
                    Check("Synthetic single track", result.AudioStreams = 1)
                ElseIf IO.Path.GetFileName(path).StartsWith("dual", StringComparison.OrdinalIgnoreCase) Then
                    Check("Synthetic dual tracks", result.AudioStreams = 2)
                    Check("Synthetic A/V start within 80ms",
                          result.FirstAudioOffsetMs.HasValue AndAlso
                          Math.Abs(result.FirstAudioOffsetMs.Value) < 80)
                ElseIf IO.Path.GetFileName(path).StartsWith("mute", StringComparison.OrdinalIgnoreCase) Then
                    Check("Synthetic mute has no audio stream", result.AudioStreams = 0)
                End If
            Next
        End If
        Console.WriteLine("PASS: " & _passed & " media-evidence tests")
    End Sub
End Module
