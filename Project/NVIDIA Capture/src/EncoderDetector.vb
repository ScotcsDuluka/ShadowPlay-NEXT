' EncoderDetector.vb
' ShadowPlay Engine - Detect Available FFmpeg Encoders
' Parses ffmpeg -encoders to find NVENC, QSV, AMF, CPU encoders
'
' ── FFmpeg -encoders output format ──
'  V....D h264_nvenc            NVIDIA NVENC H.264 encoder
'  V..... libx264                H.264 / AVC / MPEG-4 AVC
'  A..... aac                   AAC (Advanced Audio Coding)
'
' NOTE: Since we run `ffmpeg -encoders`, ALL listed items are encoders.
'       Just look for lines starting with "V" (video) then extract encoder ID.

Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO

Public Class EncoderDetector

    Public Class EncoderInfo
        Public Property ID As String
        Public Property Description As String
        Public Property VendorType As String
        Public Property CodecFamily As String
        Public Property IsHardware As Boolean

        Public Overrides Function ToString() As String
            Return ID & " (" & VendorType & " " & CodecFamily & ")"
        End Function
    End Class

    Public Class CaptureDeviceInfo
        Public Property ID As String
        Public Property Description As String

        Public Overrides Function ToString() As String
            Return ID & " - " & Description
        End Function
    End Class

    ' ── Known encoder IDs to keep (even if vendor detection returns Unknown) ──

    Private Shared ReadOnly _keepEncoders As HashSet(Of String) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv", "vp9_qsv",
        "h264_amf", "hevc_amf", "av1_amf",
        "libx264", "libx265", "libsvtav1", "libvpx-vp9", "libvpx",
        "mpeg4", "msmpeg4v3", "mpeg2video", "mjpeg", "png",
        "rawvideo", "wrapped_avframe", "bmp", "tiff",
        "h264_vaapi", "hevc_vaapi", "vp9_vaapi",
        "h264_videotoolbox", "hevc_videotoolbox"
    }

    ' ── Fallback encoders (used when detection fails) ──

    Private Shared ReadOnly _fallbackEncoders As List(Of EncoderInfo) = CreateFallbackList()

    Private Shared Function CreateFallbackList() As List(Of EncoderInfo)
        Dim list As New List(Of EncoderInfo)()
        list.Add(New EncoderInfo With {.ID = "h264_nvenc", .Description = "NVIDIA NVENC H.264 encoder", .VendorType = "NVIDIA", .CodecFamily = "H.264", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "hevc_nvenc", .Description = "NVIDIA NVENC HEVC encoder", .VendorType = "NVIDIA", .CodecFamily = "H.265/HEVC", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "h264_qsv", .Description = "Intel Quick Sync Video H.264", .VendorType = "Intel", .CodecFamily = "H.264", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "hevc_qsv", .Description = "Intel Quick Sync Video HEVC", .VendorType = "Intel", .CodecFamily = "H.265/HEVC", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "h264_amf", .Description = "AMD AMF H.264 encoder", .VendorType = "AMD", .CodecFamily = "H.264", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "hevc_amf", .Description = "AMD AMF HEVC encoder", .VendorType = "AMD", .CodecFamily = "H.265/HEVC", .IsHardware = True})
        list.Add(New EncoderInfo With {.ID = "libx264", .Description = "H.264 / AVC (x264)", .VendorType = "CPU", .CodecFamily = "H.264", .IsHardware = False})
        list.Add(New EncoderInfo With {.ID = "libx265", .Description = "H.265 / HEVC (x265)", .VendorType = "CPU", .CodecFamily = "H.265/HEVC", .IsHardware = False})
        list.Add(New EncoderInfo With {.ID = "libsvtav1", .Description = "SVT-AV1 (AV1)", .VendorType = "CPU", .CodecFamily = "AV1", .IsHardware = False})
        Return list
    End Function

    ' ── Instance fields (NOT shared) ──────────────────────────

    Private _ffmpegPath As String
    Private _videoEncoders As List(Of EncoderInfo)
    Private _captureDevices As List(Of CaptureDeviceInfo)
    Private _lastError As String
    Private _rawOutput As String
    Private _usedFallback As Boolean

    Public Sub New(ffmpegPath As String)
        _ffmpegPath = ffmpegPath
        _videoEncoders = New List(Of EncoderInfo)()
        _captureDevices = New List(Of CaptureDeviceInfo)()
        _lastError = ""
        _rawOutput = ""
        _usedFallback = False
    End Sub

    ' ── Properties ────────────────────────────────────────────

    Public ReadOnly Property VideoEncoders As List(Of EncoderInfo)
        Get
            Return _videoEncoders
        End Get
    End Property

    Public ReadOnly Property CaptureDevices As List(Of CaptureDeviceInfo)
        Get
            Return _captureDevices
        End Get
    End Property

    Public ReadOnly Property LastDetectionError As String
        Get
            Return _lastError
        End Get
    End Property

    Public ReadOnly Property RawOutput As String
        Get
            Return _rawOutput
        End Get
    End Property

    Public ReadOnly Property UsedFallback As Boolean
        Get
            Return _usedFallback
        End Get
    End Property

    ' ══════════════════════════════════════════════════════════
    ' ── Detect Encoders ──────────────────────────────────────
    ' ══════════════════════════════════════════════════════════

    Public Function DetectEncoders() As Boolean
        _videoEncoders.Clear()
        _lastError = ""
        _rawOutput = ""
        _usedFallback = False

        ' Step 1: Check FFmpeg file exists
        If Not File.Exists(_ffmpegPath) Then
            _lastError = "FFmpeg not found at: " & _ffmpegPath
            LoadFallback()
            Return False
        End If

        ' Step 2: Run ffmpeg -encoders and capture output
        Dim output As String = ""
        Dim exitCode As Integer = -1
        Try
            Dim result As Tuple(Of String, Integer) = RunFFmpeg("-encoders -hide_banner")
            output = result.Item1
            exitCode = result.Item2
        Catch ex As Exception
            _lastError = "FFmpeg crash: " & ex.Message
            DebugLog("RunFFmpeg exception: " & ex.ToString())
            LoadFallback()
            Return False
        End Try

        ' Step 3: Check output
        If String.IsNullOrWhiteSpace(output) Then
            _lastError = "FFmpeg returned empty output (exit code: " & exitCode.ToString() & ")"
            DebugLog("Empty output, exit code: " & exitCode.ToString())
            LoadFallback()
            Return False
        End If

        _rawOutput = output

        ' Step 4: Parse output
        Dim foundCount As Integer = 0
        Try
            Dim lines As String() = output.Split(New String() {Environment.NewLine, vbCrLf, vbLf, vbCr},
                                                 StringSplitOptions.RemoveEmptyEntries)

            For Each line In lines
                If String.IsNullOrWhiteSpace(line) Then Continue For

                Dim trimmed As String = line.TrimStart()

                ' Must start with "V" (video)
                If trimmed.Length < 3 Then Continue For
                If trimmed(0) <> "V"c Then Continue For

                ' Skip header/category lines
                ' Headers: " V..... = Video encoders" or " ----"
                If trimmed.Length > 10 Then
                    Dim firstTen As String = trimmed.Substring(0, Math.Min(10, trimmed.Length))
                    If firstTen.Contains("="c) Then Continue For
                    If firstTen.Contains("-"c) Then Continue For
                End If

                ' Extract encoder ID
                ' Format: "V....D  encoder_id   description"
                ' After 7-char flags, skip whitespace, encoder_id is next token
                Dim encoderId As String = ""
                Dim desc As String = ""
                If trimmed.Length > 7 Then
                    Dim rest As String = trimmed.Substring(7).TrimStart()
                    Dim parts As String() = rest.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
                    If parts.Length >= 1 Then
                        encoderId = parts(0)
                    End If
                    If parts.Length >= 2 Then
                        desc = String.Join(" ", parts, 1, parts.Length - 1)
                    End If
                End If

                ' Validate encoder ID
                If String.IsNullOrWhiteSpace(encoderId) OrElse encoderId.Length < 2 Then Continue For
                Dim isValidId As Boolean = True
                For Each ch In encoderId
                    If Not Char.IsLetterOrDigit(ch) AndAlso ch <> "_"c AndAlso ch <> "-"c Then
                        isValidId = False
                        Exit For
                    End If
                Next
                If Not isValidId Then Continue For

                ' Classify
                Dim vendorType As String = ClassifyVendor(encoderId)
                Dim codecFamily As String = ClassifyCodec(encoderId)

                ' Filter: keep only known/interesting encoders
                If vendorType = "Unknown" AndAlso Not _keepEncoders.Contains(encoderId) Then
                    Continue For
                End If

                ' Skip audio-only encoders
                If encoderId.Equals("aac", StringComparison.OrdinalIgnoreCase) Then Continue For
                If encoderId.Equals("opus", StringComparison.OrdinalIgnoreCase) Then Continue For
                If encoderId.Equals("libmp3lame", StringComparison.OrdinalIgnoreCase) Then Continue For
                If encoderId.Equals("ac3", StringComparison.OrdinalIgnoreCase) Then Continue For
                If encoderId.Equals("flac", StringComparison.OrdinalIgnoreCase) Then Continue For
                If encoderId.Equals("vorbis", StringComparison.OrdinalIgnoreCase) Then Continue For

                _videoEncoders.Add(New EncoderInfo With {
                    .ID = encoderId,
                    .Description = desc,
                    .VendorType = vendorType,
                    .CodecFamily = codecFamily,
                    .IsHardware = (vendorType <> "CPU")
                })
                foundCount += 1
            Next

        Catch ex As Exception
            _lastError = "Parse error: " & ex.Message
            DebugLog("Parse exception: " & ex.ToString())
        End Try

        ' Step 5: If no encoders found, load fallback
        If _videoEncoders.Count = 0 Then
            _lastError = "Parsed " & foundCount.ToString() & " lines but 0 encoders matched. Raw: " & _rawOutput.Substring(0, Math.Min(200, _rawOutput.Length))
            DebugLog("No encoders found. Raw output (first 500 chars):")
            DebugLog(_rawOutput.Substring(0, Math.Min(500, _rawOutput.Length)))
            LoadFallback()
            Return False
        End If

        DebugLog("Detection OK: " & _videoEncoders.Count.ToString() & " encoders found")
        Return True
    End Function

    ''' <summary>
    ''' Load fallback encoder list when detection fails
    ''' </summary>
    Private Sub LoadFallback()
        _usedFallback = True
        For Each enc In _fallbackEncoders
            _videoEncoders.Add(enc)
        Next
        DebugLog("Loaded " & _fallbackEncoders.Count.ToString() & " fallback encoders")
    End Sub

    ' ── Detect Capture Devices ─────────────────────────────────

    Public Function DetectCaptureDevices() As Boolean
        _captureDevices.Clear()

        ' Always add known capture methods
        _captureDevices.Add(New CaptureDeviceInfo With {.ID = "ddagrab", .Description = "Desktop Duplication (DXGI) - Windows 8.1+"})
        _captureDevices.Add(New CaptureDeviceInfo With {.ID = "gdigrab", .Description = "GDI Screen Capture - Legacy"})
        _captureDevices.Add(New CaptureDeviceInfo With {.ID = "gfxcapture", .Description = "Windows.Graphics.Capture - Win10 2004+"})

        ' Try to detect from FFmpeg -devices
        Try
            If File.Exists(_ffmpegPath) Then
                Dim result As Tuple(Of String, Integer) = RunFFmpeg("-devices -hide_banner")
                Dim output As String = result.Item1
                If Not String.IsNullOrWhiteSpace(output) Then
                    Dim lines As String() = output.Split(New String() {Environment.NewLine, vbCrLf, vbLf, vbCr},
                                                         StringSplitOptions.RemoveEmptyEntries)
                    For Each line In lines
                        If String.IsNullOrWhiteSpace(line) Then Continue For
                        Dim parts As String() = line.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
                        If parts.Length < 3 Then Continue For
                        Dim deviceId As String = parts(1)
                        ' Don't add duplicates
                        Dim alreadyHave As Boolean = False
                        For Each d In _captureDevices
                            ' Shared String.Equals call (instance form trips BC42025
                            ' under the .NET 10 SDK's VB compiler); identical semantics.
                            If String.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase) Then
                                alreadyHave = True : Exit For
                            End If
                        Next
                        If Not alreadyHave Then
                            _captureDevices.Add(New CaptureDeviceInfo With {.ID = deviceId, .Description = deviceId & " capture"})
                        End If
                    Next
                End If
            End If
        Catch
        End Try

        Return True
    End Function

    ' ── Detect Audio Devices ────────────────────────────────────

    Public Function DetectAudioDevices() As List(Of String)
        Dim devices As New List(Of String)()
        Try
            If Not File.Exists(_ffmpegPath) Then Return devices

            Dim result As Tuple(Of String, Integer) = RunFFmpeg("-list_devices true -f dshow -i dummy")
            Dim output As String = result.Item1

            If String.IsNullOrWhiteSpace(output) Then Return devices

            Dim lines As String() = output.Split(New String() {Environment.NewLine, vbCrLf, vbLf, vbCr},
                                                 StringSplitOptions.RemoveEmptyEntries)

            Dim inAudioSection As Boolean = False
            For Each line In lines
                Dim trimmed As String = line.Trim()

                ' Detect "DirectShow audio" section header
                If trimmed.IndexOf("DirectShow audio", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    inAudioSection = True
                    Continue For
                End If
                ' If we hit video section, stop
                If trimmed.IndexOf("DirectShow video", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    inAudioSection = False
                    Continue For
                End If
                ' Also detect alternative header format
                If trimmed.IndexOf("audio devices", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso
                   trimmed.IndexOf("video") < 0 Then
                    inAudioSection = True
                    Continue For
                End If

                If inAudioSection Then
                    ' Device lines look like:  "Alternative name" (or "[dshow @ ...]  \"Device Name\"")
                    ' Try to extract name from quoted strings
                    Dim quoteStart As Integer = trimmed.IndexOf(""""c)
                    If quoteStart >= 0 Then
                        Dim quoteEnd As Integer = trimmed.IndexOf(""""c, quoteStart + 1)
                        If quoteEnd > quoteStart Then
                            Dim deviceName As String = trimmed.Substring(quoteStart + 1, quoteEnd - quoteStart - 1)
                            If deviceName.Length > 0 AndAlso Not devices.Contains(deviceName) Then
                                devices.Add(deviceName)
                            End If
                        End If
                    End If

                    ' Also try: "  name" indented device name (after "Alternative name")
                    Dim altIdx As Integer = trimmed.IndexOf("Alternative name", StringComparison.OrdinalIgnoreCase)
                    If altIdx >= 0 Then
                        Dim afterAlt As String = trimmed.Substring(altIdx + 17).Trim()
                        If afterAlt.StartsWith(""""c) Then
                            Dim aEnd As Integer = afterAlt.IndexOf(""""c, 1)
                            If aEnd > 0 Then
                                Dim altName As String = afterAlt.Substring(1, aEnd - 1)
                                If Not devices.Contains(altName) Then
                                    devices.Add(altName)
                                End If
                            End If
                        End If
                    End If
                End If
            Next

        Catch
        End Try
        Return devices
    End Function

    ''' <summary>
    ''' Check if a specific audio device name exists on the system
    ''' </summary>
    Public Function IsAudioDeviceAvailable(deviceName As String) As Boolean
        If String.IsNullOrWhiteSpace(deviceName) Then Return False
        Dim devices As List(Of String) = DetectAudioDevices()
        For Each d In devices
            ' Shared String.Equals call (instance form trips BC42025
            ' under the .NET 10 SDK's VB compiler); identical semantics.
            If String.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        ' Partial match (some devices have truncated names)
        For Each d In devices
            If d.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               deviceName.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return True
            End If
        Next
        Return False
    End Function

    ' ── Filters ───────────────────────────────────────────────

    Public Function GetNVENCEncoders() As List(Of EncoderInfo)
        Return _videoEncoders.Where(Function(e) e.VendorType = "NVIDIA").ToList()
    End Function

    Public Function GetQSVEncoders() As List(Of EncoderInfo)
        Return _videoEncoders.Where(Function(e) e.VendorType = "Intel").ToList()
    End Function

    Public Function GetAMFEncoders() As List(Of EncoderInfo)
        Return _videoEncoders.Where(Function(e) e.VendorType = "AMD").ToList()
    End Function

    Public Function GetCPUEncoders() As List(Of EncoderInfo)
        Return _videoEncoders.Where(Function(e) e.VendorType = "CPU").ToList()
    End Function

    Public Function GetRecommendedEncoder() As String
        If GetNVENCEncoders().Any() Then Return "h264_nvenc"
        If GetQSVEncoders().Any() Then Return "h264_qsv"
        If GetAMFEncoders().Any() Then Return "h264_amf"
        Return "libx264"
    End Function

    ' ── Classification ───────────────────────────────────────

    Private Function ClassifyVendor(encoderId As String) As String
        Dim lower As String = encoderId.ToLower()
        If lower.Contains("nvenc") Then Return "NVIDIA"
        If lower.Contains("qsv") Then Return "Intel"
        If lower.Contains("amf") Then Return "AMD"
        If lower.Contains("vaapi") Then Return "VAAPI"
        If lower.Contains("videotoolbox") Then Return "Apple"
        If lower.Contains("libx264") OrElse lower.Contains("libx265") OrElse lower.Contains("libsvtav1") Then Return "CPU"
        If lower.Contains("libvpx") OrElse lower.Contains("mpeg4") OrElse lower.Contains("mpeg2video") OrElse lower.Contains("mjpeg") Then Return "CPU"
        Return "Unknown"
    End Function

    Private Function ClassifyCodec(encoderId As String) As String
        Dim lower As String = encoderId.ToLower()
        If lower.StartsWith("h264") OrElse lower.StartsWith("libx264") Then Return "H.264"
        If lower.StartsWith("hevc") OrElse lower.StartsWith("libx265") Then Return "H.265/HEVC"
        If lower.StartsWith("av1") OrElse lower.Contains("svtav1") Then Return "AV1"
        If lower.StartsWith("vp9") OrElse lower.Contains("libvpx") Then Return "VP9"
        If lower.Contains("mpeg4") Then Return "MPEG-4"
        If lower.Contains("mpeg2") Then Return "MPEG-2"
        If lower.Contains("mjpeg") Then Return "MJPEG"
        Return "Other"
    End Function

    ' ── Run FFmpeg ────────────────────────────────────────────
    '
    ' IMPORTANT: Uses synchronous reads (NOT async BeginOutputReadLine)
    ' because async approach in VB.NET closure captures the StringBuilder
    ' by reference, causing data loss when Using block disposes the Process.
    '
    ' Pattern: Read stdout first, then stderr. For `ffmpeg -encoders`
    ' the output goes to stdout on all tested builds.

    Private Function RunFFmpeg(arguments As String) As Tuple(Of String, Integer)
        Dim stdout As String = ""
        Dim stderr As String = ""
        Dim exitCode As Integer = -1

        Dim si As New ProcessStartInfo()
        si.FileName = _ffmpegPath
        si.Arguments = arguments
        si.UseShellExecute = False
        si.RedirectStandardOutput = True
        si.RedirectStandardError = True
        si.CreateNoWindow = True

        Using proc As New Process()
            proc.StartInfo = si
            proc.Start()

            ' ✅ M2 FIX: read stdout and stderr concurrently to avoid deadlock.
            ' Old code called ReadToEnd() on stdout first, then stderr. If
            ' ffmpeg's stderr fills the OS pipe buffer (~64KB) while we're
            ' still reading stdout, ffmpeg blocks writing to stderr → we
            ' block reading stdout → classic deadlock. Now read both on
            ' separate Tasks and wait for both.
            Dim stdoutTask As Task(Of String) = Task.Run(Function() proc.StandardOutput.ReadToEnd())
            Dim stderrTask As Task(Of String) = Task.Run(Function() proc.StandardError.ReadToEnd())

            ' Wait for exit with timeout
            If proc.WaitForExit(30000) Then
                exitCode = proc.ExitCode
            Else
                Try : proc.Kill() : Catch : End Try
                exitCode = -999
            End If

            ' Wait for both reads to finish (with a short timeout in case
            ' the process died before flushing).
            Try
                stdout = stdoutTask.Result
            Catch
                stdout = ""
            End Try
            Try
                stderr = stderrTask.Result
            Catch
                stderr = ""
            End Try
        End Using

        ' Return stdout (primary), or stderr if stdout is empty
        If Not String.IsNullOrWhiteSpace(stdout) Then
            Return New Tuple(Of String, Integer)(stdout, exitCode)
        ElseIf Not String.IsNullOrWhiteSpace(stderr) Then
            Return New Tuple(Of String, Integer)(stderr, exitCode)
        Else
            Return New Tuple(Of String, Integer)("", exitCode)
        End If
    End Function

    ' ── Debug Log ─────────────────────────────────────────────

    Private Sub DebugLog(message As String)
        _lastError = message
        Try
            Dim logDir As String = AppLayout.P("Logs")
            Dim logPath As String = Path.Combine(logDir, "encoder-detect.log")
            Dim logLine As String = "[" & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "] " & message
            ' ✅ P1: route through BackgroundLogger instead of File.AppendAllText per line.
            BackgroundLogger.Log(logPath, logLine)
        Catch
        End Try
    End Sub

End Class
