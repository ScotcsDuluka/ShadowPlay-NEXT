Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.Configuration.Schema
    ''' <summary>
    ''' EngineConfig v2 — new unified configuration schema for Engine-Rebuild.
    '''
    ''' This is a NEW schema that lives ALONGSIDE the legacy 4-file config
    ''' (config.json + video.json + audio.json + engine.json). It does NOT
    ''' replace the legacy schema. The legacy CaptureSettings class remains
    ''' the production source of truth until Step 5 (Production Validation)
    ''' is complete.
    '''
    ''' Design principles:
    '''   1. Single source of truth — every FFmpeg-affecting setting lives
    '''      here. No duplicates across config.json / video.json / engine.json.
    '''   2. Explicit — no hidden defaults inside BuildFFmpegArguments.
    '''      GOP, bufsize, AQ, tune, profile, pix_fmt are all visible fields.
    '''   3. Backward compatible — ConfigMigrator can build a V2 from V1
    '''      config files with no data loss.
    '''   4. Validation-friendly — every field has a clear valid range that
    '''      ConfigValidator can check.
    ''' </summary>
    Public NotInheritable Class EngineConfigV2
        ''' <summary>Schema version. Bump only on breaking schema change.</summary>
        Public Const SchemaVersion As Integer = 2

        Public Sub New()
            Version = SchemaVersion
            GeneratedAt = DateTime.UtcNow
        End Sub

        Public Property Version As Integer = SchemaVersion

        Public Property GeneratedAt As DateTime = DateTime.UtcNow

        Public Property Video As VideoSection = New VideoSection()

        Public Property Audio As AudioSection = New AudioSection()

        Public Property Output As OutputSection = New OutputSection()

        Public Property Runtime As RuntimeSection = New RuntimeSection()

        Public Property Experimental As ExperimentalSection = New ExperimentalSection()

        ' ──────────────────────────────────────────────────────────
        ' Video section
        ' ──────────────────────────────────────────────────────────

        Public NotInheritable Class VideoSection
            Public Property Capture As CaptureSubSection = New CaptureSubSection()
            Public Property Resolution As ResolutionSubSection = New ResolutionSubSection()
            Public Property Encoder As EncoderSubSection = New EncoderSubSection()
            Public Property OutputFilter As String = ""
        End Class

        Public NotInheritable Class CaptureSubSection
            ''' <summary>ddagrab | gdigrab | gfxcapture</summary>
            Public Property Method As String = "ddagrab"

            ''' <summary>ddagrab output_idx (output adapter index, 0 = primary)</summary>
            Public Property OutputIndex As Integer = 0

            ''' <summary>
            ''' Requested input framerate. For ddagrab this is the lavfi framerate
            ''' option; for gdigrab it is -framerate; for gfxcapture it is max_framerate.
            ''' </summary>
            Public Property Framerate As Integer = 60
        End Class

        Public NotInheritable Class ResolutionSubSection
            ''' <summary>native | custom</summary>
            Public Property Mode As String = "native"

            Public Property Width As Integer = 1920

            Public Property Height As Integer = 1080
        End Class

        Public NotInheritable Class EncoderSubSection
            ''' <summary>Symbolic key from Overlay UI: NVENC_H264, NVENC_HEVC, NVENC_AV1, QuickSync_H264, AMF_H264, LibX264, LibX265</summary>
            Public Property Key As String = "NVENC_H264"

            ''' <summary>
            ''' FFmpeg codec name resolved from Key via OverlayConfig.MapEncoderToFfmpeg.
            ''' Stored separately so the resolver does not need to depend on OverlayConfig.
            ''' </summary>
            Public Property FFmpegCodec As String = "h264_nvenc"

            ''' <summary>NVENC p1-p7 / QSV veryfast..veryslow / libx264 ultrafast..veryslow</summary>
            Public Property Preset As String = "p4"

            ''' <summary>NVENC tune: "ll" | "ull" | "lossless" | "" (none)</summary>
            Public Property Tune As String = "ll"

            ''' <summary>H.264 profile: "" | "baseline" | "main" | "high" | "high10"</summary>
            Public Property Profile As String = ""

            ''' <summary>cbr | vbr | cq</summary>
            Public Property RateControl As String = "cbr"

            ''' <summary>Single source of truth (bps, NOT kbps). Default 20 Mbps.</summary>
            Public Property BitrateBps As Long = 20000000L

            ''' <summary>CBR min rate (= BitrateBps for strict CBR). 0 = omit -minrate.</summary>
            Public Property MinrateBps As Long = 20000000L

            ''' <summary>CBR max rate (= BitrateBps for strict CBR). 0 = omit -maxrate.</summary>
            Public Property MaxrateBps As Long = 20000000L

            ''' <summary>
            ''' Buffer size in bps. Default = BitrateBps * 2 (FIX: legacy used 1× bitrate).
            ''' </summary>
            Public Property BufsizeBps As Long = 40000000L

            ''' <summary>GOP size in frames. Default = Framerate (1-second GOP).</summary>
            Public Property GopSize As Integer = 60

            ''' <summary>cfr | vfr | cfr_with_drop | passthrough (FFmpeg -fps_mode)</summary>
            Public Property FpsMode As String = "cfr"

            ''' <summary>NVENC spatial AQ: 0 | 1</summary>
            Public Property SpatialAQ As Integer = 1

            ''' <summary>NVENC temporal AQ: 0 | 1</summary>
            Public Property TemporalAQ As Integer = 1

            ''' <summary>NVENC look-ahead frames: 0-32 (0 = off)</summary>
            Public Property LookAhead As Integer = 0

            ''' <summary>NVENC zerolatency mode: true | false</summary>
            Public Property ZeroLatency As Boolean = False

            ''' <summary>Constant Quality value when RateControl=cq: 1-51 (0 = disabled)</summary>
            Public Property Cq As Integer = 0

            ''' <summary>nv12 | yuv420p | yuv444p | p010le (10-bit)</summary>
            Public Property PixelFormat As String = "nv12"
        End Class

        ' ──────────────────────────────────────────────────────────
        ' Audio section
        ' ──────────────────────────────────────────────────────────

        Public NotInheritable Class AudioSection
            Public Property System As SystemAudioSubSection = New SystemAudioSubSection()

            Public Property Microphone As MicrophoneSubSection = New MicrophoneSubSection()

            Public Property Encoding As AudioEncodingSubSection = New AudioEncodingSubSection()

            Public Property Sync As AudioSyncSubSection = New AudioSyncSubSection()
        End Class

        Public NotInheritable Class SystemAudioSubSection
            Public Property Enabled As Boolean = True

            Public Property Volume As Single = 1.0F

            Public Property DeviceId As String = ""

            Public Property DeviceName As String = ""
        End Class

        Public NotInheritable Class MicrophoneSubSection
            Public Property Enabled As Boolean = False

            Public Property Volume As Single = 1.0F

            Public Property DeviceId As String = ""

            Public Property DeviceName As String = ""
        End Class

        Public NotInheritable Class AudioEncodingSubSection
            ''' <summary>aac | opus | pcm_s16le</summary>
            Public Property Codec As String = "aac"

            Public Property BitrateBps As Long = 320000L

            Public Property SampleRate As Integer = 48000

            Public Property Channels As Integer = 2

            ''' <summary>single (amix) | separate (2 tracks in MP4)</summary>
            Public Property TrackMode As String = "single"
        End Class

        Public NotInheritable Class AudioSyncSubSection
            ''' <summary>Max positive audio offset before -ss skip (seconds).</summary>
            Public Property MaxOffsetSec As Double = 5.0

            ''' <summary>Min negative audio offset before adelay (seconds).</summary>
            Public Property MinOffsetSec As Double = -2.0

            ''' <summary>PASS/FAIL threshold for A/V sync in StressTestRunner (ms).</summary>
            Public Property AVSyncToleranceMs As Integer = 100
        End Class

        ' ──────────────────────────────────────────────────────────
        ' Output section
        ' ──────────────────────────────────────────────────────────

        Public NotInheritable Class OutputSection
            ''' <summary>mp4 | mov | mkv | m4v</summary>
            Public Property Container As String = "mp4"

            Public Property Directory As String = ""

            Public Property FilenamePattern As String = "ShadowPlay_{timestamp}.{ext}"

            ''' <summary>True = -y (overwrite); False = -n (refuse overwrite).</summary>
            Public Property Overwrite As Boolean = True

            ''' <summary>Apply -movflags +faststart on FINAL output (not on temp video).</summary>
            Public Property FastStart As Boolean = True

            Public Property TempVideoSuffix As String = ".video.tmp.mp4"

            Public Property TempAudioSuffix As String = ".system.tmp.wav"

            Public Property TempMicSuffix As String = ".mic.tmp.wav"
        End Class

        ' ──────────────────────────────────────────────────────────
        ' Runtime section
        ' ──────────────────────────────────────────────────────────

        Public NotInheritable Class RuntimeSection
            Public Property Hotkeys As Dictionary(Of String, String) = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"ToggleOverlay", "Alt+Z"},
                {"Screenshot", "Alt+F1"},
                {"PhotosToggle", "Alt+F2"},
                {"GameFilterToggle", "Alt+F3"},
                {"ManualRecordToggle", "Alt+F9"},
                {"InstantReplayToggle", "Alt+Shift+F10"},
                {"InstantReplaySave", "Alt+F10"},
                {"BroadcastToggle", "Alt+F8"}
            }

            Public Property FFmpegPath As String = ""

            Public Property FFprobePath As String = ""

            ''' <summary>Normal | AboveNormal | High (NEVER Realtime — scheduler headroom rule)</summary>
            Public Property ProcessPriority As String = "AboveNormal"

            ''' <summary>quiet | error | warning | info | verbose | debug</summary>
            Public Property LogLevel As String = "info"

            Public Property LogToFile As Boolean = True

            Public Property LogPath As String = ""

            Public Property DebugMode As Boolean = False

            Public Property AutoStartOverlay As Boolean = False

            Public Property ShutdownTimeoutMs As ShutdownTimeoutSubSection = New ShutdownTimeoutSubSection()
        End Class

        Public NotInheritable Class ShutdownTimeoutSubSection
            Public Property FFmpegQuit As Integer = 10000

            Public Property MuxWait As Integer = 60000

            Public Property FFprobeWait As Integer = 5000
        End Class

        ' ──────────────────────────────────────────────────────────
        ' Experimental section
        ' ──────────────────────────────────────────────────────────

        Public NotInheritable Class ExperimentalSection
            ''' <summary>Enable zero-copy D3D11 → NVENC interop (V1 spike — not validated yet).</summary>
            Public Property EnableZeroCopy As Boolean = False

            ''' <summary>Enable D3D11 NVENC interop spike code (P1-B.2-V1).</summary>
            Public Property EnableD3D11Interop As Boolean = False
        End Class

    End Class
End Namespace
