Option Strict On
Option Explicit On
Option Infer On

' LiveMuxSessionTests.vb — G1: dedicated coverage for the PRODUCTION mux.
'
' The CaptureSession live path (CaptureEngine.Recording) feeds every encoded
' frame and every audio packet through LiveMuxSession → one real ffmpeg over
' named pipes → fragmented MP4 → +faststart remux → final output. The old
' FFmpegTests exercised FFmpegPipelineBackend/MuxCoordinator (console-only);
' LiveMuxSession itself had no dedicated tests. This module pins the real
' production lifecycle:
'
'   A  normal stop — feed → drain → finalize → remux → valid output, zero drops
'   B  ffmpeg exit ≠ 0 — no silent success, error surfaced, cleanup complete
'   C  remux failure → salvage — output NOT falsely reported as saved
'   D  Dispose without Stop — ffmpeg killed, pipes disposed, re-Dispose safe
'   E  Stop during active writes — no exception escapes the feeder, deterministic terminal state
'   F  drain timeout (ffmpeg suspended via NtSuspendProcess — TEST-ONLY) — bounded shutdown
'      + the DroppedBytes ledger: written + dropped == accepted (no under/over-count)
'   H  Stop is single-terminal — a second Stop never re-finalizes or re-reports success
'   G  video-only session (sysRate=0) — no audio pipe, valid video-only output
'   J/K/L/N  process OWNERSHIP (C/5): OnProcessStarted hook fires for every
'      spawned ffmpeg (recording + remux), is fail-open on throw, and the
'      session's Stop/Dispose stay the explicit owners. The KILL_ON_JOB_CLOSE
'      handle-close contract itself is proven by JOB-1 in Engine.Concurrency.Tests.
'
' All tests run the REAL LiveMuxSession against the REAL bundled ffmpeg.
' Named pipes are Windows-only → the whole module skips elsewhere.

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.FFmpegTests

    Friend Module LiveMuxSessionTests

        Private _ffmpeg As String = ""
        Private _sandbox As String = ""
        Private _videoH264 As Byte() = Nothing
        Private _audioPcm As Byte() = Nothing
        Private ReadOnly _logSync As New Object()
        Private _logLines As List(Of String) = Nothing

        ' ───────────────────────────────────────────────────────────────
        ' Test-only suspension P/Invoke (deterministic consumer stall for
        ' the drain-timeout contract). Never referenced by production code.
        ' ───────────────────────────────────────────────────────────────
        Private Const PROCESS_SUSPEND_RESUME As Integer = &H800

        <DllImport("ntdll.dll")>
        Private Function NtSuspendProcess(hProcess As IntPtr) As Integer
        End Function

        <DllImport("ntdll.dll")>
        Private Function NtResumeProcess(hProcess As IntPtr) As Integer
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Function OpenProcess(dwDesiredAccess As UInteger, bInheritHandle As Boolean, dwProcessId As Integer) As IntPtr
        End Function

        <DllImport("kernel32.dll")>
        Private Function CloseHandle(hObject As IntPtr) As Boolean
        End Function

        ' ───────────────────────────────────────────────────────────────

        Friend Sub RunAll()
            If Not RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
                Console.WriteLine("(LiveMuxSession tests skipped — named pipes are Windows-only)")
                Return
            End If

            _ffmpeg = FindRepoFFmpeg()
            If String.IsNullOrEmpty(_ffmpeg) Then
                Console.WriteLine("(LiveMuxSession tests skipped — bundled ffmpeg.exe not found)")
                Return
            End If

            If Not SetupSandbox() Then
                Console.WriteLine("(LiveMuxSession tests skipped — media generation failed)")
                Return
            End If

            Dim baseline As Integer = FfmpegCount()
            Console.WriteLine($"      [LM] sandbox={_sandbox} baseline ffmpeg={baseline}")

            RunTest("LM-A: normal stop — drain → finalize → remux → valid output, zero drops", AddressOf Test_NormalStop)
            RunTest("LM-B: ffmpeg exit ≠ 0 — no silent success, error surfaced, cleanup", AddressOf Test_FFmpegExitNonZero)
            RunTest("LM-C: remux failure → salvage keeps fragmented file, NOT reported saved", AddressOf Test_RemuxFailureSalvage)
            RunTest("LM-D: Dispose without Stop — ffmpeg killed, re-Dispose safe, no orphan", AddressOf Test_DisposeWithoutStop)
            RunTest("LM-E: Stop during active writes — feeder never throws, deterministic terminal", AddressOf Test_StopDuringActiveWrites)
            RunTest("LM-F: drain timeout bounded + DroppedBytes ledger (written+dropped == accepted)", AddressOf Test_DrainTimeoutLedger)
            RunTest("LM-SEP: SeparateTrack → THREE streams (sys 2ch + mic 1ch)", AddressOf Test_SeparateTrackThreeStreams)
            RunTest("LM-H: Stop is single-terminal — second Stop never re-finalizes or re-reports success", AddressOf Test_StopTwiceSingleTerminal)
            RunTest("LM-G: video-only session (sysRate=0) — no audio pipe, valid video-only output", AddressOf Test_VideoOnlySession)
            RunTest("LM-J: spawn ownership — hook fires once with live ffmpeg; Stop → exited", AddressOf Test_SpawnOwnershipHook)
            RunTest("LM-K: ffmpeg exits right after spawn — hook still fires, clean failure, no orphan", AddressOf Test_HookOnImmediateExit)
            RunTest("LM-L: throwing ownership hook is fail-open — lifecycle intact", AddressOf Test_HookThrowIsFailOpen)
            RunTest("LM-N: Dispose without Stop kills the hooked ffmpeg", AddressOf Test_DisposeKillsHookedProcess)

            Dim leftovers As Integer = FfmpegCount() - baseline
            If leftovers > 0 Then
                RunTest("LM-FINAL: no orphan ffmpeg left by the suite", Sub()
                                                                           Assert(False, leftovers & " orphan ffmpeg process(es) left behind")
                                                                       End Sub)
            End If
        End Sub

        ' ───────────────────────────────────────────────────────────────
        ' Plumbing
        ' ───────────────────────────────────────────────────────────────

        Private Function FindRepoFFmpeg() As String
            Dim dir As New IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
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

        Private Function FfmpegCount() As Integer
            Dim procs As Process() = Process.GetProcessesByName("ffmpeg")
            Dim n As Integer = procs.Length
            For Each p As Process In procs
                Try : p.Dispose() : Catch : End Try
            Next
            Return n
        End Function

        Private Function WaitFfmpegAtMost(max As Integer, budgetMs As Integer) As Boolean
            Dim sw As Stopwatch = Stopwatch.StartNew()
            While sw.ElapsedMilliseconds < budgetMs
                If FfmpegCount() <= max Then Return True
                Thread.Sleep(100)
            End While
            Return FfmpegCount() <= max
        End Function

        ''' <summary>PID liveness by re-enumeration — safe across mux.Dispose()
        ''' (which disposes the Process object; its HasExited must not be read
        ''' afterwards). PID reuse inside a test window is negligible.</summary>
        Private Function PidAlive(pid As Integer) As Boolean
            Try
                Using p As Process = Process.GetProcessById(pid)
                    Return Not p.HasExited
                End Using
            Catch ex As ArgumentException
                Return False
            End Try
        End Function

        Private Sub CollectLog(msg As String)
            SyncLock _logSync
                _logLines?.Add(msg)
            End SyncLock
        End Sub

        Private Function LogHas(fragment As String) As Boolean
            SyncLock _logSync
                If _logLines Is Nothing Then Return False
                For Each ln In _logLines
                    If ln IsNot Nothing AndAlso ln.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
                Next
            End SyncLock
            Return False
        End Function

        ''' <summary>Generate the shared media ONCE: raw H.264 elementary stream
        ''' (SPS/PPS + frames) and s16le stereo PCM, via the bundled ffmpeg.</summary>
        Private Function SetupSandbox() As Boolean
            _sandbox = IO.Path.Combine(IO.Path.GetTempPath(),
                                       "lmux-tests-" & DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            IO.Directory.CreateDirectory(_sandbox)

            Dim videoPath As String = IO.Path.Combine(_sandbox, "src_video.h264")
            Dim audioPath As String = IO.Path.Combine(_sandbox, "src_audio.pcm")

            If Not RunGenerator(
                "-y -f lavfi -i testsrc=duration=2:size=320x240:rate=30 " &
                "-c:v libx264 -preset ultrafast -tune zerolatency -g 30 -f h264 """ & videoPath & """") Then Return False
            If Not RunGenerator(
                "-y -f lavfi -i sine=frequency=440:duration=5 -ar 48000 -ac 2 -f s16le """ & audioPath & """") Then Return False

            _videoH264 = IO.File.ReadAllBytes(videoPath)
            _audioPcm = IO.File.ReadAllBytes(audioPath)
            Return _videoH264.Length > 0 AndAlso _audioPcm.Length > 0
        End Function

        Private Function RunGenerator(args As String) As Boolean
            Dim psi As New ProcessStartInfo With {
                .FileName = _ffmpeg,
                .Arguments = args,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            Using p As Process = Process.Start(psi)
                Dim errTask = p.StandardError.ReadToEndAsync()
                If Not p.WaitForExit(30000) Then
                    Try : p.Kill() : Catch : End Try
                    Return False
                End If
                errTask.Wait(2000)
                Return p.ExitCode = 0
            End Using
        End Function

        Private Function MakeMux(outPath As String, Optional onProcessStarted As Action(Of Process) = Nothing) As LiveMuxSession
            SyncLock _logSync
                _logLines = New List(Of String)()
            End SyncLock
            ' video-only fps must match the generated stream cadence loosely —
            ' the -framerate declaration only stamps PTS; the raw H.264 carries
            ' its own access units.
            Return New LiveMuxSession(_ffmpeg, outPath, 30, 48000, 2, 0, 0, False, 1.0F, 1.0F,
                                      AddressOf CollectLog, onProcessStarted)
        End Function

        ''' <summary>Feed the whole generated H.264 stream in slices
        ''' (FeedVideo always reads from offset 0 — slice here).</summary>
        Private Function FeedVideoFile(mux As LiveMuxSession) As Long
            Const chunk As Integer = 32 * 1024
            Dim data As Byte() = _videoH264
            Dim off As Integer = 0
            While off < data.Length
                Dim n As Integer = Math.Min(chunk, data.Length - off)
                Dim slice(n - 1) As Byte
                Buffer.BlockCopy(data, off, slice, 0, n)
                mux.FeedVideo(slice, n)
                off += n
            End While
            Return data.Length
        End Function

        Private Function FeedAudioSeconds(mux As LiveMuxSession, seconds As Double) As Long
            Dim bytes As Long = CLng(48000L * 2L * 2L * seconds)   ' 48k * 2ch * 2B
            If bytes > _audioPcm.Length Then bytes = _audioPcm.Length
            mux.FeedSystemAudioSegment(_audioPcm, 0, CInt(bytes))
            Return bytes
        End Function

        ''' <summary>Probe a media file's streams via the bundled ffmpeg
        ''' (-i to null output) and return the stderr text.</summary>
        Private Function ProbeStreams(path As String) As String
            Dim psi As New ProcessStartInfo With {
                .FileName = _ffmpeg,
                .Arguments = "-hide_banner -i """ & path & """",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True
            }
            Using p As Process = Process.Start(psi)
                Dim errTask = p.StandardError.ReadToEndAsync()
                p.WaitForExit(10000)
                errTask.Wait(2000)
                Return If(errTask.Status = TaskStatus.RanToCompletion, errTask.Result, "")
            End Using
        End Function

        ''' <summary>G ledger: every byte accepted by Feed must end up either
        ''' written to the pipe or accounted as dropped — no under/over-count.
        ''' (BeginTimelines(0,0) → no pad/discard terms in this suite.)</summary>
        Private Sub AssertLedger(res As LiveMuxResult, acceptedVideo As Long, acceptedAudio As Long, label As String)
            ' DroppedBytes aggregates all pipes — the ledger is global:
            ' every accepted byte ends written to a pipe or accounted dropped.
            Assert(res.VideoBytesFed + res.SystemBytesFed + res.DroppedBytes = acceptedVideo + acceptedAudio,
                   label & $": ledger video={res.VideoBytesFed:N0} + audio={res.SystemBytesFed:N0} " &
                           $"+ dropped={res.DroppedBytes:N0} <> accepted {acceptedVideo + acceptedAudio:N0}")
        End Sub


        ''' <summary>W3 pin: SeparateTrack=True with a live mic input must produce'''
        ''' THREE output streams — video + 2ch system AAC + 1ch mic AAC.'''
        Private Sub Test_SeparateTrackThreeStreams()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_sep.mp4")
            Dim mux As New LiveMuxSession(_ffmpeg, outPath, 30, 48000, 2, 48000, 1, True, 1.0F, 1.0F,
                                          AddressOf CollectLog)
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)

                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                ' mic: 1ch sine, 1s — 48k frames x 2B
                Dim mic As Byte() = New Byte(48000 * 2 - 1) {}
                For i As Integer = 0 To 47999
                    Dim v As Short = CShort(Math.Sin(i * 900.0 * 2.0 * Math.PI / 48000.0) * 12000.0)
                    mic(i * 2) = CByte(v And &HFF)
                    mic(i * 2 + 1) = CByte((v >> 8) And &HFF)
                Next
                mux.FeedMicAudioSegment(mic, 0, mic.Length)
                Thread.Sleep(2000)

                ' W3 pin: snapshot the frag BEFORE Stop — proves whether the
                ' mic stream existed in the muxed fragments or was lost at
                ' finalize/remux.
                Dim fragSnap As String = outPath & ".snap.mp4"
                Try
                    If IO.File.Exists(outPath & ".frag.mp4") Then
                        IO.File.Copy(outPath & ".frag.mp4", fragSnap, True)
                    End If
                Catch
                End Try

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.Succeeded, "Succeeded=False: " & res.ErrorMessage)
                Assert(res.MicBytesFed > 0, "no mic bytes reached the mux")

                Dim probe As String = ProbeStreams(outPath)
                Dim audioCount As Integer = 0
                Dim idx As Integer = probe.IndexOf("Audio:", StringComparison.Ordinal)
                While idx >= 0
                    audioCount += 1
                    idx = probe.IndexOf("Audio:", idx + 1)
                End While
                If IO.File.Exists(fragSnap) Then
                    Dim fp As String = ProbeStreams(fragSnap)
                    Dim fa As Integer = 0
                    Dim ix As Integer = fp.IndexOf("Audio:", StringComparison.Ordinal)
                    While ix >= 0
                        fa += 1
                        ix = fp.IndexOf("Audio:", ix + 1)
                    End While
                    Console.WriteLine($"      [LM-SEP] frag snapshot audio streams = {fa}")
                Else
                    Console.WriteLine("      [LM-SEP] frag snapshot MISSING (frag never created)")
                End If
                ' KNOWN UPSTREAM LIMITATION (W3 pin, FFmpeg trac #1663): with
                ' THREE named-pipe inputs the LAST audio input is demuxed but
                ' its packets never reach the muxer — the output loses the mic
                ' stream while args/feeds/accounting are all correct. This test
                ' stays RED as the guard: it goes green the day the ffmpeg in
                ' use (or a pipe-architecture rework) fixes the drop.
                Assert(audioCount = 2, $"expected 2 audio streams, got {audioCount} " &
                                       $"[FFmpeg trac #1663: third named-pipe input dropped by the muxer] : {probe}" &
                                       Environment.NewLine & "STDERR: " & res.StderrTail &
                                       Environment.NewLine & "MICFED: " & res.MicBytesFed.ToString("N0"))
            Finally
                mux.Dispose()
            End Try
        End Sub

        ' ───────────────────────────────────────────────────────────────
        ' Matrix
        ' ───────────────────────────────────────────────────────────────

        ''' <summary>A: the production happy path — feed video + audio, stop,
        ''' drain, finalize, remux, verify the file really contains streams.</summary>
        Private Sub Test_NormalStop()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_a.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)

                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                Thread.Sleep(2000)   ' let ffmpeg connect + consume before EOF

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.Succeeded, "Succeeded=False: " & res.ErrorMessage)
                Assert(res.FFmpegExitCode = 0, "exit code " & res.FFmpegExitCode)
                Assert(res.UsedFaststartRemux, "faststart remux should have run")
                Assert(res.VideoBytesFed > 0, "no video bytes reached the mux")
                Assert(res.SystemBytesFed > 0, "no audio bytes reached the mux")
                Assert(res.DroppedBytes = 0, $"unexpected drops on the happy path: {res.DroppedBytes:N0}")

                Assert(IO.File.Exists(outPath), "final output missing")
                Dim probe As String = ProbeStreams(outPath)
                Assert(probe.Contains("Video:"), "no video stream in output")
                Assert(probe.Contains("Audio:"), "no audio stream in output")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after normal stop")
        End Sub

        ''' <summary>B: ffmpeg cannot open the output ('|' is illegal on Windows)
        ''' → exits non-zero → Stop must NOT report success and must surface
        ''' the failure; broken-pipe drops keep the ledger balanced.</summary>
        Private Sub Test_FFmpegExitNonZero()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_b|illegal.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Dim acceptedVideo As Long = 0
            Dim acceptedAudio As Long = 0
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)
                acceptedVideo += FeedVideoFile(mux)
                acceptedAudio += FeedAudioSeconds(mux, 0.5)
                Thread.Sleep(1500)

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(Not res.Succeeded, "FAILED OUTPUT MUST NOT REPORT SUCCESS")
                Assert(res.FFmpegExitCode <> 0, "expected non-zero ffmpeg exit code")
                Assert(res.ErrorMessage.Length > 0, "error message should carry the failure")

                AssertLedger(res, acceptedVideo, acceptedAudio, "LM-B")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after failed record")
        End Sub

        ''' <summary>C: the record phase succeeds but the +faststart remux fails
        ''' (final path is a directory) → the salvage must keep the fragmented
        ''' file and the result must NOT claim the output was saved.</summary>
        Private Sub Test_RemuxFailureSalvage()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_c.mp4")
            IO.Directory.CreateDirectory(outPath)   ' remux/salvage target is a DIRECTORY
            Dim fragPath As String = outPath & ".frag.mp4"
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)
                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                Thread.Sleep(2000)

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.FFmpegExitCode = 0, "record phase should succeed, got " & res.ErrorMessage)
                Assert(Not res.UsedFaststartRemux, "remux must have failed (target is a directory)")
                Assert(Not res.Succeeded, "SALVAGE FAILED — OUTPUT MUST NOT BE REPORTED AS SAVED")
                Assert(res.ErrorMessage.Contains("faststart-remux failed"),
                       "error should name the remux failure: " & res.ErrorMessage)
                Assert(IO.File.Exists(fragPath), "fragmented file must be kept on salvage failure")
            Finally
                mux.Dispose()
                Try
                    If IO.Directory.Exists(outPath) Then IO.Directory.Delete(outPath, True)
                    If IO.File.Exists(fragPath) Then IO.File.Delete(fragPath)
                Catch
                End Try
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after salvage test")
        End Sub

        ''' <summary>D: Dispose without Stop — the wedge-orphan guard must kill
        ''' ffmpeg; a second Dispose is a safe no-op.</summary>
        Private Sub Test_DisposeWithoutStop()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_d.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Assert(mux.Start(), "Start returned False")
            mux.BeginTimelines(0.0, 0.0)
            FeedVideoFile(mux)
            FeedAudioSeconds(mux, 0.5)
            Thread.Sleep(500)

            mux.Dispose()                       ' no Stop — orphan guard must fire
            Assert(WaitFfmpegAtMost(0, 5000), "ffmpeg survived Dispose without Stop")
            mux.Dispose()                       ' idempotent — must not throw
        End Sub

        ''' <summary>E: a feeder thread writes while Stop drains — no exception
        ''' may escape the feeder, and the terminal state stays deterministic.</summary>
        Private Sub Test_StopDuringActiveWrites()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_e.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Dim feederErrors As New List(Of String)()
            Dim stopFeeding As Boolean = False

            Assert(mux.Start(), "Start returned False")
            mux.BeginTimelines(0.0, 0.0)

            Dim feeder As New Thread(
                Sub()
                    Try
                        Dim i As Integer = 0
                        Dim data As Byte() = _videoH264
                        While Not stopFeeding
                            Dim off As Integer = (i * 16384) Mod Math.Max(1, data.Length - 16384)
                            Dim slice(16383) As Byte
                            Buffer.BlockCopy(data, off, slice, 0, 16384)
                            mux.FeedVideo(slice, 16384)
                            Dim aOff As Integer = (i * 9600) Mod Math.Max(1, _audioPcm.Length - 9600)
                            mux.FeedSystemAudioSegment(_audioPcm, aOff, 9600)
                            i += 1
                            Thread.Sleep(5)
                        End While
                    Catch ex As Exception
                        SyncLock feederErrors
                            feederErrors.Add(ex.ToString())
                        End SyncLock
                    End Try
                End Sub) With {.IsBackground = True}
            feeder.Start()
            Thread.Sleep(500)

            Dim res As LiveMuxResult = mux.Stop(30000)
            stopFeeding = True
            Assert(feeder.Join(5000), "feeder thread did not exit")
            SyncLock feederErrors
                If feederErrors.Count > 0 Then
                    Assert(False, "feeder threw while Stop ran: " & feederErrors(0))
                End If
            End SyncLock

            Assert(res.FFmpegExitCode = 0, "terminal exit code " & res.FFmpegExitCode & ": " & res.ErrorMessage)
            Assert(res.Succeeded, "active-writes stop should still finalize: " & res.ErrorMessage)
            Assert(IO.File.Exists(outPath), "output missing after active-writes stop")
            mux.Dispose()
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after active-writes stop")
        End Sub

        ''' <summary>F: suspend the ffmpeg consumer (deterministic stall) →
        ''' audio overflow + drain timeout must stay bounded and every byte
        ''' must land in written or dropped — the G ledger.</summary>
        Private Sub Test_DrainTimeoutLedger()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_f.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Dim procHandle As IntPtr = IntPtr.Zero
            Dim acceptedVideo As Long = 0
            Dim acceptedAudio As Long = 0
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)
                acceptedVideo += FeedVideoFile(mux)
                acceptedAudio += FeedAudioSeconds(mux, 0.3)
                Thread.Sleep(1500)

                ' Suspend the ONLY ffmpeg (RunAll guarantees a clean baseline).
                Dim procs As Process() = Process.GetProcessesByName("ffmpeg")
                Assert(procs.Length = 1, "expected exactly one ffmpeg, found " & procs.Length)
                Dim pid As Integer = procs(0).Id
                For Each p As Process In procs : Try : p.Dispose() : Catch : End Try : Next
                procHandle = OpenProcess(PROCESS_SUSPEND_RESUME, False, pid)
                Assert(procHandle <> IntPtr.Zero, "OpenProcess failed")
                Assert(NtSuspendProcess(procHandle) = 0, "NtSuspendProcess failed")

                ' Overflow the 8 MB audio cap while the consumer is frozen.
                For i As Integer = 1 To 140          ' 140 × 65536B ≈ 9.2 MB
                    mux.FeedSystemAudioSegment(_audioPcm, 0, 65536)
                    acceptedAudio += 65536
                    Thread.Sleep(2)
                Next

                Dim sw As Stopwatch = Stopwatch.StartNew()
                Dim res As LiveMuxResult = mux.Stop(3000)    ' drain budget = 1000ms
                Dim elapsedMs As Long = sw.ElapsedMilliseconds

                Assert(elapsedMs < 20000, $"stop was not bounded: {elapsedMs}ms")
                Assert(res.DroppedBytes > 0, "overflow/residual bytes must be accounted as dropped")
                AssertLedger(res, acceptedVideo, acceptedAudio, "LM-F")
                Assert(LogHas("drain timeout") OrElse LogHas("dropped") OrElse LogHas("counted as dropped"),
                       "expected drain/drop evidence in the mux log")
            Finally
                If procHandle <> IntPtr.Zero Then
                    Try : NtResumeProcess(procHandle) : Catch : End Try
                    CloseHandle(procHandle)
                End If
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after drain-timeout test")
        End Sub

        ''' <summary>H: LiveMuxSession is single-terminal — the production
        ''' caller (CaptureSession) Stops exactly once; a second Stop must not
        ''' re-finalize, must not re-report success, and must leave the first
        ''' result's output untouched.</summary>
        Private Sub Test_StopTwiceSingleTerminal()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_h.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath)
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)
                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                Thread.Sleep(2000)

                Dim res1 As LiveMuxResult = mux.Stop(30000)
                Assert(res1.Succeeded, "first stop should succeed: " & res1.ErrorMessage)
                Dim sizeAtFirstStop As Long = If(IO.File.Exists(outPath), New IO.FileInfo(outPath).Length, -1)

                Dim res2 As LiveMuxResult = mux.Stop(30000)
                Assert(Not res2.Succeeded, "second Stop must not re-report success")
                Assert(res2.ErrorMessage.Length > 0, "second Stop should explain itself")
                Assert(IO.File.Exists(outPath), "first finalize's output must survive a second Stop")
                Dim sizeAfter As Long = New IO.FileInfo(outPath).Length
                Assert(sizeAfter = sizeAtFirstStop, "output file was modified by the second Stop")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after double-stop test")
        End Sub

        ''' <summary>G: video-only session — the "17:19" contract. When the
        ''' caller passes systemSampleRate=0 (audio disabled), LiveMux must
        ''' create NO audio pipe and NO audio input: a created-but-unfed pipe
        ''' used to kill ffmpeg's input open (exit -22, all video dropped, no
        ''' output file at all). The rest of this suite always constructs the
        ''' mux with 48000/2 audio, so this path had zero coverage while
        ''' "record with audio off" (silent desktop) is a first-class user
        ''' scenario.</summary>
        Private Sub Test_VideoOnlySession()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_g.mp4")
            ' rate=0 on BOTH tracks is the explicit audio-disabled signal
            ' (ctor: rate<=0 → no sp_a/sp_m pipe, no audio args, audio EOF
            ' handling in Stop never runs).
            Dim mux As New LiveMuxSession(_ffmpeg, outPath, 30, 0, 0, 0, 0, False, 1.0F, 1.0F,
                                          AddressOf CollectLog)
            Try
                Assert(mux.Start(), "Start returned False")
                mux.BeginTimelines(0.0, 0.0)

                Dim fed As Long = FeedVideoFile(mux)
                Thread.Sleep(2000)   ' let ffmpeg connect + consume before EOF

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.Succeeded, "Succeeded=False: " & res.ErrorMessage)
                Assert(res.FFmpegExitCode = 0, "exit code " & res.FFmpegExitCode)
                Assert(res.UsedFaststartRemux, "faststart remux should have run")
                Assert(res.VideoBytesFed > 0, "no video bytes reached the mux")
                Assert(res.SystemBytesFed = 0, "video-only session must not report audio bytes")
                Assert(res.DroppedBytes = 0, $"unexpected drops on the video-only path: {res.DroppedBytes:N0}")
                AssertLedger(res, fed, 0, "LM-G")

                Assert(IO.File.Exists(outPath), "final output missing")
                Dim probe As String = ProbeStreams(outPath)
                Assert(probe.Contains("Video:"), "no video stream in output")
                Assert(Not probe.Contains("Audio:"), "video-only session must not produce an audio stream")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after video-only test")
        End Sub

        ' ───────────────────────────────────────────────────────────────
        ' Ownership (C/5): every ffmpeg LiveMuxSession spawns must reach the
        ' host's OnProcessStarted hook → JobObjectGuard (KILL_ON_JOB_CLOSE),
        ' and the session's own Stop/Dispose remain the explicit owners.
        ' ───────────────────────────────────────────────────────────────

        ''' <summary>J: successful spawn ownership — the hook fires for EVERY
        ''' spawned ffmpeg (recording + faststart remux), receives a live
        ''' process each time, and the normal Stop path still terminates the
        ''' recording child. Process identity is snapshotted INSIDE the hook:
        ''' the remux child's Process object is disposed by its Using block
        ''' before Stop returns, so post-hoc member access is illegal. (The
        ''' KILL_ON_JOB_CLOSE close-the-handle contract itself is proven by
        ''' JOB-1 in Engine.Concurrency.Tests.)</summary>
        Private Sub Test_SpawnOwnershipHook()
            Dim hookCount As Integer = 0
            Dim recordingPid As Integer = -1
            Dim recordingName As String = ""
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_j.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath,
                Sub(p)
                    Dim n As Integer = Interlocked.Increment(hookCount)
                    If n = 1 Then
                        recordingPid = p.Id
                        recordingName = p.ProcessName
                    End If
                End Sub)
            Try
                Assert(mux.Start(), "Start returned False")
                Assert(hookCount = 1, "ownership hook must fire once for the recording ffmpeg, got " & hookCount)
                Assert(recordingPid > 0, "hook received no process id")
                Assert(recordingName = "ffmpeg", "hooked process is not ffmpeg: " & recordingName)
                Assert(PidAlive(recordingPid), "hooked ffmpeg not alive right after spawn")

                mux.BeginTimelines(0.0, 0.0)
                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                Thread.Sleep(2000)

                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.Succeeded, "Succeeded=False: " & res.ErrorMessage)
                Assert(hookCount = 2, "the remux ffmpeg must also reach the ownership hook, got " & hookCount)
                Assert(Not PidAlive(recordingPid), "hooked recording ffmpeg still alive after Stop")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after ownership test")
        End Sub

        ''' <summary>K: ffmpeg exits right after spawn (illegal output path) —
        ''' the ownership hook still fires (assignment happens the moment the
        ''' process exists), the mux fails loudly, and Dispose stays safe.</summary>
        Private Sub Test_HookOnImmediateExit()
            Dim hookCount As Integer = 0
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_k|illegal.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath, Sub(p) Interlocked.Increment(hookCount))
            Try
                Assert(mux.Start(), "Start returned False")
                Assert(hookCount = 1, "hook must fire even when ffmpeg dies immediately, got " & hookCount)
                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(Not res.Succeeded, "illegal output path must not report success")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after immediate-exit test")
        End Sub

        ''' <summary>L: a THROWING ownership hook must not poison Start or the
        ''' lifecycle (fail-open contract — a failed job assignment leaves the
        ''' process self-owned, exactly the pre-wiring behavior).</summary>
        Private Sub Test_HookThrowIsFailOpen()
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_l.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath,
                Sub(p) Throw New InvalidOperationException("injected hook failure"))
            Try
                Assert(mux.Start(), "Start must survive a throwing ownership hook")
                mux.BeginTimelines(0.0, 0.0)
                FeedVideoFile(mux)
                FeedAudioSeconds(mux, 1.0)
                Thread.Sleep(2000)
                Dim res As LiveMuxResult = mux.Stop(30000)
                Assert(res.Succeeded, "Succeeded=False: " & res.ErrorMessage)
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after throwing-hook test")
        End Sub

        ''' <summary>N: Dispose without Stop kills the hooked ffmpeg — the
        ''' session is the explicit cleanup owner within its own lifetime;
        ''' liveness is checked by PID re-enumeration because Dispose disposes
        ''' the Process object itself.</summary>
        Private Sub Test_DisposeKillsHookedProcess()
            Dim captured As Process = Nothing
            Dim outPath As String = IO.Path.Combine(_sandbox, "lm_n.mp4")
            Dim mux As LiveMuxSession = MakeMux(outPath, Sub(p) captured = p)
            Try
                Assert(mux.Start(), "Start returned False")
                Assert(captured IsNot Nothing, "hook received no process")
                Dim pid As Integer = captured.Id
                Thread.Sleep(500)   ' let ffmpeg connect to the pipes
                mux.Dispose()
                Dim dead As Boolean = False
                Dim sw As Stopwatch = Stopwatch.StartNew()
                While sw.ElapsedMilliseconds < 5000
                    If Not PidAlive(pid) Then
                        dead = True
                        Exit While
                    End If
                    Thread.Sleep(100)
                End While
                Assert(dead, "hooked ffmpeg survived Dispose (orphan within the owner's own lifetime)")
            Finally
                mux.Dispose()
            End Try
            Assert(WaitFfmpegAtMost(0, 5000), "orphan ffmpeg after dispose-kill test")
        End Sub

    End Module

End Namespace
