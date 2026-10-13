Option Strict On
Option Explicit On
Option Infer On

' LiveMuxSession.vb — OBS-style live mux: NVENC H.264 + WASAPI PCM piped
' DIRECTLY into one FFmpeg process while recording.
'
' OWNER directive (2026-08-23): record video+audio together, both follow the
' wall clock, both end together — the OBS model. This replaces the
' temp-H.264 + WAV-sidecar + wrap + later-mux pipeline whose intermediate
' files could not carry timestamps (the root of every sync bug we chased).
'
' Architecture:
'   ffmpeg.exe (spawned at record start, ONE process)
'     input 0: \\.\pipe\sp_v_xxx   raw H.264 (CFR paced by CaptureSession)
'     input 1: \\.\pipe\sp_a_xxx   raw PCM s16le (gap-filled wall clock)
'    [input 2: \\.\pipe\sp_m_xxx   mic PCM]
'     output : fragmented MP4 (crash-safe) → +faststart remux at stop
'
' Timeline alignment (the SyncMath model, applied at FEED time):
'   video t0 = first encoded frame (session-start frame fix guarantees t≈0).
'   audio started at t_a; if audio ran BEFORE video t0 → DISCARD that head
'   (old -ss skip); if audio's first data lands AFTER t0 → PAD silence.
'   From then on both pipes advance at wall-clock rate → container-aligned
'   BY CONSTRUCTION. Residual = WASAPI delivery latency (constant, small).
'
' Pipes use the exact pattern proven by the legacy AudioPipe on this very
' machine (named pipe, 1MB buffer, WaitForConnection, FFmpeg as client).

Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.IO.Pipes
Imports System.Threading

Namespace CaptureEngine.FFmpegBackend

    ''' <summary>Statistics of a completed live-mux run.</summary>
    Public NotInheritable Class LiveMuxResult
        Public Property Succeeded As Boolean
        ' P1-B: ffmpeg died (exit != 0) but a USABLE fragmented recording was
        ' promoted to the output path. The result stays an honest FAILURE
        ' (Succeeded = False) - this flag only marks that partial footage was
        ' preserved for the user.
        Public Property PartialSalvaged As Boolean
        Public Property FFmpegExitCode As Integer
        Public Property VideoBytesFed As Long
        Public Property SystemBytesFed As Long
        Public Property MicBytesFed As Long
        Public Property DroppedBytes As Long
        Public Property UsedFaststartRemux As Boolean
        Public Property ErrorMessage As String = ""
        ' W3 audio-forensics: the muxer's own stderr tail is evidence on the
        ' SUCCESS path too (a silently dropped mapped stream, codec warnings,
        ' interleave notices never reach ErrorMessage). Always captured.
        Public Property StderrTail As String = ""

        Public Overrides Function ToString() As String
            Return $"LiveMux: ok={Succeeded} exit={FFmpegExitCode} " &
                   $"v={VideoBytesFed:N0}B a={SystemBytesFed:N0}B mic={MicBytesFed:N0}B " &
                   $"dropped={DroppedBytes:N0}B faststart={UsedFaststartRemux}" &
                   If(PartialSalvaged, " PARTIAL-SALVAGED", "") &
                   If(String.IsNullOrEmpty(ErrorMessage), "", " err=" & ErrorMessage)
        End Function
    End Class

    Public NotInheritable Class LiveMuxSession
        Implements IDisposable

        Private Const PipeBufferBytes As Integer = 1024 * 1024
        Private Const ConnectTimeoutMs As Integer = 15000
        ' ★ AUDIT FIX #5 (GLM/6): chunk-count caps were meaningless across
        ' streams (video chunks 30-200KB, audio ~5KB). Byte-bound now:
        '   video 96MB (~0.5-4s depending on bitrate) — BLOCKS the producer
        '   with timeout (audit #4: dropping H.264 packets mid-GOP corrupts
        '   the stream; OBS lets the encoder lag instead of dropping)
        '   audio 8MB (~20s) — drops oldest, but BLOCK-ALIGNED (audit #6:
        '   dropping PCM at arbitrary byte offsets turns the remainder into
        '   noise; round to whole samples)
        Private Const VideoQueueByteCap As Long = 96L * 1024 * 1024
        Private Const AudioQueueByteCap As Long = 8L * 1024 * 1024
        Private Const ProducerBlockTimeoutMs As Integer = 10000

        Private ReadOnly _ffmpegPath As String
        Private ReadOnly _finalPath As String
        Private ReadOnly _fragPath As String
        Private ReadOnly _videoFps As Integer
        Private ReadOnly _separateTracks As Boolean
        Private ReadOnly _sysVolume As Single
        Private ReadOnly _micVolume As Single
        Private ReadOnly _log As Action(Of String)

        ' stream configs (Nothing = stream absent)
        Private ReadOnly _sysRate As Integer
        Private ReadOnly _sysChannels As Integer
        Private ReadOnly _micRate As Integer
        Private ReadOnly _micChannels As Integer

        Private _proc As Process
        Private _video As PipeFeed
        Private _audio As PipeFeed
        Private _mic As PipeFeed
        Private _stderrTask As Task(Of String)
        Private _started As Boolean
        Private _disposed As Boolean
        ' ★ Ownership: host-provided hook (SessionConfig.OnProcessStarted →
        ' JobObjectGuard.Assign). Every ffmpeg this class spawns is handed to
        ' the host's job so a crashed/killed host cannot orphan it —
        ' KILL_ON_JOB_CLOSE closes the job handle → children die.
        Private ReadOnly _onProcessStarted As Action(Of Process)

        Public Sub New(ffmpegPath As String,
                       finalOutputPath As String,
                       videoFps As Integer,
                       systemSampleRate As Integer,
                       systemChannels As Integer,
                       micSampleRate As Integer,
                       micChannels As Integer,
                       separateTracks As Boolean,
                       systemVolume As Single,
                       micVolume As Single,
                       Optional log As Action(Of String) = Nothing,
                       Optional onProcessStarted As Action(Of Process) = Nothing)
            _ffmpegPath = ffmpegPath
            _onProcessStarted = onProcessStarted
            _finalPath = finalOutputPath
            _fragPath = finalOutputPath & ".frag.mp4"
            _videoFps = Math.Max(1, videoFps)
            ' ★ 17:19 FIX (video-only session died at first frame): when the
            ' session has NO system audio the caller used to pass the default
            ' 48000/2 regardless — LiveMux then created the sp_a pipe and put
            ' it in the ffmpeg args, but nothing ever connected; ffmpeg's
            ' input#1 open failed ('Invalid argument') → exit -22, ALL video
            ' bytes dropped, no output file. rate<=0 is the explicit
            ' "audio disabled" signal: no audio pipe, no audio input.
            _sysRate = If(systemSampleRate > 0, systemSampleRate, 0)
            _sysChannels = If(systemSampleRate > 0, systemChannels, 0)
            _micRate = If(micSampleRate > 0, micSampleRate, 0)
            _micChannels = If(micSampleRate > 0, micChannels, 0)
            _separateTracks = separateTracks
            _sysVolume = systemVolume
            _micVolume = micVolume
            _log = log
            _onProcessStarted = onProcessStarted
        End Sub

        Private Sub Log(msg As String)
            _log?.Invoke(msg)
        End Sub

        Private Sub LogPipe(msg As String)
            _log?.Invoke(msg)
        End Sub

        ' ─── lifecycle ──────────────────────────────────────────────

        ''' <summary>
        ''' Create pipes, start listeners, spawn FFmpeg. Returns False (with
        ''' reason logged) if the process cannot start. FFmpeg blocks reading
        ''' until data arrives — safe to start before capture begins.
        ''' </summary>
        Public Function Start() As Boolean
            Dim id As String = Guid.NewGuid().ToString("N").Substring(0, 10)
            Try
            _video = New PipeFeed("sp_v_" & id, waitForTimeline:=False, blockProducer:=True, blockAlign:=1, log:=AddressOf LogPipe)
            ' ★ 17:19: no audio pipe at all when system audio is disabled
            ' (rate 0) — a created-but-unfed pipe kills ffmpeg's input open.
            ' Audio is raw PCM without per-packet timestamps. Dropping an old
            ' chunk would remove time from the stream and concatenate later
            ' speech across the missing silence. Use the same bounded
            ' backpressure policy as video; if the producer cannot keep up,
            ' the session fails honestly instead of producing shifted audio.
            _audio = If(_sysRate > 0 AndAlso _sysChannels > 0,
                        New PipeFeed("sp_a_" & id, waitForTimeline:=True, blockProducer:=True, blockAlign:=_sysChannels * 2, log:=AddressOf LogPipe),
                        Nothing)
            _mic = If(_micRate > 0, New PipeFeed("sp_m_" & id, waitForTimeline:=True, blockProducer:=True, blockAlign:=_micChannels * 2, log:=AddressOf LogPipe), Nothing)

            _video.StartListening()
            _audio?.StartListening()
            _mic?.StartListening()

                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegPath,
                    .Arguments = BuildArgs(),
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                _proc = Process.Start(psi)
                ' ★ Ownership: assign the recording ffmpeg to the host's job
                ' object ASAP after spawn (MuxCoordinator parity). Fail-open:
                ' a throwing/failing assignment leaves the process self-owned
                ' exactly as before — Stop/Dispose remain the explicit owners.
                If _proc IsNot Nothing Then
                    Try : _onProcessStarted?.Invoke(_proc) : Catch ex As Exception
                        Log("[live-mux] process-ownership hook failed (ffmpeg stays self-owned): " & ex.Message)
                    End Try
                End If
                _stderrTask = _proc.StandardError.ReadToEndAsync()

                _video.StartWriter()
                _audio?.StartWriter()
                _mic?.StartWriter()

                _started = True
                Log($"[live-mux] started ffmpeg pid {_proc.Id} @ {_videoFps}fps, " &
                    $"sys {_sysRate}Hz/{_sysChannels}ch, mic {If(_mic IsNot Nothing, _micRate & "Hz/" & _micChannels & "ch", "off")}")
                Return True
            Catch ex As Exception
                Log("[live-mux] start FAILED: " & ex.Message)
                Return False
            End Try
        End Function

        Private Function BuildArgs() As String
            Dim sb As New Text.StringBuilder()
            sb.Append("-y -hide_banner -loglevel warning ")

            ' input 0: raw H.264 CFR
            sb.Append($"-f h264 -framerate {_videoFps} -i ""\\.\pipe\{_video.Name}"" ")

            ' input 1: system audio raw PCM — ONLY when system audio exists
            ' (rate 0 = audio disabled; a pipe no one feeds kills ffmpeg's
            ' input open, see ctor note)
            Dim hasSys As Boolean = _sysRate > 0 AndAlso _sysChannels > 0
            If hasSys Then
                sb.Append($"-f s16le -ar {_sysRate} -ac {_sysChannels} -i ""\\.\pipe\{_audio.Name}"" ")
            End If

            ' input 2: mic raw PCM
            If _mic IsNot Nothing Then
                sb.Append($"-f s16le -ar {_micRate} -ac {_micChannels} -i ""\\.\pipe\{_mic.Name}"" ")
            End If

            If _mic IsNot Nothing AndAlso Not _separateTracks Then
                ' amix single track (mirrors MuxCoordinator's mixed mode)
                Dim sysFilter As String = BuildOneFilter(_sysVolume, True)
                Dim micFilter As String = BuildOneFilter(_micVolume, True)
                sb.Append($"-filter_complex ""[1:a]{sysFilter}[a0];[2:a]{micFilter}[a1];" &
                          "[a0][a1]amix=inputs=2:duration=longest:normalize=0[aout]"" ")
                sb.Append("-map 0:v -map ""[aout]"" ")
            ElseIf _mic IsNot Nothing Then
                sb.Append("-map 0:v -map 1:a -map 2:a ")
            ElseIf hasSys Then
                sb.Append("-map 0:v -map 1:a ")
            Else
                ' video-only
                sb.Append("-map 0:v ")
            End If

            sb.Append("-c:v copy ")
            If _mic IsNot Nothing AndAlso _separateTracks Then
                sb.Append("-c:a:0 aac -b:a:0 320k -ar:a:0 48000 ")
                sb.Append("-c:a:1 aac -b:a:1 320k -ar:a:1 48000 ")
            ElseIf hasSys OrElse _mic IsNot Nothing Then
                sb.Append("-c:a aac -b:a 320k -ar 48000 ")
            End If

            ' fragmented MP4: recording survives a crash mid-session
            sb.Append("-movflags +frag_keyframe+empty_moov+default_base_moof ")
            ' ★ P1-B: close a fragment at least every 1 s — frag_keyframe alone
            ' leaves ONE giant open fragment when the encoder emits no keyframes
            ' (measured: NVENC keyframe only at seq≈2220 → an 8 s kill left a
            ' 28-byte ftyp-only file, the entire recording lost in the muxer's
            ' memory). Duration-bounded fragments hit disk continuously, so the
            ' partial-salvage path has real content at ANY kill point.
            sb.Append("-frag_duration 1000000 ")
            sb.Append($"""{_fragPath}""")
            Return sb.ToString()
        End Function

        Private Shared Function BuildOneFilter(volume As Single, includeApad As Boolean) As String
            Dim parts As New List(Of String)()
            If Math.Abs(volume - 1.0F) > 0.001F Then
                Dim v As Single = Math.Max(0.0F, Math.Min(2.0F, volume))
                parts.Add($"volume={v.ToString("0.000", CultureInfo.InvariantCulture)}")
            End If
            ' ★ NOISE FIX: aresample=async=1 REMOVED. It was inherited from the
            ' MuxCoordinator era (post-hoc mux of a WAV with its own timeline).
            ' With AudioTap the pipe stream is ALREADY wall-clock true; async
            ' mode 'stretches' audio by adding/dropping samples based on PTS
            ' drift it perceives — on a raw-PCM pipe (no timestamps, pacing =
            ' arrival rate) it manufactured constant time-stretch = the
            ' full-session noise the owner kept hearing. first_pts=0 also
            ' fought the pipe's origin offset. Volume + apad only.
            If includeApad Then parts.Add("apad")
            Return String.Join(",", parts)
        End Function

        ' ─── timeline + feeding ─────────────────────────────────────

        ''' <summary>Video t0 established (first encoded frame). Offsets use the SyncMath model.</summary>
        Public Sub BeginTimelines(systemOffsetSec As Double, micOffsetSec As Double)
            ' offset > 0 → audio started BEFORE video → discard head
            ' offset < 0 → audio data begins AFTER video → pad silence
            Dim sysBytesPerSec As Long = CLng(_sysRate) * _sysChannels * 2L
            Dim micBytesPerSec As Long = CLng(_micRate) * _micChannels * 2L

            Dim sysDiscard As Long = CLng(Math.Max(0, systemOffsetSec) * sysBytesPerSec)
            Dim sysPad As Long = CLng(Math.Max(0, -systemOffsetSec) * sysBytesPerSec)
            _audio?.BeginTimeline(sysDiscard, sysPad)
            Log($"[live-mux] sys timeline: offset={systemOffsetSec:0.000}s → discard {sysDiscard:N0}B / pad {sysPad:N0}B")

            If _mic IsNot Nothing Then
                Dim micDiscard As Long = CLng(Math.Max(0, micOffsetSec) * micBytesPerSec)
                Dim micPad As Long = CLng(Math.Max(0, -micOffsetSec) * micBytesPerSec)
                _mic?.BeginTimeline(micDiscard, micPad)
                Log($"[live-mux] mic timeline: offset={micOffsetSec:0.000}s → discard {micDiscard:N0}B / pad {micPad:N0}B")
            End If
        End Sub

        Public Sub FeedVideo(payload As Byte(), length As Integer)
            If _started Then _video.Feed(payload, length)
        End Sub

        Public Sub FeedSystemAudio(pcm As Byte(), length As Integer)
            If _started Then _audio?.Feed(pcm, length)
        End Sub

        Public Sub FeedSystemAudioSegment(pcm As Byte(), offset As Integer, length As Integer)
            If Not _started OrElse _audio Is Nothing OrElse pcm Is Nothing OrElse offset < 0 OrElse length <= 0 OrElse offset + length > pcm.Length Then Return
            If offset = 0 Then
                _audio.Feed(pcm, length)
            Else
                Dim slice(length - 1) As Byte
                Buffer.BlockCopy(pcm, offset, slice, 0, length)
                _audio.Feed(slice, length)
            End If
        End Sub

        Public Sub FeedMicAudio(pcm As Byte(), length As Integer)
            If _started Then _mic?.Feed(pcm, length)
        End Sub

        Public Sub FeedMicAudioSegment(pcm As Byte(), offset As Integer, length As Integer)
            If Not _started OrElse _mic Is Nothing OrElse pcm Is Nothing OrElse offset < 0 OrElse length <= 0 OrElse offset + length > pcm.Length Then Return
            If offset = 0 Then
                _mic.Feed(pcm, length)
            Else
                Dim slice(length - 1) As Byte
                Buffer.BlockCopy(pcm, offset, slice, 0, length)
                _mic.Feed(slice, length)
            End If
        End Sub

        ' ─── stop ───────────────────────────────────────────────────

        ''' <summary>
        ''' Drain queues, close pipes (EOF), wait for FFmpeg to finalize the
        ''' fragment, then remux to +faststart. Never throws.
        ''' </summary>
        Public Function [Stop](timeoutMs As Integer) As LiveMuxResult
            Dim res As New LiveMuxResult()
            If Not _started Then
                res.ErrorMessage = "not started"
                Return res
            End If
            Try
                                ' One global stop budget. Never spend timeoutMs separately on
                ' every pipe — three 30s joins could otherwise turn one Stop
                ' click into a 90s+ hang before FFmpeg even receives EOF.
                Dim stopClock As Stopwatch = Stopwatch.StartNew()
                Dim pipeBudgetMs As Integer = Math.Min(3000, Math.Max(1000, timeoutMs \ 3))

                Log($"[live-mux] stop requested: timeout={timeoutMs}ms, pipeBudget={pipeBudgetMs}ms")
                _video.RequestStopAndDrain(pipeBudgetMs)
                _audio?.RequestStopAndDrain(pipeBudgetMs)
                _mic?.RequestStopAndDrain(pipeBudgetMs)

                Dim remainingMs As Integer = Math.Max(1000, timeoutMs - CInt(stopClock.ElapsedMilliseconds))
                Log($"[live-mux] pipes closed; waiting for ffmpeg EOF/finalize ({remainingMs}ms remaining)")
                If Not _proc.WaitForExit(remainingMs) Then
                    Try : _proc.Kill() : Catch : End Try
                    res.ErrorMessage = "ffmpeg finalize timeout"
                End If
                ' ★ Fix: after Kill() the process may still be terminating —
                ' reading ExitCode then throws InvalidOperationException, which
                ' used to escape into the outer Catch, skipping the remux/salvage
                ' entirely (the finished .frag.mp4 was never promoted to the
                ' final output → session reported failed with NO output file).
                Try
                    res.FFmpegExitCode = _proc.ExitCode
                Catch ex As Exception
                    Log($"[live-mux] exit code unavailable after kill: {ex.Message}")
                    If res.ErrorMessage.Length = 0 Then res.ErrorMessage = "ffmpeg exit code unavailable"
                End Try

                Dim stderrTail As String = ""
                Try
                    If _stderrTask.Wait(2000) Then
                        Dim lines As String() = _stderrTask.Result.Split(New Char() {ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                        stderrTail = String.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 12)))
                    End If
                Catch
                End Try
                res.StderrTail = stderrTail

                res.VideoBytesFed = _video.BytesWritten
                res.SystemBytesFed = If(_audio IsNot Nothing, _audio.BytesWritten, 0)
                res.MicBytesFed = If(_mic IsNot Nothing, _mic.BytesWritten, 0)
                res.DroppedBytes = _video.DroppedBytes + If(_audio IsNot Nothing, _audio.DroppedBytes, 0) + If(_mic IsNot Nothing, _mic.DroppedBytes, 0)

                If res.FFmpegExitCode = 0 AndAlso File.Exists(_fragPath) Then
                    ' +faststart remux (stream copy — cheap)
                    Dim remuxOk As Boolean = RunRemux()
                    res.UsedFaststartRemux = remuxOk
                    If remuxOk Then
                        Try : File.Delete(_fragPath) : Catch : End Try
                        res.Succeeded = File.Exists(_finalPath)
                    Else
                        ' salvage: fragmented file is still playable
                        Try
                            If File.Exists(_finalPath) Then File.Delete(_finalPath)
                            File.Move(_fragPath, _finalPath)
                        Catch
                        End Try
                        res.Succeeded = File.Exists(_finalPath)
                        res.ErrorMessage &= " faststart-remux failed (kept fragmented file)"
                    End If
                Else
                    res.ErrorMessage &= " ffmpeg exit=" & res.FFmpegExitCode & " " & stderrTail

                    ' P1-B frag salvage: ffmpeg died (killed/crashed) but the
                    ' fragmented recording on disk may still be USABLE - fMP4
                    ' (+frag_keyframe+empty_moov) is playable up to the last
                    ' complete fragment. Promote it to the output path so the
                    ' user keeps the partial footage. The result remains an
                    ' honest FAILURE: Succeeded stays False and PartialSalvaged
                    ' marks the output as partial (SessionResult.Pass must NOT
                    ' flip to True for a salvaged kill).
                    If File.Exists(_fragPath) AndAlso ValidateFragmentUsable(_fragPath) Then
                        Try
                            If File.Exists(_finalPath) Then File.Delete(_finalPath)
                            File.Move(_fragPath, _finalPath)
                            res.PartialSalvaged = File.Exists(_finalPath)
                            If res.PartialSalvaged Then
                                Log("[live-mux] partial salvage: fragmented recording promoted to output " +
                                    "(ffmpeg exit=" & res.FFmpegExitCode & ") - result stays FAILED")
                            End If
                        Catch ex As Exception
                            Log("[live-mux] partial salvage failed (frag kept on disk): " & ex.Message)
                        End Try
                    End If
                End If

                Log("[live-mux] " & res.ToString())
            Catch ex As Exception
                res.ErrorMessage &= " stop error: " & ex.Message
                Log("[live-mux] stop error: " & ex.Message)
            End Try
            Return res
        End Function

        ' P1-B: is a fragmented recording USABLE? ffprobe must see a video
        ' stream and a positive duration (fMP4 carries its moov from the
        ' first byte, so a mid-crash file probes fine). Bounded wait + kill,
        ' never throws. Without ffprobe: non-trivial size heuristic.
        Private Function ValidateFragmentUsable(fragPath As String) As Boolean
            Try
                If New FileInfo(fragPath).Length < 1024 Then Return False
                Dim ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpegPath), "ffprobe.exe")
                If Not File.Exists(ffprobe) Then Return True ' heuristic: size only

                Dim psi As New ProcessStartInfo With {
                    .FileName = ffprobe,
                    .Arguments = "-v error -select_streams v:0 -show_entries stream=codec_name " &
                                 "-show_entries format=duration -of json """ & fragPath & """",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    If Not p.WaitForExit(10000) Then
                        Try : p.Kill() : Catch : End Try
                        Return False
                    End If
                    Dim json As String = If(outTask.Wait(2000), outTask.Result, "")
                    Return json.Contains("codec_name") AndAlso json.Contains("duration") AndAlso
                           Not json.Contains("duration"":""N/A")
                End Using
            Catch
                Return False
            End Try
        End Function

        Private Function RunRemux() As Boolean
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegPath,
                    .Arguments = $"-y -hide_banner -loglevel error -i ""{_fragPath}"" -c copy -movflags +faststart ""{_finalPath}""",
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    ' ★ Ownership: the remux child gets the same job assignment
                    ' as the recording ffmpeg (it holds _fragPath/_finalPath).
                    If p IsNot Nothing Then
                        Try : _onProcessStarted?.Invoke(p) : Catch : End Try
                    End If
                    Dim errTask = p.StandardError.ReadToEndAsync()
                    If Not p.WaitForExit(30000) Then
                        Try : p.Kill() : Catch : End Try
                        Return False
                    End If
                    errTask.Wait(1000)
                    Return p.ExitCode = 0 AndAlso File.Exists(_finalPath)
                End Using
            Catch
                Return False
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try : _video?.Dispose() : Catch : End Try
            Try : _audio?.Dispose() : Catch : End Try
            Try : _mic?.Dispose() : Catch : End Try
            ' ★ Orphan guard: Dispose can run without Stop() (session failure
            ' path). The closed pipes usually drive ffmpeg to EOF on its own,
            ' but a process wedged in a blocking syscall would survive forever —
            ' kill it explicitly.
            Try
                If _proc IsNot Nothing AndAlso Not _proc.HasExited Then _proc.Kill()
            Catch
            End Try
            Try : _proc?.Dispose() : Catch : End Try
        End Sub

        ' ═════════════════════════════════════════════════════════════
        ' PipeFeed: named-pipe server + bounded queue + writer thread
        ' ═════════════════════════════════════════════════════════════
        Private NotInheritable Class PipeFeed
            Implements IDisposable

            Friend ReadOnly Name As String
            Private ReadOnly _pipe As NamedPipeServerStream
            Private ReadOnly _queue As New ConcurrentQueue(Of Byte())()
            Private ReadOnly _signal As New AutoResetEvent(False)
            Private ReadOnly _stopWriter As New ManualResetEvent(False)
            Private ReadOnly _timelineStarted As New ManualResetEvent(False)
            Private ReadOnly _connectedEvent As New ManualResetEvent(False)
            Private ReadOnly _sync As New Object()

            Private _writer As Thread
            Private _pipeBroken As Boolean
            Private _bytesWritten As Long
            Private _droppedBytes As Long
            Private _discardBytes As Long
            Private _padBytes As Long
            ' ★ Accounting fix: set after RequestStopAndDrain's residual fold.
            ' A Feed racing past that fold used to enqueue bytes nobody would
            ' ever write AND nobody counted — the honest DroppedBytes total
            ' could under-report. Feeds arriving after this flag are counted
            ' as dropped at entry.
            Private _closed As Integer = 0

            Friend Sub New(pipeName As String, waitForTimeline As Boolean, blockProducer As Boolean, blockAlign As Integer, Optional log As Action(Of String) = Nothing)
                Name = pipeName
                _waitForTimeline = waitForTimeline
                _blockProducer = blockProducer
                _blockAlign = Math.Max(1, blockAlign)
                _log = log
                _byteCap = If(blockProducer, VideoQueueByteCap, AudioQueueByteCap)
                _pipe = New NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                                                  PipeTransmissionMode.Byte,
                                                  PipeOptions.Asynchronous,
                                                  PipeBufferBytes, PipeBufferBytes)
            End Sub

            Private ReadOnly _waitForTimeline As Boolean
            Private ReadOnly _log As Action(Of String)
            Private ReadOnly _blockProducer As Boolean
            Private ReadOnly _blockAlign As Integer
            Private ReadOnly _byteCap As Long
            Private _bytesQueued As Long = 0

            Friend ReadOnly Property BytesWritten As Long
                Get
                    Return Interlocked.Read(_bytesWritten)
                End Get
            End Property

            Friend ReadOnly Property DroppedBytes As Long
                Get
                    Return Interlocked.Read(_droppedBytes)
                End Get
            End Property

            ''' <summary>Wait for the FFmpeg client to connect (background).</summary>
            Friend Sub StartListening()
                Task.Run(Sub()
                             Try
                                 _pipe.WaitForConnection()
                             Catch
                             End Try
                             _connectedEvent.Set()
                         End Sub)
            End Sub

            Friend Sub StartWriter()
                _writer = New Thread(AddressOf WriterLoop) With {
                    .IsBackground = True,
                    .Name = "LiveMux_" & Name
                }
                _writer.Start()
            End Sub

            ''' <summary>
            ''' t0 established. discardBytes = skip that much REAL data from the
            ''' queue head (audio ran before video). padBytes = write that much
            ''' silence FIRST (audio data begins after video t0).
            ''' </summary>
            Friend Sub BeginTimeline(discardBytes As Long, padBytes As Long)
                SyncLock _sync
                    _discardBytes = Math.Max(0, discardBytes)
                    _padBytes = Math.Max(0, padBytes)
                End SyncLock
                _timelineStarted.Set()
                _signal.Set()
            End Sub

            Friend Sub Feed(data As Byte(), count As Integer)
                If count <= 0 OrElse count > data.Length Then Return
                ' Post-close feeds can never be written — count them into the
                ' honest drop total instead of silently vanishing.
                If Volatile.Read(_closed) <> 0 Then
                    Interlocked.Add(_droppedBytes, count)
                    Return
                End If
                Dim copy(count - 1) As Byte
                Array.Copy(data, copy, count)

                If _blockProducer Then
                    ' ★ VIDEO: never drop — blocking bounded queue. A dropped
                    ' H.264 packet corrupts the stream until the next IDR.
                    ' Wait (bounded by ProducerBlockTimeoutMs) for the writer
                    ' to drain; on timeout the packet is dropped WHOLE and
                    ' counted into DroppedBytes (the session pass gate then
                    ' fails on it) — corruption-safe, honestly reported.
                    Dim swFeed As New Stopwatch()
                    swFeed.Start()
                    While Interlocked.Read(_bytesQueued) + count > _byteCap
                        _signal.Set()
                        If _stopWriter.WaitOne(0) OrElse swFeed.ElapsedMilliseconds > ProducerBlockTimeoutMs Then
                            Interlocked.Add(_droppedBytes, count)
                            Return
                        End If
                        Thread.Sleep(5)
                    End While
                    _queue.Enqueue(copy)
                    Interlocked.Add(_bytesQueued, count)
                Else
                    ' ★ AUDIO: drop-oldest under pressure, BLOCK-ALIGNED.
                    ' Dropping at a non-frame boundary shifts every later
                    ' sample into noise; round the drop to whole frames.
                    While Interlocked.Read(_bytesQueued) + count > _byteCap
                        Dim dropped As Byte() = Nothing
                        If Not _queue.TryDequeue(dropped) Then Exit While
                        Dim dropLen As Integer = dropped.Length
                        dropLen -= (dropLen Mod _blockAlign)
                        If dropLen < dropped.Length Then
                            ' keep the aligned tail as a tiny residual chunk
                            Dim tail As Byte() = New Byte(dropped.Length - dropLen - 1) {}
                            Array.Copy(dropped, dropLen, tail, 0, tail.Length)
                            _queue.Enqueue(tail)
                            Interlocked.Add(_bytesQueued, tail.Length)
                        End If
                        Interlocked.Add(_droppedBytes, dropLen)
                        Interlocked.Add(_bytesQueued, -CLng(dropped.Length))
                    End While
                    _queue.Enqueue(copy)
                    Interlocked.Add(_bytesQueued, count)
                End If
                _signal.Set()
            End Sub

            Private Sub WriterLoop()
                Try
                    ' 1. wait for FFmpeg to connect — on the REAL connection event,
                    '    NOT a blind sleep. Owner run 21:15 (a=0B, exit -22):
                    '    the old blind 15s sleep deadlocked with ffmpeg's SERIAL
                    '    input opening. ffmpeg opens input 0 (video pipe) and blocks
                    '    probing until video data arrives; the video writer was also
                    '    blind-sleeping, so no data for 15s; ffmpeg therefore opened
                    '    the AUDIO pipe only at ~15.1s — but the audio writer checked
                    '    IsConnected ONCE at 15.0s, saw nothing, and exited forever
                    '    (queue silently held everything; 254 chunks < cap so not even
                    '    drops were counted). Waiting on the event fixes both sides:
                    '    video writes the moment ffmpeg connects → probe finishes
                    '    fast → ffmpeg opens the audio pipe promptly → the audio
                    '    writer is still waiting on its event and proceeds.
                    Dim connectWait() As WaitHandle = {_connectedEvent, _stopWriter}
                    WaitHandle.WaitAny(connectWait, 30000)
                    If Not _pipe.IsConnected Then
                        DrainDiscardOnly()
                        Return
                    End If

                    ' 2. AUDIO pipes wait for the timeline (video t0) — the
                    '    discard/pad alignment needs to know t0 first.
                    '    VIDEO pipe does NOT wait: it IS the timeline, and waiting
                    '    here caused the 21:36 failure — during the 5s blind wait
                    '    the queue overflowed (256 chunks) and drop-oldest threw
                    '    away the FIRST encoded frames including SPS/PPS, leaving
                    '    ffmpeg with 'non-existing PPS 0 referenced' and a dead
                    '    output. Video starts writing the moment it connects.
                    '    ★ P13-AUDIO-TIMELINE (2026-08-28): the old 5s timeout
                    '    silently FELL THROUGH unaligned — and a BeginTimeline
                    '    arriving later could no longer apply pad/discard (the
                    '    alignment step had already run), so the Device-clock
                    '    head pad was lost whenever the first sound came after
                    '    the 5s window. The wait is now UNBOUNDED on
                    '    {_timelineStarted, _stopWriter}: RequestStopAndDrain()
                    '    sets both events and Dispose() sets _stopWriter, so
                    '    this cannot deadlock — the alignment contract (pad
                    '    before ANY real byte, discard from the queue head)
                    '    always holds.
                    If _waitForTimeline Then
                        WaitHandle.WaitAny(New WaitHandle() {_timelineStarted, _stopWriter})
                    End If

                    ' 3. initial alignment: pad silence first, then discard head
                    WritePadIfAny()
                    DiscardHeadIfAny()

                    ' 4. steady state: drain as chunks arrive
                    While True
                        WaitHandle.WaitAny(New WaitHandle() {_signal, _stopWriter}, 200)
                        DrainQueue()
                        If _stopWriter.WaitOne(0) Then Exit While
                    End While

                    ' 5. final drain (stop requested)
                    DrainQueue()
                Catch
                End Try
            End Sub

            Private Sub DrainQueue()
                Dim chunk As Byte() = Nothing
                While _queue.TryDequeue(chunk)
                    Interlocked.Add(_bytesQueued, -CLng(chunk.Length))
                    If _pipeBroken Then
                        Interlocked.Add(_droppedBytes, chunk.Length)
                        Continue While
                    End If

                    ' ★ RESILIENT WRITE (the 01:08 killer): one failure used to
                    ' kill the pipe permanently — 19MB of music vanished silently.
                    WriteChunkResilient(chunk)
                End While
            End Sub

            Private Sub DrainDiscardOnly()
                Dim chunk As Byte() = Nothing
                While _queue.TryDequeue(chunk)
                    Interlocked.Add(_bytesQueued, -CLng(chunk.Length))
                    Interlocked.Add(_droppedBytes, chunk.Length)
                End While
            End Sub

            Private Sub WritePadIfAny()
                Dim pad As Long
                SyncLock _sync
                    pad = _padBytes
                    _padBytes = 0
                End SyncLock
                If pad <= 0 OrElse _pipeBroken Then Return
                Dim zeros(65535) As Byte
                Dim off As Long = 0
                While off < pad
                    Dim n As Integer = CInt(Math.Min(pad - off, zeros.Length))
                    Try
                        _pipe.Write(zeros, 0, n)
                        Interlocked.Add(_bytesWritten, n)
                    Catch
                        _pipeBroken = True
                        Return
                    End Try
                    off += n
                End While
            End Sub

            Private Sub DiscardHeadIfAny()
                Dim disc As Long
                SyncLock _sync
                    disc = _discardBytes
                    _discardBytes = 0
                End SyncLock
                If disc <= 0 Then Return
                Dim chunk As Byte() = Nothing
                While disc > 0 AndAlso _queue.TryDequeue(chunk)
                    Interlocked.Add(_bytesQueued, -CLng(chunk.Length))
                    If chunk.Length <= disc Then
                        disc -= chunk.Length
                        Interlocked.Add(_droppedBytes, chunk.Length)
                    Else
                        ' ★ GLM/6 audit #4: partial-chunk head drop must be
                        ' BLOCK-ALIGNED — cutting at an arbitrary byte offset
                        ' shifts every later PCM sample into noise (same rule
                        ' as the audio drop path). Round the skip to whole
                        ' frames; the sub-frame residual stays in the stream
                        ' (sub-millisecond, imperceptible).
                        Dim skip As Integer = CInt(disc)
                        skip -= (skip Mod _blockAlign)
                        If skip <= 0 Then
                            ' alignment made the skip empty — write the whole chunk
                            If Not _pipeBroken Then WriteChunkResilient(chunk)
                            disc = 0
                            Continue While
                        End If
                        Dim keep As Byte() = New Byte(chunk.Length - skip - 1) {}
                        Array.Copy(chunk, skip, keep, 0, keep.Length)
                        Interlocked.Add(_droppedBytes, skip)
                        If Not _pipeBroken Then WriteChunkResilient(keep)
                        disc = 0
                    End If
                End While
            End Sub

            ''' <summary>Single resilient write point (retry x5, logs failures).</summary>
            Private Sub WriteChunkResilient(chunk As Byte())
                Dim attempts As Integer = 0
                While True
                    Try
                        _pipe.Write(chunk, 0, chunk.Length)
                        Interlocked.Add(_bytesWritten, chunk.Length)
                        Return
                    Catch ex As Exception
                        attempts += 1
                        If attempts >= 5 Then
                            _pipeBroken = True
                            _log?.Invoke($"[live-mux:{Name}] pipe write FAILED x{attempts}: {ex.Message} — stream broken")
                            Interlocked.Add(_droppedBytes, chunk.Length)
                            Return
                        End If
                        _log?.Invoke($"[live-mux:{Name}] pipe write attempt {attempts} failed: {ex.Message} — retrying")
                        Thread.Sleep(100)
                    End Try
                End While
            End Sub

            ''' <summary>Signal stop, drain the queue FULLY, then dispose the pipe → FFmpeg reads EOF.</summary>
            Friend Sub RequestStopAndDrain(timeoutMs As Integer)
                _stopWriter.Set()
                _signal.Set()
                _timelineStarted.Set()

                ' ★ 02:41 FIX (clap-sync evidence: dropped=1,530,240B ≈ 8s tail
                ' audio lost): the old code Joined the writer with the 3s pipe
                ' budget — but the audio writer can legitimately still hold a
                ' multi-second backlog (producer was 192KB/s faster than the
                ' consumer during the run). A Join is "thread finished OR
                ' timeout", and on timeout the pipe is disposed with the queue
                ' still loaded → those bytes are never written and FFmpeg's
                ' audio input simply ends early. Correct stop semantics for a
                ' BOUNDED producer/consumer pipe: wait until the QUEUE is
                ' drained (bytes actually handed to the pipe), not until the
                ' thread is idle. Video (blockProducer) can never have a
                ' backlog — its Feed blocks — so this wait is audio-only in
                ' practice and bounded by backlog/throughput, typically <1s.
                Dim swDrain As Stopwatch = Stopwatch.StartNew()
                While Interlocked.Read(_bytesQueued) > 0 AndAlso
                      swDrain.ElapsedMilliseconds < timeoutMs AndAlso
                      Not _pipeBroken
                    Thread.Sleep(10)
                End While
                Dim drainedResidual As Long = Interlocked.Read(_bytesQueued)
                If drainedResidual > 0 Then
                    _log?.Invoke($"[live-mux:{Name}] drain timeout with {drainedResidual:N0}B still queued — closing (bytes will be lost)")
                End If

                If _writer IsNot Nothing AndAlso Not _writer.Join(timeoutMs) Then
                    ' writer stuck on a blocked pipe write — force close
                    Try : _pipe.Dispose() : Catch : End Try
                    Try : _writer.Join(2000) : Catch : End Try
                End If

                ' ★ Accounting fix: whatever is STILL queued after the pipe is
                ' closed is lost — the old code logged it but never counted it
                ' into DroppedBytes, so the session-level pass gate (which fails
                ' only when DroppedBytes > 0) could pass a file that silently
                ' lost tail bytes. Fold the residual into the honest counter.
                Dim residualAfterClose As Long = Interlocked.Read(_bytesQueued)
                Dim residualChunk As Byte() = Nothing
                While _queue.TryDequeue(residualChunk)
                    Interlocked.Add(_bytesQueued, -CLng(residualChunk.Length))
                    Interlocked.Add(_droppedBytes, residualChunk.Length)
                End While
                If residualAfterClose > 0 Then
                    _log?.Invoke($"[live-mux:{Name}] {residualAfterClose:N0}B still queued after close — counted as dropped")
                End If

                ' ★ Accounting fix: everything fed from this point on can
                ' never reach the pipe (writer joined) — Feed now counts it.
                Interlocked.Exchange(_closed, 1)

                ' ★ 02:23 FIX ('Error opening input: No such file'): on very
                ' short recordings Stop() arrives while ffmpeg is STILL probing
                ' the video pipe and has not yet OPENED this audio pipe. Disposing
                ' now deletes the pipe server → ffmpeg's later open fails → the
                ' whole output dies (-2) with NO audio in the file. Wait (bounded)
                ' for ffmpeg to connect to THIS pipe first; if it never does
                ' (crashed), the timeout still closes cleanly.
                ' ★ G1 FIX: the old WaitAny({_connectedEvent, _stopWriter}) could
                ' never wait — this method sets _stopWriter at entry, so WaitAny
                ' returned IMMEDIATELY and the connection guard was dead code.
                ' Wait on the connection event alone (still bounded at 3 s);
                ' WaitForConnection completing/failing sets the event anyway, so
                ' a concurrent Dispose cannot hang here.
                If Not _pipe.IsConnected Then
                    _connectedEvent.WaitOne(3000)
                End If
                Try : _pipe.Dispose() : Catch : End Try
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                _stopWriter.Set()
                _signal.Set()
                Try : _pipe.Dispose() : Catch : End Try
                Try : _signal.Dispose() : Catch : End Try
                Try : _stopWriter.Dispose() : Catch : End Try
                Try : _timelineStarted.Dispose() : Catch : End Try
                Try : _connectedEvent.Dispose() : Catch : End Try
            End Sub
        End Class

    End Class

End Namespace
