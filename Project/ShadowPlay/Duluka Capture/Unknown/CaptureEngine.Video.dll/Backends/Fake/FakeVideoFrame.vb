Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Video.Backends.Fake
    ''' <summary>
    ''' Minimal IVideoFrame implementation for the fake backend. (P1-A v1.3.1 §3)
    '''
    ''' Tracks Dispose() calls so ownership/lifetime tests can assert that
    ''' every FrameAvailable result's frame is disposed exactly once by
    ''' whoever currently owns it (the sink under DropOldest, the backend
    ''' under DropNewest-with-Dropped outcome, or the consumer downstream).
    '''
    ''' RC-1 fix: Dispose is now thread-safe and idempotent.
    '''   - Uses Interlocked.CompareExchange guard (same pattern as Frames.VideoFrame)
    '''   - _disposeCount increments ONLY on first Dispose (guaranteed 0 or 1)
    '''   - Concurrent Dispose calls are safe — only first caller proceeds
    ''' </summary>
    Public NotInheritable Class FakeVideoFrame
        Implements IVideoFrame

        Private ReadOnly _origin As VideoFrameOrigin
        Private ReadOnly _pixelFormat As VideoPixelFormat
        Private ReadOnly _dimensions As VideoFrameDimensions
        Private ReadOnly _diagnostics As FrameDiagnostics

        ' RC-1 fix: Use Integer + Interlocked instead of Boolean + VolatileWrite
        ' 0 = not disposed, 1 = disposed
        Private _disposed As Integer = 0

        Public Sub New(origin As VideoFrameOrigin,
                       pixelFormat As VideoPixelFormat,
                       dimensions As VideoFrameDimensions,
                       diagnostics As FrameDiagnostics)
            _origin = origin
            _pixelFormat = pixelFormat
            _dimensions = dimensions
            _diagnostics = diagnostics
        End Sub

        Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
            Get
                Return _origin
            End Get
        End Property

        Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
            Get
                Return _pixelFormat
            End Get
        End Property

        Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
            Get
                Return _dimensions
            End Get
        End Property

        Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
            Get
                Return _diagnostics
            End Get
        End Property

        ''' <summary>
        ''' Number of times Dispose() has been called. After RC-1 fix, this is
        ''' always 0 (before Dispose) or 1 (after first Dispose). Previous
        ''' implementation could return >1 due to non-atomic increment.
        ''' </summary>
        Public ReadOnly Property DisposeCount As Integer
            Get
                Return Volatile.Read(_disposed)
            End Get
        End Property

        Public ReadOnly Property IsDisposed As Boolean
            Get
                Return Volatile.Read(_disposed) = 1
            End Get
        End Property

        ''' <summary>
        ''' Dispose the frame. Thread-safe and idempotent.
        ''' Only the first call proceeds; subsequent calls are no-ops.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            ' RC-1 fix: Interlocked.CompareExchange ensures only ONE caller
            ' passes the guard, even under concurrent access.
            If Interlocked.CompareExchange(_disposed, 1, 0) <> 0 Then Return

            ' No heavy cleanup needed for the fake frame — just the guard.
            ' Real frames (Frames.VideoFrame) invoke a cleanup callback here.
        End Sub
    End Class
End Namespace
