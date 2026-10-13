Option Strict On
Option Explicit On

Imports System.Threading

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' A single encoded video packet (one access unit / one AVPacket equivalent).
    ''' (P1-F §5.1)
    '''
    ''' OWNERSHIP MODEL (critical — read before modifying):
    '''
    '''   1. EncodedPacket is owned by EXACTLY ONE caller at a time.
    '''   2. The caller receives ownership from IEncoderBackend.Encode() return value
    '''      OR from Flush(sink) callback.
    '''   3. The owner MUST call Dispose() when done — this releases the
    '''      underlying byte[] buffer back to the encoder's pool (future)
    '''      OR leaves it for GC (current default).
    '''   4. After Dispose(), accessing Payload OR Metadata is UNDEFINED
    '''      BEHAVIOR — implementations MUST treat the packet as invalid.
    '''   5. Dispose() is IDEMPOTENT — calling it more than once is safe
    '''      (per P1-B.1 FIX lesson #3: never throw from Dispose).
    '''   6. Dispose() is THREAD-SAFE — uses Interlocked.Increment to
    '''      atomically update _disposeCount (fixes non-atomic read-modify-write
    '''      race that was present in v1.0).
    '''
    ''' DATA OWNERSHIP:
    '''   - Payload (byte[]): owned by this packet. The byte[] is allocated
    '''     by the encoder; the packet is the sole owner. Disposing the
    '''     packet releases the reference; GC reclaims the array.
    '''   - In a future pooled-buffer variant, Dispose() would return the
    '''     byte[] to the pool. This contract accommodates that — callers
    '''     MUST NOT retain references to Payload beyond Dispose().
    '''
    ''' THREADING:
    '''   - Encode() is called from the encoder's worker thread (or caller thread
    '''     for synchronous encoders).
    '''   - Dispose() may be called from a different thread (e.g. the muxer thread).
    '''   - Interlocked.Increment ensures the _disposeCount update is atomic
    '''     across threads, fixing the race condition present in v1.0
    '''     (which used non-atomic VolatileRead+VolatileWrite).
    ''' </summary>
    Public NotInheritable Class EncodedPacket
        Implements IDisposable

        Private ReadOnly _metadata As PacketMetadata
        Private ReadOnly _payload As Byte()
        Private ReadOnly _payloadLength As Integer

        ' Atomic counter. 0 = not disposed. >0 = disposed (call count tracked
        ' so tests can detect double-dispose bugs, mirroring FakeVideoFrame pattern).
        ' v1.1 FIX: use Interlocked.Increment instead of non-atomic VolatileRead+VolatileWrite.
        Private _disposeCount As Integer = 0

        ''' <summary>
        ''' Construct an EncodedPacket.
        ''' </summary>
        ''' <param name="metadata">Per-packet metadata (PTS, DTS, keyframe flag, etc.).</param>
        ''' <param name="payload">Encoded byte payload. The packet takes ownership — caller MUST NOT retain the reference.</param>
        ''' <param name="payloadLength">Number of valid bytes in <paramref name="payload"/>. Must be >= 0 and <= payload.Length.</param>
        ''' <exception cref="ArgumentNullException">payload is Nothing.</exception>
        ''' <exception cref="ArgumentOutOfRangeException">payloadLength is negative or exceeds payload.Length.</exception>
        Public Sub New(metadata As PacketMetadata, payload As Byte(), payloadLength As Integer)
            If payload Is Nothing Then
                Throw New ArgumentNullException(NameOf(payload))
            End If
            If payloadLength < 0 OrElse payloadLength > payload.Length Then
                Throw New ArgumentOutOfRangeException(NameOf(payloadLength),
                    "payloadLength must be in [0, payload.Length].")
            End If
            _metadata = metadata
            _payload = payload
            _payloadLength = payloadLength
        End Sub

        ''' <summary>Per-packet metadata. Valid until Dispose() is called.</summary>
        Public ReadOnly Property Metadata As PacketMetadata
            Get
                Return _metadata
            End Get
        End Property

        ''' <summary>
        ''' Encoded byte payload. The caller MUST NOT retain a reference to
        ''' this array beyond the lifetime of the packet (Dispose() releases it).
        ''' </summary>
        Public ReadOnly Property Payload As Byte()
            Get
                Return _payload
            End Get
        End Property

        ''' <summary>Number of valid bytes in Payload.</summary>
        Public ReadOnly Property PayloadLength As Integer
            Get
                Return _payloadLength
            End Get
        End Property

        ''' <summary>True if this packet has been disposed. Safe to call from any thread.</summary>
        Public ReadOnly Property IsDisposed As Boolean
            Get
                Return Volatile.Read(_disposeCount) > 0
            End Get
        End Property

        ''' <summary>
        ''' Number of times Dispose() has been called. MUST be exactly 1 over
        ''' the packet's lifetime; >1 indicates a double-dispose bug, 0 indicates
        ''' a leak. Used by ownership/lifetime tests.
        ''' </summary>
        Public ReadOnly Property DisposeCount As Integer
            Get
                Return Volatile.Read(_disposeCount)
            End Get
        End Property

        ''' <summary>
        ''' Release the payload. Idempotent — safe to call multiple times.
        ''' After Dispose(), accessing Payload or Metadata is undefined behavior.
        '''
        ''' v1.1 FIX: uses Interlocked.Increment for atomic counter update.
        ''' Previous v1.0 used VolatileRead+VolatileWrite (non-atomic RMW)
        ''' which could lose updates under concurrent Dispose calls.
        ''' </summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            ' Atomically increment the dispose counter. Interlocked.Increment
            ' guarantees the update is atomic across threads, fixing the
            ' non-atomic read-modify-write race present in v1.0.
            '
            ' We do NOT clear the _payload reference here — that would race
            ' with concurrent reads on Payload. Instead, the IsDisposed flag
            ' signals to callers that the data is no longer valid. GC will
            ' reclaim the array once all references are dropped.
            '
            ' Future pooled-buffer variant: replace the body with
            '     Dim wasFirst As Boolean = (Interlocked.CompareExchange(_disposeCount, 1, 0) = 0)
            '     If wasFirst Then BufferPool.Return(_payload)
            Interlocked.Increment(_disposeCount)
        End Sub
    End Class
End Namespace
