Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Video.Frames
    ''' <summary>
    ''' Concrete VideoFrame implementation with ownership model.
    '''
    ''' Lifecycle: Created → Active → Disposed
    '''
    ''' Features:
    '''   - Implements IVideoFrame (generic contract, no D3D11/DXGI/NVENC ties)
    '''   - Dispose is thread-safe (Interlocked.Exchange guard)
    '''   - Dispose is idempotent (calling twice does not crash)
    '''   - Properties remain readable after Dispose (return cached values)
    '''   - Supports a resource cleanup callback (invoked once on first Dispose)
    '''   - IsDisposed property for checking state without try/catch
    '''
    ''' Thread safety:
    '''   - Dispose: thread-safe via Interlocked.CompareExchange
    '''   - Property reads: safe before and after Dispose (no lock needed;
    '''     values are immutable after construction)
    '''   - No locks on heavy operations — Dispose callback is called outside
    '''     any lock (it may do GPU release, native free, etc.)
    ''' </summary>
    Public NotInheritable Class VideoFrame
        Implements IVideoFrame

        ' ── Immutable properties (set at construction, never change) ──
        Private ReadOnly _frameId As Long
        Private ReadOnly _timestamp As Long
        Private ReadOnly _width As Integer
        Private ReadOnly _height As Integer
        Private ReadOnly _pixelFormat As PixelFormat
        Private ReadOnly _metadata As FrameMetadata
        Private ReadOnly _resourceHandle As IntPtr

        ' ── Dispose state ──
        ' 0 = not disposed (Active), 1 = disposed
        Private _disposed As Integer = 0

        ' ── Resource cleanup callback (invoked once on first Dispose) ──
        Private ReadOnly _cleanupCallback As Action(Of VideoFrame)

        ''' <summary>
        ''' Create a VideoFrame.
        ''' </summary>
        ''' <param name="frameId">Monotonic frame ID from the producer.</param>
        ''' <param name="timestamp">Capture timestamp (100-ns QPC ticks).</param>
        ''' <param name="width">Frame width in pixels.</param>
        ''' <param name="height">Frame height in pixels.</param>
        ''' <param name="pixelFormat">Pixel format of the frame data.</param>
        ''' <param name="metadata">Frame metadata (timestamps, source, flags).</param>
        ''' <param name="resourceHandle">Opaque handle to native/managed resource (IntPtr.Zero for CPU-only).</param>
        ''' <param name="cleanupCallback">Optional callback invoked once on first Dispose. Use to release GPU/native resources.</param>
        Public Sub New(frameId As Long,
                       timestamp As Long,
                       width As Integer,
                       height As Integer,
                       pixelFormat As PixelFormat,
                       metadata As FrameMetadata,
                       Optional resourceHandle As IntPtr = Nothing,
                       Optional cleanupCallback As Action(Of VideoFrame) = Nothing)
            If width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(width), "Width must be positive.")
            If height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(height), "Height must be positive.")

            _frameId = frameId
            _timestamp = timestamp
            _width = width
            _height = height
            _pixelFormat = pixelFormat
            _metadata = metadata
            _resourceHandle = resourceHandle
            _cleanupCallback = cleanupCallback
        End Sub

        ' ===== IVideoFrame properties (all reads are safe after Dispose) =====

        Public ReadOnly Property FrameId As Long Implements IVideoFrame.FrameId
            Get
                Return _frameId
            End Get
        End Property

        Public ReadOnly Property Timestamp As Long Implements IVideoFrame.Timestamp
            Get
                Return _timestamp
            End Get
        End Property

        Public ReadOnly Property Width As Integer Implements IVideoFrame.Width
            Get
                Return _width
            End Get
        End Property

        Public ReadOnly Property Height As Integer Implements IVideoFrame.Height
            Get
                Return _height
            End Get
        End Property

        Public ReadOnly Property PixelFormat As PixelFormat Implements IVideoFrame.PixelFormat
            Get
                Return _pixelFormat
            End Get
        End Property

        Public ReadOnly Property Metadata As FrameMetadata Implements IVideoFrame.Metadata
            Get
                Return _metadata
            End Get
        End Property

        Public ReadOnly Property ResourceHandle As IntPtr Implements IVideoFrame.ResourceHandle
            Get
                Return _resourceHandle
            End Get
        End Property

        Public ReadOnly Property IsDisposed As Boolean Implements IVideoFrame.IsDisposed
            Get
                Return Volatile.Read(_disposed) = 1
            End Get
        End Property

        ' ===== IDisposable =====

        ''' <summary>
        ''' Dispose the frame. Thread-safe and idempotent.
        ''' First call invokes the cleanup callback (if provided).
        ''' Subsequent calls are no-ops.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            ' Thread-safe: only the first caller proceeds past this guard.
            If Interlocked.CompareExchange(_disposed, 1, 0) <> 0 Then Return

            ' Invoke cleanup callback OUTSIDE any lock — it may do GPU/native
            ' resource release which can be slow or block.
            If _cleanupCallback IsNot Nothing Then
                Try
                    _cleanupCallback(Me)
                Catch
                    ' Swallow — cleanup callback failure must not crash the caller.
                    ' The frame is already marked as disposed.
                End Try
            End If
        End Sub
    End Class
End Namespace
