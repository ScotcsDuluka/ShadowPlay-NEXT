Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Threading

Namespace CaptureEngine.FFmpegBackend
    ''' <summary>
    ''' Coordinates the mux step: ffprobe (get video duration) + FFmpeg mux
    ''' (combine temp video + temp audio → final output) + temp file cleanup.
    '''
    ''' P1-C Commit 4: Full implementation.
    '''
    ''' Architecture (per Engine-Audio two-process design):
    '''   - Created lazily at FFmpegPipelineBackend.Stop() time (if audio enabled)
    '''   - Step 1: ffprobe temp.video.mp4 → get exact video duration (ms precision)
    '''   - Step 2: compute per-track audio offset (videoStart - audioStart)
    '''   - Step 3: spawn mux FFmpeg: -i video.mp4 -i system.wav [-i mic.wav]
    '''             -c:v copy -c:a aac -ss <offset> -t <duration> → final.mp4
    '''   - Step 4: delete temp files (only if mux succeeded)
    '''
    ''' Thread safety:
    '''   - Run() is called from FFmpegPipelineBackend.Stop() (serialized)
    '''   - Not concurrent — only one mux at a time
    '''   - Guarded by FFmpegPipelineBackend._muxCompleted (Interlocked.Exchange)
    '''
    ''' Critical rule (per OWNER):
    '''   ❌ NEVER hold a lock across ffprobe() or mux process WaitForExit()
    '''   ✅ These methods are called OUTSIDE the caller's lock
    ''' </summary>
    ''' ★ C/5 DORMANT (audit 2026-09-06): zero production callers — constructed
    ''' only by FFmpegPipelineBackend (dormant) + Recording.Tests. Friend =
    ''' compile-time isolation; contains delete-before-move on OutputPath, so
    ''' do NOT re-activate without the MapSessionConfig H1 seam.
    Friend NotInheritable Class MuxCoordinator
        Implements IDisposable

        Private ReadOnly _sync As New Object()
        Private _disposed As Boolean = False

        ' Paths (set by caller before Run)
        Public Property FFmpegPath As String = ""
        Public Property TempVideoPath As String = ""
        Public Property TempSystemWavPath As String = ""
        Public Property TempMicWavPath As String = ""
        Public Property OutputPath As String = ""

        ' Per-track sync offsets (computed by caller)
        Public Property SystemOffsetSec As Double = 0
        Public Property MicOffsetSec As Double = 0

        ' Video duration (filled by ffprobe, used by mux -t)
        Public Property VideoDurationSec As Double = 0

        ' Audio config (set by caller)
        Public Property HasSystemAudio As Boolean = False
        Public Property HasMicAudio As Boolean = False
        Public Property SystemVolume As Single = 1.0F
        Public Property MicVolume As Single = 1.0F
        Public Property SeparateTracks As Boolean = False

        ''' <summary>
        ''' Phase 12b: optional hook invoked right after every spawned ffmpeg/
        ''' ffprobe process starts. The production host uses this to assign the
        ''' child to its JobObjectGuard (KILL_ON_JOB_CLOSE) so a host crash can
        ''' never leave an orphan ffmpeg behind. Additive — Nothing keeps the
        ''' proven behavior unchanged.
        ''' </summary>
        Public Property OnProcessStarted As Action(Of Process) = Nothing

        ''' <summary>
        ''' Get exact video duration via ffprobe (ms precision).
        ''' Returns duration in seconds, or 0.0 if ffprobe fails.
        ''' Does NOT throw — failures are logged and return 0.0.
        ''' </summary>
        Public Function ProbeVideoDuration() As Double
            If Not File.Exists(TempVideoPath) Then Return 0.0

            Dim ffmpegDir As String = Path.GetDirectoryName(FFmpegPath)
            Dim ffprobePath As String = Path.Combine(ffmpegDir, "ffprobe.exe")
            If Not File.Exists(ffprobePath) Then
                ' Try subfolder
                ffprobePath = Path.Combine(ffmpegDir, "API-Core", "ffprobe.exe")
                If Not File.Exists(ffprobePath) Then
                    ffprobePath = Path.Combine(ffmpegDir, "api-core", "ffprobe.exe")
                End If
            End If
            If Not File.Exists(ffprobePath) Then Return 0.0

            Try
                Dim psi As New ProcessStartInfo()
                psi.FileName = ffprobePath
                psi.Arguments = "-v error -show_entries format=duration -of csv=p=0 """ & TempVideoPath & """"
                psi.UseShellExecute = False
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = False
                psi.CreateNoWindow = True

                Using proc As New Process()
                    proc.StartInfo = psi
                    proc.Start()

                    ' Phase 12b: give the host a chance to assign ffprobe to its job object.
                    Try : OnProcessStarted?.Invoke(proc) : Catch : End Try

                    Dim stdoutTask As Task(Of String) = proc.StandardOutput.ReadToEndAsync()

                    If Not proc.WaitForExit(5000) Then
                        Try : proc.Kill() : Catch
                        End Try
                        Try : proc.WaitForExit(1000) : Catch
                        End Try
                        Return 0.0
                    End If

                    Dim stdout As String = ""
                    Try
                        stdout = stdoutTask.Result.Trim()
                    Catch
                    End Try

                    If String.IsNullOrEmpty(stdout) Then Return 0.0

                    Dim dur As Double = 0.0
                    If Double.TryParse(stdout, NumberStyles.Any, CultureInfo.InvariantCulture, dur) AndAlso dur > 0 Then
                        VideoDurationSec = dur
                        Return dur
                    End If
                End Using
            Catch
            End Try

            Return 0.0
        End Function

        ''' <summary>
        ''' Run the mux FFmpeg process (video + audio → final output).
        ''' Returns True if mux succeeded (exit code 0); False if failed.
        ''' Does NOT throw — failures return False.
        '''
        ''' Builds the mux command:
        '''   ffmpeg -hide_banner -loglevel info
        '''     -i video.mp4
        '''     [-ss <sysOffset>] -i system.wav
        '''     [-ss <micOffset>] -i mic.wav
        '''     -map 0:v -map 1:a [-map 2:a]
        '''     -c:v copy
        '''     -c:a aac -b:a 320k -ar 48000
        '''     [-t <videoDuration>]
        '''     -movflags +faststart -y output.mp4
        ''' </summary>
        Public Function Run() As Boolean
            If Not File.Exists(TempVideoPath) Then Return False

            Dim args As String = BuildMuxArguments()

            Try
                Dim psi As New ProcessStartInfo()
                psi.FileName = FFmpegPath
                psi.Arguments = args
                psi.UseShellExecute = False
                psi.RedirectStandardOutput = True
                psi.RedirectStandardError = True
                psi.CreateNoWindow = True

                Using proc As New Process()
                    proc.StartInfo = psi
                    proc.Start()

                    ' Phase 12b: give the host a chance to assign the mux ffmpeg
                    ' to its job object (no-orphan-ffmpeg acceptance criterion).
                    Try : OnProcessStarted?.Invoke(proc) : Catch : End Try

                    Dim stdoutTask As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
                    Dim stderrTask As Task(Of String) = proc.StandardError.ReadToEndAsync()

                    If Not proc.WaitForExit(60000) Then
                        Try : proc.Kill() : Catch
                        End Try
                        Try : proc.WaitForExit(2000) : Catch
                        End Try
                        Return False
                    End If

                    ' Drain stdout/stderr (prevents deadlock)
                    Try : stdoutTask.Wait(1000) : Catch
                    End Try
                    Try : stderrTask.Wait(1000) : Catch
                    End Try

                    Return proc.ExitCode = 0
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Delete temp files (only called if mux succeeded).</summary>
        Public Sub CleanupTempFiles()
            DeleteIfExists(TempVideoPath)
            DeleteIfExists(TempSystemWavPath)
            DeleteIfExists(TempMicWavPath)
        End Sub

        ''' <summary>
        ''' If no audio data, just rename temp video to final output (no mux needed).
        ''' Returns True if rename succeeded; False if rename failed.
        ''' </summary>
        Public Function RenameTempVideoToOutput() As Boolean
            Try
                If File.Exists(OutputPath) Then File.Delete(OutputPath)
                File.Move(TempVideoPath, OutputPath)
                Return True
            Catch
                Return False
            End Try
        End Function

        ' ===== Private helpers =====

        Private Function BuildMuxArguments() As String
            Dim sb As New StringBuilder()

            sb.Append("-hide_banner -loglevel info ")

            ' Input 0: video (temp .video.mp4)
            sb.Append($"-i ""{TempVideoPath}"" ")

            ' Per-track audio input with positive offset (-ss)
            Dim sysDelayMs As Integer = CInt(Math.Max(0, -SystemOffsetSec) * 1000)
            Dim micDelayMs As Integer = CInt(Math.Max(0, -MicOffsetSec) * 1000)

            ' Input 1: system audio
            If HasSystemAudio AndAlso File.Exists(TempSystemWavPath) Then
                If SystemOffsetSec > 0.001 Then
                    sb.Append($"-ss {SystemOffsetSec.ToString("0.000", CultureInfo.InvariantCulture)} ")
                End If
                sb.Append($"-i ""{TempSystemWavPath}"" ")
            End If

            ' Input 2: mic audio
            If HasMicAudio AndAlso File.Exists(TempMicWavPath) Then
                If MicOffsetSec > 0.001 Then
                    sb.Append($"-ss {MicOffsetSec.ToString("0.000", CultureInfo.InvariantCulture)} ")
                End If
                sb.Append($"-i ""{TempMicWavPath}"" ")
            End If

            ' Determine actual audio inputs present
            Dim hasSys As Boolean = HasSystemAudio AndAlso File.Exists(TempSystemWavPath)
            Dim hasMic As Boolean = HasMicAudio AndAlso File.Exists(TempMicWavPath)

            If hasSys AndAlso hasMic Then
                If SeparateTracks Then
                    Dim sysFilter As String = BuildAudioFilter(SystemVolume, sysDelayMs, True)
                    Dim micFilter As String = BuildAudioFilter(MicVolume, micDelayMs, True)
                    sb.Append("-map 0:v -map 1:a -map 2:a ")
                    sb.Append($"-af:0 {sysFilter} ")
                    sb.Append($"-af:1 {micFilter} ")
                    sb.Append("-c:v copy ")
                    sb.Append("-c:a:0 aac -b:a:0 320k -ar:a:0 48000 ")
                    sb.Append("-c:a:1 aac -b:a:1 320k -ar:a:1 48000 ")
                Else
                    Dim sysFilter As String = BuildAudioFilter(SystemVolume, sysDelayMs, False)
                    Dim micFilter As String = BuildAudioFilter(MicVolume, micDelayMs, False)
                    sb.Append("-filter_complex ""[1:a]" & sysFilter & "[a0];" &
                              "[2:a]" & micFilter & "[a1];" &
                              "[a0][a1]amix=inputs=2:duration=longest:normalize=0,apad[aout]"" ")
                    sb.Append("-map 0:v -map [aout] ")
                    sb.Append("-c:v copy ")
                    sb.Append("-c:a aac -b:a 320k -ar 48000 ")
                End If
            ElseIf hasSys Then
                Dim sysFilter As String = BuildAudioFilter(SystemVolume, sysDelayMs, True)
                sb.Append("-map 0:v -map 1:a ")
                sb.Append($"-af {sysFilter} ")
                sb.Append("-c:v copy ")
                sb.Append("-c:a aac -b:a 320k -ar 48000 ")
            ElseIf hasMic Then
                Dim micFilter As String = BuildAudioFilter(MicVolume, micDelayMs, True)
                sb.Append("-map 0:v -map 1:a ")
                sb.Append($"-af {micFilter} ")
                sb.Append("-c:v copy ")
                sb.Append("-c:a aac -b:a 320k -ar 48000 ")
            Else
                sb.Append("-map 0:v -c:v copy ")
            End If

            ' Duration trim
            If (hasSys OrElse hasMic) AndAlso VideoDurationSec > 0.001 Then
                sb.Append($"-t {VideoDurationSec.ToString("0.000", CultureInfo.InvariantCulture)} ")
            End If

            ' Faststart for MP4-family
            Dim ext As String = Path.GetExtension(OutputPath).ToLowerInvariant()
            If ext = ".mp4" OrElse ext = ".mov" OrElse ext = ".m4v" Then
                sb.Append("-movflags +faststart ")
            End If

            ' Fallback if no -t
            If (hasSys OrElse hasMic) AndAlso VideoDurationSec <= 0.001 Then
                sb.Append("-shortest ")
            End If

            sb.Append($"-y ""{OutputPath}""")

            Return sb.ToString()
        End Function

        Private Shared Function BuildAudioFilter(volume As Single, delayMs As Integer, includeApad As Boolean) As String
            Dim parts As New List(Of String)()

            If Math.Abs(volume - 1.0F) > 0.001F Then
                Dim v As Single = Math.Max(0.0F, Math.Min(2.0F, volume))
                parts.Add($"volume={v.ToString("0.000", CultureInfo.InvariantCulture)}")
            End If

            parts.Add("aresample=async=1:first_pts=0")

            If delayMs > 0 Then
                parts.Add($"adelay={delayMs}|{delayMs}")
            End If

            If includeApad Then
                parts.Add("apad")
            End If

            Return String.Join(",", parts)
        End Function

        Private Sub DeleteIfExists(path As String)
            If String.IsNullOrEmpty(path) Then Return
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _sync
                If _disposed Then Return
                _disposed = True
            End SyncLock
        End Sub
    End Class
End Namespace
