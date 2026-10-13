Option Strict On
Option Explicit On
Option Infer On

' AudioTap.vb — ONE gap-fill engine for BOTH capture tracks (system + mic).
'
' Consolidates the duplicated gap-fill v2 logic (previously copy-pasted in
' CaptureSession for system audio and mic) per the GLM/6 audit recommendation.
' Single implementation = single place to fix sync math, single evidence model.
'
' WALL-CLOCK MODEL (v2, owner-validated):
'   WASAPI delivers callbacks only when audio data exists. Between callbacks
'   the elapsed REAL time passes but no bytes arrive. This tap reconstructs
'   the true timeline: when a buffer arrives, its content already SPANS the
'   gap (WASAPI accumulated it), so:
'       silenceNeeded = arrivalGap − bufferDuration
'   Every silence byte goes to BOTH the WAV sidecar AND the live-mux pipe —
'   one timeline everywhere. Pre-roll: the first callback arrives LATE by the
'   device's warm-up (this machine: loopback first callback ≈ 5.7s — legacy
'   logged SysFirstCallbackDelayMs=5756.7); the silent head before it is real
'   wall-clock silence and must be inserted or the music shifts to t=0.
'
' DEVICE LATENCY (audit round 2): the byte stream's notion of 'now' is the
' ARRIVAL time, but the audio in a buffer was CAPTURED earlier by the device
' latency L (WASAPI shared mode ≈ 10-40ms). Compensating = shift the whole
' stream earlier by L (leadSec). The lead is applied by advancing the tap's
' origin: silenceNeeded is computed from (arrival − L) instead of arrival,
' which lands every buffer L earlier on the timeline.

Imports System.Diagnostics
Imports System.Threading

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>Where a tap sends its reconstructed byte stream.</summary>
    Public Interface IAudioTapSink
        Sub Write(data As Byte(), count As Integer)
    End Interface

    ''' <summary>
    ''' Gap-filling wall-clock audio tap for one capture device.
    ''' NOT thread-safe beyond the WASAPI callback thread + one Finalize call.
    ''' </summary>
    Public NotInheritable Class AudioTap

        Private ReadOnly _name As String
        Private ReadOnly _sampleRate As Integer
        Private ReadOnly _channels As Integer
        Private ReadOnly _bytesPerFrame As Integer        ' block align
        Private ReadOnly _bytesPerSec As Integer
        Private ReadOnly _sink As IAudioTapSink
        Private ReadOnly _evidence As Action(Of String)

        ' Device latency compensation (audit round 2): positive = audio stream
        ''' is shifted EARLIER by this amount (device captured it before we
        ''' received it). 0 = disabled.
        Private ReadOnly _leadSec As Double

        Private _originTicks As Long = 0        ' 0 = no buffer yet (set on first)
        Private _lastTicks As Long = 0

        ' ★ Clock steering (GLM/6 round 3): the device crystal drifts vs the
        ' Stopwatch (~50ppm = 30ms per 10 minutes) — long recordings slowly
        ' desync. Every SteeringIntervalSec we compare accumulated BYTES
        ' against WALL-CLOCK elapsed since origin; a deviation beyond
        ' tolerance is corrected by adjusting _lastTicks (which feeds the gap
        ' math), so the NEXT gap absorbs the correction as extra/less silence,
        ' block-aligned. Converts permanent drift into bounded drift.
        Private Const SteeringIntervalSec As Double = 5.0
        Private Const SteeringToleranceSec As Double = 0.015   ' +/-15ms
        Private _lastSteerCheckTicks As Long = 0
        Private _steerCorrections As Integer = 0

        ' ★ 02:24 FIX: bytes at the last steering check — a check where the
        ' stream delivered (almost) nothing is a GAP (device idle), not drift.
        ' Correcting on gaps produced ±14-21s bogus 'corrections' in the
        ' owner's log. Steering only runs when data actually flowed.
        Private _lastSteerCheckBytes As Long = 0

        ' ★ SELF-AUDIT FIX: steering must correct only GROWING drift. The
        ' owner's 01:08 run showed a CONSTANT ~80ms 'behind' every check —
        ' that is the first-buffer placement offset (a constant), NOT crystal
        ' drift. Correcting it every 5s would have injected 80ms of fake
        ' silence per interval and shredded the audio. Baseline model:
        ' the first check records the constant offset; only the CHANGE from
        ' the baseline is ever corrected.
        Private _driftBaselineSec As Double = 0.0
        Private _driftBaselineSet As Boolean = False
        Private _silenceBuf As Byte() = Nothing
        Private _silenceCap As Integer = 0
        Private _silenceInsertedBytes As Long = 0
        Private _dataBytes As Long = 0
        Private _firstCallbackDelayMs As Double = -1.0

        Public Sub New(name As String,
                       sampleRate As Integer,
                       channels As Integer,
                       bitsPerSample As Integer,
                       sink As IAudioTapSink,
                       Optional leadSec As Double = 0.0,
                       Optional evidence As Action(Of String) = Nothing)
            If sampleRate <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(sampleRate))
            If channels <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(channels))
            _name = name
            _sampleRate = sampleRate
            _channels = channels
            _bytesPerFrame = Math.Max(1, channels * (bitsPerSample \ 8))
            _bytesPerSec = sampleRate * _bytesPerFrame
            _sink = sink
            _leadSec = leadSec
            _evidence = evidence
        End Sub

        Public ReadOnly Property SilenceInsertedBytes As Long
            Get
                Return Interlocked.Read(_silenceInsertedBytes)
            End Get
        End Property

        Public ReadOnly Property DataBytes As Long
            Get
                Return Interlocked.Read(_dataBytes)
            End Get
        End Property

        Public ReadOnly Property TotalDurationSec As Double
            Get
                Return (Interlocked.Read(_silenceInsertedBytes) + Interlocked.Read(_dataBytes)) / CDbl(_bytesPerSec)
            End Get
        End Property

        ''' <summary>Device warm-up measured on this run: delay of the first callback.</summary>
        Public ReadOnly Property FirstCallbackDelayMs As Double
            Get
                Return _firstCallbackDelayMs
            End Get
        End Property

        ''' <summary>Wall-clock origin the tap is tracking against (ticks). 0 until first buffer.</summary>
        Public ReadOnly Property OriginTicks As Long
            Get
                Return _originTicks
            End Get
        End Property

        ''' <summary>
        ''' Feed one raw PCM buffer (the format given at construction). The tap
        ''' inserts any needed silence BEFORE the buffer, then forwards both.
        ''' Call from the WASAPI DataAvailable callback.
        ''' </summary>
        Public Sub Feed(buffer As Byte(), count As Integer)
            If count <= 0 OrElse buffer Is Nothing OrElse count > buffer.Length Then Return

            Dim nowT As Long = Stopwatch.GetTimestamp()

            If _originTicks = 0 Then
                ' First buffer: establish the origin AND measure the device's
                ' first-callback delay (evidence for latency calibration).
                ' ★ REGRESSION FIX (self-audit): the original v2 code inserted
                ' PRE-ROLL silence on the first callback (the device warm-up —
                ' this machine ≈ 5.7s loopback). The first AudioTap version
                ' forwarded the buffer immediately, dropping the pre-roll and
                ' shifting the music back to t=0 ('music before I played any'
                ' again). Fix: anchor _lastTicks at the ORIGIN and FALL THROUGH
                ' to the common gap math — the arrival gap of the first buffer
                ' IS the warm-up, and the common math inserts it as silence.
                _firstCallbackDelayMs = (nowT - _startRequestedTicks) * 1000.0 / Stopwatch.Frequency
                _originTicks = _startRequestedTicks
                _lastTicks = _startRequestedTicks
                _evidence?.Invoke($"[tap:{_name}] first callback +{_firstCallbackDelayMs:0}ms warm-up; buffer {count / CDbl(_bytesPerSec) * 1000.0:0}ms")
                ' (fall through: pre-roll silence inserted by the common path)
            End If

            ' ★ Clock steering check (every SteeringIntervalSec):
            '   streamSec = total bytes fed / nominal rate
            '   elapsedSec = wall clock since origin
            '   drift = elapsed - stream
            '   drift > tolerance  → stream is SHORT → shift _lastTicks BACK
            '     (arrivalGap grows → next gap inserts MORE silence)
            '   drift < -tolerance → stream is LONG → shift _lastTicks FORWARD
            '     (arrivalGap shrinks — possibly negative → clamped to no
            '     silence; the excess decays over subsequent corrections)
            ' Corrections are capped at 100ms per interval so a single glitch
            ' cannot slam the timeline.
            If _lastSteerCheckTicks = 0 Then _lastSteerCheckTicks = _originTicks
            If (nowT - _lastSteerCheckTicks) / Stopwatch.Frequency >= SteeringIntervalSec Then
                Dim bytesNow As Long = Interlocked.Read(_silenceInsertedBytes) + Interlocked.Read(_dataBytes)
                Dim bytesSinceLastCheck As Long = bytesNow - _lastSteerCheckBytes
                _lastSteerCheckBytes = bytesNow
                _lastSteerCheckTicks = nowT
                Dim elapsedSecS As Double = (nowT - _originTicks) / Stopwatch.Frequency
                Dim streamSecS As Double = bytesNow / CDbl(_bytesPerSec)
                Dim driftSecS As Double = elapsedSecS - streamSecS

                ' ★ data-flow gate: no meaningful data since the last check =
                ' the device was idle (a gap). The gap math already handles it
                ' via the next buffer's silence insertion; steering must NOT
                ' touch the timeline on gaps (02:24: ±14-21s false swings).
                If bytesSinceLastCheck < CLng(_bytesPerSec * 0.5) Then
                    GoTo SteerDone
                End If

                If Not _driftBaselineSet Then
                    ' First check: record the CONSTANT offset (first-buffer
                    ' placement + dispatch latency). Never correct it here —
                    ' it is a bias, absorbed by the lead calibration at the pipe.
                    _driftBaselineSec = driftSecS
                    _driftBaselineSet = True
                    _evidence?.Invoke($"[tap:{_name}] steering baseline: {driftSecS * 1000.0:0}ms constant offset (bias — handled by pipe lead, not corrected)")
                Else
                    Dim deltaSecS As Double = driftSecS - _driftBaselineSec
                    If Math.Abs(deltaSecS) > SteeringToleranceSec Then
                        Dim corrSec As Double = Math.Max(-0.1, Math.Min(0.1, deltaSecS))
                        _lastTicks -= CLng(corrSec * Stopwatch.Frequency)
                        _driftBaselineSec = driftSecS
                        _steerCorrections += 1
                        _evidence?.Invoke($"[tap:{_name}] steering: drift changed {deltaSecS * 1000.0:0}ms beyond baseline, correcting via next gap (#{_steerCorrections})")
                    End If
                End If
SteerDone:
            End If

            Dim arrivalGapSec As Double = (nowT - _lastTicks) / Stopwatch.Frequency
            Dim bufferDurSec As Double = count / CDbl(_bytesPerSec)

            ' ★ SELF-AUDIT FIX: the lead was subtracted from EVERY gap here —
            ' mathematically wrong (the lead is a one-time stream shift, not a
            ' per-gap discount). It also suppressed ALL silence insertion
            ' whenever gaps were under bufferDur+lead, which silently defeated
            ' the steering corrections too. The lead now lives in exactly ONE
            ' place: the pipe origin (BeginTimelines offsets in CaptureSession).
            Dim silenceNeededSec As Double = arrivalGapSec - bufferDurSec

            ' ★ cap raised 60s → 3600s: a music pause longer than 60s used to
            ' skip the fill ENTIRELY (owner's 02:24 run had a 48.78s tail —
            ' one second longer and the whole track would have shifted).
            If silenceNeededSec > 0.05 AndAlso silenceNeededSec <= 3600.0 Then
                Dim silBytes As Integer = CInt(silenceNeededSec * _bytesPerSec)
                silBytes -= (silBytes Mod _bytesPerFrame)
                If silBytes > 0 Then
                    If _silenceBuf Is Nothing OrElse _silenceCap < silBytes Then
                        _silenceCap = Math.Max(silBytes, 65536)
                        _silenceBuf = New Byte(_silenceCap - 1) {}
                    End If
                    Dim off As Integer = 0
                    While off < silBytes
                        Dim n As Integer = Math.Min(silBytes - off, _silenceBuf.Length)
                        _sink.Write(_silenceBuf, n)
                        Interlocked.Add(_silenceInsertedBytes, n)
                        off += n
                    End While
                End If
            End If

            _lastTicks = nowT
            Forward(buffer, count)
        End Sub

        Private Sub Forward(buffer As Byte(), count As Integer)
            _sink.Write(buffer, count)
            Interlocked.Add(_dataBytes, count)
        End Sub

        Private _startRequestedTicks As Long = 0

        ''' <summary>
        ''' Call at the StartRecording() CALL moment (proven model: call time,
        ''' NOT first-callback time). Establishes the wall-clock origin used for
        ''' first-callback measurement and pre-roll.
        ''' </summary>
        Public Sub MarkStart()
            _startRequestedTicks = Stopwatch.GetTimestamp()
        End Sub

        ''' <summary>
        ''' Close the timeline at stop: if the last buffer arrived before the
        ''' session ended, pad silence to NOW so the stream spans the full
        ''' session. Call once, after the capture has fully stopped.
        ''' </summary>
        Public Sub FinalizeToNow()
            FinalizeToTicks(Stopwatch.GetTimestamp())
        End Sub

        ''' <summary>
        ''' Close the timeline at an immutable session-stop QPC tick.
        ''' </summary>
        Public Sub FinalizeToTicks(targetTicks As Long)
            If targetTicks <= 0 Then targetTicks = Stopwatch.GetTimestamp()
            If _originTicks = 0 Then
                ' Never received anything — fully silent track. Pad the whole
                ' span from MarkStart to now so the stream exists (else ffmpeg
                ' gets zero packets for this input and aborts).
                If _startRequestedTicks > 0 Then
                    Dim spanSec As Double = (targetTicks - _startRequestedTicks) / Stopwatch.Frequency
                    If spanSec > 0 AndAlso spanSec <= 3600.0 Then
                        PadSilence(spanSec)
                    End If
                End If
                Return
            End If

            Dim tailSec As Double = (targetTicks - _lastTicks) / Stopwatch.Frequency
            If tailSec > 0.02 AndAlso tailSec <= 3600.0 Then
                PadSilence(tailSec)
            End If
            _evidence?.Invoke($"[tap:{_name}] closed: data={Interlocked.Read(_dataBytes):N0}B silence={Interlocked.Read(_silenceInsertedBytes):N0}B total={TotalDurationSec:0.00}s")
        End Sub

        Private Sub PadSilence(sec As Double)
            Dim silBytes As Long = CLng(sec * _bytesPerSec)
            silBytes -= (silBytes Mod _bytesPerFrame)
            If silBytes <= 0 Then Return
            If _silenceBuf Is Nothing Then
                _silenceCap = Math.Max(CInt(Math.Min(silBytes, 1048576L)), 65536)
                _silenceBuf = New Byte(_silenceCap - 1) {}
            End If
            Dim off As Long = 0
            While off < silBytes
                Dim n As Integer = CInt(Math.Min(silBytes - off, _silenceBuf.Length))
                _sink.Write(_silenceBuf, n)
                Interlocked.Add(_silenceInsertedBytes, n)
                off += n
            End While
            _evidence?.Invoke($"[tap:{_name}] tail padded {sec:0.00}s silence")
        End Sub

    End Class

End Namespace
