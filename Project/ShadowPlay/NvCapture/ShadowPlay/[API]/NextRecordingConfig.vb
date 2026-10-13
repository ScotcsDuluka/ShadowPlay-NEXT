Option Strict On
Option Explicit On
Option Infer On

' NextRecordingConfig.vb
'
' PHASE 0 — CONFIG TRUTH (audit task 23 → fix wave task 24).
'
' Single seam for "what the NEXT recording will use". The SessionConfig
' mapping used to live inline in HandleRecordingStart
' (RecordingEngineHost.vb:214-228 pre-fix) where it read UI_Engine._settings
' — a CaptureSettings loaded ONCE at form init (UI_Engine.vb:616, called
' from :85). Result (audit verdict): a setting changed after process start
' never reached the new-engine path — "UI setting = persisted config =
' effective runtime config" did not hold.
'
' FIX-1 wires HandleRecordingStart through THIS class:
'   LoadEffectiveSettings() = fresh reload from disk (the same fresh-reload
'   the legacy path has always done — HandleEngineRecordStart,
'   UI_Engine.vb:369-371). Inside CaptureSettings.Load the unified Overlay
'   config.json WINS (CaptureSettings.vb:101-116), so the reloaded object
'   IS the effective config.
'
' MapSessionConfig is a VERBATIM move of the pre-fix inline mapping — the
' fix changes WHERE the settings come from, NOT the mapping semantics
' (field-for-field identical, including the Nothing-settings fallbacks).
'
' Compiled by BOTH the Engine project (Windows, root namespace
' NVIDIA_Capture) and Engine.ConfigTruth.Tests (Linux, linked source) —
' all cross-type references are therefore unqualified/global, and the file
' must stay free of WinForms dependencies.

Imports System.Diagnostics
Imports CaptureEngine.Recording

Public NotInheritable Class NextRecordingConfig

    ''' <summary>
    ''' Engine's config path anchor — the SAME expression UI_Engine uses at
    ''' form load (UI_Engine.vb:76: _configPath = AppLayout.P("Config",
    ''' "engine.json")). CaptureSettings.Load derives the legacy-tier base
    ''' dir from it; the unified Overlay config.json is resolved
    ''' independently by OverlayConfig.ConfigDir.
    ''' </summary>
    Public Shared Function EngineConfigPath() As String
        Return AppLayout.P("Config", "engine.json")
    End Function

    ''' <summary>
    ''' FIX-1: fresh reload of the persisted config. Unified Overlay
    ''' config.json wins inside CaptureSettings.Load (engine.json → unified
    ''' WINS + early return → legacy video/audio.json tier only when no
    ''' config.json exists anywhere). Returns a NEW CaptureSettings every
    ''' call — callers must not cache it past one recording start.
    ''' </summary>
    Public Shared Function LoadEffectiveSettings(Optional configPath As String = Nothing) As CaptureSettings
        Return CaptureSettings.Load(If(String.IsNullOrEmpty(configPath), EngineConfigPath(), configPath))
    End Function

    ''' <summary>
    ''' The SessionConfig the NEXT recording runs with: effective settings
    ''' (fresh reload) mapped verbatim. This is the CT-4 contract entry —
    ''' Engine.ConfigTruth.Tests executes exactly this composition.
    ''' </summary>
    Public Shared Function BuildSessionConfig(outputPath As String,
                                              ffmpegPath As String,
                                              onProcessStarted As Action(Of Process),
                                              Optional configPath As String = Nothing) As SessionConfig
        Return MapSessionConfig(LoadEffectiveSettings(configPath), outputPath, ffmpegPath, onProcessStarted)
    End Function

    ''' <summary>
    ''' VERBATIM move of the SessionConfig mapping that was inline in
    ''' HandleRecordingStart (RecordingEngineHost.vb:214-228 pre-fix).
    ''' DurationSeconds=3600 ("no fixed duration — stop via command") and
    ''' every Nothing-settings fallback are preserved exactly; only the
    ''' host hook became a parameter so the mapping is testable without a
    ''' WinForms form.
    '''
    ''' PHASE 1 VIDEO RUNTIME WIRING (V-CT1): TargetFps maps the canonical
    ''' config.json Recording.current.fps (CaptureSettings.FPS after the
    ''' unified apply). Display refresh rate is NEVER consulted here —
    ''' the capture backend's refresh rate is diagnostics-only.
    ''' </summary>
    Public Shared Function MapSessionConfig(settings As CaptureSettings,
                                            outputPath As String,
                                            ffmpegPath As String,
                                            onProcessStarted As Action(Of Process)) As SessionConfig
        ' ★ C/5 H1: every RECORD_START path crosses this seam — reject here
        ' what can only be argument-injection, before any SessionConfig (and
        ' therefore any FFmpeg command line) can exist.
        If Not IsSafeRecordingOutputPath(outputPath) Then
            Throw New ArgumentException(
                "RECORD_START rejected: output path is empty or contains a quote/control character " &
                "that cannot occur in a real file path and would break FFmpeg argument parsing.",
                NameOf(outputPath))
        End If
        Return New SessionConfig() With {
            .OutputPath = outputPath,
            .DurationSeconds = 3600,          ' no fixed duration — stop via command
            .FFmpegPath = ffmpegPath,
            .TargetFps = If(settings IsNot Nothing AndAlso settings.FPS > 0, settings.FPS, 0),
            .AudioEnabled = (settings Is Nothing) OrElse settings.SystemAudioCapture,
            .SystemVolume = If(settings IsNot Nothing, settings.SystemAudioVolume, 1.0F),
            .MicEnabled = (settings IsNot Nothing) AndAlso settings.MicCapture,
            .MicVolume = If(settings IsNot Nothing, settings.MicVolume, 1.0F),
            .MicDeviceId = If(settings IsNot Nothing, settings.MicDeviceId, ""),
            .MicDeviceName = If(settings IsNot Nothing, settings.MicDeviceName, ""),
            .MicSeparateTracks = (settings IsNot Nothing) AndAlso
                                 settings.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SeparateTrack,
            .AudioClockMode = If(settings IsNot Nothing, settings.AudioClockMode, "Legacy"),
            .OnProcessStarted = onProcessStarted
        }
    End Function

    ''' <summary>
    ''' ★ C/5 H1 — output-path security contract (product design, not invented):
    '''
    ''' The output DIRECTORY authority is the USER (config.json
    ''' Paths.GalleryPath/SavePath is a user-settable arbitrary directory and
    ''' the Overlay sends per-recording paths under it). Absolute paths, other
    ''' drives, UNC shares, traversal relative to the chosen root, spaces and
    ''' unicode are BY DESIGN and must stay accepted.
    '''
    ''' What can never occur in a legitimate Windows file path is a double
    ''' quote or a control character (NTFS forbids both in names). Their only
    ''' possible effect is Windows argument-parsing breakout when the path is
    ''' embedded into the FFmpeg command line — every production builder
    ''' quotes but does not escape: LiveMuxSession BuildArgs()/RunRemux()
    ''' (""{_fragPath}"" / ""{_finalPath}"") and legacy FFmpegArgumentBuilder
    ''' (-y ""{outputFile}""). A breakout injects extra FFmpeg outputs/flags
    ''' = arbitrary file write via the -y overwrite path. Such strings are
    ''' therefore rejected here, the single seam every RECORD_START crosses.
    ''' </summary>
    Public Shared Function IsSafeRecordingOutputPath(path As String) As Boolean
        If String.IsNullOrWhiteSpace(path) Then Return False
        For Each ch As Char In path
            If ch = ControlChars.Quote Then Return False
            If ch < " "c Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' PHASE 1 VIDEO RUNTIME WIRING: the EngineStartupConfig the engine
    ''' initializes its persistent encoder with, mapped from the effective
    ''' (fresh-reload) CaptureSettings. Extracted from the inline mapping in
    ''' InitializeRecordingEngine (RecordingEngineHost.vb:78-105) so the
    ''' startup mapping is testable on Linux (same pattern as CT-4).
    '''
    ''' OWNERSHIP (docs/CONFIG_OWNERSHIP_MATRIX.md):
    '''   CodecKey     ← config.json Recording.encoder (normalized both ways)
    '''   BitrateBps   ← config.json Recording.current.bitrate (kbps→bps in loader)
    '''   Fps          ← config.json Recording.current.fps
    '''   RateControl  ← engine.json RateControl (owner unchanged — do NOT delete)
    '''   Preset       ← config.json Recording.current.encoder_preset via the
    '''                  single mapper (engine.json Preset = compat fallback)
    '''   GopSize      ← engine default (60) — NEVER derived from FPS. The
    '''                  pre-wiring host mapped settingsSnapshot.FPS → GOP
    '''                  (RecordingEngineHost.vb:96-98); that accidental
    '''                  coupling is removed by this mapping.
    '''   Resolution   ← config.json Recording.current.use_native_resolution /
    '''                  width / height (V-CT2; matrix rows 🔴 ต้อง wire)
    ''' </summary>
    Public Shared Function MapStartupConfig(settings As CaptureSettings) As EngineStartupConfig
        Dim startup As New EngineStartupConfig()
        If settings IsNot Nothing Then
            If Not String.IsNullOrEmpty(settings.Encoder) Then
                ' Fallback-fix (verbatim from the pre-extraction host code):
                ' CaptureSettings.Encoder may hold an FFmpeg name
                ' ('h264_nvenc') — the encoder contract wants the internal
                ' key ('NVENC_H264'). Normalize BOTH directions.
                startup.CodecKey = OverlayConfig.MapEncoderToInternal(settings.Encoder)
            End If
            If settings.Bitrate > 0 Then
                startup.BitrateBps = settings.Bitrate
            End If
            If settings.FPS > 0 Then
                startup.Fps = settings.FPS          ' V-CT1: FPS lives here — NOT in GopSize
            End If
            startup.RateControl = EngineStartupConfig.ResolveRateControl(settings.RateControl)
            startup.CaptureCursor = settings.CaptureCursor

            ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT4): preset unification.
            ' ONE canonical source: config.json Recording.current.encoder_preset
            ' (CaptureSettings.NvencPreset, int 1-7) — mapped with THE existing
            ' single mapper (ConfigMigrator.MapNvencPresetInteger, 1-7 →
            ' "p1".."p7", invalid → "p4"). The engine.json Preset string is
            ' kept ONLY as a compat fallback while the field has no config
            ' value (ownership matrix §8 item 3 — duplicates die LATER, after
            ' real-record evidence; engine.json is never deleted this phase).
            If settings.NvencPreset >= 1 AndAlso settings.NvencPreset <= 7 Then
                ' Global. prefix — the legacy CaptureEngine CLASS shadows the
                ' CaptureEngine namespace inside the Engine project compile.
                startup.Preset = Global.CaptureEngine.Configuration.ConfigMigrator.MapNvencPresetInteger(settings.NvencPreset)
            ElseIf Not String.IsNullOrEmpty(settings.Preset) Then
                startup.Preset = settings.Preset
            End If

            ' V-CT2: resolution group (canonical owner = config.json
            ' Recording.current.*). CaptureSettings.UseNativeResolution
            ' mirrors the config flag; CustomWidth/Height are only populated
            ' by the unified apply when the flag is False (OverlayConfig
            ' ApplyUnifiedToCaptureSettings :450-454).
            startup.UseNativeResolution = settings.UseNativeResolution
            If Not settings.UseNativeResolution Then
                startup.RequestedWidth = settings.CustomWidth
                startup.RequestedHeight = settings.CustomHeight
            End If

            ' Capture method + pixel format: evidence plumbing (task §8/§9 —
            ' requested→selected→actual must be traceable, never silently
            ' substituted).
            startup.RequestedCaptureMethod = If(settings.CaptureMethod, "")
            startup.RequestedPixelFormat = If(settings.PixelFormat, "")
        End If
        Return startup
    End Function

End Class
