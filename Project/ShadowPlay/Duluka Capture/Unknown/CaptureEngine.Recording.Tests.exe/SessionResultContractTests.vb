Option Strict On
Option Explicit On
Option Infer On

' SessionResultContractTests.vb — M2-W2 video-only loss gate.
'
' THE GAP (proven 2026-09-12): RecordingDTOs.vb:310-313 gates MuxDroppedBytes
' under AudioRequested —
'   audioOk = If(AudioRequested,
'               AudioStreamFound AndAlso MuxDroppedBytes = 0 AndAlso
'               AudioDroppedBytes = 0,
'               True)
' A video-only session (AudioRequested=False) therefore reports Pass=True
' even when the VIDEO pipe dropped AUs: the file exists, header-parses, has
' frames — but is silently missing everything the pipe threw away.
'
' Live proof (real production code, evidence\w2-mux-gate V3-PIPE-DROP):
'   real LiveMuxSession + real ffmpeg — 120 AUs fed, 60 in the MP4,
'   DroppedBytes = 11,517 counted by the pipe, file complete and
'   header-valid → the session shape below reports Pass=True today.
' (Related audit gaps, NOT this test's scope: exit-code suppression in the
'  salvage path (GAP-1) and header-only validity (GAP-2).)
'
' The KNOWN-FAIL test pins the TARGET contract (Pass=False on video-pipe
' loss); it flips to "PASS — fix verified" the moment the Pass gate stops
' conditioning MuxDroppedBytes on AudioRequested. The three controls pin
' today's-correct behavior so the eventual fix cannot over-tighten.
' Pure VB — no hardware, no ffmpeg, deterministic.

Imports System
Imports CaptureEngine.Recording

Namespace CaptureEngine.Recording.Tests

    Friend Module SessionResultContractTests

        ''' <summary>The counters observed by the REAL W2MuxGate V3 run
        ''' (evidence\w2-mux-gate\report.md): half the AUs dropped at the
        ''' pipe, the MP4 still complete and header-valid.</summary>
        Private Function MakeVideoOnlyLossSession() As SessionResult
            Return New SessionResult() With {
                .OutputPath = "evidence-shaped.mp4",
                .RequestedDurationSec = 4,
                .ActualDurationSec = 4.0,
                .FramesCaptured = 120,
                .FramesEncoded = 60,
                .NvencErrors = 0,
                .TotalVideoBytes = 23034,
                .FileExists = True,
                .FileSize = 12994,
                .VideoStreamFound = True,
                .AudioRequested = False,
                .AudioStreamFound = False,
                .MuxDroppedBytes = 11517,
                .AudioDroppedBytes = 0,
                .AudioAccountingOk = True,
                .MicDroppedBytes = 0,
                .MicAccountingOk = True
            }
        End Function

        Public Sub RunAll()
            Console.WriteLine("── SessionResult.Pass contract (M2-W2 video-only loss gate) ──")
            TestRunner.RunTest("PASSGATE: audio session with mux drops fails Pass (existing)",
                               AddressOf Test_AudioSessionDropsFail)
            TestRunner.RunTest("PASSGATE: video-only healthy session passes",
                               AddressOf Test_VideoOnlyHealthyPasses)
            TestRunner.RunTest("PASSGATE: audio-enabled healthy session passes",
                               AddressOf Test_AudioHealthyPasses)
            TestRunner.RunKnownFail("PASSGATE: video-only session with video-pipe drops must NOT pass",
                                    AddressOf Test_VideoOnlyDropsMustFail,
                                    "RecordingDTOs.vb:310-313 — MuxDroppedBytes=0 must hold unconditionally, not only when AudioRequested. Live evidence: evidence\w2-mux-gate (11,517B real pipe drop, complete MP4, Pass=True today).")
        End Sub

        ''' <summary>Existing contract (already correct): a mux-pipe drop fails
        ''' an audio-enabled session via audioOk.</summary>
        Private Sub Test_AudioSessionDropsFail()
            Dim s = MakeVideoOnlyLossSession()
            s.AudioRequested = True
            s.AudioStreamFound = True
            TestRunner.Assert(Not s.Pass,
                              $"audio session with MuxDroppedBytes={s.MuxDroppedBytes} must fail Pass (got Pass={s.Pass})")
        End Sub

        ''' <summary>Fix guard: a fully healthy video-only session must KEEP
        ''' passing — the eventual gate must not over-tighten.</summary>
        Private Sub Test_VideoOnlyHealthyPasses()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            TestRunner.Assert(s.Pass,
                              $"healthy video-only session (MuxDroppedBytes=0) must pass (got Pass={s.Pass})")
        End Sub

        ''' <summary>Fix guard: a fully healthy audio session must KEEP passing.</summary>
        Private Sub Test_AudioHealthyPasses()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.AudioRequested = True
            s.AudioStreamFound = True
            TestRunner.Assert(s.Pass,
                              $"healthy audio session must pass (got Pass={s.Pass})")
        End Sub

        ''' <summary>THE regression: video-only + video-pipe loss must fail
        ''' Pass. Deterministic bug reproduction today — the counters are the
        ''' ones the real pipe reported while the file was silently truncated.</summary>
        Private Sub Test_VideoOnlyDropsMustFail()
            Dim s = MakeVideoOnlyLossSession()
            TestRunner.Assert(Not s.Pass,
                              $"video-only session with MuxDroppedBytes={s.MuxDroppedBytes} (video AUs lost, file truncated) reports Pass={s.Pass} — false success")
        End Sub

    End Module

End Namespace
