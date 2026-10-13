Option Strict On
Option Explicit On
Option Infer On

Imports System

Namespace CaptureEngine.Video.Frames
    ''' <summary>
    ''' Generic video frame contract — NOT tied to D3D11, DXGI, NVENC, or FFmpeg.
    '''
    ''' This is the P1-D Frame Contract Foundation. It defines the minimal
    ''' interface that ALL frame producers (capture backends, decoders, test
    ''' sources) and consumers (encoders, muxers, test sinks) agree on.
    '''
    ''' Ownership model:
    '''   - Single owner at all times.
    '''   - Owner MUST call Dispose() when done.
    '''   - Properties remain readable after Dispose (return cached values).
    '''   - Dispose is thread-safe and idempotent.
    ''' </summary>
    Public Interface IVideoFrame
        Inherits IDisposable

        ''' <summary>Unique frame identifier (monotonic, assigned by producer).</summary>
        ReadOnly Property FrameId As Long

        ''' <summary>Capture timestamp in 100-nanosecond QPC-derived ticks (P1-B.2 §16.3 Option β).</summary>
        ReadOnly Property Timestamp As Long

        ''' <summary>Frame width in pixels.</summary>
        ReadOnly Property Width As Integer

        ''' <summary>Frame height in pixels.</summary>
        ReadOnly Property Height As Integer

        ''' <summary>Pixel format of the frame data.</summary>
        ReadOnly Property PixelFormat As PixelFormat

        ''' <summary>Frame metadata (timestamps, source, flags).</summary>
        ReadOnly Property Metadata As FrameMetadata

        ''' <summary>
        ''' Opaque resource handle — may be a native pointer, a managed object,
        ''' or Nothing for CPU-only frames. The concrete VideoFrame implementation
        ''' determines what this is. Consumers MUST NOT interpret this value
        ''' without knowing the concrete frame type.
        ''' </summary>
        ReadOnly Property ResourceHandle As IntPtr

        ''' <summary>True if the frame has been disposed.</summary>
        ReadOnly Property IsDisposed As Boolean
    End Interface
End Namespace
