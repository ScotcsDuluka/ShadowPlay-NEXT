' CaptureSettings.vb
' ShadowPlay Engine — Unified 4-file Configuration
'
' ┌─────────────────────────────────────────────────────────┐
' │ config.json  — General (Paths, UI, Hotkeys, GitHub)    │
' │ video.json   — Video (Encoder, FPS, Bitrate, Presets)  │
' │ audio.json   — Audio (System/Mic, Volume, TrackMode)    │
' │ engine.json  — Engine (CaptureMethod, Process, Timing) │
' └─────────────────────────────────────────────────────────┘
'
' Each file has its own ConfigVersion for independent migration.
' All projects (Overlay + Engine) share the same files.

Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization

<Serializable()>
Public Class CaptureSettings

    ' ═══════════════════════════════════════════════════════════════
    ' FIELDS — merged from video.json + audio.json + engine.json
    ' ═══════════════════════════════════════════════════════════════

    ' ── From video.json ──
    Public Property Encoder As String = "NVENC_H264"
    Public Property FPS As Integer = 60
    Public Property Bitrate As Long = 20000000
    Public Property NvencPreset As Integer = 4
    Public Property UseNativeResolution As Boolean = True
    Public Property CustomWidth As Integer = 0
    Public Property CustomHeight As Integer = 0
    Public Property ReplayDuration As Integer = 60
    Public Property ActivePreset As String = "Medium"

    ' ── From audio.json ──
    Public Property SystemAudioCapture As Boolean = True
    Public Property MicCapture As Boolean = False
    Public Property SystemAudioVolume As Single = 1.0F
    Public Property MicVolume As Single = 1.0F
    Public Property MicDeviceName As String = ""
    Public Property MicDeviceId As String = ""

    ' ★ P13.4: audio clock selection (doc PHASE-13 §4) — "Legacy" = proven
    ' AudioTap v2 (Stopwatch); "Device" = WasapiPositionCapture + AudioTap
    ' v3 with exact QPC anchors and ZERO calibration lead. Persisted in
    ' audio.json so OWNER can A/B on real runs without a rebuild.
    Public Property AudioClockMode As String = "Legacy"

    Public Enum AudioTrackModeEnum
        SingleTrack
        SeparateTrack
    End Enum

    Public Property AudioTrackMode As AudioTrackModeEnum = AudioTrackModeEnum.SingleTrack

    ' ── From engine.json ──
    Public Property CaptureMethod As String = "ddagrab"
    Public Property PixelFormat As String = "nv12"
    Public Property Preset As String = "p4"
    Public Property RateControl As String = "cbr"
    Public Property FileFormat As String = "mp4"
    Public Property FFmpegPath As String = ""
    Public Property HotkeyStart As String = "Control+Shift+F9"
    Public Property HotkeyStop As String = "Control+Shift+F10"
    Public Property HotkeyToggle As String = "Control+Shift+F8"
    ' OWNER (2026-09-25): include the mouse cursor in captured frames.
    ' Default OFF until the composition passes a debug-layer session on the
    ' 1080 Ti driver (Video.Tests: DeviceRemoved when ON). UI toggle writes
    ' engine.json explicitly.
    Public Property CaptureCursor As Boolean = False

    ' ── Legacy (deprecated, kept for backward compat) ──
    Public Property AudioCapture As Boolean = False
    Public Property AudioDevice As String = ""
    Public Property ConfigVersion As Integer = 1

    ' ═══════════════════════════════════════════════════════════════
    ' SAVE / LOAD
    ' ═══════════════════════════════════════════════════════════════

    Private Const VIDEO_CONFIG_VERSION As Integer = 1
    Private Const AUDIO_CONFIG_VERSION As Integer = 1
    Private Const ENGINE_CONFIG_VERSION As Integer = 1

    ''' <summary>
    ''' Load all settings.
    ''' configPath is used as base directory (all files live in same folder).
    '''
    ''' GLM/6 UNIFIED CONFIG ORDER (config.json is the ONE user-facing file):
    '''   1. engine.json          — Engine-internal knobs only (PixelFormat,
    '''                             RateControl, FileFormat, Hotkeys...) until
    '''                             the host goes fully headless.
    '''   2. Overlay config.json  — unified user settings (encoder/fps/bitrate/
    '''                             resolution/audio/paths). WINS when present.
    '''   3. LEGACY video.json + audio.json — only when (2) is unavailable
    '''                             (old installs / source-tree runs).
    ''' </summary>
    Public Shared Function Load(configPath As String) As CaptureSettings
        Dim settings As New CaptureSettings()
        Dim baseDir As String = Path.GetDirectoryName(configPath)
        If String.IsNullOrEmpty(baseDir) Then baseDir = AppLayout.Dir

        Dim enginePath As String = Path.Combine(baseDir, "engine.json")

        ' ── 1) UNIFIED: Overlay config.json (single source of truth) ──
        ' Load engine.json FIRST (Engine-only knobs: PixelFormat/RateControl/
        ' FileFormat/Hotkeys), THEN apply unified on top so user settings WIN
        ' any overlap (engine.json may contain stale baked-in values).
        If OverlayConfig.IsAvailable Then
            If File.Exists(enginePath) Then
                LoadEngineSettings(settings, enginePath)
            Else
                Dim foundEngine As String = FindConfigFile("engine.json")
                If Not String.IsNullOrEmpty(foundEngine) Then LoadEngineSettings(settings, foundEngine)
            End If

            If OverlayConfig.ApplyUnifiedToCaptureSettings(settings) Then
                Return settings
            End If
        End If

        ' ── 2) LEGACY fallback (old installs): original precedence ──
        ' video.json → audio.json → engine.json (video wins over engine).
        Dim videoPath As String = Path.Combine(baseDir, "video.json")
        If File.Exists(videoPath) Then
            LoadVideoSettings(settings, videoPath)
        End If

        Dim audioPath As String = Path.Combine(baseDir, "audio.json")
        If File.Exists(audioPath) Then
            LoadAudioSettings(settings, audioPath)
        End If

        If File.Exists(enginePath) Then
            LoadEngineSettings(settings, enginePath)
        End If

        ' ── Fallback: search parent dirs if files not found ──
        If Not File.Exists(videoPath) Then
            Dim foundVideo As String = FindConfigFile("video.json")
            If Not String.IsNullOrEmpty(foundVideo) Then LoadVideoSettings(settings, foundVideo)
        End If
        If Not File.Exists(audioPath) Then
            Dim foundAudio As String = FindConfigFile("audio.json")
            If Not String.IsNullOrEmpty(foundAudio) Then LoadAudioSettings(settings, foundAudio)
        End If
        If Not File.Exists(enginePath) Then
            Dim foundEngine As String = FindConfigFile("engine.json")
            If Not String.IsNullOrEmpty(foundEngine) Then LoadEngineSettings(settings, foundEngine)
        End If

        ' ── Auto-detect FFmpegPath if not actually usable ──
        ' IsUsableFFmpeg, NOT File.Exists: a saved path may point at a
        ' corrupt/stale binary (2026-09-08 postmortem) — re-detect so the
        ' good candidate one directory over wins instead.
        If Not FFmpegLocator.IsUsableFFmpeg(settings.FFmpegPath) Then
            settings.FFmpegPath = FindFFmpegPath()
        End If

        ' ── Default output directory ──
        If String.IsNullOrEmpty(settings.OutputDirectory) Then
            settings.OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ShadowPlay Recordings")
        End If
        If Not Directory.Exists(settings.OutputDirectory) Then
            Directory.CreateDirectory(settings.OutputDirectory)
        End If

        Return settings
    End Function

    ''' <summary>
    ''' Save Engine-specific fields to engine.json.
    ''' Video/Audio settings are saved by their respective forms.
    ''' </summary>
    Public Sub Save(configPath As String)
        Dim baseDir As String = Path.GetDirectoryName(configPath)
        If String.IsNullOrEmpty(baseDir) Then baseDir = AppLayout.Dir
        Dim enginePath As String = Path.Combine(baseDir, "engine.json")

        Try
            Dim sb As New Text.StringBuilder()
            sb.AppendLine("{")
            sb.AppendLine("  ""ConfigVersion"": " & ENGINE_CONFIG_VERSION & ",")
            sb.AppendLine("  ""CaptureMethod"": """ & JsonEscape(CaptureMethod) & """,")
            sb.AppendLine("  ""PixelFormat"": """ & JsonEscape(PixelFormat) & """,")
            sb.AppendLine("  ""Preset"": """ & JsonEscape(Preset) & """,")
            sb.AppendLine("  ""RateControl"": """ & JsonEscape(RateControl) & """,")
            sb.AppendLine("  ""FileFormat"": """ & JsonEscape(FileFormat) & """,")
            sb.AppendLine("  ""FFmpegPath"": """ & JsonEscape(FFmpegPath) & """,")
            sb.AppendLine("  ""HotkeyStart"": """ & JsonEscape(HotkeyStart) & """,")
            sb.AppendLine("  ""HotkeyStop"": """ & JsonEscape(HotkeyStop) & """,")
            sb.AppendLine("  ""HotkeyToggle"": """ & JsonEscape(HotkeyToggle) & """,")
            sb.AppendLine("  ""CaptureCursor"": " & CaptureCursor.ToString().ToLower() & ",")
            sb.AppendLine("  ""UseNativeResolution"": " & UseNativeResolution.ToString().ToLower() & ",")
            sb.AppendLine("  ""CustomWidth"": " & CustomWidth & ",")
            sb.AppendLine("  ""CustomHeight"": " & CustomHeight & "")
            sb.AppendLine("}")
            AppLayout.EnsureParentDir(enginePath)   ' Config\ is runtime-created
            File.WriteAllText(enginePath, sb.ToString())
        Catch
        End Try
    End Sub

    ' ═══════════════════════════════════════════════════════════════
    ' INDIVIDUAL FILE LOADERS
    ' ═══════════════════════════════════════════════════════════════

    Private Shared Sub LoadVideoSettings(settings As CaptureSettings, filePath As String)
        Try
            Dim json As String = File.ReadAllText(filePath)
            Using doc As JsonDocument = JsonDocument.Parse(json)
                Dim p As JsonElement = Nothing

                If doc.RootElement.TryGetProperty("Encoder", p) Then settings.Encoder = p.GetString()
                If doc.RootElement.TryGetProperty("ActivePreset", p) Then settings.ActivePreset = p.GetString()
                If doc.RootElement.TryGetProperty("ReplayDuration", p) Then settings.ReplayDuration = p.GetInt32()
                If doc.RootElement.TryGetProperty("APICapture", p) Then
                    Dim api As String = p.GetString()
                    If Not String.IsNullOrEmpty(api) Then settings.CaptureMethod = api
                End If

                ' "Current" section
                Dim currentProp As JsonElement = Nothing
                If doc.RootElement.TryGetProperty("Current", currentProp) Then
                    If currentProp.TryGetProperty("FPS", p) Then settings.FPS = p.GetInt32()
                    If currentProp.TryGetProperty("Bitrate", p) Then settings.Bitrate = p.GetInt32() * 1000L
                    If currentProp.TryGetProperty("EncoderPreset", p) Then settings.NvencPreset = p.GetInt32()
                    If currentProp.TryGetProperty("UseNativeResolution", p) Then settings.UseNativeResolution = p.GetBoolean()
                    If currentProp.TryGetProperty("Width", p) Then settings.CustomWidth = p.GetInt32()
                    If currentProp.TryGetProperty("Height", p) Then settings.CustomHeight = p.GetInt32()
                End If

                ' Also check lowercase "current" (Overlay uses camelCase)
                If doc.RootElement.TryGetProperty("current", currentProp) Then
                    If currentProp.TryGetProperty("fps", p) Then settings.FPS = p.GetInt32()
                    If currentProp.TryGetProperty("bitrate", p) Then settings.Bitrate = p.GetInt32() * 1000L
                    If currentProp.TryGetProperty("encoder_preset", p) Then settings.NvencPreset = p.GetInt32()
                    If currentProp.TryGetProperty("use_native_resolution", p) Then settings.UseNativeResolution = p.GetBoolean()
                    If currentProp.TryGetProperty("width", p) Then settings.CustomWidth = p.GetInt32()
                    If currentProp.TryGetProperty("height", p) Then settings.CustomHeight = p.GetInt32()
                End If
            End Using
        Catch
        End Try
    End Sub

    Private Shared Sub LoadAudioSettings(settings As CaptureSettings, filePath As String)
        Try
            Dim json As String = File.ReadAllText(filePath)
            Using doc As JsonDocument = JsonDocument.Parse(json)
                Dim p As JsonElement = Nothing

                If doc.RootElement.TryGetProperty("SystemEnabled", p) Then settings.SystemAudioCapture = p.GetBoolean()
                If doc.RootElement.TryGetProperty("MicEnabled", p) Then settings.MicCapture = p.GetBoolean()
                If doc.RootElement.TryGetProperty("SystemVolume", p) Then settings.SystemAudioVolume = p.GetSingle()
                If doc.RootElement.TryGetProperty("MicVolume", p) Then settings.MicVolume = p.GetSingle()
                If doc.RootElement.TryGetProperty("MicDevice", p) Then settings.MicDeviceName = p.GetString()
                If doc.RootElement.TryGetProperty("MicDeviceId", p) Then settings.MicDeviceId = p.GetString()
                If doc.RootElement.TryGetProperty("AudioTrackMode", p) Then
                    settings.AudioTrackMode = DirectCast(p.GetInt32(), AudioTrackModeEnum)
                End If
                If doc.RootElement.TryGetProperty("AudioClockMode", p) Then
                    Dim v As String = p.GetString()
                    If Not String.IsNullOrEmpty(v) Then settings.AudioClockMode = v.Trim()
                End If

                ' Also check lowercase (Overlay uses snake_case in some places)
                If doc.RootElement.TryGetProperty("system_enabled", p) Then settings.SystemAudioCapture = p.GetBoolean()
                If doc.RootElement.TryGetProperty("mic_enabled", p) Then settings.MicCapture = p.GetBoolean()
                If doc.RootElement.TryGetProperty("system_volume", p) Then settings.SystemAudioVolume = p.GetSingle()
                If doc.RootElement.TryGetProperty("mic_volume", p) Then settings.MicVolume = p.GetSingle()
                If doc.RootElement.TryGetProperty("mic_device", p) Then settings.MicDeviceName = p.GetString()
                If doc.RootElement.TryGetProperty("mic_device_id", p) Then settings.MicDeviceId = p.GetString()
            End Using
        Catch
        End Try
    End Sub

    Private Shared Sub LoadEngineSettings(settings As CaptureSettings, filePath As String)
        Try
            Dim json As String = File.ReadAllText(filePath)
            Using doc As JsonDocument = JsonDocument.Parse(json)
                Dim p As JsonElement = Nothing

                If doc.RootElement.TryGetProperty("CaptureMethod", p) Then settings.CaptureMethod = p.GetString()
                If doc.RootElement.TryGetProperty("PixelFormat", p) Then settings.PixelFormat = p.GetString()
                If doc.RootElement.TryGetProperty("Preset", p) Then settings.Preset = p.GetString()
                If doc.RootElement.TryGetProperty("RateControl", p) Then settings.RateControl = p.GetString()
                If doc.RootElement.TryGetProperty("FileFormat", p) Then settings.FileFormat = p.GetString()
                If doc.RootElement.TryGetProperty("FFmpegPath", p) Then settings.FFmpegPath = p.GetString()
                If doc.RootElement.TryGetProperty("HotkeyStart", p) Then settings.HotkeyStart = p.GetString()
                If doc.RootElement.TryGetProperty("HotkeyStop", p) Then settings.HotkeyStop = p.GetString()
                If doc.RootElement.TryGetProperty("HotkeyToggle", p) Then settings.HotkeyToggle = p.GetString()
                If doc.RootElement.TryGetProperty("CaptureCursor", p) Then settings.CaptureCursor = p.GetBoolean()
                If doc.RootElement.TryGetProperty("UseNativeResolution", p) Then settings.UseNativeResolution = p.GetBoolean()
                If doc.RootElement.TryGetProperty("CustomWidth", p) Then settings.CustomWidth = p.GetInt32()
                If doc.RootElement.TryGetProperty("CustomHeight", p) Then settings.CustomHeight = p.GetInt32()
                If doc.RootElement.TryGetProperty("OutputDirectory", p) Then settings.OutputDirectory = p.GetString()
            End Using
        Catch
        End Try
    End Sub

    ' ═══════════════════════════════════════════════════════════════
    ' AUDIO.JSON SAVE (called by AudioSettingsForm)
    ' ═══════════════════════════════════════════════════════════════

    Public Sub SaveAudio(audioFilePath As String)
        Try
            Dim sb As New Text.StringBuilder()
            sb.AppendLine("{")
            sb.AppendLine("  ""ConfigVersion"": " & AUDIO_CONFIG_VERSION & ",")
            sb.AppendLine("  ""SystemEnabled"": " & SystemAudioCapture.ToString().ToLower() & ",")
            sb.AppendLine("  ""MicEnabled"": " & MicCapture.ToString().ToLower() & ",")
            sb.AppendLine("  ""SystemVolume"": " & SystemAudioVolume.ToString(Globalization.CultureInfo.InvariantCulture) & ",")
            sb.AppendLine("  ""MicVolume"": " & MicVolume.ToString(Globalization.CultureInfo.InvariantCulture) & ",")
            sb.AppendLine("  ""MicDevice"": """ & JsonEscape(MicDeviceName) & """,")
            sb.AppendLine("  ""MicDeviceId"": """ & JsonEscape(MicDeviceId) & """,")
            sb.AppendLine("  ""AudioTrackMode"": " & CInt(AudioTrackMode).ToString() & ",")
            sb.AppendLine("  ""AudioClockMode"": """ & JsonEscape(AudioClockMode) & """")
            sb.AppendLine("}")
            AppLayout.EnsureParentDir(audioFilePath)   ' Config\ is runtime-created
            File.WriteAllText(audioFilePath, sb.ToString())
        Catch
        End Try
    End Sub

    ' ═══════════════════════════════════════════════════════════════
    ' HELPERS
    ' ═══════════════════════════════════════════════════════════════

    Private Shared Function FindConfigFile(fileName As String) As String
        Dim appDir As String = AppLayout.Dir

        ' ROOT-FIXED LAYOUT: Config\ owns *.json in the deployed tree
        Dim layoutCandidate As String = Path.Combine(appDir, "Config", fileName)
        If File.Exists(layoutCandidate) Then Return layoutCandidate

        ' Same folder (legacy dev layout)
        Dim candidate As String = Path.Combine(appDir, fileName)
        If File.Exists(candidate) Then Return candidate

        ' Parent dirs
        Dim parentDir As String = appDir
        For depth As Integer = 1 To 5
            Try
                Dim parent As DirectoryInfo = Directory.GetParent(parentDir)
                If parent Is Nothing Then Exit For
                parentDir = parent.FullName
                candidate = Path.Combine(parentDir, fileName)
                If File.Exists(candidate) Then Return candidate
            Catch
                Exit For
            End Try
        Next

        ' Sibling Overlay folder
        Dim engineProjDir As String = appDir
        For depth As Integer = 1 To 6
            Try
                Dim parent As DirectoryInfo = Directory.GetParent(engineProjDir)
                If parent Is Nothing Then Exit For
                engineProjDir = parent.FullName
                Dim overlayBin As String = Path.Combine(parent.FullName, "Overlay", "bin")
                If Directory.Exists(overlayBin) Then
                    For Each configDir As String In {"Release", "Debug"}
                        Dim configPath_ As String = Path.Combine(overlayBin, configDir)
                        If Directory.Exists(configPath_) Then
                            For Each subDir As String In Directory.GetDirectories(configPath_)
                                candidate = Path.Combine(subDir, fileName)
                                If File.Exists(candidate) Then Return candidate
                            Next
                        End If
                    Next
                    Exit For
                End If
            Catch
                Exit For
            End Try
        Next

        Return ""
    End Function

    Private Shared Function FindFFmpegPath() As String
        Dim appDir As String = AppLayout.Dir
        Dim candidates As New List(Of String)()
        candidates.Add(AppLayout.P("FFmpeg", "ffmpeg.exe"))
        candidates.Add(Path.Combine(appDir, "API-Core", "ffmpeg.exe"))
        candidates.Add(Path.Combine(appDir, "api-core", "ffmpeg.exe"))
        candidates.Add(Path.Combine(appDir, "ffmpeg.exe"))

        Dim parentDir As String = appDir
        For depth As Integer = 1 To 5
            Try
                Dim parent As DirectoryInfo = Directory.GetParent(parentDir)
                If parent Is Nothing Then Exit For
                parentDir = parent.FullName
                candidates.Add(Path.Combine(parentDir, "API-Core", "ffmpeg.exe"))
                candidates.Add(Path.Combine(parentDir, "api-core", "ffmpeg.exe"))
                candidates.Add(Path.Combine(parentDir, "ffmpeg.exe"))
            Catch
                Exit For
            End Try
        Next

        Try
            Dim engineProjDir As String = appDir
            For depth As Integer = 1 To 6
                Dim parent As DirectoryInfo = Directory.GetParent(engineProjDir)
                If parent Is Nothing Then Exit For
                engineProjDir = parent.FullName
                Dim overlayBin As String = Path.Combine(parent.FullName, "Overlay", "bin")
                If Directory.Exists(overlayBin) Then
                    For Each configDir As String In {"Release", "Debug"}
                        Dim configPath_ As String = Path.Combine(overlayBin, configDir)
                        If Directory.Exists(configPath_) Then
                            For Each subDir As String In Directory.GetDirectories(configPath_)
                                candidates.Add(Path.Combine(subDir, "api-core", "ffmpeg.exe"))
                                candidates.Add(Path.Combine(subDir, "API-Core", "ffmpeg.exe"))
                            Next
                        End If
                    Next
                    Exit For
                End If
            Next
        Catch
        End Try

        ' VALIDATED resolution (FFmpegLocator): File.Exists alone once let a
        ' corrupt {bin}\FFmpeg\ffmpeg.exe dead-end every lookup with
        ' Win32Exception 193 while a good API-Core\ffmpeg.exe sat one
        ' candidate later. Each candidate now must actually RUN (-version
        ' probe, cached per file version) before it is accepted.
        Return FFmpegLocator.FirstUsableFFmpeg(candidates, Nothing)
    End Function

    Private Shared Function JsonEscape(s As String) As String
        If String.IsNullOrEmpty(s) Then Return ""
        Return s.Replace("\", "\\").Replace("""", "\""")
    End Function

    Public Shared Function CreateDefault(configPath As String) As CaptureSettings
        Dim settings As New CaptureSettings()
        settings.FFmpegPath = FindFFmpegPath()

        If String.IsNullOrEmpty(settings.OutputDirectory) Then
            settings.OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ShadowPlay Recordings")
        End If

        If Not Directory.Exists(settings.OutputDirectory) Then
            Directory.CreateDirectory(settings.OutputDirectory)
        End If

        Return settings
    End Function

    ' ── Output directory (stored in engine.json or config.json) ──
    Private _outputDirectory As String = ""
    Public Property OutputDirectory As String
        Get
            Return _outputDirectory
        End Get
        Set(value As String)
            _outputDirectory = value
        End Set
    End Property

    Public Function GetCaptureResolution() As System.ValueTuple(Of Integer, Integer)
        If UseNativeResolution OrElse CustomWidth = 0 OrElse CustomHeight = 0 Then
            Return (Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height)
        End If
        Return (CustomWidth, CustomHeight)
    End Function

    Public Function GenerateOutputFilename() As String
        Dim timestamp As String = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
        Return Path.Combine(OutputDirectory, "ShadowPlay_" & timestamp & "." & FileFormat)
    End Function

    Public Class ValidationResult
        Public Property Valid As Boolean
        Public Property Message As String
        Public Sub New(valid As Boolean, message As String)
            Me.Valid = valid
            Me.Message = message
        End Sub
    End Class

    Public Function Validate() As ValidationResult
        If FPS < 1 OrElse FPS > 240 Then
            Return New ValidationResult(False, "FPS must be between 1 and 240")
        End If
        If Bitrate < 1000000 Then
            Return New ValidationResult(False, "Bitrate must be at least 1 Mbps")
        End If
        If Bitrate > 200000000 Then
            Return New ValidationResult(False, "Bitrate must not exceed 200 Mbps")
        End If
        Dim validMethods As String() = {"ddagrab", "gdigrab", "gfxcapture"}
        If Not validMethods.Contains(CaptureMethod.ToLower()) Then
            Return New ValidationResult(False, "Invalid capture method. Use: " & String.Join(", ", validMethods))
        End If
        If String.IsNullOrWhiteSpace(Encoder) Then
            Return New ValidationResult(False, "No encoder selected")
        End If
        If String.IsNullOrWhiteSpace(FFmpegPath) OrElse Not File.Exists(FFmpegPath) Then
            Return New ValidationResult(False, "FFmpeg not found at: " & FFmpegPath)
        End If
        If MicCapture AndAlso String.IsNullOrWhiteSpace(MicDeviceName) Then
            Return New ValidationResult(False, "Microphone is enabled but no device name is set. Select a microphone in the Overlay settings.")
        End If
        Return New ValidationResult(True, "")
    End Function

End Class
