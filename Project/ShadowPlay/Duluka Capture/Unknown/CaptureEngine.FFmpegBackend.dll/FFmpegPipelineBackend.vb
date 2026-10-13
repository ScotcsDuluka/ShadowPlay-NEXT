Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports CaptureEngine.Backends

Namespace CaptureEngine.FFmpegBackend
    ''' <summary>
    ''' FFmpegPipelineBackend — the orchestrator. Implements IVideoBackend
    ''' (fire-and-forget model: GetFrame() returns Nothing).
    '''
    ''' P1-C Commit 5: Generation guard + Mux failure propagation + Muxing state.
    '''
    ''' Lifecycle:
    '''   Created → Starting → Running → Stopping → [Muxing] → Stopped → Disposed
    '''                                                    ↘ Faulted ↗
    '''
    ''' Ownership:
    '''   - FFmpegProcessHost (owns the FFmpeg Process)
    '''   - FFmpegStderrParser (parses stderr stats)
    '''   - AudioSidecar (WASAPI → temp.wav, conditional on audio config)
    '''   - MuxCoordinator (ffprobe + mux FFmpeg, created lazily at Stop time)
    '''
    ''' Critical thread safety rules (per OWNER):
    '''   🔴 NEVER hold SyncLock across:
    '''       - WaitForExit()
    '''       - Kill()
    '''       - Mux()
    '''       - ffprobe()
    '''   🔴 NEVER RaiseEvent inside SyncLock:
    '''       - Capture state under lock → release lock → fire event
    '''   🔴 Guard against 3-way race:
    '''       FFmpeg exited (OnExited, threadpool) + Stop() (caller) + Dispose() (caller/finalizer)
    '''       Use Interlocked.Exchange(_stopCompleted, 1) — first wins, others no-op.
    '''   🔴 Generation guard:
    '''       Each Start() increments _currentGeneration. Callbacks carry the generation
    '''       they were created with. If it doesn't match _currentGeneration, the callback
    '''       is from a stale (previous session) process and must be ignored.
    ''' </summary>
    ''' ★ C/5 DORMANT (audit 2026-09-06): ZERO production callers — superseded by
    ''' LiveMuxSession. Friend = compile-time isolation: code outside this assembly
    ''' cannot wire this in and bypass the NextRecordingConfig.MapSessionConfig H1
    ''' seam (its WithArguments/WithOutputPath API takes raw unvalidated args).
    ''' Tests reach it via InternalsVisibleTo. Do NOT re-activate without the seam.
    Friend NotInheritable Class FFmpegPipelineBackend
        Implements IVideoBackend
        Implements IDisposable

        ' ── State ──
        Private ReadOnly _sync As New Object()
        Private _state As VideoBackendState = VideoBackendState.Created
        Private _disposed As Boolean = False
        Private _stopCompleted As Integer = 0  ' Interlocked guard for 3-way race
        Private _muxCompleted As Integer = 0   ' Guard against double-mux

        ' ── Generation guard (P0 fix) ──
        Private _currentGeneration As Integer = 0

        ' ── Components (owned) ──
        Private _processHost As FFmpegProcessHost
        Private _stderrParser As FFmpegStderrParser
        Private _audioSidecar As AudioSidecar
        Private _muxCoordinator As MuxCoordinator

        ' ── Config (set before Start) ──
        Private _ffmpegPath As String = ""
        Private _arguments As String = ""
        Private _outputPath As String = ""
        Private _useTwoProcess As Boolean = False
        Private _tempVideoPath As String = ""
        Private _tempSystemWav As String = ""
        Private _tempMicWav As String = ""
        Private _systemAudioEnabled As Boolean = False
        Private _micEnabled As Boolean = False
        Private _systemVolume As Single = 1.0F
        Private _micVolume As Single = 1.0F
        Private _separateTracks As Boolean = False

        ' ── Sync timestamps ──
        Private _videoStartTicks As Long = 0
        Private _videoStartDetected As Boolean = False
        Private _systemStartTicks As Long = 0
        Private _micStartTicks As Long = 0

        ' ── Events (fired OUTSIDE lock) ──
        Public Event StateChanged As Action(Of VideoBackendState)
        Public Event RecordingStarted As Action(Of String)
        Public Event RecordingStopped As Action(Of String)
        Public Event ErrorOccurred As Action(Of String)

        ' ===== IVideoBackend =====

        Public ReadOnly Property CurrentState As VideoBackendState Implements IVideoBackend.CurrentState
            Get
                SyncLock _sync
                    Return _state
                End SyncLock
            End Get
        End Property

        Public Sub Start() Implements IVideoBackend.Start
            SyncLock _sync
                If _disposed Then
                    Throw New ObjectDisposedException(NameOf(FFmpegPipelineBackend))
                End If
                If _state = VideoBackendState.Running OrElse _state = VideoBackendState.Starting Then
                    Return  ' Idempotent — already started
                End If
                If _state <> VideoBackendState.Created AndAlso _state <> VideoBackendState.Stopped Then
                    Throw New InvalidOperationException(
                        "Start cannot be called from state '" & _state.ToString() & "'.")
                End If
                _state = VideoBackendState.Starting

                ' Increment generation for this new session
                _currentGeneration += 1
            End SyncLock

            FireStateChanged(VideoBackendState.Starting)

            Try
                ' Create components
                _stderrParser = New FFmpegStderrParser()

                ' Dispose previous process host if any (from a previous session)
                If _processHost IsNot Nothing Then
                    RemoveHandler _processHost.Exited, AddressOf OnProcessExited
                    RemoveHandler _processHost.StderrLine, AddressOf OnStderrLine
                    _processHost.Dispose()
                End If

                _processHost = New FFmpegProcessHost()
                _processHost.FFmpegPath = _ffmpegPath
                _processHost.Arguments = _arguments
                _processHost.OutputPath = If(_useTwoProcess, _tempVideoPath, _outputPath)

                ' Wire events (callbacks carry generation from ProcessHost)
                AddHandler _processHost.Exited, AddressOf OnProcessExited
                AddHandler _processHost.StderrLine, AddressOf OnStderrLine

                ' Reset stop guard for this new session
                Threading.Interlocked.Exchange(_stopCompleted, 0)
                Threading.Interlocked.Exchange(_muxCompleted, 0)
                _videoStartDetected = False
                _videoStartTicks = 0

                ' Start FFmpeg (ProcessHost increments its own _generation internally)
                _processHost.Start()

                ' Start audio sidecar (if enabled)
                If _useTwoProcess Then
                    _audioSidecar = New AudioSidecar()
                    _audioSidecar.SystemAudioEnabled = _systemAudioEnabled
                    _audioSidecar.MicEnabled = _micEnabled
                    _audioSidecar.TempSystemWavPath = _tempSystemWav
                    _audioSidecar.TempMicWavPath = _tempMicWav
                    _audioSidecar.Start()
                    _systemStartTicks = _audioSidecar.SystemStartTicks
                    _micStartTicks = _audioSidecar.MicStartTicks
                End If

                ' Transition to Running
                SyncLock _sync
                    _state = VideoBackendState.Running
                End SyncLock
                FireStateChanged(VideoBackendState.Running)
                FireRecordingStarted(_outputPath)

            Catch ex As Exception
                SyncLock _sync
                    _state = VideoBackendState.Faulted
                End SyncLock
                FireStateChanged(VideoBackendState.Faulted)
                FireErrorOccurred("Start failed: " & ex.Message)
                Throw
            End Try
        End Sub

        Public Function GetFrame() As VideoFrame Implements IVideoBackend.GetFrame
            ' Full-pipeline backend: FFmpeg writes to file, not to frames.
            Return Nothing
        End Function

        Public Sub [Stop]() Implements IVideoBackend.[Stop]
            ' Guard: only first caller of Stop/Dispose/OnExited proceeds
            If Threading.Interlocked.Exchange(_stopCompleted, 1) <> 0 Then Return

            Dim prevState As VideoBackendState
            SyncLock _sync
                prevState = _state
                If prevState = VideoBackendState.Disposed Then Return
                ' If not Running, Stop is a no-op
                If prevState <> VideoBackendState.Running Then Return
                _state = VideoBackendState.Stopping
            End SyncLock
            FireStateChanged(VideoBackendState.Stopping)

            ' ─── Step 1: Send 'q' to FFmpeg (OUTSIDE lock) ───
            If _processHost IsNot Nothing AndAlso Not _processHost.HasExited Then
                _processHost.SendQuit()

                ' Wait for FFmpeg to exit (OUTSIDE lock — 10s timeout)
                If Not _processHost.WaitForExit(10000) Then
                    ' Timeout — kill
                    _processHost.Kill()
                    _processHost.WaitForExit(2000)
                End If
            End If

            ' ─── Step 2: Stop audio sidecar (AFTER FFmpeg exit) ───
            If _audioSidecar IsNot Nothing Then
                _audioSidecar.Stop()
            End If

            ' ─── Step 3: Mux (if two-process mode) ───
            Dim muxSucceeded As Boolean = True
            If _useTwoProcess Then
                muxSucceeded = RunMux()
            End If

            ' ─── Step 4: Transition to Stopped or Faulted ───
            ' P0 fix: If mux failed, transition to Faulted and do NOT emit RecordingStopped.
            If muxSucceeded Then
                SyncLock _sync
                    _state = VideoBackendState.Stopped
                End SyncLock
                FireStateChanged(VideoBackendState.Stopped)
                FireRecordingStopped(_outputPath)
            Else
                SyncLock _sync
                    _state = VideoBackendState.Faulted
                End SyncLock
                FireStateChanged(VideoBackendState.Faulted)
                FireErrorOccurred("Mux failed — output file may be incomplete or missing.")
            End If
        End Sub

        Public Function GetDiagnostics() As IReadOnlyDictionary(Of String, Long) Implements IVideoBackend.GetDiagnostics
            If _stderrParser IsNot Nothing Then
                Return _stderrParser.GetSnapshot()
            End If
            Return New Dictionary(Of String, Long)()
        End Function

        ' ===== IDisposable =====

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim needStop As Boolean = False

            SyncLock _sync
                If _disposed Then Return
                _disposed = True

                If _state = VideoBackendState.Running OrElse _state = VideoBackendState.Starting Then
                    needStop = True
                End If
            End SyncLock

            ' Stop if running (OUTSIDE lock — calls WaitForExit/Kill/Mux)
            If needStop Then
                [Stop]()
            End If

            ' Dispose components (OUTSIDE lock)
            If _processHost IsNot Nothing Then
                RemoveHandler _processHost.Exited, AddressOf OnProcessExited
                RemoveHandler _processHost.StderrLine, AddressOf OnStderrLine
                _processHost.Dispose()
                _processHost = Nothing
            End If

            If _audioSidecar IsNot Nothing Then
                _audioSidecar.Dispose()
                _audioSidecar = Nothing
            End If

            If _muxCoordinator IsNot Nothing Then
                _muxCoordinator.Dispose()
                _muxCoordinator = Nothing
            End If

            _stderrParser = Nothing

            ' Finalize state
            SyncLock _sync
                _state = VideoBackendState.Disposed
            End SyncLock
            FireStateChanged(VideoBackendState.Disposed)
        End Sub

        ' ===== Configuration (fluent API, called before Start) =====

        Public Function WithFFmpegPath(path As String) As FFmpegPipelineBackend
            _ffmpegPath = path
            Return Me
        End Function

        Public Function WithArguments(args As String) As FFmpegPipelineBackend
            _arguments = args
            Return Me
        End Function

        Public Function WithOutputPath(path As String) As FFmpegPipelineBackend
            _outputPath = path
            Return Me
        End Function

        Public Function WithTwoProcess(enabled As Boolean,
                                       tempVideoPath As String,
                                       tempSystemWav As String,
                                       tempMicWav As String) As FFmpegPipelineBackend
            _useTwoProcess = enabled
            _tempVideoPath = tempVideoPath
            _tempSystemWav = tempSystemWav
            _tempMicWav = tempMicWav
            Return Me
        End Function

        Public Function WithAudio(systemEnabled As Boolean,
                                  micEnabled As Boolean,
                                  systemVolume As Single,
                                  micVolume As Single,
                                  separateTracks As Boolean) As FFmpegPipelineBackend
            _systemAudioEnabled = systemEnabled
            _micEnabled = micEnabled
            _systemVolume = systemVolume
            _micVolume = micVolume
            _separateTracks = separateTracks
            Return Me
        End Function

        ' ===== Private: Mux =====

        ''' <summary>
        ''' Run the mux step. Returns True if mux succeeded (or no mux needed);
        ''' False if mux failed.
        ''' </summary>
        Private Function RunMux() As Boolean
            If Threading.Interlocked.Exchange(_muxCompleted, 1) <> 0 Then Return True

            _muxCoordinator = New MuxCoordinator()
            _muxCoordinator.FFmpegPath = _ffmpegPath
            _muxCoordinator.TempVideoPath = _tempVideoPath
            _muxCoordinator.TempSystemWavPath = _tempSystemWav
            _muxCoordinator.TempMicWavPath = _tempMicWav
            _muxCoordinator.OutputPath = _outputPath
            _muxCoordinator.HasSystemAudio = _systemAudioEnabled
            _muxCoordinator.HasMicAudio = _micEnabled
            _muxCoordinator.SystemVolume = _systemVolume
            _muxCoordinator.MicVolume = _micVolume
            _muxCoordinator.SeparateTracks = _separateTracks

            ' Compute per-track offsets
            If _videoStartDetected Then
                If _systemStartTicks > 0 Then
                    _muxCoordinator.SystemOffsetSec =
                        (_videoStartTicks - _systemStartTicks) / Stopwatch.Frequency
                    _muxCoordinator.SystemOffsetSec = Math.Max(-2.0, Math.Min(5.0, _muxCoordinator.SystemOffsetSec))
                End If
                If _micStartTicks > 0 Then
                    _muxCoordinator.MicOffsetSec =
                        (_videoStartTicks - _micStartTicks) / Stopwatch.Frequency
                    _muxCoordinator.MicOffsetSec = Math.Max(-2.0, Math.Min(5.0, _muxCoordinator.MicOffsetSec))
                End If
            End If

            ' Transition to Muxing (P1 fix: explicit Muxing state for observability)
            SyncLock _sync
                _state = VideoBackendState.Muxing
            End SyncLock
            FireStateChanged(VideoBackendState.Muxing)

            ' Check if audio data exists
            Dim hasAudio As Boolean = (_audioSidecar IsNot Nothing AndAlso _audioSidecar.HasAudioData)

            If Not hasAudio Then
                ' No audio data — just rename temp video to final output
                Return _muxCoordinator.RenameTempVideoToOutput()
            Else
                ' Step 1: ffprobe (OUTSIDE lock)
                _muxCoordinator.ProbeVideoDuration()

                ' Step 2: Run mux FFmpeg (OUTSIDE lock)
                Dim muxOk As Boolean = _muxCoordinator.Run()

                If muxOk Then
                    ' Clean up temp files
                    _muxCoordinator.CleanupTempFiles()
                    Return True
                Else
                    ' Mux failed — keep temp files for inspection, return False
                    Return False
                End If
            End If
        End Function

        ' ===== Private: Event handlers =====

        Private Sub OnProcessExited(gen As Integer, exitCode As Integer)
            ' P0 fix: Generation guard — ignore stale callbacks from previous sessions
            If gen <> _currentGeneration Then Return

            ' Guard: only first caller of Stop/Dispose/OnExited proceeds
            If Threading.Interlocked.Exchange(_stopCompleted, 1) <> 0 Then Return

            Dim prevState As VideoBackendState
            SyncLock _sync
                prevState = _state
            End SyncLock

            ' Only handle unexpected exits (state was Running)
            If prevState = VideoBackendState.Running Then
                ' FFmpeg exited unexpectedly (crash or error)
                If _audioSidecar IsNot Nothing Then
                    _audioSidecar.Stop()
                End If

                Dim muxSucceeded As Boolean = True
                If exitCode = 0 AndAlso _useTwoProcess Then
                    muxSucceeded = RunMux()
                End If

                ' P0 fix: If mux failed or exit was non-zero, transition to Faulted
                If exitCode = 0 AndAlso muxSucceeded Then
                    SyncLock _sync
                        _state = VideoBackendState.Stopped
                    End SyncLock
                    FireStateChanged(VideoBackendState.Stopped)
                    FireRecordingStopped(_outputPath)
                Else
                    SyncLock _sync
                        _state = VideoBackendState.Faulted
                    End SyncLock
                    FireStateChanged(VideoBackendState.Faulted)
                    If exitCode <> 0 Then
                        FireErrorOccurred("FFmpeg exited unexpectedly with code " & exitCode.ToString())
                    Else
                        FireErrorOccurred("Mux failed — output file may be incomplete or missing.")
                    End If
                End If
            End If
        End Sub

        Private Sub OnStderrLine(gen As Integer, line As String)
            ' P0 fix: Generation guard — ignore stale callbacks
            If gen <> _currentGeneration Then Return

            If _stderrParser IsNot Nothing Then
                _stderrParser.ProcessLine(line)
            End If

            ' Detect video start (first frame= line with time=)
            If Not _videoStartDetected AndAlso _useTwoProcess Then
                If line.Contains("frame=") AndAlso line.Contains("time=") Then
                    Try
                        Dim timeIdx As Integer = line.IndexOf("time=") + 5
                        Dim timeStr As String = line.Substring(timeIdx).TrimStart()
                        Dim timeEnd As Integer = timeStr.IndexOf(" "c)
                        If timeEnd > 0 Then timeStr = timeStr.Substring(0, timeEnd)

                        Dim videoTime As TimeSpan
                        If TimeSpan.TryParse(timeStr, Globalization.CultureInfo.InvariantCulture, videoTime) Then
                            Dim nowTicks As Long = Stopwatch.GetTimestamp()
                            Dim videoTimeTicks As Long = CLng(videoTime.TotalSeconds * Stopwatch.Frequency)
                            _videoStartTicks = nowTicks - videoTimeTicks
                            _videoStartDetected = True
                        End If
                    Catch
                    End Try
                End If
            End If

            ' Fire error event if parser detected an error
            If _stderrParser IsNot Nothing AndAlso _stderrParser.HasError Then
                FireErrorOccurred(_stderrParser.LastError)
            End If
        End Sub

        ' ===== Private: Event dispatch (ALWAYS outside lock) =====

        Private Sub FireStateChanged(newState As VideoBackendState)
            RaiseEvent StateChanged(newState)
        End Sub

        Private Sub FireRecordingStarted(path As String)
            RaiseEvent RecordingStarted(path)
        End Sub

        Private Sub FireRecordingStopped(path As String)
            RaiseEvent RecordingStopped(path)
        End Sub

        Private Sub FireErrorOccurred(msg As String)
            RaiseEvent ErrorOccurred(msg)
        End Sub
    End Class
End Namespace
