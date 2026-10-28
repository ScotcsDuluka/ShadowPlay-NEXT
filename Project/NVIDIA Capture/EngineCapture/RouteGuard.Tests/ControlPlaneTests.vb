Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text

Module ControlPlaneTests
    Private _checks As Integer
    Private Sub Check(name As String, valid As Boolean)
        If Not valid Then Throw New Exception("FAIL: " & name)
        _checks += 1
        Console.WriteLine("PASS: " & name)
    End Sub

    Public Sub Run()
        Dim token = New String("A"c, 32)
        Dim headers As New Dictionary(Of String, String) From {{"header:host", "127.0.0.1:59077"}}
        Check("Loopback reads require no secret", CaptureRestAuthorization.Authorize("GET", headers, 59077, "") = 0)
        Check("Missing vault fails writes closed", CaptureRestAuthorization.Authorize("POST", headers, 59077, "") = 503)
        Check("Missing worker credential denied", CaptureRestAuthorization.Authorize("POST", headers, 59077, token) = 403)
        headers("header:" & CaptureRestAuthorization.TokenHeader) = token
        Check("Authenticated gateway write accepted without dispatch", CaptureRestAuthorization.Authorize("POST", headers, 59077, token) = 0)
        headers("header:" & CaptureRestAuthorization.TokenHeader) = New String("B"c, 32)
        Check("Mismatched worker credential denied", CaptureRestAuthorization.Authorize("POST", headers, 59077, token) = 403)
        headers("header:" & CaptureRestAuthorization.TokenHeader) = token
        headers("header:origin") = "http://evil.example"
        Check("Foreign browser origin denied even with credential", CaptureRestAuthorization.Authorize("POST", headers, 59077, token) = 403)
        headers.Remove("header:origin")
        headers("header:host") = "evil.example:59077"
        Check("DNS rebinding host denied", CaptureRestAuthorization.Authorize("POST", headers, 59077, token) = 403)
        headers("header:host") = "127.0.0.1:59078"
        Check("Wrong listener port denied", CaptureRestAuthorization.Authorize("GET", headers, 59077, token) = 403)
        headers("header:host") = "127.0.0.1:59077"
        Check("Unsupported verbs cannot dispatch", CaptureRestAuthorization.Authorize("DELETE", headers, 59077, token) = 405)
        If OperatingSystem.IsWindows() AndAlso File.Exists(CaptureRestAuthorization.ProtectedTokenPath) Then
            Check("Existing same-user DPAPI vault readable without disclosure",
                  CaptureRestAuthorization.ValidToken(CaptureRestAuthorization.ReadProtectedToken()))
        End If

        For Each malformed In New String() {
            "POST /record/start HTTP/1.1" & vbCrLf & "Host: localhost:59077" & vbCrLf &
                "Content-Length: 0" & vbCrLf & "Content-Length: 0" & vbCrLf & vbCrLf,
            "POST /record/start HTTP/1.1" & vbCrLf & "Host: localhost:59077" & vbCrLf &
                "Transfer-Encoding: chunked" & vbCrLf & vbCrLf,
            "POST /record/start HTTP/1.1" & vbCrLf & "Host: localhost:59077" & vbCrLf &
                "X-ShadowPlay-Capture-Token: " & token & vbCrLf & "X-ShadowPlay-Capture-Token: " & token & vbCrLf & vbCrLf,
            "GET http://evil.example/health HTTP/1.1" & vbCrLf & "Host: localhost:59077" & vbCrLf & vbCrLf,
            "GET /health HTTP/1.1" & vbCrLf & "Host : localhost:59077" & vbCrLf & vbCrLf}
            Using input As New MemoryStream(Encoding.ASCII.GetBytes(malformed))
                Check("Ambiguous HTTP request rejected " & _checks, CaptureHttpRequestReader.Read(input) Is Nothing)
            End Using
        Next
        Using input As New MemoryStream(Encoding.ASCII.GetBytes("POST /settings HTTP/1.1" & vbCrLf &
            "Host: localhost:59077" & vbCrLf & "X-ShadowPlay-Capture-Token: " & token & vbCrLf & "Content-Length: 0" & vbCrLf & vbCrLf))
            Dim request = CaptureHttpRequestReader.Read(input)
            Check("Parsed credential reaches authorization without command execution", request IsNot Nothing AndAlso
                CaptureRestAuthorization.Authorize(request("method"), request, 59077, token) = 0)
        End Using

        Dim idle = CaptureRuntimeTruth.Observe(False, "", "", 0, 0, False, False, 0, False)
        Check("Idle has no actual owner even if configuration selects engine", Not idle.busy AndAlso idle.actualRunningEngine = "" AndAlso idle.actualSessionBackend = "")
        Dim pending = CaptureRuntimeTruth.Observe(True, "", "", 0, 0, False, False, 0, False)
        Check("Queued native start is busy but not actual session", pending.busy AndAlso pending.lifecycleOwner = "Duluka" AndAlso pending.actualSessionBackend = "" AndAlso Not pending.isCapturing)
        Dim awaiting = CaptureRuntimeTruth.Observe(True, "s1", "AwaitingFirstRealFrame", 0, 0, False, False, 0, False)
        Check("Allocated native session is not capture progress", awaiting.actualSessionBackend = "Duluka" AndAlso awaiting.actualRunningEngine = "")
        Dim synthetic = CaptureRuntimeTruth.Observe(True, "s1", "Recording", 0, 4, False, False, 0, False)
        Check("Synthetic encoded frames cannot attest real capture", Not synthetic.isCapturing)
        Dim recording = CaptureRuntimeTruth.Observe(True, "s1", "Recording", 4, 4, False, False, 0, False)
        Check("Real native capture and encoder progress attest running engine", recording.isCapturing AndAlso recording.actualRunningEngine = "Duluka")
        Check("Running frame evidence does not certify hardware or media", Not recording.hardwareVerified AndAlso Not recording.mediaVerified)
        Dim finalize = CaptureRuntimeTruth.Observe(True, "s1", "Finalizing", 4, 4, False, False, 0, False)
        Check("Finalization preserves busy ownership without claiming capture", finalize.busy AndAlso Not finalize.isCapturing)
        Dim ffmpeg = CaptureRuntimeTruth.Observe(False, "", "", 0, 0, True, True, 0, False)
        Check("FFmpeg process startup is not encoded frame progress", ffmpeg.actualSessionBackend = "FFmpeg" AndAlso Not ffmpeg.isCapturing)
        ffmpeg = CaptureRuntimeTruth.Observe(False, "", "", 0, 0, True, True, 5, False)
        Check("Standalone FFmpeg owns its running engine", ffmpeg.isCapturing AndAlso ffmpeg.actualRunningEngine = "FFmpeg")
        Dim external = CaptureRuntimeTruth.Observe(False, "", "", 0, 0, False, False, 0, True)
        Check("External NVIDIA request is not external recorder attestation", external.busy AndAlso external.actualSessionBackend = "" AndAlso external.actualRunningEngine = "")
        Dim conflict = CaptureRuntimeTruth.Observe(True, "s1", "Recording", 5, 5, True, True, 5, False)
        Check("Concurrent owner conflict fails truth closed", conflict.busy AndAlso conflict.lifecycleState = "ConflictingOwners" AndAlso conflict.actualRunningEngine = "")
        Dim settings As New Dictionary(Of String, String) From {
            {"quality", "Custom"}, {"resolution", "1080p HD"}, {"framerate", "120"}, {"bitrateBps", "50000000"}}
        Check("Explicit 120fps settings validated without mutation", CaptureRestSettingsPolicy.Valid(settings) AndAlso settings("framerate") = "120")
        settings("resolution") = "made_up"
        Check("Unknown resolution cannot silently select native", Not CaptureRestSettingsPolicy.Valid(settings))
        settings("resolution") = "Custom"
        Check("Custom means existing canonical custom geometry", CaptureRestSettingsPolicy.Valid(settings))
        settings("framerate") = "0"
        Check("Zero FPS rejected", Not CaptureRestSettingsPolicy.Valid(settings))
        settings("framerate") = "60junk"
        Check("Numeric suffix cannot become a different FPS", Not CaptureRestSettingsPolicy.Valid(settings))
        settings("framerate") = "241"
        Check("Out-of-contract FPS rejected", Not CaptureRestSettingsPolicy.Valid(settings))
        settings("framerate") = "30"
        settings("bitrateBps") = "2147483648"
        Check("Snapshot bitrate overflow rejected", Not CaptureRestSettingsPolicy.Valid(settings))
        Console.WriteLine("PASS: " & _checks & " control-plane authorization and runtime truth checks")
    End Sub
End Module
