Option Strict On
Option Explicit On
Option Infer On

' D3D11DeviceFactory.vb
'
' Creates a D3D11 device on the primary NVIDIA adapter.
' Used by NvencEncoderBackend (per audit verdict #1: each backend owns its own device).
'
' Ported from spikes/D3D11_NVENC_Spike/Phases/Phase1_DeviceTest.cs (production
' translation — uses EngineLogger instead of Console, returns structured result
' instead of int exit code).
'
' Device creation flags (CRITICAL — do NOT change):
'   BgraSupport  — required for DXGI Desktop Duplication API
'   VideoSupport — required for NVENC interop (NvEncRegisterResource would
'                  return NV_ENC_ERR_DEVICE_NOT_EXIST without this flag)
' MultithreadProtected = TRUE is also mandatory — without it, capture FPS
' drops from ~100 to ~3 (verified in Phase 2 spike).

Imports Vortice.Direct3D
Imports Vortice.Direct3D11
Imports Vortice.DXGI
Imports SharpGen.Runtime
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Encoder.Nvenc.Internal

    ''' <summary>
    ''' Result of D3D11 device creation. Contains the device + context + adapter
    ''' info needed by NvencEncoderBackend for OpenEncodeSessionEx.
    ''' </summary>
    Public NotInheritable Class D3D11DeviceResult
        Implements IDisposable

        Public ReadOnly Property Device As ID3D11Device
        Public ReadOnly Property DeviceContext As ID3D11DeviceContext
        Public ReadOnly Property Adapter As IDXGIAdapter1
        Public ReadOnly Property Factory As IDXGIFactory1
        Public ReadOnly Property AdapterIndex As Integer
        Public ReadOnly Property Description As String
        Public ReadOnly Property VendorId As UInteger
        Public ReadOnly Property DeviceId As UInteger
        Public ReadOnly Property LuidLow As UInteger
        Public ReadOnly Property LuidHigh As Integer
        Public ReadOnly Property FeatureLevel As FeatureLevel

        Public Sub New(device As ID3D11Device,
                       context As ID3D11DeviceContext,
                       adapter As IDXGIAdapter1,
                       factory As IDXGIFactory1,
                       adapterIndex As Integer,
                       description As String,
                       vendorId As UInteger,
                       deviceId As UInteger,
                       luidLow As UInteger,
                       luidHigh As Integer,
                       featureLevel As FeatureLevel)
            _Device = device
            _DeviceContext = context
            _Adapter = adapter
            _Factory = factory
            _AdapterIndex = adapterIndex
            _Description = description
            _VendorId = vendorId
            _DeviceId = deviceId
            _LuidLow = luidLow
            _LuidHigh = luidHigh
            _FeatureLevel = featureLevel
        End Sub

        Private _disposed As Boolean

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try : _DeviceContext?.Dispose() : Catch : End Try
            Try : _Device?.Dispose() : Catch : End Try
            Try : _Adapter?.Dispose() : Catch : End Try
            Try : _Factory?.Dispose() : Catch : End Try
        End Sub

    End Class

    ''' <summary>
    ''' Creates D3D11 device on primary NVIDIA adapter. Each backend calls this
    ''' independently (no shared device) per audit verdict #1.
    ''' </summary>
    Public NotInheritable Class D3D11DeviceFactory

        Private Const NVIDIA_VENDOR_ID As UInteger = &H10DEUI

        Private ReadOnly _logger As EngineLogger

        Public Sub New(logger As EngineLogger)
            _logger = logger
        End Sub

        ''' <summary>
        ''' Create D3D11 device on primary NVIDIA adapter.
        ''' Returns Nothing on failure (error already logged).
        ''' </summary>
        Public Function Create() As D3D11DeviceResult
            ' ─── Step 1: enumerate DXGI adapters, find first NVIDIA ─────────
            Dim factory As IDXGIFactory1 = Nothing
            Try
                ' CreateDXGIFactory1(Of T)() returns T directly (NOT Result).
                ' The previous .CheckError() call was incorrect — it was a C#
                ' pattern from spike that doesn't apply here since Vortice's
                ' API returns the factory directly and throws on failure.
                factory = DXGI.CreateDXGIFactory1(Of IDXGIFactory1)()
            Catch ex As Exception
                _logger.Error($"CreateDXGIFactory1 threw: {ex.GetType().Name}: {ex.Message}", ex)
                Return Nothing
            End Try

            Dim nvidiaIdx As Integer = -1
            Dim nvidiaDesc As AdapterDescription1 = Nothing
            Dim adapterIdx As Integer = 0
            Try
                Dim adapter1 As IDXGIAdapter1 = Nothing
                Dim result As Result
                result = factory.EnumAdapters1(CUInt(adapterIdx), adapter1)
                Do While result.Success
                    Using a As IDXGIAdapter1 = adapter1
                        Dim desc As AdapterDescription1 = a.Description1
                        _logger.Info($"  Adapter [{adapterIdx}] {desc.Description} " &
                                     $"(0x{desc.VendorId:x4}:0x{desc.DeviceId:x4}) " &
                                     $"LUID=({desc.Luid.LowPart:x8},{desc.Luid.HighPart:x8})")
                        If desc.VendorId = NVIDIA_VENDOR_ID AndAlso nvidiaIdx < 0 Then
                            nvidiaIdx = adapterIdx
                            nvidiaDesc = desc
                        End If
                    End Using
                    adapterIdx += 1
                    result = factory.EnumAdapters1(CUInt(adapterIdx), adapter1)
                Loop
            Catch ex As Exception
                _logger.Error($"EnumAdapters1 threw: {ex.GetType().Name}: {ex.Message}", ex)
                factory.Dispose()
                Return Nothing
            End Try

            If nvidiaIdx < 0 Then
                _logger.Error("No NVIDIA adapter found. NVENC requires an NVIDIA GPU.")
                factory.Dispose()
                Return Nothing
            End If

            _logger.Info($"Selected NVIDIA adapter #{nvidiaIdx}: {nvidiaDesc.Description}")
            _logger.Info($"  VendorId: 0x{nvidiaDesc.VendorId:x4}")
            _logger.Info($"  DeviceId: 0x{nvidiaDesc.DeviceId:x4}")
            _logger.Info($"  LUID:     ({nvidiaDesc.Luid.LowPart:x8},{nvidiaDesc.Luid.HighPart:x8})")

            ' ─── Step 2: create D3D11 device on NVIDIA adapter ─────────────
            Dim targetAdapter As IDXGIAdapter1 = Nothing
            Try
                Dim r As Result = factory.EnumAdapters1(CUInt(nvidiaIdx), targetAdapter)
                If Not r.Success Then
                    _logger.Error($"EnumAdapters1(NVIDIA) failed: hr=0x{r.Code:x8}")
                    factory.Dispose()
                    Return Nothing
                End If
            Catch ex As Exception
                _logger.Error($"EnumAdapters1(NVIDIA) threw: {ex.GetType().Name}: {ex.Message}", ex)
                factory.Dispose()
                Return Nothing
            End Try

            Dim requestedFeatureLevels As FeatureLevel() = {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            }

            Dim device As ID3D11Device = Nothing
            Dim context As ID3D11DeviceContext = Nothing
            Dim achievedFeatureLevel As FeatureLevel
            Try
                ' DeviceCreationFlags:
                '   BgraSupport  — required for DXGI Desktop Duplication API
                '   VideoSupport — required for NVENC interop
                Dim flags As DeviceCreationFlags = DeviceCreationFlags.BgraSupport Or
                                                    DeviceCreationFlags.VideoSupport
                Dim r As Result = D3D11.D3D11CreateDevice(
                    targetAdapter,
                    DriverType.Unknown,
                    flags,
                    requestedFeatureLevels,
                    device,
                    context)
                If Not r.Success Then
                    _logger.Error($"D3D11CreateDevice failed: hr=0x{r.Code:x8}")
                    targetAdapter.Dispose()
                    factory.Dispose()
                    Return Nothing
                End If
                achievedFeatureLevel = device.FeatureLevel
            Catch ex As Exception
                _logger.Error($"D3D11CreateDevice threw: {ex.GetType().Name}: {ex.Message}", ex)
                targetAdapter.Dispose()
                factory.Dispose()
                Return Nothing
            End Try

            _logger.Info($"Device created. Feature level: {achievedFeatureLevel}")
            _logger.Info($"  Device pointer: 0x{device.NativePointer.ToInt64():x16}")

            ' ─── Step 3: enable multithread protection ───────────────────────
            ' Without this, NVENC may access the device from another thread
            ' and cause race conditions. Phase 2 spike showed capture FPS
            ' drops from ~100 to ~3 without multithread protection.
            Try
                Dim multithread As ID3D11Multithread = context.QueryInterface(Of ID3D11Multithread)()
                multithread.SetMultithreadProtected(True)
                multithread.Dispose()
                _logger.Info("Multithread protection: ENABLED")
            Catch ex As Exception
                _logger.Warning($"Could not enable multithread protection: {ex.Message}")
                ' Non-fatal — continue; performance may degrade.
            End Try

            ' ─── Step 4: verify device adapter LUID matches selected NVIDIA adapter ──
            Try
                Dim dxgiDevice As IDXGIDevice = device.QueryInterface(Of IDXGIDevice)()
                Dim deviceAdapter As IDXGIAdapter = dxgiDevice.GetParent(Of IDXGIAdapter)()
                Dim deviceAdapterDesc As AdapterDescription = deviceAdapter.Description
                _logger.Info($"Device's parent adapter: {deviceAdapterDesc.Description}")
                _logger.Info($"  VendorId: 0x{deviceAdapterDesc.VendorId:x4}")
                _logger.Info($"  DeviceId: 0x{deviceAdapterDesc.DeviceId:x4}")
                _logger.Info($"  LUID:     ({deviceAdapterDesc.Luid.LowPart:x8},{deviceAdapterDesc.Luid.HighPart:x8})")

                Dim luidMatches As Boolean =
                    deviceAdapterDesc.Luid.LowPart = nvidiaDesc.Luid.LowPart AndAlso
                    deviceAdapterDesc.Luid.HighPart = nvidiaDesc.Luid.HighPart
                If Not luidMatches Then
                    _logger.Error("D3D11 device's adapter LUID does NOT match selected NVIDIA adapter.")
                    deviceAdapter.Dispose()
                    dxgiDevice.Dispose()
                    context.Dispose()
                    device.Dispose()
                    targetAdapter.Dispose()
                    factory.Dispose()
                    Return Nothing
                End If
                deviceAdapter.Dispose()
                dxgiDevice.Dispose()
            Catch ex As Exception
                _logger.Warning($"LUID verification skipped (query failed): {ex.Message}")
                ' Non-fatal — proceed.
            End Try

            Return New D3D11DeviceResult(
                device:=device,
                context:=context,
                adapter:=targetAdapter,
                factory:=factory,
                adapterIndex:=nvidiaIdx,
                description:=nvidiaDesc.Description,
                vendorId:=nvidiaDesc.VendorId,
                deviceId:=nvidiaDesc.DeviceId,
                luidLow:=nvidiaDesc.Luid.LowPart,
                luidHigh:=nvidiaDesc.Luid.HighPart,
                featureLevel:=achievedFeatureLevel)
        End Function

    End Class

End Namespace
