' StressTestRunner.vb
' Engine-Audio — Stress Test Runner
'
' Validates the two-process architecture (ffmpeg #1 video, AudioFileWriter sidecar,
' ffmpeg #2 mux) against the invariants defined in ENGINE-REALTIME-ARCHITECTURE.md:
'
'   ── INVIOLATE ──
'   ffmpeg #1:        NO audio input, NO mux, NO amix, NO resample
'   Audio callback:   copy + enqueue only (no disk I/O, no heavy allocation)
'   JobObjectGuard:   AboveNormal priority only (NO High/Realtime)
'   Accounting:       BytesEnqueued = WrittenBytes + DroppedBytes + DroppedSilenceBytes
'                     → BytesAccountingResidual MUST be 0 after clean drain
'
'   ── RED FLAGS (auto-fail: invariant violations) ──
'   DroppedBytes > 0              → real audio data loss
'   BytesAccountingResidual ≠ 0  → writer didn't drain OR counting bug
'   Writer FAILED                → disk write exception didn't propagate
'   ExpectAudioData but 0 bytes  → audio capture never started
'
'   ── HARD PERFORMANCE FAILURES (auto-fail: threshold exceeded) ──
'   FPS < (target × 0.95)         → video capture degraded
'   Speed < 0.95x                → FFmpeg can't keep up real-time
'   dup > 5% of expected frames   → ddagrab starved (video source issue)
'   drop > 5                     → FFmpeg dropping frames
'   WriteLagBytes > 0            → writer thread behind at stop time
'   AVSyncMs > 100ms             → final MP4 audio/video duration mismatch
'                                  (measured via ffprobe on final output, NOT WAV size)

Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>
''' Drives CaptureEngine through a matrix of stress test scenarios and validates
''' the architecture invariants by parsing FFmpeg stats + AudioFileWriter diagnostics.
'''
''' CRITICAL: A/V sync is measured from the FINAL MP4 via ffprobe, NOT from WAV file
''' size. WAV size includes initial silence prefill + real audio + apad padding,
''' so dividing by hardcoded bytes/sec would give wrong measurements and trigger
''' false A/V sync failures.
''' </summary>
Public Class StressTestRunner

    Public Class TestScenario
        Public Property Name As String
        Public Property Description As String
        Public Property Settings As CaptureSettings
        Public Property DurationSec As Integer = 10
        Public Property ExpectAudio As Boolean = True
        Public Property ExpectMic As Boolean = False
        ' ── Per GPT P1 #5: separate "audio captured data" from "audio enabled" ──
        ' ExpectAudioData=True: scenario expects non-zero audio bytes (most audio tests)
        ' AllowSilentCapture=True: scenario permits 0 audio callbacks (silence scenarios)
        ' If ExpectAudioData=True AND AllowSilentCapture=False AND SysBytes=0 → FAIL
        Public Property ExpectAudioData As Boolean = True
        Public Property AllowSilentCapture As Boolean = False
        ' ── Per GPT P1 #4: silence scenarios require user environment cooperation ──
        ' RequiresSilentSystemAudio=True: test cannot guarantee silence from runner side.
        ' Runner should warn user to mute YouTube/notifications before running.
        Public Property RequiresSilentSystemAudio As Boolean = False
        ' ── Per GPT P0 #2: scenario 06 runs 10 cycles, not 1 ──
        Public Property CycleCount As Integer = 1
    End Class

    Public Class TestResult
        Public Property Name As String
        Public Property Pass As Boolean
        Public Property FailureReason As String = ""

        ' ── Video metrics (parsed from FFmpeg Lsize= line) ──
        Public Property TargetFps As Integer
        Public Property ActualFps As Double
        Public Property Speed As Double
        Public Property Dup As Long
        Public Property Drop As Long
        Public Property VideoDurationSec As Double

        ' ── Audio metrics (parsed from GetDiagnostics) ──
        Public Property SysCallbacks As Long
        Public Property SysBytesEnqueued As Long
        Public Property SysWrittenBytes As Long
        Public Property SysWriteLagBytes As Long
        Public Property SysDroppedChunks As Long
        Public Property SysDroppedBytes As Long
        Public Property SysDroppedSilenceBytes As Long
        Public Property SysBytesAccountingResidual As Long
        Public Property SysInitialSilenceSec As Double

        Public Property MicCallbacks As Long
        Public Property MicBytesEnqueued As Long
        Public Property MicWrittenBytes As Long
        Public Property MicWriteLagBytes As Long
        Public Property MicDroppedBytes As Long
        Public Property MicBytesAccountingResidual As Long

        ' ── Mux metrics ──
        Public Property SysOffsetSec As Double
        Public Property MicOffsetSec As Double
        Public Property MuxExitCode As Integer
        Public Property MuxDurationMs As Double

        ' ── A/V sync (measured via ffprobe on final MP4, per GPT P0 #1) ──
        ' FinalVideoDurationSec / FinalAudioDurationSec from ffprobe on the output file.
        ' AVSyncMeasured=True only if ffprobe successfully queried both streams.
        ' If False, validation FAILS the scenario (per GPT P0 #2) — measurement
        ' unavailable ≠ measurement OK.
        '
        ' Per GPT P1 #3: separate StartOffsetMs and EndOffsetMs:
        '   StartOffsetMs = |audio_start_time - video_start_time| × 1000
        '     (audio may start late due to adelay for negative offset, or early
        '      if audio was captured before video and -ss skipped leading silence)
        '   EndOffsetMs = |video_duration - (audio_start + audio_duration)| × 1000
        '     (audio may end early/late vs video)
        '   AVSyncMs = Max(StartOffsetMs, EndOffsetMs) — worst-case
        Public Property FinalVideoDurationSec As Double
        Public Property FinalAudioDurationSec As Double
        Public Property FinalAudioStartTimeSec As Double
        Public Property AVSyncMeasured As Boolean = False
        Public Property StartOffsetMs As Double
        Public Property EndOffsetMs As Double
        Public Property AVSyncMs As Double  ' Max(StartOffsetMs, EndOffsetMs)

        ' ── Multi-cycle support (scenario 06 spam test) ──
        Public Property CycleCount As Integer = 1
        Public Property CycleResults As New List(Of TestResult)()

        Public Property OutputFile As String

        Public Overrides Function ToString() As String
            Dim status As String = If(Pass, "PASS", "FAIL")
            Dim cycleStr As String = If(CycleCount > 1, $" ×{CycleCount}", "")
            Return $"[{status}] {Name}{cycleStr}: FPS={ActualFps:F1}/{TargetFps}, dup={Dup}, drop={Drop}, " &
                   $"DroppedBytes={SysDroppedBytes}, Residual={SysBytesAccountingResidual}, " &
                   $"Sync={AVSyncMs:F0}ms"
        End Function
    End Class


    Private _engine As CaptureEngine
    Private _outputDir As String

    Public Sub New(engine As CaptureEngine, outputDir As String)
        _engine = engine
        _outputDir = outputDir
        If Not Directory.Exists(_outputDir) Then
            Directory.CreateDirectory(_outputDir)
        End If
    End Sub

    ''' <summary>
    ''' Build the default test matrix (10 scenarios from the user's spec).
    ''' Uses baseSettings passed from UI (with real FFmpegPath, Encoder, etc.).
    ''' </summary>
    Public Function BuildDefaultMatrix(baseSettings As CaptureSettings) As List(Of TestScenario)
        Dim scenarios As New List(Of TestScenario)()

        ' Use the settings passed from UI (already has FFmpegPath/Encoder/MicDeviceId)
        baseSettings.OutputDirectory = _outputDir

        ' ── 1. Video only ──
        Dim s1 As CaptureSettings = CloneSettings(baseSettings)
        s1.SystemAudioCapture = False
        s1.MicCapture = False
        s1.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "01_video_only_60fps",
            .Description = "Video only, 60 FPS, no audio",
            .Settings = s1,
            .DurationSec = 10,
            .ExpectAudio = False,
            .ExpectAudioData = False,
            .AllowSilentCapture = True
        })

        ' ── 2. System audio only ──
        Dim s2 As CaptureSettings = CloneSettings(baseSettings)
        s2.SystemAudioCapture = True
        s2.MicCapture = False
        s2.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "02_system_audio_60fps",
            .Description = "System audio loopback, 60 FPS — user should play audio during test",
            .Settings = s2,
            .DurationSec = 10,
            .ExpectAudio = True,
            .ExpectMic = False,
            .ExpectAudioData = True,
            .AllowSilentCapture = False
        })

        ' ── 3. Mic only ──
        Dim s3 As CaptureSettings = CloneSettings(baseSettings)
        s3.SystemAudioCapture = False
        s3.MicCapture = True
        s3.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "03_mic_only_60fps",
            .Description = "Mic input only, 60 FPS — user should talk into mic during test",
            .Settings = s3,
            .DurationSec = 10,
            .ExpectAudio = True,
            .ExpectMic = True,
            .ExpectAudioData = True,
            .AllowSilentCapture = False
        })

        ' ── 4. System + Mic ──
        Dim s4 As CaptureSettings = CloneSettings(baseSettings)
        s4.SystemAudioCapture = True
        s4.MicCapture = True
        s4.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SingleTrack
        s4.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "04_system_plus_mic_60fps",
            .Description = "System + mic mixed into single track",
            .Settings = s4,
            .DurationSec = 10,
            .ExpectAudio = True,
            .ExpectMic = True,
            .ExpectAudioData = True,
            .AllowSilentCapture = False
        })

        ' ── 5. System + Mic + 144 FPS ──
        Dim s5 As CaptureSettings = CloneSettings(baseSettings)
        s5.SystemAudioCapture = True
        s5.MicCapture = True
        s5.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SingleTrack
        s5.FPS = 144
        s5.Bitrate = 17000000
        scenarios.Add(New TestScenario With {
            .Name = "05_system_plus_mic_144fps",
            .Description = "Stress: 144 FPS + system + mic (video path must stay single-input)",
            .Settings = s5,
            .DurationSec = 15,
            .ExpectAudio = True,
            .ExpectMic = True,
            .ExpectAudioData = True,
            .AllowSilentCapture = False
        })

        ' ── 6. Start/Stop spam (10 ACTUAL rapid cycles, per GPT P0 #2) ──
        Dim s6 As CaptureSettings = CloneSettings(baseSettings)
        s6.SystemAudioCapture = True
        s6.MicCapture = False
        s6.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "06_start_stop_spam",
            .Description = "10 rapid start/stop cycles, 2s each — tests lifecycle state machine",
            .Settings = s6,
            .DurationSec = 2,
            .ExpectAudio = True,
            .ExpectAudioData = False,  ' silence acceptable (no audio source guaranteed)
            .AllowSilentCapture = True,
            .CycleCount = 10  ' ACTUAL 10 cycles, not just 1
        })

        ' ── 7. Long continuous (60s) ──
        Dim s7 As CaptureSettings = CloneSettings(baseSettings)
        s7.SystemAudioCapture = True
        s7.MicCapture = True
        s7.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SingleTrack
        s7.FPS = 144
        s7.Bitrate = 17000000
        scenarios.Add(New TestScenario With {
            .Name = "07_long_60s_144fps",
            .Description = "60s continuous, 144 FPS — tests queue growth + writer thread stamina",
            .Settings = s7,
            .DurationSec = 60,
            .ExpectAudio = True,
            .ExpectMic = True,
            .ExpectAudioData = False,  ' 60s might have natural silence periods
            .AllowSilentCapture = True
        })

        ' ── 8. Separate tracks (MKV-ready test, MP4 output) ──
        Dim s8 As CaptureSettings = CloneSettings(baseSettings)
        s8.SystemAudioCapture = True
        s8.MicCapture = True
        s8.AudioTrackMode = CaptureSettings.AudioTrackModeEnum.SeparateTrack
        s8.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "08_separate_tracks",
            .Description = "Two separate audio tracks (system + mic) in MP4",
            .Settings = s8,
            .DurationSec = 10,
            .ExpectAudio = True,
            .ExpectMic = True,
            .ExpectAudioData = True,
            .AllowSilentCapture = False
        })

        ' ── 9. System silence (no audio playing) ──
        ' Per GPT P1 #4: requires user environment cooperation.
        ' Runner will warn that user must mute YouTube/notifications before running.
        Dim s9 As CaptureSettings = CloneSettings(baseSettings)
        s9.SystemAudioCapture = True
        s9.MicCapture = False
        s9.FPS = 60
        scenarios.Add(New TestScenario With {
            .Name = "09_silence_full_clip",
            .Description = "No audio playing entire clip — tests initial silence prefill + apad. " &
                           "USER MUST mute YouTube/notifications before running!",
            .Settings = s9,
            .DurationSec = 15,
            .ExpectAudio = True,
            .ExpectAudioData = False,  ' silence expected → 0 callbacks OK
            .AllowSilentCapture = True,
            .RequiresSilentSystemAudio = True
        })

        ' ── 10. 144 FPS video only (baseline) ──
        Dim s10 As CaptureSettings = CloneSettings(baseSettings)
        s10.SystemAudioCapture = False
        s10.MicCapture = False
        s10.FPS = 144
        s10.Bitrate = 17000000
        scenarios.Add(New TestScenario With {
            .Name = "10_video_only_144fps_baseline",
            .Description = "Video only 144 FPS — establishes baseline (should match Engine-Stable)",
            .Settings = s10,
            .DurationSec = 15,
            .ExpectAudio = False,
            .ExpectAudioData = False,
            .AllowSilentCapture = True
        })

        Return scenarios
    End Function

    Private Function CloneSettings(src As CaptureSettings) As CaptureSettings
        ' Create a new instance with copied fields (CaptureSettings doesn't implement ICloneable)
        Dim clone As New CaptureSettings()
        clone.UseNativeResolution = src.UseNativeResolution
        clone.Encoder = src.Encoder
        clone.FPS = src.FPS
        clone.Bitrate = src.Bitrate
        clone.CaptureMethod = src.CaptureMethod
        clone.OutputDirectory = src.OutputDirectory
        clone.AudioCapture = False  ' legacy dshow, never used in two-process
        clone.AudioDevice = ""
        clone.SystemAudioCapture = src.SystemAudioCapture
        clone.MicCapture = src.MicCapture
        clone.SystemAudioVolume = src.SystemAudioVolume
        clone.MicVolume = src.MicVolume
        clone.MicDeviceName = src.MicDeviceName
        clone.MicDeviceId = src.MicDeviceId
        clone.AudioTrackMode = src.AudioTrackMode
        clone.PixelFormat = src.PixelFormat
        clone.Preset = src.Preset
        clone.NvencPreset = src.NvencPreset
        clone.RateControl = src.RateControl
        clone.FileFormat = src.FileFormat
        clone.FFmpegPath = src.FFmpegPath
        clone.HotkeyStart = src.HotkeyStart
        clone.HotkeyStop = src.HotkeyStop
        clone.HotkeyToggle = src.HotkeyToggle
        clone.CustomWidth = src.CustomWidth
        clone.CustomHeight = src.CustomHeight
        clone.ConfigVersion = src.ConfigVersion
        Return clone
    End Function

    ''' <summary>
    ''' Run a single scenario and return the parsed result.
    '''
    ''' For multi-cycle scenarios (CycleCount > 1): runs CycleCount cycles, each with
    ''' fresh CaptureEngine + fresh output file. Aggregated result aggregates worst-case
    ''' metrics across all cycles (PASS only if all cycles pass).
    '''
    ''' Per GPT P0 #3: CaptureEngine is disposed in Try/Finally to prevent
    ''' JobObjectGuard/event handler/native resource leaks across scenarios.
    ''' </summary>
    Public Async Function RunScenarioAsync(scenario As TestScenario) As Task(Of TestResult)
        Dim result As New TestResult With {
            .Name = scenario.Name,
            .TargetFps = scenario.Settings.FPS,
            .CycleCount = scenario.CycleCount
        }

        ' ── Pre-flight warning for scenarios requiring silent system audio ──
        If scenario.RequiresSilentSystemAudio Then
            Console.WriteLine($"  ⚠️  WARNING: scenario '{scenario.Name}' requires silent system audio.")
            Console.WriteLine($"      Mute YouTube/notifications/etc. before running, or this test may be invalid.")
        End If

        ' ── Single-cycle path ──
        If scenario.CycleCount <= 1 Then
            Dim cycleResult As TestResult = Await RunSingleCycleAsync(scenario, 1)
            ' Copy cycle result into main result
            CopyResult(cycleResult, result)
            Return result
        End If

        ' ── Multi-cycle path (scenario 06 spam test) ──
        Dim allCyclePass As Boolean = True
        Dim worstFailures As New List(Of String)()

        For cycle As Integer = 1 To scenario.CycleCount
            Console.WriteLine($"    → cycle {cycle}/{scenario.CycleCount}…")
            Dim cycleResult As TestResult = Await RunSingleCycleAsync(scenario, cycle)
            result.CycleResults.Add(cycleResult)

            If Not cycleResult.Pass Then
                allCyclePass = False
                worstFailures.Add($"cycle {cycle}: {cycleResult.FailureReason}")
            End If

            ' Brief pause between cycles (let filesystem settle)
            Await Task.Delay(200)
        Next

        ' ── Aggregate metrics: take worst-case for each metric ──
        AggregateCycleResults(result)

        result.Pass = allCyclePass
        If Not allCyclePass Then
            result.FailureReason = String.Join("; ", worstFailures)
        End If

        Return result
    End Function

    ''' <summary>
    ''' Run a single cycle of a scenario. Used by both single-cycle and multi-cycle paths.
    ''' Creates fresh CaptureEngine, disposes in Finally (per GPT P0 #3).
    ''' </summary>
    Private Async Function RunSingleCycleAsync(scenario As TestScenario, cycleNum As Integer) As Task(Of TestResult)
        Dim result As New TestResult With {
            .Name = scenario.Name,
            .TargetFps = scenario.Settings.FPS,
            .CycleCount = 1
        }

        Dim engine As CaptureEngine = Nothing
        Try
            ' Generate unique output path per cycle
            Dim timestamp As String = DateTime.Now.ToString("yyyyMMdd_HHmmss")
            Dim outputPath As String = Path.Combine(_outputDir,
                $"stress_{scenario.Name}_c{cycleNum}_{timestamp}.mp4")
            result.OutputFile = outputPath

            ' Create fresh CaptureEngine for this cycle (per GPT P0 #3)
            engine = New CaptureEngine(scenario.Settings)

            Dim startComplete As New TaskCompletionSource(Of Boolean)()
            Dim stopComplete As New TaskCompletionSource(Of Boolean)()
            Dim errorMsg As String = ""

            AddHandler engine.RecordingStarted, Sub(file As String)
                                                    startComplete.TrySetResult(True)
                                                End Sub
            AddHandler engine.RecordingStopped, Sub(file As String)
                                                    stopComplete.TrySetResult(True)
                                                End Sub
            AddHandler engine.ErrorOccurred, Sub(msg As String)
                                                 errorMsg = msg
                                                 startComplete.TrySetResult(False)
                                                 stopComplete.TrySetResult(False)
                                             End Sub

            ' ── Start ──
            ' Per P0 fix: add timeout to StartRecordingAsync — if engine hangs on start,
            ' don't wait forever (was hanging UI silently).
            Dim startTask As Task(Of Boolean) = engine.StartRecordingAsync(outputPath)
            Dim startTimeoutTask As Task = Task.Delay(15000)  ' 15s max for start
            Dim completedTask As Task = Await Task.WhenAny(startTask, startTimeoutTask)

            If completedTask Is startTimeoutTask AndAlso Not startTask.IsCompleted Then
                result.Pass = False
                result.FailureReason = "StartRecordingAsync TIMEOUT (>15s) — engine may be hung"
                Return result
            End If

            Dim startOk As Boolean = startTask.Result
            If Not startOk Then
                Await Task.WhenAny(startComplete.Task, Task.Delay(5000))
            End If

            If Not startComplete.Task.IsCompleted OrElse Not startComplete.Task.Result Then
                result.Pass = False
                result.FailureReason = If(String.IsNullOrEmpty(errorMsg), "Start failed (no event)", errorMsg)
                Return result
            End If

            ' ── Wait for duration ──
            Await Task.Delay(scenario.DurationSec * 1000)

            ' ── Stop (per GPT P1 #5: explicit timeout failure) ──
            Dim stopOk As Boolean = Await engine.StopRecordingAsync()
            If Not stopOk Then
                result.Pass = False
                result.FailureReason = "StopRecordingAsync() returned False (engine rejected stop)"
                Return result
            End If

            ' Wait for RecordingStopped event (includes mux completion)
            ' Per GPT P1 #5: explicit timeout = FAIL with distinct reason
            Dim stopTimeout As Boolean = Not stopComplete.Task.Wait(30000)
            If stopTimeout Then
                result.Pass = False
                result.FailureReason = "Stop/Mux TIMEOUT (>30s) — RecordingStopped event never fired " &
                                        "(FFmpeg mux may be hung or output file may be locked)"
                Return result
            End If

            ' Check if RecordingStopped fired with error (stopComplete might have been
            ' set by ErrorOccurred handler with False result)
            If stopComplete.Task.IsCompleted AndAlso stopComplete.Task.Result = False AndAlso
               Not String.IsNullOrEmpty(errorMsg) Then
                result.Pass = False
                result.FailureReason = $"Stop error: {errorMsg}"
                Return result
            End If

            ' ── Parse metrics from engine's last-run state ──
            ParseFFmpegStats(engine.LastFFmpegStatsLine, result)
            ParseAudioDiagnostics(engine.LastAudioDiagnostics, scenario, result)
            ParseMuxSummary(engine.LastMuxSummary, result)

            ' ── A/V sync via ffprobe on FINAL MP4 (per GPT P0 #1) ──
            ' CRITICAL: This is the only correct way to measure A/V sync.
            ' WAV file size / bytes_per_second would be wrong because:
            '   - WAV includes initial silence prefill (not present in muxed output)
            '   - apad padding is added during mux, not in WAV
            '   - -ss skip removes audio from WAV before mux
            ' Only the final MP4 reflects the actual A/V alignment.
            ' Per GPT P1 #5: if output file missing after successful stop,
            ' that's an explicit failure (not silent skip).
            If Not File.Exists(outputPath) Then
                result.Pass = False
                result.FailureReason = $"Output file missing after Stop: {outputPath} " &
                                       "(mux completed but file not found — possible filesystem issue)"
                Return result
            End If
            MeasureAVSyncViaFfprobe(outputPath, scenario.Settings.FFmpegPath, scenario, result)

            ' ── Apply validation rules ──
            Validate(scenario, result)

        Catch ex As Exception
            result.Pass = False
            result.FailureReason = "Exception: " & ex.Message
        Finally
            ' ── Per GPT P0 #3: Dispose CaptureEngine to release resources ──
            ' (JobObjectGuard, event handlers, FFmpeg process references, etc.)
            If engine IsNot Nothing Then
                Try
                    engine.Dispose()
                Catch
                    ' Swallow — don't mask the actual failure reason
                End Try
            End If
        End Try

        Return result
    End Function

    Private Sub CopyResult(src As TestResult, dst As TestResult)
        dst.Pass = src.Pass
        dst.FailureReason = src.FailureReason
        dst.ActualFps = src.ActualFps
        dst.Speed = src.Speed
        dst.Dup = src.Dup
        dst.Drop = src.Drop
        dst.VideoDurationSec = src.VideoDurationSec
        dst.SysCallbacks = src.SysCallbacks
        dst.SysBytesEnqueued = src.SysBytesEnqueued
        dst.SysWrittenBytes = src.SysWrittenBytes
        dst.SysWriteLagBytes = src.SysWriteLagBytes
        dst.SysDroppedChunks = src.SysDroppedChunks
        dst.SysDroppedBytes = src.SysDroppedBytes
        dst.SysDroppedSilenceBytes = src.SysDroppedSilenceBytes
        dst.SysBytesAccountingResidual = src.SysBytesAccountingResidual
        dst.SysInitialSilenceSec = src.SysInitialSilenceSec
        dst.MicCallbacks = src.MicCallbacks
        dst.MicBytesEnqueued = src.MicBytesEnqueued
        dst.MicWrittenBytes = src.MicWrittenBytes
        dst.MicWriteLagBytes = src.MicWriteLagBytes
        dst.MicDroppedBytes = src.MicDroppedBytes
        dst.MicBytesAccountingResidual = src.MicBytesAccountingResidual
        dst.SysOffsetSec = src.SysOffsetSec
        dst.MicOffsetSec = src.MicOffsetSec
        dst.MuxExitCode = src.MuxExitCode
        dst.MuxDurationMs = src.MuxDurationMs
        dst.FinalVideoDurationSec = src.FinalVideoDurationSec
        dst.FinalAudioDurationSec = src.FinalAudioDurationSec
        dst.FinalAudioStartTimeSec = src.FinalAudioStartTimeSec
        dst.AVSyncMeasured = src.AVSyncMeasured
        dst.StartOffsetMs = src.StartOffsetMs
        dst.EndOffsetMs = src.EndOffsetMs
        dst.AVSyncMs = src.AVSyncMs
        dst.OutputFile = src.OutputFile
    End Sub

    Private Sub AggregateCycleResults(result As TestResult)
        ' For multi-cycle scenarios, take WORST case for each metric
        ' (e.g., if any cycle had DroppedBytes > 0, the aggregate fails)
        '
        ' CRITICAL (per GPT P0 #1): MUST NOT mutate CycleResults items.
        ' Old code used `Dim worst = result.CycleResults(0)` which is a
        ' REFERENCE to cycle 1, then accumulated totals onto it (doubling cycle 1
        ' contributions, then adding cycles 2..N on top). Now uses a FRESH
        ' aggregate object — cycle results remain untouched.
        If result.CycleResults.Count = 0 Then Return

        ' ── Initialize aggregate from cycle 1's worst-case candidates ──
        Dim first As TestResult = result.CycleResults(0)
        Dim worstDup As Long = first.Dup
        Dim worstDrop As Long = first.Drop
        Dim worstFps As Double = first.ActualFps
        Dim worstSpeed As Double = first.Speed
        Dim worstSysDroppedBytes As Long = first.SysDroppedBytes
        Dim worstSysDroppedChunks As Long = first.SysDroppedChunks
        Dim worstSysDroppedSilenceBytes As Long = first.SysDroppedSilenceBytes
        Dim worstSysResidual As Long = first.SysBytesAccountingResidual
        Dim worstSysWriteLag As Long = first.SysWriteLagBytes
        Dim worstMicDroppedBytes As Long = first.MicDroppedBytes
        Dim worstMicResidual As Long = first.MicBytesAccountingResidual
        Dim worstMicWriteLag As Long = first.MicWriteLagBytes
        Dim worstAVSyncMs As Double = first.AVSyncMs
        ' Per GPT P2 #4: also aggregate StartOffsetMs / EndOffsetMs separately
        ' so diagnostics can show WHICH side of the A/V sync is worst (start vs end)
        Dim worstStartOffsetMs As Double = first.StartOffsetMs
        Dim worstEndOffsetMs As Double = first.EndOffsetMs

        ' ── Accumulate totals (start from 0, NOT from cycle 1) ──
        Dim totalSysCallbacks As Long = 0
        Dim totalSysBytesEnqueued As Long = 0
        Dim totalSysWrittenBytes As Long = 0
        Dim totalMicCallbacks As Long = 0
        Dim totalMicBytesEnqueued As Long = 0
        Dim totalMicWrittenBytes As Long = 0
        Dim totalVideoDuration As Double = 0
        Dim totalFinalVideoDuration As Double = 0
        Dim totalFinalAudioDuration As Double = 0
        Dim allAVSyncMeasured As Boolean = True

        ' ── Iterate all cycles, update worst-case + accumulate totals ──
        For Each r As TestResult In result.CycleResults
            ' Worst case (max for negative metrics, min for FPS/Speed)
            If r.Dup > worstDup Then worstDup = r.Dup
            If r.Drop > worstDrop Then worstDrop = r.Drop
            If r.ActualFps > 0 AndAlso (worstFps = 0 OrElse r.ActualFps < worstFps) Then
                worstFps = r.ActualFps
            End If
            If r.Speed > 0 AndAlso (worstSpeed = 0 OrElse r.Speed < worstSpeed) Then
                worstSpeed = r.Speed
            End If
            If r.SysDroppedBytes > worstSysDroppedBytes Then worstSysDroppedBytes = r.SysDroppedBytes
            If r.SysDroppedChunks > worstSysDroppedChunks Then worstSysDroppedChunks = r.SysDroppedChunks
            If r.SysDroppedSilenceBytes > worstSysDroppedSilenceBytes Then worstSysDroppedSilenceBytes = r.SysDroppedSilenceBytes
            If r.SysBytesAccountingResidual > worstSysResidual Then
                worstSysResidual = r.SysBytesAccountingResidual
            End If
            If r.SysWriteLagBytes > worstSysWriteLag Then worstSysWriteLag = r.SysWriteLagBytes
            If r.MicDroppedBytes > worstMicDroppedBytes Then worstMicDroppedBytes = r.MicDroppedBytes
            If r.MicBytesAccountingResidual > worstMicResidual Then
                worstMicResidual = r.MicBytesAccountingResidual
            End If
            If r.MicWriteLagBytes > worstMicWriteLag Then worstMicWriteLag = r.MicWriteLagBytes
            If r.AVSyncMs > worstAVSyncMs Then worstAVSyncMs = r.AVSyncMs
            If r.StartOffsetMs > worstStartOffsetMs Then worstStartOffsetMs = r.StartOffsetMs
            If r.EndOffsetMs > worstEndOffsetMs Then worstEndOffsetMs = r.EndOffsetMs

            ' Track if ALL cycles successfully measured AV sync
            If Not r.AVSyncMeasured Then allAVSyncMeasured = False

            ' Accumulate totals (every cycle, including cycle 1)
            totalSysCallbacks += r.SysCallbacks
            totalSysBytesEnqueued += r.SysBytesEnqueued
            totalSysWrittenBytes += r.SysWrittenBytes
            totalMicCallbacks += r.MicCallbacks
            totalMicBytesEnqueued += r.MicBytesEnqueued
            totalMicWrittenBytes += r.MicWrittenBytes
            totalVideoDuration += r.VideoDurationSec
            totalFinalVideoDuration += r.FinalVideoDurationSec
            totalFinalAudioDuration += r.FinalAudioDurationSec
        Next

        ' ── Write worst-case values back to AGGREGATE result (not cycle results) ──
        result.ActualFps = worstFps
        result.Speed = worstSpeed
        result.Dup = worstDup
        result.Drop = worstDrop
        result.SysDroppedBytes = worstSysDroppedBytes
        result.SysDroppedChunks = worstSysDroppedChunks
        result.SysDroppedSilenceBytes = worstSysDroppedSilenceBytes
        result.SysBytesAccountingResidual = worstSysResidual
        result.SysWriteLagBytes = worstSysWriteLag
        result.MicDroppedBytes = worstMicDroppedBytes
        result.MicBytesAccountingResidual = worstMicResidual
        result.MicWriteLagBytes = worstMicWriteLag
        result.AVSyncMs = worstAVSyncMs
        result.StartOffsetMs = worstStartOffsetMs
        result.EndOffsetMs = worstEndOffsetMs
        result.AVSyncMeasured = allAVSyncMeasured

        ' ── Write accumulated totals ──
        result.SysCallbacks = totalSysCallbacks
        result.SysBytesEnqueued = totalSysBytesEnqueued
        result.SysWrittenBytes = totalSysWrittenBytes
        result.MicCallbacks = totalMicCallbacks
        result.MicBytesEnqueued = totalMicBytesEnqueued
        result.MicWrittenBytes = totalMicWrittenBytes
        result.VideoDurationSec = totalVideoDuration
        result.FinalVideoDurationSec = totalFinalVideoDuration
        result.FinalAudioDurationSec = totalFinalAudioDuration
    End Sub

    ''' <summary>
    ''' Run all scenarios in a matrix sequentially. Returns results for each.
    ''' </summary>
    Public Async Function RunMatrixAsync(scenarios As List(Of TestScenario),
                                         Optional progressCallback As Action(Of TestResult, Integer, Integer) = Nothing
                                         ) As Task(Of List(Of TestResult))
        Dim results As New List(Of TestResult)()
        Dim total As Integer = scenarios.Count

        For i As Integer = 0 To total - 1
            Dim scenario As TestScenario = scenarios(i)
            Console.WriteLine($"[{i + 1}/{total}] Running: {scenario.Name} — {scenario.Description}")
            Dim result As TestResult = Await RunScenarioAsync(scenario)
            results.Add(result)
            Console.WriteLine($"  → {result}")
            If progressCallback IsNot Nothing Then
                progressCallback(result, i + 1, total)
            End If

            ' Brief pause between scenarios to let filesystem settle
            Await Task.Delay(500)
        Next

        Return results
    End Function

    ' ═══════════════════════════════════════════════════════════════
    ' PARSING
    ' ═══════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Parse FFmpeg's final "frame=... Lsize=... dup=... drop=... speed=..." line.
    ''' Example:
    '''   frame= 6540 fps=143 q=8.0 Lsize=   94278KiB time=00:00:45.41 bitrate=17005.4kbits/s dup=760 drop=1 speed=0.997x elapsed=0:00:45.55
    ''' </summary>
    Private Sub ParseFFmpegStats(line As String, result As TestResult)
        If String.IsNullOrEmpty(line) Then Return

        ' fps=N or fps=N.M
        Dim fpsMatch As Match = Regex.Match(line, "fps=\s*(\d+(?:\.\d+)?)")
        If fpsMatch.Success Then
            Double.TryParse(fpsMatch.Groups(1).Value, result.ActualFps)
        End If

        ' speed=Nx or speed=N.Mx
        Dim speedMatch As Match = Regex.Match(line, "speed=\s*(\d+(?:\.\d+)?)x")
        If speedMatch.Success Then
            Double.TryParse(speedMatch.Groups(1).Value, result.Speed)
        End If

        ' dup=N
        Dim dupMatch As Match = Regex.Match(line, "dup=\s*(\d+)")
        If dupMatch.Success Then
            Long.TryParse(dupMatch.Groups(1).Value, result.Dup)
        End If

        ' drop=N
        Dim dropMatch As Match = Regex.Match(line, "drop=\s*(\d+)")
        If dropMatch.Success Then
            Long.TryParse(dropMatch.Groups(1).Value, result.Drop)
        End If

        ' time=HH:MM:SS.mmm → convert to seconds
        Dim timeMatch As Match = Regex.Match(line, "time=(\d+):(\d+):(\d+(?:\.\d+)?)")
        If timeMatch.Success Then
            Dim h As Double, m As Double, s As Double
            Double.TryParse(timeMatch.Groups(1).Value, h)
            Double.TryParse(timeMatch.Groups(2).Value, m)
            Double.TryParse(timeMatch.Groups(3).Value, s)
            result.VideoDurationSec = h * 3600 + m * 60 + s
        End If
    End Sub

    ''' <summary>
    ''' Parse AudioFileWriter.GetDiagnostics() output.
    ''' Format:
    '''   [Audio] SysCallbacks=647
    '''   [Audio] SysBytesEnqueued=17803312
    '''   ...
    ''' </summary>
    Private Sub ParseAudioDiagnostics(diagnostics As String, scenario As TestScenario, result As TestResult)
        If String.IsNullOrEmpty(diagnostics) Then Return

        For Each line As String In diagnostics.Split(New Char() {ControlChars.Lf, ControlChars.Cr}, StringSplitOptions.RemoveEmptyEntries)
            line = line.Trim()
            If Not line.StartsWith("[Audio] ") Then Continue For

            Dim eqIdx As Integer = line.IndexOf("="c)
            If eqIdx < 0 Then Continue For

            Dim key As String = line.Substring(8, eqIdx - 8).Trim()  ' skip "[Audio] "
            Dim valStr As String = line.Substring(eqIdx + 1).Trim()

            Select Case key
                Case "SysCallbacks" : Long.TryParse(valStr, result.SysCallbacks)
                Case "SysBytesEnqueued" : Long.TryParse(valStr, result.SysBytesEnqueued)
                Case "SysWrittenBytes" : Long.TryParse(valStr, result.SysWrittenBytes)
                Case "SysWriteLagBytes" : Long.TryParse(valStr, result.SysWriteLagBytes)
                Case "SysDroppedChunks" : Long.TryParse(valStr, result.SysDroppedChunks)
                Case "SysDroppedBytes" : Long.TryParse(valStr, result.SysDroppedBytes)
                Case "SysDroppedSilenceBytes" : Long.TryParse(valStr, result.SysDroppedSilenceBytes)
                Case "SysBytesAccountingResidual" : Long.TryParse(valStr, result.SysBytesAccountingResidual)
                Case "SysInitialSilenceSec" : Double.TryParse(valStr, result.SysInitialSilenceSec)

                Case "MicCallbacks" : Long.TryParse(valStr, result.MicCallbacks)
                Case "MicBytesEnqueued" : Long.TryParse(valStr, result.MicBytesEnqueued)
                Case "MicWrittenBytes" : Long.TryParse(valStr, result.MicWrittenBytes)
                Case "MicWriteLagBytes" : Long.TryParse(valStr, result.MicWriteLagBytes)
                Case "MicDroppedBytes" : Long.TryParse(valStr, result.MicDroppedBytes)
                Case "MicBytesAccountingResidual" : Long.TryParse(valStr, result.MicBytesAccountingResidual)
            End Select
        Next
    End Sub

    Private Sub ParseMuxSummary(summary As String, result As TestResult)
        ' Currently _lastMuxSummary is not populated by CaptureEngine
        ' (could be added later). Leave defaults.
    End Sub

    ''' <summary>
    ''' Measure A/V sync by running ffprobe on the FINAL MP4 output (per GPT P0 #1).
    '''
    ''' This is the ONLY correct way to measure A/V sync. The WAV file size is
    ''' meaningless for sync because:
    '''   - WAV includes initial silence prefill (added by AudioFileWriter)
    '''   - apad padding is added during mux (not in WAV)
    '''   - -ss skip removes audio from WAV before mux
    '''   - AudioFileWriter WAV ≠ final MP4 audio stream duration
    '''
    ''' Only ffprobe on the final MP4 reflects the actual muxed A/V alignment.
    '''
    ''' Per GPT P0 #2: sets AVSyncMeasured=True ONLY if both video + audio streams
    ''' were successfully queried. If ffprobe fails or output file missing,
    ''' AVSyncMeasured stays False → validation FAILS the scenario.
    '''
    ''' Per GPT P1 #3: separates StartOffsetMs and EndOffsetMs:
    '''   StartOffsetMs = |audio_start_time - 0| × 1000  (video starts at 0 by convention)
    '''   EndOffsetMs = |video_duration - (audio_start + audio_duration)| × 1000
    '''   AVSyncMs = Max(StartOffsetMs, EndOffsetMs) — worst-case offset
    '''
    ''' Per GPT P1 #4: probes ALL audio streams (a:0, a:1, ...) for separate-track
    ''' scenarios. Takes WORST case across all audio streams.
    ''' </summary>
    Private Sub MeasureAVSyncViaFfprobe(mp4Path As String, ffmpegPath As String,
                                         scenario As TestScenario, result As TestResult)
        Try
            ' Find ffprobe.exe — same directory as ffmpeg.exe
            Dim ffmpegDir As String = Path.GetDirectoryName(ffmpegPath)
            Dim ffprobePath As String = Path.Combine(ffmpegDir, "ffprobe.exe")
            If Not File.Exists(ffprobePath) Then
                ffprobePath = Path.Combine(ffmpegDir, "API-Core", "ffprobe.exe")
            End If
            If Not File.Exists(ffprobePath) Then
                ffprobePath = Path.Combine(ffmpegDir, "api-core", "ffprobe.exe")
            End If
            If Not File.Exists(ffprobePath) Then
                ' ffprobe unavailable — AVSyncMeasured stays False (validation will FAIL)
                Console.WriteLine($"  ⚠️  ffprobe.exe not found — A/V sync measurement unavailable")
                Return
            End If

            ' ── Query video stream duration + start_time (per GPT P0: Try-pattern) ──
            ' MUST distinguish "ffprobe returned 0" from "ffprobe failed" —
            ' old code conflated them, leading to false measurement success.
            Dim vDur As Double = 0
            Dim vStart As Double = 0
            Dim vDurOk As Boolean = TryProbeStreamDuration(ffprobePath, mp4Path, "v", 0, vDur)
            Dim vStartOk As Boolean = TryProbeStreamStartTime(ffprobePath, mp4Path, "v", 0, vStart)

            If Not vDurOk Then
                Console.WriteLine($"  ⚠️  ffprobe: video stream duration probe failed")
                Return  ' AVSyncMeasured stays False
            End If
            If Not vStartOk Then
                ' Video start_time is usually 0, but if probe failed entirely that's suspicious.
                ' Treat as failure — don't fabricate vStart=0 silently.
                Console.WriteLine($"  ⚠️  ffprobe: video stream start_time probe failed")
                Return  ' AVSyncMeasured stays False
            End If
            result.FinalVideoDurationSec = vDur

            ' ── Query audio streams (per GPT P1 #4: probe ALL audio streams) ──
            ' For separate-track scenarios (system + mic), we need to check BOTH
            ' audio streams — if either is out of sync, the test should fail.
            ' For single-track scenarios, only a:0 exists.
            Dim audioStreamCount As Integer = CountAudioStreams(ffprobePath, mp4Path)
            If audioStreamCount = 0 Then
                Console.WriteLine($"  ⚠️  ffprobe: no audio streams found in {mp4Path}")
                Return  ' AVSyncMeasured stays False
            End If

            Dim worstStartOffsetMs As Double = 0
            Dim worstEndOffsetMs As Double = 0
            Dim worstAudioDur As Double = 0
            Dim worstAudioStart As Double = 0

            For streamIdx As Integer = 0 To audioStreamCount - 1
                Dim aDur As Double = 0
                Dim aStart As Double = 0
                Dim aDurOk As Boolean = TryProbeStreamDuration(ffprobePath, mp4Path, "a", streamIdx, aDur)
                Dim aStartOk As Boolean = TryProbeStreamStartTime(ffprobePath, mp4Path, "a", streamIdx, aStart)

                If Not aDurOk Then
                    Console.WriteLine($"  ⚠️  ffprobe: audio stream a:{streamIdx} duration probe failed")
                    Return  ' AVSyncMeasured stays False
                End If
                If Not aStartOk Then
                    Console.WriteLine($"  ⚠️  ffprobe: audio stream a:{streamIdx} start_time probe failed")
                    Return  ' AVSyncMeasured stays False
                End If

                ' Track worst-case across all audio streams
                ' StartOffsetMs = |audio_start - video_start|
                Dim thisStartOffsetMs As Double = Math.Abs(aStart - vStart) * 1000.0
                ' EndOffsetMs = |video_duration - (audio_start + audio_duration)|
                Dim audioEnd As Double = aStart + aDur
                Dim thisEndOffsetMs As Double = Math.Abs(vDur - audioEnd) * 1000.0

                If thisStartOffsetMs > worstStartOffsetMs Then
                    worstStartOffsetMs = thisStartOffsetMs
                    worstAudioStart = aStart
                End If
                If thisEndOffsetMs > worstEndOffsetMs Then worstEndOffsetMs = thisEndOffsetMs
                If aDur > worstAudioDur Then worstAudioDur = aDur

                Console.WriteLine($"  ffprobe: a:{streamIdx} dur={aDur:F3}s start={aStart:F3}s " &
                                  $"startOffset={thisStartOffsetMs:F0}ms endOffset={thisEndOffsetMs:F0}ms")
            Next

            ' ── Set aggregate metrics (worst-case across all audio streams) ──
            result.FinalAudioDurationSec = worstAudioDur
            result.FinalAudioStartTimeSec = worstAudioStart
            result.StartOffsetMs = worstStartOffsetMs
            result.EndOffsetMs = worstEndOffsetMs
            result.AVSyncMs = Math.Max(worstStartOffsetMs, worstEndOffsetMs)
            result.AVSyncMeasured = True  ' CRITICAL: mark as measured (per GPT P0 #2)

        Catch ex As Exception
            ' ffprobe threw — AVSyncMeasured stays False (validation will FAIL)
            Console.WriteLine($"  ⚠️  ffprobe A/V sync measurement failed: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Count audio streams in the MP4 (for separate-track scenarios).
    ''' ffprobe -v error -select_streams a -show_entries stream=index -of csv=p=0
    ''' Returns one line per audio stream.
    ''' </summary>
    Private Function CountAudioStreams(ffprobePath As String, mp4Path As String) As Integer
        Dim args As String = $"-v error -select_streams a -show_entries stream=index -of csv=p=0 ""{mp4Path}"""
        Dim stdout As String = RunFfprobeSync(ffprobePath, args)
        If String.IsNullOrEmpty(stdout) Then Return 0
        Dim lines As String() = stdout.Split(New Char() {ControlChars.Lf, ControlChars.Cr},
                                              StringSplitOptions.RemoveEmptyEntries)
        Return lines.Length
    End Function

    ''' <summary>
    ''' Try to probe a stream's duration. Returns True if ffprobe ran + parsed successfully,
    ''' False otherwise. The actual value is returned via ByRef parameter.
    '''
    ''' CRITICAL (per GPT P0): must distinguish "ffprobe returned 0.0" from "ffprobe failed".
    ''' Old code returned 0 on failure, which the caller interpreted as "duration = 0" →
    ''' false measurement success. Now: Try-pattern with explicit Boolean return.
    ''' </summary>
    Private Function TryProbeStreamDuration(ffprobePath As String, mp4Path As String,
                                             codecType As String, streamIdx As Integer,
                                             ByRef duration As Double) As Boolean
        duration = 0
        Dim args As String = $"-v error -select_streams {codecType}:{streamIdx} -show_entries stream=duration -of csv=p=0 ""{mp4Path}"""
        Dim stdout As String = RunFfprobeSync(ffprobePath, args)
        If String.IsNullOrEmpty(stdout) Then Return False
        Dim dur As Double = 0
        If Not Double.TryParse(stdout.Trim(), Globalization.CultureInfo.InvariantCulture, dur) Then
            Return False
        End If
        ' Negative or zero duration means probe failed or stream is empty
        If dur <= 0 Then Return False
        duration = dur
        Return True
    End Function

    ''' <summary>
    ''' Try to probe a stream's start_time. Returns True if ffprobe ran + parsed successfully.
    '''
    ''' Note: start_time of 0 is a VALID value (audio starts at video start), so we
    ''' cannot use 0 as a "failure" sentinel. The Boolean return distinguishes them.
    ''' </summary>
    Private Function TryProbeStreamStartTime(ffprobePath As String, mp4Path As String,
                                              codecType As String, streamIdx As Integer,
                                              ByRef startTime As Double) As Boolean
        startTime = 0
        Dim args As String = $"-v error -select_streams {codecType}:{streamIdx} -show_entries stream=start_time -of csv=p=0 ""{mp4Path}"""
        Dim stdout As String = RunFfprobeSync(ffprobePath, args)
        If String.IsNullOrEmpty(stdout) Then Return False
        Dim t As Double = 0
        If Not Double.TryParse(stdout.Trim(), Globalization.CultureInfo.InvariantCulture, t) Then
            Return False
        End If
        ' start_time of 0 is valid, but negative is suspicious
        If t < 0 Then Return False
        startTime = t
        Return True
    End Function

    Private Function RunFfprobeSync(ffprobePath As String, args As String) As String
        Dim psi As New ProcessStartInfo()
        psi.FileName = ffprobePath
        psi.Arguments = args
        psi.UseShellExecute = False
        psi.RedirectStandardOutput = True
        psi.RedirectStandardError = False  ' don't redirect stderr (avoid deadlock)
        psi.CreateNoWindow = True

        Using proc As New Process()
            proc.StartInfo = psi
            proc.Start()
            ' Async read to prevent deadlock if ffprobe writes a lot
            Dim stdoutTask As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
            Dim exited As Boolean = proc.WaitForExit(10000)
            If Not exited Then
                Try : proc.Kill() : Catch : End Try
                Return ""
            End If
            Try
                Return stdoutTask.Result
            Catch
                Return ""
            End Try
        End Using
    End Function

    ' ═══════════════════════════════════════════════════════════════
    ' VALIDATION
    ' ═══════════════════════════════════════════════════════════════

    Private Sub Validate(scenario As TestScenario, result As TestResult)
        Dim failures As New List(Of String)()

        ' ── RED FLAGS (invariant violations, auto-fail) ──
        If result.SysDroppedBytes > 0 Then
            failures.Add($"SysDroppedBytes={result.SysDroppedBytes} (real audio data loss)")
        End If
        If result.MicDroppedBytes > 0 Then
            failures.Add($"MicDroppedBytes={result.MicDroppedBytes} (real mic data loss)")
        End If
        If result.SysBytesAccountingResidual <> 0 Then
            failures.Add($"SysBytesAccountingResidual={result.SysBytesAccountingResidual} (invariant violated)")
        End If
        If result.MicBytesAccountingResidual <> 0 Then
            failures.Add($"MicBytesAccountingResidual={result.MicBytesAccountingResidual} (invariant violated)")
        End If

        ' ── Writer FAILED check (per GPT P1 #3: separate System + Mic) ──
        ' If bytes were enqueued but writer wrote 0 bytes, the writer crashed.
        ' Check each track independently — Mic writer crash shouldn't be masked
        ' by System writer success.
        If scenario.Settings.SystemAudioCapture AndAlso
           result.SysBytesEnqueued > 0 AndAlso result.SysWrittenBytes = 0 Then
            failures.Add("System Writer FAILED (SysBytesEnqueued > 0 but SysWrittenBytes = 0)")
        End If
        If scenario.Settings.MicCapture AndAlso
           result.MicBytesEnqueued > 0 AndAlso result.MicWrittenBytes = 0 Then
            failures.Add("Mic Writer FAILED (MicBytesEnqueued > 0 but MicWrittenBytes = 0)")
        End If

        ' ── Per GPT P1 #2: ExpectAudioData assertion (already separate System + Mic) ──
        ' If scenario expects audio data AND silence is NOT allowed,
        ' then 0 bytes captured is a failure (WASAPI never started).
        ' This already checks both System and Mic independently (lines below).
        If scenario.ExpectAudioData AndAlso Not scenario.AllowSilentCapture Then
            If scenario.Settings.SystemAudioCapture AndAlso result.SysBytesEnqueued = 0 Then
                failures.Add("ExpectAudioData=True but SysBytesEnqueued=0 (WASAPI system capture never started)")
            End If
            If scenario.Settings.MicCapture AndAlso result.MicBytesEnqueued = 0 Then
                failures.Add("ExpectAudioData=True but MicBytesEnqueued=0 (WASAPI mic capture never started)")
            End If
        End If

        ' ── HARD PERFORMANCE FAILURES (threshold exceeded, auto-fail) ──
        ' Per GPT P1 #6: renamed from "WARNINGS" — these are auto-fail thresholds,
        ' not warnings. Calling them "warnings" in comments would be misleading
        ' because the test WILL fail on them.

        ' FPS must be within 5% of target (CFR should be exact)
        If result.ActualFps > 0 AndAlso result.TargetFps > 0 Then
            Dim minFps As Double = result.TargetFps * 0.95
            If result.ActualFps < minFps Then
                failures.Add($"FPS={result.ActualFps:F1} < target*0.95={minFps:F1} (video capture degraded)")
            End If
        End If

        ' Speed must be >= 0.95x (FFmpeg can keep up in real-time)
        If result.Speed > 0 AndAlso result.Speed < 0.95 Then
            failures.Add($"Speed={result.Speed:F2}x < 0.95 (FFmpeg behind real-time)")
        End If

        ' dup threshold: at 144 FPS for 15s = 2160 frames, 5% = 108 dup allowed
        ' (ddagrab has small natural dup at startup, that's expected)
        Dim maxDup As Long = Math.Max(50, CLng(result.TargetFps * result.VideoDurationSec * 0.05))
        If result.Dup > maxDup Then
            failures.Add($"dup={result.Dup} > {maxDup} (excessive duplicate frames — ddagrab starved)")
        End If

        ' drop threshold: any frame drop is suspicious
        If result.Drop > 5 Then
            failures.Add($"drop={result.Drop} > 5 (FFmpeg dropping frames)")
        End If

        ' WriteLagBytes after Stop should be 0 (writer drained)
        If result.SysWriteLagBytes > 0 Then
            failures.Add($"SysWriteLagBytes={result.SysWriteLagBytes} > 0 (writer didn't drain)")
        End If
        If result.MicWriteLagBytes > 0 Then
            failures.Add($"MicWriteLagBytes={result.MicWriteLagBytes} > 0 (writer didn't drain)")
        End If

        ' ── A/V sync (per GPT P0 #2 + P1 #3) ──
        ' CRITICAL: if scenario expects audio, A/V sync MUST be measured.
        ' If ffprobe failed or output file missing, AVSyncMeasured=False → FAIL.
        ' This prevents false PASS when measurement is unavailable.
        If scenario.ExpectAudio Then
            If Not result.AVSyncMeasured Then
                failures.Add("A/V sync measurement unavailable (ffprobe failed or output missing) — " &
                             "cannot validate sync, scenario FAILS rather than falsely PASSing")
            Else
                ' Check StartOffsetMs (audio start vs video start)
                ' 100ms tolerance (apad + -t handle smaller mismatches)
                If result.StartOffsetMs > 100 Then
                    failures.Add($"StartOffsetMs={result.StartOffsetMs:F0} > 100 " &
                                 "(audio starts >100ms after video start — adelay/negative offset issue)")
                End If
                ' Check EndOffsetMs (audio end vs video end)
                If result.EndOffsetMs > 100 Then
                    failures.Add($"EndOffsetMs={result.EndOffsetMs:F0} > 100 " &
                                 "(audio ends >100ms before/after video end — apad/-t issue)")
                End If
                ' AVSyncMs = Max(StartOffsetMs, EndOffsetMs) — already computed
                If result.AVSyncMs > 100 Then
                    failures.Add($"AVSyncMs={result.AVSyncMs:F0} > 100 " &
                                 "(worst-case A/V sync offset exceeds 100ms tolerance)")
                End If
            End If
        End If

        If failures.Count > 0 Then
            result.Pass = False
            result.FailureReason = String.Join("; ", failures)
        Else
            result.Pass = True
        End If
    End Sub

    ' ═══════════════════════════════════════════════════════════════
    ' FORMATTING
    ' ═══════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Format all results as an ASCII table for display/log.
    ''' </summary>
    Public Shared Function FormatResultTable(results As List(Of TestResult)) As String
        Dim sb As New StringBuilder()
        sb.AppendLine()
        sb.AppendLine("┌──────────────────────────────────────────────────────────────────────────────────────┐")
        sb.AppendLine("│                          STRESS TEST RESULTS                                         │")
        sb.AppendLine("├──────────────────────────────────────────────────────────────────────────────────────┤")
        sb.AppendLine("│ Scenario                        │ Status │ FPS    │ Dup   │ Drop  │ Drops │ Resid │ Sync │")
        sb.AppendLine("├────────────────────────────────────────┼────────┼───────┼───────┼───────┼───────┼──────┤")

        For Each r As TestResult In results
            Dim status As String = If(r.Pass, "PASS", "FAIL")
            Dim fps As String = $"{r.ActualFps:F1}/{r.TargetFps}"
            Dim syncStr As String = If(r.AVSyncMs > 0, $"{r.AVSyncMs:F0}ms", "-")
            Dim drops As String = (r.SysDroppedBytes + r.MicDroppedBytes).ToString()
            Dim resid As String = (r.SysBytesAccountingResidual + r.MicBytesAccountingResidual).ToString()

            sb.AppendLine($"│ {r.Name,-32} │ {status,-6} │ {fps,-6} │ {r.Dup,5} │ {r.Drop,5} │ {drops,5} │ {resid,5} │ {syncStr,4} │")
        Next

        sb.AppendLine("├──────────────────────────────────────────────────────────────────────────────────────┤")
        Dim passCount As Integer = Enumerable.Count(results, Function(r) r.Pass)
        Dim failCount As Integer = results.Count - passCount
        sb.AppendLine($"│ Total: {results.Count}  |  Pass: {passCount}  |  Fail: {failCount}                                    │")
        sb.AppendLine("└──────────────────────────────────────────────────────────────────────────────────────┘")

        ' List failures with reasons
        Dim failures As List(Of TestResult) = Enumerable.Where(results, Function(r) Not r.Pass).ToList()
        If failures.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine("FAILURES:")
            For Each f As TestResult In failures
                sb.AppendLine($"  {f.Name}:")
                sb.AppendLine($"    {f.FailureReason}")
            Next
        End If

        Return sb.ToString()
    End Function

End Class
