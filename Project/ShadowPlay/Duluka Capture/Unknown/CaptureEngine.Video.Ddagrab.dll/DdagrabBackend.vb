Option Strict On
Option Explicit On
Option Infer On

' DdagrabBackend.vb
'
' Production video capture backend using DXGI Output Duplication.
' Implements IVideoCaptureBackend contract — Initialize() persistent,
' Start/Stop per-session, Dispose at process exit.
'
' Phase 12a-4 FILL-IN:
'   - Real DXGI Output Duplication in worker loop
'   - D3D11 staging texture per frame (D3D11VideoFrame)
'   - AccessLost recovery (recreate duplication)
'   - ReleaseFrame in every path (success, drop, error)
'
' OWNERSHIP MODEL (verified via handoff audit):
'   - Backend OWNS: D3D11 device, DXGI Output Duplication (persistent —
'     created in Initialize, destroyed in Dispose).
'   - Per-frame: staging texture (created in WorkerLoop, owned by
'     D3D11VideoFrame). When TryPush returns Pushed/Replaced, ownership
'     TRANSFERS to sink. When Dropped, backend disposes the frame.
'   - DXGI desktop texture is NOT owned by us — it's borrowed from
'     IDXGIOutputDuplication for the duration between AcquireNextFrame
'     and ReleaseFrame. We CopyResource to our staging texture, then
'     ReleaseFrame immediately (before pushing to sink).
'
' CONTRACT COMPLIANCE:
'   - Initialize() creates persistent GPU resources (D3D11 + DXGI duplication)
'   - Start() spawns worker thread; Stop() signals + joins (2s budget)
'   - Dispose() deadlock-free per P1-B.1 FIX change #1
'   - TryPush never blocks; Dropped → backend disposes frame
'   - ReleaseFrame called in every path (success, NoFrame, error)
'
' HARD RULES:
'   - Foundation NOT modified (CaptureEngine.vb @ 82d792ab FROZEN)
'   - IVideoFrame contract unchanged (D3D11VideoFrame is additive)
'   - No reflection, no TryCast hack
'   - No reference to CaptureEngine.Encoder.Nvenc (no backward dep)
'   - Frame ownership = TRANSFER on Pushed/Replaced; BORROW never

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports SharpGen.Runtime
Imports Vortice.Direct3D
Imports Vortice.Direct3D11
Imports Vortice.DXGI
' Alias to disambiguate ResultCode (Vortice.DXGI vs Vortice.Direct3D11).
Imports ResultCode = Vortice.DXGI.ResultCode
Imports System.Runtime.InteropServices

Namespace CaptureEngine.Video.Backends.Ddagrab

    Public NotInheritable Class DdagrabBackend
        Implements IVideoCaptureBackend
        Implements IVideoBackendDiagnostics

        ' ---- backend state ----
        Private ReadOnly _sync As New Object()
        Private ReadOnly _logger As EngineLogger

        Private _state As DdagrabBackendState = DdagrabBackendState.Created
        Private _disposed As Boolean = False

        ' ---- worker ----
        Private _workerThread As Thread
        Private _stopSignal As Boolean = False
        Private _sink As IVideoFrameSink
        Private _interAttemptDelayMs As Integer = 1   ' avoid busy-looping

        ' ---- diagnostics (counters) ----
        Private _emittedFrames As Long = 0
        Private _droppedFrames As Long = 0
        Private _replacedFrames As Long = 0
        Private _noFrameCount As Long = 0
        Private _errorCount As Long = 0
        Private _accessLostCount As Long = 0
        Private _nextSequence As Long = 0
        Private _lastSourceQpcTicks As Long = 0
        Private _timestampFallbackCount As Long = 0

        ' ---- Phase 12a-5 metrics (per OWNER request) ----
        ' These metrics let 12a-6/12a-8 observe GPU resource lifecycle and
        ' detect slow leaks. Do NOT optimize (e.g. pool staging textures)
        ' based on these numbers yet — the ownership model is new and must
        ' be validated first.
        Private _texturesCreated As Long = 0
        Private _texturesDisposed As Long = 0
        Private _framesPushed As Long = 0      ' sink accepted (Pushed+Replaced)
        Private _framesDropped As Long = 0      ' sink refused (Dropped — backend disposed)

        ' ---- captured context ----
        Private _context As IVideoBackendContext

        ' ---- persistent D3D11 / DXGI resources (created in Initialize) ----
        Private _device As ID3D11Device
        Private _deviceContext As ID3D11DeviceContext
        Private _dxgiFactory As IDXGIFactory1
        Private _adapter As IDXGIAdapter1
        Private _output As IDXGIOutput
        Private _duplication As IDXGIOutputDuplication
        Private _outputWidth As Integer
        Private _outputHeight As Integer

        ' ---- adapter LUID (for cross-device comparison) ----
        Private _adapterLuidLow As UInteger
        Private _adapterLuidHigh As Integer

        ' ---- L8 escalation: eternal DuplicateOutput retry guard ----
        ' A duplication that cannot be recreated (slot exhaustion, driver
        ' wedge, OOM) used to retry at 250 ms FOREVER while the session
        ' silently recorded nothing. After MaxDuplicationRecreateFailures
        ' CONSECUTIVE failed recreations the worker faults loudly (the throw
        ' travels the same outer-catch → worker-exit state tail as a natural
        ' crash), so the session fails honestly instead of shipping a black
        ' recording. A single success resets the counter.
        Private Const MaxDuplicationRecreateFailures As Long = 120   ' ≈30s at the 250ms self-heal retry
        Private _duplicationRecreateFailures As Long = 0

        ' ---- shared-handle mode (Phase 12a-5c) ----
        ' When True, staging textures are created with SharedNthandle flag and
        ' a shared handle is obtained for cross-device resource sharing.
        Private _useSharedHandle As Boolean = False

        ' ---- staging texture description (used per-frame) ----
        Private _stagingDesc As Texture2DDescription

        ' ---- shared texture description (used per-frame in shared-handle mode) ----
        ' Separate from _stagingDesc — this texture has SharedNthandle flag.
        ' The staging texture (without SharedNthandle) receives CopyResource from
        ' DXGI. Then CopyResource from staging → shared. The shared texture is
        ' wrapped in D3D11VideoFrame for cross-device access.
        Private _sharedDesc As Texture2DDescription

        ' ---- mouse cursor composition (owner 2026-09-25) ----
        ' DXGI duplication NEVER bakes the cursor into the desktop texture —
        ' the pointer arrives as separate metadata between AcquireNextFrame
        ' and ReleaseFrame. Without composition every recording silently
        ' loses the mouse. Composition blends a cursor-SIZED region through
        ' a small CPU staging texture; the full frame never leaves the GPU.
        Private _cursorShapeBuffer As IntPtr = IntPtr.Zero
        Private _cursorShapeBufferBytes As Integer = 0
        Private _cursorStage As ID3D11Texture2D = Nothing
        Private _cursorStageW As Integer = 0
        Private _cursorStageH As Integer = 0
        Private _cursorBlend As Byte() = Nothing
        Private _cursorWarned As Boolean = False
        Private _cursorLoggedOk As Boolean = False

        ' ---- output dimensions (Public — needed by harness/orchestration
        '      to size encoder) ----
        Public ReadOnly Property OutputWidth As Integer
            Get
                Return _outputWidth
            End Get
        End Property

        Public ReadOnly Property OutputHeight As Integer
            Get
                Return _outputHeight
            End Get
        End Property

        ' ---- Phase 12b: display refresh rate (Public — the H.264→MP4 wrap
        '      needs the true display cadence; OWNER decision commit 20932aa:
        '      wrap -r uses DISPLAY REFRESH RATE, not achieved FPS) ----
        Private _outputRefreshRate As Integer = 0

        ''' <summary>
        ''' Display refresh rate in Hz (rounded from the DXGI rational rate,
        ''' e.g. 59950/1000 → 60). Populated during Initialize() from the
        ''' display mode list matching the desktop resolution. 0 when the
        ''' probe failed — callers must fall back defensively.
        ''' </summary>
        Public ReadOnly Property OutputRefreshRate As Integer
            Get
                Return _outputRefreshRate
            End Get
        End Property

        ' ---- adapter LUID (Public — for cross-device comparison in harness) ----
        Public ReadOnly Property AdapterLuidLow As UInteger
            Get
                Return _adapterLuidLow
            End Get
        End Property

        Public ReadOnly Property AdapterLuidHigh As Integer
            Get
                Return _adapterLuidHigh
            End Get
        End Property

        ''' <summary>
        ''' Enable shared-handle mode for cross-device resource sharing.
        ''' Must be set BEFORE Initialize(). When True, staging textures are
        ''' created with D3D11_RESOURCE_MISC_SHARED_NTHANDLE and a shared NT
        ''' handle is obtained for each frame. The encoder opens the shared
        ''' resource via ID3D11Device1.OpenSharedResource1.
        ''' </summary>
        Public WriteOnly Property UseSharedHandle As Boolean
            Set(value As Boolean)
                _useSharedHandle = value
            End Set
        End Property

        Public Sub New(Optional logger As EngineLogger = Nothing)
            _logger = If(logger, New EngineLogger("DdagrabBackend"))
        End Sub

        ' ===== IVideoCaptureBackend =====

        Public ReadOnly Property Diagnostics As IVideoBackendDiagnostics Implements IVideoCaptureBackend.Diagnostics
            Get
                Return Me
            End Get
        End Property

        Public Sub Initialize(context As IVideoBackendContext) Implements IVideoCaptureBackend.Initialize
            If context Is Nothing Then
                Throw New ArgumentNullException(NameOf(context))
            End If
            ThrowIfDisposed()

            SyncLock _sync
                If _state <> DdagrabBackendState.Created Then
                    Throw New InvalidOperationException(
                        "Initialize cannot be called from state '" & _state.ToString() & "'. Expected 'Created'.")
                End If
                _state = DdagrabBackendState.Initializing
            End SyncLock

            Try
                If context.BackendKind <> VideoBackendKind.Ddagrab Then
                    Throw New VideoBackendConfigurationException(
                        "DdagrabBackend.Initialize: context.BackendKind must be Ddagrab, but was " &
                        context.BackendKind.ToString() & ".")
                End If

                _context = context

                ' ─── Create D3D11 device on primary NVIDIA adapter ─────────
                ' Per audit verdict #1: each backend owns its own D3D11 device.
                ' Device flags: BgraSupport (Desktop Duplication) + VideoSupport
                ' (interop-friendly; required by some NVENC paths).
                _dxgiFactory = DXGI.CreateDXGIFactory1(Of IDXGIFactory1)()

                Dim nvidiaIdx As Integer = -1
                Dim adapterIdx As Integer = 0
                Dim adapter1 As IDXGIAdapter1 = Nothing
                Do While _dxgiFactory.EnumAdapters1(CUInt(adapterIdx), adapter1).Success
                    Using a As IDXGIAdapter1 = adapter1
                        Dim desc As AdapterDescription1 = a.Description1
                        _logger.Info($"  Adapter [{adapterIdx}] {desc.Description} " &
                                     $"(0x{desc.VendorId:x4}:0x{desc.DeviceId:x4})")
                        ' Pick first NVIDIA adapter (vendor 0x10DE).
                        If desc.VendorId = &H10DEUI AndAlso nvidiaIdx < 0 Then
                            nvidiaIdx = adapterIdx
                        End If
                    End Using
                    adapterIdx += 1
                Loop

                If nvidiaIdx < 0 Then
                    Throw New VideoBackendRuntimeException(
                        "DdagrabBackend: no NVIDIA adapter found. Capture requires an NVIDIA GPU.")
                End If

                _dxgiFactory.EnumAdapters1(CUInt(nvidiaIdx), _adapter).CheckError()
                Dim nvidiaDesc As AdapterDescription1 = _adapter.Description1
                _adapterLuidLow = nvidiaDesc.Luid.LowPart
                _adapterLuidHigh = nvidiaDesc.Luid.HighPart
                _logger.Info($"DdagrabBackend: selected NVIDIA adapter #{nvidiaIdx}: {nvidiaDesc.Description}")

                Dim requestedFeatureLevels As FeatureLevel() = {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0
                }
                Dim flags As DeviceCreationFlags = DeviceCreationFlags.BgraSupport Or
                                                    DeviceCreationFlags.VideoSupport
                D3D11.D3D11CreateDevice(
                    _adapter,
                    DriverType.Unknown,
                    flags,
                    requestedFeatureLevels,
                    _device,
                    _deviceContext).CheckError()

                ' Enable multithread protection (Phase 2 spike: required for
                ' Desktop Duplication performance — without it FPS drops from
                ' ~100 to ~3 with massive WAIT_TIMEOUT counts).
                Dim multithread As ID3D11Multithread = _deviceContext.QueryInterface(Of ID3D11Multithread)()
                multithread.SetMultithreadProtected(True)
                multithread.Dispose()
                _logger.Info($"DdagrabBackend: D3D11 device created (feature level {_device.FeatureLevel})")

                ' ─── Enumerate outputs, prefer one attached to the desktop ───
                ' 2026-09-09: blindly taking output #0 can select a stale/
                ' detached monitor entry on multi-adapter rigs (observed on a
                ' 3×GTX 1080 Ti machine) — DuplicateOutput on a detached
                ' output fails E_INVALIDARG (0x80070057) and the persistent
                ' runtime rebuild then failed in a loop. Prefer the first
                ' output with AttachedToDesktop; fall back to the first
                ' enumerated output to preserve prior behavior.
                Dim out_ As IDXGIOutput = Nothing
                Dim outIdx As Integer = -1
                Dim scanIdx As Integer = 0
                Do
                    Dim candidate As IDXGIOutput = Nothing
                    If Not _adapter.EnumOutputs(CUInt(scanIdx), candidate).Success Then Exit Do
                    Dim attached As Boolean = candidate.Description.AttachedToDesktop
                    If out_ Is Nothing Then
                        out_ = candidate          ' keep ownership of the first
                        outIdx = scanIdx
                        If attached Then Exit Do  ' first output already live — done
                    ElseIf attached Then
                        out_.Dispose()            ' default was detached — replace
                        out_ = candidate
                        outIdx = scanIdx
                        Exit Do
                    Else
                        candidate.Dispose()       ' not chosen
                    End If
                    scanIdx += 1
                Loop
                If out_ Is Nothing Then
                    Throw New VideoBackendRuntimeException(
                        "DdagrabBackend: no DXGI outputs found on adapter.")
                End If
                _output = out_
                Dim outDesc As OutputDescription = _output.Description
                _outputWidth = outDesc.DesktopCoordinates.Right - outDesc.DesktopCoordinates.Left
                _outputHeight = outDesc.DesktopCoordinates.Bottom - outDesc.DesktopCoordinates.Top
                _logger.Info($"DdagrabBackend: output #{outIdx}: {outDesc.DeviceName} " &
                             $"({_outputWidth}x{_outputHeight})")

                ' ─── Phase 12b: resolve display refresh rate ────────────
                ' Best-effort — failure leaves 0 and callers fall back.
                ' Vortice convenience overload returns the mode array directly.
                ' Flags 0 = progressive modes (the enum has no None member).
                Try
                    Dim modes As ModeDescription() = _output.GetDisplayModeList(
                        Format.B8G8R8A8_UNorm, CType(0, DisplayModeEnumerationFlags))
                    Dim bestHz As Integer = 0
                    If modes IsNot Nothing Then
                        For Each m As ModeDescription In modes
                            If CDbl(m.Width) = CDbl(_outputWidth) AndAlso CDbl(m.Height) = CDbl(_outputHeight) Then
                                Dim denom As Double = CDbl(m.RefreshRate.Denominator)
                                Dim hz As Integer = 0
                                If denom > 0 Then
                                    hz = CInt(Math.Round(CDbl(m.RefreshRate.Numerator) / denom))
                                End If
                                If hz > bestHz Then bestHz = hz
                            End If
                        Next
                    End If
                    _outputRefreshRate = bestHz
                    _logger.Info($"DdagrabBackend: display refresh rate = {_outputRefreshRate}Hz")
                Catch ex As Exception
                    _logger.Warning($"DdagrabBackend: refresh-rate probe failed: {ex.Message}")
                End Try

                ' ─── DuplicateOutput (persistent — 1 per output per process) ─
                ' Phase 11 root cause #2: Windows limits 1 duplication per output
                ' per process. Initialize() creates it ONCE; Start/Stop just
                ' starts/stops frame delivery.
                ' 2026-09-09: single-shot DuplicateOutput made the persistent
                ' runtime rebuild brittle — a transient E_INVALIDARG (desktop
                ' topology race right after a config change) failed the whole
                ' rebuild 4× in a row on the user machine while the same call
                ' succeeded at process start. Bounded retry (3 × 250ms) absorbs
                ' the transient window without masking persistent faults — the
                ' LAST exception still propagates verbatim on total failure.
                Const DuplicationInitAttempts As Integer = 3
                Const DuplicationInitRetryDelayMs As Integer = 250
                Dim dupLastEx As Exception = Nothing
                For attempt As Integer = 1 To DuplicationInitAttempts
                    Try
                        Dim output1 As IDXGIOutput1 = _output.QueryInterface(Of IDXGIOutput1)()
                        _duplication = output1.DuplicateOutput(_device)
                        output1.Dispose()
                        dupLastEx = Nothing
                        Exit For
                    Catch ex As Exception
                        _duplication = Nothing
                        dupLastEx = ex
                        _logger.Warning($"DdagrabBackend: DuplicateOutput attempt {attempt}/{DuplicationInitAttempts} failed: {ex.Message}")
                        If attempt < DuplicationInitAttempts Then
                            Threading.Thread.Sleep(DuplicationInitRetryDelayMs)
                        End If
                    End Try
                Next attempt
                If dupLastEx IsNot Nothing Then
                    Throw dupLastEx
                End If
                _logger.Info("DdagrabBackend: DXGI Output Duplication created (persistent)")

                ' ─── Staging texture description (used per-frame in WorkerLoop) ─
                ' Staging texture NEVER has SharedNthandle — it receives CopyResource
                ' from DXGI Output Duplication which doesn't support shared flags.
                _stagingDesc = New Texture2DDescription() With {
                    .Width = CUInt(_outputWidth),
                    .Height = CUInt(_outputHeight),
                    .MipLevels = 1,
                    .ArraySize = 1,
                    .Format = Format.B8G8R8A8_UNorm,
                    .SampleDescription = New SampleDescription(1, 0),
                    .Usage = ResourceUsage.Default,
                    .BindFlags = BindFlags.ShaderResource Or BindFlags.RenderTarget,
                    .CPUAccessFlags = CpuAccessFlags.None,
                    .MiscFlags = ResourceOptionFlags.None
                }

                ' Shared texture description (used only in shared-handle mode)
                ' This texture has SharedNthandle for cross-device access via OpenSharedResource1.
                If _useSharedHandle Then
                    _sharedDesc = New Texture2DDescription() With {
                        .Width = CUInt(_outputWidth),
                        .Height = CUInt(_outputHeight),
                        .MipLevels = 1,
                        .ArraySize = 1,
                        .Format = Format.B8G8R8A8_UNorm,
                        .SampleDescription = New SampleDescription(1, 0),
                        .Usage = ResourceUsage.Default,
                        .BindFlags = BindFlags.None,
                        .CPUAccessFlags = CpuAccessFlags.None,
                        .MiscFlags = ResourceOptionFlags.Shared Or ResourceOptionFlags.SharedNthandle
                    }
                    _logger.Info("DdagrabBackend: shared-handle mode ENABLED (two-texture approach)")
                End If

                _state = DdagrabBackendState.Initialized
                _logger.Info("DdagrabBackend: Initialize complete (real DXGI capture)")
            Catch ex As VideoBackendException
                _state = DdagrabBackendState.Faulted
                _logger.Error("DdagrabBackend: Initialize failed", ex)
                CleanupPersistentResources()
                Throw
            Catch ex As Exception
                _state = DdagrabBackendState.Faulted
                _logger.Error("DdagrabBackend: Initialize failed (unexpected)", ex)
                CleanupPersistentResources()
                Throw New VideoBackendRuntimeException("Initialize failed unexpectedly", ex)
            End Try
        End Sub

        Public Sub Start(sink As IVideoFrameSink) Implements IVideoCaptureBackend.Start
            If sink Is Nothing Then
                Throw New ArgumentNullException(NameOf(sink))
            End If
            ThrowIfDisposed()

            SyncLock _sync
                Select Case _state
                    Case DdagrabBackendState.Starting, DdagrabBackendState.Running
                        _logger.Warning("DdagrabBackend: Start ignored, already '" & _state.ToString() & "'.")
                        Return
                    Case DdagrabBackendState.Initialized, DdagrabBackendState.Stopped
                        _state = DdagrabBackendState.Starting
                    Case Else
                        Throw New InvalidOperationException(
                            "Start cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock

            Try
                _sink = sink
                _stopSignal = False
                _lastSourceQpcTicks = 0
                _timestampFallbackCount = 0

                ' ★ Session-start frame fix (owner: 'opened the clip, heard music
                ' before I started playing any'). Root cause: AcquireNextFrame
                ' only fires when screen CONTENT CHANGES — on a static screen a
                ' new session got NO first frame until the screen changed (could
                ' be many seconds), while audio recorded from t=0. The sync
                ' offset then skipped the audio head, dropping the silent lead-in
                ' and pulling later audio to the front.
                ' Fix: recreate the duplication at Start(). DXGI guarantees the
                ' FIRST AcquireNextFrame after DuplicateOutput returns the
                ' CURRENT desktop image immediately — the session now has a real
                ' frame at t≈0 and video/audio timelines start together.
                ' Same dispose-then-create pattern as the proven AccessLost
                ' recovery (one live duplication per output per process).
                _logger.Info("DdagrabBackend: recreating duplication for session-start frame")
                RecreateDuplication()

                ' ★ C-1: commit Running and spawn the worker under ONE lock —
                ' the transition is atomic w.r.t. the caller. Once Start() can
                ' throw, the state is either Running (worker live or about to
                ' be; Stop works) or Faulted (no worker exists). The old order
                ' spawned the worker first and committed Running last, so a
                ' throw in between (thread OOM, logger fault) left a LIVE
                ' worker under a state the session's Finally would never Stop.
                SyncLock _sync
                    _state = DdagrabBackendState.Running
                    Try
                        _workerThread = New Thread(AddressOf WorkerLoop) With {
                            .IsBackground = True,
                            .Name = "DdagrabBackend.Worker"
                        }
                        _workerThread.Start()
                    Catch
                        _state = DdagrabBackendState.Faulted   ' rollback — no worker exists
                        _workerThread = Nothing
                        Throw
                    End Try
                End SyncLock
                _logger.Info("DdagrabBackend: started (real DXGI capture)")
            Catch ex As Exception
                ' Never downgrade a committed Running (worker live) to Faulted —
                ' that is exactly the orphan the commit-first order exists to
                ' prevent. Only pre-commit failures mark Faulted.
                Dim committed As Boolean
                SyncLock _sync
                    committed = (_state = DdagrabBackendState.Running)
                    If Not committed Then _state = DdagrabBackendState.Faulted
                End SyncLock
                If committed Then
                    Try : _logger.Warning("DdagrabBackend: Start threw after commit — worker is live; caller must Stop(): " & ex.Message) : Catch : End Try
                Else
                    _logger.Error("DdagrabBackend: Start failed", ex)
                End If
                Throw New VideoBackendRuntimeException("Start failed", ex)
            End Try
        End Sub

        Public Sub [Stop]() Implements IVideoCaptureBackend.Stop
            ThrowIfDisposed()

            SyncLock _sync
                Select Case _state
                    Case DdagrabBackendState.Created, DdagrabBackendState.Initialized, DdagrabBackendState.Stopped
                        _logger.Warning("DdagrabBackend: Stop ignored, not running (state='" & _state.ToString() & "').")
                        Return
                    Case DdagrabBackendState.Stopping
                        _logger.Warning("DdagrabBackend: Stop ignored, already stopping.")
                        Return
                    Case DdagrabBackendState.Faulted
                        _logger.Warning("DdagrabBackend: Stop on Faulted state; no work to stop.")
                        Return
                    Case DdagrabBackendState.Running
                        _state = DdagrabBackendState.Stopping
                    Case Else
                        Throw New InvalidOperationException(
                            "Stop cannot be called from state '" & _state.ToString() & "'.")
                End Select
            End SyncLock

            Try
                _stopSignal = True
                Dim worker = _workerThread
                If worker IsNot Nothing Then
                    If Not worker.Join(TimeSpan.FromSeconds(2)) Then
                        ' ★ M2: do NOT declare Stopped while the worker
                        ' generation that owns the duplication/device may still
                        ' be executing — that let a follow-up Start() spawn a
                        ' SECOND worker onto the same shared GPU state. The
                        ' state stays 'Stopping' (Start is rejected from there)
                        ' and the worker's own exit tail completes the
                        ' Stopping→Stopped transition the moment the generation
                        ' actually terminates.
                        _logger.Error("DdagrabBackend: worker did not acknowledge stop within 2 s — state remains 'Stopping' until the worker generation exits")
                        Return
                    End If
                End If
                SyncLock _sync
                    If _state = DdagrabBackendState.Stopping Then
                        _state = DdagrabBackendState.Stopped
                    End If
                End SyncLock
                _logger.Info("DdagrabBackend: stopped")
            Catch ex As Exception
                _state = DdagrabBackendState.Faulted
                _logger.Error("DdagrabBackend: Stop failed", ex)
                Throw New VideoBackendShutdownException("Stop failed", ex)
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            ' P1-B.1 FIX change #1: NEVER wait for the worker while holding _sync.
            ' Worker needs _sync on every iteration — holding _sync across Join = deadlock.

            Dim workerToJoin As Thread = Nothing
            Dim needJoin As Boolean = False

            SyncLock _sync
                If _disposed Then Return
                _disposed = True

                If _state = DdagrabBackendState.Disposed Then
                    Return
                ElseIf _state = DdagrabBackendState.Running OrElse _state = DdagrabBackendState.Starting Then
                    _logger.Info("DdagrabBackend: Dispose while Running — invoking stop path.")
                    _state = DdagrabBackendState.Stopping
                    _stopSignal = True
                    workerToJoin = _workerThread
                    needJoin = True
                ElseIf _state = DdagrabBackendState.Stopping Then
                    ' ★ Soak-D fix (concurrent Stop+Dispose): a Stop() in flight
                    ' already raised _stopSignal but its 2s join may still be
                    ' running when we get here. Cleanup below releases the COM
                    ' objects the worker is calling into — RecreateDuplication
                    ' on the disposed output then spun forever / crashed
                    ' natively (exit 0xC0000005 on the 1080 Ti). Join FIRST.
                    _logger.Info("DdagrabBackend: Dispose while Stopping — joining worker before cleanup.")
                    workerToJoin = _workerThread
                    needJoin = True
                Else
                    _logger.Info("DdagrabBackend: Dispose from state '" & _state.ToString() & "'.")
                End If
            End SyncLock

            If needJoin AndAlso workerToJoin IsNot Nothing Then
                Try
                    If Not workerToJoin.Join(TimeSpan.FromSeconds(2)) Then
                        _logger.Error("DdagrabBackend: worker did not acknowledge stop within 2 s", Nothing)
                        ' ★ C-2B: the worker may be inside a blocking COM
                        ' capture call — give it one bounded extra wait before
                        ' the cleanup below releases the COM objects it is
                        ' still calling into (a released-wrapper call can
                        ' fault the process). 5 s total, then cleanup proceeds.
                        If Not workerToJoin.Join(TimeSpan.FromSeconds(3)) Then
                            _logger.Error("DdagrabBackend: worker still alive after 5 s — disposing COM resources anyway", Nothing)
                        End If
                    End If
                Catch ex As Exception
                    _logger.Error("DdagrabBackend: stop path failed during Dispose (will still dispose)", ex)
                End Try
            End If

            SyncLock _sync
                If _state = DdagrabBackendState.Stopping Then
                    _state = DdagrabBackendState.Stopped
                End If
                _state = DdagrabBackendState.Disposed
            End SyncLock

            ' Release persistent GPU resources OUTSIDE _sync (GPU release can be slow).
            CleanupPersistentResources()
            _logger.Info("DdagrabBackend: disposed")
        End Sub

        ' ===== IVideoBackendDiagnostics =====

        Public ReadOnly Property EmittedFrames As Long Implements IVideoBackendDiagnostics.EmittedFrames
            Get
                SyncLock _sync
                    Return _emittedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property DroppedFrames As Long Implements IVideoBackendDiagnostics.DroppedFrames
            Get
                SyncLock _sync
                    Return _droppedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ReplacedFrames As Long Implements IVideoBackendDiagnostics.ReplacedFrames
            Get
                SyncLock _sync
                    Return _replacedFrames
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property NoFrameCount As Long Implements IVideoBackendDiagnostics.NoFrameCount
            Get
                SyncLock _sync
                    Return _noFrameCount
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ErrorCount As Long Implements IVideoBackendDiagnostics.ErrorCount
            Get
                SyncLock _sync
                    Return _errorCount
                End SyncLock
            End Get
        End Property

        ' Extra diagnostic (not in interface — read via cast or test helper)
        Public ReadOnly Property AccessLostCount As Long
            Get
                SyncLock _sync
                    Return _accessLostCount
                End SyncLock
            End Get
        End Property

        ' ===== Phase 12a-5 metrics (per OWNER request) =====
        ' GPU resource lifecycle counters. At steady state with no leak:
        '   _texturesDisposed ~= _texturesCreated (after some lag)
        '   _framesPushed + _framesDropped = _emittedFrames
        ' If _texturesCreated - _texturesDisposed grows unboundedly → leak.

        Public ReadOnly Property TexturesCreated As Long
            Get
                SyncLock _sync
                    Return _texturesCreated
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property TexturesDisposed As Long
            Get
                SyncLock _sync
                    Return _texturesDisposed
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property FramesPushed As Long
            Get
                SyncLock _sync
                    Return _framesPushed
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property FramesDropped As Long
            Get
                SyncLock _sync
                    Return _framesDropped
                End SyncLock
            End Get
        End Property

        ' ===== Test-visible state (Friend) =====

        Public ReadOnly Property CurrentState As DdagrabBackendState
            Get
                SyncLock _sync
                    Return _state
                End SyncLock
            End Get
        End Property

        Friend Function WithInterAttemptDelayMs(delayMs As Integer) As DdagrabBackend
            SyncLock _sync
                _interAttemptDelayMs = delayMs
                Return Me
            End SyncLock
        End Function

        ' ─── C-2C test seam (Friend — deterministic worker-crash injection) ──
        ' A natural worker crash needs a DXGI/driver fault that cannot be
        ' forced safely. This seam reproduces the EXACT crash shape at the
        ' safest point: the TOP of a worker iteration, where no COM object
        ' and no frame is held — the exception then travels the same path a
        ' real crash takes (outer catch → worker-exit state tail). Production
        ' never sets the flag; the field is read-and-cleared per iteration.
        Private _workerCrashRequested As Boolean = False

        Friend Sub RequestWorkerCrashOnce()
            SyncLock _sync
                _workerCrashRequested = True
            End SyncLock
        End Sub

        ' ===== Internal worker — REAL DXGI CAPTURE LOOP =====

        Private Sub WorkerLoop()
            Try
                Do
                    Dim shouldStop As Boolean
                    SyncLock _sync
                        shouldStop = _stopSignal
                    End SyncLock
                    If shouldStop Then Exit Do

                    ' ★ C-2C seam: read-and-clear the crash request at the
                    ' top of the iteration — nothing is held here (no frame,
                    ' no acquire in flight), so the injected throw exercises
                    ' the identical termination path as a natural crash.
                    Dim crashNow As Boolean
                    SyncLock _sync
                        crashNow = _workerCrashRequested
                        _workerCrashRequested = False
                    End SyncLock
                    If crashNow Then
                        Throw New InvalidOperationException("INJECTED worker crash (C-2C test seam)")
                    End If

                    ' ─── AcquireNextFrame (DXGI Output Duplication) ────────
                    Dim sequence As Long
                    Dim attemptTime As Long
                    SyncLock _sync
                        sequence = _nextSequence
                        _nextSequence += 1
                        attemptTime = CLng(Math.Truncate(
                            CDec(Stopwatch.GetTimestamp()) * 10000000D /
                            CDec(Stopwatch.Frequency)))
                    End SyncLock

                    Dim frameInfo As OutduplFrameInfo
                    Dim desktopResource As IDXGIResource = Nothing

                    ' ★ Self-heal: if the duplication is Nothing (creation failed
                    ' at Start or after AccessLost), recreate instead of throwing
                    ' NullReference on every iteration.
                    If _duplication Is Nothing Then
                        RecreateDuplication()
                        If _duplication Is Nothing Then
                            Thread.Sleep(250)   ' brief backoff; retry next loop
                            Continue Do
                        End If
                    End If

                    Dim acquireResult As Result = _duplication.AcquireNextFrame(
                        100,  ' 100ms timeout
                        frameInfo,
                        desktopResource)

                    If Not acquireResult.Success Then
                        ' Common case: DXGI_ERROR_WAIT_TIMEOUT (no new frame since last acquire)
                        ' Not an error — increment NoFrameCount and continue.
                        If acquireResult = ResultCode.WaitTimeout Then
                            SyncLock _sync
                                _noFrameCount += 1
                            End SyncLock
                        ElseIf acquireResult = ResultCode.AccessLost Then
                            ' Output duplication lost (mode change, fullscreen app, etc.)
                            ' Recreate the duplication on next iteration.
                            SyncLock _sync
                                _accessLostCount += 1
                            End SyncLock
                            _logger.Warning("DdagrabBackend: DXGI_ACCESS_LOST — recreating duplication")
                            RecreateDuplication()
                        Else
                            SyncLock _sync
                                _errorCount += 1
                            End SyncLock
                            _logger.Error($"DdagrabBackend: AcquireNextFrame failed: hr=0x{acquireResult.Code:x8}")
                        End If

                        If _interAttemptDelayMs > 0 Then
                            Thread.Sleep(_interAttemptDelayMs)
                        End If
                        Continue Do
                    End If

                    ' Session/recording timestamp: sample QPC immediately after
                    ' AcquireNextFrame succeeds. DXGI LastPresentTime is the
                    ' timestamp of the desktop image''s LAST UPDATE, not the
                    ' time this frame was acquired; on a static desktop it can
                    ' legitimately be stale and must not become the recording
                    ' timeline origin.
                    Dim acquireQpcTicks As Long = Stopwatch.GetTimestamp()
                    Dim sourceQpcTicks As Long = frameInfo.LastPresentTime
                    Dim sourceAcquireDelta100ns As Long = 0
                    If sourceQpcTicks > 0 Then
                        sourceAcquireDelta100ns = QpcTicksTo100ns(sourceQpcTicks - acquireQpcTicks)
                    End If
                    If sequence Mod 60 = 0 Then
                        _logger.Info($"DdagrabBackend: QPC probe seq={sequence} sourcePresent={sourceQpcTicks} acquireQpc={acquireQpcTicks} deltaMs={(sourceAcquireDelta100ns / 10000.0):0.###} accumulatedFrames={frameInfo.AccumulatedFrames}")
                    End If
                    If sourceQpcTicks <= 0 OrElse
                       (_lastSourceQpcTicks > 0 AndAlso sourceQpcTicks < _lastSourceQpcTicks) Then
                        ' DXGI should expose a monotonic QPC presentation stamp, but
                        ' do not let a malformed/backward stamp move the recording
                        ' timeline backwards. Fall back to the acquisition QPC for
                        ' this frame and keep source ordering monotonic.
                        sourceQpcTicks = acquireQpcTicks
                        Interlocked.Increment(_timestampFallbackCount)
                    End If
                    If sourceQpcTicks < _lastSourceQpcTicks Then
                        sourceQpcTicks = _lastSourceQpcTicks
                    End If
                    _lastSourceQpcTicks = sourceQpcTicks
                    Dim acquiredQpc100ns As Long = QpcTicksTo100ns(sourceQpcTicks)
                    ' ─── Got a frame — copy to staging texture + ReleaseFrame ─
                    Dim stagingTexture As ID3D11Texture2D = Nothing
                    Dim sharedTexture As ID3D11Texture2D = Nothing
                    Dim desktopTexture As ID3D11Texture2D = Nothing
                    Dim frame As D3D11VideoFrame = Nothing
                    Dim frameAcquired As Boolean = False
                    Try
                        desktopTexture = desktopResource.QueryInterface(Of ID3D11Texture2D)()
                        frameAcquired = True  ' AcquireNextFrame succeeded — MUST ReleaseFrame

                        ' Create staging texture (per-frame — NO SharedNthandle)
                        stagingTexture = _device.CreateTexture2D(_stagingDesc)
                        SyncLock _sync
                            _texturesCreated += 1
                        End SyncLock

                        ' GPU copy: DXGI desktop texture → staging texture
                        _deviceContext.CopyResource(stagingTexture, desktopTexture)

                        ' Mouse cursor composition — must run while the frame is
                        ' acquired: pointer shape metadata dies at ReleaseFrame.
                        Try
                            BlendCursorOnto(stagingTexture, frameInfo)
                        Catch cex As Exception
                            If Not _cursorWarned Then
                                _cursorWarned = True
                                _logger.Warning($"DdagrabBackend: cursor blend failed (logged once): {cex.Message}")
                            End If
                        End Try

                        ' If shared-handle mode, create shared texture + handle
                        Dim sharedHandle As IntPtr = IntPtr.Zero
                        If _useSharedHandle Then
                            ' Create SEPARATE shared texture (WITH SharedNthandle)
                            sharedTexture = _device.CreateTexture2D(_sharedDesc)
                            SyncLock _sync
                                _texturesCreated += 1
                            End SyncLock

                            ' Copy: staging → shared (same-device copy, always valid)
                            _deviceContext.CopyResource(sharedTexture, stagingTexture)

                            ' Create NT handle for cross-device sharing
                            Dim dxgiRes As IDXGIResource1 = sharedTexture.QueryInterface(Of IDXGIResource1)()
                            Try
                                sharedHandle = dxgiRes.CreateSharedHandle(Nothing, Vortice.DXGI.SharedResourceFlags.Read Or Vortice.DXGI.SharedResourceFlags.Write, Nothing)
                                _logger.Info($"DdagrabBackend: shared handle created: 0x{sharedHandle.ToInt64():x16}")
                            Catch ex As Exception
                                _logger.Error($"DdagrabBackend: CreateSharedHandle threw: {ex.Message}")
                                dxgiRes.Dispose()
                                GoTo skipFrame
                            End Try
                            dxgiRes.Dispose()
                        End If

                        ' ─── Construct D3D11VideoFrame + push to sink ─────────
                        ' In shared-handle mode: frame wraps sharedTexture (with handle)
                        ' In direct mode: frame wraps stagingTexture (no handle)
                        Dim frameTexture As ID3D11Texture2D = If(sharedTexture, stagingTexture)
                        Dim frameQpc100ns As Long = acquiredQpc100ns
                        
                        ' ★ FORENSIC INSTRUMENTATION: Capture layer timing for first 4 frames
                        If sequence <= 4 Then
                            Dim captureTick As Long = Stopwatch.GetTimestamp()
                            Dim captureQpc As Long = Stopwatch.GetTimestamp() ' Using Stopwatch as QPC equivalent
                            _logger.Info($"DdagrabBackend: FRAME {sequence} CAPTURE TIMING:")
                            _logger.Info($"  acquireQpc100ns={acquiredQpc100ns}")
                            _logger.Info($"  frameQpc100ns={frameQpc100ns}")
                            _logger.Info($"  captureTick={captureTick}")
                            _logger.Info($"  captureQpc={captureQpc}")
                        End If
                        
                        frame = New D3D11VideoFrame(
                            frameTexture,
                            _outputWidth,
                            _outputHeight,
                            sequence,
                            frameQpc100ns,
                            frameQpc100ns,
                            sharedHandle)
                        frameTexture = Nothing  ' ownership transferred to frame

                        ' Set disposal callback for metric tracking
                        frame.OnDisposed = Sub()
                                              SyncLock _sync
                                                  _texturesDisposed += 1
                                              End SyncLock
                                          End Sub

                        ' TryPush — TRANSFER ownership model
                        Dim outcome As PushOutcome = _sink.TryPush(
                            FrameAcquisitionResult.Available(frame, sequence, attemptTime))
                        Select Case outcome
                            Case PushOutcome.Pushed
                                SyncLock _sync
                                    _emittedFrames += 1
                                    _framesPushed += 1
                                End SyncLock
                                frame = Nothing
                            Case PushOutcome.Replaced
                                SyncLock _sync
                                    _emittedFrames += 1
                                    _replacedFrames += 1
                                    _framesPushed += 1
                                End SyncLock
                                frame = Nothing
                            Case PushOutcome.Dropped
                                SyncLock _sync
                                    _droppedFrames += 1
                                    _framesDropped += 1
                                End SyncLock
                                frame?.Dispose()
                                frame = Nothing
                        End Select

skipFrame:
                    Catch ex As Exception
                        SyncLock _sync
                            _errorCount += 1
                        End SyncLock
                        _logger.Error($"DdagrabBackend: worker iteration failed: {ex.Message}", ex)
                        If frame IsNot Nothing Then
                            frame.Dispose()
                        End If
                        If sharedTexture IsNot Nothing Then
                            sharedTexture.Dispose()
                            SyncLock _sync
                                _texturesDisposed += 1
                            End SyncLock
                        End If
                        If stagingTexture IsNot Nothing Then
                            stagingTexture.Dispose()
                            SyncLock _sync
                                _texturesDisposed += 1
                            End SyncLock
                        End If
                    Finally
                        ' ALWAYS dispose the DXGI desktop texture wrapper
                        Try : desktopTexture?.Dispose() : Catch : End Try
                        Try : desktopResource?.Dispose() : Catch : End Try
                        ' In shared-handle mode, staging texture is temporary (copy → shared done)
                        ' Dispose it now — the shared texture (owned by frame) has the data.
                        If sharedTexture IsNot Nothing AndAlso stagingTexture IsNot Nothing Then
                            ' staging was already copied to shared — safe to dispose
                            ' But only if frame was NOT constructed from stagingTexture
                            ' (in direct mode, frame wraps stagingTexture — don't dispose here)
                        End If
                        ' Actually, in shared-handle mode, stagingTexture is NOT wrapped by frame
                        ' (frame wraps sharedTexture). So dispose stagingTexture here.
                        ' In direct mode, stagingTexture IS wrapped by frame — don't dispose.
                        If _useSharedHandle AndAlso stagingTexture IsNot Nothing Then
                            Try : stagingTexture.Dispose() : Catch : End Try
                            SyncLock _sync
                                _texturesDisposed += 1
                            End SyncLock
                        End If
                        ' ALWAYS ReleaseFrame if AcquireNextFrame succeeded
                        If frameAcquired Then
                            Try
                                _duplication.ReleaseFrame()
                            Catch ex As Exception
                                _logger.Warning($"DdagrabBackend: ReleaseFrame threw: {ex.Message}")
                            End Try
                        End If
                    End Try

                Loop
            Catch ex As Exception
                _logger.Error($"DdagrabBackend: worker thread crashed: {ex.Message}", ex)
            End Try

            ' ★ M2: the exiting worker generation owns the completion of its
            ' own stop. When a Stop() join timed out, the state was left at
            ' 'Stopping' (Start rejected); the moment this generation actually
            ' terminates — normal exit OR crash — the stop is complete and a
            ' future Start() is legitimate again. A joined Stop already
            ' completed the transition, so this only fires for the
            ' timed-out/restart path. Runs after the LAST shared-state access;
            ' nothing below touches the duplication or device.
            SyncLock _sync
                If _state = DdagrabBackendState.Stopping Then
                    _state = DdagrabBackendState.Stopped
                ElseIf _state = DdagrabBackendState.Running Then
                    ' ★ C-2C: the worker terminated with NO Stop in flight
                    ' (unexpected crash). Running must never describe a dead
                    ' worker — Start from Running is silently refused forever.
                    ' The worker is gone, so the stop is effectively complete:
                    ' transition to Stopped (loud Error log carries the
                    ' evidence) so a future Start can recreate and self-heal.
                    _state = DdagrabBackendState.Stopped
                    _logger.Error("DdagrabBackend: worker terminated without Stop — state=Stopped (Start may recreate)")
                End If
            End SyncLock

            _logger.Info("DdagrabBackend: worker exited")
        End Sub

        ' Convert DXGI QPC ticks to the engine's 100ns timestamp domain.
        ' Keep the quotient/remainder groups explicit: VB operator precedence
        ' must not change the unit conversion into (qpcTicks Mod f).
        Private Shared Function QpcTicksTo100ns(qpcTicks As Long) As Long
            Dim f As Long = Stopwatch.Frequency
            If f <= 0 Then Return 0
            Return (qpcTicks \ f) * 10000000L + ((qpcTicks Mod f) * 10000000L) \ f
        End Function

        Private Sub RecreateDuplication()
            ' ★ Soak-D guard: Dispose's cleanup may already have released the
            ' output/device while this worker was still unwinding — recreating
            ' onto disposed COM spun forever and crashed natively. Bail when
            ' disposed; the worker's stop-signal check ends the loop.
            Dim disposedNow As Boolean = False
            SyncLock _sync
                disposedNow = _disposed
            End SyncLock
            If disposedNow Then
                _duplication = Nothing
                Return
            End If
            ' DXGI_ACCESS_LOST recovery — recreate the duplication object.
            ' Existing _duplication is dead; replace it.
            Try
                _duplication?.Dispose()
            Catch
            End Try
            Try
                Dim output1 As IDXGIOutput1 = _output.QueryInterface(Of IDXGIOutput1)()
                _duplication = output1.DuplicateOutput(_device)
                output1.Dispose()
                SyncLock _sync
                    _duplicationRecreateFailures = 0
                End SyncLock
                _logger.Info("DdagrabBackend: DXGI Output Duplication recreated")
            Catch ex As Exception
                Dim failures As Long
                SyncLock _sync
                    _duplicationRecreateFailures += 1
                    failures = _duplicationRecreateFailures
                End SyncLock
                _logger.Error($"DdagrabBackend: failed to recreate duplication ({failures}/{MaxDuplicationRecreateFailures}): {ex.Message}", ex)
                If failures >= MaxDuplicationRecreateFailures Then
                    ' L8 escalation: stop the eternal 250ms retry — fault the
                    ' worker so the session fails loudly instead of recording
                    ' nothing forever.
                    Throw New VideoBackendRuntimeException(
                        $"DXGI Output Duplication unavailable — {failures} consecutive recreate failures " &
                        "(device wedge, duplication-slot exhaustion, or driver fault).")
                End If
            End Try
        End Sub

        Private Sub CleanupPersistentResources()
            ' Dispose in reverse construction order. All wrapped in try/catch
            ' so partial-init failures don't leak remaining resources.
            Try : _cursorStage?.Dispose() : Catch : End Try
            If _cursorShapeBuffer <> IntPtr.Zero Then
                Marshal.FreeHGlobal(_cursorShapeBuffer)
                _cursorShapeBuffer = IntPtr.Zero
                _cursorShapeBufferBytes = 0
            End If
            Try : _duplication?.Dispose() : Catch ex As Exception : _logger.Warning($"duplication.Dispose threw: {ex.Message}") : End Try
            Try : _output?.Dispose() : Catch : End Try
            Try : _adapter?.Dispose() : Catch : End Try
            Try : _deviceContext?.Dispose() : Catch : End Try
            Try : _device?.Dispose() : Catch : End Try
            Try : _dxgiFactory?.Dispose() : Catch : End Try
        End Sub

        Private Sub ThrowIfDisposed()
            SyncLock _sync
                If _disposed Then
                    Throw New ObjectDisposedException(
                        NameOf(DdagrabBackend),
                        "DdagrabBackend has been disposed and can no longer be used.")
                End If
            End SyncLock
        End Sub

        Public Enum DdagrabBackendState
            Created
            Initializing
            Initialized
            Starting
            Running
            Stopping
            Stopped
            Faulted
            Disposed
        End Enum
        ' =========== Mouse cursor composition (owner 2026-09-25) ===========

        ''' <summary>Blend the DXGI pointer onto the frame copy. Runs on the
        ''' worker thread between AcquireNextFrame and ReleaseFrame — the
        ''' pointer shape metadata is only valid inside that window.</summary>
        ''' <summary>OWNER toggle (2026-09-25): draw the mouse cursor into
        ''' captured frames. Config: engine.json "CaptureCursor" (default True).</summary>
        Public Property CaptureCursor As Boolean = False  ' opt-in: composition needs debug-layer pass on this driver

        Private Sub BlendCursorOnto(target As ID3D11Texture2D, frameInfo As OutduplFrameInfo)
            If Not CaptureCursor Then Return
            If Not CBool(frameInfo.PointerPosition.Visible) Then Return
            If _duplication Is Nothing Then Return

            Dim shapeBytes As Integer = CInt(frameInfo.PointerShapeBufferSize)
            If shapeBytes <= 0 Then Return
            EnsureCursorShapeBuffer(shapeBytes)

            Dim shapeInfo As OutduplPointerShapeInfo = Nothing
            Dim needed As UInteger = 0
            Dim hr As Result = _duplication.GetFramePointerShape(
                CUInt(shapeBytes), _cursorShapeBuffer, needed, shapeInfo)
            If Not hr.Success Then Return

            Dim w As Integer = CInt(shapeInfo.Width)
            Dim h As Integer = CInt(shapeInfo.Height)
            If w <= 0 OrElse h <= 0 OrElse w > 512 OrElse h > 512 Then Return

            ' Monochrome shapes double as AND-mask rows then XOR-mask rows.
            Dim drawH As Integer = h
            If CInt(shapeInfo.Type) = 1 Then drawH = h \ 2

            Dim x0 As Integer = frameInfo.PointerPosition.Position.X
            Dim y0 As Integer = frameInfo.PointerPosition.Position.Y
            If x0 < 0 OrElse y0 < 0 Then Return
            Dim cx As Integer = Math.Min(w, _outputWidth - x0)
            Dim cy As Integer = Math.Min(drawH, _outputHeight - y0)
            If cx <= 0 OrElse cy <= 0 Then Return   ' fully off-screen

            EnsureCursorStage(cx, cy)

            ' screen region under the cursor -> small CPU staging texture
            _deviceContext.CopySubresourceRegion(_cursorStage, 0UI, CUInt(x0), CUInt(y0), 0UI, target, 0UI, New Vortice.Mathematics.Box(x0, y0, 0, x0 + cx, y0 + cy, 1))

            Dim map As MappedSubresource = Nothing
            _deviceContext.Map(_cursorStage, 0UI, MapMode.ReadWrite, Vortice.Direct3D11.MapFlags.None, map)
            If map.DataPointer = IntPtr.Zero Then
                _logger.Warning("DdagrabBackend: cursor stage Map returned null data - skipping cursor this frame")
                Return
            End If

            Dim shapeType As Integer = CInt(shapeInfo.Type)
            Dim pitch As Integer = CInt(shapeInfo.Pitch)
            If pitch <= 0 Then pitch = w * 4
            Dim sb As IntPtr = _cursorShapeBuffer
            Dim fb As IntPtr = map.DataPointer
            Dim fstride As Integer = CInt(map.RowPitch)
            Try

                For row As Integer = 0 To cy - 1
                    Dim dstRow As Integer = row * fstride
                    Dim srcRow As Integer = row * pitch
                    For col As Integer = 0 To cx - 1
                        Dim o As Integer = dstRow + col * 4
                        Dim db As Integer = Marshal.ReadByte(fb, o)
                        Dim dg As Integer = Marshal.ReadByte(fb, o + 1)
                        Dim dr As Integer = Marshal.ReadByte(fb, o + 2)

                        If shapeType = 2 Then
                            ' COLOR: 32bpp BGRA, straight alpha over the screen.
                            Dim so As Integer = srcRow + col * 4
                            Dim sa As Integer = Marshal.ReadByte(sb, so + 3)
                            If sa = 255 Then
                                Marshal.WriteByte(fb, o, Marshal.ReadByte(sb, so))
                                Marshal.WriteByte(fb, o + 1, Marshal.ReadByte(sb, so + 1))
                                Marshal.WriteByte(fb, o + 2, Marshal.ReadByte(sb, so + 2))
                            ElseIf sa <> 0 Then
                                Marshal.WriteByte(fb, o, CByte((Marshal.ReadByte(sb, so) * sa + db * (255 - sa)) \ 255))
                                Marshal.WriteByte(fb, o + 1, CByte((Marshal.ReadByte(sb, so + 1) * sa + dg * (255 - sa)) \ 255))
                                Marshal.WriteByte(fb, o + 2, CByte((Marshal.ReadByte(sb, so + 2) * sa + dr * (255 - sa)) \ 255))
                            End If
                        ElseIf shapeType = 3 Then
                            ' MASKED_COLOR: alpha 0 -> XOR the screen with BGR; else replace.
                            Dim so As Integer = srcRow + col * 4
                            Dim sa As Integer = Marshal.ReadByte(sb, so + 3)
                            If sa = 0 Then
                                Marshal.WriteByte(fb, o, CByte(db Xor Marshal.ReadByte(sb, so)))
                                Marshal.WriteByte(fb, o + 1, CByte(dg Xor Marshal.ReadByte(sb, so + 1)))
                                Marshal.WriteByte(fb, o + 2, CByte(dr Xor Marshal.ReadByte(sb, so + 2)))
                            Else
                                Marshal.WriteByte(fb, o, Marshal.ReadByte(sb, so))
                                Marshal.WriteByte(fb, o + 1, Marshal.ReadByte(sb, so + 1))
                                Marshal.WriteByte(fb, o + 2, Marshal.ReadByte(sb, so + 2))
                            End If
                        Else
                            ' MONOCHROME: 1bpp AND-mask rows, then XOR-mask rows.
                            ' out = (screen AND andBit) XOR xorBit, fully opaque.
                            Dim andByte As Integer = Marshal.ReadByte(sb, srcRow + (col >> 3))
                            Dim andBit As Integer = (andByte >> (7 - (col And 7))) And 1
                            Dim xorByte As Integer = Marshal.ReadByte(sb, (row + drawH) * pitch + (col >> 3))
                            Dim xorBit As Integer = (xorByte >> (7 - (col And 7))) And 1
                            If andBit = 1 Then
                                If xorBit = 1 Then
                                    Marshal.WriteByte(fb, o, CByte(db Xor 255))
                                    Marshal.WriteByte(fb, o + 1, CByte(dg Xor 255))
                                    Marshal.WriteByte(fb, o + 2, CByte(dr Xor 255))
                                End If
                            Else
                                Dim v As Integer = If(xorBit = 1, 255, 0)
                                Marshal.WriteByte(fb, o, CByte(v))
                                Marshal.WriteByte(fb, o + 1, CByte(v))
                                Marshal.WriteByte(fb, o + 2, CByte(v))
                            End If
                        End If
                    Next
                Next
            Finally
                _deviceContext.Unmap(_cursorStage, 0)
            End Try

            _deviceContext.CopySubresourceRegion(target, 0UI, CUInt(x0), CUInt(y0), 0UI, _cursorStage, 0UI, New Vortice.Mathematics.Box(0, 0, 0, cx, cy, 1))

            If Not _cursorLoggedOk Then
                _cursorLoggedOk = True
                _logger.Info($"DdagrabBackend: cursor composition active (shape type={shapeType}, {w}x{h})")
            End If
        End Sub

        Private Sub EnsureCursorShapeBuffer(bytes As Integer)
            If _cursorShapeBuffer <> IntPtr.Zero AndAlso _cursorShapeBufferBytes >= bytes Then Return
            If _cursorShapeBuffer <> IntPtr.Zero Then Marshal.FreeHGlobal(_cursorShapeBuffer)
            _cursorShapeBuffer = Marshal.AllocHGlobal(bytes)
            _cursorShapeBufferBytes = bytes
        End Sub

        Private Sub EnsureCursorStage(w As Integer, h As Integer)
            If _cursorStage IsNot Nothing AndAlso _cursorStageW = w AndAlso _cursorStageH = h Then Return
            _cursorStage?.Dispose()
            Dim desc As New Texture2DDescription() With {
                .Width = CUInt(w),
                .Height = CUInt(h),
                .MipLevels = 1,
                .ArraySize = 1,
                .Format = Format.B8G8R8A8_UNorm,
                .SampleDescription = New SampleDescription(1, 0),
                .Usage = ResourceUsage.Staging,
                .BindFlags = BindFlags.None,
                .CPUAccessFlags = CpuAccessFlags.Read Or CpuAccessFlags.Write,
                .MiscFlags = ResourceOptionFlags.None
            }
            _cursorStage = _device.CreateTexture2D(desc)
            _cursorStageW = w
            _cursorStageH = h
        End Sub

    End Class
End Namespace
