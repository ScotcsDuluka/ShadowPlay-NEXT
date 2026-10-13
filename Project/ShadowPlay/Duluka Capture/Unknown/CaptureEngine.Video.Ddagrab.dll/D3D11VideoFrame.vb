Option Strict On
Option Explicit On
Option Infer On

' D3D11VideoFrame.vb
'
' Concrete IVideoFrame implementation wrapping a D3D11 staging texture
' captured by DdagrabBackend via DXGI Output Duplication.
'
' Implements BOTH:
'   - CaptureEngine.Video.IVideoFrame (Foundation contract)
'   - CaptureEngine.Video.ID3D11VideoFrame (Phase 12 extension — exposes
'     NativeTexture pointer for zero-copy GPU encoding)
'
' OWNERSHIP MODEL (verified via handoff contract audit):
'   - D3D11VideoFrame OWNS the staging texture (created fresh per frame).
'   - When TryPush returns Pushed/Replaced: ownership TRANSFERS to sink.
'     Sink disposes the frame (and staging texture) when:
'       - consumer calls Take() and disposes the frame, OR
'       - frame is evicted from the bounded queue (DropOldest), OR
'       - sink.Dispose() runs (drains all queued frames).
'   - When TryPush returns Dropped: backend retains ownership and MUST
'     dispose the frame immediately.
'
' TEXTURE LIFETIME:
'   - The DXGI desktop texture is NOT owned by this frame (it belongs to
'     IDXGIOutputDuplication and is released via ReleaseFrame immediately
'     after CopyResource in DdagrabBackend.WorkerLoop).
'   - The staging texture is owned by this frame — created via
'     device.CreateTexture2D in DdagrabBackend.WorkerLoop, copied from
'     the DXGI desktop texture, then wrapped in D3D11VideoFrame.
'   - Dispose() releases the staging texture via Vortice.
'
' ALLOCATION STRATEGY:
'   - One staging texture per frame. This is a GPU allocation per frame —
'     not ideal for high-FPS capture (could pool staging textures).
'   - Phase 12a uses simple per-frame allocation. If profiling shows this
'     is a bottleneck, Phase 14+ can introduce a staging texture pool
'     (with borrow/return semantics). The interface does not change.
'
' THREAD SAFETY:
'   - Dispose uses Interlocked.CompareExchange — only one caller proceeds.
'   - All property reads return cached values (safe to call after Dispose).
'   - NativeTexture returns IntPtr.Zero after Dispose (defensive).

Imports System.Threading
Imports System.Runtime.InteropServices
Imports Vortice.Direct3D11
Imports Vortice.DXGI
Imports CaptureEngine.Video

Namespace CaptureEngine.Video.Backends.Ddagrab

    ''' <summary>
    ''' D3D11 staging texture wrapper implementing IVideoFrame + ID3D11VideoFrame.
    ''' Owned by DdagrabBackend until transferred to sink (Pushed/Replaced);
    ''' backend disposes immediately on Dropped.
    ''' </summary>
    Public NotInheritable Class D3D11VideoFrame
        Implements IVideoFrame
        Implements ID3D11VideoFrame

        ' ---- Frame metadata (immutable, cached) ----
        Private ReadOnly _origin As VideoFrameOrigin
        Private ReadOnly _pixelFormat As VideoPixelFormat
        Private ReadOnly _dimensions As VideoFrameDimensions
        Private ReadOnly _diagnostics As FrameDiagnostics

        ' ---- Native resource (released on Dispose) ----
        Private ReadOnly _stagingTexture As ID3D11Texture2D
        Private _nativeTextureObj As Object  ' cached ID3D11Texture2D (Object type to avoid contract leak)
        Private ReadOnly _sharedHandle As IntPtr  ' IntPtr.Zero = direct path; non-zero = shared-handle path

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
        End Function

        ' ---- Disposal callback (for metric tracking) ----
        ' When set, called exactly once during Dispose(). Used by DdagrabBackend
        ' to increment _texturesDisposed counter regardless of who disposes the frame
        ' (backend on Dropped, sink on eviction, consumer after Encode).
        Private _onDisposed As Action

        ' ---- Disposal guard (0 = alive, 1 = disposed) ----
        Private _disposedState As Integer = 0

        ''' <summary>
        ''' Construct a D3D11VideoFrame wrapping a staging texture.
        ''' </summary>
        ''' <param name="stagingTexture">D3D11 staging texture (BGRA8, owned by this frame).
        ''' Frame.Dispose() will release this texture via Vortice.</param>
        ''' <param name="width">Texture width in pixels.</param>
        ''' <param name="height">Texture height in pixels.</param>
        ''' <param name="sequence">Monotonic sequence number (from backend's counter).</param>
        ''' <param name="captureTimeTicks">Capture time in 100-ns ticks (P1-B.2 §16.3 Option β).</param>
        ''' <param name="presentationTimestampTicks">PTS in 100-ns ticks (pipeline contract).</param>
        Public Sub New(stagingTexture As ID3D11Texture2D,
                       width As Integer,
                       height As Integer,
                       sequence As Long,
                       captureTimeTicks As Long,
                       presentationTimestampTicks As Long,
                       Optional sharedHandle As IntPtr = Nothing)
            If stagingTexture Is Nothing Then
                Throw New ArgumentNullException(NameOf(stagingTexture))
            End If
            If width <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(width))
            If height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(height))

            _stagingTexture = stagingTexture
            _nativeTextureObj = stagingTexture  ' store the actual Vortice object
            _sharedHandle = sharedHandle  ' IntPtr.Zero for direct path
            _origin = VideoFrameOrigin.GpuD3D11Texture
            _pixelFormat = VideoPixelFormat.Bgra8
            _dimensions = New VideoFrameDimensions(width, height)
            _diagnostics = New FrameDiagnostics(sequence, captureTimeTicks, presentationTimestampTicks)
        End Sub

        ''' <summary>Set a callback to invoke when Dispose() runs (for metric tracking).</summary>
        Public WriteOnly Property OnDisposed As Action
            Set(value As Action)
                _onDisposed = value
            End Set
        End Property

        ' ---- IVideoFrame properties ----

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

        ' ---- ID3D11VideoFrame extension property ----

        Public ReadOnly Property NativeTexture As Object Implements ID3D11VideoFrame.NativeTexture
            Get
                If Volatile.Read(_disposedState) <> 0 Then
                    Return Nothing
                End If
                Return _nativeTextureObj
            End Get
        End Property

        Public ReadOnly Property SharedHandle As IntPtr Implements ID3D11VideoFrame.SharedHandle
            Get
                If Volatile.Read(_disposedState) <> 0 Then
                    Return IntPtr.Zero
                End If
                Return _sharedHandle
            End Get
        End Property

        ' ---- Disposal diagnostics (test-visible) ----

        Public ReadOnly Property IsDisposed As Boolean
            Get
                Return Volatile.Read(_disposedState) <> 0
            End Get
        End Property

        Public ReadOnly Property DisposeCount As Integer
            Get
                ' Always 0 or 1 (CompareExchange guarantees atomic one-shot).
                Return Volatile.Read(_disposedState)
            End Get
        End Property

        ' ---- IDisposable ----

        Public Sub Dispose() Implements IDisposable.Dispose
            ' P1-B.1 FIX pattern: Interlocked.CompareExchange ensures
            ' only ONE caller proceeds, even under concurrent Dispose.
            If Interlocked.CompareExchange(_disposedState, 1, 0) <> 0 Then Return

            ' Release the staging texture. This is a GPU resource release —
            ' Vortice handles the COM Release under the hood.
            Try
                _stagingTexture?.Dispose()
            Catch
                ' Swallow — never throw from Dispose (per P1-B.1 FIX lesson #3).
            End Try

            ' CreateSharedHandle returns an NT HANDLE. The frame owns it for the
            ' lifetime of the shared resource handle and must close it exactly once.
            If _sharedHandle <> IntPtr.Zero Then
                Try
                    CloseHandle(_sharedHandle)
                Catch
                    ' Swallow — never throw from Dispose.
                End Try
            End If

            ' Clear the cached reference (defensive — readers see Nothing).
            _nativeTextureObj = Nothing

            ' Fire disposal callback (for metric tracking).
            ' This lets DdagrabBackend increment _texturesDisposed regardless
            ' of who disposes the frame (backend on Dropped, sink on eviction,
            ' consumer after Encode). Fixes the leak_invariant metric bug.
            Try
                _onDisposed?.Invoke()
            Catch
                ' Swallow — never throw from Dispose.
            End Try
        End Sub

    End Class

End Namespace
