Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.IO
Imports System.Text
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.FFmpeg
    ''' <summary>
    ''' V2 FFmpeg command builder — reads from EngineConfigV2 (new unified schema).
    '''
    ''' Fixes applied vs V1 (FFmpegCommandBuilderV1):
    '''   1. BufsizeBps uses the CORRECT value (BitrateBps * 2) instead of V1's
    '''      buggy 1× bitrate (V1 declared `buf = bitrate * 2` but used `br`).
    '''   2. Preset is read directly from V2 config (string "p4") — no duplicate
    '''      integer source. V1 had both NvencPreset (int) AND Preset (string).
    '''   3. PixelFormat is ALWAYS appended for NVENC + HW capture (V1 skipped it
    '''      for ddagrab+NVENC, relying on NVENC's default which may not match
    '''      the configured value).
    '''   4. RateControl is read from config (V1 hardcoded "cbr" for NVIDIA).
    '''   5. Tune is read from config (V1 hardcoded "ll" for NVIDIA).
    '''   6. SpatialAQ / TemporalAQ are read from config (V1 hardcoded 1).
    '''   7. LookAhead is appended when > 0 (V1 never appended it for NVIDIA).
    '''   8. ZeroLatency is appended when True (V1 never appended it).
    '''   9. CQ is appended when RateControl=cq (V1 had no CQ support).
    '''  10. Profile is appended when non-empty (V1 never appended -profile:v).
    '''  11. GOP is read from config (V1 hardcoded -g <FPS>).
    '''
    ''' PARITY GUARANTEE:
    '''   When V2 config is migrated from V1 via ConfigMigrator.MigrateFromV1,
    '''   the resulting V2 builder output will DIFFER from V1 in exactly:
    '''     - bufsize (V2 = 2× bitrate, V1 = 1× bitrate) — intentional fix
    '''     - -pix_fmt nv12 (V2 appends for ddagrab+NVENC; V1 does not)
    '''   All other arguments are byte-identical.
    '''
    '''   Tests in Phase 6 verify this delta explicitly.
    ''' </summary>
    Public NotInheritable Class FFmpegCommandBuilderV2
        Implements IFFmpegCommandBuilder

        Private ReadOnly _cfg As EngineConfigV2

        Public Sub New(cfg As EngineConfigV2)
            If cfg Is Nothing Then Throw New ArgumentNullException(NameOf(cfg))
            _cfg = cfg
        End Sub

        Public ReadOnly Property BuilderLabel As String Implements IFFmpegCommandBuilder.BuilderLabel
            Get
                Return "V2 (EngineConfig v2 schema)"
            End Get
        End Property

        Public Function Build(outputFile As String) As String Implements IFFmpegCommandBuilder.Build
            If String.IsNullOrEmpty(outputFile) Then
                Throw New ArgumentException("outputFile is empty.", NameOf(outputFile))
            End If

            Dim sb As New StringBuilder()
            Dim v As EngineConfigV2.VideoSection = _cfg.Video
            Dim enc As EngineConfigV2.EncoderSubSection = v.Encoder

            Dim fpsStr As String = v.Capture.Framerate.ToString()
            Dim br As String = enc.BitrateBps.ToString()
            Dim minrateStr As String = enc.MinrateBps.ToString()
            Dim maxrateStr As String = enc.MaxrateBps.ToString()
            Dim bufStr As String = enc.BufsizeBps.ToString()
            Dim gopStr As String = enc.GopSize.ToString()
            Dim hwType As HwDeviceType = DetectHwDeviceType(enc.FFmpegCodec)

            sb.Append("-hide_banner -loglevel info ")

            Dim videoFilter As String = ""

            ' ── Capture input ──
            Select Case v.Capture.Method.ToLowerInvariant()
                Case "ddagrab"
                    sb.Append("-f lavfi -i ""ddagrab=output_idx=" & v.Capture.OutputIndex.ToString() &
                              ":framerate=" & fpsStr & """ ")
                    Select Case hwType
                        Case HwDeviceType.IntelQSV
                            ' hwmap=derive_device=qsv fails on Intel iGPU (MFX session -9
                            ' from the ddagrab-derived D3D11 device); system-memory qsv
                            ' encode works — mirror the Engine FFmpegArgumentBuilder fix.
                            videoFilter = "hwdownload,format=bgra,format=nv12"
                        Case HwDeviceType.None
                            videoFilter = "hwdownload,format=bgra,format=yuv420p"
                        Case HwDeviceType.NVIDIA, HwDeviceType.AMD
                            videoFilter = ""
                    End Select

                Case "gdigrab"
                    sb.Append("-f gdigrab -framerate " & fpsStr & " -i desktop ")
                    videoFilter = ""

                Case "gfxcapture"
                    sb.Append("-f lavfi -i ""gfxcapture=monitor_idx=" & v.Capture.OutputIndex.ToString() &
                              ":max_framerate=" & fpsStr & """ ")
                    Select Case hwType
                        Case HwDeviceType.IntelQSV
                            ' Same IntelQSV hwmap failure as ddagrab (MFX session -9).
                            videoFilter = "fps=" & fpsStr & ",hwdownload,format=bgra,format=nv12"
                        Case HwDeviceType.None
                            videoFilter = "fps=" & fpsStr & ",hwdownload,format=bgra,format=yuv420p"
                        Case HwDeviceType.NVIDIA, HwDeviceType.AMD
                            videoFilter = "fps=" & fpsStr
                    End Select

                Case Else
                    Throw New InvalidOperationException(
                        "Unknown Video.Capture.Method: '" & v.Capture.Method & "'. " &
                        "Use ConfigValidator before calling Build().")
            End Select

            ' ── Custom resolution (scale filter) ──
            If v.Resolution.Mode = "custom" AndAlso v.Resolution.Width > 0 AndAlso v.Resolution.Height > 0 Then
                Dim isHw As Boolean = (v.Capture.Method.ToLowerInvariant() = "ddagrab" OrElse
                                        v.Capture.Method.ToLowerInvariant() = "gfxcapture")
                Dim scalePart As String = "scale=" & v.Resolution.Width.ToString() & ":" & v.Resolution.Height.ToString()

                If isHw Then
                    If hwType = HwDeviceType.NVIDIA OrElse hwType = HwDeviceType.AMD Then
                        If videoFilter.Length > 0 Then videoFilter = videoFilter & ","
                        videoFilter = videoFilter & "hwdownload,format=bgra," & scalePart & ",hwupload"
                    ElseIf hwType = HwDeviceType.IntelQSV Then
                        If videoFilter.Length > 0 Then videoFilter = videoFilter & ","
                        videoFilter = videoFilter & "hwdownload,format=bgra," & scalePart & ",format=nv12"
                    Else
                        If videoFilter.Length > 0 Then videoFilter = videoFilter & ","
                        videoFilter = videoFilter & "hwdownload,format=bgra," & scalePart
                    End If
                Else
                    If videoFilter.Length > 0 Then videoFilter = videoFilter & ","
                    videoFilter = videoFilter & scalePart
                End If
            End If

            ' ── User-provided output filter (append to computed filter) ──
            If Not String.IsNullOrWhiteSpace(v.OutputFilter) Then
                If videoFilter.Length > 0 Then videoFilter = videoFilter & ","
                videoFilter = videoFilter & v.OutputFilter.Trim()
            End If

            If videoFilter.Length > 0 Then
                sb.Append("-vf """ & videoFilter & """ ")
            End If

            ' ── Encoder ──
            sb.Append("-c:v " & enc.FFmpegCodec & " ")

            Select Case hwType
                Case HwDeviceType.NVIDIA
                    sb.Append("-preset " & enc.Preset & " ")
                    If enc.Tune.Length > 0 Then
                        sb.Append("-tune " & enc.Tune & " ")
                    End If
                    sb.Append("-rc " & enc.RateControl & " ")

                    ' Bitrate + minrate + maxrate + bufsize
                    sb.Append("-b:v " & br & " ")
                    If enc.MinrateBps > 0 Then sb.Append("-minrate " & minrateStr & " ")
                    If enc.MaxrateBps > 0 Then sb.Append("-maxrate " & maxrateStr & " ")
                    sb.Append("-bufsize " & bufStr & " ")

                    ' GOP + fps_mode
                    sb.Append("-g " & gopStr & " -fps_mode " & enc.FpsMode & " ")

                    ' AQ (only when enabled)
                    If enc.SpatialAQ = 1 Then sb.Append("-spatial-aq 1 ")
                    If enc.TemporalAQ = 1 Then sb.Append("-temporal-aq 1 ")

                    ' Look-ahead (only when > 0)
                    If enc.LookAhead > 0 Then
                        sb.Append("-look_ahead " & enc.LookAhead.ToString() & " ")
                    End If

                    ' Zerolatency (only when True)
                    If enc.ZeroLatency Then
                        sb.Append("-zerolatency 1 ")
                    End If

                    ' Constant Quality (only when RateControl=cq and Cq > 0)
                    If enc.RateControl = "cq" AndAlso enc.Cq > 0 Then
                        sb.Append("-cq " & enc.Cq.ToString() & " ")
                    End If

                Case HwDeviceType.IntelQSV
                    sb.Append("-preset medium ")
                    sb.Append("-b:v " & br & " ")
                    If enc.MinrateBps > 0 Then sb.Append("-minrate " & minrateStr & " ")
                    If enc.MaxrateBps > 0 Then sb.Append("-maxrate " & maxrateStr & " ")
                    sb.Append("-bufsize " & bufStr & " -rc cbr ")
                    sb.Append("-look_ahead 1 ")

                Case HwDeviceType.AMD
                    sb.Append("-preset balanced -usage transcoding ")
                    sb.Append("-b:v " & br & " ")
                    If enc.MinrateBps > 0 Then sb.Append("-minrate " & minrateStr & " ")
                    If enc.MaxrateBps > 0 Then sb.Append("-maxrate " & maxrateStr & " ")
                    sb.Append("-bufsize " & bufStr & " -rc cbr ")

                Case HwDeviceType.None
                    If enc.FFmpegCodec.IndexOf("libx265", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset medium ")
                        sb.Append("-b:v " & br & " -minrate " & minrateStr & " -maxrate " & maxrateStr & " -bufsize " & bufStr & " -pix_fmt yuv420p10le ")
                    ElseIf enc.FFmpegCodec.IndexOf("libx264", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset ultrafast -tune zerolatency ")
                        sb.Append("-b:v " & br & " -minrate " & minrateStr & " -maxrate " & maxrateStr & " -bufsize " & bufStr & " -pix_fmt yuv420p ")
                    ElseIf enc.FFmpegCodec.IndexOf("svtav1", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset 6 ")
                        sb.Append("-b:v " & br & " -minrate " & minrateStr & " -maxrate " & maxrateStr & " -bufsize " & bufStr & " -pix_fmt yuv420p ")
                    Else
                        sb.Append("-b:v " & br & " -minrate " & minrateStr & " -maxrate " & maxrateStr & " -bufsize " & bufStr & " ")
                    End If
            End Select

            ' ── Profile (H.264/HEVC profile) ──
            If enc.Profile.Length > 0 Then
                sb.Append("-profile:v " & enc.Profile & " ")
            End If

            ' ── Pixel format ──
            ' V2 FIX: always append for HW encoder + HW capture (V1 skipped this).
            ' For CPU encoders (libx264/libx265/svtav1) the format was already
            ' appended in the encoder branch above.
            Dim isHwCapture As Boolean = (v.Capture.Method.ToLowerInvariant() = "ddagrab" OrElse
                                            v.Capture.Method.ToLowerInvariant() = "gfxcapture")
            Dim isCpuEncoder As Boolean = (enc.FFmpegCodec.IndexOf("libx264", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                            enc.FFmpegCodec.IndexOf("libx265", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                            enc.FFmpegCodec.IndexOf("svtav1", StringComparison.OrdinalIgnoreCase) >= 0)

            If hwType = HwDeviceType.None Then
                If Not isCpuEncoder Then
                    sb.Append("-pix_fmt " & enc.PixelFormat & " ")
                End If
            ElseIf hwType = HwDeviceType.NVIDIA OrElse hwType = HwDeviceType.AMD Then
                ' V2 FIX: always append (V1 skipped for isHwCapture=True)
                sb.Append("-pix_fmt " & enc.PixelFormat & " ")
            ElseIf hwType = HwDeviceType.IntelQSV Then
                ' V2 FIX: always append (V1 skipped for isHwCapture=True)
                sb.Append("-pix_fmt " & enc.PixelFormat & " ")
            End If

            ' ── +faststart (final output only, NOT on temp video) ──
            Dim isTempVideo As Boolean = outputFile.Contains(".video.tmp.")
            If Not isTempVideo AndAlso _cfg.Output.FastStart Then
                Dim ext As String = Path.GetExtension(outputFile).ToLowerInvariant()
                If ext = ".mp4" OrElse ext = ".mov" OrElse ext = ".m4v" Then
                    sb.Append("-movflags +faststart ")
                End If
            End If

            ' ── Overwrite flag + output path ──
            If _cfg.Output.Overwrite Then
                sb.Append("-y ")
            Else
                sb.Append("-n ")
            End If
            sb.Append("""")
            sb.Append(outputFile)
            sb.Append("""")

            Return sb.ToString()
        End Function

        ' ── Helper enums + functions ──

        Private Enum HwDeviceType
            None
            NVIDIA
            IntelQSV
            AMD
        End Enum

        Private Function DetectHwDeviceType(ffmpegCodec As String) As HwDeviceType
            If ffmpegCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.NVIDIA
            End If
            If ffmpegCodec.IndexOf("qsv", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.IntelQSV
            End If
            If ffmpegCodec.IndexOf("amf", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.AMD
            End If
            Return HwDeviceType.None
        End Function
    End Class
End Namespace
