Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Where the frame's pixel data lives. (P1-A v1.3.1 §3.3)
    ''' </summary>
    Public Enum VideoFrameOrigin
        ''' <summary>Pinned byte buffer in system RAM.</summary>
        CpuMemory
        ''' <summary>ID3D11Texture2D living in GPU memory.</summary>
        GpuD3D11Texture
    End Enum
End Namespace
