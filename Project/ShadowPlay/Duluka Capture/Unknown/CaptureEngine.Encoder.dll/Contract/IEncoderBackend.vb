Option Strict On
Option Explicit On

Imports System
Imports CaptureEngine.Video

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Contract for a video encoder backend. (P1-F §2)
    '''
    ''' Pipeline position:
    '''
    '''   IVideoFrame (from IVideoCaptureBackend / VideoLayer)
    '''       ↓
    '''   IEncoderBackend.Encode(frame) — accepts the frame, encodes synchronously
    '''       ↓
    '''   EncodedPacket (returned to caller — caller-owned)
    '''       ↓
    '''   Output / Muxer / Network
    '''
    ''' DESIGN PRINCIPLES (inherited from Foundation P1-A v1.3.1):
    '''
    '''   1. SINGLE-OWNER FRAME: each IVideoFrame passed to Encode() is BORROWED.
    '''      The encoder MUST NOT dispose the frame — the caller (VideoLayer
    '''      or its sink) retains ownership and disposes it after Encode() returns.
    '''
    '''   2. SYNCHRONOUS ENCODE: Encode() blocks until the packet is ready OR
    '''      returns False to signal backpressure (queue full). The packet is
    '''      delivered via the out-parameter — no async callback complexity.
    '''      Rationale: simplest model for Phase 1; matches Foundation's
    '''      synchronous TryPush pattern on IVideoFrameSink.
    '''
    '''   3. ENCODEDPACKET IS CALLER-OWNED: when Encode() produces a packet,
    '''      ownership transfers to the caller. The caller MUST Dispose()
    '''      the packet when done.
    '''
    '''   4. IDEMPOTENT DISPOSE: Dispose() can be called multiple times.
    '''      Heavy operations (worker thread Join, resource release) happen
    '''      OUTSIDE the _sync lock (per P1-B.1 FIX lesson #1).
    '''
    '''   5. FAULTED IS TERMINAL: once an encoder enters Faulted state,
    '''      only Dispose() is valid. Caller must create a new instance.
    '''
    '''   6. NO IMPLICIT STATE CARRIAGE: the encoder does NOT remember
    '''      the previous frame's timestamp / dimensions. Each Encode()
    '''      call is independent — callers MUST pass a complete IVideoFrame.
    '''
    ''' CONSTRAINTS (Phase 1):
    '''   - NO NVENC-specific API leak in the public contract.
    '''     (e.g. no nvEncodeAPI types, no NV_ENC_* enums, no DllImport)
    '''   - NO D3D11 texture references in the public contract.
    '''     The contract accepts IVideoFrame — implementations decide
    '''     whether to read frame.Origin == GpuD3D11Texture or CpuMemory.
    '''   - NO FFmpeg dependency. Future FFmpeg-wrapping encoders live
    '''     in separate projects that depend on THIS contract.
    '''   - NO UI dependency. The encoder is headless.
    '''
    ''' THREAD MODEL:
    '''   - _sync protects: _state, _disposed, counter writes
    '''   - _sync does NOT protect: worker.Join, encoding, packet delivery
    '''   - CurrentState: implementations MUST use Volatile.Read
    '''   - Counters: implementations MUST use Interlocked.Read + Interlocked.Increment
    '''   - Heavy operations (Join) MUST happen OUTSIDE _sync lock
    ''' </summary>
    Public Interface IEncoderBackend
        Inherits IDisposable

        ' ===== lifecycle =====

        ''' <summary>
        ''' Initialize the encoder with the supplied configuration.
        ''' Only valid in the Created state. Transitions to Initialized on success,
        ''' Faulted on failure.
        '''
        ''' Validation timing:
        '''   - Static config validation (CodecKey, BitrateBps, GopSize, etc.)
        '''     happens here at Initialize time — fail fast.
        '''   - Per-frame validation (Dimensions, PixelFormat, Origin) happens
        '''     at Encode time — frames may vary.
        ''' </summary>
        ''' <param name="config">Encoder configuration. The encoder takes a COPY (config.Clone()).</param>
        ''' <exception cref="ArgumentNullException">config is Nothing.</exception>
        ''' <exception cref="EncoderConfigurationException">
        ''' Thrown when config values are invalid (e.g. CodecKey unknown,
        ''' BitrateBps <= 0 with RateControl=cbr, etc.).
        ''' </exception>
        ''' <exception cref="InvalidOperationException">
        ''' Thrown when Initialize() is called from a state other than Created.
        ''' </exception>
        Sub Initialize(config As EncoderConfig)

        ''' <summary>
        ''' Start the encoder. Only valid in the Initialized or Stopped state.
        ''' Transitions to Running on success.
        '''
        ''' Idempotent contract: calling Start() while already Running is a
        ''' no-op that preserves the current state (no exception).
        ''' </summary>
        ''' <exception cref="InvalidOperationException">
        ''' Thrown when Start() is called from Created, Flushing, Stopping, or Faulted.
        ''' </exception>
        ''' <exception cref="ObjectDisposedException">Encoder has been disposed.</exception>
        Sub Start()

        ''' <summary>
        ''' Encode a single IVideoFrame.
        '''
        ''' The encoder BORROWS the frame — it MUST NOT call frame.Dispose().
        ''' The caller retains ownership and is responsible for disposal.
        '''
        ''' This method is SYNCHRONOUS — it blocks until the packet is ready.
        ''' If the encoder's internal queue is full, the method returns False
        ''' immediately without raising an exception — the caller applies
        ''' backpressure (drop or retry).
        '''
        ''' When a packet is produced, it is delivered via <paramref name="packet"/>
        ''' and ownership TRANSFERS TO THE CALLER. The caller MUST Dispose()
        ''' the packet when done.
        '''
        ''' Note: 1 input frame may produce 0, 1, or multiple output packets
        ''' depending on the codec. For Phase 1 (FakeEncoderBackend), the
        ''' contract is 1 frame → 1 packet (synchronous). Real NVENC may
        ''' buffer multiple frames before emitting a packet (encoder delay).
        ''' In that case, Encode() returns False (no packet yet) until the
        ''' encoder's pipeline produces one. Callers MUST handle the
        ''' "False but frame accepted" case.
        ''' </summary>
        ''' <param name="frame">Input frame. Must not be Nothing. Must not be disposed.</param>
        ''' <param name="packet">
        ''' When this method returns True, contains the encoded packet (caller-owned).
        ''' When this method returns False, contains Nothing (no packet produced yet —
        ''' either backpressure OR encoder pipeline delay).
        ''' </param>
        ''' <returns>
        ''' True if a packet was produced. False if no packet (backpressure or pipeline delay).
        ''' </returns>
        ''' <exception cref="ArgumentNullException">frame is Nothing.</exception>
        ''' <exception cref="ArgumentException">frame has been disposed (frame.IsDisposed).</exception>
        ''' <exception cref="EncoderRuntimeException">
        ''' Thrown when the encoder encounters a runtime failure (e.g. codec
        ''' error, frame dimension mismatch, GPU device lost). Transitions
        ''' the encoder to Faulted.
        ''' </exception>
        ''' <exception cref="ObjectDisposedException">Encoder has been disposed.</exception>
        ''' <exception cref="InvalidOperationException">
        ''' Thrown when Encode() is called from a state other than Running.
        ''' </exception>
        Function Encode(frame As IVideoFrame, ByRef packet As EncodedPacket) As Boolean

        ''' <summary>
        ''' Flush in-flight frames. Drains the encoder's internal pipeline,
        ''' emitting any pending EncodedPacket instances via the supplied sink.
        '''
        ''' Transitions to Flushing on entry, returns to Running when complete.
        '''
        ''' Bounded time: Flush() MUST complete within
        ''' EncoderConfig.FlushTimeoutMs. If timeout expires, raises
        ''' EncoderShutdownException.
        '''
        ''' Sink contract:
        '''   - Sink receives ownership of each EncodedPacket passed to it.
        '''   - Sink MUST call packet.Dispose() after consuming (or storing) the packet.
        '''   - Sink MUST NOT retain packet.Payload reference beyond Dispose.
        '''   - Sink MUST NOT throw — if it does, encoder transitions to Faulted.
        ''' </summary>
        ''' <param name="sink">
        ''' Callback invoked for each flushed EncodedPacket. The sink receives
        ''' ownership of the packet — the sink MUST Dispose() it.
        ''' </param>
        ''' <returns>Number of packets delivered to sink.</returns>
        ''' <exception cref="ArgumentNullException">sink is Nothing.</exception>
        ''' <exception cref="ObjectDisposedException">Encoder has been disposed.</exception>
        ''' <exception cref="InvalidOperationException">
        ''' Thrown when Flush() is called from a state other than Running.
        ''' </exception>
        ''' <exception cref="EncoderShutdownException">
        ''' Thrown when Flush() does not complete within FlushTimeoutMs.
        ''' </exception>
        Function Flush(sink As Action(Of EncodedPacket)) As Integer

        ''' <summary>
        ''' Stop the encoder. Drains in-flight frames via Flush() (with
        ''' bounded time), then transitions to Stopping → Stopped.
        '''
        ''' Per-state behavior (v1.1 explicit matrix):
        '''   Created      → no-op, state unchanged
        '''   Initialized  → no-op, state unchanged
        '''   Running      → drain via Flush, transition Stopping → Stopped
        '''   Stopping     → no-op (already stopping)
        '''   Flushing     → interrupt flush, transition Stopping → Stopped
        '''   Stopped      → no-op, state unchanged
        '''   Faulted      → no-op, state unchanged (Faulted terminal until Dispose)
        '''   Disposed     → ObjectDisposedException
        '''
        ''' Idempotent: calling Stop() when not Running is a no-op (except Disposed).
        '''
        ''' Bounded time: Stop() MUST complete within
        ''' EncoderConfig.StopTimeoutMs.
        ''' </summary>
        ''' <exception cref="ObjectDisposedException">Encoder has been disposed.</exception>
        ''' <exception cref="EncoderShutdownException">
        ''' Thrown when Stop() does not complete within StopTimeoutMs.
        ''' </exception>
        Sub [Stop]()

        ' ===== diagnostics =====

        ''' <summary>Current lifecycle state. Safe to read from any thread.</summary>
        ReadOnly Property CurrentState As EncoderState

        ''' <summary>Diagnostics surface (counters). Safe to poll from any thread.</summary>
        ReadOnly Property Diagnostics As IEncoderDiagnostics
    End Interface
End Namespace
