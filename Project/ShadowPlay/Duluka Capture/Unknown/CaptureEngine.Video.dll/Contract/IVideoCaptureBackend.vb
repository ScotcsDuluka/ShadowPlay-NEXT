Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' A video capture backend. (P1-A v1.3.1 §4.1, §11)
    '''
    ''' Implemented by DdagrabBackend, GfxCaptureBackend (future), and
    ''' FakeVideoCaptureBackend (P1-B.1).
    ''' </summary>
    Public Interface IVideoCaptureBackend
        Inherits IDisposable

        ''' <summary>
        ''' Back-end initialization. See §5 for device-ownership behaviour.
        ''' </summary>
        ''' <exception cref="VideoBackendConfigurationException">Thrown when config values are invalid for this backend.</exception>
        ''' <exception cref="VideoBackendRuntimeException">Thrown when backend-specific native resources cannot be created.</exception>
        Sub Initialize(context As IVideoBackendContext)

        ''' <summary>
        ''' Begin emitting FrameAcquisitionResult values into the sink. Returns
        ''' when the backend has started its capture thread/poll loop.
        ''' </summary>
        Sub Start(sink As IVideoFrameSink)

        ''' <summary>
        ''' Stop emitting NEW results. (P1-A v1.3.1 §4 + P1-B.1 FIX change #2.)
        '''
        ''' Contract:
        '''   - Stop stops the producer (the backend's worker thread / capture
        '''     loop / frame-pool subscription). After Stop returns, the backend
        '''     MUST NOT push any NEW results into the sink.
        '''   - Any results ALREADY pushed to the sink (and still queued inside
        '''     it) remain OWNED BY THE SINK. The sink is responsible for
        '''     disposing their wrapped frames (on sink eviction / sink Dispose
        '''     / consumer Take).
        '''   - The backend MUST NOT require an unbounded drain of the sink's
        '''     queue during Stop. The backend may flush its own internal
        '''     in-flight buffers (bounded), but it does NOT wait for the
        '''     downstream consumer to dequeue every queued result.
        '''   - Stop must return within a bounded time (the Foundation's
        '''     existing 2-second budget). If the worker does not acknowledge,
        '''     the backend logs an error and proceeds to the Stopped state.
        ''' </summary>
        Sub [Stop]()

        ' IDisposable.Dispose inherited — idempotent, robust, never throws.

        ''' <summary>
        ''' Read-only diagnostics surface (§3.2). Exposed for the entire
        ''' lifetime of the backend; counters may be polled from any thread.
        ''' </summary>
        ReadOnly Property Diagnostics As IVideoBackendDiagnostics
    End Interface
End Namespace
