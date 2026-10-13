Option Strict On
Option Explicit On
Option Infer On

' DTOs for CaptureEngine.Recording

Imports System.Diagnostics

Namespace CaptureEngine.Recording

    ''' <summary>
    ''' Recording engine state. Distinct from Foundation's EngineState
    ''' (which is the lifecycle state machine for CaptureEngine.vb).
    ''' </summary>
    Public Enum RecordingEngineState
        Created
        Initializing
        Idle
        Recording
        Stopping
        Faulted
        Disposed
    End Enum

    ''' <summary>
    ''' Configuration for a single recording session.
    ''' Per-session concerns only — codec/bitrate/GOP are process-lifetime
    ''' (persistent encoder owner) and live in EngineStartupConfig.
    ''' </summary>
    Public NotInheritable Class SessionConfig
        Public Property OutputPath As String = ""
        Public Property DurationSeconds As Integer = 30
        Public Property UseSharedHandle As Boolean = False

        ' ── P1-A DISK HEADROOM GUARD (W1) ──
        ''' <summary>
        ''' Test seam: override the derived required headroom (bytes) for the
        ''' pre-start disk check. 0 = derive from the session config (video
        ''' bitrate + audio streams × duration, ×2 for the faststart remux
        ''' peak where the .frag and the final file coexist on disk).
        ''' The derived value is the production default; this override exists
        ''' ONLY so low/near-zero headroom can be tested deterministically
        ''' without filling the real drive.
        ''' </summary>
        Public Property DiskHeadroomOverrideBytes As Long = 0

        ' ── P3-D AUDIO TRANSPORT (W1) ──
        ''' <summary>
        ''' Production audio transport selector (P3-C evidence):
        '''   True  = Sidecar/second-pass: engine PCM → WavSidecarWriter (direct
        '''           WAV on disk) → after stop, second-pass mux folds the WAV
        '''           into the final MP4. Bypasses the FFmpeg real-time audio
        '''           pipe whose consumption collapsed to ~60% real-time on
        '''           real-audio long runs (starved 6/6).
        '''   False = Legacy live-mix: engine PCM → FFmpeg audio pipe → amix
        '''           (kept as fallback; starves on real-audio long runs).
        ''' </summary>
        Public Property AudioSidecarMode As Boolean = True


        ' FFmpeg path (from EngineConfigV2.Runtime.FFmpegPath or CLI override)
        Public Property FFmpegPath As String = ""

        ' ── PHASE 1 VIDEO RUNTIME WIRING (V-CT1): per-session video values ──
        ''' <summary>
        ''' Target CFR rate for the session (config.json Recording.current.fps).
        ''' The ONLY FPS source for pacing + live-mux declared rate. Display
        ''' refresh rate is NEVER used as FPS (HEAD ab89372 bug:
        ''' CaptureSession.vb:489/:541 read _capture.OutputRefreshRate).
        ''' 0 = unset → CaptureSession falls back to 60 with a loud warning.
        ''' </summary>
        Public Property TargetFps As Integer = 0

        ' ── CAPTURE-REST-PLAN PHASE 3: per-session bitrate override ──
        ''' <summary>
        ''' Session bitrate override (bps). 0 = no override — the engine
        ''' startup bitrate (EngineStartupConfig.BitrateBps) stays the
        ''' authority. A positive value rebuilds the persistent NVENC
        ''' rate-control session before the first frame is submitted
        ''' (RecordingEngine.StartSession). Source: REST
        ''' GET /ShadowPlay/v.1.0/Record/Settings bitrateBps.
        ''' </summary>
        Public Property BitrateBps As Long = 0

        ' ── PHASE 1 VIDEO RUNTIME WIRING (V-CT2): per-session resolution
        '    evidence. The ENCODE dimensions are init-time (persistent NVENC
        '    session — EngineStartupConfig); these fields freeze what THIS
        '    session ran with so requested-vs-actual is always provable.
        ''' <summary>config.json Recording.current.use_native_resolution (as loaded at record start).</summary>
        Public Property UseNativeResolution As Boolean = True
        ''' <summary>config.json Recording.current.width (meaningful only when not native).</summary>
        Public Property RequestedWidth As Integer = 0
        ''' <summary>config.json Recording.current.height (meaningful only when not native).</summary>
        Public Property RequestedHeight As Integer = 0
        ''' <summary>Encode width the persistent encoder was initialized with (actual).</summary>
        Public Property EncodeWidth As Integer = 0
        ''' <summary>Encode height the persistent encoder was initialized with (actual).</summary>
        Public Property EncodeHeight As Integer = 0

        ' ── Phase 12b: audio per-session options ──
        ''' <summary>Record system-audio loopback into the WAV sidecar.</summary>
        Public Property AudioEnabled As Boolean = True
        ''' <summary>System-audio volume applied at mux (0..2, 1 = unchanged).</summary>
        Public Property SystemVolume As Single = 1.0F

        ' ── M1: microphone second track (two independent WavSidecarWriter
        '    instances — per GPT standing design: separate queue/writer/
        '    accounting/start-timestamp per track; NO merged queues) ──
        ''' <summary>Record the microphone into a second WAV sidecar.</summary>
        Public Property MicEnabled As Boolean = False
        ''' <summary>Mic volume applied at mux (0..2, 1 = unchanged).</summary>
        Public Property MicVolume As Single = 1.0F
        ''' <summary>Preferred mic device id (NAudio MMDevice ID). Empty = default capture device.</summary>
        Public Property MicDeviceId As String = ""
        ''' <summary>Preferred mic device name (fallback when the id does not match).</summary>
        Public Property MicDeviceName As String = ""
        ''' <summary>
        ''' Mux mode when BOTH system + mic are present: True = two separate output
        ''' tracks (-map 0:v -map 1:a -map 2:a), False = amix single track.
        ''' Mirrors the legacy AudioTrackMode behavior.
        ''' </summary>
        Public Property MicSeparateTracks As Boolean = False

        ' ── GLM/6 audit #7: WAV sidecars are evidence/debug artifacts (the live
        '    mux is the real output). Three parallel disk writes per session is
        '    wasted I/O in steady state — default OFF now that live-mux is
        '    runtime-validated. Set True to bring back debug WAVs.
        Public Property EvidenceSidecar As Boolean = False

        ' ★ OBS keep-alive switch: renders endless silence to the loopback
        ' device so WASAPI delivers a continuous stream. Owner reported FULL-
        ' SCALE NOISE with it active on their machine (format mismatch between
        ' the WasapiOut render and the loopback capture, most likely) —
        ' default OFF until the root cause is proven. AudioTap gap-fill
        ' (proven stable in the 21:47 run) remains the primary mechanism.
        Public Property SilenceKeepAlive As Boolean = False

        ' ★ P13.3 clock-mode switch (docs/PHASE-13-SHADOWPLAY-CLOCK.md §4):
        '   "Legacy" = the proven AudioTap v2 (Stopwatch arrival gap-fill +
        '   SilenceKeepAlive switch as configured). "Device" = AudioTap v3 +
        '   WasapiPositionCapture: the system track's timeline comes from
        '   WASAPI qpcPosition stamps (hardware-measured gaps, exact QPC
        '   anchor at the mux). Unknown values normalize to "Legacy".
        '   The mic track stays on the legacy tap in both modes this phase.
        Public Property AudioClockMode As String = "Legacy"

        ' ── Phase 12b: process-lifecycle hook (no-orphan-FFmpeg criterion) ──
        ''' <summary>
        ''' Invoked right after CaptureSession spawns any child process
        ''' (H.264 wrap, verify). The host assigns the child to its
        ''' JobObjectGuard here. Additive — Nothing = previous behavior.
        ''' </summary>
        Public Property OnProcessStarted As Action(Of Process) = Nothing
    End Class

    ''' <summary>
    ''' Phase 12b: process-lifetime engine startup options — consumed once by
    ''' RecordingEngine.Initialize (the persistent NVENC session is expensive
    ''' to rebuild, so codec/bitrate/GOP cannot change per session).
    ''' Values mirror the proven legacy defaults when unset.
    ''' </summary>
    Public NotInheritable Class EngineStartupConfig
        Public Property CodecKey As String = "NVENC_H264"
        ' OWNER (2026-09-25): cursor composition switch (DdagrabBackend).
        Public Property CaptureCursor As Boolean = True
        Public Property BitrateBps As Long = 20_000_000L
        Public Property GopSize As Integer = 60
        Public Property RateControl As String = "cbr"
        Public Property Preset As String = "p4"

        ' ── PHASE 1 VIDEO RUNTIME WIRING ──
        ''' <summary>
        ''' FPS from config.json Recording.current.fps at engine init. Drives
        ''' NVENC frameRateNum (init-time, commit 3) and init-time evidence.
        ''' NEVER mapped into GopSize — FPS and GOP are independent settings
        ''' (HEAD ab89372 bug: RecordingEngineHost.vb:96-98 mapped FPS→GOP).
        ''' 0 = unset → encoder uses its default frame rate.
        ''' </summary>
        Public Property Fps As Integer = 0

        ''' <summary>
        ''' Resolve the per-session FPS authority independently of the
        ''' process-lifetime encoder. A valid session FPS always wins;
        ''' engine FPS is fallback only when the session leaves FPS unset.
        ''' </summary>
        Public Shared Function ResolveSessionTargetFps(sessionFps As Integer, engineFps As Integer) As Integer
            If sessionFps > 0 Then Return sessionFps
            Return If(engineFps > 0, engineFps, 60)
        End Function

        ''' <summary>
        ''' Normalize the process-lifetime NVENC rate-control authority.
        ''' The encoder contract accepts only cbr, vbr, or cq; unknown or
        ''' blank values fail closed to the proven cbr default.
        ''' </summary>
        Public Shared Function ResolveRateControl(value As String) As String
            Dim normalized As String = If(value, "").Trim().ToLowerInvariant()
            Select Case normalized
                Case "cbr", "vbr", "cq"
                    Return normalized
                Case Else
                    Return "cbr"
            End Select
        End Function

        ''' <summary>
        ''' Resolution group from config.json Recording.current.* at engine
        ''' init (V-CT2). True (or invalid width/height) = encode at the
        ''' captured desktop resolution. False + valid dims = NVENC encodes
        ''' at the requested size (GPU scaling — input stays desktop-sized).
        ''' </summary>
        Public Property UseNativeResolution As Boolean = True
        Public Property RequestedWidth As Integer = 0
        Public Property RequestedHeight As Integer = 0

        ''' <summary>
        ''' CaptureMethod requested by config (engine.json CaptureMethod,
        ''' mirrored from config.json Recording.api_capture by the unified
        ''' apply). Evidence only — the New Engine has exactly one
        ''' production backend (DdagrabBackend); requested→selected→actual
        ''' is logged at init.
        ''' </summary>
        Public Property RequestedCaptureMethod As String = ""

        ''' <summary>
        ''' PixelFormat requested by config (engine.json PixelFormat).
        ''' Evidence only — the runtime pipeline is BGRA/ARGB end-to-end;
        ''' nv12 conversion is NOT implemented (BLOCKER, logged loudly).
        ''' </summary>
        Public Property RequestedPixelFormat As String = ""

        ''' <summary>
        ''' V-CT2: resolve the ENCODE dimensions from the request and the actual
        ''' capture size. Pure function — deterministic and testable.
        '''
        '''   native=true (or invalid custom dims)  → (captureW, captureH)
        '''   native=false + valid custom dims      → (requestedW, requestedH)
        '''   custom dims LARGER than the capture    → ArgumentException (loud
        '''       failure — NVENC cannot upscale beyond the input; a silent
        '''       desktop-resolution fallback is forbidden by the phase law)
        '''
        ''' The backend captures the DESKTOP at its native size (DdagrabBackend
        ''' has no scaler); downscaling happens inside NVENC (encodeWidth/Height
        ''' < input, maxEncodeWidth/Height = input size).
        ''' </summary>
        Public Shared Function ResolveEncodeDimensions(captureWidth As Integer,
                                                       captureHeight As Integer,
                                                       useNativeResolution As Boolean,
                                                       requestedWidth As Integer,
                                                       requestedHeight As Integer) As Tuple(Of Integer, Integer)
            If captureWidth <= 0 OrElse captureHeight <= 0 Then
                Throw New ArgumentException(
                    $"capture dimensions must be positive — got {captureWidth}x{captureHeight}")
            End If

            If useNativeResolution OrElse requestedWidth <= 0 OrElse requestedHeight <= 0 Then
                Return Tuple.Create(captureWidth, captureHeight)
            End If

            If requestedWidth > captureWidth OrElse requestedHeight > captureHeight Then
                Throw New ArgumentException(
                    $"requested encode resolution {requestedWidth}x{requestedHeight} exceeds the " &
                    $"captured desktop {captureWidth}x{captureHeight} — NVENC cannot upscale; " &
                    "reduce the requested resolution or enable use_native_resolution")
            End If

            Return Tuple.Create(requestedWidth, requestedHeight)
        End Function
    End Class

    ''' <summary>
    ''' Result of a recording session.
    ''' </summary>
    Public NotInheritable Class SessionResult
        Public Property OutputPath As String = ""
        Public Property RequestedDurationSec As Integer
        Public Property ActualDurationSec As Double
        Public Property FramesCaptured As Long
        Public Property FramesEncoded As Long
        ''' <summary>
        ''' ★ CFR pacing evidence: ticks where the screen was static and the
        ''' LAST frame was re-encoded (a duplicate P-frame). High values on idle
        ''' desktops are EXPECTED and correct (DXGI delivers no frames when the
        ''' screen does not change). dup ≈ 0 during full-motion capture.
        ''' </summary>
        Public Property FramesDuplicated As Long
        Public Property Drops As Long
        Public Property NvencErrors As Long
        Public Property TotalVideoBytes As Long
        Public Property AudioSamples As Long
        Public Property AudioBytes As Long
        Public Property VideoStreamFound As Boolean
        Public Property AudioStreamFound As Boolean
        ''' <summary>Whether this session REQUESTED audio — Pass uses it to
        ''' decide if an audio stream is expected (video-only sessions have
        ''' no audio stream by design).</summary>
        Public Property AudioRequested As Boolean
        Public Property FileExists As Boolean
        Public Property FileSize As Long
        Public Property ErrorMessage As String = ""

        ' ── Phase 12b: sync + accounting evidence ──
        ''' <summary>System-audio offset applied at mux (sec; &gt;0 = audio head skipped, &lt;0 = audio delayed).</summary>
        Public Property SystemOffsetSec As Double
        ''' <summary>Video duration used for mux -t (ffprobe of wrapped MP4, fallback wall-clock).</summary>
        Public Property MuxVideoDurationSec As Double
        ''' <summary>
        ''' ★ Sync-fix evidence: frame rate used for the raw-H.264→MP4 wrap.
        ''' Measured average (framesEncoded / capture span) — NOT the display rate.
        ''' If this differs from the display Hz by more than a few percent, the old
        ''' fixed-rate wrap would have time-compressed the video (progressive drift).
        ''' </summary>
        Public Property WrapFps As Double
        ''' <summary>WAV sidecar accounting is consistent (enqueued = written + dropped).</summary>
        Public Property AudioAccountingOk As Boolean
        ''' <summary>WAV sidecar bytes dropped under backpressure (0 = healthy run).</summary>
        Public Property AudioDroppedBytes As Long

        ' ── M1: mic track evidence (independent accounting) ──
        ''' <summary>Mic track bytes written to the mic sidecar WAV.</summary>
        Public Property MicBytes As Long
        ''' <summary>Mic sidecar dropped bytes (0 = healthy run).</summary>
        Public Property MicDroppedBytes As Long
        ''' <summary>Mic sidecar accounting invariant holds.</summary>
        Public Property MicAccountingOk As Boolean
        ''' <summary>Mic sample count (per the MIC device's own format).</summary>
        Public Property MicSamples As Long
        ''' <summary>Mic A/V offset applied at mux (own timeline, proven model).</summary>
        Public Property MicOffsetSec As Double

        ''' <summary>
        ''' Bytes the live-mux pipe layer dropped (queue overflow, broken pipe,
        ''' or stop-drain timeout) — the only counter that sees loss between
        ''' the audio engine and the FFmpeg file. The sidecar counters above
        ''' measure the PRODUCER side and stay 0 while the mux is dropping
        ''' (clap-sync run 02:41: sidecar dropped=0, mux dropped=1,530,240B
        ''' ≈ 8s of tail audio missing from the file).
        ''' 0 = the file really contains everything captured.
        ''' </summary>
        Public Property MuxDroppedBytes As Long

        ''' <summary>P1-B: ffmpeg died (exit != 0) but a USABLE fragmented
        ''' recording was promoted to the output path. The file exists and
        ''' plays, yet the session is an honest FAILURE — Pass below must
        ''' never turn a salvaged kill into a success.</summary>
        Public Property MuxPartialSalvaged As Boolean

        Public ReadOnly Property Pass As Boolean
            Get
                ' ★ 17:28 FIX (video-only wrongly failed): audio expectations
                ' are conditional on the session's own audio switch — a
                ' video-only session has NO audio stream by design (its mux
                ' args don't even include an audio input), so demanding
                ' AudioStreamFound made every video-only run fail.
                Dim audioOk As Boolean = If(AudioRequested,
                                            AudioStreamFound AndAlso MuxDroppedBytes = 0 AndAlso
                                            AudioDroppedBytes = 0,
                                            True)
                Dim micOk As Boolean = If(MicDroppedBytes = 0, True, False)
                Return FramesEncoded > 0 AndAlso
                       NvencErrors = 0 AndAlso
                       Not MuxPartialSalvaged AndAlso
                       FileExists AndAlso
                       FileSize > 0 AndAlso
                       VideoStreamFound AndAlso
                       audioOk AndAlso micOk
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Engine status — safe to poll from any thread.
    ''' </summary>
    Public NotInheritable Class EngineStatus
        Public Property State As RecordingEngineState
        Public Property CurrentSessionId As String  ' Nothing if Idle
        Public Property FramesEncodedThisSession As Long
        Public Property LastSessionResult As SessionResult  ' Nothing if no session yet
    End Class

End Namespace
