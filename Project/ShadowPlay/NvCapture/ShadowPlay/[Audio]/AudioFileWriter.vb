Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports NAudio.CoreAudioApi
Imports NAudio.Wave
Imports CaptureEngine.FFmpegBackend

''' <summary>
''' File-based audio recorder — writes WASAPI capture to .wav files via
''' a lock-free queue + dedicated writer thread.
'''
''' ARCHITECTURE (Two-Process Recording):
'''   Video: FFmpeg ddagrab → NVENC → temp_video.mp4 (NO audio input)
'''   Audio: This class → temp_system.wav + temp_mic.wav (direct file I/O)
'''   Mux:   FFmpeg -i video.mp4 -i audio.wav -c:v copy -c:a aac final.mp4
'''
''' DESIGN PRINCIPLES (per GPT code review):
'''   1. NO wall-clock silence feeder thread — gap detection happens IN the
'''      callback, inserting silence BEFORE real audio (no overlap possible)
'''   2. NO synchronous disk I/O in callback — callback just memcpy + enqueue
'''   3. NO volume processing in callback — volume applied at mux stage
'''   4. NO fake latency measurement — per-track actual start timestamps
'''   5. Proper shutdown: stop capture → drain → flush writer → finalize
''' </summary>
Public Class AudioFileWriter
    Implements IDisposable

    Private Class TrackConfig
        Public Property Enabled As Boolean
        Public Property IsSystem As Boolean
        Public Property DeviceId As String
        Public Property DeviceName As String
        Public Property OutputPath As String
        Public Property Volume As Single
    End Class

    ' ── Track lifecycle state machine (per GPT P0.1) ──
    ' Running   = active capture, callbacks enqueue data
    ' Draining  = capture stopped, waiting for in-flight callbacks to finish
    ' Stopped   = fully shut down, writer thread joined, WAV finalized
    '
    ' CRITICAL: callbacks check lifecycle state (NOT _isRunning) so that
    ' in-flight callbacks during Draining still enqueue their data.
    ' The old code checked _isRunning BEFORE incrementing InFlightCallbacks,
    ' which meant callbacks arriving after _isRunning=False were dropped
    ' without being counted — losing the final audio chunk.
    Private Enum TrackLifecycle
        Stopped
        Running
        Draining
    End Enum

    ''' <summary>
    ''' Wrapper for queued audio data with priority metadata.
    '''
    ''' Two modes:
    '''   - Real PCM: Data = byte array, IsSilence = False
    '''   - Silence descriptor: Data = Nothing, IsSilence = True, SilenceBytes = N
    '''
    ''' The silence descriptor mode is critical for performance. PreFillSilence
    ''' enqueues a SINGLE chunk with metadata only (no allocation of the actual
    ''' silence bytes). The writer thread later expands the descriptor into
    ''' zero-filled chunks when writing to disk. This keeps the WASAPI callback
    ''' lightweight (single TryAdd instead of thousands of allocations).
    ''' </summary>
    Private Class AudioChunk
        Public Property Data As Byte()
        Public Property IsSilence As Boolean
        Public Property SilenceBytes As Long  ' used only when IsSilence=True and Data=Nothing
        ''' <summary>True for the FIRST gap (StartRecording → first callback).
        ''' Keeps InitialSilenceBytes accounting honest when the queue-full
        ''' drop policy discards silence descriptors mid-stream.</summary>
        Public Property IsInitial As Boolean
    End Class

    Private Class TrackState
        Public Property Config As TrackConfig
        Public Property Capture As WasapiCapture
        Public Property Writer As WaveFileWriter
        Public Property Queue As BlockingCollection(Of AudioChunk)
        Public Property WriterThread As Thread
        Public Property CallbackCount As Long
        Public Property BytesEnqueued As Long
        Public Property WrittenBytes As Long  ' actually written to disk by writer thread
        Public Property SamplesEnqueued As Long
        Public Property DroppedChunks As Long
        Public Property DroppedBytes As Long
        Public Property DroppedSamples As Long
        Public Property DroppedDurationSec As Double
        Public Property DroppedSilenceBytes As Long  ' silence dropped to make room for real audio
        Public Property Failed As Boolean
        Public Property FailReason As String
        ' FirstCallbackDispatchTicks: when the first WASAPI callback was dispatched.
        ' NOTE: This is NOT used for sync offset calculation — it's diagnostic only.
        ' The sync offset uses StartRecordingTicks (when capture was started) because
        ' WASAPI loopback may not fire callbacks for seconds when no audio is playing.
        Public Property FirstCallbackDispatchTicks As Long
        Public Property StartRecordingTicks As Long  ' TRUE capture start time (sync anchor)
        Public Property InitialSilenceBytes As Long  ' silence pre-filled to align WAV with capture timeline
        ' ── P13-AUDIO-TIMELINE (2026-08-28): OBS-model gap repair ──
        ' The old "GAP DETECTION REMOVED entirely" design concatenated only
        ' the sounded spans → [speech][speech][apad-silence] files. Silence
        ' is TIME: every callback gap is repaired inline BEFORE the buffer
        ' (AudioTimelineRepair — same owner-validated model as AudioTap v2).
        Public Property Repair As AudioTimelineRepair
        Public Property RepairInsertedBytes As Long  ' silence enqueued into the queue
        Public Property RepairDroppedBytes As Long   ' silence descriptors dropped under queue pressure (timeline compression — logged)
        Public Property Started As Boolean
        Public Property BytesPerSecond As Integer
        Public Property FrameSize As Integer
        Public Property InFlightCallbacks As Integer  ' for shutdown lifecycle
        Public Property Lifecycle As Integer  ' TrackLifecycle enum (Integer for Interlocked)
    End Class

    Public Class AudioConfigValues
        Public Property SystemAudioCapture As Boolean = False
        Public Property MicCapture As Boolean = False
        Public Property SystemAudioVolume As Single = 1.0F
        Public Property MicVolume As Single = 1.0F
        Public Property MicDeviceId As String = ""
        Public Property MicDeviceName As String = ""
    End Class

    Private _config As AudioConfigValues
    Private _tracks As New List(Of TrackState)
    Private _isRunning As Boolean = False
    Private _disposed As Boolean = False

    ' ── Per-track actual start timestamps ──
    ' Set when the FIRST WASAPI callback fires for each track.
    ' This is the true capture start time (after device init), used for
    ' precise audio-video sync alignment at mux time.
    Private _systemStartTicks As Long = 0
    Private _micStartTicks As Long = 0

    Public ReadOnly Property SystemStartTicks As Long
        Get
            Return _systemStartTicks
        End Get
    End Property

    Public ReadOnly Property MicStartTicks As Long
        Get
            Return _micStartTicks
        End Get
    End Property

    Public Event SystemStartFailed(reason As String)
    Public Event MicStartFailed(reason As String)
    Public Event SystemFormatDetected(format As AudioFormat)
    Public Event MicFormatDetected(format As AudioFormat)

    Public Sub New(config As AudioConfigValues)
        _config = config
    End Sub

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _isRunning
        End Get
    End Property

    ''' <summary>
    ''' Start recording audio to .wav files.
    ''' Returns True if at least one track started successfully.
    ''' </summary>
    Public Function Start(systemPath As String, micPath As String) As Boolean
        If _disposed Then Throw New ObjectDisposedException(NameOf(AudioFileWriter))
        If _isRunning Then Return True

        Dim anyStarted As Boolean = False

        If _config.SystemAudioCapture Then
            Dim sysTrack As New TrackState With {
                .Config = New TrackConfig With {
                    .Enabled = True,
                    .IsSystem = True,
                    .OutputPath = systemPath,
                    .Volume = _config.SystemAudioVolume
                }
            }
            If StartTrack(sysTrack) Then
                anyStarted = True
            Else
                RaiseEvent SystemStartFailed(sysTrack.FailReason)
            End If
            _tracks.Add(sysTrack)
        End If

        If _config.MicCapture AndAlso
           (Not String.IsNullOrEmpty(_config.MicDeviceId) OrElse
            Not String.IsNullOrEmpty(_config.MicDeviceName)) Then
            Dim micTrack As New TrackState With {
                .Config = New TrackConfig With {
                    .Enabled = True,
                    .IsSystem = False,
                    .DeviceId = _config.MicDeviceId,
                    .DeviceName = _config.MicDeviceName,
                    .OutputPath = micPath,
                    .Volume = _config.MicVolume
                }
            }
            If StartTrack(micTrack) Then
                anyStarted = True
            Else
                RaiseEvent MicStartFailed(micTrack.FailReason)
            End If
            _tracks.Add(micTrack)
        End If

        _isRunning = anyStarted
        Return anyStarted
    End Function

    Private Function StartTrack(track As TrackState) As Boolean
        Try
            Dim device As MMDevice = Nothing
            If track.Config.IsSystem Then
                Using devEnum As New MMDeviceEnumerator()
                    device = devEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                End Using
                If device Is Nothing Then
                    track.Failed = True
                    track.FailReason = "No default audio render device found"
                    Return False
                End If
                track.Capture = New WasapiLoopbackCapture(device)
            Else
                device = FindMicDevice(track.Config.DeviceId, track.Config.DeviceName)
                If device Is Nothing Then
                    track.Failed = True
                    track.FailReason = "Mic device not found: " & track.Config.DeviceName
                    Return False
                End If
                track.Capture = New WasapiCapture(device)
            End If

            Dim dir As String = Path.GetDirectoryName(track.Config.OutputPath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            track.Writer = New WaveFileWriter(track.Config.OutputPath, track.Capture.WaveFormat)
            track.BytesPerSecond = track.Capture.WaveFormat.AverageBytesPerSecond
            track.FrameSize = (track.Capture.WaveFormat.BitsPerSample \ 8) * track.Capture.WaveFormat.Channels

            ' Bounded queue: 1000 items ≈ 10 seconds of audio.
            ' If writer can't keep up (slow disk), we drop oldest to prevent
            ' blocking the WASAPI capture callback.
            track.Queue = New BlockingCollection(Of AudioChunk)(1000)

            ' Start writer thread (consumer)
            track.WriterThread = New Thread(Sub() WriterLoop(track)) With {
                .IsBackground = True,
                .Name = If(track.Config.IsSystem, "AudioWriter-Sys", "AudioWriter-Mic"),
                .Priority = ThreadPriority.Normal
            }
            track.WriterThread.Start()

            Dim handlerRef As TrackState = track
            AddHandler track.Capture.DataAvailable, Sub(sender As Object, e As WaveInEventArgs)
                                                         OnDataAvailable(handlerRef, e)
                                                     End Sub

            ' Set lifecycle to Running BEFORE StartRecording (so first callback sees Running)
            System.Threading.Interlocked.Exchange(track.Lifecycle, CInt(TrackLifecycle.Running))

            ' ── Record StartRecording call time (NOT first callback time) ──
            ' This is the TRUE audio capture start timestamp. WASAPI loopback
            ' doesn't fire callbacks when no audio is playing, so the first
            ' callback may arrive seconds late. But the capture TIMELINE starts
            ' here, when StartRecording() is called.
            '
            ' Using first-callback-time as "audio start" was a BUG that caused
            ' sysOffset to be -10s (clamped to -2s) when no audio played for 10s.
            ' The correct offset should be based on when capture STARTED, not when
            ' the first audio data arrived.
            Dim startRecordingTicks As Long = Stopwatch.GetTimestamp()
            track.StartRecordingTicks = startRecordingTicks
            If track.Config.IsSystem Then
                _systemStartTicks = startRecordingTicks
            Else
                _micStartTicks = startRecordingTicks
            End If

            ' ── P13-AUDIO-TIMELINE: wall-clock gap repair anchor ──
            ' Anchored at the StartRecording CALL time (proven model — NOT
            ' first-callback time): the first OnCallback measures exactly
            ' the device warm-up and returns it as the prefill silence.
            track.Repair = New AudioTimelineRepair(track.BytesPerSecond, track.FrameSize, startRecordingTicks)

            track.Capture.StartRecording()

            Dim fmt As AudioFormat = WaveFormatToInfo(track.Capture.WaveFormat)
            If track.Config.IsSystem Then
                RaiseEvent SystemFormatDetected(fmt)
            Else
                RaiseEvent MicFormatDetected(fmt)
            End If

            Return True
        Catch ex As Exception
            ' Clean up partial state on failure (per GPT P1)
            CleanupTrack(track)
            track.Failed = True
            track.FailReason = ex.Message
            Return False
        End Try
    End Function

    ''' <summary>
    ''' WASAPI callback — ultra-light: copy buffer, enqueue, return.
    '''
    ''' NO gap detection, NO silence insertion, NO disk I/O, NO volume processing.
    ''' All heavy lifting happens at mux time (apad, aresample, volume filters).
    '''
    ''' CALLBACK LIFECYCLE (per GPT P0.2):
    '''   1. Interlocked.Increment(InFlightCallbacks) — MUST be first, before any check
    '''   2. Read lifecycle state (Running or Draining both accept data)
    '''   3. If Stopped or zero bytes, exit (but Decrement still runs via Finally)
    '''   4. Copy + enqueue
    '''   5. Finally: Interlocked.Decrement(InFlightCallbacks)
    '''
    ''' This fixes the P0 race where the old code checked _isRunning BEFORE
    ''' incrementing, so callbacks arriving after _isRunning=False were dropped
    ''' without being counted — losing the final audio chunk.
    '''
    ''' GAP DETECTION (per GPT P0.3):
    '''   REMOVED entirely. The old wall-clock gap detection was fundamentally
    '''   broken because callback dispatch time ≠ audio capture time (OS scheduler
    '''   jitter caused false positives). True sample-accurate gap detection
    '''   would require IAudioCaptureClient::GetBuffer() with device timestamps,
    '''   which NAudio doesn't expose.
    '''
    '''   Instead, the mux stage's apad filter pads short audio with silence
    '''   to match video duration. This is honest about what we can and cannot
    '''   measure, and avoids corrupting the audio timeline with false silence.
    ''' </summary>
    Private Sub OnDataAvailable(track As TrackState, e As WaveInEventArgs)
        ' MUST increment FIRST (per GPT P0.2) — before any lifecycle check.
        ' This ensures in-flight callbacks are counted even during Draining,
        ' so Stop() can deterministically wait for them to complete.
        System.Threading.Interlocked.Increment(track.InFlightCallbacks)
        Try
            ' Read lifecycle state once (thread-safe via Interlocked)
            Dim lifecycle As Integer = System.Threading.Interlocked.CompareExchange(track.Lifecycle, 0, 0)

            ' Accept data in Running OR Draining states.
            ' Draining = capture.StopRecording() called but in-flight callbacks
            ' should still enqueue their data (prevents losing final audio chunk).
            ' Only reject in Stopped state (fully shut down).
            If lifecycle = CInt(TrackLifecycle.Stopped) Then Return
            If e.BytesRecorded = 0 Then Return

            Dim nowTicks As Long = Stopwatch.GetTimestamp()

            ' ── P13-AUDIO-TIMELINE: OBS-model silence repair (2026-08-28) ──
            ' Silence is part of the timeline (OBS rule — owner-confirmed via
            ' GPT analysis). Every callback computes the silence that belongs
            ' BEFORE its buffer:
            '   first callback → StartRecording→now warm-up (prefill rule:
            '     deterministic, no 50ms cutoff — WASAPI loopback does not
            '     fire while nothing plays, so the head gap is real time)
            '   later callbacks → (now − last) − bufferDuration, 50ms
            '     threshold, 3600s cap, block-aligned (proven AudioTap v2)
            ' Without this the WAV concatenates only the sounded spans and
            ' the mux's apad moves ALL the session's silence to the file end
            ' — the exact [speech][speech][long silence] owner evidence.
            Dim isFirstCallback As Boolean = Not track.Started
            If isFirstCallback Then
                track.Started = True
                track.FirstCallbackDispatchTicks = nowTicks
            End If

            Dim repairSilence As Long = 0
            If track.Repair IsNot Nothing Then
                repairSilence = track.Repair.OnCallback(nowTicks, e.BytesRecorded)
            End If

            If repairSilence > 0 Then
                ' Enqueue a SINGLE silence descriptor chunk (per GPT P0).
                ' The writer thread expands it into zero-filled bytes on
                ' disk — no allocation in the WASAPI callback thread.
                Dim silenceDescriptor As New AudioChunk With {
                    .Data = Nothing,
                    .IsSilence = True,
                    .SilenceBytes = repairSilence,
                    .IsInitial = isFirstCallback
                }
                Try
                    If track.Queue.TryAdd(silenceDescriptor, 0) Then
                        If isFirstCallback Then track.InitialSilenceBytes += repairSilence
                        track.BytesEnqueued += repairSilence
                        track.RepairInsertedBytes += repairSilence
                    End If
                Catch ex As InvalidOperationException
                    ' Queue completed during shutdown — silence lost, real
                    ' audio below still attempts its own TryAdd.
                End Try
            End If

            ' ── Copy audio data + enqueue (NO disk I/O here) ──
            Dim copy As Byte() = New Byte(e.BytesRecorded - 1) {}
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded)
            Dim realChunk As New AudioChunk With {
                .Data = copy,
                .IsSilence = False
            }

            ' Try to enqueue without blocking. Wrap in Try/Catch because
            ' BlockingCollection.TryAdd throws InvalidOperationException after
            ' CompleteAdding() is called (race during shutdown — see GPT review).
            ' This is a benign shutdown race: the data is lost but the callback
            ' exits cleanly instead of throwing into the WASAPI dispatch thread.
            '
            ' PRIORITY DROP POLICY (per GPT P0):
            '   When queue is full and incoming is REAL audio:
            '   - Drop oldest item (TryTake removes from front of FIFO queue)
            '   - Since silence descriptor is at FRONT of queue (enqueued by first
            '     callback before real audio), TryTake naturally drops it first
            '   - Check IsSilence flag on dropped item to update correct counter:
            '     * Silence dropped → decrement InitialSilenceBytes (good, made room)
            '     * Real audio dropped → increment DroppedBytes (data loss)
            '   This prevents synthetic silence from displacing real PCM, AND
            '   keeps diagnostics honest about what was actually dropped.
            '
            ' EXACT-SUCCESS TRACKING (per GPT P1):
            '   BytesEnqueued/CallbackCount only incremented when TryAdd succeeds.
            '   This keeps BytesEnqueued honest about "actual bytes in queue/WAV".
            Dim enqueuedSuccess As Boolean = False
            Try
                If track.Queue.TryAdd(realChunk, 0) Then
                    enqueuedSuccess = True
                Else
                    ' Queue full — drop oldest to make room
                    Dim dropped As AudioChunk = Nothing
                    If track.Queue.TryTake(dropped) Then
                        If dropped.IsSilence Then
                            ' Dropped silence — preferred outcome (real PCM is
                            ' protected). NOTE: dropping MID-STREAM silence
                            ' compresses the timeline (the span vanishes) —
                            ' counted separately so diagnostics can flag it.
                            Dim droppedLen As Long = If(dropped.Data IsNot Nothing,
                                                       dropped.Data.Length,
                                                       dropped.SilenceBytes)
                            If dropped.IsInitial Then
                                track.InitialSilenceBytes = Math.Max(0, track.InitialSilenceBytes - droppedLen)
                            End If
                            track.RepairDroppedBytes += droppedLen
                            track.DroppedSilenceBytes += droppedLen
                        Else
                            ' Dropped real audio — actual data loss, track it
                            Dim droppedLen As Long = If(dropped.Data IsNot Nothing,
                                                       dropped.Data.Length,
                                                       0)
                            track.DroppedChunks += 1
                            track.DroppedBytes += droppedLen
                            If track.FrameSize > 0 Then
                                track.DroppedSamples += droppedLen \ track.FrameSize
                            End If
                            If track.BytesPerSecond > 0 Then
                                track.DroppedDurationSec += CDbl(droppedLen) / track.BytesPerSecond
                            End If
                        End If
                        ' Now add the real audio (should succeed since we made room)
                        enqueuedSuccess = track.Queue.TryAdd(realChunk, 0)
                    End If
                End If
            Catch ex As InvalidOperationException
                ' Queue completed during shutdown — drop this chunk silently
                ' (better than throwing into the WASAPI dispatch thread)
                Return
            End Try

            ' Only count as enqueued if it actually went into the queue
            If enqueuedSuccess Then
                track.CallbackCount += 1
                track.BytesEnqueued += e.BytesRecorded
                If track.FrameSize > 0 Then
                    track.SamplesEnqueued += e.BytesRecorded \ track.FrameSize
                End If
            End If
        Finally
            System.Threading.Interlocked.Decrement(track.InFlightCallbacks)
        End Try
    End Sub

    ''' <summary>
    ''' Writer thread — consumes AudioChunk from queue, writes to disk.
    ''' This is the ONLY thread that touches WaveFileWriter, so no lock needed.
    ''' Tracks WrittenBytes for diagnostics (separate from BytesEnqueued to
    ''' detect writer thread lag or disk write failures).
    '''
    ''' Handles two chunk types:
    '''   - Real PCM (Data != Nothing): Write directly
    '''   - Silence descriptor (IsSilence=True, Data=Nothing): Expand into
    '''     zero-filled chunks and write to disk. This is where the actual
    '''     allocation happens — NOT in the WASAPI callback. Keeps the callback
    '''     lightweight (single TryAdd of a metadata chunk).
    ''' </summary>
    Private Sub WriterLoop(track As TrackState)
        ' Pre-allocate a zero-filled buffer for silence expansion (reused, not GC'd)
        Const silenceChunkSize As Integer = 16384
        Dim silenceBuffer As Byte() = New Byte(silenceChunkSize - 1) {}
        Try
            While True
                Dim chunk As AudioChunk = Nothing
                If track.Queue.TryTake(chunk, 1000) Then
                    Try
                        If track.Writer Is Nothing Then Continue While

                        If chunk.IsSilence AndAlso chunk.Data Is Nothing Then
                            ' Silence descriptor — expand into zero-filled writes
                            ' This is the actual allocation/expansion work, done
                            ' here on the writer thread instead of the callback.
                            Dim remaining As Long = chunk.SilenceBytes
                            While remaining > 0
                                Dim size As Integer = CInt(Math.Min(remaining, CLng(silenceChunkSize)))
                                If track.FrameSize > 0 Then size = (size \ track.FrameSize) * track.FrameSize
                                If size <= 0 Then Exit While
                                track.Writer.Write(silenceBuffer, 0, size)
                                track.WrittenBytes += size
                                remaining -= size
                            End While
                        ElseIf chunk.Data IsNot Nothing Then
                            ' Real PCM — write directly
                            track.Writer.Write(chunk.Data, 0, chunk.Data.Length)
                            track.WrittenBytes += chunk.Data.Length
                        End If
                    Catch ex As Exception
                        ' ── Propagate writer failure to track state (per GPT P1) ──
                        ' Without this, capture continues thinking everything is fine
                        ' but the WAV file is silently incomplete. Set Failed=True so
                        ' GetDiagnostics() shows it and the mux stage can decide
                        ' whether to use the partial WAV or skip it.
                        track.Failed = True
                        track.FailReason = "Writer thread: " & ex.Message
                        System.Diagnostics.Debug.WriteLine("[AudioFileWriter] Writer write FAILED: " & ex.Message)
                        Exit While
                    End Try
                ElseIf track.Queue.IsCompleted Then
                    Exit While
                End If
            End While
        Catch ex As Exception
            ' Top-level catch (e.g. TryTake threw) — also propagate failure
            track.Failed = True
            track.FailReason = "Writer thread (top-level): " & ex.Message
            System.Diagnostics.Debug.WriteLine("[AudioFileWriter] Writer thread crashed: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Clean up partial state when StartTrack fails partway through.
    ''' Disposes all resources that were created before the failure point
    ''' (per GPT P1 — prevents resource leaks on initialization failure).
    ''' </summary>
    Private Sub CleanupTrack(track As TrackState)
        ' Set lifecycle to Stopped first (so any callback that managed to attach exits early)
        System.Threading.Interlocked.Exchange(track.Lifecycle, CInt(TrackLifecycle.Stopped))

        Try
            If track.Queue IsNot Nothing Then
                track.Queue.CompleteAdding()
                track.Queue.Dispose()
                track.Queue = Nothing
            End If
        Catch
        End Try

        Try
            If track.WriterThread IsNot Nothing AndAlso track.WriterThread.IsAlive Then
                track.WriterThread.Join(2000)
            End If
        Catch
        End Try

        Try
            If track.Writer IsNot Nothing Then
                track.Writer.Dispose()
                track.Writer = Nothing
            End If
        Catch
        End Try

        Try
            If track.Capture IsNot Nothing Then
                track.Capture.Dispose()
                track.Capture = Nothing
            End If
        Catch
        End Try
    End Sub

    Private Function FindMicDevice(deviceId As String, deviceName As String) As MMDevice
        Using devEnum As New MMDeviceEnumerator()
            Dim devices As MMDeviceCollection = devEnum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)

            If Not String.IsNullOrEmpty(deviceId) Then
                For Each dev As MMDevice In devices
                    If dev.ID = deviceId Then Return dev
                Next
            End If

            If Not String.IsNullOrEmpty(deviceName) Then
                For Each dev As MMDevice In devices
                    If String.Equals(dev.FriendlyName, deviceName, StringComparison.Ordinal) Then Return dev
                Next
            End If

            Return Nothing
        End Using
    End Function

    Private Function WaveFormatToInfo(wf As WaveFormat) As AudioFormat
        Dim info As New AudioFormat()
        If wf Is Nothing Then Return info
        info.SampleRate = wf.SampleRate
        info.Channels = wf.Channels
        info.BitsPerSample = wf.BitsPerSample
        info.IsFloat = (wf.Encoding = WaveFormatEncoding.IeeeFloat)
        info.ChannelLayout = AudioFormat.LayoutFromChannelCount(wf.Channels)
        Return info
    End Function

    ''' <summary>
    ''' Stop all captures. Deterministic shutdown via state machine (per GPT P0.1):
    '''   Running → Draining → Stopped
    '''
    ''' Sequence:
    '''   1. Set lifecycle = Draining (callbacks still accept data in Draining)
    '''   2. Stop WASAPI capture (no NEW callbacks dispatched)
    '''   3. Wait for in-flight callbacks to complete (deterministic, not Sleep)
    '''   4. Set lifecycle = Stopped (any late callback will exit early)
    '''   5. Signal queue complete (writer thread can finish remaining items)
    '''   6. Wait for writer thread (flushes all data to disk)
    '''   7. Finalize WAV files (flush + dispose = writes correct header)
    '''
    ''' CRITICAL: In-flight callbacks ARE allowed to complete and enqueue data
    ''' during Draining state. This prevents losing the final audio chunk.
    ''' The callback uses Try/Finally with Interlocked.Increment/Decrement so
    ''' we can deterministically wait for them to finish.
    ''' </summary>
    Public Sub [Stop]()
        _isRunning = False

        ' ── Step 1: Set lifecycle = Draining (callbacks still accept data) ──
        For Each track As TrackState In _tracks
            System.Threading.Interlocked.Exchange(track.Lifecycle, CInt(TrackLifecycle.Draining))
        Next

        ' ── Step 2: Stop captures (no new callbacks dispatched) ──
        For Each track As TrackState In _tracks
            Try
                If track.Capture IsNot Nothing Then
                    track.Capture.StopRecording()
                End If
            Catch
            End Try
        Next

        ' ── Step 3: Wait for in-flight callbacks (deterministic, not Sleep) ──
        ' Each callback does Interlocked.Increment at entry (BEFORE lifecycle check),
        ' Decrement at exit (via Finally). We spin-wait until all complete, 2s timeout.
        ' This ensures NO audio data is lost — in-flight callbacks during Draining
        ' still enqueue their data before we transition to Stopped.
        Dim waitDeadline As Long = Stopwatch.GetTimestamp() + CLng(2.0 * Stopwatch.Frequency)
        Do
            Dim anyInFlight As Boolean = False
            For Each track As TrackState In _tracks
                If System.Threading.Interlocked.CompareExchange(track.InFlightCallbacks, 0, 0) > 0 Then
                    anyInFlight = True
                    Exit For
                End If
            Next
            If Not anyInFlight Then Exit Do
            Thread.Sleep(2)
        Loop While Stopwatch.GetTimestamp() < waitDeadline

        ' ── Step 4: Set lifecycle = Stopped (late callbacks exit early) ──
        For Each track As TrackState In _tracks
            System.Threading.Interlocked.Exchange(track.Lifecycle, CInt(TrackLifecycle.Stopped))
        Next

        ' ── Step 5: Signal queues to complete ──
        For Each track As TrackState In _tracks
            Try
                If track.Queue IsNot Nothing Then
                    track.Queue.CompleteAdding()
                End If
            Catch
            End Try
        Next

        ' ── Step 6: Wait for writer threads to finish ──
        ' This flushes ALL remaining data in the queue to disk.
        For Each track As TrackState In _tracks
            Try
                If track.WriterThread IsNot Nothing AndAlso track.WriterThread.IsAlive Then
                    track.WriterThread.Join(10000)
                End If
            Catch
            End Try

            ' ── Step 5: Finalize WAV file ──
            Try
                If track.Writer IsNot Nothing Then
                    track.Writer.Flush()
                    track.Writer.Dispose()
                    track.Writer = Nothing
                End If
            Catch
            End Try

            Try
                If track.Capture IsNot Nothing Then
                    track.Capture.Dispose()
                    track.Capture = Nothing
                End If
            Catch
            End Try
        Next
    End Sub

    Public Function GetDiagnostics() As String
        Dim sb As New Text.StringBuilder()
        For Each track As TrackState In _tracks
            Dim label As String = If(track.Config.IsSystem, "Sys", "Mic")
            sb.AppendLine("[Audio] " & label & "Callbacks=" & track.CallbackCount)
            sb.AppendLine("[Audio] " & label & "BytesEnqueued=" & track.BytesEnqueued)
            sb.AppendLine("[Audio] " & label & "WrittenBytes=" & track.WrittenBytes)
            Dim writeLagBytes As Long = track.BytesEnqueued - track.WrittenBytes
            sb.AppendLine("[Audio] " & label & "WriteLagBytes=" & writeLagBytes)
            sb.AppendLine("[Audio] " & label & "SamplesEnqueued=" & track.SamplesEnqueued)
            sb.AppendLine("[Audio] " & label & "DroppedChunks=" & track.DroppedChunks)
            sb.AppendLine("[Audio] " & label & "DroppedBytes=" & track.DroppedBytes)
            sb.AppendLine("[Audio] " & label & "DroppedSamples=" & track.DroppedSamples)
            sb.AppendLine("[Audio] " & label & "DroppedDurationSec=" & track.DroppedDurationSec.ToString("F3"))
            sb.AppendLine("[Audio] " & label & "DroppedSilenceBytes=" & track.DroppedSilenceBytes)
            Dim droppedSilenceSec As Double = If(track.BytesPerSecond > 0, CDbl(track.DroppedSilenceBytes) / track.BytesPerSecond, 0)
            sb.AppendLine("[Audio] " & label & "DroppedSilenceSec=" & droppedSilenceSec.ToString("F3"))
            ' ── Invariant check (per GPT P1 #2) ──
            ' After writer drains completely (WriteLagBytes ≈ 0):
            '   BytesEnqueued ≈ WrittenBytes + DroppedBytes + DroppedSilenceBytes
            ' Any non-zero residual (BytesEnqueued - WrittenBytes - DroppedBytes - DroppedSilenceBytes)
            ' after Stop() indicates writer did not drain properly OR a counting bug.
            ' We expose this as BytesAccountingResidual for diagnostics.
            Dim accountingResidual As Long = track.BytesEnqueued - track.WrittenBytes - track.DroppedBytes - track.DroppedSilenceBytes
            sb.AppendLine("[Audio] " & label & "BytesAccountingResidual=" & accountingResidual)
            sb.AppendLine("[Audio] " & label & "Started=" & track.Started.ToString())
            sb.AppendLine("[Audio] " & label & "StartRecordingTicks=" & track.StartRecordingTicks)
            sb.AppendLine("[Audio] " & label & "FirstCallbackDispatchTicks=" & track.FirstCallbackDispatchTicks)
            Dim cbDelayMs As Double = 0.0
            If track.StartRecordingTicks > 0 AndAlso track.FirstCallbackDispatchTicks > 0 Then
                cbDelayMs = (track.FirstCallbackDispatchTicks - track.StartRecordingTicks) * 1000.0 / Stopwatch.Frequency
            End If
            sb.AppendLine("[Audio] " & label & "FirstCallbackDelayMs=" & cbDelayMs.ToString("F1"))
            sb.AppendLine("[Audio] " & label & "InitialSilenceBytes=" & track.InitialSilenceBytes)
            Dim initSilenceSec As Double = If(track.BytesPerSecond > 0, CDbl(track.InitialSilenceBytes) / track.BytesPerSecond, 0)
            sb.AppendLine("[Audio] " & label & "InitialSilenceSec=" & initSilenceSec.ToString("F3"))
            sb.AppendLine("[Audio] " & label & "Lifecycle=" & CType(track.Lifecycle, TrackLifecycle).ToString())
            sb.AppendLine("[Audio] " & label & "Path=" & track.Config.OutputPath)
            If track.Failed Then
                sb.AppendLine("[Audio] " & label & "FAILED: " & track.FailReason)
            End If
        Next
        sb.AppendLine("[Audio] ShutdownRequested=" & If(Not _isRunning, "1", "0"))
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' True only when the wav contains actual audio samples. A writer stopped
    ''' before the first WASAPI callback still leaves a 58-byte header
    ''' (RIFF+fmt+fact+data with size 0), so "Length > 44" alone cannot tell it
    ''' from real content — that once made the muxer apad a zero-sample stream,
    ''' hang to the 60s timeout and ship a broken 48-byte mp4. Streaming writers
    ''' (AudioEngine sidecar) also leave the data-chunk size unpatched — 0 or
    ''' 0xFFFFFFFF — while payload bytes exist, so for the data chunk the bytes
    ''' actually present after the chunk header are the truth.
    ''' </summary>
    Public Shared Function HasAudioData(wavPath As String) As Boolean
        Try
            If Not File.Exists(wavPath) Then Return False
            If New FileInfo(wavPath).Length <= 44 Then Return False
            Using fs As FileStream = File.OpenRead(wavPath)
                Using br As New BinaryReader(fs)
                    If br.BaseStream.Length < 12 Then Return False
                    If System.Text.Encoding.ASCII.GetString(br.ReadBytes(4)) <> "RIFF" Then Return False
                    br.BaseStream.Seek(8, SeekOrigin.Begin)
                    If System.Text.Encoding.ASCII.GetString(br.ReadBytes(4)) <> "WAVE" Then Return False
                    While br.BaseStream.Position + 8 <= br.BaseStream.Length
                        Dim chunkId As String = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4))
                        Dim chunkSize As UInteger = br.ReadUInt32()
                        If chunkId = "data" Then
                            ' Streaming wavs carry 0 (or 0xFFFFFFFF) here; the
                            ' payload trailing the header is the real content.
                            Return br.BaseStream.Length - br.BaseStream.Position > 0
                        End If
                        If chunkSize > CUInt(br.BaseStream.Length - br.BaseStream.Position) Then Return False
                        br.BaseStream.Seek(chunkSize + (chunkSize Mod 2), SeekOrigin.Current)
                    End While
                    Return False
                End Using
            End Using
        Catch
            Return False
        End Try
    End Function

    Public Shared Function ListMicDevices() As List(Of Tuple(Of String, String))
        Dim result As New List(Of Tuple(Of String, String))()
        Try
            Using devEnum As New MMDeviceEnumerator()
                For Each dev As MMDevice In devEnum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    result.Add(New Tuple(Of String, String)(dev.ID, dev.FriendlyName))
                Next
            End Using
        Catch
        End Try
        Return result
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        [Stop]()
    End Sub

End Class
