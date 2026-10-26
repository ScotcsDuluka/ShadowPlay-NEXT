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
'   - Foundation NOT modified (DulukaEngine.vb @ 82d792ab FROZEN)
'   - IVideoFrame contract unchanged (D3D11VideoFrame is additive)
'   - No reflection, no TryCast hack
'   - No reference to DulukaEngine.Encoder.Nvenc (no backward dep)
'   - Frame ownership = TRANSFER on Pushed/Replaced; BORROW never

Imports System
Imports System.Diagnostics
Imports System.Linq
Imports System.Threading
Imports DulukaEngine.Diagnostics
Imports SharpGen.Runtime
Imports Vortice.Direct3D
Imports Vortice.Direct3D11
Imports Vortice.DXGI
' Alias to disambiguate ResultCode (Vortice.DXGI vs Vortice.Direct3D11).
Imports ResultCode = Vortice.DXGI.ResultCode
Imports System.Runtime.InteropServices

Namespace DulukaEngine.Video.Backends.Ddagrab

    Partial Public NotInheritable Class DdagrabBackend
        Implements IVideoCaptureBackend
        Implements IVideoBackendDiagnostics
        Implements IVideoCaptureRuntime

        ' ---- backend state ----
        Private ReadOnly _sync As New Object()
        Private ReadOnly _logger As EngineLogger

        Private _state As DdagrabBackendState = DdagrabBackendState.Created
        Private _disposed As Boolean = False
        ' B1: exactly one native cleanup owner. A Dispose timeout quarantines
        ' resources until the worker confirms quiescence at its exit tail.
        Private _persistentCleanupClaimed As Integer = 0

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
        Private _sourceGeneration As Long
        Private _reservedGeneration As Long
        ' Metadata reservation only; does not create a device/duplication or start
        ' acquisition. Controller constructs the sink from this stamp before Start.
        Public Function PrepareSourceGeneration() As Long
            SyncLock _sync
                If _disposed OrElse (_state <> DdagrabBackendState.Initialized AndAlso _state <> DdagrabBackendState.Stopped) Then
                    Throw New InvalidOperationException("source_generation_requires_initialized_idle_backend")
                End If
                If _reservedGeneration = 0 Then _reservedGeneration = SourceGenerationIds.Allocate()
                Return _reservedGeneration
            End SyncLock
        End Function
        Public ReadOnly Property SourceGeneration As Long
            Get
                Return Threading.Interlocked.Read(_sourceGeneration)
            End Get
        End Property
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
        Public ReadOnly Property OutputWidth As Integer Implements IVideoCaptureRuntime.OutputWidth
            Get
                Return _outputWidth
            End Get
        End Property

        Public ReadOnly Property OutputHeight As Integer Implements IVideoCaptureRuntime.OutputHeight
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
        Public ReadOnly Property OutputRefreshRate As Integer Implements IVideoCaptureRuntime.OutputRefreshRate
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

        Private _requestedOutputIndex As Integer = -1
        Private _requestedTarget As CaptureTarget
        Private _actualTarget As CaptureTarget

        Public Property RequestedTarget As CaptureTarget
            Get
                Return _requestedTarget
            End Get
            Set(value As CaptureTarget)
                If _state <> DdagrabBackendState.Created Then Throw New InvalidOperationException("capture_display_change_requires_rebuild")
                If value Is Nothing OrElse Not value.Eligible Then Throw New ArgumentException("capture_target_ineligible")
                _requestedTarget = value
                _requestedOutputIndex = value.OutputIndex
            End Set
        End Property

        Public ReadOnly Property ActualTarget As CaptureTarget Implements IVideoCaptureRuntime.ActualTarget
            Get
                Return _actualTarget
            End Get
        End Property

        ''' <summary>
        ''' DXGI output index local to RequestedTarget's adapter (not a Windows
        ''' Screen.AllScreens index). Provider factories supply the complete target.
        ''' Must be set before Initialize. An explicitly chosen output is
        ''' NEVER silently replaced when it becomes disconnected.
        ''' </summary>
        Public Property RequestedOutputIndex As Integer
            Get
                Return _requestedOutputIndex
            End Get
            Set(value As Integer)
                If value < -1 Then Throw New ArgumentOutOfRangeException(NameOf(value))
                If _state <> DdagrabBackendState.Created Then
                    Throw New InvalidOperationException("capture_display_change_requires_rebuild")
                End If
                If _requestedTarget IsNot Nothing AndAlso value <> _requestedTarget.OutputIndex Then
                    Throw New ArgumentException("capture_output_index_conflict")
                End If
                _requestedOutputIndex = value
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
            If context Is Nothing Then Throw New ArgumentNullException(NameOf(context))
            RunInitialize(Sub() InitializeResources(context))
        End Sub

        Private Sub InitializeResources(context As IVideoBackendContext)
            If context.BackendKind <> VideoBackendKind.Ddagrab Then
                Throw New VideoBackendConfigurationException(
                    "DdagrabBackend.Initialize: context.BackendKind must be Ddagrab, but was " &
                    context.BackendKind.ToString() & ".")
            End If

            _context = context

            ' Resolve a complete target before creating any capture device.
            ' Provider/default policy belongs to the composition factory.
            Dim inventory = DxgiDisplayInventory.Collect()
            Dim selectedTarget As CaptureTarget = _requestedTarget
            If selectedTarget Is Nothing Then
                selectedTarget = inventory.Outputs.FirstOrDefault(Function(t) t.Eligible AndAlso
                    (_requestedOutputIndex < 0 OrElse t.OutputIndex = _requestedOutputIndex))
            End If
            selectedTarget = CaptureTarget.RequireCurrent(selectedTarget, inventory.Outputs)
            _requestedOutputIndex = selectedTarget.OutputIndex

            ' ─── Create D3D11 device on the selected output's adapter ───
            ' Per audit verdict #1: each backend owns its own D3D11 device.
            ' Device flags: BgraSupport (Desktop Duplication) + VideoSupport
            ' (interop-friendly; required by some NVENC paths).
            _dxgiFactory = DXGI.CreateDXGIFactory1(Of IDXGIFactory1)()

            Dim selectedIdx As Integer = -1
            Dim adapterIdx As Integer = 0
            Dim adapter1 As IDXGIAdapter1 = Nothing
            Do While _dxgiFactory.EnumAdapters1(CUInt(adapterIdx), adapter1).Success
                Using a As IDXGIAdapter1 = adapter1
                    Dim desc As AdapterDescription1 = a.Description1
                    _logger.Info($"  Adapter [{adapterIdx}] {desc.Description} " &
                                 $"(0x{desc.VendorId:x4}:0x{desc.DeviceId:x4})")
                    If desc.Luid.LowPart = selectedTarget.Adapter.Low AndAlso desc.Luid.HighPart = selectedTarget.Adapter.High AndAlso
                        desc.VendorId = selectedTarget.Adapter.VendorId AndAlso desc.DeviceId = selectedTarget.Adapter.DeviceId Then
                        selectedIdx = adapterIdx
                    End If
                End Using
                adapterIdx += 1
            Loop

            If selectedIdx < 0 Then
                Throw New VideoBackendRuntimeException(
                    "capture_adapter_disconnected")
            End If

            _dxgiFactory.EnumAdapters1(CUInt(selectedIdx), _adapter).CheckError()
            Dim selectedDesc As AdapterDescription1 = _adapter.Description1
            _adapterLuidLow = selectedDesc.Luid.LowPart
            _adapterLuidHigh = selectedDesc.Luid.HighPart
            _logger.Info($"DdagrabBackend: selected adapter #{selectedIdx}: {selectedDesc.Description}, {selectedTarget.Adapter}")

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

            Using dxgiDevice = _device.QueryInterface(Of IDXGIDevice)()
                Using parent = dxgiDevice.GetParent(Of IDXGIAdapter)()
                    Dim actual = parent.Description
                    If actual.Luid.LowPart <> _adapterLuidLow OrElse actual.Luid.HighPart <> _adapterLuidHigh OrElse
                        actual.VendorId <> selectedDesc.VendorId OrElse actual.DeviceId <> selectedDesc.DeviceId Then
                        Throw New VideoBackendRuntimeException("capture_device_adapter_mismatch")
                    End If
                End Using
            End Using

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
                If _requestedOutputIndex >= 0 Then
                    If scanIdx = _requestedOutputIndex Then
                        If Not attached Then
                            candidate.Dispose()
                            Throw New VideoBackendRuntimeException(
                                "capture_display_disconnected: selected DXGI output is not attached")
                        End If
                        out_ = candidate
                        outIdx = scanIdx
                        Exit Do
                    End If
                    candidate.Dispose()
                    scanIdx += 1
                    Continue Do
                End If
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
            _actualTarget = New CaptureTarget(selectedTarget.Adapter, outIdx, outDesc.DeviceName, outDesc.Monitor,
                _outputWidth, _outputHeight, outDesc.DesktopCoordinates.Left, outDesc.DesktopCoordinates.Top,
                CInt(outDesc.Rotation), outDesc.AttachedToDesktop, selectedTarget.OutputTechnology, selectedTarget.TopologyId,
                selectedTarget.MonitorDevicePath)
            If Not selectedTarget.SameOutput(_actualTarget) Then Throw New VideoBackendRuntimeException("capture_output_runtime_mismatch")
            CaptureTarget.RequireCurrent(_actualTarget, DxgiDisplayInventory.Collect().Outputs)
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
                    Using output1 As IDXGIOutput1 = _output.QueryInterface(Of IDXGIOutput1)()
                        _duplication = output1.DuplicateOutput(_device)
                    End Using
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

        End Sub

        Public Sub Start(sink As IVideoFrameSink) Implements IVideoCaptureBackend.Start
            RunStart(sink, AddressOf RecreateDuplication, AddressOf WorkerLoop)
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
        Public ReadOnly Property AccessLostCount As Long Implements IVideoCaptureRuntime.AccessLostCount
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

        Public ReadOnly Property TexturesCreated As Long Implements IVideoCaptureRuntime.TexturesCreated
            Get
                SyncLock _sync
                    Return _texturesCreated
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property TexturesDisposed As Long Implements IVideoCaptureRuntime.TexturesDisposed
            Get
                SyncLock _sync
                    Return _texturesDisposed
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property FramesPushed As Long Implements IVideoCaptureRuntime.FramesPushed
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

        ' Convert DXGI QPC ticks to the engine's 100ns timestamp domain.
        ' Keep the quotient/remainder groups explicit: VB operator precedence
        ' must not change the unit conversion into (qpcTicks Mod f).
        Private Shared Function QpcTicksTo100ns(qpcTicks As Long) As Long
            Dim f As Long = Stopwatch.Frequency
            If f <= 0 Then Return 0
            Return SourceTimeUnits.QpcTo100ns(qpcTicks, f)
        End Function

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
            Quarantined ' B1: Dispose requested; worker still owns native COM resources
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
