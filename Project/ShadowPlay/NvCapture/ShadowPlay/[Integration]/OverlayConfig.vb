' OverlayConfig.vb
' Engine-side mirror of Overlay's config.json (+ legacy video.json).
'
' Why this exists:
'   The Overlay is the source of truth for capture settings. It owns
'   config.json — THE single user-facing config file since the GLM/6
'   unification (its Recording section is stored in the nested
'   video.json-shaped layout: current / my_presets). The Engine used to
'   have its own shadowplay-config.json that drifted out of sync with
'   Overlay's config — users would change a setting in the Overlay and
'   the Engine would still use the old value.
'
'   This module reads the SAME file the Overlay reads, using the SAME
'   schema — both generations of it: the nested Recording section
'   (current files) and the flat Recording keys (legacy files from
'   before the detailed schema, read until the Overlay upgrades them).
'   The Engine's UI displays the live values from Overlay's config, so
'   the user can see at a glance what the Engine will actually use when
'   RECORD_START comes in.
'
' File locations:
'   Engine looks for config.json (+ a legacy video.json next to it) in
'   this order:
'     1. Path stored in <appdir>/overlay-config-path.txt (if present)
'     2. <appdir>/config.json (if Engine and Overlay share an output
'        folder)
'     3. <appdir>/../../Overlay/bin/Release/<tfm>/config.json
'        (when running from source)
'   The path that was found is cached in _resolvedConfigDir so all
'   subsequent reads use the same location.

Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization

Public NotInheritable Class OverlayConfig

#Region "Schema — video.json (matches Overlay's VideoConfigClass)"

    Public Class VideoCurrentValues
        Public Property fps As Integer = 60
        Public Property bitrate As Integer = 17000
        Public Property encoder_preset As Integer = 4
        Public Property use_native_resolution As Boolean = True
        Public Property width As Integer = 1920
        Public Property height As Integer = 1080
    End Class

    Public Class VideoAudioConfig
        Public Property system_enabled As Boolean = True
        Public Property mic_enabled As Boolean = False
        Public Property system_volume As Single = 1.0F
        Public Property mic_volume As Single = 1.0F
        Public Property mic_device As String = ""
        Public Property mic_device_id As String = ""
    End Class

    Public Class VideoMyPresetSlot
        Public Property fps As Integer?
        Public Property bitrate As Integer?
        Public Property encoder_preset As Integer?
    End Class

    Public Class VideoMyPresets
        Public Property name As String = "MY"
        ' ✅ m11 FIX: initialize with New so callers don't NRE if they
        ' access my_presets.low.fps when the JSON didn't include a slot.
        Public Property low As VideoMyPresetSlot = New VideoMyPresetSlot()
        Public Property medium As VideoMyPresetSlot = New VideoMyPresetSlot()
        Public Property high As VideoMyPresetSlot = New VideoMyPresetSlot()
    End Class

    Public Class VideoConfig
        Public Property encoder As String = "NVENC_H264"
        Public Property encoder_now As String = "NVENC_H264"
        Public Property active_preset As String = "Medium"
        Public Property current As VideoCurrentValues = New VideoCurrentValues()
        Public Property replay_duration As Integer = 60
        Public Property audio As VideoAudioConfig = New VideoAudioConfig()
        Public Property my_presets As VideoMyPresets = New VideoMyPresets()
        Public Property engine_mode As String = Nothing
        Public Property api_capture As String = Nothing
    End Class

#End Region

#Region "Schema — config.json (matches Overlay's AppSettings)"

    Public Class RecordingSettings
        Public Property UseNativeResolution As Boolean = True
        Public Property Encoder As String = "NVENC_H264"
        Public Property EncoderNow As String = "NVENC_H264"
        Public Property FPS As Integer = 60
        Public Property Bitrate As Integer = 17000
        Public Property Width As Integer = 1920
        Public Property Height As Integer = 1080
        Public Property Preset As String = "Medium"
        Public Property EncoderPreset As Integer = 4
        Public Property ReplayDuration As Integer = 60
        Public Property MyLowFPS As Integer?
        Public Property MyLowBitrate As Integer?
        Public Property MyLowEncoderPreset As Integer?
        Public Property MyMediumFPS As Integer?
        Public Property MyMediumBitrate As Integer?
        Public Property MyMediumEncoderPreset As Integer?
        Public Property MyHighFPS As Integer?
        Public Property MyHighBitrate As Integer?
        Public Property MyHighEncoderPreset As Integer?
        Public Property MyPresetName As String = "MY"
        Public Property EngineMode As String = Nothing
        Public Property APICapture As String = Nothing
    End Class

    Public Class PathSettings
        Public Property GalleryPath As String = ""
        Public Property SavePath As String = ""
        Public Property FFmpegPath As String = ""
    End Class

    Public Class UISettings
        Public Property Language As String = "en-US"
        Public Property Theme As String = "Dark"
    End Class

    Public Class AudioSettings
        Public Property SystemAudioEnabled As Boolean = True
        Public Property MicEnabled As Boolean = False
        Public Property SystemAudioVolume As Single = 1.0F
        Public Property MicVolume As Single = 1.0F
        Public Property MicDeviceName As String = ""
        ' Unified config.json (GLM/6): matches Overlay AudioSettingsClass
        Public Property MicDeviceId As String = ""
        Public Property TrackMode As Integer = 0
        ' ★ P13.4 plumbing fix (unified-config gap): the P13.3/13.4 Device-clock
        ' A/B flag rides the ONE config file now. "Device" = hardware-stamped
        ' system-audio timeline, "Legacy" = proven v2 tap. Normalized on apply
        ' (ApplyUnifiedToCaptureSettings) — anything but "Device" means Legacy.
        ' Temporary knob: the whole flag is deleted at P13.5.
        Public Property AudioClockMode As String = "Legacy"
    End Class

    Public Class GitHubUser
        Public Property Username As String = ""
        Public Property AvatarUrl As String = ""
        Public Property IsLoggedIn As Boolean = False
        Public Property LastLogin As DateTime = DateTime.MinValue
    End Class

    Public Class AppConfig
        Public Property GitHubUser As GitHubUser = New GitHubUser()
        ' ✅ P2: read GitHubTokenEncrypted (DPAPI) but we don't decrypt it
        ' here — Engine doesn't need to use the token, just to display
        ' login status. The Overlay handles all OAuth.
        Public Property GitHubTokenEncrypted As String = ""
        Public Property Recording As RecordingSettings = New RecordingSettings()
        Public Property Paths As PathSettings = New PathSettings()
        Public Property UI As UISettings = New UISettings()
        Public Property Audio As AudioSettings = New AudioSettings()
        Public Property Hotkeys As Dictionary(Of String, String)
        Public Property VideoConfigPath As String = ""
    End Class

#End Region

#Region "Path resolution"

    Private Shared _resolvedConfigDir As String = Nothing
    Private Shared _resolvedVideoConfigPath As String = Nothing
    Private Shared _resolvedConfigPath As String = Nothing
    Private Shared ReadOnly _resolveLock As New Object()

    ' Nested (video.json-shaped) Recording section of the LAST loaded
    ' config.json — Nothing when the file is the legacy flat shape.
    ' The flat RecordingSettings mirror above binds nothing from a nested
    ' file, so ApplyUnifiedToCaptureSettings reads values from here first.
    Private Shared _lastNestedRecording As VideoConfig = Nothing

    ''' <summary>
    ''' Find the directory that contains Overlay's config.json + video.json.
    ''' Cached after first successful resolution. Returns "" if not found.
    ''' </summary>
    Public Shared ReadOnly Property ConfigDir As String
        Get
            If _resolvedConfigDir IsNot Nothing Then Return _resolvedConfigDir
            SyncLock _resolveLock
                If _resolvedConfigDir IsNot Nothing Then Return _resolvedConfigDir
                _resolvedConfigDir = ResolveConfigDir()
                If _resolvedConfigDir.Length > 0 Then
                    _resolvedConfigPath = Path.Combine(_resolvedConfigDir, "config.json")
                    _resolvedVideoConfigPath = Path.Combine(_resolvedConfigDir, "video.json")
                End If
                Return _resolvedConfigDir
            End SyncLock
        End Get
    End Property

    Public Shared ReadOnly Property ConfigPath As String
        Get
            If ConfigDir.Length = 0 Then Return ""
            Return _resolvedConfigPath
        End Get
    End Property

    Public Shared ReadOnly Property VideoConfigPath As String
        Get
            If ConfigDir.Length = 0 Then Return ""
            Return _resolvedVideoConfigPath
        End Get
    End Property

    Public Shared Sub ResetResolvedPath()
        SyncLock _resolveLock
            _resolvedConfigDir = Nothing
            _resolvedConfigPath = Nothing
            _resolvedVideoConfigPath = Nothing
            _lastNestedRecording = Nothing
        End SyncLock
    End Sub

    Private Shared Function ResolveConfigDir() As String
        Dim appDir As String = AppLayout.Dir

        ' 1. Explicit override file: <root>/Config/overlay-config-path.txt
        '    (legacy: same probe next to the exe) — single line containing
        '    the absolute path to the folder holding config.json.
        Dim overrideFile As String = Path.Combine(appDir, "Config", "overlay-config-path.txt")
        If Not File.Exists(overrideFile) Then
            overrideFile = Path.Combine(appDir, "overlay-config-path.txt")
        End If
        If File.Exists(overrideFile) Then
            Try
                Dim overridePath As String = File.ReadAllText(overrideFile).Trim()
                If overridePath.Length > 0 AndAlso File.Exists(Path.Combine(overridePath, "config.json")) Then
                    Return overridePath
                End If
            Catch
            End Try
        End If

        ' 2. ROOT-FIXED LAYOUT: the unified tree keeps config at <root>\Config
        Dim layoutConfigDir As String = Path.Combine(appDir, "Config")
        If File.Exists(Path.Combine(layoutConfigDir, "config.json")) Then
            Return layoutConfigDir
        End If

        ' 2b. Same folder as Engine (legacy dev layout: config.json next to exe)
        If File.Exists(Path.Combine(appDir, "config.json")) Then
            Return appDir
        End If

        ' 3. Walk up to find sibling Overlay/bin folder
        Dim cur As String = appDir
        For depth As Integer = 1 To 8
            Try
                Dim parent As DirectoryInfo = Directory.GetParent(cur)
                If parent Is Nothing Then Exit For
                cur = parent.FullName

                Dim overlayBin As String = Path.Combine(cur, "Overlay", "bin")
                If Directory.Exists(overlayBin) Then
                    For Each configName As String In {"Release", "Debug"}
                        Dim configDir As String = Path.Combine(overlayBin, configName)
                        If Not Directory.Exists(configDir) Then Continue For
                        ' Walk TFM subfolders (net8.0-windows10.0.26100.0 etc.)
                        For Each subDir As String In Directory.GetDirectories(configDir)
                            If File.Exists(Path.Combine(subDir, "config.json")) Then
                                Return subDir
                            End If
                        Next
                    Next
                End If
            Catch
                Exit For
            End Try
        Next

        ' 4. Fallback: Engine's own folder (will create defaults there)
        Return appDir
    End Function

#End Region

#Region "Loaders"

    Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .AllowTrailingCommas = True,
        .ReadCommentHandling = JsonCommentHandling.Skip,
        .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    }

    ''' <summary>Load Overlay's config.json. Returns Nothing on failure.</summary>
    Public Shared Function LoadConfig() As AppConfig
        _lastNestedRecording = Nothing
        Dim p As String = ConfigPath
        If p.Length = 0 OrElse Not File.Exists(p) Then Return New AppConfig()
        Try
            Dim json As String = File.ReadAllText(p)
            If String.IsNullOrWhiteSpace(json) Then Return New AppConfig()
            Dim cfg As AppConfig = JsonSerializer.Deserialize(Of AppConfig)(json, _jsonOpts)
            ' Nested-shape upgrade: when the Recording section is stored
            ' video.json-shaped (current / my_presets) the flat
            ' RecordingSettings mirror above binds nothing — parse the
            ' section into VideoConfig so ApplyUnifiedToCaptureSettings has
            ' the real values. Nothing = legacy flat file (fields bound
            ' normally into cfg.Recording).
            _lastNestedRecording = TryParseNestedRecording(json)
            Return cfg
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"OverlayConfig.LoadConfig error: {ex.Message}")
            Return New AppConfig()
        End Try
    End Function

    ''' <summary>
    ''' Parse the nested (video.json-shaped) Recording section out of
    ''' config.json. Returns Nothing when the file is the legacy flat shape
    ''' or unreadable. The section shape matches VideoConfig 1:1 (same
    ''' snake_case keys the old video.json used).
    ''' </summary>
    Private Shared Function TryParseNestedRecording(json As String) As VideoConfig
        Try
            Dim docOpts As New JsonDocumentOptions With {
                .AllowTrailingCommas = True,
                .CommentHandling = JsonCommentHandling.Skip
            }
            Using doc As JsonDocument = JsonDocument.Parse(json, docOpts)
                Dim root As JsonElement = doc.RootElement
                If root.ValueKind <> JsonValueKind.Object Then Return Nothing
                Dim sec As JsonElement
                If Not root.TryGetProperty("Recording", sec) AndAlso
                   Not root.TryGetProperty("recording", sec) Then Return Nothing
                If sec.ValueKind <> JsonValueKind.Object Then Return Nothing
                Dim cur As JsonElement
                If Not sec.TryGetProperty("current", cur) Then Return Nothing
                If cur.ValueKind <> JsonValueKind.Object Then Return Nothing
                Return JsonSerializer.Deserialize(Of VideoConfig)(sec.GetRawText(), _jsonOpts)
            End Using
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"OverlayConfig.TryParseNestedRecording error: {ex.Message}")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' Load Overlay's video settings. W2-7: config.json is the ONE
    ''' user-facing config file and is tried FIRST (it used to lose to a
    ''' stale legacy video.json whenever that file still existed — the
    ''' Engine UI mirror then showed outdated values while the runtime ran
    ''' config.json via ApplyUnifiedToCaptureSettings). Legacy video.json is
    ''' now only a fallback for old installs whose config.json is missing or
    ''' has no usable Recording section. Always returns a usable VideoConfig.
    ''' </summary>
    Public Shared Function LoadVideoConfig() As VideoConfig
        ' 1) config.json — authoritative (nested Recording + top-level Audio)
        Dim cp As String = ConfigPath
        If cp.Length > 0 AndAlso File.Exists(cp) Then
            Try
                Dim json As String = File.ReadAllText(cp)
                If Not String.IsNullOrWhiteSpace(json) Then
                    Dim vc As VideoConfig = TryParseNestedRecording(json)
                    If vc IsNot Nothing Then
                        MapTopLevelAudioIntoVideoConfig(json, vc)
                        Return vc
                    End If
                    ' No usable Recording.current in config.json → fall
                    ' through to the legacy file below.
                End If
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"OverlayConfig.LoadVideoConfig(config.json) error: {ex.Message}")
            End Try
        End If

        ' 2) legacy video.json — old installs only (stale by definition)
        Dim p As String = VideoConfigPath
        If p.Length > 0 AndAlso File.Exists(p) Then
            Try
                Dim json As String = File.ReadAllText(p)
                If Not String.IsNullOrWhiteSpace(json) Then
                    Return JsonSerializer.Deserialize(Of VideoConfig)(json, _jsonOpts)
                End If
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"OverlayConfig.LoadVideoConfig(video.json) error: {ex.Message}")
                Return New VideoConfig()
            End Try
        End If

        Return New VideoConfig()
    End Function

    ''' <summary>
    ''' The unified config keeps Audio as its OWN top-level section (flat
    ''' PascalCase keys) — map it onto the nested VideoAudioConfig fields so
    ''' consumers of the video-shaped view (Engine UI mirror) see real
    ''' values. Skipped silently when the Audio section is absent.
    ''' </summary>
    Private Shared Sub MapTopLevelAudioIntoVideoConfig(configJson As String, vc As VideoConfig)
        Try
            Dim docOpts As New JsonDocumentOptions With {
                .AllowTrailingCommas = True,
                .CommentHandling = JsonCommentHandling.Skip
            }
            Using doc As JsonDocument = JsonDocument.Parse(configJson, docOpts)
                Dim root As JsonElement = doc.RootElement
                If root.ValueKind <> JsonValueKind.Object Then Return
                Dim aEl As JsonElement
                If Not root.TryGetProperty("Audio", aEl) AndAlso
                   Not root.TryGetProperty("audio", aEl) Then Return
                If aEl.ValueKind <> JsonValueKind.Object Then Return
                Dim a As AudioSettings = JsonSerializer.Deserialize(Of AudioSettings)(aEl.GetRawText(), _jsonOpts)
                If a Is Nothing Then Return
                If vc.audio Is Nothing Then vc.audio = New VideoAudioConfig()
                vc.audio.system_enabled = a.SystemAudioEnabled
                vc.audio.mic_enabled = a.MicEnabled
                vc.audio.system_volume = a.SystemAudioVolume
                vc.audio.mic_volume = a.MicVolume
                vc.audio.mic_device = a.MicDeviceName
                vc.audio.mic_device_id = a.MicDeviceId
            End Using
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"OverlayConfig.MapTopLevelAudioIntoVideoConfig error: {ex.Message}")
        End Try
    End Sub

    ''' <summary>Quick check: do the Overlay config files exist?</summary>
    Public Shared ReadOnly Property IsAvailable As Boolean
        Get
            Return ConfigPath.Length > 0 AndAlso File.Exists(ConfigPath)
        End Get
    End Property

    ''' <summary>
    ''' Resolve the persisted engine regime from the same unified config used by
    ''' the Overlay. Nested Recording.engine_mode is authoritative; legacy flat
    ''' Recording.EngineMode is accepted for backward compatibility. Missing or
    ''' unknown values fall back to the historical api_capture inference.
    ''' </summary>
    Public Shared Function GetEngineMode() As String
        Try
            Dim cp As String = ConfigPath
            If cp.Length > 0 AndAlso File.Exists(cp) Then
                Dim nested As VideoConfig = LoadVideoConfig()
                If nested IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(nested.engine_mode) Then
                    Dim mode As String = nested.engine_mode.Trim().ToLowerInvariant()
                    If mode = "ffmpeg" OrElse mode = "legacy" Then Return "ffmpeg"
                    ' "nvidia"/"shadowplay" = drive the installed NVIDIA
                    ' ShadowPlay recorder (nvspcap64) instead of any in-process
                    ' capture — full ShadowPlay feature set on GFE machines.
                    If mode = "nvidia" OrElse mode = "shadowplay" Then Return "nvidia"
                    ' "native" is part of the Overlay's accepted Duluka
                    ' vocabulary (AppSettings.NormalizeEngineMode) and of the
                    ' documented regime table — rejecting it here makes the
                    ' Engine run legacy while the Overlay UI shows Duluka.
                    If mode = "duluka" OrElse mode = "ddagrab" OrElse mode = "native" Then Return "ddagrab"
                End If

                Dim cfg As AppConfig = LoadConfig()
                If cfg IsNot Nothing AndAlso cfg.Recording IsNot Nothing AndAlso
                   Not String.IsNullOrWhiteSpace(cfg.Recording.EngineMode) Then
                    Dim mode As String = cfg.Recording.EngineMode.Trim().ToLowerInvariant()
                    If mode = "ffmpeg" OrElse mode = "legacy" Then Return "ffmpeg"
                    If mode = "nvidia" OrElse mode = "shadowplay" Then Return "nvidia"
                    If mode = "duluka" OrElse mode = "ddagrab" OrElse mode = "native" Then Return "ddagrab"
                End If

                Dim api As String = If(cfg?.Recording?.APICapture, "").Trim().ToLowerInvariant()
                Return If(api = "ddagrab", "ddagrab", "ffmpeg")
            End If
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"OverlayConfig.GetEngineMode error: {ex.Message}")
        End Try
        Return "ffmpeg"
    End Function

    ''' <summary>
    ''' GLM/6 UNIFIED CONFIG: apply the Overlay's single config.json onto
    ''' CaptureSettings. Returns True when a usable config.json exists
    ''' (config.json is now the ONE user-facing config file; video.json /
    ''' audio.json are legacy fallbacks only).
    '''
    ''' Mapping mirrors the proven SyncWithOverlayConfig semantics:
    '''   - Encoder: Overlay 'NVENC_H264' → FFmpeg 'h264_nvenc'
    '''   - Bitrate: Overlay stores kbps, CaptureSettings expects bps
    '''   - Resolution: UseNativeResolution or custom W/H
    '''   - Audio: incl. MicDeviceId + TrackMode
    '''   - Paths: FFmpegPath (when the file exists), OutputDirectory
    ''' </summary>
    Public Shared Function ApplyUnifiedToCaptureSettings(s As CaptureSettings) As Boolean
        If Not IsAvailable Then Return False

        Dim cfg As AppConfig = LoadConfig()
        If cfg Is Nothing Then Return False

        ' ── Recording: nested shape (current files) ──
        Dim nested As VideoConfig = _lastNestedRecording
        If nested IsNot Nothing Then
            If Not String.IsNullOrEmpty(nested.encoder) Then
                s.Encoder = MapEncoderToFfmpeg(nested.encoder)
            End If
            If nested.current IsNot Nothing Then
                If nested.current.fps > 0 AndAlso nested.current.fps <= 240 Then s.FPS = nested.current.fps
                If nested.current.bitrate > 0 Then s.Bitrate = nested.current.bitrate * 1000L   ' kbps → bps
                ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT4): Recording.current.
                ' encoder_preset (1-7) is the canonical preset owner — map it
                ' into NvencPreset so the engine's preset unification reads
                ' the config value. Pre-wiring this field was parsed into
                ' VideoCurrentValues but NEVER applied here (dead value).
                If nested.current.encoder_preset >= 1 AndAlso nested.current.encoder_preset <= 7 Then
                    s.NvencPreset = nested.current.encoder_preset
                End If
                s.UseNativeResolution = nested.current.use_native_resolution
                If Not nested.current.use_native_resolution Then
                    s.CustomWidth = nested.current.width
                    s.CustomHeight = nested.current.height
                End If
            End If
            If Not String.IsNullOrEmpty(nested.active_preset) Then s.ActivePreset = nested.active_preset
            If nested.replay_duration > 0 Then s.ReplayDuration = nested.replay_duration
            If Not String.IsNullOrEmpty(nested.api_capture) Then
                s.CaptureMethod = nested.api_capture.ToLowerInvariant()
            End If
        Else
            ' ── Recording: legacy flat shape (pre-detailed-schema files) ──
            Dim r As RecordingSettings = cfg.Recording
            If r IsNot Nothing Then
                If Not String.IsNullOrEmpty(r.Encoder) Then
                    s.Encoder = MapEncoderToFfmpeg(r.Encoder)
                End If
                If r.FPS > 0 AndAlso r.FPS <= 240 Then s.FPS = r.FPS
                If r.Bitrate > 0 Then s.Bitrate = r.Bitrate * 1000L   ' kbps → bps
                s.UseNativeResolution = r.UseNativeResolution
                If Not r.UseNativeResolution Then
                    s.CustomWidth = r.Width
                    s.CustomHeight = r.Height
                End If
                ' PHASE 1 (V-CT4): flat-shape parity for the preset owner.
                If r.EncoderPreset >= 1 AndAlso r.EncoderPreset <= 7 Then
                    s.NvencPreset = r.EncoderPreset
                End If
                s.ActivePreset = r.Preset
                s.ReplayDuration = r.ReplayDuration
                If Not String.IsNullOrEmpty(r.APICapture) Then
                    s.CaptureMethod = r.APICapture.ToLowerInvariant()
                End If
            End If
        End If

        Dim a As AudioSettings = cfg.Audio
        If a IsNot Nothing Then
            s.SystemAudioCapture = a.SystemAudioEnabled
            s.MicCapture = a.MicEnabled
            s.SystemAudioVolume = a.SystemAudioVolume
            s.MicVolume = a.MicVolume
            s.MicDeviceName = a.MicDeviceName
            s.MicDeviceId = a.MicDeviceId
            s.AudioTrackMode = If(a.TrackMode = 1,
                                  CaptureSettings.AudioTrackModeEnum.SeparateTrack,
                                  CaptureSettings.AudioTrackModeEnum.SingleTrack)
            ' P13.4 plumbing fix: without this mapping the Device-clock flag was
            ' UNREACHABLE on real installs — CaptureSettings.Load returns right
            ' after ApplyUnifiedToCaptureSettings, so the legacy audio.json
            ' fallback (which did read AudioClockMode) never runs once
            ' config.json exists (i.e. always, on any install with the Overlay).
            Dim clock As String = If(a.AudioClockMode, "").Trim()
            s.AudioClockMode = If(String.Equals(clock, "Device", StringComparison.OrdinalIgnoreCase),
                                  "Device", "Legacy")
            s.AudioCapture = s.SystemAudioCapture OrElse s.MicCapture
        End If

        If cfg.Paths IsNot Nothing Then
            If Not String.IsNullOrEmpty(cfg.Paths.FFmpegPath) AndAlso File.Exists(cfg.Paths.FFmpegPath) Then
                s.FFmpegPath = cfg.Paths.FFmpegPath
            End If
            Dim outDir As String = cfg.Paths.GalleryPath
            If String.IsNullOrEmpty(outDir) Then outDir = cfg.Paths.SavePath
            If Not String.IsNullOrEmpty(outDir) Then s.OutputDirectory = outDir
        End If

        Return True
    End Function

#End Region

#Region "Encoder mapping — Overlay's NVENC_H264 → FFmpeg's h264_nvenc"

    Public Shared Function MapEncoderToFfmpeg(overlayEncoder As String) As String
        If String.IsNullOrEmpty(overlayEncoder) Then Return "h264_nvenc"
        Select Case overlayEncoder.ToUpperInvariant()
            Case "NVENC_H264" : Return "h264_nvenc"
            Case "NVENC_HEVC" : Return "hevc_nvenc"
            Case "NVENC_AV1" : Return "av1_nvenc"
            Case "QUICKSYNC_H264" : Return "h264_qsv"
            Case "QUICKSYNC_HEVC" : Return "hevc_qsv"
            Case "AMF_H264" : Return "h264_amf"
            Case "AMF_HEVC" : Return "hevc_amf"
            Case "LIBX264" : Return "libx264"
            Case "LIBX265" : Return "libx265"
            Case Else : Return "h264_nvenc"
        End Select
    End Function

    ''' <summary>
    ''' Fallback-fix (owner log 2026-08-23 20:01:46: Initialize FAILED CodecKey
    ''' h264_nvenc is not supported). CaptureSettings.Encoder may arrive as a
    ''' FFMPEG codec name (MapEncoderToFfmpeg output / unified config.json feed)
    ''' while the NvencEncoderBackend contract expects the internal key
    ''' (NVENC_H264). Translate both directions so no caller has to know which
    ''' convention the other side uses.
    ''' </summary>
    Public Shared Function MapEncoderToInternal(ffmpegOrOverlayEncoder As String) As String
        If String.IsNullOrEmpty(ffmpegOrOverlayEncoder) Then Return "NVENC_H264"
        Select Case ffmpegOrOverlayEncoder.ToLowerInvariant()
            Case "h264_nvenc" : Return "NVENC_H264"
            Case "hevc_nvenc" : Return "NVENC_HEVC"
            Case "av1_nvenc" : Return "NVENC_AV1"
            Case "h264_qsv" : Return "QUICKSYNC_H264"
            Case "hevc_qsv" : Return "QUICKSYNC_HEVC"
            Case "h264_amf" : Return "AMF_H264"
            Case "hevc_amf" : Return "AMF_HEVC"
            Case "libx264" : Return "LIBX264"
            Case "libx265" : Return "LIBX265"
            Case "nvenc_h264", "nvenc_hevc", "nvenc_av1", "quicksync_h264", "quicksync_hevc", "amf_h264", "amf_hevc"
                Return ffmpegOrOverlayEncoder.ToUpperInvariant()
            Case Else : Return "NVENC_H264"
        End Select
    End Function

    Public Shared Function MapEncoderToLabel(overlayEncoder As String) As String
        If String.IsNullOrEmpty(overlayEncoder) Then Return "NVIDIA NVENC H.264"
        Select Case overlayEncoder.ToUpperInvariant()
            Case "NVENC_H264" : Return "NVIDIA NVENC H.264"
            Case "NVENC_HEVC" : Return "NVIDIA NVENC HEVC"
            Case "NVENC_AV1" : Return "NVIDIA NVENC AV1"
            Case "QUICKSYNC_H264" : Return "Intel QuickSync H.264"
            Case "QUICKSYNC_HEVC" : Return "Intel QuickSync HEVC"
            Case "AMF_H264" : Return "AMD AMF H.264"
            Case "AMF_HEVC" : Return "AMD AMF HEVC"
            Case "LIBX264" : Return "CPU LibX264"
            Case "LIBX265" : Return "CPU LibX265"
            Case Else : Return overlayEncoder
        End Select
    End Function

    ''' <summary>
    ''' NVENC preset p1..p7 mapping. Overlay uses integer 1-7. FFmpeg uses p1..p7.
    ''' </summary>
    Public Shared Function MapQsvPreset(presetNum As Integer) As String
        Select Case presetNum
            Case 1 : Return "veryfast"
            Case 2 : Return "faster"
            Case 3 : Return "fast"
            Case 4 : Return "medium"
            Case 5 : Return "slow"
            Case 6 : Return "slower"
            Case 7 : Return "veryslow"
            Case Else : Return "medium"
        End Select
    End Function

    Public Shared Function MapNvencPreset(presetNum As Integer) As String
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

#End Region

End Class
