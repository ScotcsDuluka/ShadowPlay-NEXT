Option Strict On
Option Explicit On
Option Infer On

' RuntimeSyncTests.vb — Phase 12b REAL-RUNTIME A/V sync validation.
'
' This is the automated sync-test half of the AUDIO hard-blocker acceptance:
' it exercises the EXACT production pipeline stages that don't need a GPU —
'
'   WavSidecarWriter → temp WAV        (real sidecar writer)
'   raw H.264 → wrap (-f h264 -r R)    (same args as CaptureSession)
'   MuxCoordinator.Run + SyncMath      (real mux, real ffprobe)
'   silencedetect on the final MP4     (measures where the tone LANDED)
'
' Scenario model: the WAV contains a 440Hz tone at CONTENT time 1.0–1.4s.
' The session timeline relationship between video start and audio start is
' simulated with Stopwatch ticks, run through SyncMath (the real production
' math), and the mux must place the tone at:
'
'   aligned        (offset  0.0) → tone at 1.0s
'   audio 0.5s LATE (offset -0.5) → tone at 1.5s   (adelay path)
'   audio 0.5s EARLY(offset +0.5) → tone at 0.5s   (-ss skip path)
'
' Runs wherever ffmpeg+ffprobe exist (Windows AND this Linux CI box).

Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.Threading
Imports System.IO
Imports CaptureEngine.FFmpegBackend

Namespace CaptureEngine.Recording.Tests

    Friend Module RuntimeSyncTests

        Friend _sandbox As String = Nothing        ' dir with ffmpeg.exe/ffprobe.exe + shared media
        Friend _ffmpegExe As String = Nothing      ' sandbox\ffmpeg.exe

        ''' <summary>★ M1 dual-track tests reuse the same shared media (exposed).</summary>
        Friend ReadOnly Property SandboxDir As String
            Get
                Return _sandbox
            End Get
        End Property

        Friend ReadOnly Property FfmpegExe As String
            Get
                Return _ffmpegExe
            End Get
        End Property

        Friend ReadOnly Property SharedVideoMp4 As String
            Get
                Return _videoMp4
            End Get
        End Property

        ''' <summary>
        ''' ★ M1: one-shot media setup shared by the single-track and dual-track
        ''' suites. Returns True when sandbox + wrapped video are ready.
        ''' CRITICAL: never re-runs DiscoverFFmpeg when the SYNC-RT suite already
        ''' built its sandbox — that created a SECOND empty sandbox (no media, no
        ''' ffprobe) and DUAL skipped on Windows despite everything existing in
        ''' the first sandbox. If no H.264 encoder exists, fall back to a
        ''' copy-only wrap (the DUAL tests mux but never re-encode video).
        ''' </summary>
        Friend Function EnsureMediaForDual() As Boolean
            ' Already prepared by RuntimeSyncTests.RunAll → reuse as-is.
            If _sandbox IsNot Nothing AndAlso File.Exists(_videoMp4) Then Return True

            ' Fresh process / SYNC-RT skipped: do the full discovery ourselves.
            If _sandbox Is Nothing Then
                If Not DiscoverFFmpeg() Then Return False
            End If
            If Not PrepareSharedMedia() Then
                ' No H.264 encoder (e.g. NVIDIA-curated API-Core build): DUAL only
                ' needs an MP4 CONTAINER to mux against — even a black frame copied
                ' to death works. Generate via lavfi → null-encoded? Not possible.
                ' Instead: synthesize video directly with whatever encoder exists
                ' (same fallback list) — if none exists at all, bail out honestly.
                If Not PrepareEncoderlessMediaForDual() Then Return False
            End If
            Return _sandbox IsNot Nothing AndAlso File.Exists(_videoMp4)
        End Function

        ''' <summary>
        ''' Encoder-less fallback for DUAL: build the 5s MP4 with ANY available
        ''' H.264 encoder writing straight to MP4 (no raw-H264 + wrap step).
        ''' ★ Found on Windows run: '-preset ultrafast' is x264-only syntax —
        ''' NVENC rejects it with 'Invalid argument' and every encoder in the
        ''' chain failed even on a machine with a GTX 1080 Ti. Per-encoder
        ''' presets now (x264: ultrafast · nvenc: p1 · qsv: veryfast · amf: speed).
        ''' </summary>
        Private Function PrepareEncoderlessMediaForDual() As Boolean
            _videoMp4 = Path.Combine(_sandbox, "video.mp4")
            Dim attempts() As (enc As String, preset As String) = {
                ("libx264", "ultrafast"),
                ("h264_nvenc", "p1"),
                ("h264_qsv", "veryfast"),
                ("h264_amf", "speed")
            }
            For Each a In attempts
                Dim presetArg As String = If(String.IsNullOrEmpty(a.preset), "", $" -preset {a.preset}")
                Dim ok As Boolean = RunFFmpegQuiet(
                    $"-y -hide_banner -loglevel info -f lavfi -i testsrc=size=320x240:rate=60:duration=5 " &
                    $"-c:v {a.enc}{presetArg} -pix_fmt yuv420p ""{_videoMp4}""", 30000)
                If ok AndAlso File.Exists(_videoMp4) AndAlso New FileInfo(_videoMp4).Length > 1000 Then
                    Console.WriteLine($"      [DUAL] video encoder: {a.enc} (preset {a.preset})")
                    Return True
                End If
                Try : File.Delete(_videoMp4) : Catch : End Try
            Next
            Console.WriteLine("      [DUAL] no H.264 encoder available at all — cannot prepare media")
            Return False
        End Function

        ''' <summary>Run ffmpeg, print the stderr TAIL on failure (encoder diagnosis).</summary>
        Private Function RunFFmpegQuiet(args As String, timeoutMs As Integer) As Boolean
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegExe,
                    .Arguments = args,
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim errTask = p.StandardError.ReadToEndAsync()
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    If Not p.WaitForExit(timeoutMs) Then
                        Try : p.Kill() : Catch : End Try
                        Return False
                    End If
                    errTask.Wait(2000) : outTask.Wait(2000)
                    If p.ExitCode <> 0 Then
                        Dim tail As String = ""
                        Try
                            Dim lines As String() = errTask.Result.Split(New Char() {ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                            tail = String.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 2)).ToArray())
                        Catch
                        End Try
                        Console.WriteLine($"      [DUAL] encoder attempt failed: {tail}")
                    End If
                    Return p.ExitCode = 0
                End Using
            Catch
                Return False
            End Try
        End Function
        Private _videoH264 As String = Nothing
        Private _videoMp4 As String = Nothing
        Private _videoDuration As Double = 0.0

        Public Sub RunAll()
            Console.WriteLine()
            Console.WriteLine("── Runtime sync (real ffmpeg + MuxCoordinator) ──")

            If Not DiscoverFFmpeg() Then
                TestRunner.RunSkip("SYNC-RT: *all*", "ffmpeg/ffprobe not found (set RRT_FFMPEG to a dir containing both)")
                Return
            End If

            If Not PrepareSharedMedia() Then
                TestRunner.RunSkip("SYNC-RT: *all*", "no H.264 encoder available (tried libx264/h264_nvenc/h264_qsv/h264_amf)")
                Return
            End If

            TestRunner.RunTest("SYNC-RT: aligned session → tone at 1.000s", Sub() Scenario(0.0, 1.0))
            TestRunner.RunTest("SYNC-RT: audio 0.5s LATE → tone at 1.500s", Sub() Scenario(-0.5, 1.5))
            TestRunner.RunTest("SYNC-RT: audio 0.5s EARLY → tone at 0.500s", Sub() Scenario(0.5, 0.5))
            TestRunner.RunTest("SYNC-RT: video-only mux (audio disabled)", AddressOf Test_VideoOnly)
            TestRunner.RunTest("SYNC-RT: cleanup removes temp files", AddressOf Test_Cleanup)
        End Sub

        ' ─── FFmpeg discovery ──────────────────────────────────────────

        ''' <summary>
        ''' Phase 12b: walk up from the test bin dir to the repo root and
        ''' find the production deployment at Overlay\API-Core. Without this
        ''' the runtime sync suite SKIPs on Windows even though the real
        ''' ffmpeg exists there (first Windows validation 2026-08-23).
        ''' </summary>
        Private Function FindRepoRootFFmpeg() As String
            Dim dir As New DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory)
            For i As Integer = 1 To 8
                If dir Is Nothing Then Return Nothing
                Dim candidate As String = Path.Combine(dir.FullName, "Overlay", "API-Core", "ffmpeg.exe")
                If File.Exists(candidate) Then Return candidate
                dir = dir.Parent
            Next
            Return Nothing
        End Function

        Private Function DiscoverFFmpeg() As Boolean
            Dim root As String = Environment.GetEnvironmentVariable("RRT_FFMPEG")
            Dim candidates As New List(Of String)

            If Not String.IsNullOrEmpty(root) Then
                candidates.Add(Path.Combine(root, "ffmpeg"))
                candidates.Add(Path.Combine(root, "ffmpeg.exe"))
            End If
            candidates.Add("/usr/bin/ffmpeg")       ' Linux CI box
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"))
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "API-Core", "ffmpeg.exe"))
            candidates.Add(FindRepoRootFFmpeg())    ' Windows repo checkout → Overlay\API-Core

            Dim found As String = Nothing
            For Each c As String In candidates
                Try
                    If File.Exists(c) Then found = c : Exit For
                Catch
                End Try
            Next
            If found Is Nothing Then Return False

            ' ffprobe must sit in the same directory as ffmpeg
            Dim dir As String = Path.GetDirectoryName(found)
            Dim ffprobeCandidates() As String = {
                Path.Combine(dir, "ffprobe.exe"),
                Path.Combine(dir, "ffprobe")
            }
            Dim ffprobeFound As String = Nothing
            For Each c As String In ffprobeCandidates
                If File.Exists(c) Then ffprobeFound = c : Exit For
            Next
            If ffprobeFound Is Nothing Then Return False

            ' Sandbox: MuxCoordinator probes "<dir of FFmpegPath>\ffprobe.exe".
            ' Normalize names there so the production resolution logic is
            ' exercised verbatim on every OS.
            '
            ' Sandbox hygiene (C-hygiene pass): reclaim RRT_RT_* sandboxes
            ' abandoned by earlier hard-killed runs (older than 6h) BEFORE
            ' creating this run's own. Only this suite's prefix under the
            ' temp path is ever touched; cleanup failures become warnings.
            SandboxSweepStale(6)
            _sandbox = Path.Combine(Path.GetTempPath(), "RRT_RT_" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Directory.CreateDirectory(_sandbox)
            _ffmpegExe = Path.Combine(_sandbox, "ffmpeg.exe")
            ' ★ Windows-run fix #3 (THE REAL ONE): copy the WHOLE ffmpeg folder.
            ' ffmpeg.exe is not standalone — on the owner's deployment it needs
            ' avdevice-62.dll, avcodec-62.dll, avutil-60.dll, ... beside it. The
            ' previous single-file copy produced 'The code execution cannot
            ' proceed because avdevice-62.dll was not found' (Win32 loader dialog
            ' → empty stderr → every encoder attempt 'failed' silently).
            ' Guard: only folder-copy when the source dir actually contains dlls
            ' (a deployment folder). /usr/bin on Linux has no dlls → single copy.
            Dim srcDir As New DirectoryInfo(Path.GetDirectoryName(found))
            Dim hasDlls As Boolean = srcDir.GetFiles("*.dll").Length > 0
            If hasDlls Then
                For Each f As FileInfo In srcDir.GetFiles()
                    Dim ext As String = f.Extension.ToLowerInvariant()
                    If ext = ".exe" OrElse ext = ".dll" Then
                        TryCopyFile(f.FullName, Path.Combine(_sandbox, f.Name))
                        Try : Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Path.Combine(_sandbox, f.Name) & ":Zone.Identifier") : Catch : End Try
                    End If
                Next
            Else
                TryCopyFile(found, _ffmpegExe)
                TryCopyFile(ffprobeFound, Path.Combine(_sandbox, "ffprobe.exe"))
            End If

            ' ★ Verify the sandboxed ffmpeg actually RUNS (-version) — fail loudly
            ' with the real reason instead of empty-stderr mystery downstream.
            If Not SandboxFfmpegWorks() Then
                ' Don't leak the freshly created sandbox on a discovery failure.
                SandboxTryDelete(_sandbox, "sandbox ffmpeg failed -v verification")
                _sandbox = Nothing
                Return False
            End If

            Console.WriteLine($"      ffmpeg: {found}")
            Console.WriteLine($"      sandbox: {_sandbox}")
            Return True
        End Function

        Private Sub TryCopyFile(src As String, dst As String)
            Try
                File.Copy(src, dst, overwrite:=True)
            Catch ex As Exception
                Console.WriteLine($"      WARN copy {Path.GetFileName(src)} failed: {ex.Message}")
            End Try
        End Sub

        ' ─── Sandbox hygiene (C-hygiene pass) ─────────────────────────
        ' Contract: ONLY directories this suite creates are ever touched
        ' (Path.GetTempPath() + name starting with "RRT_RT_"). Cleanup
        ' failures are collected in SandboxCleanupWarnings — reported by the
        ' runner at exit, never masking a test verdict, never throwing.

        Friend ReadOnly SandboxCleanupWarnings As New List(Of String)()
        Private Const SandboxPrefix As String = "RRT_RT_"

        ''' <summary>Deletes THIS run's RRT sandbox (and nothing else). Called
        ''' from the runner's Finally after every suite has finished.</summary>
        Friend Sub CleanupSandbox()
            If String.IsNullOrEmpty(_sandbox) Then Return
            If SandboxTryDelete(_sandbox, "current run sandbox") Then _sandbox = Nothing
        End Sub

        Friend Function SandboxTryDelete(path As String, label As String) As Boolean
            Try
                If Not Directory.Exists(path) Then Return True
                Directory.Delete(path, True)
                Return Not Directory.Exists(path)
            Catch ex As Exception
                SandboxCleanupWarnings.Add(label & " [" & path & "]: " & ex.Message)
                Return False
            End Try
        End Function

        ''' <summary>Reclaims RRT_RT_* sandboxes abandoned by earlier
        ''' hard-killed runs (LastWriteTime older than maxAgeHours). Returns
        ''' the number reclaimed; never throws.</summary>
        Private Sub SandboxSweepStale(maxAgeHours As Integer)
            Try
                Dim tempRoot As DirectoryInfo = New DirectoryInfo(Path.GetTempPath())
                Dim cutoff As DateTime = DateTime.Now.AddHours(-maxAgeHours)
                For Each d As DirectoryInfo In tempRoot.EnumerateDirectories(SandboxPrefix & "*")
                    Try
                        If d.LastWriteTime >= cutoff Then Continue For
                        SandboxTryDelete(d.FullName, "stale sandbox (" & d.Name & ")")
                    Catch
                    End Try
                Next
            Catch
            End Try
        End Sub

        ''' <summary>Runs sandbox\ffmpeg.exe -version; prints why if it cannot start.</summary>
        Private Function SandboxFfmpegWorks() As Boolean
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegExe,
                    .Arguments = "-version",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    If Not p.WaitForExit(10000) Then
                        Try : p.Kill() : Catch : End Try
                        Console.WriteLine("      FATAL: sandboxed ffmpeg.exe hung on -version")
                        Return False
                    End If
                    Dim firstLine As String = ""
                    Try : firstLine = outTask.Result.Split({ControlChars.Cr, ControlChars.Lf})(0) : Catch : End Try
                    If p.ExitCode = 0 Then
                        Console.WriteLine($"      sandbox ffmpeg OK: {firstLine}")
                        Return True
                    End If
                    Console.WriteLine($"      FATAL: sandboxed ffmpeg.exe -version exit={p.ExitCode}")
                    Return False
                End Using
            Catch ex As Exception
                Console.WriteLine($"      FATAL: cannot START sandboxed ffmpeg.exe: {ex.Message}")
                Return False
            End Try
        End Function

        ' ─── Shared media (generated once per run) ─────────────────────

        Private Function PrepareSharedMedia() As Boolean
            _videoH264 = Path.Combine(_sandbox, "video.h264")
            _videoMp4 = Path.Combine(_sandbox, "video.mp4")

            ' Raw H.264 @60fps for 5s — mirrors the NVENC output the session writes.
            ' Encoder fallback chain: the production Overlay\API-Core ffmpeg on
            ' Windows ships WITHOUT libx264 (NVIDIA-curated build) — the first
            ' Windows validation run skipped SYNC-RT for exactly this reason
            ' even though h264_nvenc is present. Software encoder first
            ' (hermetic), then hardware.
            ' ★ Windows-run fix: '-preset ultrafast' is x264-only syntax — NVENC
            ' rejects it ('Invalid argument') even on a real GTX 1080 Ti, which
            ' made the whole chain fail. Per-encoder presets now.
            Dim attempts() As (enc As String, preset As String) = {
                ("libx264", "ultrafast"),
                ("h264_nvenc", "p1"),
                ("h264_qsv", "veryfast"),
                ("h264_amf", "speed")
            }
            Dim genOk As Boolean = False
            For Each a In attempts
                Dim presetArg As String = If(String.IsNullOrEmpty(a.preset), "", $" -preset {a.preset}")
                genOk = RunFFmpeg(
                    $"-y -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=60:duration=5 " &
                    $"-c:v {a.enc}{presetArg} -pix_fmt yuv420p -f h264 """ & _videoH264 & """", 30000)
                If genOk AndAlso File.Exists(_videoH264) AndAlso New FileInfo(_videoH264).Length >= 1000 Then
                    Console.WriteLine($"      video encoder: {a.enc} (preset {a.preset})")
                    Exit For
                End If
                genOk = False
                Try : File.Delete(_videoH264) : Catch : End Try
            Next
            If Not genOk Then Return False

            ' Wrap — EXACT CaptureSession wrap arguments (display rate = 60 here).
            Dim wrapOk As Boolean = RunFFmpeg(
                $"-y -hide_banner -loglevel error -f h264 -r 60 -i ""{_videoH264}"" -c:v copy ""{_videoMp4}""", 30000)
            If Not wrapOk OrElse Not File.Exists(_videoMp4) Then
                Return False
            End If

            _videoDuration = ProbeDuration(_videoMp4)
            If _videoDuration < 4.5 OrElse _videoDuration > 5.5 Then
                Console.WriteLine($"      WARN wrapped video duration {_videoDuration:0.000}s (expected ≈5)")
            End If
            Console.WriteLine($"      shared video: 5s @60fps, wrapped duration {_videoDuration:0.000}s")
            Return True
        End Function

        ' ─── Scenario core ─────────────────────────────────────────────

        ''' <summary>
        ''' Run one full mux scenario with a simulated start relationship.
        ''' </summary>
        ''' <param name="timelineOffsetSec">
        ''' videoStart - audioStart in seconds (audio EARLY → positive; audio LATE → negative).
        ''' </param>
        ''' <param name="expectedToneAtSec">Where the tone must land in the final MP4.</param>
        Private Sub Scenario(timelineOffsetSec As Double, expectedToneAtSec As Double)
            Dim dir As String = Path.Combine(_sandbox, "s_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)

            ' 1. Sidecar WAV: 6.0s, tone at content 1.0–1.4s
            Dim wavPath As String = Path.Combine(dir, "sys.wav")
            Dim wavReport = WriteToneWav(wavPath, 6.0, 1.0, 0.4)
            TestRunner.Assert(wavReport.AccountingOk, "sidecar accounting: " & wavReport.ToString())
            TestRunner.AssertNear(ProbeDuration(wavPath), 6.0, 0.05, "wav duration")

            ' 2. Copy wrapped video into the scenario dir (per-scenario temp paths)
            Dim tempVideo As String = Path.Combine(dir, "video.tmp.mp4")
            File.Copy(_videoMp4, tempVideo)

            ' 3. SyncMath from simulated Stopwatch ticks (production path)
            Dim freq As Double = Stopwatch.Frequency
            Dim audioTicks As Long = 5_000_000_000L                     ' arbitrary positive origin
            Dim videoTicks As Long = CLng(audioTicks + timelineOffsetSec * freq)
            Dim offset As Double = SyncMath.ComputeAudioOffsetSec(videoTicks, audioTicks, freq)
            TestRunner.AssertNear(offset, timelineOffsetSec, 0.0005, "SyncMath offset")

            ' 4. Mux (production coordinator, real ffprobe + ffmpeg)
            Dim outPath As String = Path.Combine(dir, "final.mp4")
            Using mux As New MuxCoordinator()
                mux.FFmpegPath = _ffmpegExe
                mux.TempVideoPath = tempVideo
                mux.TempSystemWavPath = wavPath
                mux.OutputPath = outPath
                mux.HasSystemAudio = True
                mux.SystemVolume = 1.0F
                mux.SystemOffsetSec = offset

                Dim probed As Double = mux.ProbeVideoDuration()
                TestRunner.Assert(probed > 4.5, "ffprobe duration of wrapped video (got " & probed.ToString("0.000") & ")")
                mux.VideoDurationSec = probed

                Dim ok As Boolean = mux.Run()
                TestRunner.Assert(ok, "mux.Run exit code 0")

                ' 5. Output validation
                TestRunner.Assert(File.Exists(outPath), "final mp4 exists")
                Dim dur As Double = ProbeDuration(outPath)
                TestRunner.AssertNear(dur, probed, 0.25, "final duration ≈ video duration (-t clamp)")

                Dim types As List(Of String) = StreamTypes(outPath)
                TestRunner.Assert(types.Contains("video"), "video stream present")
                TestRunner.Assert(types.Contains("audio"), "audio stream present")

                ' 6. THE sync assertion — where did the tone actually land?
                Dim toneAt As Double = FirstNonSilenceSec(outPath)
                TestRunner.Assert(toneAt >= 0, "silencedetect found the tone")
                TestRunner.AssertNear(toneAt, expectedToneAtSec, 0.08,
                                      $"tone position (offset {offset:0.000}s)")
            End Using
        End Sub

        Private Sub Test_VideoOnly()
            Dim dir As String = Path.Combine(_sandbox, "vo_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)
            Dim tempVideo As String = Path.Combine(dir, "video.tmp.mp4")
            File.Copy(_videoMp4, tempVideo)
            Dim outPath As String = Path.Combine(dir, "final.mp4")

            Using mux As New MuxCoordinator()
                mux.FFmpegPath = _ffmpegExe
                mux.TempVideoPath = tempVideo
                mux.TempSystemWavPath = ""       ' no audio at all
                mux.OutputPath = outPath
                mux.HasSystemAudio = False
                mux.VideoDurationSec = mux.ProbeVideoDuration()
                Dim ok As Boolean = mux.Run()
                TestRunner.Assert(ok, "video-only mux exit 0")
                TestRunner.Assert(File.Exists(outPath), "output exists")
                Dim types As List(Of String) = StreamTypes(outPath)
                TestRunner.Assert(types.Contains("video"), "video stream present")
                TestRunner.Assert(Not types.Contains("audio"), "no audio stream in video-only mode")
            End Using
        End Sub

        Private Sub Test_Cleanup()
            Dim dir As String = Path.Combine(_sandbox, "cl_" & Guid.NewGuid().ToString("N").Substring(0, 6))
            Directory.CreateDirectory(dir)
            Dim tempVideo As String = Path.Combine(dir, "video.tmp.mp4")
            Dim wavPath As String = Path.Combine(dir, "sys.wav")
            File.Copy(_videoMp4, tempVideo)
            WriteToneWav(wavPath, 1.0, 0.0, 0.5)
            Dim outPath As String = Path.Combine(dir, "final.mp4")

            Dim mux As New MuxCoordinator() With {
                .FFmpegPath = _ffmpegExe,
                .TempVideoPath = tempVideo,
                .TempSystemWavPath = wavPath,
                .OutputPath = outPath,
                .HasSystemAudio = True,
                .VideoDurationSec = _videoDuration
            }
            TestRunner.Assert(mux.Run(), "mux ok")
            mux.CleanupTempFiles()
            TestRunner.Assert(Not File.Exists(tempVideo), "temp video deleted")
            TestRunner.Assert(Not File.Exists(wavPath), "temp wav deleted")
            TestRunner.Assert(File.Exists(outPath), "final kept")
        End Sub

        ' ─── WAV generation through the real sidecar writer ────────────

        ''' <summary>6s-ish WAV with a 440Hz tone at [toneAt, toneAt+toneLen]s.</summary>
        Private Function WriteToneWav(path As String, totalSec As Double, toneAt As Double, toneLen As Double) As WavFinalizeReport
            Using w As New WavSidecarWriter(path, 2, 48000, 16)
                w.Start()
                Dim totalChunks As Integer = CInt(totalSec * 100)      ' 10ms chunks
                Dim toneStartChunk As Integer = CInt(toneAt * 100)
                Dim toneEndChunk As Integer = CInt((toneAt + toneLen) * 100)
                For i As Integer = 0 To totalChunks - 1
                    Dim chunk As Byte()
                    If i >= toneStartChunk AndAlso i < toneEndChunk Then
                        chunk = WavSidecarTests.ToneChunk(480, 0.6)
                    Else
                        chunk = New Byte(480 * 4 - 1) {}               ' silence
                    End If
                    w.EnqueueChunk(chunk, chunk.Length)
                    ' Pace like a real WASAPI producer. A no-sleep tight loop
                    ' floods the bounded queue faster than any writer can drain
                    ' (Release-mode enqueue hit exactly the 240-chunk cap →
                    ' 2.4s WAV). 1 ms/chunk = 10× real-time — well inside the
                    ' sidecar's design target — and keeps the test under 1 s.
                    Thread.Sleep(1)
                Next
                Return w.Complete(5000)
            End Using
        End Function

        ' ─── Process helpers ───────────────────────────────────────────

        Private Function RunFFmpeg(args As String, timeoutMs As Integer) As Boolean
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegExe,
                    .Arguments = args,
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim errTask = p.StandardError.ReadToEndAsync()
                    Dim outTask = p.StandardOutput.ReadToEndAsync()
                    If Not p.WaitForExit(timeoutMs) Then
                        Try : p.Kill() : Catch : End Try
                        Return False
                    End If
                    errTask.Wait(2000) : outTask.Wait(2000)
                    Return p.ExitCode = 0
                End Using
            Catch
                Return False
            End Try
        End Function

        Private Function ProbeDuration(mediaPath As String) As Double
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = Path.Combine(_sandbox, "ffprobe.exe"),
                    .Arguments = "-v error -show_entries format=duration -of csv=p=0 """ & mediaPath & """",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim out As String = p.StandardOutput.ReadToEnd().Trim()
                    p.WaitForExit(5000)
                    Dim d As Double
                    If Double.TryParse(out, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                        Return d
                    End If
                End Using
            Catch
            End Try
            Return -1.0
        End Function

        Private Function StreamTypes(mediaPath As String) As List(Of String)
            Dim result As New List(Of String)()
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = Path.Combine(_sandbox, "ffprobe.exe"),
                    .Arguments = "-v error -show_entries stream=codec_type -of csv=p=0 """ & mediaPath & """",
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim out As String = p.StandardOutput.ReadToEnd()
                    p.WaitForExit(5000)
                    For Each line As String In out.Split(New Char() {ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                        Dim t As String = line.Trim()
                        If t.Length > 0 AndAlso Not result.Contains(t) Then result.Add(t)
                    Next
                End Using
            Catch
            End Try
            Return result
        End Function

        ''' <summary>
        ''' First silence_end reported by silencedetect = when the tone starts.
        ''' Returns -1 when no non-silent audio was found.
        ''' </summary>
        Private Function FirstNonSilenceSec(mediaPath As String) As Double
            Try
                Dim psi As New ProcessStartInfo With {
                    .FileName = _ffmpegExe,
                    .Arguments = $"-hide_banner -i ""{mediaPath}"" -af silencedetect=noise=-35dB:d=0.05 -f null -",
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .RedirectStandardOutput = True,
                    .CreateNoWindow = True
                }
                Using p As Process = Process.Start(psi)
                    Dim err As String = p.StandardError.ReadToEnd()
                    p.WaitForExit(30000)
                    ' [silencedetect @ 0x..] silence_end: 1.5023 | silence_duration: 1.4523
                    Dim idx As Integer = err.IndexOf("silence_end:")
                    If idx < 0 Then Return -1.0
                    Dim rest As String = err.Substring(idx + "silence_end:".Length).Trim()
                    Dim parts As String() = rest.Split(New Char() {" "c, "|"c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                    Dim d As Double
                    If parts.Length > 0 AndAlso Double.TryParse(parts(0), NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                        Return d
                    End If
                End Using
            Catch
            End Try
            Return -1.0
        End Function

    End Module

End Namespace
