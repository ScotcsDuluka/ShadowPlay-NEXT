Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Threading

Namespace CaptureEngine.Video.Backends.Ddagrab
    ''' <summary>
    ''' DdagrabFrame — placeholder IVideoFrame implementation for the
    ''' DdagrabBackend. (P1-A v1.3.1 §3, §11)
    '''
    ''' GLM-1 SKELETON STATUS:
    ''' This class exists so that when real DXGI Output Duplication is
    ''' implemented, the worker loop can construct DdagrabFrame instances
    ''' wrapping the captured ID3D11Texture2D and push them to the sink
    ''' via FrameAcquisitionResult.Available(...).
    '''
    ''' Currently NOT emitted by the skeleton worker (which emits NoFrame
    ''' forever). The class is included here so:
    '''   - The future real-implementation TODO has a concrete target type.
    '''   - Tests can construct DdagrabFrame instances directly to verify
    '''     the IVideoFrame contract surface (origin / pixel format /
    '''     dimensions / diagnostics / IDisposable).
    '''
    ''' Constraints honored:
    '''   - Implements the same IVideoFrame contract as FakeVideoFrame.
    '''   - Tracks DisposeCount for ownership/lifetime tests.
    '''   - No GPU resources held (placeholder).
    ''' </summary>
    Public NotInheritable Class DdagrabFrame
        Implements IVideoFrame

        Private ReadOnly _origin As VideoFrameOrigin
        Private ReadOnly _pixelFormat As VideoPixelFormat
        Private ReadOnly _dimensions As VideoFrameDimensions
        Private ReadOnly _diagnostics As FrameDiagnostics

        Private _disposeCount As Integer = 0

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
        ''' Number of times Dispose() has been called. MUST be exactly 1 over
        ''' the frame's lifetime; >1 indicates double-dispose, 0 indicates
        ''' a leak. Used by ownership/lifetime tests.
        ''' </summary>
        Public ReadOnly Property DisposeCount As Integer
            Get
                Return Volatile.Read(_disposeCount)
            End Get
        End Property

        Public ReadOnly Property IsDisposed As Boolean
            Get
                Return Volatile.Read(_disposeCount) > 0
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            Volatile.Write(_disposeCount, Volatile.Read(_disposeCount) + 1)
        End Sub
    End Class
End Namespace
