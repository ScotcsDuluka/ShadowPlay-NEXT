Option Strict On
Option Explicit On
Option Infer On

' NvencResources.vb
'
' Helper that owns the NVENC per-encoder resources:
'   - bitstream buffer (output sink for encoded NAL bytes)
'   - registered encoder texture (input surface for encoding)
'
' Ported from spikes/D3D11_NVENC_Spike/Phases/Phase4_NVENCRegistration.cs
' and Phase10_RealRecording.cs (the resource creation + destruction parts).
'
' Owned by NvencEncoderBackend.Initialize() and disposed by Dispose().
' NOT per-session — these resources stay alive for the encoder's lifetime.

Imports System.Runtime.InteropServices
Imports Vortice.Direct3D11
Imports Vortice.DXGI
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Encoder.Nvenc.Internal

    ''' <summary>
    ''' Owns the NVENC bitstream buffer + registered encoder texture.
    ''' Created in Initialize(); disposed in Dispose().
    ''' </summary>
    Public NotInheritable Class NvencResources
        Implements IDisposable

        Private ReadOnly _logger As EngineLogger
        Private _disposed As Boolean

        ' NVENC handles
        Public ReadOnly Property BitstreamBuffer As IntPtr
        Public ReadOnly Property RegisteredResource As IntPtr
        Public ReadOnly Property EncoderTexture As ID3D11Texture2D

        ' Texture dimensions (from creation — used by EncodePicture)
        Public ReadOnly Property Width As UInteger
        Public ReadOnly Property Height As UInteger

        Private _encoder As IntPtr  ' NVENC encoder handle (NOT owned — owned by NvencEncoderBackend)
        Private _nvenc As NvEncFunctionTable  ' NOT owned — borrowed from backend

        Public Sub New(logger As EngineLogger)
            _logger = logger
        End Sub

        ''' <summary>
        ''' Create bitstream buffer + register encoder texture on the given device.
        ''' Throws on failure (caller must dispose partially-created resources).
        ''' </summary>
        Public Sub Create(encoder As IntPtr,
                          nvenc As NvEncFunctionTable,
                          device As ID3D11Device,
                          width As UInteger,
                          height As UInteger)
            _encoder = encoder
            _nvenc = nvenc
            _Width = width
            _Height = height

            ' ─── Create bitstream buffer ─────────────────────────────────────
            Dim bsParams As NvEncodeAPI.NV_ENC_CREATE_BITSTREAM_BUFFER = Nothing
            bsParams.version = NvEncodeAPI.NV_ENC_CREATE_BITSTREAM_BUFFER_VER
            bsParams.size = 0
            bsParams.memoryHeap = 0
            ' _padding, bitstreamBuffer (OUT), reserved1, reserved2 = zero (default Nothing)

            Dim status As UInteger = _nvenc.CreateBitstreamBuffer.Invoke(_encoder, bsParams)
            If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                _logger.Error($"NvEncCreateBitstreamBuffer failed: status={status} " &
                              $"({NvEncodeAPI.NvencStatusToString(status)})")
                Throw New InvalidOperationException(
                    $"NvEncCreateBitstreamBuffer failed: {NvEncodeAPI.NvencStatusToString(status)}")
            End If
            _BitstreamBuffer = bsParams.bitstreamBuffer
            _logger.Info($"Bitstream buffer created: 0x{_BitstreamBuffer.ToInt64():x16}")

            ' ─── Create encoder texture ──────────────────────────────────────
            Dim texDesc As New Texture2DDescription() With {
                .Width = CUInt(width),
                .Height = CUInt(height),
                .MipLevels = 1,
                .ArraySize = 1,
                .Format = Format.B8G8R8A8_UNorm,
                .SampleDescription = New SampleDescription(1, 0),
                .Usage = ResourceUsage.Default,
                .BindFlags = BindFlags.RenderTarget Or BindFlags.ShaderResource,
                .CPUAccessFlags = CpuAccessFlags.None,
                .MiscFlags = ResourceOptionFlags.None
            }
            _EncoderTexture = device.CreateTexture2D(texDesc)
            _logger.Info($"Encoder texture created: {width}x{height} BGRA8 on device 0x{device.NativePointer.ToInt64():x16}")

            ' ─── Register encoder texture with NVENC ──────────────────────────
            Dim regParams As NvEncodeAPI.NV_ENC_REGISTER_RESOURCE = Nothing
            regParams.version = NvEncodeAPI.NV_ENC_REGISTER_RESOURCE_VER
            regParams.resourceType = NvEncodeAPI.NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
            regParams.width = width
            regParams.height = height
            regParams.pitch = 0   ' 0 for D3D11 textures
            regParams.subResourceIndex = 0
            regParams.resourceToRegister = _EncoderTexture.NativePointer
            regParams.registeredResource = IntPtr.Zero  ' OUT
            regParams.bufferFormat = NvEncodeAPI.NV_ENC_BUFFER_FORMAT_ARGB
            regParams.reserved1 = Nothing  ' VB default for arrays — marshalled as Nothing
            regParams.reserved2 = Nothing

            status = _nvenc.RegisterResource.Invoke(_encoder, regParams)
            If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                _logger.Error($"NvEncRegisterResource failed: status={status} " &
                              $"({NvEncodeAPI.NvencStatusToString(status)})")
                ' Clean up partially-created resources
                Try
                    If _BitstreamBuffer <> IntPtr.Zero Then
                        _nvenc.DestroyBitstreamBuffer.Invoke(_encoder, _BitstreamBuffer)
                    End If
                Catch
                End Try
                Try : _EncoderTexture?.Dispose() : Catch : End Try
                Throw New InvalidOperationException(
                    $"NvEncRegisterResource failed: {NvEncodeAPI.NvencStatusToString(status)}")
            End If
            _RegisteredResource = regParams.registeredResource
            _logger.Info($"Texture registered with NVENC: 0x{_RegisteredResource.ToInt64():x16}")
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True

            ' Unregister resource (offset 256 = NvEncUnregisterResource)
            If _RegisteredResource <> IntPtr.Zero AndAlso _nvenc?.UnregisterResource IsNot Nothing Then
                Try
                    Dim status As UInteger = _nvenc.UnregisterResource.Invoke(_encoder, _RegisteredResource)
                    If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                        _logger.Warning($"NvEncUnregisterResource returned {status} " &
                                        $"({NvEncodeAPI.NvencStatusToString(status)}) — ignoring")
                    End If
                Catch ex As Exception
                    _logger.Warning($"NvEncUnregisterResource threw: {ex.Message}")
                End Try
            End If

            ' Destroy bitstream buffer
            If _BitstreamBuffer <> IntPtr.Zero AndAlso _nvenc?.DestroyBitstreamBuffer IsNot Nothing Then
                Try
                    Dim status As UInteger = _nvenc.DestroyBitstreamBuffer.Invoke(_encoder, _BitstreamBuffer)
                    If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                        _logger.Warning($"NvEncDestroyBitstreamBuffer returned {status} " &
                                        $"({NvEncodeAPI.NvencStatusToString(status)}) — ignoring")
                    End If
                Catch ex As Exception
                    _logger.Warning($"NvEncDestroyBitstreamBuffer threw: {ex.Message}")
                End Try
            End If

            ' Dispose encoder texture
            Try : _EncoderTexture?.Dispose() : Catch : End Try
        End Sub

    End Class

End Namespace
