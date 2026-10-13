Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports CaptureEngine.Configuration
Imports CaptureEngine.Configuration.Schema
Imports CaptureEngine.FFmpeg

Namespace CaptureEngine.Pipeline
    ''' <summary>
    ''' Resolves an EngineConfigV2 into a concrete PipelineConfig + a
    ''' ready-to-use IFFmpegCommandBuilder.
    '''
    ''' Today the resolver only supports FFmpeg-based backends:
    '''   - "ffmpeg_ddagrab"     (default)
    '''   - "ffmpeg_gdigrab"
    '''   - "ffmpeg_gfxcapture"
    '''
    ''' Future backends will be added as architectural slots (per
    ''' docs/ARCHITECTURE.md):
    '''   - "dxgi"                — native DXGI Output Duplication
    '''   - "wgc"                 — Windows Graphics Capture
    '''   - "nvfbc"               — NVIDIA NvFBC (future)
    '''
    ''' Resolution rules:
    '''   1. If SourceConfig.Experimental.EnableD3D11Interop = True AND
    '''      CaptureMethod = "ddagrab" → resolves to "dxgi" (FUTURE —
    '''      currently throws NotImplementedException because DXGI backend
    '''      does not exist yet)
    '''   2. Otherwise → "ffmpeg_" + CaptureMethod
    '''
    ''' Audio backend:
    '''   - If Audio.System.Enabled OR Audio.Microphone.Enabled → "wasapi_loopback"
    '''   - Else → "none"
    '''
    ''' Output container:
    '''   - Read directly from Output.Container
    '''
    ''' Encoder:
    '''   - Read directly from Video.Encoder.FFmpegCodec
    ''' </summary>
    Public NotInheritable Class PipelineResolver
        Private Sub New()
            ' Static helper class — no instances.
        End Sub

        ''' <summary>
        ''' Resolve a V2 config into a PipelineConfig snapshot.
        '''
        ''' Throws InvalidOperationException if the config requests an
        ''' unsupported backend combination.
        ''' </summary>
        Public Shared Function Resolve(cfg As EngineConfigV2) As PipelineConfig
            If cfg Is Nothing Then Throw New ArgumentNullException(NameOf(cfg))

            Dim videoBackend As String = ResolveVideoBackend(cfg)
            Dim encoder As String = cfg.Video.Encoder.FFmpegCodec
            Dim audioBackend As String = ResolveAudioBackend(cfg)
            Dim outputContainer As String = cfg.Output.Container

            Return New PipelineConfig(videoBackend, encoder, audioBackend, outputContainer, cfg)
        End Function

        ''' <summary>
        ''' Build the appropriate IFFmpegCommandBuilder for the resolved pipeline.
        '''
        ''' Today only V2 builder is supported (V1 builder is for legacy parity
        ''' tests — production callers should use V2).
        '''
        ''' Future: when DXGI/WGC backends are added, they will return their
        ''' own IFFmpegCommandBuilder implementations (or null for native
        ''' backends that don't go through FFmpeg at all).
        ''' </summary>
        Public Shared Function BuildFFmpegCommandBuilder(cfg As EngineConfigV2) As IFFmpegCommandBuilder
            If cfg Is Nothing Then Throw New ArgumentNullException(NameOf(cfg))

            Dim pipeline As PipelineConfig = Resolve(cfg)

            Select Case pipeline.VideoBackend
                Case "ffmpeg_ddagrab", "ffmpeg_gdigrab", "ffmpeg_gfxcapture"
                    Return New FFmpegCommandBuilderV2(cfg)

                Case "dxgi"
                    ' Future: DXGI backend may not need FFmpeg at all (direct NVENC
                    ' encoding via NvEncodeAPI). For now, this throws.
                    Throw New NotImplementedException(
                        "DXGI backend is not yet implemented. Set Experimental.EnableD3D11Interop=False " &
                        "to use FFmpeg ddagrab instead.")

                Case Else
                    Throw New InvalidOperationException(
                        "Unknown VideoBackend: '" & pipeline.VideoBackend & "'. " &
                        "PipelineResolver.Resolve should have rejected this.")
            End Select
        End Function

        ' ── Private helpers ──

        Private Shared Function ResolveVideoBackend(cfg As EngineConfigV2) As String
            Dim method As String = cfg.Video.Capture.Method.ToLowerInvariant()

            ' Future: experimental DXGI path
            If cfg.Experimental.EnableD3D11Interop AndAlso method = "ddagrab" Then
                ' Mark as DXGI intent — but BuildFFmpegCommandBuilder will reject it
                ' with NotImplementedException. This is intentional: the slot exists
                ' in the architecture but is not implemented yet.
                Return "dxgi"
            End If

            Select Case method
                Case "ddagrab" : Return "ffmpeg_ddagrab"
                Case "gdigrab" : Return "ffmpeg_gdigrab"
                Case "gfxcapture" : Return "ffmpeg_gfxcapture"
                Case Else
                    Throw New InvalidOperationException(
                        "Unknown Video.Capture.Method: '" & cfg.Video.Capture.Method & "'. " &
                        "Valid: ddagrab, gdigrab, gfxcapture.")
            End Select
        End Function

        Private Shared Function ResolveAudioBackend(cfg As EngineConfigV2) As String
            If cfg.Audio.System.Enabled OrElse cfg.Audio.Microphone.Enabled Then
                Return "wasapi_loopback"
            End If
            Return "none"
        End Function
    End Class
End Namespace
