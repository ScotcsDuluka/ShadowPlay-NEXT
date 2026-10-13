Option Strict On
Option Explicit On
Option Infer On

Namespace CaptureEngine.Engine
    ''' <summary>
    ''' Lifecycle states of the CaptureEngine.
    ''' State transitions are governed exclusively by the CaptureEngine class.
    ''' External code MUST NOT mutate this value; it may only observe
    ''' <c>CaptureEngine.CurrentState</c>.
    ''' </summary>
    Public Enum EngineState
        ''' <summary>Engine constructed; <c>Initialize</c> has not yet been called.</summary>
        Created
        ''' <summary><c>Initialize</c> is currently executing.</summary>
        Initializing
        ''' <summary><c>Initialize</c> completed; engine is idle and ready to <c>Start</c>.</summary>
        Stopped
        ''' <summary><c>Start</c> is currently executing.</summary>
        Starting
        ''' <summary>Engine is actively running.</summary>
        Running
        ''' <summary><c>Stop</c> is currently executing.</summary>
        Stopping
        ''' <summary>An unrecoverable fault occurred; the engine must be disposed.</summary>
        Faulted
        ''' <summary><c>Dispose</c> completed; all resources have been released.</summary>
        Disposed
    End Enum
End Namespace
