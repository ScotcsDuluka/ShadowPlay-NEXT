Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Video.Handoff
    ''' <summary>
    ''' Concrete bounded handoff sink owned by VideoLayer. (P1-A v1.3.1 §6.3, §6.7, §15.4)
    '''
    ''' This sink owns:
    '''   - the bounded queue (capacity default 2, max 16);
    '''   - the BoundedHandoffPolicy (DropOldest / DropNewest);
    '''   - all eviction / disposal of evicted frames.
    '''
    ''' The backend has ZERO knowledge of the policy or capacity; it only
    ''' observes PushOutcome from TryPush and reacts accordingly.
    '''
    ''' The sink is a simple FIFO queue protected by a lock. Consumers call
    ''' Take() to dequeue. P1-B.1 tests inject custom sinks (Recording,
    ''' Refusing, Evicting, Blocking) to exercise specific code paths; the
    ''' production BoundedVideoFrameSink is also exercised directly in
    ''' backpressure tests.
    ''' </summary>
    Public NotInheritable Class BoundedVideoFrameSink
        Implements IVideoFrameSink

        Private Const MaxCapacityHardCap As Integer = 16

        Private ReadOnly _sync As New Object()
        Private ReadOnly _queue As New Queue(Of FrameAcquisitionResult)()
        Private ReadOnly _capacity As Integer
        Private ReadOnly _policy As BoundedHandoffPolicy
        Private ReadOnly _logger As EngineLogger

        Private _disposed As Boolean = False
        Private _pushedCount As Long = 0
        Private _replacedCount As Long = 0
        Private _droppedCount As Long = 0

        Public Sub New(capacity As Integer,
                       policy As BoundedHandoffPolicy,
                       Optional logger As EngineLogger = Nothing)
            If capacity < 1 Then
                Throw New ArgumentOutOfRangeException(NameOf(capacity), "Capacity must be at least 1.")
            End If
            If capacity > MaxCapacityHardCap Then
                Throw New ArgumentOutOfRangeException(NameOf(capacity),
                    "Capacity exceeds hard cap of " & MaxCapacityHardCap.ToString() & ".")
            End If
            _capacity = capacity
            _policy = policy
            _logger = logger
        End Sub

        Public ReadOnly Property Capacity As Integer
            Get
                Return _capacity
            End Get
        End Property

        Public ReadOnly Property Policy As BoundedHandoffPolicy
            Get
                Return _policy
            End Get
        End Property

        Public ReadOnly Property PushedCount As Long
            Get
                SyncLock _sync
                    Return _pushedCount
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ReplacedCount As Long
            Get
                SyncLock _sync
                    Return _replacedCount
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property DroppedCount As Long
            Get
                SyncLock _sync
                    Return _droppedCount
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property Count As Integer
            Get
                SyncLock _sync
                    Return _queue.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Non-blocking push per the contract. NEVER throws for queue-full;
        ''' returns Dropped (DropNewest policy) or Pushed-with-eviction
        ''' (DropOldest policy, returns Replaced).
        '''
        ''' P1-B.1 FIX change #3: evicted frames are extracted under the
        ''' lock but disposed OUTSIDE the lock. The queue mutation itself
        ''' (Dequeue/Enqueue + counter increments) stays under lock; the
        ''' potentially heavy frame.Dispose() call is moved out so that
        ''' concurrent TryPush / TryTake / Dispose calls are not blocked
        ''' by an unrelated frame's Dispose (which on GPU frames may
        ''' synchronously release GPU resources).
        ''' </summary>
        Public Function TryPush(result As FrameAcquisitionResult) As PushOutcome Implements IVideoFrameSink.TryPush
            Dim toDisposeOutsideLock As IVideoFrame = Nothing

            SyncLock _sync
                If _disposed Then
                    _droppedCount += 1
                    Return PushOutcome.Dropped
                End If

                If _queue.Count < _capacity Then
                    _queue.Enqueue(result)
                    _pushedCount += 1
                    Return PushOutcome.Pushed
                End If

                ' Queue is full — apply policy.
                Select Case _policy
                    Case BoundedHandoffPolicy.DropOldest
                        Dim evicted = _queue.Dequeue()
                        ' Extract the evicted frame for disposal outside the lock.
                        toDisposeOutsideLock = evicted.Frame
                        _queue.Enqueue(result)
                        _pushedCount += 1
                        _replacedCount += 1
                        ' (return value is set after the lock is released)

                    Case BoundedHandoffPolicy.DropNewest
                        ' Refuse the new result. Caller retains ownership.
                        _droppedCount += 1
                        Return PushOutcome.Dropped

                    Case Else
                        Throw New InvalidOperationException("Unknown policy: " & _policy.ToString())
                End Select
            End SyncLock

            ' DISPOSE THE EVICTED FRAME OUTSIDE THE LOCK.
            If toDisposeOutsideLock IsNot Nothing Then
                DisposeFrameSafely(toDisposeOutsideLock)
            End If

            Return PushOutcome.Replaced
        End Function

        ''' <summary>
        ''' Dequeue the next result. Returns False if the queue is empty.
        ''' The caller becomes the new owner of result.Frame (if any) and
        ''' MUST dispose it.
        ''' </summary>
        Public Function TryTake(ByRef result As FrameAcquisitionResult) As Boolean
            SyncLock _sync
                If _queue.Count = 0 Then
                    result = Nothing
                    Return False
                End If
                result = _queue.Dequeue()
                Return True
            End SyncLock
        End Function

        Public Sub Dispose()
            ' P1-B.1 FIX change #3: drain queued results UNDER the lock (queue
            ' mutation only), then dispose their frames OUTSIDE the lock.
            Dim drained As New List(Of IVideoFrame)()

            SyncLock _sync
                If _disposed Then Return
                _disposed = True
                ' Detach all queued results; collect frames for later disposal.
                While _queue.Count > 0
                    Dim item = _queue.Dequeue()
                    If item.Frame IsNot Nothing Then
                        drained.Add(item.Frame)
                    End If
                End While
            End SyncLock

            ' DISPOSE ALL DRAINED FRAMES OUTSIDE THE LOCK.
            For Each frame In drained
                DisposeFrameSafely(frame)
            Next
        End Sub

        ''' <summary>
        ''' Dispose a frame safely (swallow exceptions; log if a logger is present).
        ''' Called OUTSIDE the lock. Named differently from the old
        ''' DisposeEvictedFrame so the call sites make it obvious which path
        ''' is lock-free.
        ''' </summary>
        Private Sub DisposeFrameSafely(frame As IVideoFrame)
            If frame Is Nothing Then Return
            Try
                frame.Dispose()
            Catch ex As Exception
                If _logger IsNot Nothing Then
                    _logger.Error("BoundedVideoFrameSink: error disposing frame outside lock", ex)
                End If
            End Try
        End Sub
    End Class
End Namespace
