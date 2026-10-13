Option Strict On
Option Explicit On

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Diagnostics surface for an IEncoderBackend. (P1-F §6)
    '''
    ''' Mirrors the IVideoBackendDiagnostics pattern: read-only counters
    ''' that are safe to poll from any thread. Implementations MUST ensure
    ''' counter reads are atomic (use Interlocked.Read for Long, or
    ''' VolatileRead for Integer).
    '''
    ''' Counter semantics:
    '''
    '''   SubmittedFrames        — number of Encode() calls that accepted a frame
    '''   EncodedPackets         — number of EncodedPacket instances emitted
    '''   DroppedFrames          — frames rejected due to backpressure / overflow
    '''   FlushCycles            — number of completed Flush() cycles
    '''   ErrorCount             — number of Encode() / Flush() calls that threw
    '''   LastErrorIfAny         — exception message from most recent failure (empty if none)
    '''   LastErrorType          — "config" / "runtime" / "shutdown" / "" (empty if no error)
    '''
    ''' Invariant (conservation):
    '''   SubmittedFrames = EncodedPackets + DroppedFrames + (frames in-flight)
    '''
    ''' At steady-state (no in-flight frames), the invariant simplifies to:
    '''   SubmittedFrames = EncodedPackets + DroppedFrames
    '''
    ''' Tests SHOULD assert this invariant at end-of-run.
    ''' </summary>
    Public Interface IEncoderDiagnostics
        ''' <summary>Number of Encode() calls that successfully accepted a frame for processing.</summary>
        ReadOnly Property SubmittedFrames As Long

        ''' <summary>Number of EncodedPacket instances emitted to the caller.</summary>
        ReadOnly Property EncodedPackets As Long

        ''' <summary>Number of frames rejected due to backpressure or queue overflow.</summary>
        ReadOnly Property DroppedFrames As Long

        ''' <summary>Number of completed Flush() cycles (each Flush() call counts as 1).</summary>
        ReadOnly Property FlushCycles As Long

        ''' <summary>Number of Encode() / Flush() / Stop() calls that raised an exception.</summary>
        ReadOnly Property ErrorCount As Long

        ''' <summary>
        ''' Message from the most recent exception, or empty string if no errors.
        ''' Used for diagnostics only — callers MUST NOT parse this string to
        ''' make behavioral decisions.
        ''' </summary>
        ReadOnly Property LastErrorIfAny As String

        ''' <summary>
        ''' Type of the most recent error, for programmatic classification.
        ''' Values: "config" (EncoderConfigurationException),
        '''          "runtime" (EncoderRuntimeException),
        '''          "shutdown" (EncoderShutdownException),
        '''          "" (empty — no error yet).
        ''' Callers MAY use this to distinguish error categories.
        ''' </summary>
        ReadOnly Property LastErrorType As String
    End Interface
End Namespace
