Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.Pipeline
    ''' <summary>
    ''' Resolved pipeline configuration — what the PipelineResolver decided
    ''' to use for this capture session.
    '''
    ''' This is a SNAPSHOT of the resolver's decision, not a mutable config.
    ''' Once resolved, callers should not modify it.
    ''' </summary>
    Public NotInheritable Class PipelineConfig
        ''' <summary>Resolved backend type: "ffmpeg_ddagrab" | "ffmpeg_gdigrab" | "ffmpeg_gfxcapture" | "dxgi" (future).</summary>
        Public ReadOnly Property VideoBackend As String

        ''' <summary>Resolved encoder type: "h264_nvenc" | "hevc_nvenc" | "h264_qsv" | "libx264" | etc.</summary>
        Public ReadOnly Property Encoder As String

        ''' <summary>Resolved audio backend: "wasapi_loopback" | "wasapi_capture" | "none".</summary>
        Public ReadOnly Property AudioBackend As String

        ''' <summary>Resolved output container: "mp4" | "mov" | "mkv" | "m4v".</summary>
        Public ReadOnly Property OutputContainer As String

        ''' <summary>The V2 config that was used to resolve this pipeline (reference, not copy).</summary>
        Public ReadOnly Property SourceConfig As EngineConfigV2

        Public Sub New(videoBackend As String,
                       encoder As String,
                       audioBackend As String,
                       outputContainer As String,
                       sourceConfig As EngineConfigV2)
            Me.VideoBackend = videoBackend
            Me.Encoder = encoder
            Me.AudioBackend = audioBackend
            Me.OutputContainer = outputContainer
            Me.SourceConfig = sourceConfig
        End Sub

        Public Overrides Function ToString() As String
            Return $"Pipeline[Video={VideoBackend}, Encoder={Encoder}, Audio={AudioBackend}, Container={OutputContainer}]"
        End Function
    End Class
End Namespace
