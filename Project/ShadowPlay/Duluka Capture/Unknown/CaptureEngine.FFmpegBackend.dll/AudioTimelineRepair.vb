Option Strict On
Option Explicit On
Option Infer On

' AudioTimelineRepair.vb — the OBS rule for callback-driven capture tracks:
' silence is TIME, not garbage. [P13-AUDIO-TIMELINE]
'
' HOME = CaptureEngine.FFmpegBackend (beside AudioTap v2, whose proven
' gap-repair model this class extracts) because it must stay net10.0
' cross-platform: CaptureEngine.Recording.Tests (Linux CI) references
' ONLY this assembly, while CaptureEngine.Recording is net10.0-windows.
'
' OWNER evidence (2026-08-28): recordings came out as
'     real:    [silence][speech][silence][speech][silence]
'     file:    [speech][speech][long silence.............]
' — every silent span of the session had been REMOVED from its position
' and re-appeared only as the mux stage's apad tail. Root cause: the
' legacy engine's AudioFileWriter had NO mid-stream gap repair by design
' ("GAP DETECTION REMOVED entirely" per an old review), so the WAV
' concatenated only the sounded spans (a compressed timeline), which the
' apad filter then padded at the END to video duration. GPT's own OBS
' analysis (owner, 2026-08-28) states the correct model: silence is part
' of the audio stream's timeline and must stay at its real position.
'
' THE RULE (OBS audio-io, and the same owner-validated AudioTap v2 model
' used by CaptureEngine.Recording/CaptureSession):
'   a callback that arrives after a quiet span carries ONLY the sounded
'   content; the span between the previous callback's content end and
'   this buffer's arrival must be written as silence BEFORE the buffer:
'       silenceNeeded = (now − lastCallbackTicks)/freq − bufferDuration
'   (subtracting bufferDuration removes the double count: WASAPI already
'   accumulated the buffer's own span between the two callbacks.)
'
' Proven thresholds carried over from AudioTap v2:
'   - 50ms minimum gap — sub-threshold deltas are scheduler/packet jitter,
'     not real holes
'   - 3600s cap — a stall longer than an hour is a log event, not padding
'   - every silence byte count is floored to a whole PCM frame
' The FIRST gap (StartRecording → first callback) keeps the prefill rule:
' deterministic device warm-up, NO 50ms cutoff — any gap > 0 is real.
'
' Pure and deterministic: no threads, no clock reads — the caller supplies
' Stopwatch ticks. Linux-testable by construction.

Imports System.Diagnostics

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>
    ''' Wall-clock gap repair for one callback-driven audio track.
    ''' NOT thread-safe: call OnCallback from the WASAPI callback thread,
    ''' serially (same contract as AudioTap).
    ''' </summary>
    Public NotInheritable Class AudioTimelineRepair

        ''' <summary>Gaps shorter than this are ignored (jitter).</summary>
        Public Const MinGapSec As Double = 0.05

        ''' <summary>Gaps longer than this are NOT padded (device stall —
        ''' a log event, not minutes of synthetic silence).</summary>
        Public Const MaxGapSec As Double = 3600.0

        Private ReadOnly _frequency As Double      ' Stopwatch ticks per second
        Private ReadOnly _bytesPerSecond As Long
        Private ReadOnly _frameSize As Integer     ' block align (bytes per PCM frame)
        Private ReadOnly _startTicks As Long       ' StartRecording() call time

        Private _lastTicks As Long = 0
        Private _firstSeen As Boolean = False

        ' ── Evidence surface (surfaced through AudioFileWriter diagnostics) ──
        Private _silenceBytes As Long = 0
        Private _gapCount As Integer = 0
        Private _lastGapSec As Double = 0.0

        ''' <summary>Silence bytes recommended so far (written + dropped).</summary>
        Public ReadOnly Property SilenceRecommendedBytes As Long
            Get
                Return _silenceBytes
            End Get
        End Property

        ''' <summary>How many gaps produced silence (including the first).</summary>
        Public ReadOnly Property GapCount As Integer
            Get
                Return _gapCount
            End Get
        End Property

        ''' <summary>Last repaired gap in seconds (evidence).</summary>
        Public ReadOnly Property LastGapSec As Double
            Get
                Return _lastGapSec
            End Get
        End Property

        ''' <summary>True once the first callback anchored the repair.</summary>
        Public ReadOnly Property Anchored As Boolean
            Get
                Return _firstSeen
            End Get
        End Property

        Public Sub New(frequency As Double,
                       bytesPerSecond As Long,
                       frameSize As Integer,
                       startTicks As Long)
            If frequency <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(frequency))
            If bytesPerSecond <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(bytesPerSecond))
            _frequency = frequency
            _bytesPerSecond = bytesPerSecond
            _frameSize = Math.Max(1, frameSize)
            _startTicks = startTicks
        End Sub

        ''' <summary>Convenience ctor using the live Stopwatch frequency.</summary>
        Public Sub New(bytesPerSecond As Long, frameSize As Integer, startTicks As Long)
            Me.New(Stopwatch.Frequency, bytesPerSecond, frameSize, startTicks)
        End Sub

        ''' <summary>
        ''' Call at the top of every WASAPI DataAvailable callback. Returns
        ''' the number of SILENCE bytes that must be enqueued BEFORE this
        ''' buffer (0 when the stream is continuous). The caller updates its
        ''' last-arrival bookkeeping by calling this exactly once per
        ''' callback, with the callback's dispatch timestamp.
        ''' </summary>
        Public Function OnCallback(nowTicks As Long, bufferBytes As Long) As Long
            If nowTicks <= 0 OrElse bufferBytes < 0 Then Return 0

            If Not _firstSeen Then
                ' First callback: the whole StartRecording → now span is the
                ' device warm-up. Prefill rule: deterministic, no 50ms
                ' cutoff (any gap > 0 is real time the mux will place).
                _firstSeen = True
                _lastTicks = nowTicks
                Dim gapSec As Double = (nowTicks - _startTicks) / _frequency
                If gapSec > 0 AndAlso gapSec <= MaxGapSec Then
                    Dim bytes As Long = Aligned(CLng(gapSec * _bytesPerSecond))
                    If bytes > 0 Then
                        _silenceBytes += bytes
                        _gapCount += 1
                        _lastGapSec = gapSec
                        Return bytes
                    End If
                End If
                Return 0
            End If

            ' Common case: measured arrival gap minus the buffer's own span.
            Dim holeSec As Double =
                (nowTicks - _lastTicks) / _frequency - bufferBytes / CDbl(_bytesPerSecond)
            _lastTicks = nowTicks

            If holeSec > MinGapSec AndAlso holeSec <= MaxGapSec Then
                Dim silBytes As Long = Aligned(CLng(holeSec * _bytesPerSecond))
                If silBytes > 0 Then
                    _silenceBytes += silBytes
                    _gapCount += 1
                    _lastGapSec = holeSec
                    Return silBytes
                End If
            End If
            Return 0
        End Function

        ''' <summary>
        ''' Freeze the anchor at stop time (the capture is done; no further
        ''' callbacks). The session tail is padded by the mux stage's apad —
        ''' by design, so the tail length follows the EXACT video duration.
        ''' </summary>
        Public Sub Resync(nowTicks As Long)
            If _firstSeen AndAlso nowTicks > 0 Then _lastTicks = nowTicks
        End Sub

        Private Function Aligned(bytes As Long) As Long
            If _frameSize <= 1 Then Return bytes
            Return bytes - (bytes Mod _frameSize)
        End Function

    End Class

End Namespace
