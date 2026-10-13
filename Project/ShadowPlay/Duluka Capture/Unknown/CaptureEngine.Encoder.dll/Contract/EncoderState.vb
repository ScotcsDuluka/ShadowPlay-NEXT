Option Strict On
Option Explicit On

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Lifecycle state of an IEncoderBackend. (P1-F §3)
    '''
    ''' Allowed transitions:
    '''
    '''   Created ──Initialize()──► Initialized
    '''   Initialized ──Start()──► Running
    '''   Running ──Encode()──► Running              (steady state)
    '''   Running ──Flush()──► Flushing
    '''   Flushing ──(flush complete)──► Running      (Flush may be called repeatedly)
    '''   Running / Flushing ──Stop()──► Stopping ──► Stopped
    '''   Stopped ──Start()──► Running                (restart allowed)
    '''   Any state ──Dispose()──► Disposed
    '''   Any state ──(failure)──► Faulted
    '''   Faulted ──Dispose()──► Disposed             (Faulted is terminal until Dispose)
    '''
    ''' Forbidden transitions:
    '''   Disposed ──► any state                     (must create new instance)
    '''   Faulted ──► Running / Initialized          (must Dispose + recreate)
    '''
    ''' Thread-safety: state reads are observable via CurrentState. State
    ''' mutations MUST be performed under the encoder's _sync lock (see
    ''' IEncoderBackend implementer contract). Reads from other threads
    ''' MUST use Volatile.Read to ensure visibility.
    ''' </summary>
    Public Enum EncoderState
        ''' <summary>Constructed; Initialize() not yet called.</summary>
        Created

        ''' <summary>Initialize() completed successfully; ready to Start().</summary>
        Initialized

        ''' <summary>Start() completed; encoder is actively processing Encode() calls.</summary>
        Running

        ''' <summary>Flush() in progress; encoder is draining in-flight frames.</summary>
        Flushing

        ''' <summary>Stop() in progress; encoder is draining + shutting down worker.</summary>
        Stopping

        ''' <summary>Stop() completed; encoder is idle, can be restarted via Start().</summary>
        Stopped

        ''' <summary>Unrecoverable failure; encoder must be Dispose()'d.</summary>
        Faulted

        ''' <summary>Dispose() completed; all resources released. Terminal state.</summary>
        Disposed
    End Enum
End Namespace
