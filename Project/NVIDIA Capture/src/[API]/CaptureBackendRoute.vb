Option Strict On
Option Explicit On
Option Infer On

' A pure policy seam for Capture/Encoder ownership.
' Safe to run in tests: no GPU, process, IO, or WinForms dependencies.
Public NotInheritable Class CaptureBackendRoute
    Private Sub New()
    End Sub

    Public NotInheritable Class Decision
        Public ReadOnly Property Kind As String
        Public ReadOnly Property CaptureApi As String
        Public ReadOnly Property Encoder As String
        Public ReadOnly Property ErrorCode As String

        Public ReadOnly Property Allowed As Boolean
            Get
                Return ErrorCode.Length = 0
            End Get
        End Property

        Public Sub New(kind As String, api As String, encoderName As String, errorCode As String)
            Me.Kind = kind
            Me.CaptureApi = api
            Me.Encoder = encoderName
            Me.ErrorCode = errorCode
        End Sub
    End Class

    Private Shared Function Reject(code As String, api As String, encoderName As String) As Decision
        Return New Decision("Unsupported", api, encoderName, code)
    End Function

    Public Shared Function Decide(mode As String, captureApi As String, encoderName As String) As Decision
        Dim m = If(mode, "").Trim().ToLowerInvariant()
        Dim api = If(captureApi, "").Trim().ToLowerInvariant()
        Dim encoder = If(encoderName, "").Trim().ToLowerInvariant()

        Select Case m
            Case "duluka", "native", "ddagrab"
                If api <> "" AndAlso api <> "ddagrab" AndAlso api <> "dxgi_desktop_duplication" Then
                    Return Reject("unsupported_duluka_capture_api", api, encoder)
                End If
                ' The native NvencEncoderBackend currently implements H.264 only.
                If encoder <> "nvenc_h264" AndAlso encoder <> "h264_nvenc" Then
                    Return Reject("unsupported_native_encoder", api, encoder)
                End If
                Return New Decision("Duluka", "dxgi_desktop_duplication", "NVENC_H264", "")

            Case "ffmpeg", "legacy"
                If api <> "ddagrab" AndAlso api <> "gdigrab" AndAlso api <> "gfxcapture" Then
                    Return Reject("unsupported_ffmpeg_capture_api", api, encoder)
                End If
                Select Case encoder
                    Case "nvenc_h264", "h264_nvenc", "nvenc_hevc", "hevc_nvenc", "nvenc_av1", "av1_nvenc",
                         "quicksync_h264", "h264_qsv", "quicksync_hevc", "hevc_qsv",
                         "amf_h264", "h264_amf", "amf_hevc", "hevc_amf", "libx264", "libx265"
                        Return New Decision("FFmpeg", api, encoder, "")
                    Case Else
                        Return Reject("unsupported_ffmpeg_encoder", api, encoder)
                End Select

            Case "nvidia", "shadowplay"
                ' Historical installed GeForce Experience recorder. NOT NvFBC.
                Return New Decision("InstalledNvidia", "installed_shadowplay", "installed_shadowplay", "")

            Case "obs"
                Return Reject("obs_capture_not_implemented", api, encoder)
            Case Else
                Return Reject("unknown_capture_engine", api, encoder)
        End Select
    End Function
End Class
