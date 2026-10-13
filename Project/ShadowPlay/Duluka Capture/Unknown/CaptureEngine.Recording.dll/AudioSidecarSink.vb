Option Strict On
Option Explicit On

Imports CaptureEngine.Audio
Imports System.Threading
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording

    ' AudioSidecarSink.vb — P3-D sidecar audio transport (W1).
    '
    ' IAudioSink adapter that writes engine PCM into a WavSidecarWriter
    ' (direct WAV on disk) instead of the FFmpeg real-time audio pipe.
    '
    ' WHY (P3-B/C evidence): the FFmpeg audio pipe consumption collapses to
    ' ~60% real-time on real-audio long runs (video pipe consumed 100%,
    ' audio pipe starved 6/6) — the WAV sidecar delivered 100% in every run
    ' including 15 min. The writer lives on a separate thread + file, so
    ' FFmpeg video muxing can no longer stall audio capture.
    '
    ' FORMAT: the WavSidecarWriter is created lazily on the FIRST packet —
    ' the packet carries the authoritative capture format (sample rate /
    ' channels after WASAPI Start), so no format guessing at setup time.
    ' PCM16 is the engine's dispatch format (AudioPcm16.Convert upstream).
    '
    ' ACCOUNTING: enqueued = written + dropped (WavFinalizeReport), and
    ' writes arriving after FinalizeNow are counted separately — no silent
    ' loss anywhere.

    Public NotInheritable Class AudioSidecarSink
        Implements IAudioSink

        Private ReadOnly _wavPath As String
        Private ReadOnly _fallbackChannels As Integer
        Private ReadOnly _sync As New Object()
        Private _writer As WavSidecarWriter
        Private _finalized As Boolean
        Private _postFinalizeDropped As Long
        Private _createFailures As Long

        Public Sub New(wavPath As String, Optional fallbackChannels As Integer = 2)
            _wavPath = wavPath
            _fallbackChannels = Math.Max(1, fallbackChannels)
        End Sub

        Public ReadOnly Property WavPath As String
            Get
                Return _wavPath
            End Get
        End Property

        Public ReadOnly Property PostFinalizeDropped As Long
            Get
                Return Interlocked.Read(_postFinalizeDropped)
            End Get
        End Property

        Public ReadOnly Property CreateFailures As Long
            Get
                Return Interlocked.Read(_createFailures)
            End Get
        End Property

        Public Sub Write(packet As AudioPacket) Implements IAudioSink.Write
            If packet.Data Is Nothing OrElse packet.Data.Length = 0 Then Return
            Dim writer = _writer
            If writer Is Nothing Then
                SyncLock _sync
                    writer = _writer
                    If writer Is Nothing Then
                        Try
                            Dim channels = Math.Max(1, packet.Channels)
                            _writer = New WavSidecarWriter(_wavPath, channels, packet.SampleRate, 16)
                            _writer.Start()
                        Catch ex As Exception
                            Interlocked.Increment(_createFailures)
                            Return
                        End Try
                    End If
                    writer = _writer
                End SyncLock
            End If
            If _finalized Then
                Interlocked.Add(_postFinalizeDropped, packet.Data.Length)
                Return
            End If
            writer.EnqueueChunk(packet.Data, packet.Data.Length)
        End Sub

        ''' <summary>Finalize the WAV (bounded). Idempotent; further writes are
        ''' counted as post-finalize drops. Returns the writer report or
        ''' Nothing when the writer never started (no audio captured).</summary>
        Public Function FinalizeNow(timeoutMs As Integer) As WavFinalizeReport
            SyncLock _sync
                If _finalized Then Return Nothing
                _finalized = True
            End SyncLock
            Dim w = _writer
            If w Is Nothing Then Return Nothing
            Return w.Complete(timeoutMs)
        End Function

    End Class

End Namespace
