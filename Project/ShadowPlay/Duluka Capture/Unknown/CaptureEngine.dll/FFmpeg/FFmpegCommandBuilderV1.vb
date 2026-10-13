Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.IO
Imports System.Text

Namespace CaptureEngine.FFmpeg
    ''' <summary>
    ''' V1 FFmpeg command builder — byte-identical replica of the legacy
    ''' CaptureEngine.BuildFFmpegArguments (Engine/Engine/[FFmpeg]/FFmpegArgumentBuilder.vb).
    '''
    ''' WHY A REPLICA?
    '''   The legacy method is a Private Function on the CaptureEngine class
    '''   (the Engine WinForms exe's CaptureEngine.vb — NOT this assembly),
    '''   tightly coupled to _settings (Engine.CaptureSettings) and
    '''   LogDebug/WriteDebugLog.
    '''
    '''   Phase 2 of migration requires the command builder to be:
    '''     1. A standalone class (testable without instantiating Engine's CaptureEngine)
    '''     2. Implementing IFFmpegCommandBuilder (so V1 and V2 share an interface)
    '''     3. Byte-identical output to legacy for the same input config
    '''        (parity guarantee — see Phase 6 tests)
    '''
    ''' This class does NOT call LogDebug or WriteDebugLog. Logging is the
    ''' caller's responsibility (Engine's CaptureEngine.StartRecordingAsync
    ''' continues to log "FFmpeg command: ..." before invoking the builder).
    '''
    ''' It does NOT spawn FFmpeg — it just builds the argument string.
    '''
    ''' HARDENING NOTES (preserved from legacy, NOT fixed):
    '''   - BufsizeBps = BitrateBps (1× bitrate) — same as legacy NVIDIA branch
    '''     (the `buf = bitrate * 2` declaration is dead code in legacy)
    '''   - RateControl is hardcoded "cbr" for NVIDIA (legacy behavior)
    '''   - Tune is hardcoded "ll" for NVIDIA
    '''   - SpatialAQ / TemporalAQ are hardcoded to 1 for NVIDIA
    '''   - PixelFormat is NOT appended for ddagrab+NVENC (legacy behavior)
    '''
    ''' These are preserved to guarantee byte-identical output. V2 fixes them.
    ''' </summary>
    Public NotInheritable Class FFmpegCommandBuilderV1
        Implements IFFmpegCommandBuilder

        ''' <summary>
        ''' V1 capture settings — mirrors Engine.CaptureSettings fields but
        ''' defined here so this builder does not depend on Engine project
        ''' (which is a WinForms exe, not a class library).
        '''
        ''' Callers populate this from Engine.CaptureSettings via direct field
        ''' copy (Engine project knows its own CaptureSettings shape).
        ''' </summary>
        Public NotInheritable Class V1Settings
            Public Property Encoder As String = "h264_nvenc"
            Public Property FPS As Integer = 60
            Public Property Bitrate As Long = 20000000L
            Public Property NvencPreset As Integer = 4
            Public Property CaptureMethod As String = "ddagrab"
            Public Property UseNativeResolution As Boolean = True
            Public Property CustomWidth As Integer = 0
            Public Property CustomHeight As Integer = 0
            Public Property PixelFormat As String = "nv12"
        End Class

        Private ReadOnly _settings As V1Settings

        Public Sub New(settings As V1Settings)
            If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
            _settings = settings
        End Sub

        Public ReadOnly Property BuilderLabel As String Implements IFFmpegCommandBuilder.BuilderLabel
            Get
                Return "V1 (legacy CaptureEngine.BuildFFmpegArguments replica)"
            End Get
        End Property

        Public Function Build(outputFile As String) As String Implements IFFmpegCommandBuilder.Build
            If String.IsNullOrEmpty(outputFile) Then
                Throw New ArgumentException("outputFile is empty.", NameOf(outputFile))
            End If

            Dim sb As New StringBuilder()
            Dim fpsStr As String = _settings.FPS.ToString()
            Dim br As String = _settings.Bitrate.ToString()
            Dim buf As String = (_settings.Bitrate * 2).ToString() ' legacy declared but NEVER USED for NVIDIA branch
            Dim hwType As HwDeviceType = DetectHwDeviceType(_settings.Encoder)

            sb.Append("-hide_banner -loglevel info ")

            Dim videoFilter As String = ""

            Select Case _settings.CaptureMethod.ToLower()
                Case "ddagrab"
                    sb.Append("-f lavfi -i ""ddagrab=output_idx=0:framerate=" & fpsStr & """ ")
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
                    sb.Append("-f lavfi -i ""gfxcapture=monitor_idx=0:max_framerate=" & fpsStr & """ ")
                    Select Case hwType
                        Case HwDeviceType.IntelQSV
                            ' Same IntelQSV hwmap failure as ddagrab (MFX session -9).
                            videoFilter = "fps=" & fpsStr & ",hwdownload,format=bgra,format=nv12"
                        Case HwDeviceType.None
                            videoFilter = "fps=" & fpsStr & ",hwdownload,format=bgra,format=yuv420p"
                        Case HwDeviceType.NVIDIA, HwDeviceType.AMD
                            videoFilter = "fps=" & fpsStr
                    End Select
            End Select

            If Not _settings.UseNativeResolution AndAlso _settings.CustomWidth > 0 Then
                Dim isHw As Boolean = (_settings.CaptureMethod.ToLower() = "ddagrab" OrElse
                                       _settings.CaptureMethod.ToLower() = "gfxcapture")
                Dim scalePart As String = "scale=" & _settings.CustomWidth.ToString() & ":" & _settings.CustomHeight.ToString()

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

            If videoFilter.Length > 0 Then
                sb.Append("-vf """ & videoFilter & """ ")
            End If

            sb.Append("-c:v " & _settings.Encoder & " ")

            Dim nvencPreset As String = MapNvencPreset(_settings.NvencPreset)
            If String.IsNullOrEmpty(nvencPreset) Then nvencPreset = "p4"

            Select Case hwType
                Case HwDeviceType.NVIDIA
                    sb.Append("-preset " & nvencPreset & " -tune ll -rc cbr ")
                    ' LEGACY BUG preserved: uses br (1× bitrate) for bufsize, NOT buf (2× bitrate)
                    sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & br & " ")
                    sb.Append("-g " & fpsStr & " -fps_mode cfr ")
                    sb.Append("-spatial-aq 1 -temporal-aq 1 ")

                Case HwDeviceType.IntelQSV
                    sb.Append("-preset medium ")
                    sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " -rc cbr ")
                    sb.Append("-look_ahead 1 ")

                Case HwDeviceType.AMD
                    sb.Append("-preset balanced -usage transcoding ")
                    sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " -rc cbr ")

                Case HwDeviceType.None
                    If _settings.Encoder.IndexOf("libx265", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset medium ")
                        sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " -pix_fmt yuv420p10le ")
                    ElseIf _settings.Encoder.IndexOf("libx264", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset ultrafast -tune zerolatency ")
                        sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " -pix_fmt yuv420p ")
                    ElseIf _settings.Encoder.IndexOf("svtav1", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        sb.Append("-preset 6 ")
                        sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " -pix_fmt yuv420p ")
                    Else
                        sb.Append("-b:v " & br & " -minrate " & br & " -maxrate " & br & " -bufsize " & buf & " ")
                    End If
            End Select

            Dim isHwCapture As Boolean = (_settings.CaptureMethod.ToLower() = "ddagrab" OrElse
                                            _settings.CaptureMethod.ToLower() = "gfxcapture")

            If hwType = HwDeviceType.None Then
                If _settings.Encoder.IndexOf("libx265", StringComparison.OrdinalIgnoreCase) < 0 AndAlso
                   _settings.Encoder.IndexOf("libx264", StringComparison.OrdinalIgnoreCase) < 0 AndAlso
                   _settings.Encoder.IndexOf("svtav1", StringComparison.OrdinalIgnoreCase) < 0 Then
                    sb.Append("-pix_fmt " & _settings.PixelFormat & " ")
                End If
            ElseIf hwType = HwDeviceType.NVIDIA OrElse hwType = HwDeviceType.AMD Then
                If Not isHwCapture Then
                    sb.Append("-pix_fmt " & _settings.PixelFormat & " ")
                End If
            ElseIf hwType = HwDeviceType.IntelQSV Then
                If Not isHwCapture Then
                    sb.Append("-pix_fmt " & _settings.PixelFormat & " ")
                End If
            End If

            ' Temp video file (two-process mode) must NOT get +faststart
            Dim isTempVideo As Boolean = outputFile.Contains(".video.tmp.")
            If Not isTempVideo Then
                Dim ext As String = Path.GetExtension(outputFile).ToLowerInvariant()
                If ext = ".mp4" OrElse ext = ".mov" OrElse ext = ".m4v" Then
                    sb.Append("-movflags +faststart ")
                End If
            End If
            sb.Append("-y """ & outputFile & """")

            Return sb.ToString()
        End Function

        ' ── Helper enums + functions (mirror legacy CaptureEngine) ──

        Private Enum HwDeviceType
            None
            NVIDIA
            IntelQSV
            AMD
        End Enum

        Private Function DetectHwDeviceType(encoderId As String) As HwDeviceType
            If encoderId.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.NVIDIA
            End If
            If encoderId.IndexOf("qsv", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.IntelQSV
            End If
            If encoderId.IndexOf("amf", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return HwDeviceType.AMD
            End If
            Return HwDeviceType.None
        End Function

        Private Function MapNvencPreset(presetNum As Integer) As String
            Select Case presetNum
                Case 1 : Return "p1"
                Case 2 : Return "p2"
                Case 3 : Return "p3"
                Case 4 : Return "p4"
                Case 5 : Return "p5"
                Case 6 : Return "p6"
                Case 7 : Return "p7"
                Case Else : Return "p4"
            End Select
        End Function
    End Class
End Namespace
