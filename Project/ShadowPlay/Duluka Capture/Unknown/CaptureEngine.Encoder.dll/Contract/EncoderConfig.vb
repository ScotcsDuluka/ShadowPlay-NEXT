Option Strict On
Option Explicit On

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Configuration for an IEncoderBackend. (P1-F §4)
    '''
    ''' This is the contract-level config — it contains only fields that
    ''' are meaningful to ALL encoder implementations (NVENC, QSV, AMF,
    ''' Software). Implementation-specific options (e.g. NVENC's
    ''' SpatialAQ / TemporalAQ / Tune) live in implementation-specific
    ''' config types defined by the concrete encoder's own project.
    '''
    ''' Ownership: reference type with clone support. Safe to pass
    ''' between threads; callers SHOULD clone before mutating.
    '''
    ''' Unit policy: ALL bitrate values are in BITS PER SECOND (bps).
    ''' This matches EngineConfigV2 and avoids the legacy kbps/bps
    ''' confusion documented in ConfigMigrator.
    '''
    ''' Timestamp policy: all timestamps are in the Engine's internal
    ''' PTS unit (P1-A v1.3.1 §3.6.1 — same domain as FrameDiagnostics).
    ''' TimeSpan.Ticks and Stopwatch.GetTimestamp() MUST NOT be mixed.
    ''' </summary>
    Public NotInheritable Class EncoderConfig
        Implements ICloneable

        ' ---- codec identity ----

        ''' <summary>
        ''' Symbolic codec key — drives implementation dispatch (e.g.
        ''' "NVENC_H264", "NVENC_HEVC", "NVENC_AV1", "QSV_H264",
        ''' "AMF_H264", "LIBX264"). Concrete encoders MUST accept this
        ''' key in their factory and reject mismatches.
        ''' </summary>
        Public Property CodecKey As String = "NVENC_H264"

        ''' <summary>
        ''' FFmpeg-compatible codec name (e.g. "h264_nvenc", "hevc_nvenc",
        ''' "h264_qsv", "h264_amf", "libx264"). Used when the encoder
        ''' wraps FFmpeg. May be empty for non-FFmpeg encoders (future
        ''' native NvEncodeAPI path).
        ''' </summary>
        Public Property FFmpegCodec As String = "h264_nvenc"

        ' ---- encoding parameters ----

        ''' <summary>Bitrate in bits per second. Must be > 0 for CBR/VBR.</summary>
        Public Property BitrateBps As Long = 20_000_000L

        ''' <summary>Minimum bitrate (bps). For CBR, MUST equal BitrateBps.</summary>
        Public Property MinrateBps As Long = 20_000_000L

        ''' <summary>Maximum bitrate (bps). For CBR, MUST equal BitrateBps.</summary>
        Public Property MaxrateBps As Long = 20_000_000L

        ''' <summary>
        ''' Rate-control buffer size (bps). Must be >= BitrateBps.
        ''' Convention: NVENC strict CBR uses bufsize = bitrate;
        ''' SW encoders typically use bufsize = 2 × bitrate.
        ''' </summary>
        Public Property BufsizeBps As Long = 40_000_000L

        ''' <summary>Group of Pictures size in frames. Typically = framerate for 1-second GOP.</summary>
        Public Property GopSize As Integer = 60

        ''' <summary>Rate control mode: "cbr", "vbr", or "cq".</summary>
        Public Property RateControl As String = "cbr"

        ''' <summary>Constant Quality value (1-51). Only meaningful when RateControl = "cq".</summary>
        Public Property Cq As Integer = 0

        ''' <summary>
        ''' Encoder preset (implementation-specific string, e.g. "p4" for NVENC,
        ''' "medium" for libx264). Concrete encoders validate against their
        ''' own preset list.
        ''' </summary>
        Public Property Preset As String = "p4"

        ''' <summary>Output pixel format hint (e.g. "nv12", "yuv420p", "p010le").</summary>
        Public Property OutputPixelFormat As String = "nv12"

        ''' <summary>
        ''' PHASE 1 VIDEO RUNTIME WIRING: frame rate the NVENC session is
        ''' initialized with (frameRateNum/frameRateDen = fps/1). Init-time —
        ''' the per-session pacing rate lives in SessionConfig.TargetFps.
        ''' 0 = encoder default (60).
        ''' </summary>
        Public Property FrameRateFps As Integer = 0

        ' ---- frame I/O contract ----

        ''' <summary>
        ''' Expected input frame width. The encoder MAY reject frames whose
        ''' IVideoFrame.Dimensions.Width differs. 0 = accept any width.
        ''' </summary>
        Public Property ExpectedWidth As Integer = 0

        ''' <summary>Expected input frame height. 0 = accept any height.</summary>
        Public Property ExpectedHeight As Integer = 0

        ''' <summary>
        ''' Expected input pixel format. The encoder MUST reject frames
        ''' whose IVideoFrame.PixelFormat does not match (Phase 1 = Bgra8).
        ''' </summary>
        Public Property ExpectedInputFormat As CaptureEngine.Video.VideoPixelFormat =
            CaptureEngine.Video.VideoPixelFormat.Bgra8

        ' ── PHASE 1 VIDEO RUNTIME WIRING (V-CT2) ──
        ''' <summary>
        ''' Encode width (output resolution). 0 = same as ExpectedWidth.
        ''' When smaller than the input, a GPU scaler (NVENC native scaling)
        ''' downsizes the frame. Never larger than the input — NVENC cannot
        ''' upscale, and a silent desktop-resolution fallback is forbidden.
        ''' </summary>
        Public Property EncodeWidth As Integer = 0

        ''' <summary>Encode height (output resolution). 0 = same as ExpectedHeight.</summary>
        Public Property EncodeHeight As Integer = 0

        ' ---- threading / latency ----

        ''' <summary>Maximum in-flight Encode() calls before backpressure is applied.</summary>
        Public Property MaxInFlightFrames As Integer = 4

        ''' <summary>Timeout (ms) for Flush() to complete before raising EncoderShutdownException.</summary>
        Public Property FlushTimeoutMs As Integer = 5000

        ''' <summary>Timeout (ms) for Stop() to complete before raising EncoderShutdownException.</summary>
        Public Property StopTimeoutMs As Integer = 10000

        ''' <summary>
        ''' Creates a shallow copy of this config. Config fields are either
        ''' value types (Long, Integer) or immutable references (String),
        ''' so MemberwiseClone is safe. Future complex fields must be
        ''' deep-copied by overriding this method.
        ''' </summary>
        Public Function Clone() As EncoderConfig
            Return CType(Me.MemberwiseClone(), EncoderConfig)
        End Function

        Private Function CloneObject() As Object Implements ICloneable.Clone
            Return Clone()
        End Function
    End Class
End Namespace
