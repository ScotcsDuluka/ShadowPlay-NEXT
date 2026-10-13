Option Strict On
Option Explicit On
Option Infer On

' Program.vb — Phase 12b unit tests (no ffmpeg required).
' Runtime sync tests (real ffmpeg) live in RuntimeSyncTests.vb.

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording.Tests

    Friend Module TestRunner
        Friend _passed As Integer = 0
        Friend _failed As Integer = 0
        Friend _skipped As Integer = 0
        ' ★ P13-A: contract tests that are EXPECTED to fail until a pending
        ' fix lands. A known-fail never breaks the exit code; the moment the
        ' fix lands it starts passing and shows up in _passed as verified.
        Friend _knownFails As Integer = 0
        Friend ReadOnly _failures As New List(Of String)()

        Friend Sub RunTest(name As String, test As Action)
            Console.Write($"  {name} ... ")
            Try
                test()
                Console.WriteLine("PASS")
                _passed += 1
            Catch ex As Exception
                Console.WriteLine("FAIL")
                Console.WriteLine($"      → {ex.Message}")
                _failures.Add(name & ": " & ex.Message)
                _failed += 1
            End Try
        End Sub

        ''' <summary>Run a contract test that pins a KNOWN bug. Throw today
        ''' = deterministic bug reproduction (logged, does NOT fail the
        ''' suite); pass = the fix landed, reported as verified.</summary>
        Friend Sub RunKnownFail(name As String, test As Action, note As String)
            Console.Write($"  {name} ... ")
            Try
                test()
                Console.WriteLine("PASS — fix verified")
                _passed += 1
            Catch ex As Exception
                Console.WriteLine("KNOWN-FAIL (expected until fix)")
                Console.WriteLine($"      → {ex.Message}")
                Console.WriteLine($"      note: {note}")
                _knownFails += 1
            End Try
        End Sub

        Friend Sub RunSkip(name As String, reason As String)
            Console.WriteLine($"  {name} ... SKIP ({reason})")
            _skipped += 1
        End Sub

        Friend Sub Assert(cond As Boolean, message As String)
            If Not cond Then Throw New Exception(message)
        End Sub

        Friend Sub AssertNear(actual As Double, expected As Double, tolerance As Double, label As String)
            If Math.Abs(actual - expected) > tolerance Then
                Throw New Exception($"{label}: expected {expected:0.000} ±{tolerance:0.000}, got {actual:0.000}")
            End If
        End Sub
    End Module

    Friend Module Program

        Function Main(args As String()) As Integer
            Console.WriteLine("==================================================")
            Console.WriteLine(" CaptureEngine.Recording.Tests — Phase 12b")
            Console.WriteLine(" (SyncMath + WavSidecar + real-ffmpeg sync)")
            Console.WriteLine("==================================================")
            Console.WriteLine()

            Try
                SyncMathTests.RunAll()
                WavSidecarTests.RunAll()
                DisposerTests.RunAll()   ' ★ GPU disposer: no-lost-frame + no-throw contracts
                LiveMuxJobOwnershipTests.RunAll()   ' ★ F-07: LiveMux ffmpeg process ownership (real ffmpeg)
                AudioTimelineRepairTests.RunAll()   ' ★ P13-AUDIO-TIMELINE: OBS gap-repair rules
                AudioTimelineDeviceClockTests.RunAll()   ' ★ P13-A: OWNER-spec deterministic sample timeline (no hardware)
                MuxSinkAccountingTests.RunAll()   ' ★ Audit fix: pending-cap drops are counted, never silent
                SessionResultContractTests.RunAll()   ' ★ M2-W2: video-only loss gate — Pass contract (1 known-fail until the fix lands)
                RuntimeSyncTests.RunAll()
                DualTrackTests.RunAll()   ' ★ M1: system + mic second track
                P3AStopHardeningTests.RunAll()   ' ★ W3 P3-A: stop/finalize hardening (budget-bounded)
            Finally
                ' Sandbox hygiene (C-hygiene pass): the RRT_RT_ sandbox created
                ' by RuntimeSyncTests/DualTrackTests is removed on EVERY outcome
                ' — PASS, FAIL, assertion failure, timeout, unexpected
                ' exception. Cleanup failures are reported separately and
                ' never change the test verdict or the exit code.
                RuntimeSyncTests.CleanupSandbox()
                If RuntimeSyncTests.SandboxCleanupWarnings.Count > 0 Then
                    Console.WriteLine()
                    Console.WriteLine($" SANDBOX CLEANUP WARNINGS: {RuntimeSyncTests.SandboxCleanupWarnings.Count} (test verdicts unaffected)")
                    For Each w As String In RuntimeSyncTests.SandboxCleanupWarnings
                        Console.WriteLine("   - " & w)
                    Next
                End If
            End Try

            Console.WriteLine()
            Console.WriteLine("--------------------------------------------------")
            Console.WriteLine($" RESULT: {TestRunner._passed} passed, {TestRunner._failed} failed, {TestRunner._skipped} skipped, {TestRunner._knownFails} known-fail (pending fix)")
            If TestRunner._failures.Count > 0 Then
                Console.WriteLine(" Failures:")
                For Each f As String In TestRunner._failures
                    Console.WriteLine($"   - {f}")
                Next
            End If
            Console.WriteLine("--------------------------------------------------")
            Return If(TestRunner._failed > 0, 1, 0)
        End Function

    End Module

    ''' <summary>Unit tests for the extracted proven offset model.</summary>
    Friend Module SyncMathTests

        Public Sub RunAll()
            Console.WriteLine("── SyncMath (proven offset model) ──")
            TestRunner.RunTest("SYNC: offset zero when video ticks unknown", AddressOf Test_ZeroWhenNoVideo)
            TestRunner.RunTest("SYNC: offset zero when audio never started", AddressOf Test_ZeroWhenNoAudio)
            TestRunner.RunTest("SYNC: audio started first → positive offset", AddressOf Test_AudioFirst)
            TestRunner.RunTest("SYNC: audio started late → negative offset", AddressOf Test_AudioLate)
            TestRunner.RunTest("SYNC: clamps to [-2, +5]", AddressOf Test_Clamps)
            TestRunner.RunTest("SYNC: OffsetToDelayMs mirrors mux adelay", AddressOf Test_DelayMs)
            TestRunner.RunTest("SYNC: NeedsInputSkip / FormatInputSkipArg", AddressOf Test_SkipArg)
        End Sub

        Private Sub Test_ZeroWhenNoVideo()
            ' videoStart=0 (no frame captured) → offset must be 0 regardless of audio
            Dim off As Double = SyncMath.ComputeAudioOffsetSec(0, 12345, 1000.0)
            TestRunner.AssertNear(off, 0.0, 0.000001, "offset(video=0)")
        End Sub

        Private Sub Test_ZeroWhenNoAudio()
            Dim off As Double = SyncMath.ComputeAudioOffsetSec(99999, 0, 1000.0)
            TestRunner.AssertNear(off, 0.0, 0.000001, "offset(audio=0)")
        End Sub

        Private Sub Test_AudioFirst()
            ' audio started 250ms BEFORE first video frame → offset +0.25 (skip audio head)
            ' Origin = 10s on the session clock so both ticks stay positive on
            ' every Stopwatch.Frequency (Linux = 1GHz, Windows ≈10MHz).
            Dim freq As Double = Stopwatch.Frequency
            Dim video As Long = CLng(freq * 10.0)
            Dim audio As Long = CLng(video - 0.25 * freq)
            TestRunner.Assert(audio > 0, "test origin positive")
            Dim off As Double = SyncMath.ComputeAudioOffsetSec(video, audio, freq)
            TestRunner.AssertNear(off, 0.25, 0.0005, "offset(audio first)")
        End Sub

        Private Sub Test_AudioLate()
            ' audio started 500ms AFTER first video frame → offset -0.5 (delay audio)
            Dim freq As Double = Stopwatch.Frequency
            Dim video As Long = 10_000_000L
            Dim audio As Long = CLng(video + 0.5 * freq)
            Dim off As Double = SyncMath.ComputeAudioOffsetSec(video, audio, freq)
            TestRunner.AssertNear(off, -0.5, 0.0005, "offset(audio late)")
        End Sub

        Private Sub Test_Clamps()
            TestRunner.AssertNear(SyncMath.ClampOffsetSec(-99.0), -2.0, 0.000001, "min clamp")
            TestRunner.AssertNear(SyncMath.ClampOffsetSec(+99.0), 5.0, 0.000001, "max clamp")
            TestRunner.AssertNear(SyncMath.ClampOffsetSec(0.123), 0.123, 0.000001, "in-range passthrough")
        End Sub

        Private Sub Test_DelayMs()
            TestRunner.Assert(SyncMath.OffsetToDelayMs(0.0) = 0, "aligned → no delay")
            TestRunner.Assert(SyncMath.OffsetToDelayMs(0.5) = 0, "positive → skip, not delay")
            TestRunner.Assert(SyncMath.OffsetToDelayMs(-0.5) = 500, "negative → 500ms delay")
            TestRunner.Assert(SyncMath.OffsetToDelayMs(SyncMath.MinOffsetSec) = 2000, "clamped min → 2000ms")
        End Sub

        Private Sub Test_SkipArg()
            TestRunner.Assert(Not SyncMath.NeedsInputSkip(-0.5), "negative offset → no skip")
            TestRunner.Assert(Not SyncMath.NeedsInputSkip(0.0005), "sub-ms → no skip")
            TestRunner.Assert(SyncMath.NeedsInputSkip(0.5), "positive → skip")
            TestRunner.Assert(SyncMath.FormatInputSkipArg(0.5) = "0.500", "invariant format")
            TestRunner.Assert(SyncMath.FormatInputSkipArg(-1.0) = "", "no-skip returns empty")
        End Sub

    End Module

    ''' <summary>Unit tests for the bounded-queue WAV sidecar (no ffmpeg needed).</summary>
    Friend Module WavSidecarTests

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── WavSidecarWriter (bounded queue + accounting) ──")
            TestRunner.RunTest("WAV: header + accounting + duration", AddressOf Test_HeaderAndAccounting)
            TestRunner.RunTest("WAV: drop-on-full keeps accounting consistent", AddressOf Test_DropOnFull)
            TestRunner.RunTest("WAV: multi-threaded enqueue is safe", AddressOf Test_ConcurrentEnqueue)
            TestRunner.RunTest("WAV: Complete is idempotent-safe", AddressOf Test_CompleteTwiceSafe)
        End Sub

        Private Function TempWav(name As String) As String
            Return Path.Combine(Path.GetTempPath(), "RRT_WAV_" & Guid.NewGuid().ToString("N").Substring(0, 8) & "_" & name)
        End Function

        ''' <summary>Synthetic chunk: n samples of 440Hz stereo 16-bit at 48kHz.</summary>
        Friend Function ToneChunk(samples As Integer, amplitude As Double) As Byte()
            Dim bytes(samples * 4 - 1) As Byte   ' 2ch * 2 bytes
            For i As Integer = 0 To samples - 1
                Dim v As Integer = CInt(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * amplitude * 32767.0)
                If v > 32767 Then v = 32767
                If v < -32768 Then v = -32768
                Dim u As UShort = CUShort(v And &HFFFFI)
                Dim off As Integer = i * 4
                bytes(off) = CByte(u And &HFFUI)
                bytes(off + 1) = CByte((u >> 8) And &HFFUI)
                bytes(off + 2) = bytes(off)
                bytes(off + 3) = bytes(off + 1)
            Next
            Return bytes
        End Function

        Private Sub Test_HeaderAndAccounting()
            Dim path As String = TempWav("hdr.wav")
            Using w As New WavSidecarWriter(path, 2, 48000, 16)
                w.Start()
                Dim total As Long = 0
                For i As Integer = 1 To 100
                    Dim chunk = ToneChunk(480, 0.5)   ' 10ms each → 1.0s total
                    w.EnqueueChunk(chunk, chunk.Length)
                    total += chunk.Length
                    Thread.Sleep(2)                   ' let the writer drain a bit
                Next
                Dim report = w.Complete(5000)
                TestRunner.Assert(report.Succeeded, "finalize succeeded")
                TestRunner.Assert(report.AccountingOk, "accounting invariant: " & report.ToString())
                TestRunner.Assert(report.BytesWritten = total, $"all bytes written ({report.BytesWritten} of {total})")
                TestRunner.Assert(report.BytesDropped = 0, "nothing dropped in a calm run")
                TestRunner.AssertNear(report.DurationSec, 1.0, 0.01, "duration")
            End Using

            ' Header validity — canonical 44-byte PCM header
            Dim bytes As Byte() = File.ReadAllBytes(path)
            TestRunner.Assert(bytes.Length >= 44, "file ≥ 44 bytes")
            Dim magic As String = System.Text.Encoding.ASCII.GetString(bytes, 0, 4)
            Dim wave As String = System.Text.Encoding.ASCII.GetString(bytes, 8, 4)
            TestRunner.Assert(magic = "RIFF", "RIFF magic (got '" & magic & "')")
            TestRunner.Assert(wave = "WAVE", "WAVE magic (got '" & wave & "')")
            Dim riffSize As UInteger = BitConverter.ToUInt32(bytes, 4)
            TestRunner.Assert(riffSize = CUInt(bytes.Length - 8), $"RIFF size patched ({riffSize} vs file {bytes.Length})")
            Dim dataSize As UInteger = BitConverter.ToUInt32(bytes, 40)
            TestRunner.Assert(dataSize = CUInt(bytes.Length - 44), "data size patched")
            File.Delete(path)
        End Sub

        Private Sub Test_DropOnFull()
            Dim path As String = TempWav("drop.wav")
            Using w As New WavSidecarWriter(path, 2, 48000, 16, maxQueueChunks:=4)
                w.Start()

                ' Deterministic overflow: a 32MB first chunk keeps the writer
                ' thread blocked inside FileStream.Write for tens of ms while
                ' the producer floods 20 small chunks past the cap of 4.
                Dim bigChunk(32 * 1024 * 1024 - 1) As Byte
                w.EnqueueChunk(bigChunk, bigChunk.Length)

                For i As Integer = 1 To 20
                    Dim chunk = ToneChunk(480, 0.5)
                    w.EnqueueChunk(chunk, chunk.Length)
                Next

                Dim report = w.Complete(10000)
                TestRunner.Assert(report.AccountingOk, "accounting holds under overflow: " & report.ToString())
                TestRunner.Assert(report.ChunksDropped > 0, "expected drops under flood")
                TestRunner.Assert(report.Succeeded, "finalize still succeeds")
                TestRunner.Assert(report.BytesWritten >= bigChunk.Length, "head chunk fully written")
            End Using
            File.Delete(path)
        End Sub

        Private Sub Test_ConcurrentEnqueue()
            Dim path As String = TempWav("conc.wav")
            Using w As New WavSidecarWriter(path, 2, 48000, 16)
                w.Start()
                Dim t1 As New Thread(Sub()
                                         For i As Integer = 1 To 50
                                             Dim c = ToneChunk(480, 0.3)
                                             w.EnqueueChunk(c, c.Length)
                                         Next
                                     End Sub)
                Dim t2 As New Thread(Sub()
                                         For i As Integer = 1 To 50
                                             Dim c = ToneChunk(480, 0.3)
                                             w.EnqueueChunk(c, c.Length)
                                         Next
                                     End Sub)
                t1.Start() : t2.Start()
                t1.Join() : t2.Join()
                Dim report = w.Complete(5000)
                TestRunner.Assert(report.AccountingOk, "accounting holds across producers: " & report.ToString())
            End Using
            File.Delete(path)
        End Sub

        Private Sub Test_CompleteTwiceSafe()
            Dim path As String = TempWav("twice.wav")
            Dim w As New WavSidecarWriter(path, 2, 48000, 16)
            w.Start()
            Dim chunk = ToneChunk(480, 0.5)
            w.EnqueueChunk(chunk, chunk.Length)
            Dim r1 = w.Complete(3000)
            TestRunner.Assert(r1.Succeeded, "first Complete ok")
            Dim r2 = w.Complete(3000)
            TestRunner.Assert(Not r2.Succeeded, "second Complete reports not-finalizable")
            w.Dispose()
            File.Delete(path)
        End Sub

    End Module

End Namespace
