Option Strict On
Option Explicit On
Option Infer On

' MediaValidation.vb — F-02 (C/4): shared truthful output validation for
' tests that claim "recording saved / output valid".
'
' Audited deficiencies this helper replaces:
'   - INTEGRATION looked for an ffprobe BINARY NAMED "ffprobe" (no .exe) —
'     on Windows the lookup never matched, so the duration check was dead
'     code and the test was green with a headerless file;
'   - STRESS asserted only File.Exists + size > 0 for a "valid recording".
'
' Contract pinned here (same evidence ffprobe would give, via the bundled
' ffmpeg's own container dump):
'   1. file exists                    4. expected video stream present
'   2. file size > 0                  5. expected stream ABSENCE (video-only)
'   3. container probe succeeds       6. container duration > 0
'
' Failure-path tests (expected "no output") must NOT call this helper —
' they assert the opposite semantics explicitly.

Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text.RegularExpressions

Namespace CaptureEngine.FFmpegTests

    ''' <summary>Thrown by a test to report ENVIRONMENT NOT CAPABLE
    ''' (F-01/C-4): RunTest converts this into a SKIP verdict — never PASS,
    ''' never FAIL.</summary>
    Friend Class SkipException
        Inherits Exception

        Public Sub New(reason As String)
            MyBase.New(reason)
        End Sub
    End Class

    Friend Module MediaValidation

        ''' <summary>Assert that <paramref name="path"/> is a real, probe-able
        ''' media file whose streams match the expectation. Throws (test FAIL)
        ''' on any violated clause.</summary>
        Friend Sub AssertValidMedia(ffmpegExe As String, path As String,
                                    expectVideo As Boolean, expectAudio As Boolean,
                                    label As String)
            Program.Assert(IO.File.Exists(path), label & ": output file missing — " & path)
            Dim fi As New FileInfo(path)
            Program.Assert(fi.Length > 0, label & $": output file is EMPTY (0 bytes) — {path}")

            Dim probe As String = ProbeMedia(ffmpegExe, path)

            Dim m As Match = Regex.Match(probe, "Duration:\s*(\d+):(\d+):(\d+\.?\d*)")
            Program.Assert(m.Success,
                           label & ": probe reported no container Duration (not a valid media file): " &
                                   FirstLine(probe, 220))
            Dim durationSec As Double = CDbl(m.Groups(1).Value) * 3600 +
                                        CDbl(m.Groups(2).Value) * 60 +
                                        CDbl(m.Groups(3).Value)
            Program.Assert(durationSec > 0,
                           label & $": container duration = {durationSec:0.000}s (expected > 0)")

            Program.Assert(probe.Contains("Video:") = expectVideo,
                           label & $": video stream presence={Not expectVideo} but expected {expectVideo}: " &
                                   FirstLine(probe, 220))
            Program.Assert(probe.Contains("Audio:") = expectAudio,
                           label & $": audio stream presence={Not expectAudio} but expected {expectAudio}: " &
                                   FirstLine(probe, 220))
        End Sub

        ''' <summary>ffmpeg -i (no output target) writes the container/stream
        ''' report to stderr and exits non-zero — the exit code is irrelevant
        ''' here; the stderr dump IS the container evidence.</summary>
        Friend Function ProbeMedia(ffmpegExe As String, path As String) As String
            Dim psi As New ProcessStartInfo With {
                .FileName = ffmpegExe,
                .Arguments = "-hide_banner -i """ & path & """",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True
            }
            Using p As Process = Process.Start(psi)
                Dim errTask = p.StandardError.ReadToEndAsync()
                Dim outTask = p.StandardOutput.ReadToEndAsync()
                If Not p.WaitForExit(15000) Then
                    Try : p.Kill() : Catch : End Try
                End If
                Try : errTask.Wait(2000) : Catch : End Try
                Try : outTask.Wait(1000) : Catch : End Try
                Return If(errTask.Status = Threading.Tasks.TaskStatus.RanToCompletion, errTask.Result, "")
            End Using
        End Function

        Private Function FirstLine(text As String, maxChars As Integer) As String
            If text Is Nothing Then Return ""
            Dim oneLine As String = text.Replace(vbCr, "").Replace(vbLf, " | ")
            If oneLine.Length > maxChars Then oneLine = oneLine.Substring(0, maxChars) & "…"
            Return oneLine
        End Function

    End Module

End Namespace
