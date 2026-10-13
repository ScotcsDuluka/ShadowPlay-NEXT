Imports System.Collections.Concurrent
Imports System.Threading
Imports CaptureEngine.Video

Namespace CaptureEngine.Recording

    ''' <summary>
    ''' Dedicated single-consumer disposal queue for GPU-backed video frames.
    ''' Keeps D3D11/COM Release work off the CFR presentation thread while
    ''' preserving deterministic cleanup at session end.
    ''' </summary>
    Friend NotInheritable Class DeferredVideoFrameDisposer
        Implements IDisposable

        Private ReadOnly _queue As New ConcurrentQueue(Of IVideoFrame)()
        Private ReadOnly _wake As New AutoResetEvent(False)
        Private ReadOnly _worker As Thread
        ' ★ _stopping/_disposed are transitioned UNDER _stateLock together with
        ' the Enqueue check-then-act, so a producer racing CompleteAndWait can
        ' never drop a frame into a queue nobody will drain (the old lock-free
        ' check lost frames + ObjectDisposedException'd on _wake.Set()).
        Private ReadOnly _stateLock As New Object()
        Private _stopping As Integer = 0
        Private _disposed As Integer = 0

        Public Sub New()
            _worker = New Thread(AddressOf WorkerLoop) With {
                .IsBackground = True,
                .Name = "CaptureEngine-GpuDisposer"
            }
            _worker.Start()
        End Sub

        Public Sub Enqueue(frame As IVideoFrame)
            If frame Is Nothing Then Return
            SyncLock _stateLock
                If Volatile.Read(_disposed) <> 0 OrElse Volatile.Read(_stopping) <> 0 Then
                    Try
                        frame.Dispose()
                    Catch
                    End Try
                    Return
                End If
                _queue.Enqueue(frame)
                Try
                    _wake.Set()
                Catch
                    ' _wake is only disposed after the worker has exited; kept
                    ' defensive so a torn-down handle can never kill a producer.
                End Try
            End SyncLock
        End Sub

        ''' <summary>
        ''' Stop the worker and drain everything. NEVER throws: the old
        ''' TimeoutException-on-Join(5000) converted a merely-slow GPU drain
        ''' (TDR, heavy load) into an aborted session finalization — skipping
        ''' encoder stop, audio finalization, and mux finalize entirely.
        ''' The drain is concurrency-safe with a still-alive worker (frame
        ''' Dispose is one-shot via Interlocked.CompareExchange).
        ''' </summary>
        Public Sub CompleteAndWait()
            Interlocked.Exchange(_stopping, 1)
            _wake.Set()
            If Not _worker.Join(5000) Then
                ' Slow (not necessarily hung) drain — keep waiting once more,
                ' then drain from this thread regardless. Reserving an
                ' exception for this would poison the whole recording.
                _worker.Join(10000)
            End If
            DrainSynchronously()
        End Sub

        Private Sub WorkerLoop()
            Do
                _wake.WaitOne(50)
                DrainSynchronously()
                SyncLock _stateLock
                    If Volatile.Read(_stopping) <> 0 AndAlso _queue.IsEmpty Then Exit Do
                End SyncLock
            Loop
        End Sub

        Private Sub DrainSynchronously()
            Dim frame As IVideoFrame = Nothing
            While _queue.TryDequeue(frame)
                Try
                    frame.Dispose()
                Catch
                    ' Never let one bad frame kill the disposer thread.
                End Try
                frame = Nothing
            End While
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _stateLock
                If Interlocked.Exchange(_disposed, 1) <> 0 Then Return
                Volatile.Write(_stopping, 1)
            End SyncLock
            _wake.Set()
            _worker.Join(5000)
            DrainSynchronously()
            _wake.Dispose()
        End Sub

    End Class

End Namespace
