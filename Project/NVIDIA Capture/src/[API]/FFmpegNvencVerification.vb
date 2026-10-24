Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks

' Synthetic-only hardware verification. Does not capture a display or write a video file.
' Explicitly triggered by POST /encoders/verify; GET /engines only observes the snapshot.
Public NotInheritable Class FFmpegNvencVerification
    Private Sub New()
    End Sub

    Public NotInheritable Class EncoderTest
        Public Property gpuIndex As Integer
        Public Property encoder As String = ""
        Public Property syntheticEncodePassed As Boolean
        Public Property realRecordingVerified As Boolean = False
        Public Property reason As String = ""
    End Class

    Public NotInheritable Class VerificationState
        Public Property status As String = "not_run"
        Public Property reason As String = ""
        Public Property gpuCount As Integer
        Public Property testedAtUtc As String = ""
        Public Property results As EncoderTest() = Array.Empty(Of EncoderTest)()
    End Class

    Private Shared ReadOnly Guard As New Object()
    Private Shared Current As New VerificationState()
    Private Shared CurrentPathKey As String = ""
    Private Shared LastStart As DateTime = DateTime.MinValue
    Private Shared ReadOnly NvencNames As String() = {"h264_nvenc", "hevc_nvenc", "av1_nvenc"}

    Private Shared Function BinaryKey(path As String) As String
        Try
            Dim f As New FileInfo(path)
            If Not f.Exists Then Return ""
            Return f.FullName & ":" & f.Length.ToString() & ":" & f.LastWriteTimeUtc.Ticks.ToString()
        Catch
            Return ""
        End Try
    End Function

    ' Reads are pure: probing hardware is NEVER a side effect of GET /engines.
    Public Shared Function Snapshot(path As String) As VerificationState
        Dim key = BinaryKey(path)
        SyncLock Guard
            If String.IsNullOrEmpty(key) OrElse Not String.Equals(key, CurrentPathKey, StringComparison.OrdinalIgnoreCase) Then
                Return New VerificationState With {.status = "not_run", .reason = "not_verified_for_this_binary"}
            End If
            Return New VerificationState With {
                .status = Current.status,
                .reason = Current.reason,
                .gpuCount = Current.gpuCount,
                .testedAtUtc = Current.testedAtUtc,
                .results = Current.results.ToArray()
            }
        End SyncLock
    End Function

    ' Single-flight, rate-limited, read-only with respect to screen and output files.
    ' The only GPU workload is one synthetic color frame sent to FFmpeg's null muxer.
    Public Shared Function Start(path As String, advertised As IEnumerable(Of String)) As String
        Dim key = BinaryKey(path)
        If String.IsNullOrEmpty(key) OrElse Not FFmpegLocator.IsUsableFFmpeg(path) Then Return "ffmpeg_unavailable"
        Dim allowed = NvencNames.Where(Function(name) advertised IsNot Nothing AndAlso
            advertised.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray()
        If allowed.Length = 0 Then Return "no_nvenc_encoders_advertised"
        SyncLock Guard
            If Current.status = "running" Then Return "already_running"
            If DateTime.UtcNow.Subtract(LastStart).TotalSeconds < 60 AndAlso
                String.Equals(key, CurrentPathKey, StringComparison.OrdinalIgnoreCase) Then Return "rate_limited"
            CurrentPathKey = key
            LastStart = DateTime.UtcNow
            Current = New VerificationState With {.status = "running", .reason = "synthetic_probe_in_progress"}
        End SyncLock
        Task.Run(Sub() RunAll(path, key, allowed))
        Return "started"
    End Function

    Private Shared Sub RunAll(path As String, key As String, encoders As String())
        Dim finished As New VerificationState With {.status = "complete"}
        Try
            Dim listing = RunBounded("nvidia-smi.exe", "-L", 6000)
            If listing.Item1 <> 0 Then Throw New IOException("nvidia_smi_unavailable")
            Dim ids As New List(Of Integer)()
            For Each line In listing.Item2.Split({ControlChars.Cr, ControlChars.Lf},
                                                  StringSplitOptions.RemoveEmptyEntries)
                Dim match = Regex.Match(line, "^GPU\s+(\d+):")
                If match.Success Then
                    Dim index As Integer
                    If Integer.TryParse(match.Groups(1).Value, index) AndAlso index >= 0 AndAlso
                        index < 8 Then ids.Add(index)
                End If
            Next
            If ids.Count = 0 Then Throw New IOException("no_nvidia_gpu_listed")
            finished.gpuCount = ids.Distinct().Count()
            Dim tests As New List(Of EncoderTest)()
            For Each gpu In ids.Distinct()
                For Each encoder In encoders
                    Dim args = "-hide_banner -nostdin -loglevel error -f lavfi " &
                               "-i color=c=black:s=128x128:r=1:d=1 -frames:v 1 " &
                               "-pix_fmt yuv420p -c:v " & encoder & " -gpu " & gpu.ToString() &
                               " -f null NUL"
                    Dim run = RunBounded(path, args, 10000)
                    tests.Add(New EncoderTest With {
                        .gpuIndex = gpu,
                        .encoder = encoder,
                        .syntheticEncodePassed = run.Item1 = 0,
                        .reason = If(run.Item1 = 0, "", "encoder_init_or_encode_failed")
                    })
                Next
            Next
            finished.results = tests.ToArray()
            finished.testedAtUtc = DateTime.UtcNow.ToString("o")
        Catch ex As Exception
            finished.status = "error"
            finished.reason = If(TypeOf ex Is IOException, ex.Message, "synthetic_probe_error")
            finished.testedAtUtc = DateTime.UtcNow.ToString("o")
        End Try
        SyncLock Guard
            If String.Equals(key, CurrentPathKey, StringComparison.OrdinalIgnoreCase) Then Current = finished
        End SyncLock
    End Sub

    Private Shared Function RunBounded(exe As String, args As String, timeoutMs As Integer) As Tuple(Of Integer, String)
        Dim psi As New ProcessStartInfo(exe, args) With {
            .CreateNoWindow = True,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True
        }
        Using proc As Process = Process.Start(psi)
            If proc Is Nothing Then Return Tuple.Create(-1, "")
            Dim stdout As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
            Dim stderr As Task(Of String) = proc.StandardError.ReadToEndAsync()
            If Not proc.WaitForExit(timeoutMs) Then
                Try : proc.Kill(True) : Catch : End Try
                Return Tuple.Create(-2, "timed_out")
            End If
            Dim combined = stdout.GetAwaiter().GetResult() & vbLf & stderr.GetAwaiter().GetResult()
            If combined.Length > 16384 Then combined = combined.Substring(0, 16384)
            Return Tuple.Create(proc.ExitCode, combined)
        End Using
    End Function
End Class
