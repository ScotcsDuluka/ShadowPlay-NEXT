Option Strict On
Option Explicit On
Option Infer On

' Observed lifecycle ownership is intentionally separate from frame progress.
' This seam accepts observations only; it cannot select or start an engine.
Public NotInheritable Class CaptureRuntimeTruth
    Public Property lifecycleOwner As String = ""
    Public Property actualSessionBackend As String = ""
    Public Property actualRunningEngine As String = ""
    Public Property lifecycleState As String = "Idle"
    Public Property sessionId As String = ""
    Public Property busy As Boolean
    Public Property isCapturing As Boolean
    Public Property realFramesCaptured As Long
    Public Property framesEncoded As Long
    Public Property hardwareVerified As Boolean
    Public Property mediaVerified As Boolean

    Public Shared Function Observe(nativeTaskPending As Boolean, nativeSessionId As String,
                                   nativeState As String, nativeRealFrames As Long, nativeEncoded As Long,
                                   ffmpegBusy As Boolean, ffmpegRecording As Boolean, ffmpegFrames As Long,
                                   installedNvidiaRequested As Boolean) As CaptureRuntimeTruth
        Dim value As New CaptureRuntimeTruth()
        Dim owners = If(nativeTaskPending, 1, 0) + If(ffmpegBusy, 1, 0) + If(installedNvidiaRequested, 1, 0)
        value.busy = owners > 0
        If owners > 1 Then
            value.lifecycleState = "ConflictingOwners"
            Return value
        End If
        If nativeTaskPending Then
            value.lifecycleOwner = "Duluka"
            value.sessionId = If(nativeSessionId, "")
            value.lifecycleState = If(String.IsNullOrWhiteSpace(nativeState), "Preparing", nativeState)
            If value.sessionId.Length > 0 Then value.actualSessionBackend = "Duluka"
            value.realFramesCaptured = Math.Max(0, nativeRealFrames)
            value.framesEncoded = Math.Max(0, nativeEncoded)
            value.isCapturing = value.sessionId.Length > 0 AndAlso value.realFramesCaptured > 0 AndAlso
                                value.framesEncoded > 0 AndAlso value.lifecycleState = "Recording"
        ElseIf ffmpegBusy Then
            value.lifecycleOwner = "FFmpeg"
            value.lifecycleState = If(ffmpegRecording, "AwaitingFrameProgress", "PreparingOrFinalizing")
            If ffmpegRecording Then value.actualSessionBackend = "FFmpeg"
            value.framesEncoded = Math.Max(0, ffmpegFrames)
            ' FFmpeg frame statistics attest encoder output, not physical GPU identity.
            value.isCapturing = ffmpegRecording AndAlso value.framesEncoded > 0
            If value.isCapturing Then value.lifecycleState = "Recording"
        ElseIf installedNvidiaRequested Then
            value.lifecycleOwner = "InstalledNvidia"
            value.lifecycleState = "ExternalStatusUnverified"
            ' A host-side hotkey/flag cannot attest an external recorder session.
        End If
        If value.isCapturing Then value.actualRunningEngine = value.lifecycleOwner
        Return value
    End Function
End Class
