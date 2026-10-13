Option Strict On
Option Explicit On

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Per-packet metadata carried on every EncodedPacket. (P1-F §5.2)
    '''
    ''' All timestamps are in the Engine's internal PTS unit
    ''' (P1-A v1.3.1 §3.6.1 — same domain as FrameDiagnostics).
    ''' This guarantees that encoded packets can be correlated with
    ''' their source IVideoFrame.Diagnostics.PresentationTimestampTicks
    ''' without unit conversion at the muxer.
    '''
    ''' Field semantics (mirrors FFmpeg AVPacket where applicable):
    '''   Sequence            — monotonic per-encoder counter (0, 1, 2, ...)
    '''   PresentationTime    — when the decoded frame SHOULD be displayed
    '''   DecodingTime         — when the decoder SHOULD decode this packet
    '''   DurationTicks        — how long the frame stays on screen
    '''   IsKeyFrame           — true if packet is an I-frame (random access point)
    '''   IsReferenceFrame     — true if packet may be referenced by P/B-frames
    '''   CodecKey             — symbolic codec (matches EncoderConfig.CodecKey)
    '''   CodecSpecificFlags   — bit field for future codec-specific bits
    '''
    ''' This is an immutable struct — copy-by-value semantics. Safe to
    ''' pass between threads without synchronization.
    ''' </summary>
    Public Structure PacketMetadata
        Private ReadOnly _sequence As Long
        Private ReadOnly _pts As Long
        Private ReadOnly _dts As Long
        Private ReadOnly _duration As Long
        Private ReadOnly _isKeyFrame As Boolean
        Private ReadOnly _isReference As Boolean
        Private ReadOnly _codecKey As String
        Private ReadOnly _flags As Integer

        Public Sub New(sequence As Long,
                       presentationTimeTicks As Long,
                       decodingTimeTicks As Long,
                       durationTicks As Long,
                       isKeyFrame As Boolean,
                       isReferenceFrame As Boolean,
                       codecKey As String,
                       codecSpecificFlags As Integer)
            _sequence = sequence
            _pts = presentationTimeTicks
            _dts = decodingTimeTicks
            _duration = durationTicks
            _isKeyFrame = isKeyFrame
            _isReference = isReferenceFrame
            _codecKey = If(codecKey, String.Empty)
            _flags = codecSpecificFlags
        End Sub

        ''' <summary>Monotonic per-encoder packet counter. MUST be strictly increasing.</summary>
        Public ReadOnly Property Sequence As Long
            Get
                Return _sequence
            End Get
        End Property

        ''' <summary>Presentation timestamp (PTS) in Engine ticks. Same unit as FrameDiagnostics.</summary>
        Public ReadOnly Property PresentationTimestampTicks As Long
            Get
                Return _pts
            End Get
        End Property

        ''' <summary>Decoding timestamp (DTS) in Engine ticks. May equal PTS for low-latency encoders.</summary>
        Public ReadOnly Property DecodingTimestampTicks As Long
            Get
                Return _dts
            End Get
        End Property

        ''' <summary>How long the decoded frame stays on screen, in Engine ticks. 0 = unknown.</summary>
        Public ReadOnly Property DurationTicks As Long
            Get
                Return _duration
            End Get
        End Property

        ''' <summary>True if this packet is a keyframe (I-frame / random access point).</summary>
        Public ReadOnly Property IsKeyFrame As Boolean
            Get
                Return _isKeyFrame
            End Get
        End Property

        ''' <summary>True if this packet may be referenced by future P/B-frames.</summary>
        Public ReadOnly Property IsReferenceFrame As Boolean
            Get
                Return _isReference
            End Get
        End Property

        ''' <summary>Symbolic codec key (matches EncoderConfig.CodecKey at construction time).</summary>
        Public ReadOnly Property CodecKey As String
            Get
                Return _codecKey
            End Get
        End Property

        ''' <summary>Bit field for future codec-specific flags (e.g. NVENC "weighted prediction").</summary>
        Public ReadOnly Property CodecSpecificFlags As Integer
            Get
                Return _flags
            End Get
        End Property
    End Structure
End Namespace
