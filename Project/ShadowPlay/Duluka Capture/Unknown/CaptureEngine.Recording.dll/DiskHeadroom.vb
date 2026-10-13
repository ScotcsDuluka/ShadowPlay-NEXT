Option Strict On
Option Explicit On

Imports System.IO
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Recording

    ' DiskHeadroom.vb — P1-A pre-flight disk gate (W1).
    '
    ' Derives the space a session actually needs FROM THE SESSION'S OWN
    ' NUMBERS (no invented constant):
    '   video   = SessionConfig.BitrateBps / 8 × duration   (cbr; bufsize ≤ 2×
    '             bitrate is a smoothing buffer, not extra disk)
    '   audio   = 320 kbps × stream count × duration        (LiveMux hardcodes
    '             -b:a 320k per AAC stream: mixed = 1 stream,
    '             SeparateTrack = 2)
    '   peak    = (video + audio) × 2 — the faststart remux holds the .frag and
    '             the final file on disk at the same time (LiveMuxSession.
    '             RunRemux reads the frag while writing the final).
    ' Free space is read from the OUTPUT PATH's drive root.
    '
    ' SessionConfig.DiskHeadroomOverrideBytes > 0 replaces the derived peak
    ' (deterministic test seam for low/near-zero headroom runs).

    Public NotInheritable Class DiskHeadroom

        Public ReadOnly Property Ok As Boolean
        Public ReadOnly Property RequiredBytes As Long
        Public ReadOnly Property FreeBytes As Long
        Public ReadOnly Property DriveRoot As String
        Public ReadOnly Property Derivation As String
        Public ReadOnly Property FailureReason As String

        Private Sub New(ok As Boolean, required As Long, free As Long, root As String, derivation As String, reason As String)
            _Ok = ok : _RequiredBytes = required : _FreeBytes = free
            _DriveRoot = root : _Derivation = derivation : _FailureReason = reason
        End Sub

        Public Shared Function Evaluate(config As SessionConfig, videoBitrateBps As Long, logger As EngineLogger) As DiskHeadroom
            Dim duration = Math.Max(0, config.DurationSeconds)
            Dim root As String = ""
            Try
                root = Path.GetPathRoot(Path.GetFullPath(config.OutputPath))
                If String.IsNullOrEmpty(root) Then root = "C:\"
            Catch
                root = "C:\"
            End Try

            Dim required As Long
            Dim derivation As String

            If config.DiskHeadroomOverrideBytes > 0 Then
                required = config.DiskHeadroomOverrideBytes
                derivation = $"override={required / 1048576.0:0.0}MB (test seam)"
            Else
                Dim videoBytes = CLng(Math.Max(1, videoBitrateBps) / 8.0) * duration
                Dim audioBytes As Long
                Dim derivationAudio As String
                If config.AudioSidecarMode Then
                    ' P3-D sidecar transport: the WAV is RAW PCM on disk
                    ' (192 KB/s per stereo stream — BIGGER than AAC), and the
                    ' remux peak holds live-frag + WAV + final simultaneously.
                    Dim wavStreams = If(config.AudioEnabled, 1, 0) + If(config.MicEnabled, 1, 0)
                    audioBytes = CLng(192000.0 * wavStreams) * duration
                    required = CLng(videoBytes * 2.0) + audioBytes
                    derivationAudio = $"WAV PCM {wavStreams} ch-stream(s) = {audioBytes / 1048576.0:0.0}MB"
                    derivation = $"(video {Math.Max(1, videoBitrateBps) / 1000000.0:0.#}Mbps + {derivationAudio}) peak over {duration}s = {required / 1048576.0:0.0}MB"
                Else
                    Dim streams = 0
                    If config.AudioEnabled Then streams += 1
                    If config.MicEnabled AndAlso config.MicSeparateTracks Then streams += 1
                    ' amix mixed mode folds mic into ONE 320k stream; SeparateTrack
                    ' emits TWO 320k streams (system + mic).
                    If config.AudioEnabled AndAlso config.MicEnabled AndAlso config.MicSeparateTracks Then streams = 2
                    If streams = 0 AndAlso config.MicEnabled Then streams = 1
                    audioBytes = CLng(320000 / 8.0) * duration * Math.Max(0, streams)
                    required = CLng((videoBytes + audioBytes) * 2.0)
                    derivationAudio = $"audio 0.32Mbps × {streams}"
                    derivation = $"(video {Math.Max(1, videoBitrateBps) / 1000000.0:0.#}Mbps + {derivationAudio}) × {duration}s × 2 (remux peak) = {required / 1048576.0:0.0}MB"
                End If
            End If

            Dim free As Long = -1
            Try
                Dim drive As New DriveInfo(root)
                If Not drive.IsReady Then
                    Return New DiskHeadroom(False, required, -1, root, derivation,
                                            $"output drive {root} is not ready")
                End If
                free = drive.AvailableFreeSpace
            Catch ex As Exception
                Return New DiskHeadroom(False, required, -1, root, derivation,
                                        $"cannot read free space on {root}: {ex.Message}")
            End Try

            If free < required Then
                Dim reason = $"insufficient disk headroom on {root}: required ≈ {required / 1048576.0:0.0}MB " &
                             $"({derivation}), free {free / 1048576.0:0.0}MB — free space, reduce duration/bitrate, " &
                             "or the recording will die mid-write (ENOSPC)"
                Return New DiskHeadroom(False, required, free, root, derivation, reason)
            End If

            logger?.Info($"[RecordingEngine] disk headroom ok: required ≈ {required / 1048576.0:0.0}MB ({derivation}), free {free / 1048576.0:0.0}MB on {root}")
            Return New DiskHeadroom(True, required, free, root, derivation, "")
        End Function

    End Class

End Namespace
