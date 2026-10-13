Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.Backends
    ''' <summary>
    ''' Contract for video capture backends. Replaces direct FFmpeg invocation
    ''' so the Engine can support both FFmpeg-based backends (ddagrab/gdigrab/
    ''' gfxcapture) and future native backends (DXGI Desktop Duplication,
    ''' Windows Graphics Capture, NvFBC).
    '''
    ''' ══════════════════════════════════════════════════════════════════
    ''' PHASE 4 STABILIZATION — RELATIONSHIP WITH FOUNDATION
    ''' ══════════════════════════════════════════════════════════════════
    '''
    ''' Decision: KEEP (with explicit ADAPTER boundary)
    '''
    ''' Foundation (CaptureEngine.Video, frozen @ 82d792ab):
    '''   - IVideoCaptureBackend (push model: Start(sink), no GetFrame)
    '''   - IVideoFrame (enum-based: VideoFrameOrigin, VideoPixelFormat)
    '''   - Sink-owned queue (BoundedVideoFrameSink)
    '''
    ''' New (CaptureEngine.Backends):
    '''   - IVideoBackend (pull model: Start(), GetFrame() → VideoFrame)
    '''   - VideoFrame (string-based: Origin, PixelFormat)
    '''   - Caller-owned polling (no internal queue)
    '''
    ''' Why both?
    '''   - Foundation is FROZEN — cannot modify it
    '''   - Foundation is push-based (sink queue, suitable for high-throughput
    '''     GPU pipeline). Pull-based model is needed for simpler FFmpeg-wrapping
    '''     backends where the caller polls Process output.
    '''   - OWNER explicitly requested IVideoBackend in Phase 5 of original
    '''     mission ("ต้อง compatible กับ CaptureEngine.Video")
    '''
    ''' Adapter boundary (NOT YET IMPLEMENTED — separate future task):
    '''   A `FoundationToIVideoBackendAdapter` class will:
    '''     1. Wrap an IVideoCaptureBackend instance
    '''     2. Internally create a sink queue (BoundedVideoFrameSink)
    '''     3. Implement IVideoBackend.Start() → call wrappedBackend.Start(sink)
    '''     4. Implement IVideoBackend.GetFrame() → take from sink (block with
    '''        bounded timeout, return Nothing on timeout)
    '''     5. Convert enum VideoFrameOrigin/PixelFormat to string properties
    '''     6. Implement IVideoBackend.Stop() → wrappedBackend.Stop()
    '''     7. Implement IVideoBackend.Dispose() → wrappedBackend.Dispose()
    '''
    '''   A regression test MUST be added when the adapter is implemented:
    '''     - Adapter Start → push N frames via fake → caller GetFrame N times
    '''       must return the same N frames in order
    '''     - Adapter Dispose-while-Running must route through Stop (Foundation
    '''       contract P1-B.1 FIX change #1)
    '''
    ''' Until adapter is implemented:
    '''   - IVideoBackend is an empty contract (no concrete implementations)
    '''   - Production Engine/ path does NOT use IVideoBackend (uses legacy
    '''     CaptureEngine.BuildFFmpegArguments directly)
    '''   - This is acceptable: V2 stays opt-in, production stays on V1
    '''
    ''' ══════════════════════════════════════════════════════════════════
    '''
    ''' Lifecycle:
    '''   Create → Start → [GetFrame() repeatedly] → Stop → Dispose
    '''
    '''   Start is idempotent — calling it twice must not throw.
    '''   Stop is idempotent — calling it twice must not throw.
    '''   GetFrame is only valid between Start and Stop.
    '''   Dispose is idempotent — calling it twice must not throw.
    '''
    ''' Threading:
    '''   - Start, Stop, Dispose are thread-safe (may be called from any thread).
    '''   - GetFrame is intended to be called from a dedicated capture thread
    '''     (not the UI thread). It may block briefly waiting for the next
    '''     frame from the source.
    ''' </summary>
    Public Interface IVideoBackend
        Inherits IDisposable

        ''' <summary>Backend lifecycle state.</summary>
        ReadOnly Property CurrentState As VideoBackendState

        ''' <summary>
        ''' Start capturing. Idempotent — calling twice does nothing.
        '''
        ''' Throws InvalidOperationException if called after Dispose.
        ''' Throws backend-specific exceptions on capture init failure
        ''' (e.g. DXGI output not found, FFmpeg binary missing).
        ''' </summary>
        Sub Start()

        ''' <summary>
        ''' Stop capturing. Idempotent — calling twice does nothing.
        '''
        ''' Stop signals the producer to halt, drains any in-flight frames
        ''' (sink-owned), and finalizes the backend's capture session.
        '''
        ''' Per P1-A v1.3.1 §4 + P1-B.1 FIX change #2: Stop stops the producer;
        ''' queued frames remain sink-owned; backend does NOT require unbounded
        ''' drain during Stop.
        '''
        ''' NOTE: Method name is bracketed as [Stop] because 'Stop' is a VB.NET
        ''' reserved keyword (suspends execution). The brackets escape it as an
        ''' identifier — callers invoke as backend.Stop() (no brackets needed at
        ''' call site). This matches the Foundation's IVideoCaptureBackend.vb
        ''' pattern (also uses Sub [Stop]()).
        ''' </summary>
        Sub [Stop]()

        ''' <summary>
        ''' Try to get the next captured frame.
        '''
        ''' Returns:
        '''   - VideoFrame with valid data if a frame is available
        '''   - Nothing if no frame is available (caller should retry or yield)
        '''
        ''' Throws InvalidOperationException if called before Start or after Stop.
        ''' Throws ObjectDisposedException if called after Dispose.
        '''
        ''' Implementation note: GetFrame should NOT block indefinitely.
        ''' Callers polling in a tight loop should add their own sleep/yield
        ''' between calls (the NoFrame case is the backend's signal that
        ''' no work is ready yet, per P1-A v1.3.1 §6.4).
        ''' </summary>
        Function GetFrame() As VideoFrame

        ''' <summary>
        ''' Get backend-specific diagnostics counters (frames emitted,
        ''' frames dropped, errors, etc.). Read-only snapshot.
        '''
        ''' Returns a snapshot dictionary. Never throws.
        ''' </summary>
        Function GetDiagnostics() As IReadOnlyDictionary(Of String, Long)
    End Interface

    ''' <summary>Backend lifecycle state.</summary>
    Public Enum VideoBackendState
        ''' <summary>Created but not started.</summary>
        Created

        ''' <summary>Start() in progress (capture init).</summary>
        Starting

        ''' <summary>Capturing frames.</summary>
        Running

        ''' <summary>Stop() in progress — FFmpeg shutdown + audio stop.</summary>
        Stopping

        ''' <summary>Muxing in progress — combining video + audio into final output.</summary>
        Muxing

        ''' <summary>Stopped — can be started again.</summary>
        Stopped

        ''' <summary>Permanently disposed — cannot be restarted.</summary>
        Disposed

        ''' <summary>Backend failed (capture init error, device lost, mux failure, etc.).</summary>
        Faulted
    End Enum

    ''' <summary>
    ''' Captured video frame — opaque handle to a single frame's worth of data.
    '''
    ''' Lightweight value type. Concrete backends (FFmpegBackend, D3D11Backend)
    ''' will provide their own concrete implementations; this interface allows
    ''' the pipeline layer to consume frames without knowing the underlying
    ''' format (BGRA8 texture, NV12 surface, raw byte buffer, etc.).
    '''
    ''' Frame ownership:
    '''   - Caller receives a VideoFrame reference
    '''   - Caller MUST call Dispose() when done (frames may hold GPU resources)
    '''   - Caller MUST NOT hold frame references across Stop() boundaries
    ''' </summary>
    Public Interface VideoFrame
        Inherits IDisposable

        ''' <summary>Frame width in pixels.</summary>
        ReadOnly Property Width As Integer

        ''' <summary>Frame height in pixels.</summary>
        ReadOnly Property Height As Integer

        ''' <summary>Frame timestamp in 100-nanosecond QPC-derived ticks.</summary>
        ReadOnly Property TimestampTicks As Long

        ''' <summary>Origin: "ddagrab" | "gdigrab" | "gfxcapture" | "dxgi" | "wgc".</summary>
        ReadOnly Property Origin As String

        ''' <summary>Pixel format: "bgra8" | "nv12" | "yuv420p" | etc.</summary>
        ReadOnly Property PixelFormat As String
    End Interface
End Namespace
