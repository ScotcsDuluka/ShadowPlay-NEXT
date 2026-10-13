Option Strict On
Option Explicit On
Option Infer On

' AudioTimelineDeviceClockTests.vb — OWNER-spec deterministic sample-timeline
' tests for the Device-clock path (AudioPositionTracker + AudioTapDeviceClock).
'
' NO hardware, NO threads, NO audio device: WASAPI stamps are synthesized on
' a virtual clock and the sink is an in-memory byte accumulator, so the test
' runs on Linux CI and reproduces the OWNER's field bug exactly.
'
' OWNER contract (2026-08-31): three 480-frame packets (10ms @ 48kHz) arrive
' at t = 0s / 10s / 15s. The reconstructed byte stream MUST place:
'
'   audio1   [0.000s, 0.010s)
'   silence  [0.010s, 10.000s)     <- idle gap 1 (real position)
'   audio2   [10.000s, 10.010s)
'   silence  [10.010s, 15.000s)    <- idle gap 2 (real position)
'   audio3   [15.000s, 15.010s)
'   (nothing beyond 15.010s except nothing — session ends there)
'
' byte offsets (192000 B/s): audio1 [0,1920) silence [1920,1920000)
' audio2 [1920000,1921920) silence [1921920,2880000) audio3 [2880000,2881920)
' total = 2881920 bytes.
'
' FIXTURE RULE (first real run on 4023b9f, Linux dotnet 10.0.400 — OWNER
' gate 2026-08-31): the device cursor base MUST be non-zero. 0 is the
' tracker's "no cursor" sentinel (AudioPositionTracker Risk #2 fallback):
' an anchor with devPos=0 engages the fallback-re-anchor path, whose
' "no hole fabricated" rule then MASKS gap1 entirely (first run: A failed
' at byte 1920 with audio sitting there, stream total coincidentally
' exact). A real loopback cursor is free-running — it is never 0 past
' device init — so CursorBase below models reality and keeps the sentinel
' path out of these scenarios. The sentinel itself is proven by P13.2
' tests elsewhere and is NOT a bug.
'
' Scenarios (the two silence models the endpoint actually exhibits —
' docs/PHASE-13-SHADOWPLAY-CLOCK.md):
'
'   A  "endpoint renders silence" — packets keep flowing, the device cursor
'      ADVANCES through the quiet phase. Timeline must be built from the
'      cursor deltas (P13.4c design). MUST PASS on the current build —
'      this is the regression guard for the Phase-A fix.
'
'   A2 "continuous SILENT packets" — content is zero but the cursor advances
'      in lockstep. NO silence may be synthesized on top (anti-overfill
'      guard: the Phase-A fix must never double-pad, and must never pad
'      from qpc deltas when the cursor already accounts for the time).
'
'   B  "endpoint idles" — NO packets flow and the render cursor FREEZES
'      (P13.4b field evidence: "the first packet anchors seconds late";
'      the OWNER's real-time [silence][voice][silence][voice] becomes
'      [voice][voice][long tail silence] in the file). KNOWN-FAIL on the
'      pre-fix build — the failure message IS the deterministic bug
'      reproduction. It flips to PASS automatically the moment the
'      Phase-A fix lands, and that PASS line is the fix's acceptance
'      evidence.
'
'   C  "freeze → resume cycles" — locks the Phase-A REBASE rule: after
'      an idle reconstruction the raw cursor domain is stale by the
'      synthesized amount; packets resuming from the frozen spot must
'      land exactly on the virtual timeline (zero hole, zero violation,
'      zero double-pad) across MULTIPLE idle episodes.
'
'   D  "TimestampError judges no gap" — the OWNER rule: a packet with a
'      known-error stamp is placed by continuity and never used as gap
'      evidence; the next TRUSTED stamp pads from the last trusted
'      wall anchor.

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports CaptureEngine.Audio.Wasapi
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording.Tests

    Friend Module AudioTimelineDeviceClockTests

        Private Const Rate As Integer = 48000
        Private Const Channels As Integer = 2
        Private Const Bits As Integer = 16
        Private ReadOnly BytesPerFrame As Integer = Channels * (Bits \ 8)   ' 4
        Private ReadOnly BytesPerSec As Integer = Rate * BytesPerFrame      ' 192000
        Private Const Frames As Integer = 480                               ' 10ms @ 48kHz
        Private ReadOnly Bytes10ms As Integer = Frames * BytesPerFrame      ' 1920

        ' 100ns domain: 1s = 10,000,000; 10ms = 100,000 (tracker invariant).
        Private Const OneSec As Long = 10_000_000L
        Private Const TenMs As Long = 100_000L
        Private Const SessionLen100ns As Long = 150_100_000L    ' 15.01s

        ' Contract byte offsets (192000 B/s).
        Private Const Audio1At As Long = 0L
        Private Const Audio2At As Long = 1_920_000L             ' 10.000s
        Private Const Audio3At As Long = 2_880_000L             ' 15.000s
        Private Const TotalBytes As Long = 2_881_920L           ' 15.01s * 192000

        Private Const AudioFill As Byte = &HAAI                 ' tone marker
        Private Const SilenceFill As Byte = &H0I

        ''' <summary>Free-running cursor base — a real endpoint's first packet
        ''' carries an arbitrary non-zero render position. MUST NOT be 0:
        ''' devPos=0 is the tracker's no-cursor sentinel (see header).</summary>
        Private ReadOnly CursorBase As Long = 1_000_000L

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── Audio sample timeline (Device clock, deterministic — OWNER spec t=0/10/15s) ──")
            TestRunner.RunTest("TIMELINE A: cursor advances → gap silence lands at REAL position",
                               AddressOf Test_A_CursorAdvances)
            TestRunner.RunTest("TIMELINE A2: continuous silent packets → zero synthesized silence (no double-pad)",
                               AddressOf Test_A2_SilentPacketsNoOverfill)
            TestRunner.RunKnownFail("TIMELINE B: cursor freezes (endpoint idle) → gap silence still lands at REAL position",
                                    AddressOf Test_B_CursorFrozen,
                                    "Phase-A fix target — deterministic repro of [voice][voice][long tail silence]")
            TestRunner.RunTest("TIMELINE C: freeze → resume → freeze → resume (rebase continuity)",
                               AddressOf Test_C_FreezeResumeCycle)
            TestRunner.RunTest("TIMELINE D: TimestampError packet judges no gap (OWNER rule)",
                               AddressOf Test_D_TimestampErrorSuppressed)
        End Sub

        ' ── Harness ─────────────────────────────────────────────────────

        ''' <summary>In-memory IAudioTapSink: accumulates the reconstructed
        ''' byte stream in delivery order. Single-threaded by contract.</summary>
        Private NotInheritable Class CollectingSink
            Implements IAudioTapSink

            Public ReadOnly Stream As New MemoryStream()

            Public Sub Write(data As Byte(), count As Integer) Implements IAudioTapSink.Write
                Stream.Write(data, 0, count)
            End Sub
        End Class

        ''' <summary>One 10ms packet filled with the given byte pattern.</summary>
        Private Function MakePacket(fill As Byte) As Byte()
            Dim b(Bytes10ms - 1) As Byte
            For i As Integer = 0 To b.Length - 1
                b(i) = fill
            Next
            Return b
        End Function

        Private Function BytesOf(sink As CollectingSink) As Byte()
            Return sink.Stream.ToArray()
        End Function

        ''' <summary>Every byte in [start, start + Bytes10ms) must be the
        ''' audio marker. Message reports the ACTUAL placement so the failure
        ''' line doubles as the bug reproduction report.</summary>
        Private Sub ExpectAudioSpan(b As Byte(), startByte As Long, label As String)
            If startByte + Bytes10ms > b.Length Then
                Throw New Exception($"{label}: contract wants audio at byte {startByte:N0} " &
                                    $"but the stream is only {b.Length:N0} bytes — gap silence " &
                                    $"MIGRATED toward the tail (timeline compressed)")
            End If
            For i As Long = 0 To Bytes10ms - 1
                Dim actual As Byte = b(CInt(startByte + i))
                If actual <> AudioFill Then
                    Throw New Exception($"{label}: contract wants audio at byte {startByte:N0} " &
                                        $"(sample time {startByte / CDbl(BytesPerSec):0.000}s) but found " &
                                        $"{actual:X2} — audio actually sits elsewhere (offset {IndexAudioStart(b, startByte):N0})")
                End If
            Next
        End Sub

        ''' <summary>First audio-marker byte at or after fromByte — for
        ''' diagnostics in failure messages.</summary>
        Private Function IndexAudioStart(b As Byte(), fromByte As Long) As Long
            For i As Long = fromByte To b.Length - 1L
                If b(CInt(i)) = AudioFill Then Return i
            Next
            Return -1L
        End Function

        ''' <summary>Every byte in [from, toExclusive) must be zero — the
        ''' reconstructed gap silence must be EXACTLY here, not elsewhere.</summary>
        Private Sub ExpectSilenceSpan(b As Byte(), fromByte As Long, toExclusive As Long, label As String)
            For i As Long = fromByte To Math.Min(toExclusive, b.Length) - 1L
                If b(CInt(i)) <> SilenceFill Then
                    Throw New Exception($"{label}: contract wants silence at byte {i:N0} " &
                                        $"(sample time {i / CDbl(BytesPerSec):0.000}s) but found {b(CInt(i)):X2}")
                End If
            Next
        End Sub

        ''' <summary>The OWNER contract, asserted end-to-end.</summary>
        Private Sub AssertOwnerContract(b As Byte(), tap As AudioTapDeviceClock, scenario As String)
            If b.Length <> TotalBytes Then
                Dim actualA2 As Long = IndexAudioStart(b, Audio1At + Bytes10ms)
                Dim actualTime As String = If(actualA2 < 0, "NONE", $"{actualA2 / CDbl(BytesPerSec):0.000}s (byte {actualA2:N0})")
                Throw New Exception($"{scenario}: stream must be {TotalBytes:N0} bytes (15.01s), got {b.Length:N0}; " &
                                    $"audio2 contract = 10.000s, actual first audio after 0.010s = {actualTime} " &
                                    $"→ gap silence migrated to the tail")
            End If
            ExpectAudioSpan(b, Audio1At, $"{scenario}: audio1")
            ExpectSilenceSpan(b, Audio1At + Bytes10ms, Audio2At, $"{scenario}: gap1 silence")
            ExpectAudioSpan(b, Audio2At, $"{scenario}: audio2")
            ExpectSilenceSpan(b, Audio2At + Bytes10ms, Audio3At, $"{scenario}: gap2 silence")
            ExpectAudioSpan(b, Audio3At, $"{scenario}: audio3")
            TestRunner.Assert(tap.MonotonicViolations = 0,
                              $"{scenario}: cursor must never go backwards in this scenario")
        End Sub

        ' ── Scenario A: cursor advances through silence ─────────────────

        ''' <summary>The endpoint keeps delivering packets while silent; the
        ''' device render cursor advances across both gaps. P13.4c design:
        ''' hole = devPos − lastEnd is measured in CONTENT time.</summary>
        Private Sub Test_A_CursorAdvances()
            Dim sink As New CollectingSink()
            Dim tap As New AudioTapDeviceClock("sys", Rate, Channels, Bits, sink)
            Dim t0 As Long = 800_000_000L        ' arbitrary wall anchor (80s), all math relative

            ' t=0s: audio1, cursor [base, base+480)
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase, t0)
            ' t=10s: audio2, cursor jumped to base+480000 — 9.99s hole measured
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480_000L, t0 + 100_000_000L)
            ' t=15s: audio3, cursor at base+720000 — 4.99s hole measured
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 720_000L, t0 + 150_000_000L)
            ' session end = 15.01s; last cursor end = 720480 frames = 15.01s → zero tail
            tap.FinalizeTo100ns(t0, t0 + SessionLen100ns)

            AssertOwnerContract(BytesOf(sink), tap, "A")
            TestRunner.Assert(tap.HolePackets = 2, "A: exactly 2 measured holes, got " & tap.HolePackets)
            TestRunner.Assert(tap.SilenceInsertedBytes = 1_918_080L + 958_080L,
                              $"A: synthesized silence = 1918080+958080, got {tap.SilenceInsertedBytes:N0}")
        End Sub

        ' ── Scenario A2: continuous silent packets ──────────────────────

        ''' <summary>SILENT-flag-style stream: 1500 genuine zero-content
        ''' packets, cursor in lockstep. The timeline is already complete —
        ''' synthesizing ANY silence on top would double-pad and drift the
        ''' track. Direct guard against a max(cursor,qpc) over-fill.</summary>
        Private Sub Test_A2_SilentPacketsNoOverfill()
            Dim sink As New CollectingSink()
            Dim tap As New AudioTapDeviceClock("sys", Rate, Channels, Bits, sink)
            Dim t0 As Long = 800_000_000L
            Dim zeros As Byte() = MakePacket(SilenceFill)

            For i As Integer = 0 To 1499        ' 0ms .. 14990ms, cursor base+i*480
                tap.Feed(zeros, Bytes10ms, CursorBase + CLng(i) * Frames, t0 + CLng(i) * TenMs)
            Next
            tap.FinalizeTo100ns(t0, t0 + 150_000_000L)   ' exactly 15.00s

            ' VB operator precedence: * binds TIGHTER than \ — without the
            ' parens this evaluated 150M \ (10M * 192000) = 0 and failed a
            ' stream that was byte-exact (first real run on 4023b9f).
            TestRunner.Assert(sink.Stream.Length = (150_000_000L \ 10_000_000L) * BytesPerSec,
                              $"A2: stream must be 1500 packets = 2,880,000 bytes, got {sink.Stream.Length:N0}")
            TestRunner.Assert(tap.SilenceInsertedBytes = 0,
                              $"A2: cursor accounted for ALL time — synthesized silence must be 0, got {tap.SilenceInsertedBytes:N0}")
            TestRunner.Assert(tap.HolePackets = 0,
                              $"A2: zero holes expected (lockstep cursor), got {tap.HolePackets}")
            TestRunner.Assert(tap.MonotonicViolations = 0,
                              $"A2: no cursor violations expected, got {tap.MonotonicViolations}")
        End Sub

        ' ── Scenario B: cursor freezes (endpoint idle) — the bug ────────

        ''' <summary>P13.4b field model: while nothing plays, the loopback
        ''' endpoint delivers NOTHING and its render cursor does not move.
        ''' qpcPosition still advances (it is WHEN the position was sampled).
        ''' Current build: feed2 hole = 480−480 = 0, feed3 hole = 480−960 =
        ''' −480 (monotonic violation, absorbed) → no mid-gap silence → audio2
        ''' lands at 0.010s → Finalize pads 14.99s to the tail (stream 2,883,840).
        ''' Contract (Phase-A fix): audio2 MUST start at 10.000s.</summary>
        Private Sub Test_B_CursorFrozen()
            Dim sink As New CollectingSink()
            Dim tap As New AudioTapDeviceClock("sys", Rate, Channels, Bits, sink)
            Dim t0 As Long = 800_000_000L

            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase, t0)              ' t=0s,  cursor [base, base+480)
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480L, t0 + 100_000_000L)   ' t=10s, cursor STILL base+480 (idle)
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480L, t0 + 150_000_000L)   ' t=15s, cursor STILL base+480
            tap.FinalizeTo100ns(t0, t0 + SessionLen100ns)

            AssertOwnerContract(BytesOf(sink), tap, "B")
        End Sub

        ' ── Scenario C: freeze → resume cycles (rebase continuity) ──────

        ''' <summary>The Phase-A idle reconstruction advances the VIRTUAL
        ''' timeline past the device's frozen cursor domain. This scenario
        ''' drives two full idle episodes and demands that packets whose raw
        ''' cursor resumes from the frozen spot land EXACTLY on the virtual
        ''' timeline afterwards (the rebase rule) — plus the full byte
        ''' contract at every position. Total: 20.02s = 3,843,840 bytes.</summary>
        Private Sub Test_C_FreezeResumeCycle()
            Dim sink As New CollectingSink()
            Dim tap As New AudioTapDeviceClock("sys", Rate, Channels, Bits, sink)
            Dim t0 As Long = 800_000_000L

            ' t=0s: audio1, cursor [base, base+480)
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase, t0)
            ' t=10s: IDLE episode 1 — cursor frozen at base+480 → pad 9.99s;
            ' audio2 lands at [10.000s, 10.010s); rebase = 479,520 frames.
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480L, t0 + 100_000_000L)
            ' t=15s: cursor RESUMED (device rendered audio2 at raw [480,960))
            ' but still idle since — wall says 5s passed → pad 4.99s; audio3
            ' at [15.000s, 15.010s); rebase rewritten to 719,520.
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 960L, t0 + 150_000_000L)
            ' t=20s: IDLE episode 3 — cursor frozen again at base+1440 → pad
            ' 4.99s; audio4 at [20.000s, 20.010s); rebase rewritten to 959,040.
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 1440L, t0 + 200_000_000L)
            ' t=20.01s: cursor resumed (rendered audio4) — wall gap == the
            ' packet's own span → plain continuity, ZERO pad; audio5 at
            ' [20.010s, 20.020s).
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 1920L, t0 + 200_100_000L)
            ' Session ends exactly at audio5's end → zero tail.
            tap.FinalizeTo100ns(t0, t0 + 200_200_000L)

            Dim b As Byte() = BytesOf(sink)
            ' bytes = ticks × B/s ÷ 1e7 (multiply FIRST: 20.02s is not a
            ' whole second and \ floors — and * binds tighter than \ anyway).
            Dim total As Long = 200_200_000L * BytesPerSec \ 10_000_000L   ' 3,843,840
            TestRunner.Assert(b.Length = total,
                              $"C: stream must be {total:N0} bytes (20.02s), got {b.Length:N0}")
            ExpectAudioSpan(b, 0L, "C: audio1 @0.000s")
            ExpectSilenceSpan(b, Bytes10ms, 1_920_000L, "C: gap1 @0.010–10.000s")
            ExpectAudioSpan(b, 1_920_000L, "C: audio2 @10.000s")
            ExpectSilenceSpan(b, 1_921_920L, 2_880_000L, "C: gap2 @10.010–15.000s")
            ExpectAudioSpan(b, 2_880_000L, "C: audio3 @15.000s")
            ExpectSilenceSpan(b, 2_881_920L, 3_840_000L, "C: gap3 @15.010–20.000s")
            ExpectAudioSpan(b, 3_840_000L, "C: audio4 @20.000s")
            ExpectAudioSpan(b, 3_841_920L, "C: audio5 @20.010s (contiguous after resume)")
            TestRunner.Assert(tap.HolePackets = 0, "C: all gaps were idle-reconstructed, cursor-proven = 0")
            TestRunner.Assert(tap.IdleGapPackets = 3, $"C: exactly 3 idle gaps, got {tap.IdleGapPackets}")
            TestRunner.Assert(tap.SilenceInsertedBytes = 3_834_240L,
                              $"C: silence = 9.99+4.99+4.99s, got {tap.SilenceInsertedBytes:N0}")
            TestRunner.Assert(tap.MonotonicViolations = 0,
                              "C: a frozen cursor is not a rewind — zero violations")
        End Sub

        ' ── Scenario D: TimestampError judges no gap (OWNER rule) ───────

        ''' <summary>A TimestampError stamp may not judge any gap (OWNER:
        ''' "ห้ามใช้ packet นี้ตัดสิน gap"). The packet's content still
        ''' exists — continuity placement — and the NEXT good packet pads
        ''' from the last TRUSTED wall anchor. The honest cost: the error
        ''' packet's 10ms is displaced; the timeline length and every
        ''' trusted position stay exact. Total = contract + one packet.</summary>
        Private Sub Test_D_TimestampErrorSuppressed()
            Dim sink As New CollectingSink()
            Dim tap As New AudioTapDeviceClock("sys", Rate, Channels, Bits, sink)
            Dim t0 As Long = 800_000_000L

            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase, t0)
            ' t=10s: frozen cursor + TimestampError → NO pad may fire.
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480L,
                     t0 + 100_000_000L, WasapiPacketFlags.TimestampError)
            ' t=15s: GOOD stamp — judges against the trusted anchor (t0):
            ' idle = 15s − 10ms(prev dur) = 14.99s → audio3 at 15.010s.
            tap.Feed(MakePacket(AudioFill), Bytes10ms, CursorBase + 480L, t0 + 150_000_000L)
            tap.FinalizeTo100ns(t0, t0 + SessionLen100ns)

            Dim b As Byte() = BytesOf(sink)
            TestRunner.Assert(b.Length = 2_883_840L,
                              $"D: stream = contract + the error packet (2,883,840), got {b.Length:N0}")
            ExpectAudioSpan(b, 0L, "D: audio1 @0.000s")
            ExpectAudioSpan(b, 1_920L, "D: audio2 placed by continuity (error stamp judged nothing)")
            ExpectSilenceSpan(b, 3_840L, 2_881_920L, "D: idle silence from the TRUSTED anchor only")
            ExpectAudioSpan(b, 2_881_920L, "D: audio3 @15.010s (displaced exactly one packet)")
            TestRunner.Assert(tap.TimestampErrorPackets = 1, "D: error packet counted")
            TestRunner.Assert(tap.IdleGapPackets = 1, $"D: exactly one idle gap (from the good packet), got {tap.IdleGapPackets}")
            TestRunner.Assert(tap.SilenceInsertedBytes = 2_878_080L,
                              $"D: silence = 14.99s, got {tap.SilenceInsertedBytes:N0}")
            TestRunner.Assert(tap.MonotonicViolations = 0, "D: no violation fabricated")
        End Sub

    End Module

End Namespace
