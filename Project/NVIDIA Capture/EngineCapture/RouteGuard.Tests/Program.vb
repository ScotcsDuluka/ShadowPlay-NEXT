Option Strict On
Imports System
Imports System.IO
Imports System.Text
Imports System.Linq

Module Program
    Private _passed As Integer

    Private Sub Check(name As String, result As Boolean)
        If Not result Then Throw New Exception("FAIL: " & name)
        Console.WriteLine("PASS: " & name)
        _passed += 1
    End Sub

    Sub Main()
        ControlPlaneTests.Run()
        Dim d = CaptureBackendRoute.Decide("Duluka", "dxgi_desktop_duplication", "NVENC_H264")
        Check("Duluka DXGI H264", d.Allowed AndAlso d.Kind = "Duluka")
        Check("Native route truth", d.CaptureApi = "dxgi_desktop_duplication")
        Check("Duluka legacy alias", CaptureBackendRoute.Decide("ddagrab", "ddagrab", "h264_nvenc").Allowed)
        Check("Duluka rejects HEVC (native not implemented)",
              Not CaptureBackendRoute.Decide("Duluka", "ddagrab", "NVENC_HEVC").Allowed)
        Check("Duluka rejects AV1", Not CaptureBackendRoute.Decide("native", "ddagrab", "NVENC_AV1").Allowed)
        Check("Duluka rejects GDI", Not CaptureBackendRoute.Decide("Duluka", "gdigrab", "NVENC_H264").Allowed)

        Check("FFmpeg ddagrab NVENC", CaptureBackendRoute.Decide("FFmpeg", "ddagrab", "h264_nvenc").Kind = "FFmpeg")
        Check("FFmpeg gdigrab CPU", CaptureBackendRoute.Decide("legacy", "gdigrab", "libx264").Allowed)
        Check("FFmpeg gfxcapture", CaptureBackendRoute.Decide("FFmpeg", "gfxcapture", "NVENC_HEVC").Allowed)
        Check("FFmpeg rejects fake input", Not CaptureBackendRoute.Decide("ffmpeg", "made_up", "libx264").Allowed)
        Check("FFmpeg rejects untrusted codec", Not CaptureBackendRoute.Decide("ffmpeg", "ddagrab", "custom;rm").Allowed)
        Check("Unknown mode refuses silent FFmpeg", Not CaptureBackendRoute.Decide("unknown", "ddagrab", "libx264").Allowed)
        Check("OBS planned not wired", CaptureBackendRoute.Decide("obs", "obs_managed", "obs_managed").ErrorCode =
              "obs_capture_not_implemented")
        Check("Historical installed NVIDIA remains distinct", CaptureBackendRoute.Decide("nvidia", "", "").Kind =
              "InstalledNvidia")
        ' FFmpeg listing parser: metadata only, never claims GPU support.
        Dim binary = FFmpegRuntimeProbe.Parse(
            "ffmpeg version N-test",
            " .. ddagrab |->V" & vbLf & " .. gfxcapture |->V" & vbLf &
            " Filters documentation mentioning libx264 should not count",
            " D  gdigrab  GDI capture" & vbLf & "  gdigrab mentioned in prose",
            " V....D h264_nvenc NVIDIA" & vbLf & " V....D libx264 H264" & vbLf &
            " V..... h264_qsv Intel")
        Check("FFmpeg runnable flag from version", binary.BinaryRunnable)
        Check("FFmpeg parsed 3 capture APIs", binary.CaptureApis.Length = 3)
        Check("FFmpeg parsed 3 listed encoders", binary.Encoders.Length = 3)
        Check("FFmpeg route supported when listed", binary.Supports(
              CaptureBackendRoute.Decide("ffmpeg", "gdigrab", "NVENC_H264")))
        Check("FFmpeg not supported if capture API absent", Not binary.Supports(
              CaptureBackendRoute.Decide("ffmpeg", "unavailable", "libx264")))
        Check("FFmpeg not supported if encoder absent", Not binary.Supports(
              CaptureBackendRoute.Decide("ffmpeg", "ddagrab", "libx265")))
        Check("Unusable FFmpeg version rejected", Not FFmpegRuntimeProbe.Parse(
              "not_ffmpeg", " .. ddagrab ", " D gdigrab ", " V....D libx264 ").BinaryRunnable)
        Check("Encoder normalized", FFmpegRuntimeProbe.CanonicalEncoder("QUICKSYNC_HEVC") = "hevc_qsv")
        If Environment.GetCommandLineArgs().Length > 1 Then
            Dim actual = FFmpegRuntimeProbe.Inspect(Environment.GetCommandLineArgs()(1))
            Check("Local FFmpeg binary runnable", actual.BinaryRunnable)
            Check("Local FFmpeg ddagrab + h264_nvenc advertised",
                  actual.Supports(CaptureBackendRoute.Decide("ffmpeg", "ddagrab", "h264_nvenc")))
            Check("Local FFmpeg gdigrab + libx264 advertised",
                  actual.Supports(CaptureBackendRoute.Decide("ffmpeg", "gdigrab", "libx264")))
        End If
        ' Regression: HTTP headers/body may span arbitrarily many TCP reads.
        Dim payloadText As String = "{""note"":""ทดสอบ ✓""}"
        Dim payloadBytes = Encoding.UTF8.GetBytes(payloadText)
        Dim header = "POST /record/start HTTP/1.1" & vbCrLf &
                     "Host: 127.0.0.1" & vbCrLf &
                     "Content-Length: " & payloadBytes.Length & vbCrLf & vbCrLf
        Dim requestBytes = Encoding.ASCII.GetBytes(header).Concat(payloadBytes).ToArray()
        Using split As New TinyChunkStream(requestBytes, 3)
            Dim message = CaptureHttpRequestReader.Read(split)
            Check("HTTP split packets parse correctly", message IsNot Nothing AndAlso
                  message("method") = "POST" AndAlso message("path") = "/record/start")
            Check("HTTP split Unicode body intact", message IsNot Nothing AndAlso
                  message("body") = payloadText)
        End Using
        Using oversized As New MemoryStream(Encoding.ASCII.GetBytes(
            "POST /record/start HTTP/1.1" & vbCrLf &
            "Content-Length: 900000" & vbCrLf & vbCrLf))
            Check("HTTP oversized body rejected", CaptureHttpRequestReader.Read(oversized) Is Nothing)
        End Using
        Using truncated As New MemoryStream(Encoding.ASCII.GetBytes(
            "POST /record/start HTTP/1.1" & vbCrLf &
            "Content-Length: 4" & vbCrLf & vbCrLf & "no"))
            Check("HTTP truncated body rejected", CaptureHttpRequestReader.Read(truncated) Is Nothing)
        End Using
        Using noBody As New TinyChunkStream(Encoding.ASCII.GetBytes(
            "GET /health HTTP/1.1" & vbCrLf & "Host: localhost" & vbCrLf & vbCrLf), 1)
            Dim message = CaptureHttpRequestReader.Read(noBody)
            Check("HTTP GET without content length", message IsNot Nothing AndAlso
                  message("method") = "GET" AndAlso message("body") = "")
        End Using
        ' Isolated mode preflight is pure and refuses accidental Stable roots.
        Dim testRoot = Path.Combine(Path.GetTempPath(), "capture-guard-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Path.Combine(testRoot, "Config"))
        Try
            Check("Sandbox requires marker",
                  CaptureIsolation.Validate(testRoot, testRoot, 59477) = "isolated_marker_missing")
            File.WriteAllText(Path.Combine(testRoot, ".shadowplay-capture-isolated"),
                              "shadowplay-next-isolated-capture-v1")
            Check("Sandbox requires dedicated config files",
                  CaptureIsolation.Validate(testRoot, testRoot, 59477) = "isolated_config_missing")
            File.WriteAllText(Path.Combine(testRoot, "Config", "config.json"), "{}")
            File.WriteAllText(Path.Combine(testRoot, "Config", "engine.json"), "{}")
            Check("Sandbox accepts isolated root and reserved port range",
                  CaptureIsolation.Validate(testRoot, testRoot, 59477) = "")
            Check("Sandbox refuses production REST port",
                  CaptureIsolation.Validate(testRoot, testRoot, 59077) = "isolated_port_invalid")
            Check("Sandbox refuses node API/control ports",
                  CaptureIsolation.Validate(testRoot, testRoot, 59012) = "isolated_port_invalid")
            Check("Sandbox rejects root mismatch",
                  CaptureIsolation.Validate(testRoot, Path.GetTempPath(), 59477) =
                  "isolated_layout_root_mismatch")
        Finally
            Directory.Delete(testRoot, True)
        End Try
        Console.WriteLine("PASS: " & _passed & " route/capability/HTTP/sandbox guards")
    End Sub
    Private NotInheritable Class TinyChunkStream
        Inherits MemoryStream
        Private ReadOnly _chunkSize As Integer

        Public Sub New(data As Byte(), chunkSize As Integer)
            MyBase.New(data, writable:=False)
            _chunkSize = chunkSize
        End Sub

        Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
            Return MyBase.Read(buffer, offset, Math.Min(count, _chunkSize))
        End Function
    End Class
End Module
