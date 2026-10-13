Option Strict On
Option Explicit On
Option Infer On

' Program.vb — Phase 12b Production Validation Driver (Windows)
'
' Exercises the REAL production stack — RecordingEngine + CaptureSession with
' D3D11/ddagrab capture, native NVENC, WASAPI sidecar, H.264 wrap, and the
' MuxCoordinator — across the same lifecycle matrix that failed in Phase 11:
'
'   Test A — Normal:      3 × 10s sessions, back-to-back
'   Test B — Early stop:  1s / 5s / 10s sessions (stop-path stress)
'   Test C — Restart:     5 × 3s immediate restarts (resource reuse)
'
' Per session it validates the Phase 12b Definition of Done:
'   ✔ valid MP4 with video AND audio streams
'   ✔ WAV sidecar accounting (enqueued = written + dropped)
'   ✔ A/V sync offset evidence recorded
'   ✔ no orphan ffmpeg after the session completes
'   ✔ engine returns to Idle (backends reusable, no re-init)
'
' Usage:
'   dotnet run -c Release -- --ffmpeg "C:\...\Overlay\API-Core\ffmpeg.exe"
'   dotnet run -c Release -- --ffmpeg <path> --quick     (2 sessions only)
'   dotnet run -c Release -- --ffmpeg <path> --single 15 (one 15s session)
'
' Writes evidence to: evidence\phase-12b-validation-<timestamp>.md
'
' IMPORTANT: Play audio on your system during each session!

Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Recording

Module Program

    Private _evidence As New StringBuilder()
    Private _passCount As Integer = 0
    Private _failCount As Integer = 0
    Private Const AvDurationToleranceSec As Double = 0.200

    Public Function Main(args As String()) As Integer
        Console.OutputEncoding = System.Text.Encoding.UTF8

        Dim ffmpegPath As String = "ffmpeg"
        Dim quick As Boolean = False
        Dim singleSec As Integer = 0
        ' PHASE 1 VIDEO VALIDATION (--videocheck):
        '   --videocheck           run the real-record validation driver
        '   --config <engine.json> explicit config path (empty = default chain)
        '   --out <mp4>            output file for the validation recording
        '   --seconds N            session duration (default 8)
        Dim videoCheck As Boolean = False
        Dim configArg As String = ""
        Dim outArg As String = "videocheck.mp4"
        Dim checkSec As Integer = 8
        Dim mismatchSessionFps As Integer = 0
        For i As Integer = 0 To args.Length - 2
            If args(i) = "--ffmpeg" Then ffmpegPath = args(i + 1)
            If args(i) = "--quick" Then quick = True
            If args(i) = "--single" Then Integer.TryParse(args(i + 1), singleSec)
            If args(i) = "--videocheck" Then videoCheck = True
            If args(i) = "--config" Then configArg = args(i + 1)
            If args(i) = "--out" Then outArg = args(i + 1)
            If args(i) = "--seconds" Then Integer.TryParse(args(i + 1), checkSec)
            If args(i) = "--mismatch-fps" Then Integer.TryParse(args(i + 1), mismatchSessionFps)
        Next

        If ffmpegPath = "ffmpeg" Then
            Dim candidates As String() = {
                Path.Combine(AppContext.BaseDirectory, "API-Core", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "API-Core", "ffmpeg.exe"),
                "C:\My Project\NVIDIA-Shadowplay\Overlay\API-Core\ffmpeg.exe"
            }
            For Each c In candidates
                If File.Exists(c) Then ffmpegPath = c : Exit For
            Next
        End If

        ' ── PHASE 1 VIDEO RUNTIME VALIDATION driver (canonical config chain) ──
        If videoCheck Then
            Return RunVideoCheck(configArg, outArg, checkSec, ffmpegPath, mismatchSessionFps)
        End If

        Console.WriteLine("============================================================")
        Console.WriteLine(" Phase 12b — Production Validation (RecordingEngine matrix)")
        Console.WriteLine("============================================================")
        Console.WriteLine($"  FFmpeg:  {ffmpegPath}")
        Console.WriteLine($"  Orphan baseline: {CountFFmpeg()} ffmpeg processes")
        Console.WriteLine()
        Console.WriteLine(">>> IMPORTANT: PLAY AUDIO on your system during each session!")
        Console.WriteLine()

        If Not File.Exists(ffmpegPath) AndAlso ffmpegPath = "ffmpeg" Then
            Console.Error.WriteLine("*** FATAL: ffmpeg not found — pass --ffmpeg <path>")
            Return 2
        End If

        Ev("# Phase 12b Production Validation Evidence")
        Ev("")
        Ev($"**Date:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        Ev($"**Machine:** {Environment.MachineName}  ·  **OS:** {Environment.OSVersion}")
        Ev($"**FFmpeg:** `{ffmpegPath}`")
        Ev("")

        Dim baselineOrphans As Integer = CountFFmpeg()
        Dim logger As New EngineLogger("Validate", EngineLogger.LogLevel.Info, AddressOf Console.WriteLine)
        Dim overallOk As Boolean = True

        Console.WriteLine(">>> Creating RecordingEngine (persistent D3D11 + DXGI + NVENC)...")
        Dim engine As New RecordingEngine(logger)

        Try
            Dim swTotal As Stopwatch = Stopwatch.StartNew()
            Dim memBefore As Long = Environment.WorkingSet
            engine.Initialize()
            Ev($"- Engine.Initialize: OK (working set {memBefore / 1048576L:F0} → {Environment.WorkingSet / 1048576L:F0} MB)")

            If singleSec > 0 Then
                overallOk = RunSession(engine, "Single", singleSec, ffmpegPath, baselineOrphans, verbose:=True)
            ElseIf quick Then
                overallOk = RunSession(engine, "Quick1", 10, ffmpegPath, baselineOrphans, verbose:=True)
                overallOk = RunSession(engine, "Quick2", 10, ffmpegPath, baselineOrphans, verbose:=True) AndAlso overallOk
            Else
                ' Test A — Normal
                Dim aOk As Boolean = True
                For i As Integer = 1 To 3
                    aOk = RunSession(engine, $"A{i}", 10, ffmpegPath, baselineOrphans, verbose:=True) AndAlso aOk
                Next
                Ev($"- **Test A (3 × 10s normal): {If(aOk, "PASS", "FAIL")}**")
                overallOk = overallOk AndAlso aOk

                ' Test B — Early stop
                Dim bOk As Boolean = True
                For Each sec As Integer In New Integer() {1, 5, 10}
                    bOk = RunSession(engine, $"B{sec}s", sec, ffmpegPath, baselineOrphans, verbose:=True) AndAlso bOk
                Next
                Ev($"- **Test B (early stop 1/5/10s): {If(bOk, "PASS", "FAIL")}**")
                overallOk = overallOk AndAlso bOk

                ' Test C — Immediate restart
                Dim cOk As Boolean = True
                For i As Integer = 1 To 5
                    cOk = RunSession(engine, $"C{i}", 3, ffmpegPath, baselineOrphans, verbose:=True) AndAlso cOk
                Next
                Ev($"- **Test C (5 × 3s immediate restart): {If(cOk, "PASS", "FAIL")}**")
                overallOk = overallOk AndAlso cOk

                Dim memAfter As Long = Environment.WorkingSet
                Ev($"- Memory: {memBefore / 1048576L:F0} MB → {memAfter / 1048576L:F0} MB across the full matrix")
                Ev($"- Matrix wall time: {swTotal.Elapsed.TotalMinutes:F1} min")
            End If

        Catch ex As Exception
            Console.Error.WriteLine($"*** FATAL: {ex.Message}")
            Console.Error.WriteLine(ex.StackTrace)
            Ev($"- **FATAL: {ex.Message}**")
            overallOk = False
        Finally
            engine.Dispose()
            Ev("- Engine.Dispose: OK")
        End Try

        ' Final orphan check — must equal baseline after engine dispose
        Dim finalOrphans As Integer = CountFFmpeg()
        Dim orphanOk As Boolean = (finalOrphans = baselineOrphans)
        Ev($"- Orphan ffmpeg check: baseline {baselineOrphans} → final {finalOrphans} = {If(orphanOk, "PASS", "FAIL")}")
        overallOk = overallOk AndAlso orphanOk

        ' ─── Verdict ────────────────────────────────────────────────
        Console.WriteLine()
        Console.WriteLine("============================================================")
        Console.WriteLine($" PHASE 12B VERDICT: {If(overallOk, "PASS", "FAIL")}  ({_passCount} sessions pass / {_failCount} fail)")
        Console.WriteLine("============================================================")

        Ev("")
        Ev($"## Overall: {If(overallOk, "PASS ✅", "FAIL ❌")}")
        Ev("")
        Ev($"Sessions passed: {_passCount} · failed: {_failCount}")

        Try
            Directory.CreateDirectory("evidence")
            Dim evPath As String = System.IO.Path.Combine("evidence",
                $"phase-12b-validation-{DateTime.Now:yyyyMMdd-HHmmss}.md")
            File.WriteAllText(evPath, _evidence.ToString())
            Console.WriteLine($" Evidence written: {System.IO.Path.GetFullPath(evPath)}")
        Catch ex As Exception
            Console.WriteLine($" Evidence write failed: {ex.Message}")
        End Try

        Return If(overallOk, 0, 1)
    End Function

    ' ─── One session + full DoD validation ───────────────────────────

    Private Function RunSession(engine As RecordingEngine,
                                label As String,
                                durationSec As Integer,
                                ffmpegPath As String,
                                baselineOrphans As Integer,
                                verbose As Boolean) As Boolean
        Console.WriteLine()
        Console.WriteLine($">>> [{label}] {durationSec}s session → {label}.mp4")
        Console.WriteLine("    >>> PLAY AUDIO NOW! <<<")
        Console.WriteLine()

        Dim outPath As String = $"{label}.mp4"
        Dim config As New SessionConfig() With {
            .OutputPath = outPath,
            .DurationSeconds = durationSec,
            .FFmpegPath = ffmpegPath,
            .AudioEnabled = True,
            .SystemVolume = 1.0F
        }

        Dim r As SessionResult = engine.StartSession(config)
        PrintSessionResult(label, r)

        ' ── DoD assertions ──
        Dim ok As Boolean = r.Pass
        Dim notes As New List(Of String)()

        If Not r.Pass Then notes.Add("session result FAIL")
        ' A/V duration acceptance: same ~200ms tolerance documented by scripts/diag-recording.ps1.
        Dim videoDur As Double = ProbeStreamDuration(ffmpegPath, outPath, "v:0")
        Dim audioDur As Double = ProbeStreamDuration(ffmpegPath, outPath, "a:0")
        If videoDur > 0 AndAlso audioDur > 0 Then
            Dim avDelta As Double = Math.Abs(videoDur - audioDur)
            Ev($"| A/V stream duration (video / audio) | {videoDur:0.000}s / {audioDur:0.000}s |")
            Ev($"| A/V duration delta | {avDelta * 1000.0:0}ms (limit {AvDurationToleranceSec * 1000.0:0}ms) |")
            If avDelta > AvDurationToleranceSec Then
                ok = False
                notes.Add($"A/V duration delta {avDelta * 1000.0:0}ms exceeds {AvDurationToleranceSec * 1000.0:0}ms")
            End If
        Else
            ok = False
            notes.Add("could not probe both video/audio stream durations")
        End If

        If Not r.AudioAccountingOk Then
            ok = False
            notes.Add($"audio accounting residual (dropped {r.AudioDroppedBytes:N0}B)")
        ElseIf r.AudioDroppedBytes > 0 Then
            notes.Add($"WARN dropped {r.AudioDroppedBytes:N0}B under backpressure")
        End If

        Dim orphans As Integer = CountFFmpeg()
        If orphans > baselineOrphans Then
            ok = False
            notes.Add($"ORPHAN ffmpeg: {orphans} > baseline {baselineOrphans}")
        End If

        Dim status As EngineStatus = engine.GetStatus()
        If status.State <> RecordingEngineState.Idle Then
            ok = False
            notes.Add($"engine not Idle after session: {status.State}")
        End If

        ' Leftover temp files?
        Dim tmpH264 As String = Path.ChangeExtension(outPath, ".tmp.h264")
        Dim tmpVideo As String = Path.ChangeExtension(outPath, ".tmp.video.mp4")
        Dim tmpWav As String = Path.ChangeExtension(outPath, ".tmp.wav")
        Dim leftovers As New List(Of String)()
        For Each t As String In {tmpH264, tmpVideo, tmpWav}
            If File.Exists(t) Then leftovers.Add(Path.GetFileName(t))
        Next
        If leftovers.Count > 0 Then notes.Add("WARN leftover temp: " & String.Join(", ", leftovers))

        If ok Then _passCount += 1 Else _failCount += 1

        Ev($"### [{label}] {durationSec}s — {If(ok, "PASS ✅", "FAIL ❌")}")
        Ev("")
        Ev($"| metric | value |")
        Ev($"|---|---|")
        Ev($"| duration (actual / mux) | {r.ActualDurationSec:0.00}s / {r.MuxVideoDurationSec:0.00}s |")
        Ev($"| frames captured / encoded | {r.FramesCaptured:N0} / {r.FramesEncoded:N0} |")
        Ev($"| nvenc errors | {r.NvencErrors:N0} |")
        Ev($"| audio bytes (written / dropped) | {r.AudioBytes:N0} / {r.AudioDroppedBytes:N0} |")
        Ev($"| audio accounting ok | {r.AudioAccountingOk} |")
        Ev($"| A/V sync offset | {r.SystemOffsetSec:0.000}s |")
        Ev($"| streams (video / audio) | {r.VideoStreamFound} / {r.AudioStreamFound} |")
        Ev($"| file size | {r.FileSize:N0} B |")
        Ev($"| orphan ffmpeg (baseline {baselineOrphans}) | {orphans} |")
        Ev($"| engine state after | {status.State} |")
        If notes.Count > 0 Then
            Ev("")
            For Each n As String In notes
                Ev($"- note: {n}")
            Next
        End If
        Ev("")

        If verbose Then
            Console.WriteLine($"    → [{label}] {If(ok, "PASS", "FAIL")} {If(notes.Count > 0, "(" & String.Join("; ", notes) & ")", "")}")
        End If

        Return ok
    End Function

    Private Function ProbeStreamDuration(ffmpegPath As String, mp4Path As String, streamSelector As String) As Double
        Try
            Dim ffprobe As String = Path.Combine(Path.GetDirectoryName(ffmpegPath), "ffprobe.exe")
            If Not File.Exists(ffprobe) Then ffprobe = "ffprobe"
            Dim psi As New ProcessStartInfo With {
                .FileName = ffprobe,
                .Arguments = $"-v error -select_streams {streamSelector} -show_entries stream=duration -of csv=p=0 ""{mp4Path}""",
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            Using proc As Process = Process.Start(psi)
                Dim output As String = proc.StandardOutput.ReadToEnd().Trim()
                proc.WaitForExit(5000)
                Dim value As Double
                If Double.TryParse(output, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, value) Then Return value
            End Using
        Catch
        End Try
        Return 0.0
    End Function

    Private Function CountFFmpeg() As Integer
        Try
            Return Process.GetProcessesByName("ffmpeg").Length
        Catch
            Return 0
        End Try
    End Function

    Private Sub PrintSessionResult(label As String, r As SessionResult)
        Console.WriteLine($"────────────────────────────────────────────────────────────")
        Console.WriteLine($" {label} — Result")
        Console.WriteLine($"────────────────────────────────────────────────────────────")
        Console.WriteLine($"  output:               {r.OutputPath}")
        Console.WriteLine($"  duration:              {r.ActualDurationSec:F2}s (mux {r.MuxVideoDurationSec:F2}s, target {r.RequestedDurationSec}s)")
        Console.WriteLine($"  frames_captured:      {r.FramesCaptured}")
        Console.WriteLine($"  frames_encoded:       {r.FramesEncoded}")
        Console.WriteLine($"  frames_duplicated:    {r.FramesDuplicated}   (CFR pacing: static-screen ticks re-encoded)")
        Console.WriteLine($"  nvenc_errors:          {r.NvencErrors}")
        Console.WriteLine($"  video_bytes:          {r.TotalVideoBytes:N0}")
        Console.WriteLine($"  audio_bytes:          {r.AudioBytes:N0} (dropped {r.AudioDroppedBytes:N0})")
        Console.WriteLine($"  audio_accounting:     {r.AudioAccountingOk}")
        Console.WriteLine($"  sync_offset:          {r.SystemOffsetSec:F3}s")
        Console.WriteLine($"  video_stream:         {If(r.VideoStreamFound, "FOUND", "MISSING")}")
        Console.WriteLine($"  audio_stream:         {If(r.AudioStreamFound, "FOUND", "MISSING")}")
        Console.WriteLine($"  file_size:            {r.FileSize:N0} bytes ({r.FileSize / 1024.0 / 1024.0:F2} MB)")
        If Not String.IsNullOrEmpty(r.ErrorMessage) Then
            Console.WriteLine($"  error:                {r.ErrorMessage}")
        End If
        Console.WriteLine($"  pass:                  {r.Pass}")
        Console.WriteLine($"────────────────────────────────────────────────────────────")
    End Sub

    ' ═══ PHASE 1 VIDEO RUNTIME VALIDATION (--videocheck) ═══
    '
    ' Drives the CANONICAL config chain exactly as Engine.ConfigTruth.Tests
    ' does and exactly as RecordingEngineHost does on Windows:
    '
    '   NextRecordingConfig.LoadEffectiveSettings(configPath)
    '     → NextRecordingConfig.MapStartupConfig(settings)
    '     → RecordingEngine.Initialize(startup)
    '     → NextRecordingConfig.BuildSessionConfig(out, ffmpeg, configPath)
    '     → RecordingEngine.StartSession(config)
    '
    ' The caller (scripts/windows-phase1-video-validation.ps1) writes the
    ' config variants and ffprobe-asserts the produced MP4. This driver
    ' only runs the REAL pipeline and echoes the effective startup values
    ' — no config invented here, no result faked.
    Private Function RunVideoCheck(configPath As String, outPath As String, seconds As Integer, ffmpegPath As String, mismatchSessionFps As Integer) As Integer
        Console.WriteLine("============================================================")
        Console.WriteLine(" PHASE 1 VIDEO — Windows real-record validation (--videocheck)")
        Console.WriteLine("============================================================")
        Console.WriteLine($"  config:  {If(String.IsNullOrWhiteSpace(configPath), "(default resolution chain)", configPath)}")
        Console.WriteLine($"  output:  {outPath}")
        Console.WriteLine($"  seconds: {seconds}")
        Console.WriteLine($"  FFmpeg:  {ffmpegPath}")
        Console.WriteLine()

        Dim cfg As String = If(String.IsNullOrWhiteSpace(configPath), Nothing, configPath)

        ' ★ W3 pin fix (SeparateTrack forensics): with an EXPLICIT --config
        ' dir, pin the process layout root to that dir's PARENT so
        ' OverlayConfig.ResolveConfigDir step 2 finds <root>\Config\config.json
        ' (when the caller provides one) INSTEAD of the sibling-Overlay-bin
        ' walk (step 3) which silently imports the OWNER'S deployed unified
        ' config over every variant this driver was asked to test. Without a
        ' unified file in the temp tree, IsAvailable=False → the legacy tier
        ' (video/audio.json) governs — both shapes now honor --config.
        If Not String.IsNullOrWhiteSpace(cfg) Then
            Dim configDir As String = IO.Path.GetDirectoryName(IO.Path.GetFullPath(cfg))
            If Not String.IsNullOrEmpty(configDir) Then
                Environment.SetEnvironmentVariable("NVIDIA_SHADOWPLAY_APP_ROOT",
                                                   IO.Path.GetDirectoryName(configDir))
                Console.WriteLine($"  layout:  NVIDIA_SHADOWPLAY_APP_ROOT pinned to {IO.Path.GetDirectoryName(configDir)} (--config isolation)")
            End If
        End If

        Dim baselineOrphans As Integer = CountFFmpeg()
        Dim logger As New EngineLogger("VideoCheck", EngineLogger.LogLevel.Info, AddressOf Console.WriteLine)
        Dim engine As New RecordingEngine(logger)

        Try
            ' ── canonical chain: effective settings → startup mapping ──
            Dim settings As CaptureSettings = NextRecordingConfig.LoadEffectiveSettings(cfg)
            Dim startup As EngineStartupConfig = NextRecordingConfig.MapStartupConfig(settings)

            ' Regression hook: deliberately initialize NVENC at 60fps and ask the
            ' subsequent session for another FPS. This exercises the production
            ' per-session rebuild path for the exact timing bug found in real clips.
            If mismatchSessionFps > 0 Then
                startup.Fps = 60
                Console.WriteLine($"  MISMATCH REGRESSION: startup NVENC fps=60, session fps={mismatchSessionFps}")
            End If

            Console.WriteLine("  effective startup (requested values from config):")
            Console.WriteLine($"    encoder='{startup.CodecKey}' fps={startup.Fps} bitrate={startup.BitrateBps} bps")
            Console.WriteLine($"    rc='{startup.RateControl}' preset='{startup.Preset}' gop={startup.GopSize}")
            Console.WriteLine($"    native={startup.UseNativeResolution} custom={startup.RequestedWidth}x{startup.RequestedHeight}")
            Console.WriteLine($"    capture='{If(Not String.IsNullOrEmpty(startup.RequestedCaptureMethod), startup.RequestedCaptureMethod, "(default ddagrab)")}' pixfmt='{If(Not String.IsNullOrEmpty(startup.RequestedPixelFormat), startup.RequestedPixelFormat, "(default)")}'")
            Console.WriteLine()

            ' ── persistent engine init (one-shot: codec/bitrate/fps/preset/GOP/resolution) ──
            engine.Initialize(startup)

            ' ── session config from a FRESH reload (CT-4 contract) → real record ──
            Dim sessionCfg As SessionConfig = NextRecordingConfig.BuildSessionConfig(outPath, ffmpegPath, Nothing, cfg)
            sessionCfg.DurationSeconds = seconds
            If mismatchSessionFps > 0 Then sessionCfg.TargetFps = mismatchSessionFps
            Dim r As SessionResult = engine.StartSession(sessionCfg)
            PrintSessionResult("VideoCheck", r)

            Dim orphans As Integer = CountFFmpeg()
            Dim orphanOk As Boolean = orphans <= baselineOrphans
            Console.WriteLine($"  orphan ffmpeg: baseline {baselineOrphans} → final {orphans} = {If(orphanOk, "OK", "FAIL")}")
            Return If(r.Pass AndAlso orphanOk, 0, 1)
        Catch ex As Exception
            Console.Error.WriteLine($"*** VIDEOCHECK FATAL: {ex.Message}")
            Console.Error.WriteLine(ex.StackTrace)
            Return 3
        Finally
            Try : engine.Dispose() : Catch : End Try
        End Try
    End Function

    Private Sub Ev(line As String)
        _evidence.AppendLine(line)
    End Sub

End Module
