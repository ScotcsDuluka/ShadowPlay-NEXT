Option Strict On
Option Explicit On
Option Infer On

' CaptureSession.vb
'
' Per-session resource owner. Composes:
'   - BoundedVideoFrameSink (latest-oriented bounded frame handoff)
'   - System audio → WavSidecarWriter (bounded queue + writer thread)
'   - Raw H.264 file (native NVENC packets)
'   - FFmpeg wrap (raw H.264 → temp MP4 @ display refresh rate)
'   - MuxCoordinator (video + audio → final MP4, A/V sync offsets)
'
' Lifecycle (per session):
'   1. Start audio sidecar (WASAPI loopback → WavSidecarWriter queue)
'   2. Start video capture (DdagrabBackend.Start(sink)) + encoder
'   3. Capture/encode loop: sink.Take → encoder.Encode → write H.264 to file
'   4. Stop video → drain sink → stop encoder
'   5. Stop audio → event-driven WAV finalize (accounting invariant)
'   6. FFmpeg wrap: H.264 → temp MP4 at DISPLAY REFRESH RATE
'   7. ffprobe temp MP4 → exact video duration
'   8. FFmpeg mux with SyncMath offsets → final MP4
'   9. Verify MP4 streams → SessionResult
'
' Phase 12b audio model (AUDIO hard-blocker rework):
'   PRODUCER  : WASAPI DataAvailable callback → copy → enqueue. NO disk I/O
'               on the callback thread (proven AudioFileWriter lesson).
'   CONSUMER  : WavSidecarWriter dedicated thread drains bounded queue.
'   ACCOUNTING: enqueued = written + dropped (residual must be 0).
'   SYNC      : systemStartTicks captured at StartRecording() CALL time
'               (NOT first callback — avoids the historical -10s bug);
'               videoStartTicks at first frame taken from the sink;
'               offset = (videoStart - systemStart)/freq clamped [-2,+5]
'               (proven legacy model, extracted into SyncMath).
'   FINALIZE  : RecordingStopped event → ManualResetEvent wait (no Sleep).
'
' Ownership:
'   - BORROWS _capture + _encoder from RecordingEngine (does NOT dispose)
'   - OWNS sink + sidecar writer + audio capture + H.264 file + temp files

Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports NAudio.Wave
Imports CaptureEngine.Audio
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Video
Imports CaptureEngine.Video.Backends.Ddagrab
Imports CaptureEngine.Video.Handoff
Imports CaptureEngine.Encoder
Imports CaptureEngine.Encoder.Nvenc
Imports CaptureEngine.FFmpegBackend
Imports CaptureEngine.Audio.Wasapi

Namespace CaptureEngine.Recording

    Public NotInheritable Class CaptureSession
        Implements IDisposable

        ' ★ CFR PACING FIX (the 60fps bug): Thread.Sleep on Windows has a
        ' ~15.6ms quantum unless some process raised the system timer
        ' resolution. Our CFR loop targets 13.3ms ticks (75fps) but slept
        ' 15.6ms per tick = EXACTLY 60fps — proven by three owner runs:
        '   2138 frames / 35.66s = 59.95fps  (21:47)
        '    788 frames / 12.10s = 65.1fps   (01:49)
        ' The video pipe declares -framerate 75 up front, so a 60fps delivery
        ' compresses the video timeline by 20% — desync that GROWS with clip
        ' length and no lead calibration can fix. While a game runs, the game
        ' itself raises the resolution (why it 'sometimes worked'). We now
        ' raise it ourselves for the session's duration.
        <DllImport("winmm.dll")>
        Private Shared Function timeBeginPeriod(period As UInteger) As Integer
        End Function

        <DllImport("winmm.dll")>
        Private Shared Function timeEndPeriod(period As UInteger) As Integer
        End Function

        Private ReadOnly _capture As DdagrabBackend
        Private ReadOnly _encoder As NvencEncoderBackend
        Private ReadOnly _config As SessionConfig
        Private ReadOnly _logger As EngineLogger
        Private _stopSignal As Boolean = False
        Private _stopRequestedTicks As Long = 0
        Private _disposed As Boolean = False

        ' Sync timeline (Stopwatch ticks)
        Private _systemStartTicks As Long = 0
        Private _videoStartTicks As Long = 0
        ' Single recording timeline origin shared by video, audio, and mux.
        Private _timelineStartTicks As Long = 0
        Private _timelineStartQpc100ns As Long = 0
        ' Device-mode video origin in the same 100ns QPC domain as WASAPI.
        Private _videoStartQpc100ns As Long = 0
        ' ★ M1: mic has its own independent timeline (device clock/cadence
        ' differ from system loopback — never share or derive one from other)
        Private _micStartTicks As Long = 0

        ' ★ OBS-style live mux (owner directive): one FFmpeg process receives
        ' video+audio through named pipes while recording — no temp files,
        ' no post-hoc wrap/mux, wall-clock-true by construction.
        Private _liveMux As LiveMuxSession

        ' ★ Shared Audio Engine: one audio owner for every video engine.
        ' The legacy/Duluka audio implementations below remain as migration
        ' code, but the live session path uses this shared engine only.
        Private _audioEngine As AudioEngineSession
        Private _sysAudioEngineSink As AudioEngineMuxSink
        Private _micAudioEngineSink As AudioEngineMuxSink

        ' ★ OBS trick: render endless silence to the loopback device while
        ' recording so WASAPI delivers callbacks CONTINUOUSLY (silence included).
        ' Kills gap-fill/steering/noise-at-silence at the source.
        Private _silenceKeepAlive As SilenceKeepAlive

        ' ★ AudioTap instances (single gap-fill engine for both tracks)
        Private _sysTap As AudioTap
        Private _micTap As AudioTap

        ' ★ P13.3: Device-clock path for the SYSTEM track (AudioClockMode=Device).
        ' One WasapiPositionCapture (hardware qpcPosition stamps) + one
        ' AudioTapDeviceClock (measured-gap fill). The legacy fields above
        ' stay Nothing on this path — the two modes never mix in a session.
        ' The mic track stays on the legacy tap in BOTH modes this phase.
        Private _sysPosCapture As WasapiPositionCapture
        Private _sysTap3 As AudioTapDeviceClock
        Private _deviceClockSys As Boolean = False
        Private _sessionStartQpc100ns As Long = 0

        ' ★ P13.4-race fix: video t0 (first frame, tick thread) and the first
        ' audio packet (capture thread) are independent — either may win. The
        ' mux timelines need BOTH inputs, so BeginTimelinesOnce() is callable
        ' from BOTH sides and fires the moment the last needed input exists.
        ' The applied sys offset is kept for the stop-path evidence report.
        Private _timelinesBegun As Boolean = False
        Private ReadOnly _timelineLock As New Object()
        Private _appliedSysOffsetSec As Double = Double.NaN

        Private Sub SysPipeFeed(data As Byte(), count As Integer)
            _liveMux?.FeedSystemAudio(data, count)
        End Sub

        Private Sub MicPipeFeed(data As Byte(), count As Integer)
            _liveMux?.FeedMicAudio(data, count)
        End Sub

        ''' <summary>
        ''' Begin the live-mux audio timelines EXACTLY ONCE. Two callers race
        ''' legitimately: the video-t0 tick and the Device-clock packet
        ''' handler. All inputs are frozen stamps, so time-of-call is
        ''' irrelevant; the mux queues audio bytes until the timeline event
        ''' (8 MB cap — orders above the sub-100 ms window) and its writer
        ''' waits UNBOUNDED for the timeline event (or stop — P13-AUDIO-
        ''' TIMELINE: the old 5s fall-through skipped the head pad/discard),
        ''' so deferring is safe by contract.
        ''' </summary>
        Private Sub BeginTimelinesOnce()
            If _liveMux Is Nothing Then Return
            SyncLock _timelineLock
                If _timelinesBegun Then Return
                If _videoStartTicks = 0 Then Return              ' no video origin yet
                If _deviceClockSys AndAlso
                   (_sysTap3 Is Nothing OrElse Not _sysTap3.Anchored) Then
                    Return                                       ' defer to the anchor
                End If

                Dim sysOff As Double
                If _deviceClockSys Then
                    ' P13.4 — SyncMath v2: exact QPC anchor arithmetic.
                    ' The reconstructed stream BEGINS at the first fed packet's
                    ' hardware stamp — anchor-to-anchor, no call-time guess,
                    ' no pre-roll estimate; the lead is ZERO by default
                    ' (SystemAudioLeadDeviceSec; residual goes in only if
                    ' sync-verify measures it).
                    Dim videoT0100ns As Long = _videoStartQpc100ns
                    If videoT0100ns <= 0 Then
                        videoT0100ns = WasapiPositionCapture.StopwatchTicksTo100ns(_videoStartTicks)
                    End If
                    sysOff = SyncMath.ComputeAudioOffsetSecFromAnchors(
                        videoT0100ns, _sysTap3.FirstQpc100ns) + SyncMath.SystemAudioLeadDeviceSec
                Else
                    sysOff = SyncMath.ComputeAudioOffsetSec(
                        _videoStartTicks, _systemStartTicks, Stopwatch.Frequency) + SyncMath.SystemAudioLeadSec
                End If
                Dim micOff As Double = SyncMath.ComputeAudioOffsetSec(
                    _videoStartTicks, _micStartTicks, Stopwatch.Frequency) + SyncMath.MicAudioLeadSec
                _logger.Info($"[session] timeline origin: sys offset={sysOff:0.000}s mic offset={micOff:0.000}s ({If(_deviceClockSys, "device QPC anchor", "stopwatch t0 trim")} + lead {SyncMath.SystemAudioLeadSec:0.000}s)")
                _liveMux.BeginTimelines(sysOff, micOff)
                _appliedSysOffsetSec = sysOff
                _timelinesBegun = True
            End SyncLock
        End Sub

        ' Dual sink: one stream to live-mux pipe (+ optional WAV evidence sidecar
        ' — disabled by default per audit #7; the pipe is the real output).
        Private Class AudioTapSinkDual
            Implements IAudioTapSink

            Private ReadOnly _wav As WavSidecarWriter
            Private ReadOnly _pipe As Action(Of Byte(), Integer)

            Public Sub New(wav As WavSidecarWriter, pipe As Action(Of Byte(), Integer))
                _wav = wav          ' may be Nothing (evidence sidecar disabled)
                _pipe = pipe
            End Sub

            Public Sub Write(data As Byte(), count As Integer) Implements IAudioTapSink.Write
                _wav?.EnqueueChunk(data, count)
                _pipe?.Invoke(data, count)
            End Sub
        End Class

        Public Sub New(capture As DdagrabBackend,
                      encoder As NvencEncoderBackend,
                      config As SessionConfig,
                      logger As EngineLogger)
            _capture = capture
            _encoder = encoder
            _config = config
            _logger = logger
        End Sub

        Public Function Run() As SessionResult
            Dim result As New SessionResult() With {
                .OutputPath = _config.OutputPath,
                .RequestedDurationSec = _config.DurationSeconds,
                .AudioRequested = _config.AudioEnabled
            }

            ' ─── Temp file paths ─────────────────────────────────────────
            Dim tempWav As String = Path.ChangeExtension(_config.OutputPath, ".tmp.wav")
            Dim tempMicWav As String = Path.ChangeExtension(_config.OutputPath, ".tmp.mic.wav")

            ' ─── Resources ───────────────────────────────────────────────
            Dim sink As BoundedVideoFrameSink = Nothing
            Dim frameDisposer As DeferredVideoFrameDisposer = Nothing
            Dim audioCapture As WasapiLoopbackCapture = Nothing
            Dim wavStoppedEvent As New ManualResetEvent(False)
            Dim wavWriter As WavSidecarWriter = Nothing
            Dim wavReport As WavFinalizeReport = Nothing
            Dim waveFormat As WaveFormat = Nothing

            ' ★ M1: mic sidecar — mirror of the system sidecar, fully independent
            ' (own device, own queue, own writer, own accounting, own timeline)
            Dim micCapture As NAudio.CoreAudioApi.WasapiCapture = Nothing
            Dim micStoppedEvent As New ManualResetEvent(False)
            Dim micWriter As WavSidecarWriter = Nothing
            Dim micWaveFormat As WaveFormat = Nothing
            Dim micReport As WavFinalizeReport = Nothing

            Dim sw As Stopwatch = Stopwatch.StartNew()
            Dim duration As TimeSpan = TimeSpan.FromSeconds(_config.DurationSeconds)

            ' ★ M1: session-owned video lifecycle tracking. Any path that runs
            ' after _capture.Start()/_encoder.Start() must stop them again —
            ' the success path stops inline; the Finally stops whatever a
            ' failure/exception left running, so a failed session can never
            ' leak a running capture worker (orphaned frames forever) nor
            ' leave the encoder Running: Start() no-ops on Running, which
            ' would skip the NEXT session's FORCEIDR/SPS-PPS re-arm and
            ' produce a header-less H.264 stream the mux cannot decode.
            Dim captureRunning As Boolean = False
            Dim encoderRunning As Boolean = False

            ' ★ M1: the CFR loop's retained frames (freshest pending + last
            ' displayed) were only disposed by the post-loop tail — an
            ' exception before that tail skipped it and leaked one D3D11
            ' staging texture per failure (D3D11VideoFrame has no finalizer).
            ' Declared here so the Finally can retire them on every path;
            ' Dispose is one-shot (CompareExchange), so the happy path's
            ' earlier disposal makes this a no-op there.
            Dim pendingFrame As IVideoFrame = Nothing     ' freshest captured, not yet displayed
            Dim pendingSeq As Long = -1
            Dim lastFrame As IVideoFrame = Nothing        ' last displayed (for duplicates)

            ' One common recording clock. Capture/audio can warm before T0;
            ' no sample/frame belongs to the recording timeline before this boundary.
            _timelineStartTicks = Stopwatch.GetTimestamp() + Math.Max(1L, Stopwatch.Frequency \ 10L)
            _timelineStartQpc100ns = WasapiPositionCapture.StopwatchTicksTo100ns(_timelineStartTicks)
            _systemStartTicks = _timelineStartTicks
            _logger.Info($"[session] common timeline armed: T0={_timelineStartQpc100ns} (all producers armed before T0)")

            Try
                ' ★ Timer resolution: 1ms for the whole session (CFR pacing
                ' needs sub-15.6ms sleeps — see the winmm declarations above).
                timeBeginPeriod(1UI)
                _logger.Info("[session] timer resolution set to 1ms (CFR pacing)")
                sink = New BoundedVideoFrameSink(16, BoundedHandoffPolicy.DropOldest, _logger)
                frameDisposer = New DeferredVideoFrameDisposer()

                ' ─── 2. Shared Audio Engine (single owner) ─────────────
                ' UI authority is Overlay [6] Audio Capture.vb. SessionConfig
                ' already carries those persisted values into this engine.
                _audioEngine = New AudioEngineSession(New AudioEngineConfig With {
                    .SystemEnabled = _config.AudioEnabled,
                    .MicrophoneEnabled = _config.MicEnabled,
                    .MicrophoneDeviceId = If(_config.MicDeviceId, ""),
                    .MicrophoneDeviceName = If(_config.MicDeviceName, "")
                }, Sub(m) _logger.Info(m))
                If _config.AudioEnabled Then
                    _sysAudioEngineSink = New AudioEngineMuxSink(AudioTrackKind.System)
                    _audioEngine.AddSink(AudioTrackKind.System, _sysAudioEngineSink)
                End If
                If _config.MicEnabled Then
                    _micAudioEngineSink = New AudioEngineMuxSink(AudioTrackKind.Microphone)
                    _audioEngine.AddSink(AudioTrackKind.Microphone, _micAudioEngineSink)
                End If
                ' Audio capture is armed/warmed now, but its recording timeline starts at common T0.
                _audioEngine.Start(_timelineStartQpc100ns)
                _logger.Info("[session] Audio owner = CaptureEngine.Audio (armed before common T0)")
                ' ★ Config honesty: AudioClockMode is accepted by the config
                ' stack but NO LONGER selects a path — the live session always
                ' uses the shared AudioEngine (WasapiPositionCapture stamps +
                ' QPC anchors = device-clock by construction). The Legacy/Device
                ' switch only exists in the dormant migration blocks below.
                ' Logged loudly so the setting is never silently ignored again.
                _logger.Info($"[session] AudioClockMode='{If(_config.AudioClockMode, "")}' is superseded: the shared AudioEngine path is device-clock by construction")

                ' ─── 2b. Legacy audio path is bypassed during migration ──
                ' Keep the old implementation intact for rollback/reference.
                If False Then
                ' ─── 2. Start audio sidecar ─────────────────────────────
                If _config.AudioEnabled Then
                    _logger.Info("[session] Starting audio sidecar...")

                    ' ★ P13.3 clock-mode selection (docs/PHASE-13-SHADOWPLAY-
                    ' CLOCK.md §4): "Device" → WasapiPositionCapture +
                    ' AudioTapDeviceClock (hardware-stamped timeline). Anything
                    ' else → the proven legacy path, byte-for-byte unchanged.
                    Dim deviceRequested As Boolean = String.Equals(
                        If(_config.AudioClockMode, "").Trim(), "Device",
                        StringComparison.OrdinalIgnoreCase)
                    _deviceClockSys = deviceRequested AndAlso WasapiPositionCapture.IsWindowsPlatform
                    If deviceRequested AndAlso Not WasapiPositionCapture.IsWindowsPlatform Then
                        _logger.Warning("[session] AudioClockMode=Device on non-Windows — falling back to Legacy tap")
                    End If

                    If _deviceClockSys Then
                        ' ── DEVICE-CLOCK PATH (v3) ──────────────────────────
                        ' One-time setup happens lazily on the capture thread's
                        ' FIRST PacketReady: by then the mix format is known
                        ' (Start() completed synchronously), so the sidecar
                        ' writer and the tap are built from the REAL device
                        ' format — no drops, no handler/format race.
                        Dim sysSetupDone As Boolean = False
                        _sysPosCapture = New WasapiPositionCapture(New WasapiCaptureOptions() With {
                            .Loopback = True,
                            .IncludePcm = True
                        })

                        AddHandler _sysPosCapture.PacketReady, Sub(pkt)
                            If _stopSignal OrElse sw.Elapsed >= duration Then
                                Try : _sysPosCapture.Stop() : Catch : End Try
                            End If
                            If pkt.Data Is Nothing OrElse pkt.Frames <= 0 Then Return

                            If Not sysSetupDone Then
                                sysSetupDone = True
                                Dim dch As Integer = Math.Max(1, _sysPosCapture.Channels)
                                Dim dfloat As Boolean = (_sysPosCapture.BitsPerSample = 32)
                                Dim dbits As Integer = If(dfloat, 16, _sysPosCapture.BitsPerSample)
                                _logger.Info($"[session] Audio (Device clock): {pkt.Frames}f packets @ {_sysPosCapture.SampleRate}Hz {dch}ch {_sysPosCapture.BitsPerSample}bit mix → PCM{dbits} feed")
                                If _config.EvidenceSidecar Then
                                    wavWriter = New WavSidecarWriter(tempWav, dch, _sysPosCapture.SampleRate, dbits)
                                    wavWriter.Start()
                                End If
                                _sysTap3 = New AudioTapDeviceClock("sys", _sysPosCapture.SampleRate, dch, dbits,
                                    New AudioTapSinkDual(wavWriter, AddressOf SysPipeFeed),
                                    Sub(m) _logger.Info("[session] " & m))
                                _sysTap3.PrimeSessionStart(_sessionStartQpc100ns)
                            End If

                            ' Convert the float mix format exactly like the
                            ' legacy handler, then feed WITH the hardware stamp
                            ' (frame count is invariant under the conversion).
                            ' pkt.Flags is forwarded (Phase-A): TimestampError
                            ' suppresses gap judgment for that packet (OWNER rule).
                            If _sysTap3 IsNot Nothing Then
                                If _sysPosCapture.BitsPerSample = 32 Then
                                    Dim srcBytes As Integer = pkt.Frames * Math.Max(1, _sysPosCapture.Channels) * 4
                                    Dim pcm As Byte() = ConvertFloatToPcm16(pkt.Data, Math.Min(srcBytes, pkt.Data.Length))
                                    _sysTap3.Feed(pcm, pcm.Length, pkt.DevicePositionFrames, pkt.QpcPosition100ns, pkt.Flags)
                                Else
                                    Dim rawBytes As Integer = pkt.Frames * Math.Max(1, _sysPosCapture.Channels) * Math.Max(1, _sysPosCapture.BitsPerSample \ 8)
                                    _sysTap3.Feed(pkt.Data, Math.Min(rawBytes, pkt.Data.Length), pkt.DevicePositionFrames, pkt.QpcPosition100ns, pkt.Flags)
                                End If

                                ' The moment this tap anchors, the v2 offset math
                                ' can run (video t0 may already exist, or will —
                                ' the helper handles both orders; no-op if begun).
                                If _sysTap3.Anchored Then BeginTimelinesOnce()
                            End If
                        End Sub

                        AddHandler _sysPosCapture.StoppedWithError, Sub(err)
                            ' Hardening contract: containment — this must NEVER
                            ' take the process down; the track degrades, the
                            ' session (video) continues.
                            _logger.Error("[session] Device-clock capture STOPPED WITH ERROR (track ended, session continues): " & err)
                        End Sub

                        ' Session start stamp (100ns QPC) for the tap's tail
                        ' close — replaces the legacy MarkStart wall-clock trick.
                        _sessionStartQpc100ns = WasapiPositionCapture.StopwatchTicksTo100ns(Stopwatch.GetTimestamp())
                        _sysPosCapture.Start()
                        _systemStartTicks = Stopwatch.GetTimestamp()

                        ' Model S (P13.1 v6 evidence): the endpoint renders
                        ' silence through quiet phases — the timeline advances
                        ' on its own. The keep-alive render trick is NOT needed
                        ' on this path (and stays Legacy-only after the owner's
                        ' full-scale-noise report anyway).
                        _logger.Info("[session] Audio sidecar started (Device clock: WasapiPositionCapture + AudioTapDeviceClock; keep-alive not required)")
                    Else
                        ' ── LEGACY PATH (proven AudioTap v2, unchanged) ─────
                        audioCapture = New WasapiLoopbackCapture()
                        waveFormat = audioCapture.WaveFormat
                    _logger.Info($"[session] Audio: {waveFormat.Channels}ch {waveFormat.SampleRate}Hz {waveFormat.BitsPerSample}bit {waveFormat.Encoding}")

                    ' WavSidecarWriter is PCM (16/24/32). WASAPI loopback on
                    ' shared mode is typically IeeeFloat 32-bit → convert to
                    ' PCM16 here so the sidecar stays a plain PCM WAV.
                    Dim srcFloat As Boolean = (waveFormat.Encoding = WaveFormatEncoding.IeeeFloat)
                    Dim sidecarBits As Integer = If(srcFloat, 32, waveFormat.BitsPerSample)
                    ' NOTE: 32-bit int PCM is NOT the same as float — WavSidecarWriter
                    ' takes raw frames; for float sources we convert below and keep 16-bit.
                    If srcFloat Then sidecarBits = 16

                    If _config.EvidenceSidecar Then
                        wavWriter = New WavSidecarWriter(tempWav, waveFormat.Channels, waveFormat.SampleRate, sidecarBits)
                        wavWriter.Start()
                    Else
                        _logger.Info("[session] evidence WAV sidecar disabled (live-mux is the output)")
                    End If

                    ' ★ WASAPI loopback gap-filling (real-bug fix, owner evidence
                    ' 2026-08-23 18:42: 16.28s session but WAV had only 3.54s —
                    '  loopback delivers callbacks ONLY while sound is actually
                    '  playing; silence = NO data at all. A naive byte-concat WAV
                    ' is 3.5s of sound taped together — the mux then places that
                    ' audio at the file start: 'music before I played any'.
                    '  Fix (same model as legacy AudioPipe pre-roll/gap logic):
                    '  track the last filled timestamp; when a callback arrives
                    '  after a gap, insert silence for the elapsed gap FIRST,
                    '  then the real audio. WAV duration == wall-clock duration,
                    '  so mux offsets hold for the entire file.
                    ' ★ AudioTap consolidation (GLM/6 audit): ONE gap-fill engine for
                    ' both tracks (previously duplicated logic). The tap receives
                    ' CONVERTED pcm16 bytes, feeds WAV sidecar + live-mux pipe with
                    ' one wall-clock timeline, measures the device's first-callback
                    ' warm-up, and closes the tail at stop. Lead = the systemic
                    ' audio-lead calibration (shifts the stream earlier by the
                    ' device-latency bias).
                    Dim sysTapBits As Integer = If(srcFloat, 16, waveFormat.BitsPerSample)
                    _sysTap = New AudioTap(
                        "sys",
                        waveFormat.SampleRate,
                        waveFormat.Channels,
                        sysTapBits,
                        New AudioTapSinkDual(wavWriter, AddressOf SysPipeFeed),
                        0.0,
                        Sub(m) _logger.Info("[session] " & m))

                    AddHandler audioCapture.DataAvailable, Sub(s, e)
                                                                If e.BytesRecorded > 0 Then
                                                                    If srcFloat Then
                                                                        Dim pcm As Byte() = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded)
                                                                        _sysTap.Feed(pcm, pcm.Length)
                                                                    Else
                                                                        _sysTap.Feed(e.Buffer, e.BytesRecorded)
                                                                    End If
                                                                End If
                                                                If _stopSignal OrElse sw.Elapsed >= duration Then
                                                                    Try : audioCapture.StopRecording() : Catch : End Try
                                                                End If
                                                            End Sub

                    AddHandler audioCapture.RecordingStopped, Sub(s, e) wavStoppedEvent.Set()

                    ' ★ Keep-alive BEFORE capture start: rendering silence now
                    ' means the loopback's FIRST callback arrives within ~10ms
                    ' (the mixer is already active) — the 5.7s warm-up gap and
                    ' its pre-roll reconstruction never happen.
                    If _config.SilenceKeepAlive Then
                        _silenceKeepAlive = New SilenceKeepAlive()
                        _logger.Info($"[session] silence keep-alive: {If(_silenceKeepAlive.IsActive, "ACTIVE on " & _silenceKeepAlive.DeviceName, "unavailable — tap gap-fill remains active")} (OBS-style continuous loopback)")
                    Else
                        _logger.Info("[session] silence keep-alive disabled by config — AudioTap gap-fill active (proven path)")
                    End If

                    ' PROVEN MODEL: capture the StartRecording CALL time —
                    ' NOT the first-callback time (historical -10s offset bug).
                    _sysTap.MarkStart()
                    audioCapture.StartRecording()
                    _systemStartTicks = Stopwatch.GetTimestamp()
                    _logger.Info("[session] Audio sidecar started (bounded queue + writer thread)")
                    End If ' device-clock vs legacy system-audio path
                    End If ' legacy audio migration bypass
                End If

                ' ★ M1: Start MIC sidecar (independent #2) ──────────────────
                ' Mirror of the system sidecar. Hard rules (GPT standing design):
                '   - own WasapiCapture (device-selected), own WavSidecarWriter
                '   - own start timestamp (micStartTicks) — never derived from
                '     system ticks; the two devices have independent clocks
                '   - failure to open the mic must NOT kill the session — the
                '     track is dropped and logged (system audio continues)
                If False AndAlso _config.MicEnabled Then
                    Try
                        _logger.Info("[session] Starting legacy mic sidecar (bypassed)...")
                        Dim micDevice As NAudio.CoreAudioApi.MMDevice = FindMicDevice(_config.MicDeviceId, _config.MicDeviceName)
                        micCapture = If(micDevice IsNot Nothing, New NAudio.CoreAudioApi.WasapiCapture(micDevice), New NAudio.CoreAudioApi.WasapiCapture())
                        micWaveFormat = micCapture.WaveFormat
                        _logger.Info($"[session] Mic: {micWaveFormat.Channels}ch {micWaveFormat.SampleRate}Hz {micWaveFormat.BitsPerSample}bit {micWaveFormat.Encoding}")

                        Dim micSrcFloat As Boolean = (micWaveFormat.Encoding = WaveFormatEncoding.IeeeFloat)
                        Dim micBits As Integer = If(micSrcFloat, 16, micWaveFormat.BitsPerSample)

                        If _config.EvidenceSidecar Then
                            micWriter = New WavSidecarWriter(tempMicWav, micWaveFormat.Channels, micWaveFormat.SampleRate, micBits)
                            micWriter.Start()
                        End If

                        ' ★ AUDIT FIX consolidated via AudioTap (same engine as system)
                        _micTap = New AudioTap(
                            "mic",
                            micWaveFormat.SampleRate,
                            micWaveFormat.Channels,
                            micBits,
                            New AudioTapSinkDual(micWriter, AddressOf MicPipeFeed),
                            0.0,
                            Sub(m) _logger.Info("[session] " & m))

                        AddHandler micCapture.DataAvailable, Sub(s, e)
                                                                  If e.BytesRecorded > 0 Then
                                                                      If micSrcFloat Then
                                                                          Dim pcm As Byte() = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded)
                                                                          _micTap.Feed(pcm, pcm.Length)
                                                                      Else
                                                                          _micTap.Feed(e.Buffer, e.BytesRecorded)
                                                                      End If
                                                                  End If
                                                                  If _stopSignal OrElse sw.Elapsed >= duration Then
                                                                      Try : micCapture.StopRecording() : Catch : End Try
                                                                  End If
                                                              End Sub

                        AddHandler micCapture.RecordingStopped, Sub(s, e) micStoppedEvent.Set()

                        _micTap.MarkStart()
                        micCapture.StartRecording()
                        _micStartTicks = Stopwatch.GetTimestamp()
                        _logger.Info("[session] Mic sidecar started (independent writer)")
                    Catch ex As Exception
                        ' Mic is a second track — its failure degrades, never kills.
                        _logger.Warning($"[session] Mic sidecar FAILED to start (track dropped): {ex.Message}")
                        Try : micCapture?.Dispose() : Catch : End Try
                        micCapture = Nothing
                        Try : micWriter?.Dispose() : Catch : End Try
                        micWriter = Nothing
                        _micStartTicks = 0
                    End Try
                End If

                ' ─── 2b. ★ OBS-style LIVE MUX: one FFmpeg, both streams, wall clock ──
                ' OWNER directive: record video+audio together, both follow the
                ' clock, both end together (replaces temp-H.264 + WAV + wrap + mux).
                '
                ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT1): the declared mux rate
                ' and the CFR pacing rate are the CONFIG FPS — the same value,
                ' from config.json Recording.current.fps (SessionConfig.TargetFps).
                ' BEFORE (HEAD ab89372): both read _capture.OutputRefreshRate
                ' (display refresh) — config FPS never reached the runtime.
                Dim targetFps As Integer = _config.TargetFps
                If targetFps <= 0 Then
                    targetFps = 60
                    _logger.Warning("[session] TargetFps missing/invalid in effective config — fallback 60fps (config FPS is the only valid source; display refresh is NEVER used)")
                End If
                _logger.Info($"[session] video: fps source = config.json Recording.current.fps = {targetFps} (display refresh {_capture.OutputRefreshRate}Hz is diagnostics-only)")
                _logger.Info($"[session] video: requested resolution = {If(_config.UseNativeResolution, $"native (use_native_resolution=true)", $"{_config.RequestedWidth}x{_config.RequestedHeight} (use_native_resolution=false)")} → capture {_capture.OutputWidth}x{_capture.OutputHeight} → encode {_config.EncodeWidth}x{_config.EncodeHeight}")

                Dim sysRate As Integer = 48000
                Dim sysCh As Integer = 2
                Dim micRate As Integer = 0
                Dim micCh As Integer = 0
                Dim hasSharedSys As Boolean = _audioEngine IsNot Nothing AndAlso _audioEngine.TryGetTrackFormat(AudioTrackKind.System, sysRate, sysCh)
                Dim hasSharedMic As Boolean = _audioEngine IsNot Nothing AndAlso _audioEngine.TryGetTrackFormat(AudioTrackKind.Microphone, micRate, micCh)
                If _config.AudioEnabled AndAlso Not hasSharedSys Then
                    _logger.Warning("[session] Shared Audio Engine has no System track — live mux will remain audio-input idle")
                End If
                If _config.MicEnabled AndAlso Not hasSharedMic Then
                    _logger.Warning("[session] Shared Audio Engine has no Microphone track — mic input disabled")
                    micRate = 0
                    micCh = 0
                End If

                _liveMux = New LiveMuxSession(
                    _config.FFmpegPath,
                    _config.OutputPath,
                    targetFps,
                    sysRate, sysCh,
                    micRate, micCh,
                    _config.MicSeparateTracks,
                    _config.SystemVolume,
                    _config.MicVolume,
                    Sub(m) _logger.Info(m),
                    _config.OnProcessStarted)   ' ★ F-07: job-assign every spawned ffmpeg
                If Not _liveMux.Start() Then
                    Throw New Exception("LiveMux failed to start (ffmpeg) — session aborted")
                End If
                _sysAudioEngineSink?.AttachMux(_liveMux)
                _micAudioEngineSink?.AttachMux(_liveMux)
                _logger.Info("[session] Shared Audio Engine sinks attached to LiveMux (audio PTS alignment handled upstream)")

                ' ─── 3. Arm video capture + encoder BEFORE common T0 ────
                _logger.Info("[session] Arming video capture + encoder before common T0...")
                ' ★ C-1: claim ownership BEFORE starting each backend. If a
                ' Start throws mid-way (thread OOM, logger fault) the Finally
                ' unwind still sees running=True and Stops the backend — the
                ' old flag-after-call order skipped the unwind and stranded a
                ' live capture worker. Stop is idempotent/state-guarded on
                ' both backends, so claiming early is always safe.
                encoderRunning = True
                _encoder.Start()
                captureRunning = True
                _capture.Start(sink)

                ' Audio/video mux timeline is already defined by the same T0.
                _audioEngine.SetVideoStartQpc100ns(_timelineStartQpc100ns)
                _sysAudioEngineSink?.SetVideoStart(_timelineStartQpc100ns)
                _micAudioEngineSink?.SetVideoStart(_timelineStartQpc100ns)
                _liveMux?.BeginTimelines(0.0, 0.0)
                _timelinesBegun = True
                _logger.Info("[session] Capture + Audio armed; common T0 committed to all timelines")

                ' ─── 4. (video bytes now stream into the live-mux pipe) ──

                ' ─── 5. CFR-paced capture/encode loop ────────────────
                ' ★ AUDIT FIX (owner: 'video still looks weird'). Full-path audit
                ' from zero found the core defect: DXGI Desktop Duplication only
                ' delivers frames when the screen CHANGES (0..refreshRate fps —
                ' inherently VFR). Raw H.264 cannot carry timestamps, so a
                ' constant-rate wrap of a VFR stream SMEARS content: a 10s static
                ' scene (≈5 frames) + 20s of motion (≈1500 frames) becomes
                ' 1505 uniformly-spaced frames — static compressed to ~0.1s,
                ' motion stretched to ~30s. No -r choice can fix that.
                '
                ' Fix = CFR pacing (what real ShadowPlay / OBS CFR / the legacy
                ' engine's DuplicateFrames do): pace encoding at a CONSTANT rate
                ' (display refresh). Each tick encodes the FRESHEST captured
                ' frame; when the screen was static (no new frame), re-encode the
                ' last frame → a tiny P-frame duplicate. Result: true CFR stream,
                ' exact wall-clock timing at every point, static scenes simply
                ' repeat frames like normal video. Re-encoding is safe: the
                ' encoder CopyResources the frame texture into its own texture
                ' on every Encode call.
                ' ★ PHASE 1 VIDEO RUNTIME WIRING (V-CT1): targetFps comes from
                ' the effective config (mapped above) — NOT from the display.
                ' The pre-wiring line was:
                '     Dim targetFps As Integer = _capture.OutputRefreshRate
                '     If targetFps <= 0 Then targetFps = 60
                Dim tickIntervalTicks As Long = CLng(Stopwatch.Frequency / targetFps)
                Dim nextTick As Long = _timelineStartTicks

                ' Phase 12c runtime telemetry.
                Dim selectedCount As Long = 0
                Dim selectedSeqLast As Long = -1
                Dim selectedSeqSkips As Long = 0
                Dim maxSelectedLag100ns As Long = 0
                Dim maxSourceGap100ns As Long = 0
                Dim totalEncodeTicks As Long = 0
                Dim maxEncodeTicks As Long = 0
                Dim maxTickLateTicks As Long = 0
                Dim lateTickCount As Long = 0
                Dim presentationTickCount As Long = 0
                Dim lastSelectedTs As Long = -1

                _logger.Info($"[session] Recording for {_config.DurationSeconds}s @ CFR {targetFps}fps...")

                Dim durationTicks As Long = CLng(duration.TotalSeconds * Stopwatch.Frequency)
                ' C/6: latched when the encoder transitions to Faulted mid-session —
                ' every further Encode can only throw from the state check, so the
                ' loop hands the session to the normal stop sequence (which still
                ' muxes what was encoded) instead of erroring once per tick.
                Dim encoderFaulted As Boolean = False
                Do While (Stopwatch.GetTimestamp() - _timelineStartTicks) < durationTicks AndAlso
                         Not Threading.Volatile.Read(_stopSignal) AndAlso
                         Not encoderFaulted
                    ' Pull only the next chronological source frame. Do NOT drain the whole
                    ' queue and keep the newest; that skips temporal history and can make
                    ' motion appear accelerated during capture bursts.
                    If pendingFrame Is Nothing Then
                        Dim far As FrameAcquisitionResult
                        If sink.TryTake(far) Then
                            result.FramesCaptured += 1
                            pendingFrame = far.Frame
                            pendingSeq = far.Sequence
                            
                            ' ★ FORENSIC INSTRUMENTATION: Queue dequeue timing for first 4 frames
                            If pendingSeq <= 4 Then
                                Dim dequeueTick As Long = Stopwatch.GetTimestamp()
                                Dim dequeueQpc As Long = Stopwatch.GetTimestamp() ' Using Stopwatch as QPC equivalent
                                _logger.Info($"CaptureSession: FRAME {pendingSeq} QUEUE DEQUEUE:")
                                _logger.Info($"  dequeueTick={dequeueTick}")
                                _logger.Info($"  dequeueQpc={dequeueQpc}")
                                _logger.Info($"  frame.Diagnostics.CaptureTimeTicks={pendingFrame.Diagnostics.CaptureTimeTicks}")
                            End If
                        End If
                    End If

                    ' The recording timeline has one owner: common T0 reserved before any
                    ' producer started. A frame arriving before T0 is only warm-up data.
                    If _videoStartTicks = 0 AndAlso Stopwatch.GetTimestamp() >= _timelineStartTicks Then
                        _videoStartTicks = _timelineStartTicks
                        _videoStartQpc100ns = _timelineStartQpc100ns
                    End If

                    Dim nowTicks As Long = Stopwatch.GetTimestamp()
                    If nowTicks >= nextTick Then
                        ' ── CFR catch-up: emit every presentation tick that is already due. ──
                        ' A synchronous NVENC encode can occasionally cross one or more
                        ' 120fps deadlines. Handling only one tick per outer iteration
                        ' permanently loses those presentation slots and pushes the deficit
                        ' into tail-fill. Bound each burst so Stop() remains responsive.
                        Dim catchUpCount As Integer = 0
                        Do
                            If nextTick >= _timelineStartTicks + durationTicks Then Exit Do

                            Dim targetQpc100ns As Long = _timelineStartQpc100ns +
                                (CLng(Math.Max(0L, nextTick - _timelineStartTicks)) * 10000000L \ Stopwatch.Frequency)
                            Dim selectedFrame As IVideoFrame = Nothing
                            presentationTickCount += 1

                            ' ★ FORENSIC INSTRUMENTATION: CFR frame selection timing for first 4 presentation ticks
                            If presentationTickCount <= 4 Then
                                Dim cfrTick As Long = Stopwatch.GetTimestamp()
                                Dim cfrQpc As Long = Stopwatch.GetTimestamp() ' Using Stopwatch as QPC equivalent
                                _logger.Info($"CaptureSession: CFR TICK {presentationTickCount} SELECTION DEBUG:")
                                _logger.Info($"  targetQpc100ns={targetQpc100ns}")
                                _logger.Info($"  timelineStartQpc100ns={_timelineStartQpc100ns}")
                                _logger.Info($"  nextTick={nextTick}")
                                _logger.Info($"  _timelineStartTicks={_timelineStartTicks}")
                                _logger.Info($"  cfrTick={cfrTick}")
                                _logger.Info($"  cfrQpc={cfrQpc}")
                            End If

                            While pendingFrame IsNot Nothing AndAlso
                                  pendingFrame.Diagnostics.CaptureTimeTicks <= targetQpc100ns
                                ' Keep only the newest eligible source frame. The
                                ' previously selected frame is obsolete now and must
                                ' be disposed immediately; D3D11VideoFrame has no finalizer.
                                If selectedFrame IsNot Nothing Then
                                    frameDisposer.Enqueue(selectedFrame)
                                End If
                                Dim selectedSeq As Long = pendingSeq
                                If selectedSeqLast >= 0 AndAlso selectedSeq > selectedSeqLast + 1 Then selectedSeqSkips += selectedSeq - selectedSeqLast - 1
                                If selectedSeq >= 0 Then selectedSeqLast = selectedSeq
                                selectedCount += 1
                                Dim selectedTs As Long = pendingFrame.Diagnostics.CaptureTimeTicks
                                Dim selectedLag As Long = targetQpc100ns - selectedTs
                                If selectedLag > maxSelectedLag100ns Then maxSelectedLag100ns = selectedLag
                                If lastSelectedTs >= 0 AndAlso selectedTs >= lastSelectedTs Then
                                    Dim sourceGap As Long = selectedTs - lastSelectedTs
                                    If sourceGap > maxSourceGap100ns Then maxSourceGap100ns = sourceGap
                                End If
                                lastSelectedTs = selectedTs
                                selectedFrame = pendingFrame
                                pendingFrame = Nothing
                                pendingSeq = -1

                                Dim nextSource As FrameAcquisitionResult
                                If sink.TryTake(nextSource) Then
                                    result.FramesCaptured += 1
                                    pendingFrame = nextSource.Frame
                                    pendingSeq = nextSource.Sequence
                                End If
                                
                                ' ★ FORENSIC INSTRUMENTATION: Selected frame timing for first 4 presentation ticks
                                If presentationTickCount <= 4 Then
                                    Dim selectedTick As Long = Stopwatch.GetTimestamp()
                                    Dim selectedQpc As Long = Stopwatch.GetTimestamp() ' Using Stopwatch as QPC equivalent
                                    _logger.Info($"CaptureSession: CFR TICK {presentationTickCount} SELECTED FRAME:")
                                    _logger.Info($"  selectedSeq={selectedSeq}")
                                    _logger.Info($"  selectedTs={selectedTs}")
                                    _logger.Info($"  selectedLag={selectedLag}ns")
                                    _logger.Info($"  selectedTick={selectedTick}")
                                    _logger.Info($"  selectedQpc={selectedQpc}")
                                End If
                            End While

                            If selectedFrame IsNot Nothing Then
                                If lastFrame IsNot Nothing Then
                                    frameDisposer.Enqueue(lastFrame)
                                End If
                                lastFrame = selectedFrame
                            End If

                            Dim encodeFrame As IVideoFrame = lastFrame

                            Dim isDuplicate As Boolean = (selectedFrame Is Nothing AndAlso lastFrame IsNot Nothing)
                            Dim encodeTicks As Long = 0
                            If encodeFrame IsNot Nothing Then
                                Dim packet As EncodedPacket = Nothing
                                Dim encodeStartTicks As Long = Stopwatch.GetTimestamp()
                                Try
                                    If _encoder.Encode(encodeFrame, packet) AndAlso packet IsNot Nothing Then
                                        ' ★ FORENSIC INSTRUMENTATION: Muxer feed timing for first 4 presentation ticks
                                        If presentationTickCount <= 4 Then
                                            Dim muxFeedTick As Long = Stopwatch.GetTimestamp()
                                            Dim muxFeedQpc As Long = Stopwatch.GetTimestamp() ' Using Stopwatch as QPC equivalent
                                            _logger.Info($"CaptureSession: CFR TICK {presentationTickCount} MUXER FEED:")
                                            _logger.Info($"  muxFeedTick={muxFeedTick}")
                                            _logger.Info($"  muxFeedQpc={muxFeedQpc}")
                                            _logger.Info($"  packet.Metadata.PresentationTimestampTicks={packet.Metadata.PresentationTimestampTicks}")
                                            _logger.Info($"  packet.Metadata.Sequence={packet.Metadata.Sequence}")
                                        End If
                                        
                                        _liveMux?.FeedVideo(packet.Payload, packet.PayloadLength)
                                        result.TotalVideoBytes += packet.PayloadLength
                                        result.FramesEncoded += 1
                                        packet.Dispose()
                                    End If
                                Catch ex As Exception
                                    result.NvencErrors += 1
                                    _logger.Error($"[session] Encode error: {ex.Message}")
                                    ' C/6 containment: once the encoder has faulted, every
                                    ' further tick can only throw again from its state check —
                                    ' stop burning the remaining presentation ticks and hand
                                    ' the session to the normal stop sequence, which still
                                    ' muxes what was encoded. Tail-fill already breaks on error.
                                    If _encoder.CurrentState = EncoderState.Faulted Then
                                        _logger.Error("[session] Encoder Faulted — aborting CFR loop")
                                        encoderFaulted = True
                                        Exit Do
                                    End If
                                End Try

                                encodeTicks = Stopwatch.GetTimestamp() - encodeStartTicks
                                totalEncodeTicks += encodeTicks
                                If encodeTicks > maxEncodeTicks Then maxEncodeTicks = encodeTicks

                                If isDuplicate Then
                                    result.FramesDuplicated += 1
                                End If
                            End If

                            Dim tickDoneTicks As Long = Stopwatch.GetTimestamp()
                            Dim tickLateTicks As Long = tickDoneTicks - nextTick
                            If tickLateTicks > maxTickLateTicks Then maxTickLateTicks = tickLateTicks
                            If tickLateTicks > tickIntervalTicks Then lateTickCount += 1

                            nextTick += tickIntervalTicks
                            catchUpCount += 1
                            nowTicks = Stopwatch.GetTimestamp()
                        Loop While catchUpCount < 8 AndAlso
                                   nextTick <= nowTicks AndAlso
                                   Not Threading.Volatile.Read(_stopSignal)

                    Else
                        ' Sleep toward the next tick (keep ~2ms spin margin for
                        ' Stop() responsiveness and tick precision).
                        ' ★ AUDIT FIX: at 240fps (4.17ms interval) waitMs is
                        ' ALWAYS < 2.5 → Thread.Sleep never fires → a full CPU
                        ' core spins at 100%. Use SpinWait for the sub-2.5ms
                        ' remainder instead of a hot loop.
                        Dim waitMs As Double = (nextTick - nowTicks) * 1000.0 / Stopwatch.Frequency
                        ' Do not sleep for the whole remaining interval. Windows may
                        ' overshoot a multi-ms Sleep even after timeBeginPeriod(1),
                        ' which turns a 120fps target into ~80-90fps pacing.
                        ' Use short 1ms scheduler quanta and re-check the deadline.
                        ' The final sub-ms remainder stays on SpinWait for precision.
                        If waitMs > 1.0 Then
                            Thread.Sleep(1)
                        ElseIf waitMs > 0.05 Then
                            Thread.SpinWait(200)
                        End If
                    End If
                Loop

                ' ─── 6. Stop video → final fresh frame → stop encoder ──
                ' ─── 6. Freeze session stop time BEFORE any drain/encode work ──
                ' Everything downstream must use this immutable stop snapshot.
                ' Tail-fill and encoder shutdown can take seconds and must never
                ' extend the audio/video timeline after the user pressed Stop.
                ' Use the exact user Stop() request timestamp when one exists.
                ' Natural duration expiry has no latched request, so sample QPC now.
                Dim stopQpcTicks As Long = Interlocked.Read(_stopRequestedTicks)
                If stopQpcTicks <= 0 Then stopQpcTicks = Stopwatch.GetTimestamp()
                Dim stopElapsedSeconds As Double = Math.Min(duration.TotalSeconds, Math.Max(0.0, (stopQpcTicks - _timelineStartTicks) / CDbl(Stopwatch.Frequency)))

                _logger.Info($"[session] Stop snapshot: elapsed={stopElapsedSeconds:F3}s")
                _logger.Info("[session] Stopping video capture...")
                _capture.Stop()
                captureRunning = False   ' ★ M1: success path owns the stop — Finally must not re-stop
                ' Capture-path evidence: distinguish real DXGI no-update periods,
                ' handoff backpressure, and backend errors from CFR duplication.
                _logger.Info($"[session] capture diagnostics: emitted={_capture.EmittedFrames}, pushed={_capture.FramesPushed}, dropped={_capture.DroppedFrames}, replaced={_capture.ReplacedFrames}, noFrame={_capture.NoFrameCount}, errors={_capture.ErrorCount}, accessLost={_capture.AccessLostCount}, textures={_capture.TexturesCreated}/{_capture.TexturesDisposed}")

                Dim avgEncodeMs As Double = If(result.FramesEncoded > 0, totalEncodeTicks * 1000.0 / Stopwatch.Frequency / result.FramesEncoded, 0.0)
                Dim maxEncodeMs As Double = maxEncodeTicks * 1000.0 / Stopwatch.Frequency
                Dim maxSelectedLagMs As Double = maxSelectedLag100ns / 10000.0
                Dim maxSourceGapMs As Double = maxSourceGap100ns / 10000.0
                Dim maxTickLateMs As Double = maxTickLateTicks * 1000.0 / Stopwatch.Frequency
                _logger.Info($"[session] CFR telemetry: ticks={presentationTickCount}, selectedSources={selectedCount}, seqSkips={selectedSeqSkips}, maxSelectedLag={maxSelectedLagMs:0.###}ms, maxSourceGap={maxSourceGapMs:0.###}ms, avgEncode={avgEncodeMs:0.###}ms, maxEncode={maxEncodeMs:0.###}ms, lateTicks>{1000.0 * tickIntervalTicks / Stopwatch.Frequency:0.###}ms={lateTickCount}, maxTickLate={maxTickLateMs:0.###}ms")

                ' Drain: keep only the FRESHEST leftover frame (stale ones just
                ' dispose — the CFR stream already displayed newer states).
                Dim farDrain As FrameAcquisitionResult
                While sink.TryTake(farDrain)
                    result.FramesCaptured += 1
                    Dim f As IVideoFrame = farDrain.Frame
                    If f Is Nothing Then Continue While
                    If farDrain.Sequence > pendingSeq Then
                        frameDisposer.Enqueue(pendingFrame)
                        pendingFrame = f
                        pendingSeq = farDrain.Sequence
                    Else
                        frameDisposer.Enqueue(f)
                    End If
                End While

                ' Encode the final fresh frame (if any), then tail-fill the
                ' CFR timeline to the configured session duration. A real encode
                ' can occasionally finish just below target FPS (for example
                ' 594/600 frames); ending with only those frames shortens video
                ' time while audio continues. Repeating the last frame for the
                ' missing presentation slots preserves wall-clock duration.
                Dim finalFrame As IVideoFrame = pendingFrame
                If finalFrame Is Nothing Then finalFrame = lastFrame


                If finalFrame IsNot Nothing Then
                    If pendingFrame IsNot Nothing Then
                        Dim packetF As EncodedPacket = Nothing
                        Try
                            If _encoder.Encode(pendingFrame, packetF) AndAlso packetF IsNot Nothing Then
                                _liveMux?.FeedVideo(packetF.Payload, packetF.PayloadLength)
                                result.TotalVideoBytes += packetF.PayloadLength
                                result.FramesEncoded += 1
                                packetF.Dispose()

                                ' Transfer ownership before tail-fill; finalFrame
                                ' must never point at a disposed pending frame.
                                frameDisposer.Enqueue(lastFrame)
                                lastFrame = pendingFrame
                                pendingFrame = Nothing
                                finalFrame = lastFrame
                            End If
                        Catch
                            result.NvencErrors += 1
                        End Try
                    End If

                    Dim actualSessionSeconds As Double = stopElapsedSeconds
                    Dim targetFrames As Long = CLng(Math.Ceiling(actualSessionSeconds * targetFps))
                    Dim fillBefore As Long = result.FramesEncoded
                    While result.FramesEncoded < targetFrames AndAlso finalFrame IsNot Nothing
                        Dim packetPad As EncodedPacket = Nothing
                        Try
                            If Not _encoder.Encode(finalFrame, packetPad) OrElse packetPad Is Nothing Then
                                result.NvencErrors += 1
                                Exit While
                            End If
                            _liveMux?.FeedVideo(packetPad.Payload, packetPad.PayloadLength)
                            result.TotalVideoBytes += packetPad.PayloadLength
                            result.FramesEncoded += 1
                            result.FramesDuplicated += 1
                            packetPad.Dispose()
                        Catch
                            result.NvencErrors += 1
                            Exit While
                        End Try
                    End While
                    If result.FramesEncoded > fillBefore Then
                        _logger.Info($"[session] CFR tail-fill: +{result.FramesEncoded - fillBefore} frames (target {targetFrames})")
                    End If
                End If
                ' ★ F-05 (C/6 evidence): retire the retained frames EXACTLY
                ' ONCE and in the right order. pendingFrame still owns a
                ' D3D11 texture when the final fresh-frame encode threw above
                ' (the Try transfers ownership on success only), so the guard
                ' MUST run before the Nothing assignments — a premature
                ' `pendingFrame = Nothing` turned the guard into dead code and
                ' stranded one staging texture per failure (D3D11VideoFrame
                ' has no finalizer).
                If pendingFrame IsNot Nothing AndAlso pendingFrame IsNot lastFrame Then
                    frameDisposer.Enqueue(pendingFrame)
                End If
                frameDisposer.Enqueue(lastFrame)
                pendingFrame = Nothing   ' ownership handed to the disposer
                lastFrame = Nothing
                frameDisposer.CompleteAndWait()

                _logger.Info("[session] GPU frame disposer drained; stopping encoder...")
                _encoder.Stop()
                encoderRunning = False   ' ★ M1: success path owns the stop — Finally must not re-stop

                ' ─── 8. Stop shared Audio Engine ───────────────────────
                _stopSignal = True
                If _audioEngine IsNot Nothing Then
                    Try
                        _audioEngine.Stop(WasapiPositionCapture.StopwatchTicksTo100ns(stopQpcTicks))
                    Catch ex As Exception
                        _logger.Warning("[session] Shared Audio Engine stop failed: " & ex.Message)
                    End Try
                    _logger.Info(_audioEngine.Diagnostics.ToString())
                    Dim sysDiag = _audioEngine.Diagnostics.Tracks.Find(Function(t) t.Track = AudioTrackKind.System)
                    If sysDiag IsNot Nothing Then
                        result.AudioBytes = sysDiag.DataBytes + sysDiag.SilenceBytes
                        result.AudioSamples = If(sysDiag.Channels > 0, result.AudioBytes \ (sysDiag.Channels * 2), 0)
                        ' ★ Sink-drop accounting: pending-cap discards in the
                        ' mux sink (pre-alignment window) are counted by the
                        ' sink itself — fold them into the honest drop total.
                        result.AudioDroppedBytes = sysDiag.DroppedBytes +
                            If(_sysAudioEngineSink IsNot Nothing, _sysAudioEngineSink.PendingDroppedBytes, 0)
                        result.AudioAccountingOk = (result.AudioDroppedBytes = 0)
                    End If
                    Dim micDiag = _audioEngine.Diagnostics.Tracks.Find(Function(t) t.Track = AudioTrackKind.Microphone)
                    If micDiag IsNot Nothing Then
                        result.MicBytes = micDiag.DataBytes + micDiag.SilenceBytes
                        result.MicSamples = If(micDiag.Channels > 0, result.MicBytes \ (micDiag.Channels * 2), 0)
                        result.MicDroppedBytes = micDiag.DroppedBytes +
                            If(_micAudioEngineSink IsNot Nothing, _micAudioEngineSink.PendingDroppedBytes, 0)
                        result.MicAccountingOk = (result.MicDroppedBytes = 0)
                    End If
                End If

                ' ─── 8b. Legacy audio path retained but bypassed ─────────
                If False AndAlso (_deviceClockSys OrElse audioCapture IsNot Nothing) Then
                    _logger.Info("[session] Stopping audio sidecar...")
                    _stopSignal = True

                    If _deviceClockSys Then
                        ' v3: Stop() joins the capture thread (2s cap) — after
                        ' it returns no more packets race the finalize below.
                        Try : _sysPosCapture?.Stop() : Catch : End Try
                    Else
                        Try : audioCapture.StopRecording() : Catch : End Try

                        ' Event-driven wait — replaces the old Thread.Sleep(500)
                        ' race. WASAPI fires RecordingStopped exactly once.
                        If Not wavStoppedEvent.WaitOne(3000) Then
                            _logger.Warning("[session] RecordingStopped event timeout (3s) — finalizing anyway")
                        End If
                    End If

                    ' Stop the keep-alive FIRST (stop rendering silence), then
                    ' finalize the tap (the tail-pad still covers the last ~ms).
                    ' (Legacy path only — the Device-clock path never creates a
                    ' keep-alive: Model S proved the endpoint renders silence
                    ' through quiet phases on its own.)
                    Try
                        If _silenceKeepAlive IsNot Nothing Then
                            _silenceKeepAlive.Dispose()
                            _silenceKeepAlive = Nothing
                        End If
                    Catch
                    End Try

                    ' ★ The tap closes the timeline: tail-pad from the last
                    ' packet to session end, or the FULL span if nothing ever
                    ' arrived (fully silent session — else ffmpeg gets zero
                    ' packets for this input and aborts).
                    If _deviceClockSys AndAlso _sysTap3 IsNot Nothing Then
                        ' v3: exact QPC tail — pad to the session-end stamp,
                        ' no wall-clock estimation, no FinalizeToNow guess.
                        _sysTap3.FinalizeTo100ns(_sessionStartQpc100ns,
                            WasapiPositionCapture.StopwatchTicksTo100ns(stopQpcTicks))
                    Else
                        _sysTap?.FinalizeToTicks(stopQpcTicks)
                    End If

                    If wavWriter IsNot Nothing Then
                        wavReport = wavWriter.Complete(5000)
                        _logger.Info("[session] " & wavReport.ToString())

                        result.AudioBytes = wavReport.BytesWritten
                        result.AudioDroppedBytes = wavReport.BytesDropped
                        result.AudioAccountingOk = wavReport.AccountingOk
                        If _deviceClockSys AndAlso _sysPosCapture IsNot Nothing Then
                            ' v3 sidecar is always PCM16 in the device's layout.
                            result.AudioSamples = wavReport.BytesWritten \ (Math.Max(1, _sysPosCapture.Channels) * 2)
                        Else
                            result.AudioSamples = If(waveFormat IsNot Nothing,
                                                     wavReport.BytesWritten \ (waveFormat.Channels * waveFormat.BitsPerSample \ 8),
                                                     0)
                        End If
                    Else
                        ' Evidence disabled: report the tap totals instead.
                        result.AudioBytes = If(_sysTap3 IsNot Nothing, _sysTap3.DataBytes,
                                               If(_sysTap IsNot Nothing, _sysTap.DataBytes, 0))
                        result.AudioAccountingOk = True
                    End If
                End If

                ' ★ M1: stop + finalize mic sidecar (independent #2) ──────
                If micCapture IsNot Nothing Then
                    _logger.Info("[session] Stopping mic sidecar...")
                    Try : micCapture.StopRecording() : Catch : End Try

                    If Not micStoppedEvent.WaitOne(3000) Then
                        _logger.Warning("[session] Mic RecordingStopped event timeout (3s) — finalizing anyway")
                    End If

                    _micTap?.FinalizeToTicks(stopQpcTicks)

                    If micWriter IsNot Nothing Then
                        micReport = micWriter.Complete(5000)
                        _logger.Info("[session] Mic " & micReport.ToString())

                        result.MicBytes = micReport.BytesWritten
                        result.MicDroppedBytes = micReport.BytesDropped
                        result.MicAccountingOk = micReport.AccountingOk
                        result.MicSamples = If(micWaveFormat IsNot Nothing,
                                               micReport.BytesWritten \ (micWaveFormat.Channels * micWaveFormat.BitsPerSample \ 8),
                                               0)
                    Else
                        result.MicBytes = If(_micTap IsNot Nothing, _micTap.DataBytes, 0)
                        result.MicAccountingOk = True
                    End If
                End If

                result.ActualDurationSec = stopElapsedSeconds

                ' ★ OBS-model: finalize the LIVE MUX (drain pipes → FFmpeg EOF
                ' → finalize fragmented MP4 → +faststart remux). The declared CFR
                ' rate and the wall-clock audio feed already define the timeline;
                ' no wrap step, no post-hoc offsets.
                ' WrapFps evidence = the CONFIG fps the session actually ran at
                ' (pacing + mux declared rate), no longer the display rate.
                result.WrapFps = targetFps
                Dim liveRes As LiveMuxResult = _liveMux.[Stop](30000)
                _logger.Info("[session] " & liveRes.ToString())

                ' ★ 02:41 FIX (honest accounting): surface mux-layer drops into
                ' the session result — Pass now fails when the pipe layer lost
                ' bytes. (Clap-sync evidence: sidecar counters reported
                ' dropped=0 while the mux threw away 1,530,240B ≈ 8s of tail
                ' audio; pass=True hid the loss for years.)
                result.MuxDroppedBytes = liveRes.DroppedBytes
                If liveRes.DroppedBytes > 0 Then
                    _logger.Warning($"[session] live-mux dropped {liveRes.DroppedBytes:N0}B — file is missing captured audio (pass will report False)")
                End If

                ' ─── 10. Probe final MP4 duration (evidence) ─────────────
                Dim probePsi As New ProcessStartInfo With {
                    .FileName = _config.FFmpegPath,
                    .Arguments = "-hide_banner -i """ & _config.OutputPath & """",
                    .UseShellExecute = False,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }
                Try
                    Using probeProc As Process = Process.Start(probePsi)
                        ' Bounded probe: ReadToEnd would never return on a hung
                        ' ffmpeg (EOF only arrives at process exit), so the read
                        ' is async and the process is killed on timeout — the
                        ' same pattern the legacy engine uses for ffprobe.
                        Dim probeErrTask As System.Threading.Tasks.Task(Of String) = probeProc.StandardError.ReadToEndAsync()
                        If Not probeProc.WaitForExit(5000) Then
                            Try : probeProc.Kill() : probeProc.WaitForExit(2000) : Catch : End Try
                        End If
                        Dim probeErr As String = If(probeErrTask.Wait(1000), probeErrTask.Result, "")
                        Dim m As System.Text.RegularExpressions.Match =
                            System.Text.RegularExpressions.Regex.Match(probeErr, "Duration:\s*(\d+):(\d+):(\d+\.?\d*)")
                        If m.Success Then
                            result.MuxVideoDurationSec =
                                CInt(m.Groups(1).Value) * 3600 + CInt(m.Groups(2).Value) * 60 + CDbl(m.Groups(3).Value)
                        End If
                    End Using
                Catch
                End Try

                ' Evidence: the system audio offset that the live feed ACTUALLY
                ' applied — the v2 anchor value on the Device path (whatever
                ' BeginTimelinesOnce computed), stopwatch estimate otherwise.
                Dim systemOffset As Double
                If _deviceClockSys AndAlso Not Double.IsNaN(_appliedSysOffsetSec) Then
                    systemOffset = _appliedSysOffsetSec
                Else
                    systemOffset = SyncMath.ComputeAudioOffsetSec(
                        _videoStartTicks, _systemStartTicks, Stopwatch.Frequency)
                End If
                result.SystemOffsetSec = systemOffset
                Dim micOffset As Double = SyncMath.ComputeAudioOffsetSec(
                    _videoStartTicks, _micStartTicks, Stopwatch.Frequency)
                result.MicOffsetSec = micOffset

                ' Clean the evidence WAV on success (keep on failure for debug)
                If liveRes.Succeeded Then
                    Try : File.Delete(tempWav) : Catch : End Try
                End If


                ' ─── 13. Verify MP4 ─────────────────────────────────────
                Dim fi As New FileInfo(_config.OutputPath)
                result.FileExists = fi.Exists
                result.FileSize = If(fi.Exists, fi.Length, 0)

                If fi.Exists Then
                    Dim verifyPsi As New ProcessStartInfo With {
                        .FileName = _config.FFmpegPath,
                        .Arguments = $"-hide_banner -i ""{_config.OutputPath}""",
                        .UseShellExecute = False,
                        .RedirectStandardError = True,
                        .CreateNoWindow = True
                    }
                    Try
                        Using verifyProc As Process = Process.Start(verifyPsi)
                            Try : _config.OnProcessStarted?.Invoke(verifyProc) : Catch : End Try
                            ' Bounded probe (same pattern as the duration probe
                            ' above): async read + kill on timeout — a hung verify
                            ' ffmpeg must never wedge the stop path or outlive
                            ' the session.
                            Dim verifyErrTask As System.Threading.Tasks.Task(Of String) = verifyProc.StandardError.ReadToEndAsync()
                            If Not verifyProc.WaitForExit(5000) Then
                                Try : verifyProc.Kill() : verifyProc.WaitForExit(2000) : Catch : End Try
                            End If
                            Dim stderr As String = If(verifyErrTask.Wait(1000), verifyErrTask.Result, "")
                            result.VideoStreamFound = stderr.Contains("Stream #") AndAlso stderr.Contains("Video:")
                            result.AudioStreamFound = stderr.Contains("Stream #") AndAlso stderr.Contains("Audio:")
                        End Using
                    Catch
                    End Try
                End If

                _logger.Info($"[session] Result: pass={result.Pass}, frames={result.FramesEncoded} " &
                             $"(captured={result.FramesCaptured}, dup={result.FramesDuplicated}), " &
                             $"video_bytes={result.TotalVideoBytes}, audio_bytes={result.AudioBytes}, " &
                             $"dropped={result.AudioDroppedBytes}, offset={result.SystemOffsetSec:0.000}s, " &
                             $"cfrFps={result.WrapFps:0.###}, duration={result.MuxVideoDurationSec:0.000}s, " &
                             $"file_size={result.FileSize}")

            Catch ex As Exception
                result.ErrorMessage = ex.Message
                _logger.Error($"[session] Failed: {ex.Message}", ex)
            Finally
                Try : timeEndPeriod(1UI) : Catch : End Try
                ' ★ M1: unwind the session-owned video lifecycle on EVERY exit
                ' path. A failure after _capture.Start()/_encoder.Start() used
                ' to leave the capture worker running (owner-less frames forever)
                ' and the encoder Running — the next session's Start() then
                ' no-op'd and skipped the FORCEIDR/SPS-PPS re-arm. Idempotent:
                ' on the success path the flags are already False (inline stops
                ' own the transition), so this is a no-op there.
                If captureRunning Then
                    Try : _capture.Stop() : Catch ex As Exception
                        _logger.Warning("[session] capture stop during failure unwind: " & ex.Message)
                    End Try
                End If
                If encoderRunning Then
                    Try : _encoder.Stop() : Catch ex As Exception
                        _logger.Warning("[session] encoder stop during failure unwind: " & ex.Message)
                    End Try
                End If
                Try : pendingFrame?.Dispose() : Catch : End Try
                Try : lastFrame?.Dispose() : Catch : End Try
                ' ★ Hardening: the shared Audio Engine is session-owned. The
                ' success path already called Stop() above (idempotent — the
                ' _stopped guard makes this a no-op there), but ANY failure
                ' before that point (LiveMux start failure, capture arm
                ' failure, exception) used to skip Stop entirely — leaving
                ' the WASAPI capture threads + open endpoint streams alive
                ' for the lifetime of the process (one leak per failed
                ' session in a long-lived host).
                Try : _audioEngine?.Stop(WasapiPositionCapture.StopwatchTicksTo100ns(Stopwatch.GetTimestamp())) : Catch : End Try
                Try : _audioEngine?.Dispose() : Catch : End Try
                Try : _sysAudioEngineSink = Nothing : Catch : End Try
                Try : _micAudioEngineSink = Nothing : Catch : End Try
                Try : _silenceKeepAlive?.Dispose() : Catch : End Try
                Try : _liveMux?.Dispose() : Catch : End Try
                Try : wavWriter?.Dispose() : Catch : End Try
                Try : audioCapture?.Dispose() : Catch : End Try
                ' ★ M1: mic sidecar cleanup mirrors the system sidecar
                Try : micWriter?.Dispose() : Catch : End Try
                Try : micCapture?.Dispose() : Catch : End Try
                Try : frameDisposer?.Dispose() : Catch : End Try
                Try : sink?.Dispose() : Catch : End Try
                Try : wavStoppedEvent.Dispose() : Catch : End Try
                Try : micStoppedEvent.Dispose() : Catch : End Try
            End Try

            Return result
        End Function

        ''' <summary>
        ''' ★ M1: resolve the microphone MMDevice (id first, then FriendlyName).
        ''' Ported from the PROVEN legacy AudioFileWriter.FindMicDevice — same
        ''' resolution order (exact ID match → exact FriendlyName match → Nothing).
        ''' Nothing = caller falls back to the default capture device.
        ''' </summary>
        Private Shared Function FindMicDevice(deviceId As String, deviceName As String) As NAudio.CoreAudioApi.MMDevice
            Using devEnum As New NAudio.CoreAudioApi.MMDeviceEnumerator()
                Dim devices As NAudio.CoreAudioApi.MMDeviceCollection =
                    devEnum.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture,
                                                    NAudio.CoreAudioApi.DeviceState.Active)

                If Not String.IsNullOrEmpty(deviceId) Then
                    For Each dev As NAudio.CoreAudioApi.MMDevice In devices
                        If dev.ID = deviceId Then Return dev
                    Next
                End If

                If Not String.IsNullOrEmpty(deviceName) Then
                    For Each dev As NAudio.CoreAudioApi.MMDevice In devices
                        If String.Equals(dev.FriendlyName, deviceName, StringComparison.Ordinal) Then Return dev
                    Next
                End If

                Return Nothing
            End Using
        End Function

        ''' <summary>
        ''' Convert IEEE-float32 interleaved PCM into signed 16-bit PCM.
        ''' Allocation per chunk is acceptable: chunks are ~10ms and the copy
        ''' already happens once (buffer ownership rule).
        ''' </summary>
        Private Shared Function ConvertFloatToPcm16(buffer As Byte(), bytesRecorded As Integer) As Byte()
            Dim sampleCount As Integer = bytesRecorded \ 4
            Dim out(sampleCount * 2 - 1) As Byte
            For i As Integer = 0 To sampleCount - 1
                Dim f As Single = BitConverter.ToSingle(buffer, i * 4)
                If Single.IsNaN(f) OrElse Single.IsInfinity(f) Then f = 0.0F
                If f > 1.0F Then f = 1.0F
                If f < -1.0F Then f = -1.0F
                Dim v As Integer = CInt(Math.Round(f * 32767.0F))
                If v > 32767 Then v = 32767
                If v < -32768 Then v = -32768
                Dim u As UShort = CUShort(v And &HFFFFI)
                out(i * 2) = CByte(u And &HFFUI)
                out(i * 2 + 1) = CByte((u >> 8) And &HFFUI)
            Next
            Return out
        End Function

        Public Sub [Stop]()
            Interlocked.CompareExchange(_stopRequestedTicks, Stopwatch.GetTimestamp(), 0)
            Threading.Volatile.Write(_stopSignal, True)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _stopSignal = True
            ' P13.3: the Device-clock capture is session-owned — release it
            ' here so an abnormal teardown cannot leave the WASAPI stream open.
            Try : _sysPosCapture?.Dispose() : Catch : End Try
            ' ★ Hardening: same session-ownership rule for the shared Audio
            ' Engine (idempotent — Stop is guarded, Dispose disposes tracks).
            Try : _audioEngine?.Dispose() : Catch : End Try
            ' NOTE: does NOT dispose _capture or _encoder — those are owned by RecordingEngine
        End Sub

    End Class

End Namespace


