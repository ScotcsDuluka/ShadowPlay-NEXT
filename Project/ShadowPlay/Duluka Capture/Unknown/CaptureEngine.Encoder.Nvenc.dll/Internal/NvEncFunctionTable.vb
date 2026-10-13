Option Strict On
Option Explicit On
Option Infer On

' NvEncFunctionTable.vb
'
' High-level wrapper around NVENC P/Invoke declarations.
' Handles function table loading and exposes only what the encoder needs.
'
' VERBATIM translation of spike's NvEncFunctionTable class (was in NvEncodeAPI.cs).
' Adapted to production namespace + uses EngineLogger instead of Console.

Imports System.Runtime.InteropServices
Imports CaptureEngine.Diagnostics

Namespace CaptureEngine.Encoder.Nvenc.Internal

    Public NotInheritable Class NvEncFunctionTable
        Implements IDisposable

        Private _fnList As NvEncodeAPI.NV_ENCODE_API_FUNCTION_LIST
        Private _loaded As Boolean

        Public Property OpenEncodeSessionEx As NvEncodeAPI.NvEncOpenEncodeSessionExDelegate
        Public Property GetEncodeGUIDCount As NvEncodeAPI.NvEncGetEncodeGUIDCountDelegate
        Public Property GetEncodeGUIDs As NvEncodeAPI.NvEncGetEncodeGUIDsDelegate
        Public Property GetInputFormatCount As NvEncodeAPI.NvEncGetInputFormatCountDelegate
        Public Property GetInputFormats As NvEncodeAPI.NvEncGetInputFormatsDelegate
        Public Property RegisterResource As NvEncodeAPI.NvEncRegisterResourceDelegate
        Public Property UnregisterResource As NvEncodeAPI.NvEncUnregisterResourceDelegate
        Public Property DestroyEncoder As NvEncodeAPI.NvEncDestroyEncoderDelegate
        Public Property InitializeEncoder As NvEncodeAPI.NvEncInitializeEncoderDelegate

        ' Phase 6 encode pipeline delegates
        Public Property CreateBitstreamBuffer As NvEncodeAPI.NvEncCreateBitstreamBufferDelegate
        Public Property DestroyBitstreamBuffer As NvEncodeAPI.NvEncDestroyBitstreamBufferDelegate
        Public Property MapInputResource As NvEncodeAPI.NvEncMapInputResourceDelegate
        Public Property UnmapInputResource As NvEncodeAPI.NvEncUnmapInputResourceDelegate
        Public Property EncodePicture As NvEncodeAPI.NvEncEncodePictureDelegate
        Public Property LockBitstream As NvEncodeAPI.NvEncLockBitstreamDelegate
        Public Property UnlockBitstream As NvEncodeAPI.NvEncUnlockBitstreamDelegate

        ' PHASE 1 VIDEO RUNTIME WIRING: driver-side preset config retrieval
        Public Property GetPresetConfig As NvEncodeAPI.NvEncGetEncodePresetConfigDelegate
        Public Property GetPresetConfigEx As NvEncodeAPI.NvEncGetEncodePresetConfigExDelegate

        Public ReadOnly Property MaxSupportedApiVersion As UInteger
            Get
                Return _maxSupportedApiVersion
            End Get
        End Property
        Private _maxSupportedApiVersion As UInteger

        Private ReadOnly _logger As EngineLogger

        Public Sub New(logger As EngineLogger)
            _logger = logger
        End Sub

        ' Helper: all messages are prefixed by the logger's source (set at construction).
        ' The methods below take a single message string per EngineLogger's API.

        Public Function TryLoad() As Boolean
            Try
                Dim status As UInteger = NvEncodeAPI.NvEncodeAPIGetMaxSupportedVersion(
                    _maxSupportedApiVersion)
                If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                    _logger.Error(
                        $"NvEncodeAPIGetMaxSupportedVersion failed: status={status}")
                    Return False
                End If

                NvEncodeAPI.SetVersionFromPacked(_maxSupportedApiVersion)
                Dim maxMajor As UInteger = NvEncodeAPI.NVENCAPI_MAJOR_VERSION
                Dim maxMinor As UInteger = NvEncodeAPI.NVENCAPI_MINOR_VERSION
                _logger.Info(
                    $"NVENC max supported API: major={maxMajor}, minor={maxMinor} (packed=0x{_maxSupportedApiVersion:X8})")
                _logger.Info(
                    $"Encoder requests API: major={maxMajor}, minor={maxMinor} " &
                    $"(NVENCAPI_VERSION=0x{NvEncodeAPI.NVENCAPI_VERSION:X8})")

                _fnList = Nothing ' zero-init
                _fnList.version = NvEncodeAPI.NV_ENCODE_API_FUNCTION_LIST_VER
                Dim fnListSize As Integer = Marshal.SizeOf(Of NvEncodeAPI.NV_ENCODE_API_FUNCTION_LIST)()
                _logger.Info(
                    $"Function table version: 0x{_fnList.version:X8} (struct size={fnListSize} bytes)")

                status = NvEncodeAPI.NvEncodeAPICreateInstance(_fnList)
                If status <> NvEncodeAPI.NV_ENC_SUCCESS Then
                    _logger.Error(
                        $"NvEncodeAPICreateInstance failed: status={status} " &
                        $"({NvEncodeAPI.NvencStatusToString(status)})")
                    Return False
                End If

                OpenEncodeSessionEx = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncOpenEncodeSessionExDelegate)(_fnList.nvEncOpenEncodeSessionEx)
                GetEncodeGUIDCount = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncGetEncodeGUIDCountDelegate)(_fnList.nvEncGetEncodeGUIDCount)
                GetEncodeGUIDs = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncGetEncodeGUIDsDelegate)(_fnList.nvEncGetEncodeGUIDs)
                GetInputFormatCount = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncGetInputFormatCountDelegate)(_fnList.nvEncGetInputFormatCount)
                GetInputFormats = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncGetInputFormatsDelegate)(_fnList.nvEncGetInputFormats)
                RegisterResource = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncRegisterResourceDelegate)(_fnList.nvEncRegisterResource)
                UnregisterResource = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncUnregisterResourceDelegate)(_fnList.nvEncUnregisterResource)
                DestroyEncoder = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncDestroyEncoderDelegate)(_fnList.nvEncDestroyEncoder)
                InitializeEncoder = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncInitializeEncoderDelegate)(_fnList.nvEncInitializeEncoder)

                CreateBitstreamBuffer = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncCreateBitstreamBufferDelegate)(_fnList.nvEncCreateBitstreamBuffer)
                DestroyBitstreamBuffer = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncDestroyBitstreamBufferDelegate)(_fnList.nvEncDestroyBitstreamBuffer)
                MapInputResource = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncMapInputResourceDelegate)(_fnList.nvEncMapInputResource)
                UnmapInputResource = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncUnmapInputResourceDelegate)(_fnList.nvEncUnmapInputResource)
                EncodePicture = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncEncodePictureDelegate)(_fnList.nvEncEncodePicture)
                LockBitstream = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncLockBitstreamDelegate)(_fnList.nvEncLockBitstream)
                UnlockBitstream = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncUnlockBitstreamDelegate)(_fnList.nvEncUnlockBitstream)
                GetPresetConfig = Marshal.GetDelegateForFunctionPointer(
                    Of NvEncodeAPI.NvEncGetEncodePresetConfigDelegate)(_fnList.nvEncGetEncodePresetConfig)
                If _fnList.nvEncGetEncodePresetConfigEx <> IntPtr.Zero Then
                    GetPresetConfigEx = Marshal.GetDelegateForFunctionPointer(
                        Of NvEncodeAPI.NvEncGetEncodePresetConfigExDelegate)(_fnList.nvEncGetEncodePresetConfigEx)
                Else
                    GetPresetConfigEx = Nothing
                End If

                _loaded = True
                Return True
            Catch ex As DllNotFoundException
                _logger.Error($"nvEncodeAPI64.dll not found: {ex.Message}", ex)
                Return False
            Catch ex As Exception
                _logger.Error($"Error loading NVENC: {ex.GetType().Name}: {ex.Message}", ex)
                Return False
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _loaded Then Return
            _loaded = False
        End Sub

    End Class

End Namespace
