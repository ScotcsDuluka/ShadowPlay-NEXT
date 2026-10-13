Option Strict On
Option Explicit On
Option Infer On

' DualTrackTests.vb — M1 mic second track runtime validation (real ffmpeg).
'
' Standing design (GPT): System → WavSidecarWriter #1, Mic → #2 — independent
' timing, independent accounting, NO merged queues. These tests exercise the
' REAL production mux path (MuxCoordinator with BOTH tracks + per-track
' offsets) using synthetic sidecars written by the REAL WavSidecarWriter.
'
' The three mandated patterns prove tracks do not swap and offsets hold:
'
'   A: system TONE / mic SILENCE  → tone must be on stream #0:a:0 ONLY
'   B: system SILENCE / mic TONE  → tone must be on stream #0:a:1 ONLY
'   C: both TONE (system @1.0s, mic @2.0s content time)
'      → sys tone at 1.0s on :0, mic tone at 2.0s on :1 (per-stream!)
'
' Per-stream measurement: ffmpeg silencedetect PER AUDIO STREAM via
' -map 0:a:0 / -map 0:a:1 (select one stream at a time) — no guessing from
' a mixed decode. Tone positions are asserted with ±80ms tolerance like the
' single-track suite.
'
' Independent-clock realism: system sidecar is 48kHz stereo, mic sidecar is
' 44.1kHz mono — different device formats on purpose; the mux normalizes
' output rate, capture layers never resample (GPT hard rule).

Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording.Tests

    Friend Module DualTrackTests

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── Dual-track (M1: system + mic, real ffmpeg mux) ──")

            If Not RuntimeSyncTests.EnsureMediaForDual() Then
                TestRunner.RunSkip("DUAL: *all*", "ffmpeg/ffprobe not available")
                Return
            End If
            _sandbox = RuntimeSyncTests.SandboxDir
            _ffmpeg = RuntimeSyncTests.FfmpegExe
            _videoMp4 = RuntimeSyncTests.SharedVideoMp4

            TestRunner.RunTest("DUAL A: sys tone / mic silence → tone on stream 0 only", AddressOf Test_A_SysToneMicSilence)
            TestRunner.RunTest("DUAL B: sys silence / mic tone → tone on stream 1 only", AddressOf Test_B_SysSilenceMicTone)
            TestRunner.RunTest("DUAL C: both tones → per-stream positions exact", AddressOf Test_C_BothTones)
            TestRunner.RunTest("DUAL C-neg: swapped offsets would fail (sanity of the harness)", AddressOf Test_C_HarnessSanity)
        End Sub

        ' ─── helpers ────────────────────────────────────────────────

        Private _sandbox As String
        Private _ffmpeg As String
        Private _videoMp4 As String

        ''' <summary>Write a sidecar WAV via the real writer: tone window [toneAt, toneAt+len].</summary>
        Private Function WriteSidecar(path As String, sampleRate As Integer, channels As Integer,
                                      totalSec As Double, toneAt As Double, toneLen As Double) As WavFinalizeReport
            Using w As New WavSidecarWriter(path, channels, sampleRate, 16)
                w.Start()
                Dim chunkSamples As Integer = sampleRate \ 100          ' 10ms chunks
                Dim totalChunks As Integer = CInt(totalSec * 100)
                Dim toneStartChunk As Integer = CInt(toneAt * 100)
                Dim toneEndChunk As Integer = CInt((toneAt + toneLen) * 100)
                For i As Integer = 0 To totalChunks - 1
                    Dim chunk As Byte()
                    If i >= toneStartChunk AndAlso i < toneEndChunk Then
                        chunk = ToneStereo(chunkSamples, 0.6)
                    Else
                        chunk = New Byte(chunkSamples * channels * 2 - 1) {}
                    End If
                    w.EnqueueChunk(chunk, chunk.Length)
                    Thread.Sleep(1)   ' pace like a real WASAPI producer
                Next
                Return w.Complete(5000)
            End Using
        End Function

        ''' <summary>440Hz tone, stereo samples (works for mono too — caller passes 1ch? no: stereo only here for sys; mono uses ToneMono).</summary>
        Private Function ToneStereo(samples As Integer, amplitude As Double) As Byte()
            Dim out(samples * 4 - 1) As Byte   ' 2ch * 2 bytes
            For i As Integer = 0 To samples - 1
                Dim v As Integer = CInt(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * amplitude * 32767.0)
                If v > 32767 Then v = 32767
                If v < -32768 Then v = -32768
                Dim u As UShort = CUShort(v And &HFFFFI)
                out(i * 4) = CByte(u And &HFFUI)
                out(i * 4 + 1) = CByte((u >> 8) And &HFFUI)
                out(i * 4 + 2) = out(i * 4)
                out(i * 4 + 3) = out(i * 4 + 1)
            Next
            Return out
        End Function

        ''' <summary>Run the dual-track mux with the REAL MuxCoordinator.</summary>
        Private Function RunDualMux(sysWav As String, micWav As String, outPath As String,
                                    sysOffset As Double, micOffset As Double) As Boolean
            Dim tempVideo As String = Path.Combine(Path.GetDirectoryName(outPath), "v.tmp.mp4")
            File.Copy(_videoMp4, tempVideo, overwrite:=True)

            Using mux As New MuxCoordinator()
                mux.FFmpegPath = _ffmpeg
                mux.TempVideoPath = tempVideo
                mux.TempSystemWavPath = sysWav
                mux.TempMicWavPath = micWav
                mux.OutputPath = outPath
                mux.HasSystemAudio = File.Exists(sysWav)
                mux.HasMicAudio = File.Exists(micWav)
                mux.SeparateTracks = True                      ' dual output tracks — measurable per stream
                mux.SystemOffsetSec = sysOffset
                mux.MicOffsetSec = micOffset
                mux.VideoDurationSec = mux.ProbeVideoDuration()
                Return mux.Run()
            End Using
        End Function

        ''' <summary>Duration of one audio stream of the MP4 (ffprobe).</summary>
        Private Function StreamDuration(mp4 As String, streamIndex As Integer) As Double
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = Path.Combine(Path.GetDirectoryName(_ffmpeg), "ffprobe.exe"),
                    .Arguments = $"-v error -select_streams a:{streamIndex} -show_entries stream=duration -of csv=p=0 ""{mp4}""",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim outp As String = p.StandardOutput.ReadToEnd().Trim()
                    p.WaitForExit(5000)
                    Dim d As Double
                    If Double.TryParse(outp, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then Return d
                End Using
            Catch
            End Try
            Return -1.0
        End Function

        ''' <summary>
        ''' First non-silent START on one audio stream. Uses silencedetect but
        ''' EXCLUDES the EOF flush event: at end-of-stream silencedetect emits a
        ''' final silence_end at ≈ stream duration — an all-silent stream therefore
        ''' reports one silence_end at the very end (found the hard way: DUAL A/B
        ''' initially 'detected a tone' at 5.013s on fully silent tracks).
        ''' Returns -1 when the stream has no tone.
        ''' </summary>
        Private Function FirstToneOnStream(mp4 As String, streamIndex As Integer) As Double
            Try
                Dim dur As Double = StreamDuration(mp4, streamIndex)
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpeg,
                    .Arguments = $"-hide_banner -i ""{mp4}"" -map 0:a:{streamIndex} " &
                                 $"-af silencedetect=noise=-35dB:d=0.05 -f null -",
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim err As String = p.StandardError.ReadToEnd()
                    p.WaitForExit(30000)

                    ' Collect every silence_end:<t> and keep the FIRST one that is
                    ' NOT the EOF flush (within 0.5s of stream end).
                    Dim pos As Integer = 0
                    While True
                        Dim idx As Integer = err.IndexOf("silence_end:", pos)
                        If idx < 0 Then Exit While
                        Dim rest As String = err.Substring(idx + "silence_end:".Length).Trim()
                        Dim parts As String() = rest.Split(New Char() {" "c, "|"c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                        Dim d As Double
                        If parts.Length > 0 AndAlso Double.TryParse(parts(0), NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                            If dur <= 0 OrElse Math.Abs(d - dur) > 0.5 Then
                                Return d   ' real silence→tone transition
                            End If
                        End If
                        pos = idx + 12
                    End While
                End Using
            Catch
            End Try
            Return -1.0
        End Function

        ''' <summary>
        ''' Peak volume of one audio stream (volumedetect) — independent second
        ''' measurement. A tone track peaks near -5 dB; a silent track near -90 dB.
        ''' Returns max_volume in dB, or Double.NaN on failure.
        ''' </summary>
        Private Function MaxVolumeOnStream(mp4 As String, streamIndex As Integer) As Double
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpeg,
                    .Arguments = $"-hide_banner -i ""{mp4}"" -map 0:a:{streamIndex} -af volumedetect -f null -",
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim err As String = p.StandardError.ReadToEnd()
                    p.WaitForExit(30000)
                    Dim idx As Integer = err.IndexOf("max_volume:")
                    If idx < 0 Then Return Double.NaN
                    Dim rest As String = err.Substring(idx + "max_volume:".Length).Trim()
                    Dim tok As String = rest.Split(New Char() {" "c})(0)
                    Dim d As Double
                    If Double.TryParse(tok, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then Return d
                End Using
            Catch
            End Try
            Return Double.NaN
        End Function

        Private Function AudioStreamCount(mp4 As String) As Integer
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = Path.Combine(Path.GetDirectoryName(_ffmpeg), "ffprobe.exe"),
                    .Arguments = $"-v error -select_streams a -show_entries stream=index -of csv=p=0 ""{mp4}""",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim outp As String = p.StandardOutput.ReadToEnd()
                    p.WaitForExit(5000)
                    Dim n As Integer = 0
                    For Each line As String In outp.Split(New Char() {ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                        If line.Trim().Length > 0 Then n += 1
                    Next
                    Return n
                End Using
            Catch
                Return -1
            End Try
        End Function

        ' ─── the three patterns ─────────────────────────────────────

        Private Sub Test_A_SysToneMicSilence()
            Dim dir As String = Path.Combine(_sandbox, "dualA_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)

            ' Sys: 48kHz stereo, tone at 1.0-1.4s. Mic: 44.1kHz MONO, all silence.
            Dim sysWav As String = Path.Combine(dir, "sys.wav")
            Dim micWav As String = Path.Combine(dir, "mic.wav")
            Dim repSys = WriteSidecarMonoOrStereo(sysWav, 48000, 2, 6.0, 1.0, 0.4)
            Dim repMic = WriteSidecarMonoOrStereo(micWav, 44100, 1, 6.0, -1.0, 0.0)   ' no tone
            TestRunner.Assert(repSys.AccountingOk, "sys accounting: " & repSys.ToString())
            TestRunner.Assert(repMic.AccountingOk, "mic accounting: " & repMic.ToString())

            Dim outPath As String = Path.Combine(dir, "final.mp4")
            TestRunner.Assert(RunDualMux(sysWav, micWav, outPath, 0.0, 0.0), "dual mux exit 0")
            TestRunner.Assert(AudioStreamCount(outPath) = 2, "two audio streams present")

            Dim sysTone As Double = FirstToneOnStream(outPath, 0)
            Dim micTone As Double = FirstToneOnStream(outPath, 1)
            Dim micPeak As Double = MaxVolumeOnStream(outPath, 1)
            TestRunner.AssertNear(sysTone, 1.0, 0.08, "sys tone position on stream 0")
            TestRunner.Assert(micTone < 0, "mic stream: no tone transition (silencedetect, EOF-excluded)")
            TestRunner.Assert(Not Double.IsNaN(micPeak) AndAlso micPeak < -50.0,
                              $"mic stream peak must be silence-level (<-50dB), got {micPeak} dB")
        End Sub

        Private Sub Test_B_SysSilenceMicTone()
            Dim dir As String = Path.Combine(_sandbox, "dualB_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)

            Dim sysWav As String = Path.Combine(dir, "sys.wav")
            Dim micWav As String = Path.Combine(dir, "mic.wav")
            Dim repSys = WriteSidecarMonoOrStereo(sysWav, 48000, 2, 6.0, -1.0, 0.0)   ' no tone
            Dim repMic = WriteSidecarMonoOrStereo(micWav, 44100, 1, 6.0, 1.5, 0.4)    ' tone at 1.5s
            TestRunner.Assert(repSys.AccountingOk, "sys accounting")
            TestRunner.Assert(repMic.AccountingOk, "mic accounting")

            Dim outPath As String = Path.Combine(dir, "final.mp4")
            TestRunner.Assert(RunDualMux(sysWav, micWav, outPath, 0.0, 0.0), "dual mux exit 0")
            TestRunner.Assert(AudioStreamCount(outPath) = 2, "two audio streams present")

            Dim sysTone As Double = FirstToneOnStream(outPath, 0)
            Dim micTone As Double = FirstToneOnStream(outPath, 1)
            Dim sysPeak As Double = MaxVolumeOnStream(outPath, 0)
            TestRunner.Assert(sysTone < 0, "sys stream: no tone transition (silencedetect, EOF-excluded)")
            TestRunner.Assert(Not Double.IsNaN(sysPeak) AndAlso sysPeak < -50.0,
                              $"sys stream peak must be silence-level (<-50dB), got {sysPeak} dB")
            TestRunner.AssertNear(micTone, 1.5, 0.08, "mic tone position on stream 1")
        End Sub

        Private Sub Test_C_BothTones()
            Dim dir As String = Path.Combine(_sandbox, "dualC_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)

            ' Sys tone @1.0s, mic tone @2.0s — different content positions per track.
            Dim sysWav As String = Path.Combine(dir, "sys.wav")
            Dim micWav As String = Path.Combine(dir, "mic.wav")
            WriteSidecarMonoOrStereo(sysWav, 48000, 2, 6.0, 1.0, 0.4)
            WriteSidecarMonoOrStereo(micWav, 44100, 1, 6.0, 2.0, 0.4)

            Dim outPath As String = Path.Combine(dir, "final.mp4")
            TestRunner.Assert(RunDualMux(sysWav, micWav, outPath, 0.0, 0.0), "dual mux exit 0")
            TestRunner.Assert(AudioStreamCount(outPath) = 2, "two audio streams present")

            TestRunner.AssertNear(FirstToneOnStream(outPath, 0), 1.0, 0.08, "sys tone on stream 0 @1.0s")
            TestRunner.AssertNear(FirstToneOnStream(outPath, 1), 2.0, 0.08, "mic tone on stream 1 @2.0s")
        End Sub

        ''' <summary>
        ''' Harness sanity (protects against a measurement method that always passes):
        ''' apply a DELIBERATE +0.5s skip to the sys track — its tone (content 1.0s)
        ''' must land at 0.5s, measurably different from the unskipped 1.0s.
        ''' (Not +1.0s: that lands the tone exactly at 0.0 where no leading silence
        ''' exists and silence→tone transitions are undetectable by silencedetect.)
        ''' </summary>
        Private Sub Test_C_HarnessSanity()
            Dim dir As String = Path.Combine(_sandbox, "dualH_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)

            Dim sysWav As String = Path.Combine(dir, "sys.wav")
            Dim micWav As String = Path.Combine(dir, "mic.wav")
            WriteSidecarMonoOrStereo(sysWav, 48000, 2, 6.0, 1.0, 0.4)
            WriteSidecarMonoOrStereo(micWav, 44100, 1, 6.0, 1.0, 0.4)

            Dim outPath As String = Path.Combine(dir, "final.mp4")
            TestRunner.Assert(RunDualMux(sysWav, micWav, outPath, 0.5, 0.0), "mux exit 0")

            ' sys tone at content 1.0s with +0.5s skip → lands at 0.5s (not 1.0)
            TestRunner.AssertNear(FirstToneOnStream(outPath, 0), 0.5, 0.08, "offset-skip sanity: sys tone at 0.5s")
            ' mic unaffected: tone at 1.0s
            TestRunner.AssertNear(FirstToneOnStream(outPath, 1), 1.0, 0.08, "offset-skip sanity: mic tone unchanged")
        End Sub

        ''' <summary>Unified sidecar writer: stereo(2ch) or mono(1ch) tone generation.</summary>
        Private Function WriteSidecarMonoOrStereo(path As String, sampleRate As Integer, channels As Integer,
                                                  totalSec As Double, toneAt As Double, toneLen As Double) As WavFinalizeReport
            If channels = 2 Then Return WriteSidecar(path, sampleRate, channels, totalSec, toneAt, toneLen)

            Using w As New WavSidecarWriter(path, channels, sampleRate, 16)
                w.Start()
                Dim chunkSamples As Integer = sampleRate \ 100
                Dim totalChunks As Integer = CInt(totalSec * 100)
                Dim toneStartChunk As Integer = CInt(toneAt * 100)
                Dim toneEndChunk As Integer = CInt((toneAt + toneLen) * 100)
                For i As Integer = 0 To totalChunks - 1
                    Dim chunk As Byte()
                    If i >= toneStartChunk AndAlso i < toneEndChunk Then
                        chunk = New Byte(chunkSamples * 2 - 1) {}
                        For s As Integer = 0 To chunkSamples - 1
                            Dim v As Integer = CInt(Math.Sin(2.0 * Math.PI * 440.0 * s / CSng(sampleRate)) * 0.6 * 32767.0)
                            Dim u As UShort = CUShort(v And &HFFFFI)
                            chunk(s * 2) = CByte(u And &HFFUI)
                            chunk(s * 2 + 1) = CByte((u >> 8) And &HFFUI)
                        Next
                    Else
                        chunk = New Byte(chunkSamples * 2 - 1) {}
                    End If
                    w.EnqueueChunk(chunk, chunk.Length)
                    Thread.Sleep(1)
                Next
                Return w.Complete(5000)
            End Using
        End Function

    End Module

End Namespace
