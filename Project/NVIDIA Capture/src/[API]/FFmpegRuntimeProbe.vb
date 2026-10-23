Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks

' Binary-declared FFmpeg capabilities are NOT a GPU/driver availability test.
' Read-only probes: -version, -filters, -devices and -encoders. No capture starts.
Public NotInheritable Class FFmpegRuntimeProbe
    Private Sub New()
    End Sub

    Private Shared ReadOnly CaptureFilterNames As String() = {"ddagrab", "gfxcapture"}
    Private Shared ReadOnly CaptureDeviceNames As String() = {"gdigrab"}
    Private Shared ReadOnly EncoderNames As String() = {
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf",
        "libx264", "libx265"
    }

    Public NotInheritable Class Result
        Public Property BinaryRunnable As Boolean
        Public Property Version As String = ""
        Public Property CaptureApis As String() = Array.Empty(Of String)()
        Public Property Encoders As String() = Array.Empty(Of String)()
        Public Property Reason As String = ""

        Public Function Supports(route As CaptureBackendRoute.Decision) As Boolean
            If Not BinaryRunnable OrElse route Is Nothing OrElse Not route.Allowed OrElse
               route.Kind <> "FFmpeg" Then Return False
            Dim encoder As String = CanonicalEncoder(route.Encoder)
            Return CaptureApis.Contains(route.CaptureApi, StringComparer.OrdinalIgnoreCase) AndAlso
                   Encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase)
        End Function
    End Class

    Public Shared Function CanonicalEncoder(name As String) As String
        Select Case If(name, "").Trim().ToLowerInvariant()
            Case "nvenc_h264" : Return "h264_nvenc"
            Case "nvenc_hevc" : Return "hevc_nvenc"
            Case "nvenc_av1" : Return "av1_nvenc"
            Case "quicksync_h264" : Return "h264_qsv"
            Case "quicksync_hevc" : Return "hevc_qsv"
            Case "amf_h264" : Return "h264_amf"
            Case "amf_hevc" : Return "hevc_amf"
            Case Else : Return If(name, "").Trim().ToLowerInvariant()
        End Select
    End Function

    ' Pure parsing seam used by tests. Only real listing rows count:
    ' " .. ddagrab ..." and " V....D h264_nvenc ...". Documentation text
    ' that merely mentions a name must not report an advertised capability.
    Public Shared Function Parse(versionOutput As String, filtersOutput As String,
                                 devicesOutput As String, encodersOutput As String) As Result
        Dim result As New Result()
        Dim first = If(versionOutput, "").Split({ControlChars.Cr, ControlChars.Lf},
                           StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        result.BinaryRunnable = first IsNot Nothing AndAlso
            first.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase)
        If Not result.BinaryRunnable Then
            result.Reason = "ffmpeg_version_probe_failed"
            Return result
        End If
        result.Version = first
        Dim filters = ParseListing(If(filtersOutput, ""), CaptureFilterNames)
        Dim devices = ParseListing(If(devicesOutput, ""), CaptureDeviceNames)
        result.CaptureApis = filters.Concat(devices).Distinct(StringComparer.OrdinalIgnoreCase).
            OrderBy(Function(item) item, StringComparer.Ordinal).ToArray()
        result.Encoders = ParseListing(If(encodersOutput, ""), EncoderNames).
            OrderBy(Function(item) item, StringComparer.Ordinal).ToArray()
        Return result
    End Function

    Private Shared Function ParseListing(output As String, knownNames As IEnumerable(Of String)) As String()
        Dim found As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each row In output.Split({ControlChars.Cr, ControlChars.Lf},
                                    StringSplitOptions.RemoveEmptyEntries)
            Dim parts = row.Trim().Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
            If parts.Length < 2 Then Continue For
            ' FFmpeg feature list rows start with capability flags:
            ' ".." (filters), "D" (devices), "V....." (encoders).
            Dim flags = parts(0)
            If flags.Length > 8 OrElse Not flags.All(
                Function(ch) ch = "."c OrElse Char.IsUpper(ch)) Then Continue For
            For Each name In knownNames
                If String.Equals(parts(1), name, StringComparison.OrdinalIgnoreCase) Then
                    found.Add(name)
                    Exit For
                End If
            Next
        Next
        Return found.ToArray()
    End Function

    Private Shared ReadOnly CacheLock As New Object()
    Private Shared CachedPath As String = ""
    Private Shared CachedVersion As String = ""
    Private Shared CachedResult As Result = Nothing

    ' GET /engines must never block the Capture status/health request while
    ' FFmpeg binaries load DLLs. First call returns probe_pending and schedules
    ' one background read-only capability scan for that file version.
    Public Shared Function CachedOrStart(exePath As String) As Result
        Dim path As String = If(exePath, "")
        Dim version As String = ""
        Try
            If IO.File.Exists(path) Then
                Dim info As New FileInfo(path)
                version = info.Length.ToString() & ":" & info.LastWriteTimeUtc.Ticks.ToString()
            End If
        Catch
        End Try
        SyncLock CacheLock
            If CachedResult IsNot Nothing AndAlso
               String.Equals(CachedPath, path, StringComparison.OrdinalIgnoreCase) AndAlso
               CachedVersion = version Then Return CachedResult
            CachedPath = path
            CachedVersion = version
            CachedResult = New Result With {.Reason = "ffmpeg_probe_pending"}
        End SyncLock
        Task.Run(Sub()
                     Dim scanned = Inspect(path)
                     SyncLock CacheLock
                         If String.Equals(CachedPath, path, StringComparison.OrdinalIgnoreCase) AndAlso
                            CachedVersion = version Then CachedResult = scanned
                     End SyncLock
                 End Sub)
        Return New Result With {.Reason = "ffmpeg_probe_pending"}
    End Function

    Public Shared Function Inspect(exePath As String) As Result
        If String.IsNullOrWhiteSpace(exePath) OrElse Not File.Exists(exePath) Then
            Return New Result With {.Reason = "ffmpeg_binary_missing"}
        End If
        Try
            Dim ver As String = RunReadOnly(exePath, "-version")
            Dim filters As String = RunReadOnly(exePath, "-hide_banner -filters")
            Dim devices As String = RunReadOnly(exePath, "-hide_banner -devices")
            Dim encoders As String = RunReadOnly(exePath, "-hide_banner -encoders")
            Return Parse(ver, filters, devices, encoders)
        Catch ex As Exception
            Return New Result With {.Reason = "ffmpeg_probe_error_" & ex.GetType().Name}
        End Try
    End Function

    Private Shared Function RunReadOnly(exePath As String, arguments As String) As String
        Dim psi As New ProcessStartInfo(exePath, arguments) With {
            .CreateNoWindow = True,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True
        }
        Using proc As Process = Process.Start(psi)
            If proc Is Nothing Then Throw New IOException("ffmpeg probe spawn failed")
            Dim so As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
            Dim se As Task(Of String) = proc.StandardError.ReadToEndAsync()
            If Not proc.WaitForExit(5000) Then
                Try : proc.Kill(True) : Catch : End Try
                Throw New TimeoutException("ffmpeg probe exceeded 5 seconds")
            End If
            If proc.ExitCode <> 0 Then Throw New IOException("ffmpeg probe returned non-zero")
            Dim output = so.GetAwaiter().GetResult() & vbLf & se.GetAwaiter().GetResult()
            If output.Length > 65536 Then Throw New IOException("ffmpeg probe output too large")
            Return output
        End Using
    End Function
End Class
