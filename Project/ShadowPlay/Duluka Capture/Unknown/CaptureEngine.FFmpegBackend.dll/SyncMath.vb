Option Strict On
Option Explicit On
Option Infer On

' SyncMath.vb
'
' A/V sync offset math — extracted from the PROVEN legacy engine model
' (Engine/Engine/[Capture]/CaptureEngine.vb, ~line 489-501) so it can be
' unit-tested on any platform (net8.0, no Windows dependencies).
'
' Timestamp model (per legacy engine comments + Phase 11/12 audits):
'   _videoStartTicks  = session-clock ticks when the FIRST video frame is
'                       captured from the sink (≈ video timeline t=0 in the
'                       final MP4).
'   _systemStartTicks = session-clock ticks when StartRecording() was CALLED
'                       on the system-audio capture (TRUE capture start).
'
'   NOTE (proven lesson, legacy line ~86-90): these are StartRecording CALL
'   times, NOT first-callback times. WASAPI loopback may delay the first
'   DataAvailable callback by seconds when no audio is playing; using
'   first-callback time produced the historical "-10s offset bug".
'
' At mux time each audio track gets its own offset:
'   systemOffsetSec = (videoStart - systemStart) / freq
'     positive → audio started BEFORE video → skip audio head (-ss at mux)
'     negative → audio started AFTER video  → delay audio (adelay at mux)
'
' Clamp range [-2s, +5s] is carried over verbatim from the proven engine.

Imports System.Diagnostics

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>
    ''' Pure A/V sync math. No state, no I/O — safe to call from anywhere.
    ''' All methods are culture-invariant and thread-safe.
    ''' </summary>
    Public NotInheritable Class SyncMath

        ''' <summary>Lower clamp for per-track audio offsets (seconds).
        ''' LEGACY call-time path ONLY — those stamps are StartRecording CALL
        ''' times whose guesses can be wildly wrong; the clamp is the proven
        ''' safety net there.</summary>
        Public Const MinOffsetSec As Double = -2.0

        ''' <summary>Upper clamp for per-track audio offsets (seconds).
        ''' LEGACY call-time path ONLY (see MinOffsetSec).</summary>
        Public Const MaxOffsetSec As Double = 5.0

        ' ★ P13.4b (field evidence, OWNER run 2026-08-28): the Device-anchor
        ' offset used to inherit the legacy [-2s,+5s] clamp — and an endpoint
        ' idle at session start anchors the tap ~5s late, the raw offset went
        ' below -2s, got clamped (log: offset=-2.000s) and the audio track
        ' was permanently misplaced. The live mux expresses ANY offset
        ' byte-exactly (head discard/pad — LiveMuxSession.BeginTimelines), so
        ' the anchor path needs a SANITY bound against unanchored garbage,
        ' not a policy clamp. Bound = the tap's MaxGapSec (an honest session
        ' never exceeds it; a stalled device is a different failure mode).
        Public Const MinAnchorOffsetSec As Double = -3600.0
        Public Const MaxAnchorOffsetSec As Double = 3600.0

    ' ★ Audio calibration (owner-measured 2026-08-23): the system-audio path
    ' runs ~100ms BEHIND video in the final file (WASAPI shared-mode buffering
    ' + loopback delivery latency). Feeding the audio pipes this much EARLIER
    ' compensates the bias: positive = shift audio earlier (audio was late).
    ' Calibrated 2026-08-24: owner measured the stream 50ms AHEAD with
    ' lead=100ms ('ไวเกินไป 50ms') → device latency ≈ 50ms on this machine.
    ' lead=50ms. Re-measure with scripts\sync-verify.ps1 after changes:
    '   still ahead by X → lead -= X;  behind by X → lead += X.
    Public Const SystemAudioLeadSec As Double = 0.05
    Public Const MicAudioLeadSec As Double = 0.05

    ' ★ P13.4 (doc §3.3): with Device-clock ANCHORS the offset is exact —
    ' leads are ZERO by default. The knob is retained for a MEASURED
    ' residual (loopback read-lag via scripts\sync-verify.ps1), never by
    ' ear, never hand-calibrated per machine.
    Public Const SystemAudioLeadDeviceSec As Double = 0.0
    Public Const MicAudioLeadDeviceSec As Double = 0.0

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Compute the per-track audio offset for the mux step.
        '''
        ''' offset &gt; 0  → audio started before video → mux skips audio head by |offset| (-ss)
        ''' offset &lt; 0  → audio started after video  → mux delays audio by |offset| (adelay)
        ''' offset = 0  → tracks started together.
        ''' </summary>
        ''' <param name="videoStartTicks">
        ''' Stopwatch ticks when the first video frame was captured. Pass 0 if
        ''' no video frame was ever captured (result will be 0 — mux cannot
        ''' sync against a timeline that does not exist).
        ''' </param>
        ''' <param name="audioStartTicks">
        ''' Stopwatch ticks when audio StartRecording() was CALLED. Pass 0 if
        ''' audio never started.
        ''' </param>
        ''' <param name="stopwatchFrequency">Stopwatch.Frequency for the machine.</param>
        Public Shared Function ComputeAudioOffsetSec(videoStartTicks As Long,
                                                     audioStartTicks As Long,
                                                     stopwatchFrequency As Double) As Double
            If videoStartTicks <= 0 OrElse audioStartTicks <= 0 Then Return 0.0
            If stopwatchFrequency <= 0 Then Return 0.0

            Dim raw As Double = (videoStartTicks - audioStartTicks) / stopwatchFrequency
            Return ClampOffsetSec(raw)
        End Function

        ''' <summary>
        ''' P13.4 — SyncMath v2: exact QPC anchor arithmetic.
        '''
        ''' offsetSec = (videoT0Qpc − firstAudioQpc) / 10⁷   ' both in 100ns
        '''
        ''' Both stamps come from the SAME hardware counter domain (QPC):
        ''' videoT0 = Stopwatch.GetTimestamp() at the first encoded frame,
        ''' normalized to 100ns; firstAudioQpc = the first WASAPI packet's
        ''' qpcPosition100ns (WasapiPositionCapture). No call-time guessing,
        ''' no pre-roll estimation — anchor-to-anchor subtraction.
        '''
        ''' P13.4b: bounded by ±3600s SANITY only — NOT the legacy [-2,+5]
        ''' clamp. The mux pads/discards the head byte-exactly for any value.
        ''' </summary>
        ''' <param name="videoT0Qpc100ns">First video frame's stamp, 100ns. 0 = no video timeline (returns 0).</param>
        ''' <param name="firstAudioQpc100ns">First audio packet's device stamp, 100ns. 0 = no audio anchor (returns 0).</param>
        Public Shared Function ComputeAudioOffsetSecFromAnchors(videoT0Qpc100ns As Long,
                                                                firstAudioQpc100ns As Long) As Double
            If videoT0Qpc100ns <= 0 OrElse firstAudioQpc100ns <= 0 Then Return 0.0
            Dim raw As Double = (videoT0Qpc100ns - firstAudioQpc100ns) / 10000000.0
            If raw < MinAnchorOffsetSec Then Return MinAnchorOffsetSec
            If raw > MaxAnchorOffsetSec Then Return MaxAnchorOffsetSec
            Return raw
        End Function

        ''' <summary>Clamp a raw offset into the proven [-2s, +5s] window.
        ''' LEGACY call-time path only — the Device-anchor path uses the
        ''' ±3600s sanity bound inside ComputeAudioOffsetSecFromAnchors.</summary>
        Public Shared Function ClampOffsetSec(rawOffsetSec As Double) As Double
            If rawOffsetSec < MinOffsetSec Then Return MinOffsetSec
            If rawOffsetSec > MaxOffsetSec Then Return MaxOffsetSec
            Return rawOffsetSec
        End Function

        ''' <summary>
        ''' The adelay milliseconds the mux applies for a negative offset.
        ''' Positive number, or 0 when no delay is needed. Mirrors
        ''' MuxCoordinator.BuildMuxArguments: sysDelayMs = max(0, -offset)*1000.
        ''' </summary>
        Public Shared Function OffsetToDelayMs(offsetSec As Double) As Integer
            If offsetSec >= 0 Then Return 0
            Return CInt(Math.Max(0, -offsetSec) * 1000)
        End Function

        ''' <summary>True when the mux step must skip the audio head (-ss).</summary>
        Public Shared Function NeedsInputSkip(offsetSec As Double) As Boolean
            Return offsetSec > 0.001
        End Function

        ''' <summary>
        ''' Format an offset for an ffmpeg -ss argument (invariant culture,
        ''' 3 decimals). Returns "" when no skip is needed.
        ''' </summary>
        Public Shared Function FormatInputSkipArg(offsetSec As Double) As String
            If Not NeedsInputSkip(offsetSec) Then Return ""
            Return offsetSec.ToString("0.000", Globalization.CultureInfo.InvariantCulture)
        End Function

    End Class

End Namespace
