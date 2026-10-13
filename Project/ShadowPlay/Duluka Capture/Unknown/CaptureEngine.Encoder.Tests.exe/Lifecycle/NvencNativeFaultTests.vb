Option Strict On
Option Explicit On
Option Infer On

' NvencNativeFaultTests.vb — C/2: hardware-PROVEN NVENC failure / dispose
' concurrency closure (C/6 hardware-blocked items 1-3). Every scenario here
' runs the REAL NvencEncoderBackend against the REAL driver on the machine's
' NVIDIA GPU — no Fake backend anywhere.
'
' C/1 hardware-gate contract (aligned with Video.Tests HardwareGate F-01):
'   - NVIDIA machine  → all five scenarios RUN for real (never skipped);
'   - non-NVIDIA      → all five report SKIP with the probe reason (never
'                       FAIL, never PASS, never counted toward the exit
'                       code) — a missing GPU is an environment property,
'                       not a regression.
'
' P1  Initialize failure AFTER partial acquisition:
'       device + function table + OPEN encode session are acquired, then the
'       DRIVER itself deterministically rejects NvEncInitializeEncoder
'       (8192x8192 exceeds Pascal H.264's 4096 max — a naturally
'       reproducible, driver-API-level failure; no injection hook, no GPU
'       state corruption). Proves: Created → acquired → driver failure →
'       Faulted → full native unwind (DestroyEncoder + function table +
'       D3D11 device) → no session leak across repeated failures (budget
'       probe still initializes).
' P2  Encode vs Dispose: real frames flowing, Barrier(2)-synchronized
'       dispose mid-encode, 24 repetitions. Proves: no use-after-dispose,
'       only expected managed faults, bounded dispose (C/6 in-flight gate),
'       final state Disposed, no deadlock, session budget intact afterwards.
' P3  Post-fault containment: controlled mid-session fault (frame dimension
'       mismatch — the same TransitionToFaulted gate production frames hit)
'       during a live encode session. Proves: Faulted, further Encode
'       rejected, exactly one recorded error (no spam loop), clean Dispose,
'       and the NEXT session initializes and encodes real packets.
'
' Serializer audit note: this file touches NO serializer/ABI code —
' NV_ENC_CONFIG / union / preset / CBR-filler layout is exercised via the
' production paths only.

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Threading
Imports CaptureEngine.Diagnostics
Imports CaptureEngine.Encoder
Imports CaptureEngine.Encoder.Nvenc
Imports CaptureEngine.Video
Imports CaptureEngine.Encoder.Tests
Imports Vortice.DXGI

Namespace CaptureEngine.Encoder.Tests.Lifecycle

    Friend NotInheritable Class NvencNativeFaultTests

        Private Const Reps As Integer = 24          ' P2 repetition count (≥20 required)
        Private Const FailureReps As Integer = 5    ' P1 consecutive driver-rejected inits

        Public Shared Sub RunAll(runner As Action(Of String, Action))
            runner("NVENC-NAT-0: hardware gate — real NVENC + NVIDIA adapter evidence", AddressOf Test_HardwareGate)
            runner("NVENC-NAT-P1: driver-rejected Initialize after session open → Faulted + full native unwind", AddressOf Test_InitializeFailureAfterAcquire)
            runner("NVENC-NAT-P1: " & FailureReps & " consecutive init failures leak no NVENC session (budget probe)", AddressOf Test_NoSessionLeakAcrossFailures)
            runner("NVENC-NAT-P2: Encode vs Dispose race — " & Reps & " reps, Barrier-synced, real frames", AddressOf Test_EncodeVsDispose)
            runner("NVENC-NAT-P3: controlled mid-session fault → containment + next session real", AddressOf Test_PostFaultContainment)
        End Sub

        ' ── shared helpers ────────────────────────────────────────────────

        Private Shared Sub DiscardLog(m As String)
        End Sub

        Private Shared Function NewBackend(ParamArray sinks() As Action(Of String)) As NvencEncoderBackend
            If sinks IsNot Nothing AndAlso sinks.Length > 0 Then
                Return New NvencEncoderBackend(New EngineLogger("nvenc-native", EngineLogger.LogLevel.Info,
                    Sub(m)
                        For Each s As Action(Of String) In sinks
                            s(m)
                        Next
                    End Sub))
            End If
            Return New NvencEncoderBackend(New EngineLogger("nvenc-native", EngineLogger.LogLevel.Info, AddressOf DiscardLog))
        End Function

        Private Shared Function ValidSmallConfig() As EncoderConfig
            Dim c As New EncoderConfig()
            c.CodecKey = "NVENC_H264"
            c.RateControl = "cbr"
            c.BitrateBps = 2000000L
            c.GopSize = 60
            c.FrameRateFps = 30
            c.ExpectedWidth = 640
            c.ExpectedHeight = 480
            Return c
        End Function

        ''' <summary>Real D3D11 device on the primary (NVIDIA) adapter — the
        ''' same factory the production encoder uses.</summary>
        Private Shared Function CreateDevice() as Internal.D3D11DeviceResult
            Dim f As New Internal.D3D11DeviceFactory(New EngineLogger("nvenc-native-dev", EngineLogger.LogLevel.Info, AddressOf DiscardLog))
            Dim r As Internal.D3D11DeviceResult = f.Create()
            If r Is Nothing Then Throw New Exception("D3D11DeviceFactory.Create returned Nothing — no usable GPU")
            Return r
        End Function

        ''' <summary>Real BGRA8 GPU texture with an NT shared handle (the exact
        ''' Ddagrab shared-handle-mode recipe), wrapped in a test-only frame
        ''' implementing the SAME production contracts (IVideoFrame +
        ''' ID3D11VideoFrame) the encoder consumes.</summary>
        Private Shared Function CreateSharedFrame(dev As Internal.D3D11DeviceResult,
                                                  w As Integer, h As Integer, seq As Long) As TestGpuFrame
            Dim desc As New Vortice.Direct3D11.Texture2DDescription() With {
                .Width = CUInt(w), .Height = CUInt(h), .MipLevels = 1UI, .ArraySize = 1UI,
                .Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                .SampleDescription = New Vortice.DXGI.SampleDescription(1, 0),
                .Usage = Vortice.Direct3D11.ResourceUsage.Default,
                .BindFlags = Vortice.Direct3D11.BindFlags.None,
                .CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                .MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.Shared Or
                             Vortice.Direct3D11.ResourceOptionFlags.SharedNthandle
            }
            Dim tex As Vortice.Direct3D11.ID3D11Texture2D = dev.Device.CreateTexture2D(desc)
            Dim dxgiRes As IDXGIResource1 = tex.QueryInterface(Of IDXGIResource1)()
            Dim handle As IntPtr = dxgiRes.CreateSharedHandle(Nothing,
                SharedResourceFlags.Read Or SharedResourceFlags.Write, Nothing)
            dxgiRes.Dispose()
            Return New TestGpuFrame(tex, w, h, seq, Stopwatch.GetTimestamp(), seq * 333333L, handle)
        End Function

        ''' <summary>
        ''' Test-only GPU frame: implements the SAME production contracts the
        ''' encoder consumes (CaptureEngine.Video.IVideoFrame + ID3D11VideoFrame)
        ''' around a real BGRA8 D3D11 texture with a real NT shared handle.
        ''' Behavior mirrors D3D11VideoFrame: NativeTexture goes Nothing after
        ''' Dispose (the encoder's defensive gate), Dispose releases the texture.
        ''' </summary>
        Private NotInheritable Class TestGpuFrame
            Implements IVideoFrame
            Implements ID3D11VideoFrame

            Private ReadOnly _tex As Vortice.Direct3D11.ID3D11Texture2D
            Private ReadOnly _handle As IntPtr
            Private ReadOnly _w As Integer
            Private ReadOnly _h As Integer
            Private ReadOnly _seq As Long
            Private ReadOnly _captureTicks As Long
            Private ReadOnly _ptsTicks As Long
            Private _disposed As Boolean

            Public Sub New(tex As Vortice.Direct3D11.ID3D11Texture2D,
                           w As Integer, h As Integer, seq As Long,
                           captureTicks As Long, ptsTicks As Long, handle As IntPtr)
                _tex = tex
                _w = w
                _h = h
                _seq = seq
                _captureTicks = captureTicks
                _ptsTicks = ptsTicks
                _handle = handle
            End Sub

            Public ReadOnly Property Origin As VideoFrameOrigin Implements IVideoFrame.Origin
                Get
                    Return VideoFrameOrigin.GpuD3D11Texture
                End Get
            End Property

            Public ReadOnly Property PixelFormat As VideoPixelFormat Implements IVideoFrame.PixelFormat
                Get
                    Return VideoPixelFormat.Bgra8
                End Get
            End Property

            Public ReadOnly Property Dimensions As VideoFrameDimensions Implements IVideoFrame.Dimensions
                Get
                    Return New VideoFrameDimensions(_w, _h)
                End Get
            End Property

            Public ReadOnly Property Diagnostics As FrameDiagnostics Implements IVideoFrame.Diagnostics
                Get
                    Return New FrameDiagnostics(_seq, _captureTicks, _ptsTicks)
                End Get
            End Property

            Public ReadOnly Property NativeTexture As Object Implements ID3D11VideoFrame.NativeTexture
                Get
                    If _disposed Then Return Nothing
                    Return _tex
                End Get
            End Property

            Public ReadOnly Property SharedHandle As IntPtr Implements ID3D11VideoFrame.SharedHandle
                Get
                    Return _handle
                End Get
            End Property

            Public Sub Dispose() Implements IDisposable.Dispose
                If _disposed Then Return
                _disposed = True
                _tex.Dispose()
            End Sub
        End Class

        ' ── gate ─────────────────────────────────────────────────────────

        ' ── C/1 hardware gate (one-time probe, cached) ────────────────────
        ' Probe = the REAL production acquisition path: D3D11DeviceFactory on
        ' the NVIDIA adapter + NvEncFunctionTable.TryLoad. No fake shortcuts.
        Private Shared _probed As Boolean = False
        Private Shared _nvencAvailable As Boolean = False
        Private Shared _gateReason As String = ""
        Private Shared _gateAdapter As String = ""

        Private Shared Sub EnsureNvencProbed()
            If _probed Then Return
            _probed = True
            Try
                Dim dev As Internal.D3D11DeviceResult = CreateDevice()
                Try
                    _gateAdapter = $"{dev.Description} (vendor=0x{dev.VendorId:x4} device=0x{dev.DeviceId:x4})"
                    Dim ft As New Internal.NvEncFunctionTable(New EngineLogger("nvenc-native-gate", EngineLogger.LogLevel.Info, AddressOf DiscardLog))
                    Try
                        If ft.TryLoad() Then
                            _nvencAvailable = True
                            _gateReason = ""
                        Else
                            _gateReason = "NvEncFunctionTable.TryLoad failed — NVENC API unavailable"
                        End If
                    Finally
                        ft.Dispose()
                    End Try
                Finally
                    dev.Dispose()
                End Try
            Catch ex As Exception
                _nvencAvailable = False
                _gateReason = ex.Message
            End Try
        End Sub

        ''' <summary>C/1: throw SkipException (environment not capable) unless
        ''' this machine has a REAL NVIDIA adapter with a loadable NVENC API.
        ''' Never throws on an NVIDIA machine.</summary>
        Private Shared Sub RequireNvenc()
            EnsureNvencProbed()
            If Not _nvencAvailable Then
                Throw New SkipException("NVENC-NAT requires real NVIDIA NVENC hardware: " & _gateReason)
            End If
        End Sub

        Private Shared Sub Test_HardwareGate()
            RequireNvenc()
            ' On an NVIDIA machine the gate doubles as the evidence line.
            Console.WriteLine($"    adapter: {_gateAdapter}")
        End Sub

        ' ── P1: initialize failure after partial acquisition ─────────────

        ''' <summary>
        ''' NVP_PROBE_TEARDOWN=1 — drives the exact production acquisition and
        ''' failure-unwind sequence step by step (production internals, linked)
        ''' printing a marker between steps, to isolate WHICH teardown step
        ''' triggers the deferred access violation after a driver-rejected
        ''' NvEncInitializeEncoder. Test-side experiment only.
        ''' </summary>
        Private Shared Sub Test_TeardownSequenceProbe()
            ' NVP_PROBE_TEARDOWN=2 → SMOKING-GUN variant: destroy the encoder
            ' TWICE (mirroring the production double-unwind: the explicit
            ' failure path destroys everything, then its Throw lands in the
            ' generic Catch which unwinds the same natives again).
            Dim doubleDestroy = Environment.GetEnvironmentVariable("NVP_PROBE_TEARDOWN") = "2"
            Dim mark As Action(Of String) =
                Sub(m)
                    Console.WriteLine("    [teardown] " & m)
                    Console.Out.Flush()
                End Sub

            Dim dev As Internal.D3D11DeviceResult = CreateDevice()
            mark("device created")

            Dim ft As New Internal.NvEncFunctionTable(New EngineLogger("nvenc-probe", EngineLogger.LogLevel.Info,
                Sub(m) mark("log: " & m)))
            TestHelpers.Assert(ft.TryLoad(), "function table load failed")

            Dim handle As IntPtr = IntPtr.Zero
            Dim sp As Internal.NvEncodeAPI.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS = Nothing
            sp.version = Internal.NvEncodeAPI.NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER
            sp.deviceType = Internal.NvEncodeAPI.NV_ENC_DEVICE_DIRECTX
            sp.device = dev.Device.NativePointer
            sp.reserved = IntPtr.Zero
            sp.apiVersion = Internal.NvEncodeAPI.NVENCAPI_VERSION
            sp.reserved1 = Nothing
            sp.reserved2 = Nothing
            Dim openStatus As UInteger = ft.OpenEncodeSessionEx.Invoke(sp, handle)
            TestHelpers.Assert(openStatus = Internal.NvEncodeAPI.NV_ENC_SUCCESS, $"session open failed: {openStatus}")
            mark($"session opened: 0x{handle.ToInt64():x16}")

            Dim initParams As Internal.NvEncodeAPI.NV_ENC_INITIALIZE_PARAMS =
                Internal.NvEncParamBuilder.BuildInitializeParams(4097UI, 100UI, 4097UI, 100UI, 30, "p4")
            Dim presetCfg As New Internal.NvEncodeAPI.NV_ENC_PRESET_CONFIG()
            presetCfg.version = Internal.NvEncodeAPI.NV_ENC_PRESET_CONFIG_VER
            presetCfg.presetCfg = Nothing
            presetCfg.presetCfg.version = Internal.NvEncodeAPI.NV_ENC_CONFIG_VER
            Dim pcStatus As UInteger = ft.GetPresetConfigEx.Invoke(handle, Internal.NvEncodeAPI.NV_ENC_CODEC_H264_GUID,
                                                                   initParams.presetGUID, initParams.tuningInfo, presetCfg)
            mark($"preset config: status={pcStatus}")
            Dim encodeCfg As Internal.NvEncodeAPI.NV_ENC_CONFIG = presetCfg.presetCfg
            Internal.NvEncParamBuilder.EnsureArrays(encodeCfg)
            Internal.NvEncParamBuilder.ApplyVideoSettings(encodeCfg, 2000000L, 0L, 0L, "cbr", 60)
            Dim raw As Byte() = Internal.NvEncConfigSerializer.Serialize(encodeCfg)
            Dim cfgPtr As IntPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(raw.Length)
            System.Runtime.InteropServices.Marshal.Copy(raw, 0, cfgPtr, raw.Length)
            initParams.encodeConfig = cfgPtr

            Dim st As UInteger = ft.InitializeEncoder.Invoke(handle, initParams)
            mark($"InitializeEncoder status={st} (expected 8 = NV_ENC_ERR_INVALID_PARAM)")
            System.Runtime.InteropServices.Marshal.FreeHGlobal(cfgPtr)

            mark("step 1: DestroyEncoder…")
            Try : ft.DestroyEncoder.Invoke(handle) : Catch ex As Exception : mark($"   destroy threw: {ex.Message}") : End Try
            mark("step 1 done")

            If doubleDestroy Then
                mark("step 1x: SECOND DestroyEncoder on the destroyed handle (production double-unwind)…")
                Try : ft.DestroyEncoder.Invoke(handle) : Catch ex As Exception : mark($"   second destroy threw: {ex.Message}") : End Try
                mark("step 1x done")
            End If

            Thread.Sleep(700)
            mark("step 1b: alive 700ms after DestroyEncoder")

            mark("step 2: function table Dispose…")
            ft.Dispose()
            mark("step 2 done")

            Thread.Sleep(700)
            mark("step 2b: alive 700ms after fn-table dispose")

            mark("step 3: D3D11 device Dispose…")
            dev.Dispose()
            mark("step 3 done")

            Thread.Sleep(1500)
            mark("SURVIVED full teardown sequence 1.5s — no deferred AV")
        End Sub


        Private Shared Sub Test_InitializeFailureAfterAcquire()
            RequireNvenc()
            If Environment.GetEnvironmentVariable("NVP_PROBE_TEARDOWN") = "1" OrElse Environment.GetEnvironmentVariable("NVP_PROBE_TEARDOWN") = "2" Then
                Test_TeardownSequenceProbe()
                Return
            End If
            Dim logs As New List(Of String)
            Dim probeDims As String = Environment.GetEnvironmentVariable("NVP_PROBE_DIMS")
            Dim enc As NvencEncoderBackend = NewBackend(Sub(m)
                                                            SyncLock logs : logs.Add(m) : End SyncLock
                                                            If probeDims IsNot Nothing Then Console.WriteLine("      LOG " & m)
                                                        End Sub)
            ' Deterministic DRIVER-level failure: 8192x8192 passes every managed
            ' config gate but exceeds Pascal H.264's 4096 max — the rejection
            ' happens inside NvEncInitializeEncoder, i.e. AFTER device + function
            ' table + OPEN encode session were acquired.
            '
            ' NVP_PROBE_DIMS=WxH overrides the dims (diagnosis mode: prints the
            ' outcome instead of asserting, so each variant runs in its own
            ' process and a native AV cannot mask later variants).
            Dim cfg As EncoderConfig = ValidSmallConfig()
            Dim probeMode As Boolean = probeDims IsNot Nothing
            If probeMode Then
                Dim parts As String() = probeDims.Split("x"c)
                cfg.ExpectedWidth = Integer.Parse(parts(0))
                cfg.ExpectedHeight = Integer.Parse(parts(1))
            Else
                cfg.ExpectedWidth = 8192
                cfg.ExpectedHeight = 8192
            End If

            Dim threw As Exception = Nothing
            Dim initialized As Boolean = False
            Try
                enc.Initialize(cfg)
                initialized = True
            Catch ex As Exception
                threw = ex
            End Try

            If probeMode Then
                Dim nativeLine As String = "none"
                SyncLock logs
                    Console.WriteLine("    ── full log tail ──")
                    For Each l As String In logs
                        Console.WriteLine("      " & l)
                    Next
                    nativeLine = logs.Find(Function(l) l.Contains("NvEncInitializeEncoder"))
                    If nativeLine Is Nothing Then nativeLine = logs.Find(Function(l) l.Contains("session opened"))
                End SyncLock
                Console.WriteLine($"    PROBE {cfg.ExpectedWidth}x{cfg.ExpectedHeight}: initialized={initialized} " &
                                  $"exc={If(threw IsNot Nothing, threw.GetType().Name & ": " & threw.Message, "none")}")
                ' NVP_PROBE_NODISPOSE=1 → stop BEFORE the post-fault Dispose so
                ' the AV site (unwind vs dispose-from-faulted) can be isolated.
                If Environment.GetEnvironmentVariable("NVP_PROBE_NODISPOSE") = "1" Then
                    Console.WriteLine($"    state after fault: {enc.CurrentState} — SKIPPING Dispose (AV-site probe)")
                    Thread.Sleep(2000)
                    Console.WriteLine("    probe still alive 2s after fault (no deferred AV): True")
                    Return
                End If
                enc.Dispose()
                Console.WriteLine("    PROBE: Dispose after fault completed, state=" & enc.CurrentState.ToString())
                Return
            End If

            TestHelpers.Assert(threw IsNot Nothing,
                "8192x8192 unexpectedly initialized on this GPU — injection premise invalid on this hardware")
            TestHelpers.Assert(TypeOf threw Is EncoderRuntimeException,
                "expected EncoderRuntimeException, got " & threw.GetType().Name & ": " & threw.Message)

            SyncLock logs
                TestHelpers.Assert(logs.Exists(Function(l) l.Contains("NvEncInitializeEncoder failed")),
                    "failure did not land in the native NvEncInitializeEncoder call — not a post-acquisition fault")
                TestHelpers.Assert(logs.Exists(Function(l) l.Contains("NVENC encoder session opened")),
                    "session was not opened before the fault — premise is partial-acquisition failure")
            End SyncLock

            TestHelpers.AssertEqual(EncoderState.Faulted, enc.CurrentState,
                "failed Initialize must converge to terminal Faulted")
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState,
                "Dispose from Faulted must reach Disposed")
        End Sub

        Private Shared Sub Test_NoSessionLeakAcrossFailures()
            RequireNvenc()
            ' 5 consecutive driver-rejected initializes on fresh backends. Each
            ' failure unwinds device + function table + session; if the unwind
            ' leaked the NVENC session, repeated failures would consume the
            ' driver's per-process session budget and the final VALID probe
            ' initialize would fail.
            For i As Integer = 1 To FailureReps
                Dim enc As NvencEncoderBackend = NewBackend()
                Dim cfg As EncoderConfig = ValidSmallConfig()
                cfg.ExpectedWidth = 8192
                cfg.ExpectedHeight = 8192
                Dim threw As Exception = Nothing
                Try
                    enc.Initialize(cfg)
                Catch ex As Exception
                    threw = ex
                End Try
                TestHelpers.Assert(threw IsNot Nothing, "failure rep " & i & " unexpectedly initialized")
                TestHelpers.AssertEqual(EncoderState.Faulted, enc.CurrentState, "rep " & i)
                enc.Dispose()
                TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState, "dispose rep " & i)
            Next

            ' Budget probe: real session must still open, and encode REAL packets.
            Dim probe As NvencEncoderBackend = NewBackend()
            Try
                probe.Initialize(ValidSmallConfig())
                TestHelpers.AssertEqual(EncoderState.Initialized, probe.CurrentState,
                    "valid Initialize failed after " & FailureReps & " faulted initializes — native session leak")
                probe.Start()
                Using dev As Internal.D3D11DeviceResult = CreateDevice()
                    Using frame As TestGpuFrame = CreateSharedFrame(dev, 640, 480, 1)
                        Dim pk As EncodedPacket = Nothing
                        Dim ok As Boolean = probe.Encode(frame, pk)
                        TestHelpers.Assert(ok AndAlso pk IsNot Nothing AndAlso pk.PayloadLength > 0,
                            "budget-probe encode produced no real packet")
                        If pk IsNot Nothing Then pk.Dispose()
                    End Using
                End Using
            Finally
                probe.Dispose()
            End Try
        End Sub

        ' ── P2: encode vs dispose ────────────────────────────────────────

        Private Shared Sub Test_EncodeVsDispose()
            RequireNvenc()
            ' Allowed outcomes on the encode thread racing Dispose: success,
            ' False return, or the three expected managed faults. Anything
            ' else is an invariant break. Dispose must ALWAYS return (C/6
            ' in-flight drain, bounded) and land in Disposed.
            Dim unexpected As New List(Of String)
            Dim disposedOk As Integer = 0

            Using dev As Internal.D3D11DeviceResult = CreateDevice()
                For rep As Integer = 1 To Reps
                    Dim enc As NvencEncoderBackend = NewBackend()
                    enc.Initialize(ValidSmallConfig())
                    enc.Start()

                    Dim barrier As New Barrier(2)
                    Dim repUnexpected As String = Nothing
                    Dim encFault As Exception = Nothing

                    Dim tEnc As New Thread(Sub()
                        Try
                            barrier.SignalAndWait(5000)
                        Catch
                            Return ' other thread died before sync
                        End Try
                        Try
                            For k As Integer = 1 To 3
                                Using frame As TestGpuFrame = CreateSharedFrame(dev, 640, 480, rep * 10 + k)
                                    Dim pk As EncodedPacket = Nothing
                                    Dim ok As Boolean = enc.Encode(frame, pk)
                                    If ok AndAlso pk IsNot Nothing Then pk.Dispose()
                                End Using
                            Next
                        Catch ex As ObjectDisposedException
                            encFault = ex
                        Catch ex As InvalidOperationException
                            encFault = ex
                        Catch ex As EncoderRuntimeException
                            encFault = ex
                        Catch ex As Exception
                            repUnexpected = "unexpected encode exception: " & ex.GetType().Name & ": " & ex.Message
                        End Try
                    End Sub)

                    Dim tDis As New Thread(Sub()
                        Try
                            barrier.SignalAndWait(5000)
                        Catch
                            Return
                        End Try
                        Try
                            enc.Dispose()
                        Catch ex As Exception
                            repUnexpected = "unexpected dispose exception: " & ex.GetType().Name & ": " & ex.Message
                        End Try
                    End Sub)

                    tEnc.Start()
                    tDis.Start()

                    ' Deadlock detection: bounded joins (10s each side).
                    If Not tEnc.Join(10000) Then
                        tEnc.Abort()
                        Throw New Exception("rep " & rep & ": encode thread deadlocked against Dispose")
                    End If
                    If Not tDis.Join(10000) Then
                        tDis.Abort()
                        Throw New Exception("rep " & rep & ": dispose thread deadlocked mid-encode")
                    End If

                    If repUnexpected IsNot Nothing Then unexpected.Add("rep " & rep & ": " & repUnexpected)
                    TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState,
                        "rep " & rep & ": final state must be Disposed (encFault=" &
                        If(encFault IsNot Nothing, encFault.GetType().Name, "none") & ")")
                    disposedOk += 1
                Next
            End Using

            TestHelpers.Assert(unexpected.Count = 0,
                unexpected.Count & "/" & Reps & " reps had unexpected exceptions: " & String.Join(" | ", unexpected.ToArray()))
            TestHelpers.Assert(disposedOk = Reps, "not all reps reached Disposed")

            ' Session-budget probe after 24 raced open/close cycles: NVENC must
            ' still open a fresh session and emit real packets — proof that the
            ' race leaked no native sessions/handles.
            Dim probe As NvencEncoderBackend = NewBackend()
            Try
                probe.Initialize(ValidSmallConfig())
                probe.Start()
                Using dev As Internal.D3D11DeviceResult = CreateDevice()
                    Using frame As TestGpuFrame = CreateSharedFrame(dev, 640, 480, 999)
                        Dim pk As EncodedPacket = Nothing
                        Dim ok As Boolean = probe.Encode(frame, pk)
                        TestHelpers.Assert(ok AndAlso pk IsNot Nothing AndAlso pk.PayloadLength > 0,
                            "post-race budget probe produced no real packet")
                        If pk IsNot Nothing Then pk.Dispose()
                    End Using
                End Using
            Finally
                probe.Dispose()
            End Try
            Console.WriteLine($"    {Reps}/{Reps} encode-vs-dispose races clean; session budget intact after race")
        End Sub

        ' ── P3: post-fault containment ───────────────────────────────────

        Private Shared Sub Test_PostFaultContainment()
            RequireNvenc()
            Dim logs As New List(Of String)
            Dim enc As NvencEncoderBackend = NewBackend(Sub(m)
                                                            SyncLock logs : logs.Add(m) : End SyncLock
                                                        End Sub)

            ' Live encode session with REAL frames flowing.
            enc.Initialize(ValidSmallConfig())
            enc.Start()
            Dim goodPackets As Integer = 0
            Using dev As Internal.D3D11DeviceResult = CreateDevice()
                For seq As Integer = 1 To 10
                    Using frame As TestGpuFrame = CreateSharedFrame(dev, 640, 480, seq)
                        Dim pk As EncodedPacket = Nothing
                        Dim ok As Boolean = enc.Encode(frame, pk)
                        If ok AndAlso pk IsNot Nothing AndAlso pk.PayloadLength > 0 Then goodPackets += 1
                        If pk IsNot Nothing Then pk.Dispose()
                    End Using
                Next
                TestHelpers.Assert(goodPackets = 10, "expected 10 real encoded packets before the fault, got " & goodPackets)

                ' Controlled mid-session fault: frame dimension mismatch — the
                ' production TransitionToFaulted gate (deterministic, managed,
                ' no driver-state corruption).
                Using bad As TestGpuFrame = CreateSharedFrame(dev, 640, 481, 11)
                    Dim fault As Exception = Nothing
                    Try
                        Dim pk As EncodedPacket = Nothing
                        enc.Encode(bad, pk)
                    Catch ex As Exception
                        fault = ex
                    End Try
                    TestHelpers.Assert(fault IsNot Nothing, "mismatched frame did not fault the session")
                    TestHelpers.Assert(TypeOf fault Is EncoderRuntimeException,
                        "expected EncoderRuntimeException, got " & fault.GetType().Name)
                End Using
            End Using

            ' Faulted is terminal; further Encode is REJECTED (not ignored).
            TestHelpers.AssertEqual(EncoderState.Faulted, enc.CurrentState, "post-fault state")
            Using dev2 As Internal.D3D11DeviceResult = CreateDevice()
                Using f As TestGpuFrame = CreateSharedFrame(dev2, 640, 480, 12)
                    TestHelpers.AssertThrows(Of InvalidOperationException)(Sub()
                                                Dim pk As EncodedPacket = Nothing
                                                enc.Encode(f, pk)
                                            End Sub, "Encode after Faulted must be rejected")
                End Using
            End Using

            ' No error-spam loop: exactly one recorded error; and no async
            ' spam appears afterwards while the encoder sits idle in Faulted.
            TestHelpers.AssertEqual(1L, enc.Diagnostics.ErrorCount,
                "single fault must record exactly one error (spam loop suspected)")
            Dim logCount As Integer
            SyncLock logs : logCount = logs.Count : End SyncLock
            Thread.Sleep(300) ' observation window (not synchronization)
            SyncLock logs
                TestHelpers.Assert(logs.Count = logCount,
                    $"log grew from {logCount} to {logs.Count} while idle — error spam loop")
            End SyncLock

            ' Session terminates cleanly: Dispose from Faulted is fast + final.
            enc.Dispose()
            TestHelpers.AssertEqual(EncoderState.Disposed, enc.CurrentState, "dispose after fault")

            ' NEXT session can be created and encodes real packets.
            Dim next1 As NvencEncoderBackend = NewBackend()
            Try
                next1.Initialize(ValidSmallConfig())
                TestHelpers.AssertEqual(EncoderState.Initialized, next1.CurrentState, "next session init")
                next1.Start()
                Using dev As Internal.D3D11DeviceResult = CreateDevice()
                    Using frame As TestGpuFrame = CreateSharedFrame(dev, 640, 480, 21)
                        Dim pk As EncodedPacket = Nothing
                        Dim ok As Boolean = next1.Encode(frame, pk)
                        TestHelpers.Assert(ok AndAlso pk IsNot Nothing AndAlso pk.PayloadLength > 0,
                            "next session failed to encode real packet")
                        If pk IsNot Nothing Then pk.Dispose()
                    End Using
                End Using
            Finally
                next1.Dispose()
            End Try
        End Sub

    End Class

End Namespace
