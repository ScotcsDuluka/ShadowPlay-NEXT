Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

' Synthetic FFmpeg lifecycle acceptance only. Never invokes gdigrab, ddagrab,
' WASAPI, microphone, speaker, NVENC, or the installed NvCapture worker.
Module Program
    Private _checks As Integer
    Private _sessions As Integer

    Private Sub Check(condition As Boolean, label As String)
        If Not condition Then Throw New Exception("FAIL " & label)
        _checks += 1
        Console.WriteLine("PASS " & label)
    End Sub

    Private NotInheritable Class IsolatedChild
        Implements IDisposable

        Private ReadOnly _p As Process
        Private ReadOnly _stderr As Task(Of String)
        Private _finished As Boolean
        Private _exitCode As Integer

        Public Sub New(ffmpeg As String, output As String, codec As String)
            Dim psi As New ProcessStartInfo(ffmpeg) With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardInput = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = False
            }
            For Each arg In {"-hide_banner", "-nostats", "-loglevel", "error",
                             "-re", "-f", "lavfi",
                             "-i", "testsrc2=size=320x180:rate=30",
                             "-t", "12", "-an", "-c:v", codec, "-threads", "1",
                             "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                             "-movflags", "+faststart", "-y", output}
                psi.ArgumentList.Add(arg)
            Next
            _p = Process.Start(psi)
            If _p Is Nothing Then Throw New Exception("Could not spawn isolated FFmpeg")
            _sessions += 1
            _stderr = _p.StandardError.ReadToEndAsync()
        End Sub

        Public ReadOnly Property ProcessId As Integer
            Get
                Return _p.Id
            End Get
        End Property

        Public ReadOnly Property IsExited As Boolean
            Get
                Return _p.HasExited
            End Get
        End Property

        Public Sub StopGracefully()
            If _p.HasExited Then Return
            Try
                _p.StandardInput.WriteLine("q")
                _p.StandardInput.Flush()
            Catch ex As IOException
                If Not _p.HasExited Then Throw New Exception("FFmpeg stdin close unexpected", ex)
            End Try
        End Sub

        Public Sub AbortOnlyThisChild()
            If Not _p.HasExited Then _p.Kill(True)
        End Sub

        Public Function ExitCodeBounded() As Integer
            If Not _finished Then
                If Not _p.WaitForExit(15000) Then
                    AbortOnlyThisChild()
                    _p.WaitForExit(5000)
                    Throw New Exception("Isolated FFmpeg did not exit within 15s")
                End If
                _exitCode = _p.ExitCode
                _finished = True
            End If
            Return _exitCode
        End Function

        Public ReadOnly Property Diagnostics As String
            Get
                If Not _finished Then Throw New InvalidOperationException("Read logs only after child exit")
                Return _stderr.GetAwaiter().GetResult()
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                AbortOnlyThisChild()
                _p.WaitForExit(5000)
            Finally
                _p.Dispose()
            End Try
        End Sub
    End Class

    Private Function OutputPath(root As String, name As String) As String
        Return Path.Combine(root, name & ".mp4")
    End Function

    Private Sub Healthy(ffmpeg As String, ffprobe As String, root As String, tag As String)
        Dim output = OutputPath(root, tag)
        Using child As New IsolatedChild(ffmpeg, output, "libx264")
            Thread.Sleep(1800)
            child.StopGracefully()
            child.StopGracefully() ' idempotent repeated stop
            Dim exitCode = child.ExitCodeBounded()
            Check(exitCode = 0, tag & " clean FFmpeg stop (exit=0)")
            Check(child.IsExited, tag & " child stopped")
            Dim evidence = CaptureMediaInspector.Inspect(output, ffprobe)
            Check(evidence.Verified AndAlso evidence.VideoStreams = 1 AndAlso
                  evidence.AudioStreams = 0, tag & " finalized video-only MP4")
            Check(evidence.DurationSec.HasValue AndAlso
                  evidence.DurationSec.Value >= 0.3 AndAlso
                  evidence.DurationSec.Value < 11.0, tag & " duration and early stop bounded")
            Check(File.Exists(output) AndAlso (New FileInfo(output)).Length > 10000,
                  tag & " real MP4 payload")
        End Using
    End Sub

    Private Sub FailureThenRecover(ffmpeg As String, ffprobe As String, root As String)
        Dim failed = OutputPath(root, "encoder-not-found")
        Using child As New IsolatedChild(ffmpeg, failed, "shadowplay_invalid_encoder")
            Dim code = child.ExitCodeBounded()
            Check(code <> 0, "invalid codec fails with nonzero child exit")
            Check(child.IsExited, "failed encoder child exits; no orphan")
            Dim evidence = CaptureMediaInspector.Inspect(failed, ffprobe)
            Check(Not evidence.Verified, "failed encoder output is not verified media")
        End Using

        Dim aborted = OutputPath(root, "forced-stop")
        Using child As New IsolatedChild(ffmpeg, aborted, "libx264")
            Thread.Sleep(1000)
            child.AbortOnlyThisChild()
            Dim code = child.ExitCodeBounded()
            Check(code <> 0, "forced kill reports unsuccessful session")
            Check(child.IsExited, "forced kill leaves no live child")
            Dim evidence = CaptureMediaInspector.Inspect(aborted, ffprobe)
            ' A fragment could remain parseable, but an abnormal child exit
            ' MUST still invalidate the recording, regardless of probe success.
            Check(Not (code = 0 AndAlso evidence.Verified),
                  "aborted recording never passes exit+media outcome gate")
        End Using

        Healthy(ffmpeg, ffprobe, root, "recovered-after-failure")
    End Sub

    Sub Main(args As String())
        If args.Length <> 2 Then Throw New ArgumentException(
            "Usage: SessionRecovery.Tests <ffmpeg.exe> <ffprobe.exe>")
        Dim ffmpeg = Path.GetFullPath(args(0))
        Dim ffprobe = Path.GetFullPath(args(1))
        If Not File.Exists(ffmpeg) OrElse Not File.Exists(ffprobe) Then
            Throw New FileNotFoundException("Explicit source FFmpeg/FFprobe executables are required")
        End If
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal
        ' Only this generated directory is removed. No desktop, user media,
        ' production recording root or installed NvCapture files are touched.
        Dim root = Path.Combine(AppContext.BaseDirectory, "SessionRecovery_" &
                                Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Try
            Healthy(ffmpeg, ffprobe, root, "normal-stop-1")
            FailureThenRecover(ffmpeg, ffprobe, root)
            Healthy(ffmpeg, ffprobe, root, "normal-stop-2")
            Console.WriteLine("SESSION_RECOVERY_SYNTHETIC_PASS checks=" & _checks &
                              " sessions=" & _sessions &
                              " (lavfi video-only; no user devices)")
        Finally
            If Directory.Exists(root) Then Directory.Delete(root, True)
        End Try
    End Sub
End Module
