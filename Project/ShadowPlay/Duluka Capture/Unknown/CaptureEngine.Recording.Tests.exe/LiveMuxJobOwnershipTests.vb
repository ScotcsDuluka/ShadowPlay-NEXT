Option Strict On
Option Explicit On
Option Infer On

' LiveMuxJobOwnershipTests.vb — F-07 regression coverage.
'
' F-07: LiveMuxSession spawned the MAIN recording ffmpeg (and the
' faststart-remux ffmpeg) WITHOUT the process-ownership hook — the only
' processes in the whole pipeline with no JobObjectGuard assignment — so a
' host crash left them encoding the desktop forever. The fix threads the
' host's OnProcessStarted hook (SessionConfig → CaptureSession →
' LiveMuxSession) into every spawned ffmpeg, mirroring MuxCoordinator.
'
' These tests drive the REAL LiveMuxSession + REAL ffmpeg:
'   1. Start() → the hook fires with the live main ffmpeg pid.
'   2. Dispose() without Stop() → the spawned ffmpeg is killed (no orphan).
'   3. Successful session → BOTH ffmpeg processes (main + remux) went
'      through the hook.
' If no ffmpeg binary is found the suite reports SKIP — honest absence,
' never a fabricated pass.

Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports CaptureEngine.FFmpegBackend
Imports CaptureEngine.Recording.Tests

Namespace CaptureEngine.Recording.Tests

    Friend Module LiveMuxJobOwnershipTests

    Private _ffmpegExe As String = Nothing
    Private ReadOnly _hookLock As New Object()
    Private _hookedPids As New List(Of Integer)()

    Public Sub RunAll()
        Console.WriteLine()
        Console.WriteLine("── F-07 LiveMux process ownership (real ffmpeg) ──")

        _ffmpegExe = FindFfmpeg()
        If _ffmpegExe Is Nothing Then
            TestRunner.RunSkip("F07: Start routes the spawned ffmpeg through the ownership hook",
                               "no ffmpeg.exe found — hardware/tooling absence, not a pass")
            TestRunner.RunSkip("F07: Dispose without Stop kills the spawned ffmpeg (no orphan)",
                               "no ffmpeg.exe found — hardware/tooling absence, not a pass")
            TestRunner.RunSkip("F07: successful session job-assigns BOTH main and remux ffmpeg",
                               "no ffmpeg.exe found — hardware/tooling absence, not a pass")
            Return
        End If

        TestRunner.RunTest("F07: Start routes the spawned ffmpeg through the ownership hook",
                           AddressOf Test_StartHook)
        TestRunner.RunTest("F07: Dispose without Stop kills the spawned ffmpeg (no orphan)",
                           AddressOf Test_DisposeKill)
        TestRunner.RunTest("F07: successful session job-assigns BOTH main and remux ffmpeg",
                           AddressOf Test_RemuxHook)
    End Sub

    ' ── helpers ─────────────────────────────────────────────────────

    Private Function FindFfmpeg() As String
        Dim dir As New DirectoryInfo(AppContext.BaseDirectory)
        For depth As Integer = 0 To 10
            If dir Is Nothing Then Exit For
            Dim candidates As String() = {
                Path.Combine(dir.FullName, "Overlay", "API-Core", "ffmpeg.exe"),
                Path.Combine(dir.FullName, "Overlay", "bin", "Release", "net10.0-windows10.0.26100.0", "FFmpeg", "ffmpeg.exe"),
                Path.Combine(dir.FullName, "ffmpeg.exe")
            }
            For Each c In candidates
                If File.Exists(c) Then Return c
            Next
            dir = dir.Parent
        Next
        Return Nothing
    End Function

    Private Sub RecordHook(p As Process)
        If p Is Nothing Then Return
        SyncLock _hookLock
            _hookedPids.Add(p.Id)
        End SyncLock
    End Sub

    Private Function HookedPidsSnapshot() As List(Of Integer)
        SyncLock _hookLock
            Return New List(Of Integer)(_hookedPids)
        End SyncLock
    End Function

    Private Function TempOutput(name As String) As String
        Return Path.Combine(Path.GetTempPath(), "F07_" & Guid.NewGuid().ToString("N").Substring(0, 8) & "_" & name)
    End Function

    ''' <summary>Video-only LiveMuxSession (sys/mic rate 0 → no audio pipes)
    ''' with the ownership hook wired.</summary>
    Private Function CreateSession(outputPath As String) As LiveMuxSession
        Return New LiveMuxSession(_ffmpegExe, outputPath, 30,
                                  0, 0, 0, 0, False, 1.0F, 1.0F,
                                  Nothing, AddressOf RecordHook)
    End Function

    ''' <summary>Generate a tiny valid H.264 elementary stream (annexb) via
    ''' the sandbox ffmpeg — the live mux's input-0 format.</summary>
    Private Function GenerateH264(target As String) As Boolean
        Dim psi As New ProcessStartInfo With {
            .FileName = _ffmpegExe,
            .Arguments = $"-y -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=30:duration=3 -c:v libx264 -f h264 ""{target}""",
            .UseShellExecute = False,
            .CreateNoWindow = True
        }
        Using p As Process = Process.Start(psi)
            If Not p.WaitForExit(20000) Then
                Try : p.Kill() : Catch : End Try
                Return False
            End If
            Return p.ExitCode = 0 AndAlso File.Exists(target) AndAlso New FileInfo(target).Length > 0
        End Using
    End Function

    Private Function WaitExited(p As Process, timeoutMs As Integer) As Boolean
        Dim deadline As Long = DateTime.UtcNow.Ticks + timeoutMs * 10000L
        While DateTime.UtcNow.Ticks < deadline
            Try
                If p.HasExited Then Return True
            Catch
                Return True   ' process object dead → certainly exited
            End Try
            Thread.Sleep(50)
        End While
        Return False
    End Function

    ' ── tests ───────────────────────────────────────────────────────

    Private Sub Test_StartHook()
        SyncLock _hookLock : _hookedPids.Clear() : End SyncLock
        Dim outPath As String = TempOutput("hook.mp4")
        Using mux = CreateSession(outPath)
            TestRunner.Assert(mux.Start(), "LiveMux.Start succeeded (real ffmpeg)")

            Dim hooked As List(Of Integer) = HookedPidsSnapshot()
            TestRunner.Assert(hooked.Count = 1, $"hook fired exactly once on Start (got {hooked.Count})")

            Dim proc As Process = Process.GetProcessById(hooked(0))
            TestRunner.Assert(Not proc.HasExited, "hooked ffmpeg is alive right after Start")

            mux.[Stop](8000)
            TestRunner.Assert(WaitExited(proc, 5000), "main ffmpeg exited after Stop")
        End Using
        Try : File.Delete(outPath) : Catch : End Try
        Try : File.Delete(outPath & ".frag.mp4") : Catch : End Try
    End Sub

    Private Sub Test_DisposeKill()
        SyncLock _hookLock : _hookedPids.Clear() : End SyncLock
        Dim outPath As String = TempOutput("dispose.mp4")
        Dim mux = CreateSession(outPath)
        TestRunner.Assert(mux.Start(), "LiveMux.Start succeeded (real ffmpeg)")
        Dim hooked As List(Of Integer) = HookedPidsSnapshot()
        TestRunner.Assert(hooked.Count = 1, "hook fired on Start")
        Dim proc As Process = Process.GetProcessById(hooked(0))

        ' Abandon semantics: Dispose WITHOUT Stop — the ffmpeg must not
        ' survive as an orphan.
        mux.Dispose()
        TestRunner.Assert(WaitExited(proc, 5000),
                          $"ffmpeg killed by Dispose (HasExited={proc.HasExited})")
        Try : File.Delete(outPath) : Catch : End Try
        Try : File.Delete(outPath & ".frag.mp4") : Catch : End Try
    End Sub

    Private Sub Test_RemuxHook()
        SyncLock _hookLock : _hookedPids.Clear() : End SyncLock
        Dim outPath As String = TempOutput("remux.mp4")
        Dim h264 As String = TempOutput("src.h264")
        Try
            TestRunner.Assert(GenerateH264(h264), "sandbox H.264 generated")

            Using mux = CreateSession(outPath)
                TestRunner.Assert(mux.Start(), "LiveMux.Start succeeded")

                ' Pace the feed like the production CFR loop does — an instant
                ' burst + immediate EOF makes ffmpeg's h264 prober give up
                ' before codec params (dimensions) are parsed (exit -22).
                Dim payload As Byte() = File.ReadAllBytes(h264)
                Dim chunk As Integer = 4096
                Dim off As Integer = 0
                While off < payload.Length
                    Dim n As Integer = Math.Min(chunk, payload.Length - off)
                    Dim slice(n - 1) As Byte
                    Buffer.BlockCopy(payload, off, slice, 0, n)
                    mux.FeedVideo(slice, n)
                    off += n
                    Thread.Sleep(15)
                End While
                Thread.Sleep(300)

                Dim res = mux.[Stop](20000)
                TestRunner.Assert(res.Succeeded, $"session succeeded (exit={res.FFmpegExitCode}, err={res.ErrorMessage})")
            End Using

            Dim hooked As List(Of Integer) = HookedPidsSnapshot()
            TestRunner.Assert(hooked.Count >= 2,
                              $"BOTH main and remux ffmpeg went through the hook (got {hooked.Count})")
            TestRunner.Assert(File.Exists(outPath), "final faststart MP4 exists")
        Finally
            Try : File.Delete(outPath) : Catch : End Try
            Try : File.Delete(outPath & ".frag.mp4") : Catch : End Try
            Try : File.Delete(h264) : Catch : End Try
        End Try
    End Sub

End Module

End Namespace
