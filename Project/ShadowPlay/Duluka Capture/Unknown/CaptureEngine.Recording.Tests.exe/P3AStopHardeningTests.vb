Option Strict On
Option Explicit On
Option Infer On

' P3AStopHardeningTests.vb — W3 P3-A stop/finalize hardening proof.
'
' PINNED DEFECT (W3 endurance, E1): when the audio consumer wedges mid-run
' (ffmpeg stops reading the audio pipe), engine.Stop → FinalizeTrack →
' DispatchSilence streams the WHOLE tail silence through the blocked sink —
' each chunk bounded by PipeFeed.Feed's 10s producer timeout, but the tail
' has ~600 chunks → the stop path ran ~100 minutes (E1 was killed at 4.9min).
'
' FIX UNDER TEST: AudioEngineSession.Stop carries a FINALIZE BUDGET — once
' exceeded, remaining tail/hole silence is truncated (loud log) and the stop
' completes. The real sink's per-Write bound is PipeFeed.Feed's 10s producer
' timeout, so the budget overshoot is ≤ one write.
'
' The GATED SINK below stands in for the wedged consumer: every Write stalls
' a fixed time (the real AudioEngineMuxSink bound is 10s/Write through
' PipeFeed.Feed; the gate emulates the same call shape deterministically).

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Audio
Imports CaptureEngine.Audio.Wasapi

Namespace CaptureEngine.Recording.Tests

    ''' <summary>Test sink: every Write stalls StallMs (0 = pass-through).</summary>
    Friend Class GatedAudioSink
        Implements IAudioSink

        Private ReadOnly _stallMs As Integer
        Public Writes As Long = 0

        Public Sub New(stallMs As Integer)
            _stallMs = stallMs
        End Sub

        Public Sub Write(packet As AudioPacket) Implements IAudioSink.Write
            Interlocked.Increment(Writes)
            If _stallMs > 0 Then Thread.Sleep(_stallMs)
        End Sub
    End Class

    Friend Module P3AStopHardeningTests

        Private _logLines As New List(Of String)()
        Private _logSync As New Object()

        Private Function CollectLog() As Action(Of String)
            Return Sub(m)
                       SyncLock _logLines
                           _logLines.Add(m)
                       End SyncLock
                   End Sub
        End Function

        Private Function HasLog(needle As String) As Boolean
            SyncLock _logLines
                For Each l In _logLines
                    If l.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
                Next
            End SyncLock
            Return False
        End Function

        Private Function MakeEngine(sink As IAudioSink, ByRef t0 As Long) As AudioEngineSession
            Dim engine As New AudioEngineSession(
                New AudioEngineConfig With {.SystemEnabled = True, .MicrophoneEnabled = False},
                CollectLog())
            engine.AddSink(AudioTrackKind.System, sink)
            t0 = WasapiPositionCapture.StopwatchTicksTo100ns(Stopwatch.GetTimestamp())
            engine.Start(t0)
            Return engine
        End Function

        Private Function EndTicks(t0 As Long, seconds As Double) As Long
            Return t0 + CLng(seconds * 10_000_000L)
        End Function

        Public Sub RunAll()
            RunTest("P3A-1/2: normal stop, healthy consumer — bounded, honest Stopped", AddressOf Test_NormalStop)
            RunTest("P3A-3/5: stalled consumer → finalize budget trips, Stop bounded", AddressOf Test_StalledConsumerBudget)
            RunTest("P3A-6: Dispose after budget-tripped stop — idempotent, no throw", AddressOf Test_DisposeAfterFailedStop)
            RunTest("P3A-7: fresh engine after recovery — Start/Stop normal", AddressOf Test_StartAfterRecovery)
            RunTest("P3A-S: stress 50× Start/Stop (normal/stalled/long-stall mix)", AddressOf Test_Stress50Mixed)
        End Sub

        Private Sub RunTest(name As String, test As Action)
            Console.Write($"  {name} ... ")
            Dim sw = Stopwatch.StartNew()
            Try
                test()
                Console.WriteLine($"PASS ({sw.Elapsed.TotalSeconds:0.0}s)")
            Catch ex As Exception
                Console.WriteLine($"FAIL ({sw.Elapsed.TotalSeconds:0.0}s)")
                Console.WriteLine($"      → {ex.Message}")
                Throw
            End Try
        End Sub

        ' ---- 1/2: normal stop, healthy consumer ----

        Private Sub Test_NormalStop()
            Dim t0 As Long = 0
            Dim sink As New GatedAudioSink(0)
            Dim engine = MakeEngine(sink, t0)
            Try
                Thread.Sleep(2000)
                Dim sw = Stopwatch.StartNew()
                engine.Stop(EndTicks(t0, 2.5))
                sw.Stop()
                TestRunner.Assert(engine.IsRunning = False, "engine stopped")
                TestRunner.Assert(sw.ElapsedMilliseconds < 15000,
                                  $"normal stop bounded (took {sw.ElapsedMilliseconds}ms)")
                TestRunner.Assert(sink.Writes > 0, "consumer received audio")
            Finally
                engine.Dispose()
            End Try
        End Sub

        ' ---- 3/5: stalled consumer — the E1 signature, bounded by the budget ----

        Private Sub Test_StalledConsumerBudget()
            Dim t0 As Long = 0
            ' 8s stall per Write ≈ the wedged-pipe call shape (real bound: 10s
            ' via PipeFeed.Feed). Run 10s so the tail at stop ≈ 10s of silence.
            Dim sink As New GatedAudioSink(8000)
            Dim engine = MakeEngine(sink, t0)
            Try
                Thread.Sleep(10000)
                SyncLock _logLines
                    _logLines.Clear()
                End SyncLock

                Dim sw = Stopwatch.StartNew()
                engine.Stop(EndTicks(t0, 10.5))
                sw.Stop()

                ' THE INVARIANT: Stop() eventually returns — bounded.
                TestRunner.Assert(sw.ElapsedMilliseconds < 45000,
                                  $"stop bounded (took {sw.ElapsedMilliseconds}ms — pre-fix this ran 80s+)")
                TestRunner.Assert(engine.IsRunning = False, "engine stopped (real, not fake)")
                ' The budget trip is loud evidence, not a silent truncation.
                TestRunner.Assert(HasLog("finalize budget"), "budget-trip log present")
            Finally
                engine.Dispose()
            End Try
        End Sub

        ' ---- 6: Dispose after a budget-tripped stop ----

        Private Sub Test_DisposeAfterFailedStop()
            Dim t0 As Long = 0
            Dim sink As New GatedAudioSink(8000)
            Dim engine = MakeEngine(sink, t0)
            Try
                Thread.Sleep(6000)
                Dim sw = Stopwatch.StartNew()
                engine.Stop(EndTicks(t0, 6.5))
                sw.Stop()
                TestRunner.Assert(sw.ElapsedMilliseconds < 45000, "stop bounded")

                engine.Dispose()
                engine.Dispose() ' idempotent — second call must not throw
                TestRunner.Assert(engine.IsRunning = False, "disposed engine not running")
            Catch ex As Exception
                TestRunner.Assert(False, "Dispose after failed stop threw: " & ex.Message)
            Finally
                engine.Dispose()
            End Try
        End Sub

        ' ---- 7: fresh engine after recovery ----

        Private Sub Test_StartAfterRecovery()
            Dim t0 As Long = 0
            Dim sink As New GatedAudioSink(0)
            Dim engine = MakeEngine(sink, t0)
            Try
                Thread.Sleep(1000)
                engine.Stop(EndTicks(t0, 1.2))
            Finally
                engine.Dispose()
            End Try

            ' Recovery = a fresh engine instance (the session constructs one
            ' per recording) must Start/Stop normally.
            Dim t1 As Long = 0
            Dim sink2 As New GatedAudioSink(0)
            Dim engine2 = MakeEngine(sink2, t1)
            Try
                Thread.Sleep(1000)
                Dim sw = Stopwatch.StartNew()
                engine2.Stop(EndTicks(t1, 1.5))
                sw.Stop()
                TestRunner.Assert(engine2.IsRunning = False, "recovered engine stopped")
                TestRunner.Assert(sw.ElapsedMilliseconds < 15000, "recovered stop bounded")
                TestRunner.Assert(sink2.Writes > 0, "recovered engine delivers audio")
            Finally
                engine2.Dispose()
            End Try
        End Sub

        ' ---- stress: 50× Start/Stop, mixed consumer health ----

        Private Sub Test_Stress50Mixed()
            Dim maxStopMs As Long = 0
            Dim stalled As Integer = 0
            For i As Integer = 1 To 50
                Dim t0 As Long = 0
                Dim mode As Integer = i Mod 3
                Dim stallMs As Integer = If(mode = 0, 0, If(mode = 1, 100, 800))
                If stallMs > 0 Then stalled += 1
                Dim sink As New GatedAudioSink(stallMs)
                Dim engine = MakeEngine(sink, t0)
                Try
                    Thread.Sleep(300)
                    Dim sw = Stopwatch.StartNew()
                    engine.Stop(EndTicks(t0, 0.4))
                    sw.Stop()
                    If sw.ElapsedMilliseconds > maxStopMs Then maxStopMs = sw.ElapsedMilliseconds
                    TestRunner.Assert(engine.IsRunning = False, $"iter {i}: not stopped")
                    TestRunner.Assert(sw.ElapsedMilliseconds < 40000,
                                      $"iter {i}: stop unbounded ({sw.ElapsedMilliseconds}ms)")
                Finally
                    engine.Dispose()
                End Try
            Next
            TestRunner.Assert(stalled >= 20, $"mixed variants exercised (stalled={stalled})")
            TestRunner.Assert(maxStopMs < 40000, $"max stop {maxStopMs}ms bounded across 50 iterations")
            Console.Write($"[max={maxStopMs}ms] ")
        End Sub

    End Module

End Namespace
