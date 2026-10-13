Option Strict On
Option Explicit On

Namespace CaptureEngine.Video.Frames
    ''' <summary>
    ''' Pixel format enumeration — encoder-agnostic.
    '''
    ''' Only formats that the frame pipeline needs to distinguish are listed.
    ''' Encoder-specific formats (e.g. NVENC internal formats) are NOT here —
    ''' the encoder converts from these generic formats to its own internal
    ''' representation.
    ''' </summary>
    Public Enum PixelFormat
        ''' <summary>Format not known or not set. Downstream should reject.</summary>
        Unknown = 0

        ''' <summary>32 bpp, byte order B,G,R,A. Phase 1 baseline.</summary>
        BGRA8 = 1

        ''' <summary>32 bpp, byte order R,G,B,A.</summary>
        RGBA8 = 2

        ''' <summary>Semi-planar 4:2:0, 12 bpp. Common NVENC intermediate.</summary>
        NV12 = 3

        ''' <summary>10-bit semi-planar 4:2:0, HDR-capable. Future use.</summary>
        P010 = 4
    End Enum
End Namespace
