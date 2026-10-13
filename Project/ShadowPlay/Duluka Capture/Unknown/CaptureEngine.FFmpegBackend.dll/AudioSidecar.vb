Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.IO
Imports System.Threading

Namespace CaptureEngine.FFmpegBackend
    ''' <summary>
    ''' Audio sidecar: records WASAPI audio to temp .wav files independently
    ''' of the FFmpeg video process.
    '''
    ''' P1-C Commit 4: OPTIONAL STUB — no WASAPI implementation.
    '''
    ''' This is a lifecycle-safe placeholder. Start() and Stop() are no-ops
    ''' that set internal state correctly. HasAudioData always returns False.
    ''' When WASAPI is added (future task), replace the stub bodies with
    ''' real NAudio.WasapiCapture initialization.
    '''
    ''' Architecture (per Engine-Audio two-process design):
    '''   - Start() is called AFTER FFmpegProcessHost.Start() (video starts first)
    '''   - Stop() is called AFTER FFmpegProcessHost exits (audio records through
    '''     FFmpeg shutdown time; extra audio is trimmed by mux -t <videoDuration>)
    '''   - Two temp .wav files: temp.system.wav + temp.mic.wav
    '''   - Per-track start timestamps captured for mux offset calculation
    '''
    ''' Thread safety:
    '''   - Start/Stop are called from the FFmpegPipelineBackend thread (serialized)
    '''   - Internal state protected by SyncLock
    ''' </summary>
    ''' ★ C/5 DORMANT (audit 2026-09-06): zero production callers — lifecycle-safe
    ''' STUB (Start/Stop no-ops, HasAudioData always False) used only by the dormant
    ''' FFmpegPipelineBackend + FFmpegTests. Friend = compile-time isolation.
    ''' REMOVE-LATER candidate (with the FFPB family) — owner decision.
    Friend NotInheritable Class AudioSidecar
        Implements IDisposable

        Private ReadOnly _sync As New Object()
        Private _disposed As Boolean = False
        Private _started As Boolean = False

        ' Temp file paths (set by caller before Start)
        Public Property TempSystemWavPath As String = ""
        Public Property TempMicWavPath As String = ""

        ' Config flags
        Public Property SystemAudioEnabled As Boolean = False
        Public Property MicEnabled As Boolean = False

        ' Per-track start timestamps (QPC ticks, for mux offset)
        Public Property SystemStartTicks As Long = 0
        Public Property MicStartTicks As Long = 0

        ''' <summary>
        ''' Start audio recording (WASAPI loopback + optional mic capture).
        ''' STUB: no-ops. Sets _started = True and captures QPC timestamps
        ''' for future mux offset calculation. Does NOT create .wav files.
        ''' </summary>
        Public Sub Start()
            SyncLock _sync
                If _disposed Then
                    Throw New ObjectDisposedException(NameOf(AudioSidecar))
                End If
                If _started Then Return

                ' Capture per-track start timestamps (even in stub mode —
                ' the mux coordinator uses these for offset calculation).
                If SystemAudioEnabled Then
                    SystemStartTicks = Stopwatch.GetTimestamp()
                End If
                If MicEnabled Then
                    MicStartTicks = Stopwatch.GetTimestamp()
                End If

                _started = True

                ' TODO (future task): initialize NAudio WasapiLoopbackCapture
                ' + WaveFileWriter for each enabled track. Write to temp .wav files.
                ' For now, this is a no-op stub — no .wav files are created.
            End SyncLock
        End Sub

        ''' <summary>
        ''' Stop audio recording + finalize .wav files (write correct WAV headers).
        ''' STUB: no-ops. Sets _started = False.
        ''' </summary>
        Public Sub [Stop]()
            SyncLock _sync
                If Not _started Then Return
                _started = False

                ' TODO (future task): stop WasapiCapture, dispose WaveFileWriter(s).
                ' WaveFileWriter.Dispose writes the correct WAV header (data size, etc.)
                ' For now, this is a no-op stub.
            End SyncLock
        End Sub

        ''' <summary>
        ''' True if at least one audio track has data (non-empty .wav).
        ''' STUB: always returns False (no .wav files created).
        ''' </summary>
        Public ReadOnly Property HasAudioData As Boolean
            Get
                ' TODO (future task): check .wav file sizes > 44 bytes (WAV header)
                ' For now, stub returns False — no audio data.
                Return False
            End Get
        End Property

        ''' <summary>True if Start() was called and Stop() has not been called.</summary>
        Public ReadOnly Property IsRunning As Boolean
            Get
                SyncLock _sync
                    Return _started
                End SyncLock
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _sync
                If _disposed Then Return
                _disposed = True

                ' Ensure stopped
                If _started Then
                    _started = False
                    ' TODO (future): dispose WASAPI resources
                End If
            End SyncLock
        End Sub
    End Class
End Namespace
