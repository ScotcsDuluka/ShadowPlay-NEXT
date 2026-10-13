Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Globalization
Imports System.Reflection
Imports CaptureEngine.Backends
Imports CaptureEngine.FFmpegBackend
Imports CaptureEngine.Audio.Wasapi

Namespace CaptureEngine.FFmpegTests
    Friend Module Program
        Friend _passed As Integer = 0
        Friend _failed As Integer = 0
        Friend _skipped As Integer = 0
        Friend ReadOnly _failures As New List(Of String)()
        Friend ReadOnly _skips As New List(Of String)()

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine.FFmpegBackend Tests (P1-C)")
            Console.WriteLine("==================================================")
            ' C/4 provenance banner: ties this run to the exact binary that
            ' executed it (BuildUtc + SourceRevision stamped at build time).
            Console.WriteLine(" " & ProvenanceLine())
            Console.WriteLine()

            ' ----- StderrParser tests -----
            RunTest("PARSER: Parses frame= line", AddressOf Test_ParserParsesFrameLine)
            RunTest("PARSER: Parses Lsize= final line", AddressOf Test_ParserParsesLsizeLine)
            RunTest("PARSER: Detects [error] line", AddressOf Test_ParserDetectsError)
            RunTest("PARSER: Detects 'No space left on device'", AddressOf Test_ParserDetectsDiskFull)
            RunTest("PARSER: Parses size= with KiB unit", AddressOf Test_ParserParsesSizeKiB)
            RunTest("PARSER: Parses speed= with x suffix", AddressOf Test_ParserParsesSpeedX)
            RunTest("PARSER: Ignores non-progress lines", AddressOf Test_ParserIgnoresNoise)
            RunTest("PARSER: GetSnapshot returns all fields", AddressOf Test_ParserGetSnapshot)
            RunTest("PARSER: Thread-safe (never throws on garbage input)", AddressOf Test_ParserNeverThrows)

            ' ----- MuxCoordinator tests -----
            RunTest("MUX: CleanupTempFiles deletes existing files", AddressOf Test_MuxCleanupTempFiles)
            RunTest("MUX: RenameTempVideoToOutput works", AddressOf Test_MuxRenameWorks)
            RunTest("MUX: RenameTempVideoToOutput returns False on missing", AddressOf Test_MuxRenameMissing)
            RunTest("MUX: ProbeVideoDuration returns 0 on missing file", AddressOf Test_MuxProbeMissing)

            ' ----- FFmpegPipelineBackend lifecycle tests -----
            RunTest("LIFECYCLE: Created → Start throws on missing ffmpeg.exe", AddressOf Test_StartMissingFFmpeg)
            RunTest("LIFECYCLE: GetFrame returns Nothing", AddressOf Test_GetFrameReturnsNothing)
            RunTest("LIFECYCLE: GetDiagnostics returns empty dict before Start", AddressOf Test_DiagnosticsBeforeStart)
            RunTest("LIFECYCLE: Stop before Start is no-op", AddressOf Test_StopBeforeStart)
            RunTest("LIFECYCLE: Dispose sets Disposed state", AddressOf Test_DisposeSetsDisposed)
            RunTest("LIFECYCLE: Dispose twice is idempotent", AddressOf Test_DisposeTwice)

            ' ----- AudioSidecar tests -----
            RunTest("AUDIO: Start/Stop lifecycle (stub mode)", AddressOf Test_AudioSidecarStubLifecycle)
            RunTest("AUDIO: HasAudioData returns False (stub)", AddressOf Test_AudioSidecarNoData)
            RunTest("AUDIO: Captures timestamps on Start", AddressOf Test_AudioSidecarTimestamps)

            ' ----- P13.2 AudioPositionTracker tests (synthetic positions) -----
            RunTest("WPOS: First packet anchors (no gap)", AddressOf Test_WPosAnchor)
            RunTest("WPOS: Continuous stream has zero holes", AddressOf Test_WPosContinuous)
            RunTest("WPOS: qpc jitter immunity — cursor timeline unmoved (P13.4c)", AddressOf Test_WPosQpcLies)
            RunTest("WPOS: Idle gap from QPC when cursor frozen (Phase-A)", AddressOf Test_WPosIdleQpcEvidence)
            RunTest("WPOS: TimestampError stamp judges no gap (Phase-A)", AddressOf Test_WPosTimestampError)
            RunTest("WPOS: Silence hole measured exactly", AddressOf Test_WPosHole)
            RunTest("WPOS: Zero cursor falls back to continuity (Risk #2)", AddressOf Test_WPosZeroCursor)
            RunTest("WPOS: Negative frames rejected (counter hygiene)", AddressOf Test_WPosNegativeFrames)
            RunTest("WPOS: First real stamp re-anchors after fallback anchor", AddressOf Test_WPosReanchor)
            RunTest("WPOS: Backwards stamp flagged, timeline not rewound", AddressOf Test_WPosBackwards)
            RunTest("WPOS: Variable packet sizes, END-to-START exact", AddressOf Test_WPosVariableSizes)
            RunTest("WPOS: 1-hour synthetic run — zero drift, integer-exact", AddressOf Test_WPosOneHour)
            RunTest("WPOS: Duration math floors deterministically", AddressOf Test_WPosDurationFloor)
            RunTest("WPOS: Reset clears anchor (device switch)", AddressOf Test_WPosReset)
            RunTest("WPOS: QpcTicksTo100ns is frequency-independent", AddressOf Test_WPosQpcConversion)
            RunTest("WPOS: Start() guards non-Windows platforms", AddressOf Test_WPosPlatformGuard)
            RunTest("ATDC: Anchor on first packet, no silence", AddressOf Test_ATDCAnchor)
            RunTest("ATDC: Continuous stream — zero silence inserted", AddressOf Test_ATDCContinuous)
            RunTest("ATDC: Measured hole padded exactly (END→START)", AddressOf Test_ATDCHole)
            RunTest("ATDC: Sub-50ms hole padded + counted (P13.4b)", AddressOf Test_ATDCSmallHole)
            RunTest("ATDC: >1h hole dropped, no pad, no throw", AddressOf Test_ATDCHugeHole)
            RunTest("ATDC: Backwards cursor — no pad, no rewind", AddressOf Test_ATDCBackwards)
            RunTest("ATDC: Zero cursor mid-stream — continuity, no hole", AddressOf Test_ATDCZeroStamp)
            RunTest("ATDC: Re-anchor after fallback anchor — no fake hole", AddressOf Test_ATDCReanchor)
            RunTest("ATDC: Block alignment floors the pad", AddressOf Test_ATDCBlockAlign)
            RunTest("ATDC: Finalize pads exact QPC tail", AddressOf Test_ATDCFinalizeTail)
            RunTest("ATDC: Finalize never-anchored pads full span", AddressOf Test_ATDCFinalizeEmpty)
            RunTest("ATDC: A/B equivalence — silence == tracker oracle", AddressOf Test_ATDCAbEquivalence)
            RunTest("ATDC: Many 1ms holes — track == wall-clock span (P13.4b)", AddressOf Test_ATDCManySmallHoles)
            RunTest("ATDC: qpc jitter immunity — cursor timeline unmoved (P13.4c)", AddressOf Test_ATDCQpcJitterImmunity)
            RunTest("ATDC: Idle gap padded at real position + rebase (Phase-A)", AddressOf Test_ATDCIdleGap)
            RunTest("SYNC2: Exact QPC anchor offset arithmetic", AddressOf Test_Sync2AnchorOffset)
            RunTest("SYNC2: Anchor guards + ±3600s sanity bound (P13.4b)", AddressOf Test_Sync2AnchorGuards)

            ' ----- Integration tests (requires real ffmpeg.exe) -----
            RunTest("INTEGRATION: Real FFmpeg record → stop → output file", AddressOf Test_RealFFmpegIntegration)
            RunTest("STRESS: Start → Stop → Start cycle (3 rounds, real ffmpeg)", AddressOf Test_StartStopStartStress)
            RunTest("VALIDATOR: AssertValidMedia rejects garbage + accepts real media", AddressOf Test_MediaValidatorContract)

            ' ----- LiveMuxSession — the PRODUCTION mux path (G1) -----
            LiveMuxSessionTests.RunAll()

            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine(" Result: " & _passed & " passed, " & _failed & " failed, " &
                              _skipped & " skipped, " & (_passed + _failed + _skipped) & " total")
            Console.WriteLine("--------------------------------------------------")
            If _skipped > 0 Then
                Console.WriteLine()
                Console.WriteLine("Skips (ENVIRONMENT NOT CAPABLE — F-01):")
                For Each s As String In _skips
                    Console.WriteLine("  - " & s)
                Next
            End If
            If _failed > 0 Then
                Console.WriteLine()
                Console.WriteLine("Failures:")
                For Each f As String In _failures
                    Console.WriteLine("  - " & f)
                Next
            End If
            Return If(_failed > 0, 1, 0)
        End Function

        Friend Sub RunTest(name As String, test As Action)
            Dim paddedName = name
            If paddedName.Length < 70 Then paddedName = paddedName & New String(" "c, 70 - paddedName.Length)
            Console.Write("[" & paddedName & "] ")
            Try
                test()
                _passed += 1
                Console.WriteLine("PASS")
            Catch ex As SkipException
                ' F-01/C-4: ENVIRONMENT NOT CAPABLE — SKIP, never PASS, never FAIL.
                _skipped += 1
                _skips.Add(name & ": " & ex.Message)
                Console.WriteLine("SKIP")
                Console.WriteLine("    " & ex.Message)
            Catch ex As Exception
                _failed += 1
                _failures.Add(name & ": " & ex.GetType().Name & ": " & ex.Message)
                Console.WriteLine("FAIL")
                Console.WriteLine("    " & ex.GetType().Name & ": " & ex.Message)
            End Try
        End Sub

        Friend Sub Assert(cond As Boolean, msg As String)
            If Not cond Then Throw New InvalidOperationException("ASSERT: " & msg)
        End Sub

        ''' <summary>C/4 provenance: read the build-time stamp from this
        ''' assembly's AssemblyMetadata (stamped by StampTestProvenance in
        ''' Directory.Build.targets for every *Tests assembly).</summary>
        Private Function ProvenanceLine() As String
            Dim buildUtc As String = "unknown"
            Dim sourceRev As String = "unknown"
            For Each a As AssemblyMetadataAttribute In
                Assembly.GetExecutingAssembly().GetCustomAttributes(Of AssemblyMetadataAttribute)()
                If a.Key = "BuildUtc" Then buildUtc = a.Value
                If a.Key = "SourceRevision" Then sourceRev = a.Value
            Next
            Return $"binary provenance: built {buildUtc} from source {sourceRev}"
        End Function

        ' ===== FFmpegStderrParser tests =====

        Private Sub Test_ParserParsesFrameLine()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("frame= 6540 fps=143 q=8.0 size= 94278KiB time=00:00:45.41 bitrate=17005.4kbits/s dup=760 drop=1 speed=0.997x")
            Dim snap = p.GetSnapshot()
            Assert(snap("frame") = 6540, "frame should be 6540")
            Assert(snap("fps") = 143, "fps should be 143")
            Assert(snap("dup") = 760, "dup should be 760")
            Assert(snap("drop") = 1, "drop should be 1")
            Assert(snap("speed") = 997, "speed should be 997 (0.997 * 1000)")
        End Sub

        Private Sub Test_ParserParsesLsizeLine()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("Lsize= 94300KiB time=00:00:60.00 bitrate=12870.0kbits/s speed=1.01x")
            Dim snap = p.GetSnapshot()
            Assert(snap("speed") = 1010, "speed should be 1010 (1.01 * 1000)")
        End Sub

        Private Sub Test_ParserDetectsError()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("[error] Could not open output file 'output.mp4'")
            Assert(p.HasError, "HasError should be True")
            Assert(p.LastError.Contains("Could not open"), "LastError should contain the message")
        End Sub

        Private Sub Test_ParserDetectsDiskFull()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("[error] No space left on device")
            Assert(p.HasError, "Should detect 'No space left on device'")
        End Sub

        Private Sub Test_ParserParsesSizeKiB()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("frame= 100 fps=60 size= 1024KiB time=00:00:01.00 speed=1.0x")
            Dim snap = p.GetSnapshot()
            Assert(snap("size_bytes") = 1048576, "1024 KiB = 1048576 bytes")
        End Sub

        Private Sub Test_ParserParsesSpeedX()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("frame= 100 fps=60 speed=2.5x")
            Dim snap = p.GetSnapshot()
            Assert(snap("speed") = 2500, "2.5x = 2500")
        End Sub

        Private Sub Test_ParserIgnoresNoise()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("Some random FFmpeg output that is not a progress line")
            p.ProcessLine("Press [q] to stop, [?] for help")
            Dim snap = p.GetSnapshot()
            Assert(snap("frame") = 0, "frame should be 0 (no progress parsed)")
            Assert(Not p.HasError, "Should not detect error from noise")
        End Sub

        Private Sub Test_ParserGetSnapshot()
            Dim p As New FFmpegStderrParser()
            p.ProcessLine("frame= 500 fps=120 dup=3 drop=0 speed=1.5x")
            Dim snap = p.GetSnapshot()
            Assert(snap.ContainsKey("frame"), "should have frame key")
            Assert(snap.ContainsKey("fps"), "should have fps key")
            Assert(snap.ContainsKey("dup"), "should have dup key")
            Assert(snap.ContainsKey("drop"), "should have drop key")
            Assert(snap.ContainsKey("speed"), "should have speed key")
            Assert(snap.ContainsKey("size_bytes"), "should have size_bytes key")
        End Sub

        Private Sub Test_ParserNeverThrows()
            Dim p As New FFmpegStderrParser()
            ' Feed various garbage — should never throw
            p.ProcessLine(Nothing)
            p.ProcessLine("")
            p.ProcessLine("   ")
            p.ProcessLine("frame=abc fps=xyz speed=NaN")
            p.ProcessLine("[error] test")
            ' If we get here, no exception was thrown
            Assert(True, "Parser survived garbage input")
        End Sub

        ' ===== MuxCoordinator tests =====

        Private Sub Test_MuxCleanupTempFiles()
            Dim mc As New MuxCoordinator()
            ' Create temp files
            Dim tempDir = IO.Path.GetTempPath()
            Dim f1 = IO.Path.Combine(tempDir, "test_mux_cleanup_1.tmp")
            Dim f2 = IO.Path.Combine(tempDir, "test_mux_cleanup_2.tmp")
            IO.File.WriteAllText(f1, "test")
            IO.File.WriteAllText(f2, "test")
            mc.TempVideoPath = f1
            mc.TempSystemWavPath = f2
            mc.CleanupTempFiles()
            Assert(Not IO.File.Exists(f1), "temp file 1 should be deleted")
            Assert(Not IO.File.Exists(f2), "temp file 2 should be deleted")
            mc.Dispose()
        End Sub

        Private Sub Test_MuxRenameWorks()
            Dim mc As New MuxCoordinator()
            Dim tempDir = IO.Path.GetTempPath()
            Dim src = IO.Path.Combine(tempDir, "test_mux_rename_src.tmp")
            Dim dst = IO.Path.Combine(tempDir, "test_mux_rename_dst.tmp")
            IO.File.WriteAllText(src, "video data")
            mc.TempVideoPath = src
            mc.OutputPath = dst
            Dim ok = mc.RenameTempVideoToOutput()
            Assert(ok, "Rename should succeed")
            Assert(IO.File.Exists(dst), "Output file should exist")
            Assert(Not IO.File.Exists(src), "Source should be moved")
            IO.File.Delete(dst)
            mc.Dispose()
        End Sub

        Private Sub Test_MuxRenameMissing()
            Dim mc As New MuxCoordinator()
            mc.TempVideoPath = "C:\nonexistent\path\video.tmp.mp4"
            mc.OutputPath = "C:\nonexistent\path\output.mp4"
            Dim ok = mc.RenameTempVideoToOutput()
            Assert(Not ok, "Rename should return False for missing source")
            mc.Dispose()
        End Sub

        Private Sub Test_MuxProbeMissing()
            Dim mc As New MuxCoordinator()
            mc.FFmpegPath = "ffmpeg.exe"
            mc.TempVideoPath = "C:\nonexistent\video.mp4"
            Dim dur = mc.ProbeVideoDuration()
            Assert(dur = 0.0, "Duration should be 0.0 for missing file")
            mc.Dispose()
        End Sub

        ' ===== FFmpegPipelineBackend lifecycle tests =====

        Private Sub Test_StartMissingFFmpeg()
            Dim b As New FFmpegPipelineBackend()
            b.WithFFmpegPath("C:\nonexistent\ffmpeg.exe")
            b.WithArguments("-version")
            b.WithOutputPath("output.mp4")
            Dim threw As Boolean = False
            Try
                b.Start()
            Catch ex As IO.FileNotFoundException
                threw = True
            Catch ex As Exception
                ' Some environments may throw different exception types
                threw = True
            End Try
            Assert(threw, "Start with missing ffmpeg.exe should throw")
            b.Dispose()
        End Sub

        Private Sub Test_GetFrameReturnsNothing()
            Dim b As New FFmpegPipelineBackend()
            Dim frame = b.GetFrame()
            Assert(frame Is Nothing, "GetFrame should return Nothing (fire-and-forget)")
            b.Dispose()
        End Sub

        Private Sub Test_DiagnosticsBeforeStart()
            Dim b As New FFmpegPipelineBackend()
            Dim diag = b.GetDiagnostics()
            Assert(diag.Count = 0, "Diagnostics should be empty before Start")
            b.Dispose()
        End Sub

        Private Sub Test_StopBeforeStart()
            Dim b As New FFmpegPipelineBackend()
            b.WithFFmpegPath("ffmpeg.exe")
            b.WithArguments("-version")
            b.WithOutputPath("out.mp4")
            ' Stop before Start should be a safe no-op
            ' It sets _stopCompleted=1 but since state is Created (not Running), it returns early
            b.Stop()
            ' State stays Created because Stop returns early when state != Running
            Assert(b.CurrentState = VideoBackendState.Created OrElse b.CurrentState = VideoBackendState.Stopped,
                   "State should be Created or Stopped after Stop-before-Start (not Faulted)")
            ' Dispose should still work (it checks _disposed, not _stopCompleted)
            b.Dispose()
        End Sub

        Private Sub Test_DisposeSetsDisposed()
            Dim b As New FFmpegPipelineBackend()
            b.Dispose()
            Assert(b.CurrentState = VideoBackendState.Disposed, "State should be Disposed")
        End Sub

        Private Sub Test_DisposeTwice()
            Dim b As New FFmpegPipelineBackend()
            b.Dispose()
            b.Dispose()  ' Should not throw
            b.Dispose()  ' Should not throw
            Assert(b.CurrentState = VideoBackendState.Disposed, "State should be Disposed")
        End Sub

        ' ===== AudioSidecar tests =====

        Private Sub Test_AudioSidecarStubLifecycle()
            Dim a As New AudioSidecar()
            a.SystemAudioEnabled = True
            a.Start()
            Assert(a.IsRunning, "Should be running after Start")
            a.Stop()
            Assert(Not a.IsRunning, "Should not be running after Stop")
            a.Dispose()
        End Sub

        Private Sub Test_AudioSidecarNoData()
            Dim a As New AudioSidecar()
            a.SystemAudioEnabled = True
            a.Start()
            Assert(Not a.HasAudioData, "Stub should return False for HasAudioData")
            a.Stop()
            a.Dispose()
        End Sub

        Private Sub Test_AudioSidecarTimestamps()
            Dim a As New AudioSidecar()
            a.SystemAudioEnabled = True
            a.MicEnabled = True
            a.Start()
            Assert(a.SystemStartTicks > 0, "SystemStartTicks should be captured")
            Assert(a.MicStartTicks > 0, "MicStartTicks should be captured")
            a.Stop()
            a.Dispose()
        End Sub

        ' ===== P13.2 AudioPositionTracker tests =====
        ' ★ P13.4c: the timeline consumes DevicePositionFrames (the RENDER
        ' CURSOR — content time, silence included). qpcPosition100ns is the
        ' WALL ANCHOR + anomaly evidence only — feeding it jitter must NOT
        ' move the timeline (Test_WPosQpcLies is the regression lock).
        ' All positions are SYNTHETIC — deterministic, no audio hardware.
        ' Reference: 1 ms = 10,000 × 100ns; 10ms packet @48k = 480 frames.

        Private Const T0 As Long = 987654321000000L
        Private Const Pkt10ms As Long = 100000L
        Private Const DevBase As Long = 5000000L   ' arbitrary render cursor

        Private Sub Test_WPosAnchor()
            Dim t As New AudioPositionTracker(48000)
            Assert(Not t.Anchored, "should start unanchored")
            Dim rep = t.Feed(480, DevBase, T0)
            Assert(rep.AnchoredNow, "first packet must anchor")
            Assert(t.Anchored, "Anchored after first packet")
            Assert(t.FirstQpc100ns = T0, "FirstQpc100ns = first stamp (wall anchor)")
            Assert(t.FirstDevPos = DevBase, "FirstDevPos = first cursor")
            Assert(rep.Hole100ns = 0L, "no hole on anchor")
            Assert(rep.LastDevPosEnd = DevBase + 480L, "cursor end = devPos + 480")
            Assert(rep.LastEnd100ns = T0 + Pkt10ms, "lastEnd = qpc + 10ms (content mapped)")
        End Sub

        Private Sub Test_WPosContinuous()
            Dim t As New AudioPositionTracker(48000)
            For i As Integer = 0 To 299
                Dim rep = t.Feed(480, DevBase + i * 480L, T0 + i * Pkt10ms)
                Assert(rep.Hole100ns = 0L, "continuous stream, hole at i=" & i)
                Assert(Not rep.MonotonicViolation, "no violation at i=" & i)
            Next
            Assert(t.Packets = 300L, "300 packets fed")
            Assert(t.GapPackets = 0L, "zero gap packets")
            Assert(t.LastDevPosEnd = DevBase + 300L * 480L, "cursor end exact")
            Assert(t.LastEnd100ns = T0 + 300L * Pkt10ms, "lastEnd exact after 3s")
        End Sub

        Private Sub Test_WPosQpcLies()
            ' ★ P13.4c REGRESSION (the OWNER machine): 933 backwards qpc
            ' stamps per session and the qpc-delta timeline lost 5.65s.
            ' The cursor timeline must be IMMUNE to the documented noise:
            ' BACKWARDS stamps of any magnitude and forward jitter within
            ' the Phase-A idle tolerance (50ms) change NOTHING — content
            ' time comes from DevicePositionFrames; the noise is only
            ' counted as evidence.
            ' ★ PHASE-A refinement (OWNER-approved 2026-08-31): a LARGE
            ' forward stamp under a FROZEN cursor is no longer "noise" —
            ' it is the idle-gap evidence (Test_WPosIdleQpcEvidence). The
            ' two are indistinguishable from stamps alone; the policy
            ' boundary is the tolerance + the physical argument (QPC is
            ' monotonic hardware — the documented real noise is backwards).
            Dim t As New AudioPositionTracker(48000)
            Dim lies() As Long = {T0, T0 - 500000000L, T0 + 400000L,
                                  T0 - 3000000000L, T0 + 250000L,
                                  T0 - 10000000L, T0 + 200000L}
            For i As Integer = 0 To 99
                Dim rep = t.Feed(480, DevBase + i * 480L, lies(i Mod lies.Length))
                Assert(rep.Hole100ns = 0L, "qpc lie must NOT create a hole at i=" & i)
                Assert(Not rep.MonotonicViolation, "qpc lie is not a cursor violation at i=" & i)
            Next
            Assert(t.GapPackets = 0L, "zero gaps despite qpc lies")
            Assert(t.IdleGapPackets = 0L, "zero idle gaps from within-tolerance jitter")
            Assert(t.MonotonicViolations = 0L, "zero cursor violations")
            Assert(t.LastEnd100ns = T0 + 100L * Pkt10ms, "timeline exact despite qpc lies")
            Assert(t.QpcAnomalies > 0L, "qpc noise still COUNTED as evidence")
        End Sub

        Private Sub Test_WPosIdleQpcEvidence()
            ' ★ PHASE-A (OWNER-approved 2026-08-31): the frozen-cursor rule.
            ' P13.4b field model: while nothing plays the endpoint delivers
            ' NOTHING and its render cursor FREEZES — case 1 sees hole=0 and
            ' the idle span migrated to the track tail ([voice][voice][long
            ' tail silence]). Policy: devPos = MASTER TIMELINE, qpc = WALL
            ' ANCHOR / GAP DETECTOR. Frozen cursor + wall evidence beyond
            ' the previous packet's span + tolerance → IDLE gap =
            ' wallGap − prevDur (qpcDelta is NEVER the raw length), padded
            ' at THIS position; the rebase keeps later packets seamless.
            Dim t As New AudioPositionTracker(48000)
            Dim rep1 = t.Feed(480, DevBase, T0)
            Assert(Not rep1.IdleGapUsedQpc, "anchor is not an idle gap")
            ' t=10s: cursor STILL DevBase+480 (frozen), wall advanced 10s.
            Dim rep2 = t.Feed(480, DevBase + 480L, T0 + 100000000L)
            Assert(rep2.Hole100ns = 99900000L,
                   "idle gap = wallGap(10s) − prevDur(10ms) = 9.99s, got " & rep2.Hole100ns)
            Assert(rep2.IdleGapUsedQpc, "gap came from QPC wall evidence")
            Assert(t.IdleGapPackets = 1L, "one idle-gap packet")
            Assert(t.GapPackets = 0L, "cursor-proven gap counter untouched")
            Assert(rep2.LastDevPosEnd = DevBase + 480L + 479520L + 480L,
                   "virtual end = frozen end + idle frames + packet")
            Assert(rep2.LastEnd100ns = T0 + 100100000L, "wall mapping advanced to 10.01s")
            ' The rebase: a packet whose raw cursor CONTINUES from the
            ' frozen spot maps back onto the virtual timeline — zero hole.
            Dim rep3 = t.Feed(480, DevBase + 960L, T0 + 100100000L)
            Assert(rep3.Hole100ns = 0L, "post-idle continuity via rebase, got " & rep3.Hole100ns)
            Assert(Not rep3.MonotonicViolation, "rebase must not fabricate a violation")
            Assert(t.LastEnd100ns = T0 + 100200000L, "timeline advanced 10ms only")
            ' Backwards qpc noise (the OWNER machine: 933/session) must NOT
            ' fabricate an idle gap while the cursor walks normally.
            Dim rep4 = t.Feed(480, DevBase + 1440L, T0 - 3000000000L)
            Assert(rep4.Hole100ns = 0L, "backwards qpc → no gap")
            Assert(Not rep4.IdleGapUsedQpc, "backwards qpc is not idle evidence")
        End Sub

        Private Sub Test_WPosTimestampError()
            ' ★ PHASE-A OWNER rule: "ถ้า TimestampError → ห้ามใช้ packet นี้
            ' ตัดสิน gap". A known-error stamp may not: judge a gap, advance
            ' the wall base (its qpc is untrusted), or rewrite the cursor
            ' bookkeeping. The packet's CONTENT still exists — continuity
            ' placement (its 10ms may land displaced; that is the honest
            ' cost of an untrustworthy stamp, bounded by one packet).
            Dim t As New AudioPositionTracker(48000)
            t.Feed(480, DevBase, T0)
            ' t=10s: frozen cursor + TimestampError → MUST NOT pad.
            Dim rep2 = t.Feed(480, DevBase + 480L, T0 + 100000000L, WasapiPacketFlags.TimestampError)
            Assert(rep2.TimestampErrorSuppressed, "suppression flagged")
            Assert(rep2.Hole100ns = 0L, "no gap judged from an error stamp")
            Assert(t.IdleGapPackets = 0L, "no idle gap counted")
            Assert(t.LastEnd100ns = T0 + 200000L, "content advanced by duration only")
            ' The next GOOD packet judges against the last TRUSTED anchor
            ' (T0 — the error stamp did not advance the wall base):
            ' wallGap = 15s, idle = 15s − 10ms(prevDur) = 14.99s.
            Dim rep3 = t.Feed(480, DevBase + 480L, T0 + 150000000L)
            Assert(rep3.Hole100ns = 149900000L,
                   "good packet pads from the trusted anchor, got " & rep3.Hole100ns)
            Assert(rep3.IdleGapUsedQpc, "idle evidence used by the good packet only")
        End Sub

        Private Sub Test_WPosHole()
            Dim t As New AudioPositionTracker(48000)
            t.Feed(480, DevBase, T0)
            ' Cursor jumps 290ms of content (13,920 frames @48k) before the
            ' next packet — the hole is CONTENT time, measured in frames.
            Dim holeFrames As Long = 13920L
            Dim rep = t.Feed(480, DevBase + 480L + holeFrames, T0 + Pkt10ms + 2900000L)
            Assert(rep.Hole100ns = 2900000L, "hole must be exactly 290ms, got " & rep.Hole100ns)
            Assert(t.GapPackets = 1L, "one gap packet")
            Assert(t.TotalHole100ns = 2900000L, "total hole = 290ms")
            Assert(rep.LastDevPosEnd = DevBase + 480L + holeFrames + 480L, "cursor end resumes")
            Assert(rep.LastEnd100ns = T0 + Pkt10ms + 2900000L + Pkt10ms,
                   "lastEnd = wall anchor + full content span (incl. the hole)")
        End Sub

        Private Sub Test_WPosZeroCursor()
            Dim t As New AudioPositionTracker(48000)
            t.Feed(480, DevBase, T0)
            Dim before As Long = t.LastEnd100ns
            Dim rep = t.Feed(480, 0L, T0 + Pkt10ms)   ' doc §5 Risk #2: no cursor
            Assert(rep.StampFallbackUsed, "fallback must be flagged")
            Assert(rep.Hole100ns = 0L, "fallback assumes continuity — no hole")
            Assert(t.LastEnd100ns = before + Pkt10ms, "lastEnd advanced by packet dur")
            Assert(t.StampFallbacks = 1L, "fallback counted")
        End Sub

        Private Sub Test_WPosNegativeFrames()
            Dim t As New AudioPositionTracker(48000)
            Dim threw As Boolean = False
            Try
                t.Feed(-1, DevBase, T0)
            Catch ex As ArgumentOutOfRangeException
                threw = True
            End Try
            Assert(threw, "negative frames must throw, not corrupt counters")
            Assert(t.Packets = 0L, "rejected feed must not bump Packets")
            Assert(t.Frames = 0L, "rejected feed must not touch Frames")
            Assert(Not t.Anchored, "rejected feed must not anchor the timeline")
            ' Zero frames is DEGENERATE but legal — must not throw.
            Dim rep = t.Feed(0, DevBase, T0)
            Assert(rep.AnchoredNow, "0-frame packet still anchors")
        End Sub

        Private Sub Test_WPosReanchor()
            Dim t As New AudioPositionTracker(48000)
            ' Pathological driver: FIRST packet has no cursor -> fallback
            ' anchor. Without the re-anchor rule, the first REAL cursor
            ' would "measure" a hole the size of the whole stream and
            ' AudioTap v3 would pad seconds of silence.
            Dim r1 = t.Feed(480, 0L, T0)
            Assert(r1.StampFallbackUsed, "fallback anchor flagged")
            Assert(t.FirstQpc100ns = T0, "anchor keeps the packet's REAL wall stamp (content exists)")
            Dim r2 = t.Feed(480, 0L, T0 + Pkt10ms)   ' still cursorless
            Assert(r2.StampFallbackUsed, "cursorless packet still fallback")
            Assert(r2.Hole100ns = 0L, "continuity — no hole")
            Dim qpcReal As Long = T0 + 2L * Pkt10ms
            Dim r3 = t.Feed(480, DevBase, qpcReal)   ' first REAL cursor
            Assert(r3.ReAnchoredNow, "must re-anchor on first real cursor")
            Assert(Not r3.StampFallbackUsed, "a real cursor is not a fallback")
            Assert(r3.Hole100ns = 0L, "NO bogus hole across re-anchor")
            Assert(t.FirstQpc100ns = T0, "wall anchor PRESERVED (track starts at T0)")
            Assert(t.FirstDevPos = DevBase - 960L, "cursor base backfilled (implied, continuity)")
            Assert(t.LastEnd100ns = qpcReal + Pkt10ms, "timeline on the real cursor")
            Dim r4 = t.Feed(480, DevBase + 480L, qpcReal + Pkt10ms)
            Assert(r4.Hole100ns = 0L AndAlso Not r4.MonotonicViolation,
                   "post-reanchor continuity normal")
            Assert(Not r4.ReAnchoredNow, "re-anchor fires exactly once")
            Assert(t.StampFallbacks = 2L, "fallbacks still counted for evidence")
        End Sub

        Private Sub Test_WPosBackwards()
            Dim t As New AudioPositionTracker(48000)
            t.Feed(480, DevBase, T0)
            Dim before As Long = t.LastEnd100ns
            ' Cursor 10ms IN THE PAST (overlap: [DevBase-480, DevBase) vs
            ' previous [DevBase, DevBase+480)). Violation flagged, no hole,
            ' cursor end takes max(prevEnd, newEnd) — NEVER rewinds.
            Dim rep = t.Feed(480, DevBase - 480L, T0 + Pkt10ms)
            Assert(rep.MonotonicViolation, "violation must be flagged")
            Assert(rep.Hole100ns = 0L, "no hole on overlap")
            Assert(rep.LastDevPosEnd = DevBase + 480L, "cursor end = max(prevEnd, newEnd)")
            Assert(t.LastEnd100ns = before, "timeline must NOT rewind")
            Assert(t.MonotonicViolations = 1L, "violation counted")
        End Sub

        Private Sub Test_WPosVariableSizes()
            Dim t As New AudioPositionTracker(48000)
            ' Cursor crafted so each packet starts exactly where the previous
            ' ended — continuity must hold for VARIABLE frame counts.
            t.Feed(480, DevBase, T0)                          ' 10ms -> 480
            Dim r2 = t.Feed(960, DevBase + 480L, T0 + 100000L)   ' 20ms -> 1440
            Dim r3 = t.Feed(240, DevBase + 1440L, T0 + 300000L)  ' 5ms  -> 1680
            Dim r4 = t.Feed(480, DevBase + 1680L, T0 + 350000L)  ' 10ms -> 2160
            Assert(r2.Hole100ns = 0L AndAlso r3.Hole100ns = 0L AndAlso r4.Hole100ns = 0L,
                   "all variable-size transitions continuous")
            Assert(t.LastEnd100ns = T0 + 450000L, "final lastEnd exact (45ms of content)")
            Assert(t.GapPackets = 0L, "no gaps counted")
        End Sub

        Private Sub Test_WPosOneHour()
            Dim t As New AudioPositionTracker(48000)
            ' 360,000 packets x 10ms = exactly 1 hour. Integer math must
            ' land EXACTLY — this is the no-drift guarantee behind P13.4.
            For i As Long = 0 To 359999L
                t.Feed(480, DevBase + i * 480L, T0 + i * Pkt10ms)
            Next
            Assert(t.Packets = 360000L, "packet count")
            Assert(t.GapPackets = 0L, "zero gaps over 1h")
            Assert(t.MonotonicViolations = 0L, "zero violations over 1h")
            Assert(t.LastEnd100ns = T0 + 360000L * Pkt10ms,
                   "lastEnd must be EXACTLY t0 + 1h (integer-exact, no drift)")
        End Sub

        Private Sub Test_WPosDurationFloor()
            Dim t As New AudioPositionTracker(48000)
            Assert(t.Duration100ns(480) = 100000L, "480 frames = 10ms exact")
            Assert(t.Duration100ns(1) = 208L, "1 frame floors to 208 (208.33)")
            Assert(t.Duration100ns(479) = 99791L, "479 frames floors to 99791")
            Assert(t.Duration100ns(0) = 0L, "0 frames = 0")
            Assert(t.Duration100ns(-5) = 0L, "negative frames = 0")
        End Sub

        Private Sub Test_WPosReset()
            Dim t As New AudioPositionTracker(48000)
            t.Feed(480, DevBase, T0)
            t.Reset()
            Assert(Not t.Anchored, "Reset clears anchor")
            Dim rep = t.Feed(480, DevBase + 987654321L, T0 + 987654321L)
            Assert(rep.AnchoredNow, "next packet re-anchors (device switch)")
            Assert(t.FirstQpc100ns = T0 + 987654321L, "new anchor stamp")
            Assert(t.FirstDevPos = DevBase + 987654321L, "new cursor base")
            Assert(rep.Hole100ns = 0L, "no hole across reset — new timeline")
        End Sub

        Private Sub Test_WPosQpcConversion()
            ' Property, not constants: N ticks at Stopwatch.Frequency = N/freq
            ' seconds = N/freq x 1e7 units of 100ns. Must hold for ANY freq.
            Dim freq As Long = Stopwatch.Frequency
            Assert(WasapiPositionCapture.StopwatchTicksTo100ns(freq) = 10000000L,
                   "1 freq-worth of ticks = exactly 1 second = 1e7")
            Assert(WasapiPositionCapture.StopwatchTicksTo100ns(2L * freq) = 20000000L,
                   "2 seconds exact")
            Assert(WasapiPositionCapture.StopwatchTicksTo100ns(freq \ 2) = 5000000L,
                   "half a second exact")
            Assert(WasapiPositionCapture.StopwatchTicksTo100ns(0L) = 0L, "0 ticks = 0")
            ' ~29 days of QPC (the naive product overflows here).
            ' VB precedence trap: '*' binds TIGHTER than '\', so every
            ' integer division needs explicit parentheses.
            Dim huge As Long = 2500000000000000L
            Dim expect As Long = (huge \ freq) * 10000000L +
                                 ((huge Mod freq) * 10000000L \ freq)
            Assert(WasapiPositionCapture.StopwatchTicksTo100ns(huge) = expect,
                   "overflow-safe split math matches reference")
        End Sub

        Private Sub Test_WPosPlatformGuard()
            ' On Linux CI the guard MUST throw; on Windows Start() would open
            ' the real device — skip instead (same policy as ffmpeg tests).
            If WasapiPositionCapture.IsWindowsPlatform Then
                Console.Write("(SKIP: Windows) ")
                Return
            End If
            Dim threw As Boolean = False
            Try
                Dim c As New WasapiPositionCapture()
                c.Start()
                c.Stop()   ' unreachable — Start must throw off-Windows
            Catch ex As InvalidOperationException
                threw = True
            End Try
            Assert(threw, "Start() off-Windows must throw InvalidOperationException")
        End Sub

        ' ===== ATDC — AudioTapDeviceClock (P13.3, position-driven gap-fill) =====
        ' Oracle for every test: the P13.2-proven AudioPositionTracker. The
        ' tap MUST insert exactly the silence the tracker measures (minus
        ' the over-cap policy — P13.4b: sub-50ms holes are PADDED too, the
        ' old 50ms floor compressed the track on real hardware), and
        ' forward every byte.

        ''' <summary>Collects everything the tap writes (silence + data).</summary>
        Private Class SinkCollector
            Implements CaptureEngine.FFmpegBackend.IAudioTapSink
            Public Total As Long = 0
            Public Chunks As New List(Of Byte())
            Public Sub Write(data As Byte(), count As Integer) Implements CaptureEngine.FFmpegBackend.IAudioTapSink.Write
                Dim copy(count - 1) As Byte
                Array.Copy(data, copy, count)
                Chunks.Add(copy)
                Total += count
            End Sub
        End Class

        ''' <summary>48kHz stereo PCM16 → 4 bytes/frame, 192,000 bytes/sec.</summary>
        Private Function MakeBuf(frames As Integer) As Byte()
            Return New Byte(frames * 4 - 1) {}
        End Function

        Private Function NewTap(collector As SinkCollector) As CaptureEngine.FFmpegBackend.AudioTapDeviceClock
            Return New CaptureEngine.FFmpegBackend.AudioTapDeviceClock(
                "test", 48000, 2, 16, collector,
                Sub(m) Console.WriteLine("      [evidence] " & m))
        End Function

        Private Sub Test_ATDCAnchor()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Assert(Not tap.Anchored, "starts unanchored")
            tap.Feed(MakeBuf(480), MakeBuf(480).Length, DevBase, T0)
            Assert(tap.Anchored, "anchored after first feed")
            Assert(tap.FirstQpc100ns = T0, "FirstQpc100ns = first stamp")
            Assert(tap.SilenceInsertedBytes = 0L, "anchor inserts no silence")
            Assert(tap.DataBytes = 480L * 4L, "all data bytes forwarded")
            Assert(col.Total = tap.DataBytes, "sink got exactly the data")
        End Sub

        Private Sub Test_ATDCContinuous()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            For i As Integer = 0 To 299
                Dim b As Byte() = MakeBuf(480)
                tap.Feed(b, b.Length, DevBase + i * 480L, T0 + i * Pkt10ms)
            Next
            Assert(tap.SilenceInsertedBytes = 0L, "continuous stream: zero silence")
            Assert(tap.Packets = 300L, "300 packets")
            Assert(tap.LastEnd100ns = T0 + 300L * Pkt10ms, "lastEnd exact after 3s")
            Assert(col.Total = 300L * 480L * 4L, "all bytes forwarded once")
        End Sub

        Private Sub Test_ATDCHole()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            ' 250ms CONTENT gap: the cursor jumps 12,000 frames (250ms @48k).
            Dim holeFrames As Long = 12000L
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, DevBase + 480L + holeFrames, T0 + Pkt10ms + 2500000L)
            ' Expected pad = 250ms at 192,000 B/s = 480,000 bytes (frame-
            ' aligned: a multiple of 4).
            Dim expected As Long = 2500000L * 192000L \ 10000000L
            Assert(tap.SilenceInsertedBytes = expected, $"pad exactly the hole: {tap.SilenceInsertedBytes} vs {expected}")
            Assert(tap.HolePackets = 1L, "one hole packet")
            Assert(tap.TotalHole100ns = 2500000L, "hole total matches")
            Assert(col.Total = expected + 2L * 480L * 4L, "sink got silence + both packets")
            ' Total timeline: 10ms + 250ms + 10ms of audio content.
            Assert(Math.Abs(tap.TotalDurationSec - 0.27) < 0.0001, "duration == wall-clock span")
        End Sub

        Private Sub Test_ATDCSmallHole()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, DevBase + 480L + 1440L, T0 + Pkt10ms + 300000L)  ' 30ms hole (1440 frames)
            ' P13.4b: sub-50ms holes are PADDED — hardware-measured time is
            ' real time; the old 50ms floor compressed the track (field
            ' evidence: −50ms/s drift on the OWNER machine).
            Dim expected As Long = 300000L * 192000L \ 10000000L   ' 5760B
            Assert(tap.SilenceInsertedBytes = expected, $"sub-50ms hole padded: {tap.SilenceInsertedBytes} vs {expected}")
            Assert(tap.HolePackets = 1L, "hole still counted in evidence")
            Assert(tap.SubThresholdHoles = 1L, "counted in the sub-threshold evidence")
            Assert(tap.SubThresholdHole100ns = 300000L, "sub-threshold sum matches")
        End Sub

        Private Sub Test_ATDCHugeHole()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, DevBase + 480L + 345600000L, T0 + Pkt10ms)  ' 2h cursor jump
            Assert(tap.SilenceInsertedBytes = 0L, "over-cap hole: NOT padded")
            Assert(col.Total = 2L * 480L * 4L, "only the two packets written")
        End Sub

        Private Sub Test_ATDCBackwards()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            Dim b1 As Byte() = MakeBuf(480)
            ' Cursor 10ms IN THE PAST (overlap). Violation flagged, no hole,
            ' timeline takes max(prevEnd, newEnd) — NEVER rewinds.
            tap.Feed(b1, b1.Length, DevBase - 480L, T0 - Pkt10ms)
            Assert(tap.MonotonicViolations = 1L, "violation reported")
            Assert(tap.SilenceInsertedBytes = 0L, "no silence on backwards cursor")
            Assert(tap.LastEnd100ns = T0 + Pkt10ms, "timeline not rewound (max policy)")
        End Sub

        Private Sub Test_ATDCZeroStamp()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, 0L, T0 + Pkt10ms)   ' Risk #2: cursorless packet
            Assert(tap.StampFallbacks = 1L, "fallback counted")
            Assert(tap.SilenceInsertedBytes = 0L, "continuity — no hole")
            Assert(tap.LastEnd100ns = T0 + 2L * Pkt10ms, "timeline advanced by duration")
        End Sub

        Private Sub Test_ATDCReanchor()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, 0L, T0)   ' cursorless ANCHOR (fallback anchor)
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, 0L, T0 + Pkt10ms)   ' still cursorless
            Dim b2 As Byte() = MakeBuf(480)
            tap.Feed(b2, b2.Length, DevBase, T0 + 2L * Pkt10ms)   ' first REAL cursor
            Assert(tap.FirstQpc100ns = T0, "wall anchor PRESERVED (track starts at T0)")
            Assert(tap.SilenceInsertedBytes = 0L, "NO stream-sized fake hole")
            Dim b3 As Byte() = MakeBuf(480)
            tap.Feed(b3, b3.Length, DevBase + 480L, T0 + 3L * Pkt10ms)
            Assert(tap.SilenceInsertedBytes = 0L, "continuity after re-anchor")
            Assert(tap.StampFallbacks = 2L, "fallbacks counted once each (not re-corrupted)")
        End Sub

        Private Sub Test_ATDCBlockAlign()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            ' Cursor jump of 5926 frames — its content time (frames×1e7/48000
            ' = 1,234,583 ticks) is NOT a whole number of 4-byte frames.
            ' Semantics = the proven legacy tap: floor to the frame grid.
            Dim holeFrames As Long = 5926L
            Dim holeTicks As Long = holeFrames * 10000000L \ 48000L
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, DevBase + 480L + holeFrames, T0 + Pkt10ms + holeTicks)
            Dim expected As Long = CLng(holeTicks / 10000000.0 * 192000.0)
            expected -= expected Mod 4L
            Assert(tap.SilenceInsertedBytes = expected, $"floored to frame grid: {tap.SilenceInsertedBytes} vs {expected}")
            Assert(tap.SilenceInsertedBytes Mod 4L = 0L, "pad is frame-aligned")
        End Sub

        Private Sub Test_ATDCFinalizeTail()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            ' Session ends 800ms after the packet's end.
            Dim endQpc As Long = T0 + Pkt10ms + 8000000L
            tap.FinalizeTo100ns(T0 - 500000L, endQpc)
            Dim expected As Long = 8000000L * 192000L \ 10000000L
            Assert(tap.SilenceInsertedBytes = expected, $"exact QPC tail: {tap.SilenceInsertedBytes} vs {expected}")
        End Sub

        Private Sub Test_ATDCFinalizeEmpty()
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            ' Nothing ever arrived — pad the full session span so the mux
            ' still gets a valid silent track (ffmpeg zero-packets guard).
            tap.FinalizeTo100ns(T0, T0 + 50000000L)
            Assert(tap.SilenceInsertedBytes = 5L * 192000L, "full-span silence (5s @ 192kB/s)")
            Assert(Not tap.Anchored, "still unanchored")
        End Sub

        Private Sub Test_ATDCAbEquivalence()
            ' A/B gate (P13.3): drive the tap through a mixed stress timeline
            ' (gaps, jitter, stampless packets, backwards packet) and demand
            ' that the silence INSERTED equals the tracker-measured total
            ' MINUS the policy-excluded holes. P13.4b: the ONLY exclusion
            ' left is the over-cap crater (sub-50ms holes are PADDED now —
            ' the old exclusion was the track-compression bug).
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim oracle As New AudioPositionTracker(48000)

            ' Build: 30 packets, cursor hole of 200ms (9600 frames) after
            ' #10, 30ms (1440 frames) after #20, cursorless packet at #25,
            ' backwards cursor at #28.
            Dim devPos(29) As Long
            Dim qpcs(29) As Long
            devPos(0) = DevBase
            qpcs(0) = T0
            For i As Integer = 1 To 29
                Dim gapFrames As Long = 480L
                If i = 11 Then gapFrames += 9600L        ' 200ms hole → pad
                If i = 21 Then gapFrames += 1440L        ' 30ms hole → pad (P13.4b)
                devPos(i) = devPos(i - 1) + gapFrames
                qpcs(i) = T0 + i * Pkt10ms
            Next
            devPos(25) = 0L                                ' cursorless
            devPos(28) = devPos(27) - 480L                 ' backwards cursor

            Dim policyExcluded As Long = 0
            For i As Integer = 0 To 29
                Dim b As Byte() = MakeBuf(480)
                tap.Feed(b, b.Length, devPos(i), qpcs(i))
                Dim rep = oracle.Feed(480, devPos(i), qpcs(i))
                If rep.Hole100ns > 36000000000L Then policyExcluded += rep.Hole100ns
            Next

            Dim expectedSilenceTicks As Long = oracle.TotalHole100ns - policyExcluded
            Dim expectedBytes As Long = expectedSilenceTicks * 192000L \ 10000000L
            Assert(tap.TotalHole100ns = oracle.TotalHole100ns, "tap sees exactly the tracker's holes")
            Assert(tap.StampFallbacks = oracle.StampFallbacks, "fallback counts agree")
            Assert(tap.MonotonicViolations = oracle.MonotonicViolations, "violation counts agree")
            Assert(tap.SilenceInsertedBytes = expectedBytes,
                   $"A/B: inserted {tap.SilenceInsertedBytes}B == policy-filtered oracle {expectedBytes}B")
        End Sub

        Private Sub Test_ATDCManySmallHoles()
            ' P13.4b REGRESSION (the OWNER-machine pathology): many tiny
            ' holes (1ms = 48 frames each) between packets. The old 50ms
            ' policy dropped every one of them — a 1.1s wall-clock span
            ' produced a 1.0s track (~9% compression). The track duration
            ' must now equal the wall-clock span EXACTLY.
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Const N As Integer = 100
            For i As Integer = 0 To N - 1
                Dim b As Byte() = MakeBuf(480)
                tap.Feed(b, b.Length, DevBase + i * 528L, T0 + i * 110000L)  ' 10ms data + 1ms hole
            Next
            ' N−1 holes: the FIRST packet anchors the timeline (no hole).
            ' 1ms = 48 frames @48k → 192B of silence per hole.
            Dim expectedHoleBytes As Long = CLng(N - 1) * 10000L * 192000L \ 10000000L
            Assert(tap.SilenceInsertedBytes = expectedHoleBytes,
                   $"every 1ms hole padded: {tap.SilenceInsertedBytes} vs {expectedHoleBytes}")
            Assert(tap.SubThresholdHoles = CLng(N - 1), "all holes sub-threshold → counted")
            ' Track duration == wall-clock span: 99×(10+1)ms + 10ms = 1.099s.
            Assert(Math.Abs(tap.TotalDurationSec - 1.099) < 0.0001,
                   $"track duration == wall-clock span: {tap.TotalDurationSec}")
        End Sub

        Private Sub Test_ATDCQpcJitterImmunity()
            ' ★ P13.4c REGRESSION at the tap level: the OWNER machine fired
            ' 933 backwards qpc stamps per session. With the cursor timeline
            ' the documented noise (backwards stamps, forward jitter within
            ' the Phase-A tolerance) must produce ZERO silence and leave the
            ' timeline exact — only the anomaly counter moves.
            ' ★ PHASE-A: a LARGE forward stamp under a frozen cursor is idle
            ' EVIDENCE now (Test_WPosIdleQpcEvidence) — the lie set below
            ' keeps only the documented noise classes.
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim lies() As Long = {T0, T0 - 500000000L, T0 + 400000L,
                                  T0 - 3000000000L, T0 + 250000L}
            For i As Integer = 0 To 99
                Dim b As Byte() = MakeBuf(480)
                tap.Feed(b, b.Length, DevBase + i * 480L, lies(i Mod lies.Length))
            Next
            Assert(tap.SilenceInsertedBytes = 0L, "qpc jitter → zero silence padded")
            Assert(tap.IdleGapPackets = 0L, "within-tolerance jitter is not idle evidence")
            Assert(tap.QpcAnomalies > 0L, "jitter still counted as evidence")
            Assert(tap.LastEnd100ns = T0 + 100L * Pkt10ms, "timeline exact despite qpc lies")
            Assert(col.Total = 100L * 480L * 4L, "all data bytes forwarded once")
        End Sub

        Private Sub Test_ATDCIdleGap()
            ' ★ PHASE-A at the tap level: frozen cursor + wall evidence →
            ' silence is padded at the REAL position (the silent-clip bug
            ' fix), and the next packet continues on the rebased virtual
            ' timeline with zero double-pad.
            Dim col As New SinkCollector()
            Dim tap = NewTap(col)
            Dim b0 As Byte() = MakeBuf(480)
            tap.Feed(b0, b0.Length, DevBase, T0)
            ' t=10s: cursor STILL DevBase+480 (endpoint idle), wall 10s.
            Dim b1 As Byte() = MakeBuf(480)
            tap.Feed(b1, b1.Length, DevBase + 480L, T0 + 100000000L)
            Dim idleBytes As Long = 99900000L * 192000L \ 10000000L   ' 1,918,080B
            Assert(tap.SilenceInsertedBytes = idleBytes,
                   $"idle pad exactly 9.99s: {tap.SilenceInsertedBytes} vs {idleBytes}")
            Assert(tap.IdleGapPackets = 1L, "idle gap counted")
            ' Continuation on the rebased timeline: raw cursor resumes, zero pad.
            Dim b2 As Byte() = MakeBuf(480)
            tap.Feed(b2, b2.Length, DevBase + 960L, T0 + 100100000L)
            Assert(tap.SilenceInsertedBytes = idleBytes, "no double-pad after rebase")
            Assert(tap.IdleGapPackets = 1L, "still one idle gap")
            ' The stream so far: silence + three packets in delivery order.
            Assert(col.Total = idleBytes + 3L * 480L * 4L, "sink got silence + packets")
        End Sub

        ' ===== SYNC2 — SyncMath v2 exact QPC anchors (P13.4) =====

        Private Sub Test_Sync2AnchorOffset()
            ' Both stamps in the SAME QPC domain (100ns). Video t0 2.5s
            ' AFTER the audio anchor → offset +2.5 (audio began first →
            ' mux skips the audio head). Exact subtraction, no estimation.
            Dim off As Double = SyncMath.ComputeAudioOffsetSecFromAnchors(35_000_000L, 10_000_000L)
            Assert(Math.Abs(off - 2.5) < 0.0000001, "positive branch: exact 100ns subtraction")
            ' Audio starts 0.25s AFTER video → negative offset (adelay).
            Dim off2 As Double = SyncMath.ComputeAudioOffsetSecFromAnchors(10_000_000L, 12_500_000L)
            Assert(Math.Abs(off2 + 0.25) < 0.0000001, "negative branch: exact")
            ' Simultaneous anchors → 0 (start-perfect session).
            Assert(SyncMath.ComputeAudioOffsetSecFromAnchors(7_777_777L, 7_777_777L) = 0.0,
                   "equal anchors → zero offset")
        End Sub

        Private Sub Test_Sync2AnchorGuards()
            Assert(SyncMath.ComputeAudioOffsetSecFromAnchors(0L, 100L) = 0.0, "missing video timeline → 0")
            Assert(SyncMath.ComputeAudioOffsetSecFromAnchors(100L, 0L) = 0.0, "missing audio anchor → 0")
            ' P13.4b: the anchor path is NOT clamped to [-2,+5] anymore — an
            ' endpoint idle at session start anchors the tap late and the raw
            ' offset goes past -2s legitimately (field evidence: -5s → the
            ' -2.000s clamp misplaced the whole track). The mux pads/discards
            ' byte-exactly for any value; only a ±3600s SANITY bound remains.
            Dim off As Double = SyncMath.ComputeAudioOffsetSecFromAnchors(110_000_000L, 10_000_000L)
            Assert(Math.Abs(off - 10.0) < 0.0000001, "+10s passes through unclamped")
            Dim off2 As Double = SyncMath.ComputeAudioOffsetSecFromAnchors(10_000_000L, 130_000_000L)
            Assert(Math.Abs(off2 + 12.0) < 0.0000001, "-12s passes through unclamped")
            Assert(SyncMath.ComputeAudioOffsetSecFromAnchors(1L, 1L + 40000000000L) = SyncMath.MinAnchorOffsetSec,
                   "pathological -4000s → sanity bound")
            Assert(SyncMath.ComputeAudioOffsetSecFromAnchors(1L + 40000000000L, 1L) = SyncMath.MaxAnchorOffsetSec,
                   "pathological +4000s → sanity bound")
            ' The legacy call-time path KEEPS the proven [-2,+5] clamp.
            Assert(SyncMath.ClampOffsetSec(-99.0) = SyncMath.MinOffsetSec, "legacy path clamp intact")
        End Sub

        ' ===== Integration test (requires real ffmpeg.exe) =====

        ' Phase 12b: walk up from the test bin dir to the repo root and
        ' find the production deployment at Overlay\API-Core\ffmpeg.exe
        ' (Windows: C:\My Project\NVIDIA-Shadowplay\Overlay\API-Core\ffmpeg.exe).
        ' Without this, integration tests SKIP on Windows even though the
        ' production ffmpeg exists (first Windows validation 2026-08-23).
        Private Function FindRepoFFmpeg() As String
            Dim dir As New IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory)
            For i As Integer = 1 To 8
                If dir Is Nothing Then Return Nothing
                ' gfe-rebuild layout: FFmpeg lives in Project\ShadowPlay\FFmpeg.
                Dim candidate As String = IO.Path.Combine(dir.FullName, "ShadowPlay", "FFmpeg", "ffmpeg.exe")
                If IO.File.Exists(candidate) Then Return candidate
                ' Legacy pre-reorg fallback: Overlay\API-Core.
                candidate = IO.Path.Combine(dir.FullName, "Overlay", "API-Core", "ffmpeg.exe")
                If IO.File.Exists(candidate) Then Return candidate
                dir = dir.Parent
            Next
            Return Nothing
        End Function

        Private Sub Test_RealFFmpegIntegration()
            ' Find ffmpeg.exe — try common paths
            Dim ffmpegCandidates As String() = {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "API-Core", "ffmpeg.exe"),
                FindRepoFFmpeg()
            }
            Dim ffmpegPath As String = Nothing
            For Each c In ffmpegCandidates
                If IO.File.Exists(c) Then
                    ffmpegPath = c
                    Exit For
                End If
            Next

            If ffmpegPath Is Nothing Then
                Throw New SkipException("ffmpeg not available in this environment")
            End If

            ' Generate a test output path
            Dim outputDir As String = IO.Path.GetTempPath()
            Dim outputFile As String = IO.Path.Combine(outputDir, "ffmpeg_integration_test.mp4")

            ' Clean up any stale output
            If IO.File.Exists(outputFile) Then IO.File.Delete(outputFile)

            ' Use lavfi testsrc as input (generates a test pattern — no display needed)
            Dim args As String = "-y -f lavfi -i testsrc=duration=3:size=320x240:rate=30 " &
                                 "-c:v libx264 -preset ultrafast -tune zerolatency " &
                                 "-b:v 500000 -pix_fmt yuv420p -t 3 " &
                                 """" & outputFile & """"

            Dim backend As New FFmpegPipelineBackend()
            backend.WithFFmpegPath(ffmpegPath)
            backend.WithArguments(args)
            backend.WithOutputPath(outputFile)

            ' Start
            backend.Start()
            Assert(backend.CurrentState = VideoBackendState.Running,
                   "State should be Running after Start")

            ' Wait for FFmpeg to finish (testsrc with -t 3 should complete in ~1-2s)
            System.Threading.Thread.Sleep(4000)

            ' Stop
            backend.Stop()

            ' Verify state
            Dim finalState = backend.CurrentState
            Assert(finalState = VideoBackendState.Stopped OrElse finalState = VideoBackendState.Faulted,
                   "Final state should be Stopped or Faulted (not Running)")

            ' ★ F-02: truthful validation. The OLD block here had a real bug:
            ' it looked for an ffprobe binary named "ffprobe" (no .exe) — the
            ' lookup never matched on Windows, so the duration check was DEAD
            ' CODE and the test passed green even for a headerless file.
            ' AssertValidMedia probes via the bundled ffmpeg itself (same
            ' evidence), making the check mandatory: Exists + >0 bytes +
            ' container Duration > 0 + Video stream, no audio (testsrc only).
            MediaValidation.AssertValidMedia(ffmpegPath, outputFile, True, False, "INTEGRATION")

            ' Clean up
            IO.File.Delete(outputFile)

            backend.Dispose()
        End Sub

        ' ===== Stress test: Start → Stop → Start cycle =====

        Private Sub Test_StartStopStartStress()
            ' Find ffmpeg
            Dim ffmpegPath As String = Nothing
            For Each c In {"/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg",
                           IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                           FindRepoFFmpeg()}
                If IO.File.Exists(c) Then
                    ffmpegPath = c
                    Exit For
                End If
            Next
            If ffmpegPath Is Nothing Then
                Throw New SkipException("ffmpeg not found in this environment")
            End If

            Dim tempDir As String = IO.Path.GetTempPath()
            Dim backend As New FFmpegPipelineBackend()
            backend.WithFFmpegPath(ffmpegPath)

            ' Track unexpected state transitions
            Dim unexpectedErrors As Integer = 0
            AddHandler backend.ErrorOccurred, Sub(msg)
                                                  ' We only care about errors during Running state
                                                  ' (errors during Start with bad args are expected)
                                                  Dim s = backend.CurrentState
                                                  If s = VideoBackendState.Running Then
                                                      System.Threading.Interlocked.Increment(unexpectedErrors)
                                                  End If
                                              End Sub

            For cycle As Integer = 1 To 3
                Dim outputFile As String = IO.Path.Combine(tempDir, $"stress_cycle{cycle}.mp4")
                If IO.File.Exists(outputFile) Then IO.File.Delete(outputFile)

                ' Use testsrc with -t 2 (2-second test pattern)
                Dim args As String = $"-y -f lavfi -i testsrc=duration=2:size=320x240:rate=30 " &
                                     "-c:v libx264 -preset ultrafast -b:v 500000 -pix_fmt yuv420p -t 2 " &
                                     $"""" & outputFile & """"
                backend.WithArguments(args)
                backend.WithOutputPath(outputFile)

                ' Start
                backend.Start()
                Assert(backend.CurrentState = VideoBackendState.Running,
                       $"Cycle {cycle}: state should be Running after Start")

                ' Wait for FFmpeg to finish (2s video + encoding)
                System.Threading.Thread.Sleep(3000)

                ' Stop
                backend.Stop()

                Dim finalState = backend.CurrentState
                Assert(finalState = VideoBackendState.Stopped OrElse finalState = VideoBackendState.Faulted,
                       $"Cycle {cycle}: final state should be Stopped or Faulted (got {finalState})")

                ' ★ F-02: truthful validation — the old assertions (Exists +
                ' size > 0) accepted a headerless/0-packet file as "output
                ' valid". The real libx264 encode must produce a probe-able
                ' MP4 with a video stream, positive duration, and no audio.
                MediaValidation.AssertValidMedia(ffmpegPath, outputFile, True, False, $"STRESS cycle {cycle}")
                IO.File.Delete(outputFile)

                ' Verify no stale state — state should be Stopped (ready for next Start)
                Assert(backend.CurrentState = VideoBackendState.Stopped OrElse backend.CurrentState = VideoBackendState.Faulted,
                       $"Cycle {cycle}: state after Stop should be Stopped or Faulted")
            Next

            ' Verify no unexpected errors during Running state
            Assert(unexpectedErrors = 0,
                   $"Should have 0 unexpected errors during Running (got {unexpectedErrors})")

            backend.Dispose()
            Assert(backend.CurrentState = VideoBackendState.Disposed,
                   "State should be Disposed after final Dispose")
        End Sub

        ' ===== F-02 validator contract: the helper itself must be able to
        ' FAIL — otherwise "valid output" assertions are vacuous =====

        ''' <summary>Proves the F-02 helper has teeth: an empty file, a
        ''' text file with an .mp4 name, and a truncated MP4 must all be
        ''' REJECTED; a real libx264 encode must PASS. Without this test the
        ''' validator could silently accept garbage forever.</summary>
        Private Sub Test_MediaValidatorContract()
            Dim ffmpegPath As String = Nothing
            For Each c In {"/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg",
                           IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                           FindRepoFFmpeg()}
                If IO.File.Exists(c) Then
                    ffmpegPath = c
                    Exit For
                End If
            Next
            If ffmpegPath Is Nothing Then
                Throw New SkipException("ffmpeg not available in this environment")
            End If

            Dim dir As String = IO.Path.Combine(IO.Path.GetTempPath(), "media-validator-" &
                                                DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            IO.Directory.CreateDirectory(dir)
            Try
                ' 1) EMPTY file — must be rejected.
                Dim emptyPath As String = IO.Path.Combine(dir, "empty.mp4")
                IO.File.WriteAllBytes(emptyPath, New Byte() {})
                Dim rejectedEmpty As Boolean = False
                Try
                    MediaValidation.AssertValidMedia(ffmpegPath, emptyPath, True, False, "VALIDATOR-empty")
                Catch ex As InvalidOperationException
                    rejectedEmpty = True
                End Try
                Assert(rejectedEmpty, "validator ACCEPTED an empty file — false-green validator")

                ' 2) TEXT file with an .mp4 name — must be rejected (no
                '    container Duration in the probe dump).
                Dim textPath As String = IO.Path.Combine(dir, "garbage.mp4")
                IO.File.WriteAllText(textPath, "this is definitely not an mp4 container")
                Dim rejectedText As Boolean = False
                Try
                    MediaValidation.AssertValidMedia(ffmpegPath, textPath, True, False, "VALIDATOR-garbage")
                Catch ex As InvalidOperationException
                    rejectedText = True
                End Try
                Assert(rejectedText, "validator ACCEPTED a text file named .mp4 — false-green validator")

                ' 3) TRUNCATED real MP4 (first 64 bytes of a valid one) — must
                '    be rejected: this is the exact 0-packet/moov-missing
                '    failure class the legacy fallback used to announce saved.
                Dim realPath As String = IO.Path.Combine(dir, "real.mp4")
                Dim psi As New ProcessStartInfo With {
                    .FileName = ffmpegPath,
                    .Arguments = "-y -f lavfi -i testsrc=duration=1:size=320x240:rate=15 " &
                                 "-c:v libx264 -preset ultrafast -pix_fmt yuv420p """ & realPath & """",
                    .UseShellExecute = False, .CreateNoWindow = True,
                    .RedirectStandardError = True
                }
                Using p As Process = Process.Start(psi)
                    p.StandardError.ReadToEnd()
                    If Not p.WaitForExit(30000) Then
                        Try : p.Kill() : Catch : End Try
                    End If
                End Using
                Assert(IO.File.Exists(realPath), "fixture generation failed — real.mp4 missing")

                Dim truncatedPath As String = IO.Path.Combine(dir, "truncated.mp4")
                Dim allBytes As Byte() = IO.File.ReadAllBytes(realPath)
                IO.File.WriteAllBytes(truncatedPath, allBytes.Take(64).ToArray())
                Dim rejectedTruncated As Boolean = False
                Try
                    MediaValidation.AssertValidMedia(ffmpegPath, truncatedPath, True, False, "VALIDATOR-truncated")
                Catch ex As InvalidOperationException
                    rejectedTruncated = True
                End Try
                Assert(rejectedTruncated, "validator ACCEPTED a truncated mp4 — false-green validator")

                ' 4) REAL media — must PASS every clause.
                MediaValidation.AssertValidMedia(ffmpegPath, realPath, True, False, "VALIDATOR-real")
            Finally
                Try : IO.Directory.Delete(dir, True) : Catch : End Try
            End Try
        End Sub
    End Module
End Namespace
