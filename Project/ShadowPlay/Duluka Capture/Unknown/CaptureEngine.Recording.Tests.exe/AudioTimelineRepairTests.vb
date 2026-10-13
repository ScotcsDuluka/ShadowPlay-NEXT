Option Strict On
Option Explicit On
Option Infer On

' AudioTimelineRepairTests.vb — [P13-AUDIO-TIMELINE] unit tests.
'
' AudioTimelineRepair is the OBS-rule gap repair ("silence is TIME") used
' by the legacy AudioFileWriter. It is PURE (caller supplies Stopwatch
' ticks, the class reads no clock), so every rule is testable on Linux:
'
'   1. first gap  = StartRecording → first callback, prefill rule —
'                   NO 50ms cutoff (deterministic device warm-up)
'   2. continuous callbacks (Δ = buffer span) → zero silence
'   3. mid-stream hole = Δarrival − bufferDuration, written BEFORE buffer
'   4. sub-50ms deltas are jitter → ignored (strictly-greater threshold)
'   5. >3600s stalls are NOT padded (device stall = log event)
'   6. every silence byte count floored to a whole PCM frame
'   7. Resync() re-anchors at stop time (no phantom hole)
'   8. evidence surface (Anchored / GapCount / SilenceRecommendedBytes)
'
' FLOAT SAFETY (why every number here looks "weird"):
'   OnCallback computes (Δticks / freq − bytes / BPS) × BPS in doubles and
'   floors with CLng + block-align. A non-dyadic seconds value (0.03, 0.24,
'   0.05 …) lands the product one ULP below an integer → CLng truncates →
'   the aligned result shifts by one frame and an exact-byte assertion
'   flakes. EVERY timespan in these tests is therefore a dyadic rational
'   (1/8, 1/16, 3/8 …) with freq = 10^6 (1 tick = 1 µs) and
'   BPS = 192000 = 2^7 × 1500, so Δ/BPS, the subtraction and the product
'   are ALL exact doubles → CLng is exact → assertions are deterministic.
'
'   Base buffer = 125ms = 24000 bytes = 125000 ticks (the WASAPI span).

Imports System
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording.Tests

    Friend Module AudioTimelineRepairTests

        Private Const FREQ As Double = 1000000.0        ' 1 tick = 1 µs
        Private Const BPS As Long = 192000L             ' 48k stereo 16-bit
        Private Const FRAME As Integer = 4
        Private Const START As Long = 1000000L          ' session origin 1.0s
        Private Const BUF As Long = 24000L              ' 125ms of 48k/st/16
        Private Const BUF_TICKS As Long = 125000L       ' 0.125s (dyadic 1/8)

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── AudioTimelineRepair (OBS gap-repair rules) ──")
            TestRunner.RunTest("TLR: first gap = prefill, no 50ms cutoff", AddressOf Test_FirstGapPrefill)
            TestRunner.RunTest("TLR: continuous callbacks → zero silence", AddressOf Test_ContinuousZero)
            TestRunner.RunTest("TLR: mid-stream hole repaired before buffer", AddressOf Test_MidStreamHole)
            TestRunner.RunTest("TLR: sub-50ms jitter ignored (strict >)", AddressOf Test_JitterThreshold)
            TestRunner.RunTest("TLR: >3600s stall not padded", AddressOf Test_StallCap)
            TestRunner.RunTest("TLR: silence floored to whole PCM frames", AddressOf Test_FrameAlignment)
            TestRunner.RunTest("TLR: Resync re-anchors (no phantom hole)", AddressOf Test_Resync)
            TestRunner.RunTest("TLR: evidence surface accumulates", AddressOf Test_EvidenceSurface)
        End Sub

        Private Function NewRepair() As AudioTimelineRepair
            Return New AudioTimelineRepair(FREQ, BPS, FRAME, START)
        End Function

        Private Sub Test_FirstGapPrefill()
            Dim r = NewRepair()

            ' 1.0s warm-up → exactly 1.0s of silence bytes (192000, aligned)
            Dim sil As Long = r.OnCallback(START + 1000000L, BUF)
            TestRunner.Assert(sil = 192000L, $"first gap 1.0s → 192000, got {sil}")

            ' First-gap rule has NO 50ms cutoff: a 31.25ms (1/32s) warm-up
            ' must still prefill 6000 bytes — real head time for mux -ss.
            Dim r2 = NewRepair()
            Dim sil2 As Long = r2.OnCallback(START + 31250L, BUF)
            TestRunner.Assert(sil2 = 6000L, $"first gap 31.25ms → 6000 (no cutoff), got {sil2}")

            ' Zero warm-up (first callback == start) → nothing to prefill
            Dim r3 = NewRepair()
            TestRunner.Assert(r3.OnCallback(START, BUF) = 0, "zero warm-up → 0")
        End Sub

        Private Sub Test_ContinuousZero()
            Dim r = NewRepair()
            ' First callback at start (no warm-up) → 0
            TestRunner.Assert(r.OnCallback(START, BUF) = 0, "anchor callback → 0")
            ' Callback exactly one buffer-span later → hole = 0.125 − 0.125 = 0
            TestRunner.Assert(r.OnCallback(START + BUF_TICKS, BUF) = 0, "continuous → 0")
            TestRunner.Assert(r.OnCallback(START + 2 * BUF_TICKS, BUF) = 0, "continuous 2 → 0")
            ' Δ shorter than the buffer span (scheduler catching up) → the
            ' computed hole is NEGATIVE → clamped out by the threshold → 0
            TestRunner.Assert(r.OnCallback(START + 2 * BUF_TICKS + 31250L, BUF) = 0, "catch-up Δ → 0")
        End Sub

        Private Sub Test_MidStreamHole()
            Dim r = NewRepair()
            TestRunner.Assert(r.OnCallback(START, BUF) = 0, "anchor callback → 0")

            ' 500ms arrival gap, buffer covers only 125ms → hole = 375ms.
            ' That silence belongs BEFORE this buffer (the [speech][speech]
            ' evidence: without repair the span vanishes from its position).
            Dim sil As Long = r.OnCallback(START + 500000L, BUF)
            TestRunner.Assert(sil = 72000L, $"hole 375ms → 72000, got {sil}")

            ' The hole is consumed: the NEXT continuous callback stays 0.
            TestRunner.Assert(r.OnCallback(START + 625000L, BUF) = 0, "post-repair continuous → 0")
        End Sub

        Private Sub Test_JitterThreshold()
            Dim r = NewRepair()
            TestRunner.Assert(r.OnCallback(START, BUF) = 0, "anchor → 0")

            ' Keep the timeline continuous so the anchor sits at START+125000.
            TestRunner.Assert(r.OnCallback(START + BUF_TICKS, BUF) = 0, "continuous → 0")

            ' hole 31.25ms (1/32) < 50ms → jitter, ignored
            ' Δ = 0.125 + 0.03125 = 0.15625 → ticks 156250
            TestRunner.Assert(r.OnCallback(START + 156250L, BUF) = 0, "31.25ms hole → 0")

            ' hole 62.5ms (1/16) > 50ms → repaired: 0.0625 × 192000 = 12000.
            ' NOTE the anchor advanced to START+156250 above → Δ = 0.1875
            ' → callback at START + 343750 (every OnCallback moves the anchor).
            Dim sil As Long = r.OnCallback(START + 343750L, BUF)
            TestRunner.Assert(sil = 12000L, $"62.5ms hole → 12000, got {sil}")
            ' NOTE: the exact 50ms boundary itself is intentionally NOT
            ' asserted — 0.05 is not dyadic, so the strict-> comparison at
            ' the boundary is a float-domain question, not a contract one.
        End Sub

        Private Sub Test_StallCap()
            ' Mid-stream: hole beyond 3600s = device stall → NOT padded.
            ' Δ = 3600.625s (dyadic), buffer 0.125 → hole = 3600.5 > cap.
            Dim r = NewRepair()
            TestRunner.Assert(r.OnCallback(START, BUF) = 0, "anchor → 0")
            Dim sil As Long = r.OnCallback(START + 3600625000L, BUF)
            TestRunner.Assert(sil = 0, $"3600.5s stall → 0, got {sil}")

            ' First-gap variant: a >1h warm-up is also refused
            Dim r2 = NewRepair()
            TestRunner.Assert(r2.OnCallback(START + 4000000000L, BUF) = 0, "1h+ warm-up → 0")
        End Sub

        Private Sub Test_FrameAlignment()
            ' 44.1kHz stereo 16-bit → 176400 B/s. Warm-up 1/8s → 22050 raw
            ' → floored to the 4-aligned 22048 (WAV stays block-aligned).
            Dim r As New AudioTimelineRepair(FREQ, 176400L, 4, START)
            Dim sil As Long = r.OnCallback(START + 125000L, BUF)
            TestRunner.Assert(sil = 22048L, $"44.1k 0.125s gap → 22048, got {sil}")
            TestRunner.Assert(sil Mod 4 = 0, "result is whole PCM frames")
        End Sub

        Private Sub Test_Resync()
            Dim r = NewRepair()
            TestRunner.Assert(r.OnCallback(START, BUF) = 0, "anchor → 0")

            ' Stop-time anchor: capture stopped 5s after start. Without
            ' Resync the next callback would see a ~5s "hole" and pad it;
            ' with it the timeline resumes cleanly at the new anchor.
            r.Resync(START + 5000000L)
            TestRunner.Assert(r.OnCallback(START + 5125000L, BUF) = 0, "post-Resync continuous → 0")
            TestRunner.Assert(r.OnCallback(START + 5625000L, BUF) = 72000L, "post-Resync hole still repaired")
        End Sub

        Private Sub Test_EvidenceSurface()
            Dim r = NewRepair()
            TestRunner.Assert(Not r.Anchored, "not anchored before first callback")

            Dim s1 As Long = r.OnCallback(START + 500000L, BUF)   ' 0.5s warm-up
            TestRunner.Assert(s1 = 96000L, "0.5s warm-up → 96000")
            TestRunner.Assert(r.Anchored, "anchored after first callback")
            TestRunner.Assert(r.GapCount = 1, "one gap counted")
            TestRunner.Assert(r.SilenceRecommendedBytes = s1, "bytes accumulate")
            TestRunner.AssertNear(r.LastGapSec, 0.5, 0.0005, "last gap seconds")

            ' Δ 375ms − 125ms buffer → hole 250ms → 48000 (dyadic-exact)
            Dim s2 As Long = r.OnCallback(START + 875000L, BUF)
            TestRunner.Assert(s2 = 48000L, $"hole 250ms → 48000, got {s2}")
            TestRunner.Assert(r.GapCount = 2, "second gap counted")
            TestRunner.Assert(r.SilenceRecommendedBytes = s1 + s2, "bytes accumulate (2)")
            TestRunner.AssertNear(r.LastGapSec, 0.25, 0.0005, "last gap updated")
        End Sub

    End Module

End Namespace
