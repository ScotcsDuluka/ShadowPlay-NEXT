Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.Configuration
    ''' <summary>
    ''' Migrates legacy 4-file config (config.json + video.json + audio.json + engine.json)
    ''' into the new unified EngineConfigV2 schema.
    '''
    ''' Migration is ONE-WAY: V1 → V2. Round-trip fidelity is best-effort —
    ''' V2 has more fields than V1 (e.g. explicit GOP, bufsize, tune, AQ),
    ''' so V2 saves can preserve V2-only fields that have no V1 representation.
    '''
    ''' The migrator reads the V1 CaptureSettings data shape (not files
    ''' directly) — callers load V1 files themselves and pass the resulting
    ''' V1CaptureSettings to MigrateFromV1().
    ''' </summary>
    Public NotInheritable Class ConfigMigrator
        Private Sub New()
            ' Static helper class — no instances.
        End Sub

        ''' <summary>
        ''' V1 configuration snapshot as loaded by CaptureSettings.Load.
        ''' Mirrors the fields exposed by CaptureSettings (legacy class in
        ''' Engine/Engine/[Capture]/CaptureSettings.vb). Defined here so
        ''' this assembly does not need a ProjectReference to Engine.
        ''' Callers populate it from whatever V1 source they used.
        ''' </summary>
        Public NotInheritable Class V1CaptureSettings
            ' ── video.json (mapped via OverlayConfig.MapEncoderToFfmpeg at Engine side) ──
            Public Property EncoderKey As String = "NVENC_H264"           ' Overlay's symbolic key
            Public Property FFmpegCodec As String = ""                    ' Empty = not yet resolved; migrator will derive from EncoderKey
            Public Property FPS As Integer = 60
            Public Property BitrateBps As Long = 20000000L
            Public Property NvencPreset As Integer = 4                     ' 1-7 (will be migrated to "p1".."p7")
            Public Property UseNativeResolution As Boolean = True
            Public Property CustomWidth As Integer = 0
            Public Property CustomHeight As Integer = 0
            Public Property ReplayDuration As Integer = 60
            Public Property ActivePreset As String = "Medium"

            ' ── audio.json ──
            Public Property SystemAudioCapture As Boolean = True
            Public Property MicCapture As Boolean = False
            Public Property SystemAudioVolume As Single = 1.0F
            Public Property MicVolume As Single = 1.0F
            Public Property MicDeviceName As String = ""
            Public Property MicDeviceId As String = ""
            Public Property AudioTrackMode As Integer = 0                  ' 0=SingleTrack, 1=SeparateTrack

            ' ── engine.json ──
            Public Property CaptureMethod As String = "ddagrab"
            Public Property PixelFormat As String = "nv12"
            Public Property Preset As String = "p4"
            Public Property RateControl As String = "cbr"
            Public Property FileFormat As String = "mp4"
            Public Property FFmpegPath As String = ""
            Public Property HotkeyStart As String = "Control+Shift+F9"
            Public Property HotkeyStop As String = "Control+Shift+F10"
            Public Property HotkeyToggle As String = "Control+Shift+F8"

            ' ── config.json (paths) ──
            Public Property OutputDirectory As String = ""

            ' ── Overlay hotkeys dictionary (optional — from config.json) ──
            Public Property Hotkeys As Dictionary(Of String, String)
        End Class

        ''' <summary>
        ''' Build a fresh EngineConfigV2 from V1 settings.
        '''
        ''' Rules:
        '''   - BitrateBps comes from V1 BitrateBps (already in bps).
        '''     NOTE: legacy CaptureSettings.Bitrate is in bps, but Overlay's
        '''     Recording.Bitrate is in kbps. The Engine converts at SyncWithOverlayConfig.
        '''     This migrator assumes the caller already did that conversion.
        '''   - BufsizeBps = BitrateBps * 2 (FIX for legacy bug where buf was
        '''     declared but unused in NVIDIA branch — V1 actually sent 1× bitrate).
        '''   - Preset uses V1.Preset string ("p4") if present; otherwise falls
        '''     back to converting V1.NvencPreset integer (1-7 → "p1".."p7").
        '''     V2 stores ONLY the string — no duplicate integer source.
        '''   - GOP defaults to FPS (1-second GOP — matches legacy behavior).
        '''   - All NVENC options (tune, AQ, look_ahead, zerolatency, cq) get
        '''     their legacy hardcoded values as defaults so V2 produces
        '''     byte-identical FFmpeg args to V1.
        ''' </summary>
        Public Shared Function MigrateFromV1(v1 As V1CaptureSettings) As EngineConfigV2
            If v1 Is Nothing Then
                Throw New ArgumentNullException(NameOf(v1))
            End If

            Dim v2 As New EngineConfigV2()

            ' ── Video.Capture ──
            v2.Video.Capture.Method = If(String.IsNullOrEmpty(v1.CaptureMethod), "ddagrab", v1.CaptureMethod.ToLowerInvariant())
            v2.Video.Capture.OutputIndex = 0
            v2.Video.Capture.Framerate = Math.Max(1, v1.FPS)

            ' ── Video.Resolution ──
            v2.Video.Resolution.Mode = If(v1.UseNativeResolution, "native", "custom")
            v2.Video.Resolution.Width = If(v1.CustomWidth > 0, v1.CustomWidth, 1920)
            v2.Video.Resolution.Height = If(v1.CustomHeight > 0, v1.CustomHeight, 1080)

            ' ── Video.Encoder ──
            v2.Video.Encoder.Key = If(String.IsNullOrEmpty(v1.EncoderKey), "NVENC_H264", v1.EncoderKey)
            v2.Video.Encoder.FFmpegCodec = If(String.IsNullOrEmpty(v1.FFmpegCodec),
                                              MapEncoderKeyToFfmpeg(v2.Video.Encoder.Key),
                                              v1.FFmpegCodec)

            ' Preset: prefer V1 string ("p4"), fall back to int mapping
            If Not String.IsNullOrEmpty(v1.Preset) AndAlso v1.Preset.StartsWith("p"c) Then
                v2.Video.Encoder.Preset = v1.Preset
            Else
                v2.Video.Encoder.Preset = MapNvencPresetInteger(v1.NvencPreset)
            End If

            ' Legacy hardcoded values
            v2.Video.Encoder.Tune = "ll"
            v2.Video.Encoder.Profile = ""
            v2.Video.Encoder.RateControl = If(String.IsNullOrEmpty(v1.RateControl), "cbr", v1.RateControl.ToLowerInvariant())

            v2.Video.Encoder.BitrateBps = Math.Max(0L, v1.BitrateBps)
            v2.Video.Encoder.MinrateBps = v2.Video.Encoder.BitrateBps
            v2.Video.Encoder.MaxrateBps = v2.Video.Encoder.BitrateBps

            ' FIX: legacy declared buf = bitrate*2 but used br (= 1× bitrate).
            '      V2 uses the CORRECT value: bitrate * 2.
            v2.Video.Encoder.BufsizeBps = v2.Video.Encoder.BitrateBps * 2L

            ' GOP = FPS (1-second GOP — matches legacy)
            v2.Video.Encoder.GopSize = v2.Video.Capture.Framerate

            v2.Video.Encoder.FpsMode = "cfr"

            ' Legacy NVENC hardcoded values
            v2.Video.Encoder.SpatialAQ = 1
            v2.Video.Encoder.TemporalAQ = 1
            v2.Video.Encoder.LookAhead = 0
            v2.Video.Encoder.ZeroLatency = False
            v2.Video.Encoder.Cq = 0
            v2.Video.Encoder.PixelFormat = If(String.IsNullOrEmpty(v1.PixelFormat), "nv12", v1.PixelFormat)

            ' ── Audio ──
            v2.Audio.System.Enabled = v1.SystemAudioCapture
            v2.Audio.System.Volume = ClampSingle(v1.SystemAudioVolume, 0.0F, 2.0F)
            v2.Audio.System.DeviceId = ""
            v2.Audio.System.DeviceName = ""

            v2.Audio.Microphone.Enabled = v1.MicCapture
            v2.Audio.Microphone.Volume = ClampSingle(v1.MicVolume, 0.0F, 2.0F)
            v2.Audio.Microphone.DeviceId = If(v1.MicDeviceId, "")
            v2.Audio.Microphone.DeviceName = If(v1.MicDeviceName, "")

            v2.Audio.Encoding.Codec = "aac"
            v2.Audio.Encoding.BitrateBps = 320000L
            v2.Audio.Encoding.SampleRate = 48000
            v2.Audio.Encoding.Channels = 2
            v2.Audio.Encoding.TrackMode = If(v1.AudioTrackMode = 1, "separate", "single")

            ' Legacy sync thresholds (hardcoded in StressTestRunner.vb)
            v2.Audio.Sync.MaxOffsetSec = 5.0
            v2.Audio.Sync.MinOffsetSec = -2.0
            v2.Audio.Sync.AVSyncToleranceMs = 100

            ' ── Output ──
            v2.Output.Container = If(String.IsNullOrEmpty(v1.FileFormat), "mp4", v1.FileFormat.ToLowerInvariant())
            v2.Output.Directory = If(v1.OutputDirectory, "")
            v2.Output.FilenamePattern = "ShadowPlay_{timestamp}.{ext}"
            v2.Output.Overwrite = True
            v2.Output.FastStart = True
            v2.Output.TempVideoSuffix = ".video.tmp.mp4"
            v2.Output.TempAudioSuffix = ".system.tmp.wav"
            v2.Output.TempMicSuffix = ".mic.tmp.wav"

            ' ── Runtime ──
            If v1.Hotkeys IsNot Nothing AndAlso v1.Hotkeys.Count > 0 Then
                ' Use V1 dict if provided (config.json hotkeys)
                v2.Runtime.Hotkeys = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For Each kvp As KeyValuePair(Of String, String) In v1.Hotkeys
                    v2.Runtime.Hotkeys(kvp.Key) = kvp.Value
                Next
            Else
                ' Fall back to V1 engine.json hotkeys
                v2.Runtime.Hotkeys("ManualRecordToggle") = If(v1.HotkeyToggle, "Control+Shift+F8")
            End If

            v2.Runtime.FFmpegPath = If(v1.FFmpegPath, "")
            v2.Runtime.FFprobePath = "" ' Will be resolved at load time (same dir as FFmpegPath)
            v2.Runtime.ProcessPriority = "AboveNormal"
            v2.Runtime.LogLevel = "info"
            v2.Runtime.LogToFile = True
            v2.Runtime.LogPath = ""
            v2.Runtime.DebugMode = False
            v2.Runtime.AutoStartOverlay = False

            ' Legacy hardcoded shutdown timeouts
            v2.Runtime.ShutdownTimeoutMs.FFmpegQuit = 10000
            v2.Runtime.ShutdownTimeoutMs.MuxWait = 60000
            v2.Runtime.ShutdownTimeoutMs.FFprobeWait = 5000

            ' ── Experimental (off by default) ──
            v2.Experimental.EnableZeroCopy = False
            v2.Experimental.EnableD3D11Interop = False

            Return v2
        End Function

        ''' <summary>Map Overlay's symbolic encoder key to FFmpeg codec name.</summary>
        Public Shared Function MapEncoderKeyToFfmpeg(encoderKey As String) As String
            If String.IsNullOrEmpty(encoderKey) Then Return "h264_nvenc"
            Select Case encoderKey.ToUpperInvariant()
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

        ''' <summary>Map NVENC preset integer 1-7 → "p1".."p7".</summary>
        Public Shared Function MapNvencPresetInteger(presetNum As Integer) As String
            If presetNum < 1 OrElse presetNum > 7 Then Return "p4"
            Return "p" & presetNum.ToString()
        End Function

        Private Shared Function ClampSingle(value As Single, min As Single, max As Single) As Single
            If value < min Then Return min
            If value > max Then Return max
            Return value
        End Function
    End Class
End Namespace
