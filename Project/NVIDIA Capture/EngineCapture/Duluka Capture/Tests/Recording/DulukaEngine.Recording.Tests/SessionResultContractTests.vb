Option Strict On
Option Explicit On
Option Infer On

' SessionResultContractTests.vb — Phase 6B session-outcome truth.
'
' M2-W2 evidence demonstrated that a video-only recording could report
' Pass=True after its mux pipe dropped 11,517 video bytes: the old Pass
' implementation checked MuxDroppedBytes only when AudioRequested was True.
' Phase 6B fixes that known-fail: mux loss invalidates every recording.
' An explicit session ErrorMessage also invalidates Pass. Mic-only requests
' must be treated as audio requests, not misclassified as video-only.
'
' These deterministic contract tests are runnable without GPU, FFmpeg,
' WASAPI, desktop capture, microphones, or interacting with the live worker.

Imports System
Imports DulukaEngine.Recording

Namespace DulukaEngine.Recording.Tests

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
            TestRunner.RunTest("PASSGATE: all system and mic audio intents mapped",
                               AddressOf Test_AudioIntentCombinations)
            TestRunner.RunTest("PASSGATE: audio-enabled healthy session passes",
                               AddressOf Test_AudioHealthyPasses)
            TestRunner.RunTest("PASSGATE: video-only session with video-pipe drops must NOT pass",
                               AddressOf Test_VideoOnlyDropsMustFail)
            TestRunner.RunTest("PASSGATE: session with explicit error must NOT pass",
                               AddressOf Test_ErrorMessageMustFail)
            TestRunner.RunTest("PASSGATE: audio requested but stream absent must NOT pass",
                               AddressOf Test_AudioMissingMustFail)
            TestRunner.RunTest("PASSGATE: encoder errors must NOT pass",
                               AddressOf Test_EncoderErrorsMustFail)
            TestRunner.RunTest("PASSGATE: incomplete or partial output must NOT pass",
                               AddressOf Test_PartialOrMissingMustFail)
            TestRunner.RunTest("PASSGATE: audio drop or mic drop must NOT pass",
                               AddressOf Test_AudioOrMicDropsMustFail)
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

        Private Sub Test_ErrorMessageMustFail()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.ErrorMessage = "second-pass mux failed"
            TestRunner.Assert(Not s.Pass,
                              "ErrorMessage indicates failure even when MP4 looks valid")
        End Sub

        Private Sub Test_AudioMissingMustFail()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.AudioRequested = True
            s.AudioStreamFound = False
            TestRunner.Assert(Not s.Pass, "requested audio is missing")
        End Sub

        Private Sub Test_EncoderErrorsMustFail()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.NvencErrors = 1
            TestRunner.Assert(Not s.Pass, "NVENC failures cannot be successes")
        End Sub

        Private Sub Test_PartialOrMissingMustFail()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.MuxPartialSalvaged = True
            TestRunner.Assert(Not s.Pass, "salvaged fragments stay failed")
            s.MuxPartialSalvaged = False
            s.FileExists = False
            TestRunner.Assert(Not s.Pass, "missing file stays failed")
            s.FileExists = True
            s.FileSize = 0
            TestRunner.Assert(Not s.Pass, "empty file stays failed")
            s.FileSize = 1500
            s.FramesEncoded = 0
            TestRunner.Assert(Not s.Pass, "no encoded frames stays failed")
        End Sub

        Private Sub Test_AudioIntentCombinations()
            For Each systemOn As Boolean In New Boolean() {False, True}
                For Each micOn As Boolean In New Boolean() {False, True}
                    Dim cfg As New SessionConfig With {
                        .AudioEnabled = systemOn,
                        .MicEnabled = micOn
                    }
                    TestRunner.Assert(cfg.RequestsAnyAudio = (systemOn OrElse micOn),
                        $"system={systemOn} mic={micOn} audio intent mapping incorrect")
                Next
            Next
        End Sub

        Private Sub Test_AudioOrMicDropsMustFail()
            Dim s = MakeVideoOnlyLossSession()
            s.MuxDroppedBytes = 0
            s.AudioRequested = True
            s.AudioStreamFound = True
            s.AudioDroppedBytes = 64
            TestRunner.Assert(Not s.Pass, "system audio packet drops invalidate pass")
            s.AudioDroppedBytes = 0
            s.MicDroppedBytes = 64
            TestRunner.Assert(Not s.Pass, "mic packet drops invalidate pass")
        End Sub
    End Module

End Namespace
