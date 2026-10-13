Option Strict On
Option Explicit On

Namespace CaptureEngine.Video
    ''' <summary>
    ''' Bounded handoff queue policy when the queue is full.
    ''' (P1-A v1.3.1 §6.7)
    '''
    ''' v1.2 removed BlockWithTimeout from the contract. v1.3.1 retains
    ''' only DropOldest and DropNewest. A blocking-with-timeout policy MAY
    ''' be re-introduced in the future as a VideoLayer implementation detail,
    ''' NOT as a contract surface — backends MUST NOT depend on it.
    '''
    ''' The policy and queue capacity are owned by VideoLayer/Handoff, NOT by
    ''' the backend (§2.1, §2.2, §15.4).
    ''' </summary>
    Public Enum BoundedHandoffPolicy
        ''' <summary>Evict the oldest queued result to make room. Best for low-latency / real-time semantics.</summary>
        DropOldest
        ''' <summary>Refuse the new result. Best for lossless / no-drop semantics.</summary>
        DropNewest
    End Enum
End Namespace
