Option Strict On
Option Explicit On
Option Infer On

' AudioTapDeviceClock.vb — AudioTap v3: gap-fill from MEASURED device time.
'
' PHASE 13.3 (ShadowPlay single-clock design, docs/PHASE-13-SHADOWPLAY-CLOCK.md
' §3.2). This is the ClockMode=Device twin of the legacy AudioTap:
'
'   legacy (v2): Feed(buffer, count) — silence from STOPWATCH ARRIVAL deltas
'                (guess), pre-roll from MarkStart call time, clock steering,
'                drift baselines, idle gates — a graveyard of band-aids for
'                one missing timestamp.
'
'   v3 (this file): Feed(buffer, count, devicePositionFrames, qpcPosition100ns)
'                — WASAPI stamps every packet with the RENDER CURSOR of its
'                first frame (DevicePositionFrames — P13.1 evidence) plus
'                the qpc of the SAMPLING moment. The gap between packets is
'                measured on the CURSOR (content time — silence included),
'                hole = devPos − lastEnd (END→START, valid for variable
'                packet sizes). Silence math keys off CURSOR deltas ONLY —
'                ★ P13.4c: the OLD qpc-delta math was wrong in principle —
'                qpcPosition is WHEN the position was sampled, not where the
'                content is; the OWNER machine fired 933 backwards qpc stamps
'                per 38s session and the qpc timeline lost 5.65s of device
'                time. The qpc survives as the WALL ANCHOR (FirstQpc100ns →
'                SyncMath) + anomaly counter (evidence, no timeline effect).
'
' Delegated to AudioPositionTracker (P13.2, 13 synthetic-position tests):
' hole measurement, Risk-#2 zero-stamp fallback (continuity, no bogus gap),
' backwards-stamp max policy (never rewind), fallback-anchor re-anchor rule
' (no stream-sized fake hole on stampless-then-stamping drivers).
'
' KEPT from legacy (proven rules): block-align clamp, 3600s cap, zero
' silence buffer, evidence logging, dual sink (WAV sidecar + live-mux pipe).
'
' ★ P13.4b (field evidence, OWNER run 2026-08-28): the legacy 50ms
' minimum-gap threshold is GONE from the padding policy. In v3 a hole is
' HARDWARE-MEASURED (qpc END→START) — every hole is real elapsed device
' time whose content the endpoint did not deliver. Dropping sub-50ms
' holes silently COMPRESSED the track (~5% on the OWNER's machine:
' 335 holes / 7s tap, sync-verify drift −50ms/s accelerating). The
' threshold survives only as the per-hole EVIDENCE log filter (holes
' ≤ 50ms are counted + summed and reported in the close line instead).
'
' DELETED (impossible by construction here): clock steering, drift
' baseline, idle gate, Stopwatch arrival logic, MarkStart pre-roll
' guessing, FinalizeToNow wall-clock estimation (tail is now padded to
' the caller's session-end QPC — exact).
'
' Platform: pure .NET — runs on Linux CI with synthetic positions.

Imports System.Threading
Imports CaptureEngine.Audio.Wasapi

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>
    ''' Device-clock audio tap (v3): one capture track's gap-fill engine,
    ''' driven by WASAPI qpcPosition stamps. NOT thread-safe beyond the
    ''' packet-delivery thread + one Finalize call (same contract as legacy).
    ''' </summary>
    Public NotInheritable Class AudioTapDeviceClock

        ''' <summary>Holes longer than this are dropped entirely (a stalled
        ''' device is a log event, not minutes of padding). Proven value.
        ''' Everything between 0 and this cap is PADDED — P13.4b: a hole is
        ''' hardware-measured real time; dropping any of it compresses the
        ''' track (the sync-verify −50ms/s drift).</summary>
        Public Const MaxGapSec As Double = 3600.0

        ''' <summary>Holes at or below this are padded but NOT individually
        ''' logged (evidence noise filter — the OWNER machine fired 335
        ''' sub-50ms holes per 7s; they are counted and summed for the
        ''' close line instead). NOT a padding policy anymore.</summary>
        Public Const MinLogGapSec As Double = 0.05

        Private ReadOnly _name As String
        Private ReadOnly _bytesPerFrame As Integer        ' block align
        Private ReadOnly _bytesPerSec As Integer
        Private ReadOnly _sink As IAudioTapSink
        Private ReadOnly _evidence As Action(Of String)
        Private ReadOnly _tracker As AudioPositionTracker

        Private _silenceBuf As Byte() = Nothing
        Private _silenceCap As Integer = 0
        Private _silenceInsertedBytes As Long = 0
        Private _dataBytes As Long = 0
        Private _subThresholdHoles As Long = 0
        Private _subThresholdHole100ns As Long = 0
        Private _violationEvents As Long = 0
        Private _qpcAnomalyEvents As Long = 0

        ''' <summary>
        ''' Create the tap. The (sampleRate, channels, bitsPerSample) triple
        ''' must describe the BYTES that will be passed to Feed (post-
        ''' conversion PCM16 when the caller converts the float mix format),
        ''' because frame counts are derived from byte counts. The TRACKER
        ''' runs at the device sample rate — qpcPosition deltas are time,
        ''' independent of the byte format.
        ''' </summary>
        Public Sub New(name As String,
                       sampleRate As Integer,
                       channels As Integer,
                       bitsPerSample As Integer,
                       sink As IAudioTapSink,
                       Optional evidence As Action(Of String) = Nothing)
            If String.IsNullOrEmpty(name) Then Throw New ArgumentNullException(NameOf(name))
            If sampleRate <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(sampleRate))
            If channels <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(channels))
            If sink Is Nothing Then Throw New ArgumentNullException(NameOf(sink))
            _name = name
            _bytesPerFrame = Math.Max(1, channels * (bitsPerSample \ 8))
            _bytesPerSec = sampleRate * _bytesPerFrame
            _sink = sink
            _evidence = evidence
            _tracker = New AudioPositionTracker(sampleRate)
        End Sub

        ' ── Evidence surface (legacy-compatible names) ──────────────────

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

        ''' <summary>True once the first stamped packet anchored the timeline.</summary>
        Public ReadOnly Property Anchored As Boolean
            Get
                Return _tracker.Anchored
            End Get
        End Property

        ''' <summary>Device QPC (100ns) of the FIRST frame ever fed — the
        ''' SyncMath v2 audio anchor. 0 while unanchored.</summary>
        Public ReadOnly Property FirstQpc100ns As Long
            Get
                Return _tracker.FirstQpc100ns
            End Get
        End Property

        ''' <summary>END stamp of the last packet fed (100ns). 0 while unanchored.</summary>
        Public ReadOnly Property LastEnd100ns As Long
            Get
                Return _tracker.LastEnd100ns
            End Get
        End Property

        Public ReadOnly Property Packets As Long
            Get
                Return _tracker.Packets
            End Get
        End Property

        Public ReadOnly Property HolePackets As Long
            Get
                Return _tracker.GapPackets
            End Get
        End Property

        ''' <summary>Measured holes at or below the log threshold — padded
        ''' like every other hole (P13.4b) but only counted, not logged.
        ''' The gap between HolePackets and (HolePackets − SubThresholdHoles)
        ''' is what the old 50ms policy silently DROPPED.</summary>
        Public ReadOnly Property SubThresholdHoles As Long
            Get
                Return Interlocked.Read(_subThresholdHoles)
            End Get
        End Property

        ''' <summary>Sum of the sub-threshold holes, 100ns — real device time
        ''' the old policy would have dropped from the track.</summary>
        Public ReadOnly Property SubThresholdHole100ns As Long
            Get
                Return Interlocked.Read(_subThresholdHole100ns)
            End Get
        End Property

        Public ReadOnly Property TotalHole100ns As Long
            Get
                Return _tracker.TotalHole100ns
            End Get
        End Property

        Public ReadOnly Property StampFallbacks As Long
            Get
                Return _tracker.StampFallbacks
            End Get
        End Property

        Public ReadOnly Property MonotonicViolations As Long
            Get
                Return _tracker.MonotonicViolations
            End Get
        End Property

        ''' <summary>qpcPosition backwards events — wall-domain sampling noise,
        ''' ZERO timeline effect since P13.4c (evidence only).</summary>
        Public ReadOnly Property QpcAnomalies As Long
            Get
                Return _tracker.QpcAnomalies
            End Get
        End Property

        ''' <summary>Gaps reconstructed from QPC wall evidence while the
        ''' device cursor was frozen (Phase-A case 2) — the silent-clip bug
        ''' fix surface. Cursor-proven gaps are NOT counted here.</summary>
        Public ReadOnly Property IdleGapPackets As Long
            Get
                Return _tracker.IdleGapPackets
            End Get
        End Property

        ''' <summary>Packets whose gap judgment was suppressed because they
        ''' carried TimestampError (Phase-A: a known-error stamp judges
        ''' nothing).</summary>
        Public ReadOnly Property TimestampErrorPackets As Long
            Get
                Return _tracker.TimestampErrorPackets
            End Get
        End Property

        ''' <summary>Prime the audio timeline with the recording session's
        ''' QPC origin before the first packet arrives. This preserves silent
        ''' lead-in instead of treating the first delivered packet as t=0.</summary>
        Public Sub PrimeSessionStart(sessionStartQpc100ns As Long)
            _tracker.PrimeSessionStart(sessionStartQpc100ns)
        End Sub

        ' ── The v3 entry point ──────────────────────────────────────────

        ''' <summary>
        ''' Feed one raw PCM buffer together with the WASAPI stamps of its
        ''' first frame: the RENDER CURSOR (DevicePositionFrames — the content
        ''' timeline, P13.4c) and the wall anchor (qpcPosition100ns, consumed
        ''' only for FirstQpc100ns + anomaly evidence). Inserts measured-gap
        ''' silence BEFORE the buffer, then forwards both to the sink. Call
        ''' from the packet delivery thread, serially.
        ''' packetFlags: AUDCLNT_BUFFERFLAGS bits (WasapiPacketFlags) —
        ''' Phase-A: TimestampError suppresses ALL gap judgment for the
        ''' packet (OWNER rule). Legacy 4-arg callers default to 0.
        ''' </summary>
        Public Sub Feed(buffer As Byte(), count As Integer,
                        devicePositionFrames As Long, qpcPosition100ns As Long,
                        Optional packetFlags As Integer = 0)
            If buffer Is Nothing OrElse count <= 0 OrElse count > buffer.Length Then Return

            Dim frames As Integer = count \ _bytesPerFrame
            If frames <= 0 Then Return

            Dim report As AudioGapReport = _tracker.Feed(frames, devicePositionFrames, qpcPosition100ns, packetFlags)

            If report.ReAnchoredNow Then
                _evidence?.Invoke($"[tap3:{_name}] re-anchored after fallback anchor (driver began stamping) — no hole fabricated")
            ElseIf report.AnchoredNow Then
                _evidence?.Invoke($"[tap3:{_name}] anchored at qpc {report.LastEnd100ns - report.BufferDur100ns} (100ns); buffer {report.BufferDur100ns / 10000.0:0.0}ms")
            End If

            ' The hole (END→START) IS the silence. P13.4b: EVERY measured
            ' hole is padded — the hardware said that time elapsed; dropping
            ' it desyncs the track against the video. Only the log line is
            ' thresholded (MinLogGapSec), never the padding. Phase-A: the
            ' hole may come from the cursor (case 1) or — when the cursor
            ' froze — from QPC wall evidence (case 2, the silent-clip fix).
            If report.Hole100ns > 0 Then
                Dim holeSec As Double = report.Hole100ns / 10000000.0
                If holeSec <= MaxGapSec Then
                    Dim silBytes As Long = CLng(holeSec * _bytesPerSec)
                    silBytes -= (silBytes Mod _bytesPerFrame)
                    If silBytes > 0 Then
                        WriteSilence(CInt(silBytes))
                        If holeSec > MinLogGapSec Then
                            Dim kind As String = If(report.IdleGapUsedQpc,
                                "idle hole (cursor frozen, qpc evidence)", "measured hole")
                            _evidence?.Invoke($"[tap3:{_name}] {kind} {holeSec * 1000.0:0}ms → padded {silBytes}B silence")
                        Else
                            Interlocked.Increment(_subThresholdHoles)
                            Interlocked.Add(_subThresholdHole100ns, report.Hole100ns)
                        End If
                    ElseIf holeSec <= MinLogGapSec Then
                        Interlocked.Increment(_subThresholdHoles)
                        Interlocked.Add(_subThresholdHole100ns, report.Hole100ns)
                    End If
                Else
                    _evidence?.Invoke($"[tap3:{_name}] hole {holeSec:0.0}s exceeds {MaxGapSec:0}s cap — NOT padded (device stall?)")
                End If
            End If

            If report.MonotonicViolation Then
                Dim ev As Long = Interlocked.Increment(_violationEvents)
                ' Throttle: the OWNER machine fired 933 of these per session.
                If ev = 1L OrElse ev Mod 50L = 0L Then
                    _evidence?.Invoke($"[tap3:{_name}] cursor backwards/overlap absorbed (no rewind) — event #{ev}")
                End If
            End If
            If report.QpcAnomaly Then
                Dim qe As Long = Interlocked.Increment(_qpcAnomalyEvents)
                If qe = 1L OrElse qe Mod 50L = 0L Then
                    _evidence?.Invoke($"[tap3:{_name}] qpc jitter (wall-domain noise, no timeline effect) — event #{qe}")
                End If
            End If
            If report.StampFallbackUsed Then
                _evidence?.Invoke($"[tap3:{_name}] zero-stamp packet — continuity assumed (no hole)")
            End If
            If report.TimestampErrorSuppressed Then
                _evidence?.Invoke($"[tap3:{_name}] TimestampError packet — gap judgment suppressed (continuity)")
            End If

            _sink.Write(buffer, count)
            Interlocked.Add(_dataBytes, count)
        End Sub

        ''' <summary>
        ''' Close the timeline at stop: pad silence from the last packet's
        ''' END to the session-end QPC (exact — no wall-clock estimation).
        ''' Pass the session start QPC as well: if no packet ever arrived,
        ''' the whole span is padded so the mux still gets a valid silent
        ''' track instead of zero packets.
        ''' Stamps in 100ns (use WasapiPositionCapture.QpcTicksTo100ns on
        ''' Stopwatch.GetTimestamp() values — same QPC domain).
        ''' </summary>
        Public Sub FinalizeTo100ns(sessionStartQpc100ns As Long, sessionEndQpc100ns As Long)
            If sessionEndQpc100ns <= sessionStartQpc100ns Then Return

            If Not _tracker.Anchored Then
                PadSilence((sessionEndQpc100ns - sessionStartQpc100ns) / 10000000.0,
                           "never-anchored track — full-span silence")
                Return
            End If

            Dim tail100ns As Long = sessionEndQpc100ns - _tracker.LastEnd100ns
            If tail100ns > 0 Then
                PadSilence(tail100ns / 10000000.0, "tail to session end")
            End If
            _evidence?.Invoke($"[tap3:{_name}] closed: data={Interlocked.Read(_dataBytes):N0}B silence={Interlocked.Read(_silenceInsertedBytes):N0}B total={TotalDurationSec:0.00}s holes={HolePackets} idle={IdleGapPackets} (sub-50ms: {Interlocked.Read(_subThresholdHoles)} = {Interlocked.Read(_subThresholdHole100ns) / 100000.0:0.0}ms) fallbacks={StampFallbacks} cursorViolations={MonotonicViolations} qpcJitter={QpcAnomalies} tsErr={TimestampErrorPackets}")
        End Sub

        ' ── Internals ───────────────────────────────────────────────────

        Private Sub WriteSilence(silBytes As Integer)
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
        End Sub

        Private Sub PadSilence(sec As Double, reason As String)
            If sec <= 0 OrElse sec > MaxGapSec Then Return
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
            _evidence?.Invoke($"[tap3:{_name}] padded {sec:0.00}s silence ({reason})")
        End Sub

    End Class

End Namespace
