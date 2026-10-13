' BackgroundLogger.vb
' Single shared background file logger — replaces per-line File.AppendAllText.
'
' Problem (P1):
'   CaptureEngine.WriteDebugLog / UI_Engine.DebugLog / EncoderDetector.DebugLog
'   all call File.AppendAllText on every line. FFmpeg progress goes to stderr
'   at up to 60 lines/sec (one per frame). Each AppendAllText opens / writes /
'   closes the file → real disk thrash on long recordings + slow disk.
'
' Fix:
'   One BlockingCollection(Of String) per log file. A single background Task
'   per file drains the queue and writes in batches. Writers never block on
'   disk I/O. On process exit, the queue is drained synchronously so no log
'   lines are lost.
'
' Thread-safe. Multiple callers writing to the same file share one queue.

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Threading

Public NotInheritable Class BackgroundLogger

    Private Class FileWriter
        ReadOnly _queue As BlockingCollection(Of String)
        ReadOnly _task As Task
        ReadOnly _filePath As String
        Dim _stopped As Boolean

        Public Sub New(filePath As String)
            _filePath = filePath
            ' ✅ FIX: VB.NET doesn't allow named arguments in a ReadOnly field
            ' initializer (the parser treats 'New ...(name:=value)' as a method
            ' call, not a constructor initializer). Move into the ctor where
            ' named-arg syntax IS allowed.
            _queue = New BlockingCollection(Of String)(boundedCapacity:=8192)
            ' Long-running task. Marked LongRunning so the scheduler gives it
            ' a dedicated thread instead of consuming a thread-pool slot.
            _task = Task.Factory.StartNew(AddressOf DrainLoop,
                                         TaskCreationOptions.LongRunning Or
                                         TaskCreationOptions.DenyChildAttach)
        End Sub

        Private Sub DrainLoop()
            Try
                Dim dir As String = Path.GetDirectoryName(_filePath)
                If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                    Directory.CreateDirectory(dir)
                End If

                Using fs As New FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 65536)
                    Using sw As New StreamWriter(fs) With {.AutoFlush = False}
                        Dim batch As New List(Of String)(256)

                        While Not _stopped OrElse _queue.Count > 0
                            batch.Clear()

                            ' Block on the first item (or exit if completed and empty).
                            Dim first As String = Nothing
                            Dim timeoutMs As Integer = If(_stopped, 0, 250)
                            If Not _queue.TryTake(first, timeoutMs) Then
                                If _stopped AndAlso _queue.Count = 0 Then Exit While
                                Continue While
                            End If
                            batch.Add(first)

                            ' Snap up anything else queued without blocking.
                            Dim item As String = Nothing
                            While _queue.TryTake(item, 0) AndAlso batch.Count < 1024
                                batch.Add(item)
                            End While

                            For Each ln In batch
                                sw.WriteLine(ln)
                            Next

                            ' Continue draining until stopped/empty (loop repeats).
                            sw.Flush()
                        End While
                    End Using
                End Using
            Catch ex As Exception
                ' Logger must never throw back to callers — log to Debug as last resort.
                Debug.WriteLine($"BackgroundLogger.DrainLoop('{_filePath}') fatal: {ex.Message}")
            End Try
        End Sub

        Public Sub Enqueue(line As String)
            If _stopped Then Return
            ' TryAdd: if queue is full, drop instead of blocking the calling thread
            ' (a dropped log line is better than freezing the recording pipeline).
            ' ✅ FIX: after CompleteAdding() in [Stop], TryAdd throws InvalidOperationException
            ' instead of returning False — guard against the race between the _stopped
            ' check above and the IsAddingCompleted flip below.
            If _queue.IsAddingCompleted Then Return
            Try
                If Not _queue.TryAdd(line, 0) Then
                    Debug.WriteLine($"BackgroundLogger queue full, dropping line for {_filePath}")
                End If
            Catch ex As InvalidOperationException
                ' Collection completed between the IsAddingCompleted check and TryAdd — drop silently.
            End Try
        End Sub

        Public Sub [Stop]()
            _stopped = True
            _queue.CompleteAdding()
            Try
                _task.Wait(5000)
            Catch
            End Try
        End Sub
    End Class

    Private Shared ReadOnly _writers As New Dictionary(Of String, FileWriter)
    Private Shared ReadOnly _lock As New Object()

    ''' <summary>
    ''' Enqueue a single pre-formatted line (no trailing newline). Thread-safe.
    ''' </summary>
    Public Shared Sub Log(filePath As String, line As String)
        Dim w As FileWriter = Nothing
        SyncLock _lock
            If Not _writers.TryGetValue(filePath, w) Then
                w = New FileWriter(filePath)
                _writers(filePath) = w
            End If
        End SyncLock
        w.Enqueue(line)
    End Sub

    ''' <summary>
    ''' Flush all writers. Call on process exit / app shutdown.
    ' ✅ M1 FIX: stop all writers in PARALLEL so form close doesn't stall
    ' for 15 seconds (3 writers × 5s timeout each, serialized).
    ' Now worst case is 5 seconds total.
    ''' </summary>
    Public Shared Sub ShutdownAll()
        Dim snapshot As List(Of FileWriter)
        SyncLock _lock
            snapshot = _writers.Values.ToList()
            _writers.Clear()
        End SyncLock

        ' Stop all in parallel — each does _task.Wait(5000), so worst case
        ' is 5 seconds total instead of N×5s serialized.
        If snapshot.Count = 0 Then Return
        Try
            System.Threading.Tasks.Parallel.ForEach(snapshot,
                Sub(w)
                    Try
                        w.Stop()
                    Catch
                    End Try
                End Sub)
        Catch
        End Try
    End Sub

End Class
