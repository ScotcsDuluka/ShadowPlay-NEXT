Option Strict On
Imports System

Module Program
    Private _passed As Integer

    Private Sub Check(name As String, result As Boolean)
        If Not result Then Throw New Exception("FAIL: " & name)
        Console.WriteLine("PASS: " & name)
        _passed += 1
    End Sub

    Sub Main()
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
        Console.WriteLine("PASS: " & _passed & " route guards")
    End Sub
End Module
