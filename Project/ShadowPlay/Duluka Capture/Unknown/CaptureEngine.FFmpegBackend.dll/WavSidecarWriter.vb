Option Strict On
Option Explicit On
Option Infer On

' WavSidecarWriter.vb
'
' Bounded-queue WAV sidecar writer — the Phase 12 port of the PROVEN
' audio-sidecar model (Engine/Engine/[Audio]/AudioFileWriter.vb +
' ENGINE-REALTIME-ARCHITECTURE.md):
'
'   ✔ WASAPI callback thread does COPY + ENQUEUE only (never disk I/O)
'   ✔ Dedicated writer thread drains a bounded ConcurrentQueue
'   ✔ Drop-on-full with byte accounting (Enqueued = Written + Dropped)
'   ✔ Finalize patches RIFF/data sizes so the WAV is valid even after
'     partial sessions
'   ✔ No NAudio dependency here (manual RIFF/WAVE header) so this class
'     builds and is unit-testable on net8.0 (any OS)
'
' Differences vs Engine's AudioFileWriter (intentional, per spec v3 Q3):
'   - No QPC timestamp stream (Phase 12 sync uses StartRecording-call
'     timestamps via SyncMath instead).
'   - No silence insertion at this layer — genuine gaps stay as gaps in
'     the WAV; the mux step handles padding (apad) and stretch
'     (aresample=async) exactly like the proven two-process design.

Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>Finalization report — accounting must satisfy the invariant.</summary>
    Public NotInheritable Class WavFinalizeReport
        Public Property Succeeded As Boolean
        Public Property BytesEnqueued As Long
        Public Property BytesWritten As Long
        Public Property BytesDropped As Long
        Public Property ChunksDropped As Long
        Public Property AccountingResidual As Long
        Public Property FilePath As String = ""
        Public Property FileSize As Long
        Public Property DurationSec As Double

        Public ReadOnly Property AccountingOk As Boolean
            Get
                Return AccountingResidual = 0
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return $"WavSidecar: ok={Succeeded} enqueued={BytesEnqueued:N0} written={BytesWritten:N0} " &
                   $"dropped={BytesDropped:N0} (chunks={ChunksDropped:N0}) residual={AccountingResidual:N0} " &
                   $"duration={DurationSec:0.000}s file={FileSize:N0}B"
        End Function
    End Class

    ''' <summary>
    ''' Bounded PCM → WAV writer with a dedicated writer thread.
    ''' Thread-safe for one producer (WASAPI callback) + one Finalize caller.
    ''' </summary>
    Public NotInheritable Class WavSidecarWriter
        Implements IDisposable

        Private ReadOnly _filePath As String
        Private ReadOnly _channels As Integer
        Private ReadOnly _sampleRate As Integer
        Private ReadOnly _bitsPerSample As Integer
        Private ReadOnly _maxQueueChunks As Integer

        Private ReadOnly _queue As New ConcurrentQueue(Of Byte())
        Private ReadOnly _signal As New AutoResetEvent(False)
        Private ReadOnly _stopWriter As New ManualResetEvent(False)
        Private _writerThread As Thread
        Private _stream As FileStream
        Private _started As Boolean = False
        Private _finalized As Boolean = False
        Private _disposed As Boolean = False

        ' Accounting (Interlocked — callback thread enqueues, writer thread writes)
        Private _bytesEnqueued As Long = 0
        Private _bytesWritten As Long = 0
        Private _bytesDropped As Long = 0
        Private _chunksDropped As Long = 0
        Private _dataBytes As Long = 0          ' bytes actually in the data chunk

        Private ReadOnly _sync As New Object()

        ''' <summary>Wait handles for the writer loop.</summary>
        Private ReadOnly _waitHandles() As WaitHandle

        ''' <param name="filePath">Target .wav path (overwritten if exists).</param>
        ''' <param name="channels">Channel count (e.g. 2).</param>
        ''' <param name="sampleRate">Sample rate (e.g. 48000).</param>
        ''' <param name="bitsPerSample">Bits per sample (16/24/32). Float formats not supported —
        ''' convert to PCM16 upstream if the capture format is IeeeFloat.</param>
        ''' <param name="maxQueueChunks">Bounded queue capacity in chunks. Default 240
        ''' (≈10 s at 10 ms/buffer). Oldest chunks are dropped on overflow.</param>
        Public Sub New(filePath As String,
                       channels As Integer,
                       sampleRate As Integer,
                       bitsPerSample As Integer,
                       Optional maxQueueChunks As Integer = 240)
            If String.IsNullOrWhiteSpace(filePath) Then Throw New ArgumentException("filePath required", NameOf(filePath))
            If channels < 1 OrElse channels > 8 Then Throw New ArgumentOutOfRangeException(NameOf(channels))
            If sampleRate < 8000 OrElse sampleRate > 384000 Then Throw New ArgumentOutOfRangeException(NameOf(sampleRate))
            If bitsPerSample <> 16 AndAlso bitsPerSample <> 24 AndAlso bitsPerSample <> 32 Then
                Throw New ArgumentOutOfRangeException(NameOf(bitsPerSample), "WavSidecarWriter supports 16/24/32-bit PCM only")
            End If
            If maxQueueChunks < 4 Then maxQueueChunks = 4

            _filePath = Path.GetFullPath(filePath)
            _channels = channels
            _sampleRate = sampleRate
            _bitsPerSample = bitsPerSample
            _maxQueueChunks = maxQueueChunks
            _waitHandles = New WaitHandle() {_signal, _stopWriter}
        End Sub

        ' ─── Accounting readouts (safe from any thread) ────────────────

        Public ReadOnly Property BytesEnqueued As Long
            Get
                Return Interlocked.Read(_bytesEnqueued)
            End Get
        End Property

        Public ReadOnly Property BytesWritten As Long
            Get
                Return Interlocked.Read(_bytesWritten)
            End Get
        End Property

        Public ReadOnly Property BytesDropped As Long
            Get
                Return Interlocked.Read(_bytesDropped)
            End Get
        End Property

        Public ReadOnly Property QueueDepth As Integer
            Get
                Return _queue.Count
            End Get
        End Property

        Public ReadOnly Property FilePath As String
            Get
                Return _filePath
            End Get
        End Property

        ' ─── Lifecycle ──────────────────────────────────────────────────

        ''' <summary>
        ''' Create the file, write the placeholder WAV header, start the
        ''' writer thread. Throws on any I/O failure.
        ''' </summary>
        Public Sub Start()
            SyncLock _sync
                If _disposed Then Throw New ObjectDisposedException(NameOf(WavSidecarWriter))
                If _started Then Throw New InvalidOperationException("WavSidecarWriter already started")
                _started = True
            End SyncLock

            Dim dir As String = Path.GetDirectoryName(_filePath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            _stream = New FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read)
            WriteHeaderPlaceholder(_stream)

            _stopWriter.Reset()
            _signal.Reset()
            _writerThread = New Thread(AddressOf WriterThreadProc) With {
                .Name = "WavSidecarWriter",
                .IsBackground = True,
                .Priority = ThreadPriority.AboveNormal
            }
            _writerThread.Start()
        End Sub

        ''' <summary>
        ''' Producer entry point — call from the WASAPI DataAvailable callback.
        ''' Copies the buffer, enqueues the copy, and returns immediately.
        ''' NEVER blocks, NEVER touches disk. Drops the OLDEST chunks when the
        ''' queue is full (real-time capture wins over completeness).
        ''' </summary>
        Public Sub EnqueueChunk(buffer As Byte(), count As Integer)
            If buffer Is Nothing OrElse count <= 0 Then Return
            If Not _started OrElse _finalized OrElse _disposed Then Return
            If count > buffer.Length Then count = buffer.Length

            ' Ownership: copy here — the caller's buffer is reused by WASAPI
            ' as soon as the callback returns.
            Dim copy(count - 1) As Byte
            Array.Copy(buffer, copy, count)

            ' Drop-on-full: shed the oldest chunks first (bounded latency).
            While _queue.Count >= _maxQueueChunks
                Dim dropped As Byte() = Nothing
                If _queue.TryDequeue(dropped) Then
                    Interlocked.Add(_bytesDropped, dropped.Length)
                    Interlocked.Increment(_chunksDropped)
                Else
                    Exit While
                End If
            End While

            _queue.Enqueue(copy)
            Interlocked.Add(_bytesEnqueued, count)
            ' ★ Teardown guard: Dispose() may dispose _signal while this
            ' (WASAPI callback) thread is past the _disposed check — an
            ' ObjectDisposedException here used to abort the audio capture.
            Try
                _signal.[Set]()
            Catch ex As ObjectDisposedException
            End Try
        End Sub

        ''' <summary>
        ''' Stop the writer thread, drain the queue, patch WAV header sizes,
        ''' close the file. Safe to call once. Never throws — failures are
        ''' reported in the returned WavFinalizeReport.
        ''' </summary>
        ''' <param name="timeoutMs">How long to wait for the writer thread to exit.</param>
        Public Function Complete(timeoutMs As Integer) As WavFinalizeReport
            Dim report As New WavFinalizeReport() With {.FilePath = _filePath}

            SyncLock _sync
                If Not _started OrElse _finalized Then
                    ' Not started / already finalized — nothing to account.
                    report.Succeeded = False
                    Return report
                End If
                _finalized = True
            End SyncLock

            ' Signal the writer thread to drain + exit.
            _stopWriter.[Set]()
            _signal.[Set]()

            ' ★ Finalize race fix: the old code ignored the Join result and
            ' Seek+rewrote the WAV header on the same FileStream the writer
            ' thread may still be using (FileStream is not thread-safe). On a
            ' join timeout, dispose the stream first — the writer's in-flight
            ' Write fails into its catch (accounted as dropped) and it exits —
            ' then patch the header through a fresh handle.
            Dim writerAlive As Boolean = False
            If _writerThread IsNot Nothing Then
                writerAlive = Not _writerThread.Join(Math.Max(500, timeoutMs))
                If writerAlive Then
                    Debug.WriteLine("WavSidecarWriter: writer thread did not exit in time — forcing stream close")
                    Try : _stream?.Dispose() : Catch : End Try
                    Try : _writerThread.Join(2000) : Catch : End Try
                End If
            End If

            Dim ok As Boolean = False
            Try
                If writerAlive Then
                    ' Patch via a separate handle (stream may be closed/owned
                    ' by the late writer thread).
                    Using patchStream As New FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.Read)
                        PatchHeaderSizes(patchStream, _dataBytes)
                        patchStream.Flush()
                    End Using
                    ok = True
                ElseIf _stream IsNot Nothing Then
                    ' Patch RIFF + data sizes so the file is a valid WAV.
                    _stream.Seek(0, SeekOrigin.Begin)
                    PatchHeaderSizes(_stream, _dataBytes)
                    _stream.Flush()
                    ok = True
                End If
            Catch ex As Exception
                Debug.WriteLine("WavSidecarWriter: finalize error: " & ex.Message)
            Finally
                Try : _stream?.Dispose() : Catch : End Try
                _stream = Nothing
            End Try

            report.BytesEnqueued = Interlocked.Read(_bytesEnqueued)
            report.BytesWritten = Interlocked.Read(_bytesWritten)
            report.BytesDropped = Interlocked.Read(_bytesDropped)
            report.ChunksDropped = Interlocked.Read(_chunksDropped)
            report.AccountingResidual = report.BytesEnqueued - report.BytesWritten - report.BytesDropped
            report.Succeeded = ok

            Try
                If File.Exists(_filePath) Then
                    report.FileSize = New FileInfo(_filePath).Length
                End If
            Catch
            End Try

            Dim bytesPerSec As Integer = _sampleRate * _channels * (_bitsPerSample \ 8)
            If bytesPerSec > 0 Then report.DurationSec = report.BytesWritten / CDbl(bytesPerSec)

            Return report
        End Function

        ' ─── Writer thread ─────────────────────────────────────────────

        Private Sub WriterThreadProc()
            Try
                Do While True
                    WaitHandle.WaitAny(_waitHandles, 200)

                    If _stopWriter.WaitOne(0) Then
                        ' Final drain: write everything still queued, then exit.
                        DrainQueue()
                        Exit Do
                    End If

                    DrainQueue()
                Loop
            Catch ex As Exception
                Debug.WriteLine("WavSidecarWriter: writer thread fatal: " & ex.Message)
            End Try
        End Sub

        Private Sub DrainQueue()
            Dim chunk As Byte() = Nothing
            While _queue.TryDequeue(chunk)
                If _stream Is Nothing Then Exit While
                Try
                    _stream.Write(chunk, 0, chunk.Length)
                    Interlocked.Add(_bytesWritten, chunk.Length)
                    _dataBytes += chunk.Length
                Catch ex As Exception
                    ' Disk died / stream closed — account as dropped and stop.
                    Interlocked.Add(_bytesDropped, chunk.Length)
                    Interlocked.Increment(_chunksDropped)
                    Debug.WriteLine("WavSidecarWriter: write failed: " & ex.Message)
                    Exit While
                End Try
            End While
        End Sub

        ' ─── WAV header (canonical 44-byte PCM) ────────────────────────

        Private Sub WriteHeaderPlaceholder(s As FileStream)
            Dim header = BuildHeader(0)
            s.Write(header, 0, header.Length)
        End Sub

        Private Sub PatchHeaderSizes(s As FileStream, dataBytes As Long)
            ' RIFF chunk size lives at offset 4..7 — Seek(4) FIRST, otherwise
            ' the 4-byte write lands on 0..3 and clobbers the "RIFF" magic
            ' (bug caught by Recording.Tests WAV header validation).
            Dim riffSize As UInteger = CUInt(Math.Min(&H7FFFFFFFL, 36 + dataBytes))
            Dim dataFlag As UInteger = CUInt(Math.Min(&H7FFFFFFFL, dataBytes))

            s.Seek(4, SeekOrigin.Begin)
            Dim buf(3) As Byte
            buf = BitConverter.GetBytes(riffSize)
            s.Write(buf, 0, 4)               ' offsets 4..7
            s.Seek(40, SeekOrigin.Begin)     ' data chunk size field
            buf = BitConverter.GetBytes(dataFlag)
            s.Write(buf, 0, 4)               ' offsets 40..43
        End Sub

        Private Function BuildHeader(dataBytes As Long) As Byte()
            Dim ms As New MemoryStream(44)
            Dim w As New BinaryWriter(ms, Encoding.ASCII)

            Dim byteRate As UInteger = CUInt(_sampleRate * _channels * (_bitsPerSample \ 8))
            Dim blockAlign As UInteger = CUInt(_channels * (_bitsPerSample \ 8))
            Dim riffSize As UInteger = CUInt(Math.Min(&H7FFFFFFFL, 36 + dataBytes))
            Dim dataFlag As UInteger = CUInt(Math.Min(&H7FFFFFFFL, dataBytes))

            ' Magic chunks must be written as ASCII bytes — a little-endian
            ' UInt32 write would byte-swap them ("FFIR" instead of "RIFF").
            w.Write(Encoding.ASCII.GetBytes("RIFF"))
            w.Write(riffSize)                ' placeholder patched at Finalize
            w.Write(Encoding.ASCII.GetBytes("WAVE"))
            w.Write(Encoding.ASCII.GetBytes("fmt "))
            w.Write(16UI)                    ' PCM chunk size
            w.Write(1US)                     ' audio format = PCM
            w.Write(CUShort(_channels))
            w.Write(CUInt(_sampleRate))      ' 4-byte field — writing UShort here produced
            '                               ' a 42-byte header and broke ffprobe (caught
            '                               ' by Recording.Tests runtime sync suite)
            w.Write(byteRate)
            w.Write(CUShort(blockAlign))
            w.Write(CUShort(_bitsPerSample))
            w.Write(Encoding.ASCII.GetBytes("data"))
            w.Write(dataFlag)                ' placeholder patched at Finalize
            w.Flush()

            Return ms.ToArray()
        End Function

        ' ─── IDisposable ───────────────────────────────────────────────

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _sync
                If _disposed Then Return
                _disposed = True
            End SyncLock

            If Not _finalized AndAlso _started Then
                Try : Complete(1000) : Catch : End Try
            Else
                _stopWriter.[Set]()
                _signal.[Set]()
                Try : _stream?.Dispose() : Catch : End Try
                _stream = Nothing
            End If

            Try : _signal.Dispose() : Catch : End Try
            Try : _stopWriter.Dispose() : Catch : End Try
        End Sub

    End Class

End Namespace
