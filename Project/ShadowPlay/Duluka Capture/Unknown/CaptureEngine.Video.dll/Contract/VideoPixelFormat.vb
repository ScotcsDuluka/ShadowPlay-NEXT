Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Pixel format of an IVideoFrame. (P1-A v1.3.1 §3.4)
    '''
    ''' Phase 1 baseline REQUIRES Bgra8 (§15.1). HDR/10-bit formats are
    ''' explicitly deferred (§15.3). Backends MUST emit Bgra8 in Phase 1;
    ''' any other value causes Initialize() to throw
    ''' VideoBackendConfigurationException.
    ''' </summary>
    Public Enum VideoPixelFormat
        ''' <summary>32 bpp, byte order B,G,R,A. Phase 1 baseline requirement.</summary>
        Bgra8
        ''' <summary>32 bpp, byte order R,G,B,A. Reserved for future use; backends MUST NOT emit in P1-B.</summary>
        Rgba8
        ''' <summary>Semi-planar 4:2:0. NVENC-native input. Reserved for Encoder-side conversion.</summary>
        Nv12
        ''' <summary>Backend could not determine or non-BGRA8 format. Downstream MUST refuse.</summary>
        Unknown
    End Enum
End Namespace
