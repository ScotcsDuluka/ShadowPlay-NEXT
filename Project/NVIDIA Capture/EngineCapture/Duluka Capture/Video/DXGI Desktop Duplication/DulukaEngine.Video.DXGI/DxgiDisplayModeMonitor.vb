Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports DulukaEngine.Video

Namespace DulukaEngine.Video.Backends.Ddagrab

    ' DxgiDisplayModeMonitor.vb — the real DisplayModeMonitor for the native
    ' pipeline (SPN-ADR-2026-10-24-SESSION-001 §D).
    '
    ' Read-only bounded polling of the shared DXGI+CCD inventory (the SAME
    ' snapshot production planning already uses). No duplication, no D3D11
    ' device, no pixels. A poll every few seconds classifies the pinned
    ' target as Stable / TopologyChanged / TargetLost; consecutive inventory
    ' failures escalate to TargetLost only after an explicit budget so a
    ' single transient QueryInterface hiccup cannot end a session.
    Public NotInheritable Class DxgiDisplayModeMonitor
        Implements IDisposable

        Private ReadOnly _pinned As CaptureTarget
        Private ReadOnly _inventory As Func(Of DxgiDisplayInventory.Snapshot)
        Private ReadOnly _pollInterval As TimeSpan
        Private ReadOnly _maxConsecutiveErrors As Integer
        Private ReadOnly _sync As New Object()
        Private ReadOnly _eventRaised As New ManualResetEventSlim(False)
        Private ReadOnly _pollDone As New ManualResetEventSlim(True)

        Private _timer As Timer
        Private _inFlight As Boolean
        Private _disposed As Boolean
        Private _lastKind As DisplayModeEventKind = DisplayModeEventKind.Stable
        Private _lastReason As String = "monitor_not_started"
        Private _consecutiveErrors As Integer
        Private _pollCount As Long

        Public Sub New(pinned As CaptureTarget,
                       Optional pollIntervalMs As Integer = 2000,
                       Optional inventory As Func(Of DxgiDisplayInventory.Snapshot) = Nothing,
                       Optional maxConsecutiveErrors As Integer = 3)
            If pinned Is Nothing Then Throw New ArgumentNullException(NameOf(pinned))
            If Not pinned.Eligible Then Throw New ArgumentException("monitor_target_not_eligible", NameOf(pinned))
            If pollIntervalMs < 250 Then Throw New ArgumentException("monitor_poll_too_aggressive", NameOf(pollIntervalMs))
            If maxConsecutiveErrors < 1 Then Throw New ArgumentException("monitor_error_budget_invalid", NameOf(maxConsecutiveErrors))
            _pinned = pinned
            _inventory = If(inventory, AddressOf DxgiDisplayInventory.Collect)
            _pollInterval = TimeSpan.FromMilliseconds(pollIntervalMs)
            _maxConsecutiveErrors = maxConsecutiveErrors
        End Sub

        ''' <summary>Raised on the poll thread when the classified state
        ''' CHANGES. Consumers must not throw; the monitor never re-raises an
        ''' unchanged state.</summary>
        Public Event ModeChanged(kind As DisplayModeEventKind, reason As String)

        Public ReadOnly Property PinnedTarget As CaptureTarget
            Get
                Return _pinned
            End Get
        End Property

        Public ReadOnly Property CurrentState As DisplayModeEventKind
            Get
                SyncLock _sync
                    Return _lastKind
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property LastReason As String
            Get
                SyncLock _sync
                    Return _lastReason
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property PollCount As Long
            Get
                SyncLock _sync
                    Return _pollCount
                End SyncLock
            End Get
        End Property

        Public Sub Start()
            SyncLock _sync
                ThrowIfDisposed()
                If _timer IsNot Nothing Then Return
                _lastReason = "monitor_started"
                _timer = New Timer(AddressOf OnPoll, Nothing, TimeSpan.Zero, _pollInterval)
            End SyncLock
        End Sub

        ''' <summary>Run exactly one classification now (also the deterministic
        ''' test seam — Start is not required).</summary>
        Public Sub PollOnce()
            Dim changed = PollInternal()
            If changed Then RaiseFromState()
        End Sub

        ''' <returns>True when the classified state changed.</returns>
        Private Function PollInternal() As Boolean
            Dim kind As DisplayModeEventKind
            Dim reason As String
            Try
                Dim snapshot = _inventory()
                kind = DisplayModeMonitor.Classify(_pinned, snapshot.Outputs)
                reason = If(kind = DisplayModeEventKind.Stable, "pinned_target_present", "pinned_target_absent")
                SyncLock _sync
                    _consecutiveErrors = 0
                End SyncLock
            Catch ex As Exception
                SyncLock _sync
                    _consecutiveErrors += 1
                    If _consecutiveErrors < _maxConsecutiveErrors Then
                        ' Transient inventory failure: hold the previous state
                        ' and stay quiet until the budget is spent.
                        _pollCount += 1
                        Return False
                    End If
                    kind = DisplayModeEventKind.TargetLost
                    reason = "inventory_unavailable_" & _consecutiveErrors & ":" & ex.Message
                End SyncLock
            End Try

            Dim changed As Boolean
            SyncLock _sync
                _pollCount += 1
                changed = (kind <> _lastKind)
                _lastKind = kind
                _lastReason = reason
            End SyncLock
            Return changed
        End Function

        Private Sub OnPoll(state As Object)
            SyncLock _sync
                If _disposed OrElse _inFlight Then Return
                _inFlight = True
                _pollThreadId = Thread.CurrentThread.ManagedThreadId
                _pollDone.Reset()
            End SyncLock
            Try
                If PollInternal() Then RaiseFromState()
            Finally
                SyncLock _sync
                    _inFlight = False
                    _pollThreadId = -1
                End SyncLock
                _pollDone.Set()
            End Try
        End Sub

        Private Sub RaiseFromState()
            Dim kind As DisplayModeEventKind
            Dim reason As String
            SyncLock _sync
                kind = _lastKind
                reason = _lastReason
                _eventRaised.Set()
            End SyncLock
            Try
                RaiseEvent ModeChanged(kind, reason)
            Catch
                ' A consumer fault must never kill the poll loop; the next
                ' state change re-raises. Session-side faults belong to the
                ' session's own recovery path.
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _sync
                If _disposed Then Return
                _disposed = True
            End SyncLock
            Dim timer As Timer
            SyncLock _sync
                timer = _timer
                _timer = Nothing
            End SyncLock
            If timer IsNot Nothing Then
                Try : timer.Change(Timeout.Infinite, Timeout.Infinite) : Catch : End Try
                Try : timer.Dispose() : Catch : End Try
            End If
            ' Bounded in-flight drain: wait for a poll that already entered its
            ' body (never our own thread), then give up — a stuck inventory
            ' call must not make Dispose hang.
            If Thread.CurrentThread.ManagedThreadId <> _pollThreadId Then
                _pollDone.Wait(TimeSpan.FromSeconds(2))
            End If
            _eventRaised.Dispose()
        End Sub

        ' Thread id of an in-flight poll (best-effort, diagnostics only).
        Private _pollThreadId As Integer = -1
        Private Sub ThrowIfDisposed()
            If _disposed Then Throw New ObjectDisposedException(NameOf(DxgiDisplayModeMonitor))
        End Sub
    End Class
End Namespace
