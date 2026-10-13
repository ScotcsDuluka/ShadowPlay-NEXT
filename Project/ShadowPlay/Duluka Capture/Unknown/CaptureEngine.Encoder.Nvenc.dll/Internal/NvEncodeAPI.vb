Option Strict On
Option Explicit On
Option Infer On

' NvEncodeAPI.vb
'
' P/Invoke declarations for NVIDIA Video Codec SDK (NVENC).
' VERBATIM translation of spikes/D3D11_NVENC_Spike/Utils/NvEncodeAPI.cs (C# → VB.NET).
' Struct layouts, constants, delegates, and DllImport declarations are byte-identical.
'
' Phase 12: moved from spike namespace (CaptureEngine.Video.Spike.D3D11.Utils)
' to production namespace (CaptureEngine.Encoder.Nvenc.Internal).
'
' SPDX-License-Identifier: MIT

Imports System.Runtime.InteropServices

Namespace CaptureEngine.Encoder.Nvenc.Internal

    Public NotInheritable Class NvEncodeAPI

        Private Sub New()
        End Sub

        ' === NVENC status codes ===
        Public Const NV_ENC_SUCCESS As UInteger = 0UI
        Public Const NV_ENC_ERR_NO_ENCODE_DEVICE As UInteger = 1UI
        Public Const NV_ENC_ERR_UNSUPPORTED_DEVICE As UInteger = 2UI
        Public Const NV_ENC_ERR_INVALID_ENCODERDEVICE As UInteger = 3UI
        Public Const NV_ENC_ERR_INVALID_DEVICE As UInteger = 4UI
        Public Const NV_ENC_ERR_DEVICE_NOT_EXIST As UInteger = 5UI
        Public Const NV_ENC_ERR_INVALID_PTR As UInteger = 6UI
        Public Const NV_ENC_ERR_INVALID_EVENT As UInteger = 7UI
        Public Const NV_ENC_ERR_INVALID_PARAM As UInteger = 8UI
        Public Const NV_ENC_ERR_INVALID_CALL As UInteger = 9UI
        Public Const NV_ENC_ERR_OUT_OF_MEMORY As UInteger = 10UI
        Public Const NV_ENC_ERR_ENCODER_NOT_INITIALIZED As UInteger = 11UI
        Public Const NV_ENC_ERR_UNSUPPORTED_PARAM As UInteger = 12UI
        Public Const NV_ENC_ERR_LOCK_BUSY As UInteger = 13UI
        Public Const NV_ENC_ERR_NOT_ENOUGH_INPUT_DATA As UInteger = 14UI
        Public Const NV_ENC_ERR_INVALID_VERSION As UInteger = 15UI
        Public Const NV_ENC_ERR_MAP_FAILED As UInteger = 16UI
        Public Const NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY As UInteger = 17UI
        Public Const NV_ENC_ERR_UNIMPLEMENTED As UInteger = 18UI
        Public Const NV_ENC_ERR_RESOURCE_REGISTER_FAILED As UInteger = 19UI
        Public Const NV_ENC_ERR_RESOURCE_NOT_REGISTERED As UInteger = 20UI
        Public Const NV_ENC_ERR_RESOURCE_NOT_MAPPED As UInteger = 21UI
        <Obsolete("Use specific NV_ENC_ERR_* codes.")>
        Public Const NV_ENC_ERR_GENERIC As UInteger = 99UI

        ' === Device types ===
        Public Const NV_ENC_DEVICE_DIRECTX As UInteger = &H00UI
        Public Const NV_ENC_DEVICE_CUDA As UInteger = &H01UI

        ' === Resource types ===
        Public Const NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX As UInteger = &H00UI
        Public Const NV_ENC_INPUT_RESOURCE_TYPE_CUDADEVICEPTR As UInteger = &H01UI

        ' === Buffer formats ===
        Public Const NV_ENC_BUFFER_FORMAT_UNDEFINED As UInteger = &H00000000UI
        Public Const NV_ENC_BUFFER_FORMAT_NV12 As UInteger = &H00000001UI
        Public Const NV_ENC_BUFFER_FORMAT_YV12 As UInteger = &H00000010UI
        Public Const NV_ENC_BUFFER_FORMAT_IYUV As UInteger = &H00000100UI
        Public Const NV_ENC_BUFFER_FORMAT_YUV444 As UInteger = &H00001000UI
        Public Const NV_ENC_BUFFER_FORMAT_YUV420_10BIT As UInteger = &H00010000UI
        Public Const NV_ENC_BUFFER_FORMAT_YUV444_10BIT As UInteger = &H00100000UI
        Public Const NV_ENC_BUFFER_FORMAT_ARGB As UInteger = &H01000000UI
        Public Const NV_ENC_BUFFER_FORMAT_ARGB10 As UInteger = &H02000000UI
        Public Const NV_ENC_BUFFER_FORMAT_AYUV As UInteger = &H04000000UI
        Public Const NV_ENC_BUFFER_FORMAT_ABGR As UInteger = &H10000000UI
        Public Const NV_ENC_BUFFER_FORMAT_ABGR10 As UInteger = &H20000000UI

        ' === Encode pic flags (NV_ENC_PIC_FLAG_*) ===
        Public Const NV_ENC_PIC_FLAG_FORCEINTRA As UInteger = &H1UI
        Public Const NV_ENC_PIC_FLAG_FORCEIDR As UInteger = &H2UI
        Public Const NV_ENC_PIC_FLAG_OUTPUT_SPSPPS As UInteger = &H4UI
        Public Const NV_ENC_PIC_FLAG_EOS As UInteger = &H8UI

        ' === Codec GUIDs ===
        Public Shared ReadOnly NV_ENC_CODEC_H264_GUID As Guid =
            New Guid(&H6BC82762UI, &H4E63US, &H4CA4US, &HAA, &H85, &H1E, &H50, &HF3, &H21, &HF6, &HBF)
        Public Shared ReadOnly NV_ENC_CODEC_HEVC_GUID As Guid =
            New Guid(&H790CDC88UI, &H4522US, &H4D7BUS, &H94, &H25, &HBD, &HA9, &H97, &H5F, &H76, &H03)
        Public Shared ReadOnly NV_ENC_PRESET_DEFAULT_GUID As Guid =
            New Guid(&HB2DFB705UI, &H4EBDUS, &H4C49US, &H9B, &H5F, &H24, &HA7, &H77, &HD3, &HE5, &H87)
        Public Shared ReadOnly NV_ENC_CODEC_AV1_GUID As Guid =
            New Guid(&HC24B3F5DUI, &H7354US, &H4CA4US, &H9C, &HA2, &H6A, &H2B, &H55, &H4D, &HB0, &HA8)

        ' === Preset GUIDs (PHASE 1 VIDEO RUNTIME WIRING) ===
        ' VERBATIM from nvEncodeAPI.h SDK 13.0 (nv-codec-headers n13.0.19.0
        ' lines 226-252). p1..p7 exist since SDK 12 — the driver already
        ' negotiated API 13.0 (NVENCAPI_VERSION), so these are safe.
        Public Shared ReadOnly NV_ENC_PRESET_P1_GUID As Guid =
            New Guid(&HFC0A8D3EUI, &H45F8US, &H4CF8US, &H80, &HC7, &H29, &H88, &H71, &H59, &HE, &HBF)
        Public Shared ReadOnly NV_ENC_PRESET_P2_GUID As Guid =
            New Guid(&HF581CFB8UI, &H88D6US, &H4381US, &H93, &HF0, &HDF, &H13, &HF9, &HC2, &H7D, &HAB)
        Public Shared ReadOnly NV_ENC_PRESET_P3_GUID As Guid =
            New Guid(&H36850110UI, &H3A07US, &H441FUS, &H94, &HD5, &H36, &H70, &H63, &H1F, &H91, &HF6)
        Public Shared ReadOnly NV_ENC_PRESET_P4_GUID As Guid =
            New Guid(&H90A7B826UI, &HDF06US, &H4862US, &HB9, &HD2, &HCD, &H6D, &H73, &HA0, &H86, &H81)
        Public Shared ReadOnly NV_ENC_PRESET_P5_GUID As Guid =
            New Guid(&H21C6E6B4UI, &H297AUS, &H4CBAUS, &H99, &H8F, &HB6, &HCB, &HDE, &H72, &HAD, &HE3)
        Public Shared ReadOnly NV_ENC_PRESET_P6_GUID As Guid =
            New Guid(&H8E75C279UI, &H6299US, &H4AB6US, &H83, &H02, &H0B, &H21, &H5A, &H33, &H5C, &HF5)
        Public Shared ReadOnly NV_ENC_PRESET_P7_GUID As Guid =
            New Guid(&H84848C12UI, &H6F71US, &H4C13US, &H93, &H1B, &H53, &HE2, &H83, &HF5, &H79, &H74)

        ''' <summary>
        ''' NV_ENC_CODEC_PROFILE_AUTOSELECT_GUID (SDK 13.0 line 162-163).
        ''' </summary>
        Public Shared ReadOnly NV_ENC_CODEC_PROFILE_AUTOSELECT_GUID As Guid =
            New Guid(&HBFD6F8E7UI, &H233CUS, &H4341US, &H8B, &H3E, &H48, &H18, &H52, &H38, &H03, &HF4)

        ''' <summary>
        ''' THE single preset-key → NVENC GUID mapper (PHASE 1 task §6 — one
        ''' mapper, one runtime source). Accepts "p1".."p7" (case-insensitive)
        ''' and "default". Anything else → p4 (the product default) so a bad
        ''' value can never silently fall back to DEFAULT-preset semantics.
        ''' </summary>
        Public Shared Function PresetGuidForKey(key As String) As Guid
            If String.IsNullOrWhiteSpace(key) Then Return NV_ENC_PRESET_P4_GUID
            Select Case key.Trim().ToLowerInvariant()
                Case "p1" : Return NV_ENC_PRESET_P1_GUID
                Case "p2" : Return NV_ENC_PRESET_P2_GUID
                Case "p3" : Return NV_ENC_PRESET_P3_GUID
                Case "p4" : Return NV_ENC_PRESET_P4_GUID
                Case "p5" : Return NV_ENC_PRESET_P5_GUID
                Case "p6" : Return NV_ENC_PRESET_P6_GUID
                Case "p7" : Return NV_ENC_PRESET_P7_GUID
                Case "default" : Return NV_ENC_PRESET_DEFAULT_GUID
                Case Else : Return NV_ENC_PRESET_P4_GUID
            End Select
        End Function

        ''' <summary>True when the key names a concrete p1..p7 preset.</summary>
        Public Shared Function IsNamedPresetKey(key As String) As Boolean
            If String.IsNullOrWhiteSpace(key) Then Return False
            Dim k As String = key.Trim().ToLowerInvariant()
            If k.Length <> 2 OrElse k(0) <> "p"c Then Return False
            Dim n As Integer
            If Not Integer.TryParse(k(1).ToString(), n) Then Return False
            Return n >= 1 AndAlso n <= 7
        End Function

        ' === Rate-control modes (PHASE 1 VIDEO RUNTIME WIRING) ===
        ' NV_ENC_PARAMS_RC_MODE enum, SDK 13.0 lines 271-276.
        Public Const NV_ENC_PARAMS_RC_CONSTQP As UInteger = &H0UI
        Public Const NV_ENC_PARAMS_RC_VBR As UInteger = &H1UI
        Public Const NV_ENC_PARAMS_RC_CBR As UInteger = &H2UI

        ''' <summary>
        ''' THE single rate-control-key → NVENC mode mapper. Accepts
        ''' "cbr", "vbr", "constqp"/"cq" (case-insensitive); anything else →
        ''' CBR (the product default) with IsKnownRateControlKey=False so the
        ''' caller can log the substitution.
        ''' </summary>
        Public Shared Function RateControlModeForKey(key As String, ByRef known As Boolean) As UInteger
            known = True
            If String.IsNullOrWhiteSpace(key) Then
                known = False
                Return NV_ENC_PARAMS_RC_CBR
            End If
            Select Case key.Trim().ToLowerInvariant()
                Case "cbr" : Return NV_ENC_PARAMS_RC_CBR
                Case "vbr" : Return NV_ENC_PARAMS_RC_VBR
                Case "constqp", "cq" : Return NV_ENC_PARAMS_RC_CONSTQP
                Case Else
                    known = False
                    Return NV_ENC_PARAMS_RC_CBR
            End Select
        End Function

        ' === API version ===
        Public Shared Property NVENCAPI_MAJOR_VERSION As UInteger = 13UI
        Public Shared Property NVENCAPI_MINOR_VERSION As UInteger = 0UI

        Public Shared ReadOnly Property NVENCAPI_VERSION As UInteger
            Get
                Return NVENCAPI_MAJOR_VERSION Or (NVENCAPI_MINOR_VERSION << 24)
            End Get
        End Property

        Public Shared Function MakeStructVersion(structVer As UInteger) As UInteger
            Return NVENCAPI_VERSION Or (structVer << 16) Or (&H7UI << 28)
        End Function

        Public Shared Sub SetVersionFromPacked(packed As UInteger)
            NVENCAPI_MAJOR_VERSION = (packed >> 4) And &HFUI
            NVENCAPI_MINOR_VERSION = packed And &HFUI
        End Sub

        ' === Struct version constants ===
        Public Const NV_ENCODE_API_FUNCTION_LIST_VER_STRUCT As UInteger = 1UI
        Public Const NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER_STRUCT As UInteger = 1UI
        Public Const NV_ENC_REGISTER_RESOURCE_VER_STRUCT As UInteger = 3UI
        Public Const NV_ENC_CREATE_BITSTREAM_BUFFER_VER_STRUCT As UInteger = 1UI
        Public Const NV_ENC_MAP_INPUT_RESOURCE_VER_STRUCT As UInteger = 4UI
        Public Const NV_ENC_PIC_PARAMS_VER_STRUCT As UInteger = 4UI
        Public Const NV_ENC_LOCK_BITSTREAM_VER_STRUCT As UInteger = 1UI

        ' PHASE 1 VIDEO RUNTIME WIRING — struct versions from the SDK 13.0
        ' header: NV_ENC_CONFIG_VER = NVENCAPI_STRUCT_VERSION(9) | 1<<31,
        ' NV_ENC_PRESET_CONFIG_VER = NVENCAPI_STRUCT_VERSION(4) | 1<<31.
        ' NV_ENC_RC_PARAMS_VER = NVENCAPI_STRUCT_VERSION(1) (NO 1<<31 flag).
        Public Const NV_ENC_CONFIG_VER_STRUCT As UInteger = 9UI
        Public Const NV_ENC_PRESET_CONFIG_VER_STRUCT As UInteger = 4UI
        Public Const NV_ENC_RC_PARAMS_VER_STRUCT As UInteger = 1UI

        Public Shared ReadOnly Property NV_ENC_CREATE_BITSTREAM_BUFFER_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_CREATE_BITSTREAM_BUFFER_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_MAP_INPUT_RESOURCE_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_MAP_INPUT_RESOURCE_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_PIC_PARAMS_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_PIC_PARAMS_VER_STRUCT) Or (1UI << 31)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_LOCK_BITSTREAM_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_LOCK_BITSTREAM_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENCODE_API_FUNCTION_LIST_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENCODE_API_FUNCTION_LIST_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_REGISTER_RESOURCE_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_REGISTER_RESOURCE_VER_STRUCT)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_CONFIG_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_CONFIG_VER_STRUCT) Or (1UI << 31)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_PRESET_CONFIG_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_PRESET_CONFIG_VER_STRUCT) Or (1UI << 31)
            End Get
        End Property

        Public Shared ReadOnly Property NV_ENC_RC_PARAMS_VER As UInteger
            Get
                Return MakeStructVersion(NV_ENC_RC_PARAMS_VER_STRUCT)
            End Get
        End Property

        ' === Structs ===

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
            Public version As UInteger
            Public deviceType As UInteger
            Public device As IntPtr
            Public reserved As IntPtr
            Public apiVersion As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=253)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved2 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_REGISTER_RESOURCE
            Public version As UInteger
            Public resourceType As UInteger
            Public width As UInteger
            Public height As UInteger
            Public pitch As UInteger
            Public subResourceIndex As UInteger
            Public resourceToRegister As IntPtr
            Public registeredResource As IntPtr
            Public bufferFormat As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=248)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=62)>
            Public reserved2 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_CREATE_BITSTREAM_BUFFER
            Public version As UInteger
            Public size As UInteger
            Public memoryHeap As UInteger
            Public _padding As UInteger
            Public bitstreamBuffer As IntPtr
            Public reserved1 As IntPtr
            Public reserved2 As IntPtr
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=226)>
            Public reserved3 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved4 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_MAP_INPUT_RESOURCE
            Public version As UInteger
            Public subResourceIndex As UInteger
            Public inputResource As IntPtr
            Public registeredResource As IntPtr
            Public mappedResource As IntPtr
            Public mappedBufferFmt As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=251)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=63)>
            Public reserved2 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_PIC_PARAMS
            Public version As UInteger
            Public inputWidth As UInteger
            Public inputHeight As UInteger
            Public inputPitch As UInteger
            Public encodePicFlags As UInteger
            Public frameIdx As UInteger
            Public inputTimeStamp As ULong
            Public inputDuration As ULong
            Public inputBuffer As IntPtr
            Public outputBitstream As IntPtr
            Public completionEvent As IntPtr
            Public bufferFmt As UInteger
            Public pictureStruct As UInteger
            Public pictureType As UInteger
            Public _padding1 As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=1536)>
            Public codecPicParams As Byte()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=32)>
            Public meHintCountsPerBlock As Byte()
            Public meExternalHints As IntPtr
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=6)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=2)>
            Public reserved2 As IntPtr()
            Public qpDeltaMap As IntPtr
            Public qpDeltaMapSize As UInteger
            Public reservedBitFields As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=2)>
            Public meHintRefPicDist As UShort()
            Public _padding2 As UInteger
            Public alphaBuffer As IntPtr
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=286)>
            Public reserved3 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=59)>
            Public reserved4 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENC_LOCK_BITSTREAM
            Public version As UInteger
            Public bitfields As UInteger
            Public outputBitstream As IntPtr
            Public sliceOffsets As IntPtr
            Public frameIdx As UInteger
            Public hwEncodeStatus As UInteger
            Public numSlices As UInteger
            Public bitstreamSizeInBytes As UInteger
            Public outputTimeStamp As ULong
            Public outputDuration As ULong
            Public bitstreamBufferPtr As IntPtr
            Public pictureType As UInteger
            Public pictureStruct As UInteger
            Public frameAvgQP As UInteger
            Public frameSatd As UInteger
            Public ltrFrameIdx As UInteger
            Public ltrFrameBitmap As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=13)>
            Public reserved As UInteger()
            Public intraMBCount As UInteger
            Public interMBCount As UInteger
            Public averageMVX As Integer
            Public averageMVY As Integer
            Public alphaLayerSizeInBytes As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=218)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved2 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=8)>
        Public Structure NV_ENC_INITIALIZE_PARAMS
            Public version As UInteger
            Public encodeGUID As Guid
            Public presetGUID As Guid
            Public encodeWidth As UInteger
            Public encodeHeight As UInteger
            Public darWidth As UInteger
            Public darHeight As UInteger
            Public frameRateNum As UInteger
            Public frameRateDen As UInteger
            Public enableEncodeAsync As UInteger
            Public enablePTD As UInteger
            ' reportSliceOffsets..reservedBitFields (SDK bitfield word)
            Public bitFields As UInteger
            Public privDataSize As UInteger
            Public reserved As UInteger
            Public privData As IntPtr
            Public encodeConfig As IntPtr
            Public maxEncodeWidth As UInteger
            Public maxEncodeHeight As UInteger
            ' NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[2] (16 bytes each)
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=16)>
            Public maxMEHintCountsPerBlockL0 As Byte()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=16)>
            Public maxMEHintCountsPerBlockL1 As Byte()
            Public tuningInfo As UInteger
            Public bufferFormat As UInteger
            Public numStateBuffers As UInteger
            Public outputStatsLevel As UInteger
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=284)>
            Public reserved1 As UInteger()
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved2 As IntPtr()
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=1)>
        Public Structure NV_ENCODE_API_FUNCTION_LIST
            Public version As UInteger
            Public reserved As UInteger
            Public nvEncOpenEncodeSession As IntPtr
            Public nvEncGetEncodeGUIDCount As IntPtr
            Public nvEncGetEncodeProfileGUIDCount As IntPtr
            Public nvEncGetEncodeProfileGUIDs As IntPtr
            Public nvEncGetEncodeGUIDs As IntPtr
            Public nvEncGetInputFormatCount As IntPtr
            Public nvEncGetInputFormats As IntPtr
            Public nvEncGetEncodeCaps As IntPtr
            Public nvEncGetEncodePresetCount As IntPtr
            Public nvEncGetEncodePresetGUIDs As IntPtr
            Public nvEncGetEncodePresetConfig As IntPtr
            Public nvEncInitializeEncoder As IntPtr
            Public nvEncCreateInputBuffer As IntPtr
            Public nvEncDestroyInputBuffer As IntPtr
            Public nvEncCreateBitstreamBuffer As IntPtr
            Public nvEncDestroyBitstreamBuffer As IntPtr
            Public nvEncEncodePicture As IntPtr
            Public nvEncLockBitstream As IntPtr
            Public nvEncUnlockBitstream As IntPtr
            Public nvEncLockInputBuffer As IntPtr
            Public nvEncUnlockInputBuffer As IntPtr
            Public nvEncGetEncodeStats As IntPtr
            Public nvEncGetSequenceParams As IntPtr
            Public nvEncRegisterAsyncEvent As IntPtr
            Public nvEncUnregisterAsyncEvent As IntPtr
            Public nvEncMapInputResource As IntPtr
            Public nvEncUnmapInputResource As IntPtr
            Public nvEncDestroyEncoder As IntPtr
            Public nvEncInvalidateRefFrames As IntPtr
            Public nvEncOpenEncodeSessionEx As IntPtr
            Public nvEncRegisterResource As IntPtr
            Public nvEncUnregisterResource As IntPtr
            Public nvEncReconfigureEncoder As IntPtr
            Public reserved1 As IntPtr
            Public nvEncCreateMVBuffer As IntPtr
            Public nvEncDestroyMVBuffer As IntPtr
            Public nvEncRunMotionEstimationOnly As IntPtr
            ' SDK tail order: GetLastError → SetIOCUDAStreams →
            ' GetEncodePresetConfigEx → GetSequenceParamEx.
            Public nvEncGetLastErrorString As IntPtr
            Public nvEncSetIOCudaStreams As IntPtr
            Public nvEncGetEncodePresetConfigEx As IntPtr
            Public nvEncGetSequenceParamEx As IntPtr
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=277)>
            Public reserved2 As IntPtr()
        End Structure

        ' ═══ PHASE 1 VIDEO RUNTIME WIRING — encoder-config structs ═══
        ' VERBATIM layout from nvEncodeAPI.h SDK 13.0
        ' (nv-codec-headers n13.0.19.0). Sizes/offsets were mechanically
        ' derived from that header (scripts/sizeof_nvenc.py, MSVC x64 rules)
        ' and are pinned by contract tests:
        '   sizeof(NV_ENC_RC_PARAMS)     = 128   align 4
        '   sizeof(NV_ENC_CODEC_CONFIG)  = 1792  (h264 1792 / av1 1688 / hevc 1560)
        '   sizeof(NV_ENC_CONFIG)        = 3584  align 8
        '   sizeof(NV_ENC_PRESET_CONFIG) = 5128  align 8
        ' Pack:=4 reproduces the native layout exactly for every field (the
        ' largest member alignment below is 4 except the trailing pointer
        ' arrays, whose offsets are 8-aligned anyway).

        <StructLayout(LayoutKind.Sequential, Pack:=4)>
        Public Structure NV_ENC_RC_PARAMS
            Public version As UInteger                 ' +0   NV_ENC_RC_PARAMS_VER
            Public rateControlMode As UInteger         ' +4   NV_ENC_PARAMS_RC_*
            Public qpInterP As UInteger                ' +8   NV_ENC_QP constQP (flattened)
            Public qpInterB As UInteger                ' +12
            Public qpIntra As UInteger                 ' +16
            Public averageBitRate As UInteger          ' +20  bits/sec
            Public maxBitRate As UInteger              ' +24  bits/sec
            Public vbvBufferSize As UInteger           ' +28  bits
            Public vbvInitialDelay As UInteger         ' +32  bits
            Public bitFields As UInteger               ' +36  enableMinQP..reservedBitFields (all 0)
            Public minQpInterP As UInteger             ' +40  NV_ENC_QP minQP (unused)
            Public minQpInterB As UInteger             ' +44
            Public minQpIntra As UInteger              ' +48
            Public maxQpInterP As UInteger             ' +52  NV_ENC_QP maxQP (unused)
            Public maxQpInterB As UInteger             ' +56
            Public maxQpIntra As UInteger              ' +60
            Public initQpInterP As UInteger            ' +64  NV_ENC_QP initialRCQP (unused)
            Public initQpInterB As UInteger            ' +68
            Public initQpIntra As UInteger             ' +72
            Public temporallayerIdxMask As UInteger    ' +76
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=8)>
            Public temporalLayerQP As Byte()           ' +80
            Public targetQuality As Byte               ' +88
            Public targetQualityLSB As Byte            ' +89
            Public lookaheadDepth As UShort            ' +90
            Public lowDelayKeyFrameScale As Byte       ' +92
            Public yDcQPIndexOffset As SByte           ' +93
            Public uDcQPIndexOffset As SByte           ' +94
            Public vDcQPIndexOffset As SByte           ' +95
            Public qpMapMode As UInteger               ' +96
            Public multiPass As UInteger               ' +100
            Public alphaLayerBitrateRatio As UInteger  ' +104
            Public cbQPIndexOffset As SByte            ' +108
            Public crQPIndexOffset As SByte            ' +109
            Public reserved2 As UShort                 ' +110
            Public lookaheadLevel As UInteger          ' +112
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=7)>
            Public viewBitrateRatios As Byte()         ' +116 (MAX_NUM_VIEWS_MINUS_1)
            Public reserved3 As Byte                   ' +123
            Public reserved1 As UInteger               ' +124
            ' total 128
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=4)>
        Public Structure NV_ENC_CONFIG
            Public version As UInteger                 ' +0   NV_ENC_CONFIG_VER
            Public profileGUID As Guid                 ' +4   AUTOSELECT = fine
            Public gopLength As UInteger               ' +20
            Public frameIntervalP As Integer           ' +24  0=I 1=IPP 2=IBP 3=IBBP
            Public monoChromeEncoding As UInteger      ' +28
            Public frameFieldMode As UInteger          ' +32
            Public mvPrecision As UInteger             ' +36
            Public rcParams As NV_ENC_RC_PARAMS        ' +40  (128 bytes)
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=1792)>
            Public encodeCodecConfig As Byte()         ' +168 NV_ENC_CODEC_CONFIG union
                                                       '       h264 idrPeriod at +8
                                                       '       (abs +176)
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=278)>
            Public reserved As UInteger()              ' +1960
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved2 As IntPtr()               ' +3072
            ' total 3584
        End Structure

        <StructLayout(LayoutKind.Sequential, Pack:=4)>
        Public Structure NV_ENC_PRESET_CONFIG
            Public version As UInteger                 ' +0   NV_ENC_PRESET_CONFIG_VER
            Public reserved As UInteger                ' +4
            Public presetCfg As NV_ENC_CONFIG          ' +8   driver-filled
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=256)>
            Public reserved1 As UInteger()             ' +3592
            <MarshalAs(UnmanagedType.ByValArray, SizeConst:=64)>
            Public reserved2 As IntPtr()               ' +4616
            ' total 5128
        End Structure

        ' === Delegates ===
        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncOpenEncodeSessionExDelegate(
            ByRef sessionParams As NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS,
            ByRef encoder As IntPtr) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetEncodeGUIDCountDelegate(
            encoder As IntPtr, ByRef count As Integer) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetEncodeGUIDsDelegate(
            encoder As IntPtr,
            <Out> guidArray As Guid(), arraySize As Integer,
            ByRef actualCount As Integer) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetInputFormatCountDelegate(
            encoder As IntPtr, encodeGUID As Guid,
            ByRef count As Integer) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetInputFormatsDelegate(
            encoder As IntPtr, encodeGUID As Guid,
            <Out> formatArray As UInteger(), arraySize As Integer,
            ByRef actualCount As Integer) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncRegisterResourceDelegate(
            encoder As IntPtr,
            ByRef registerParams As NV_ENC_REGISTER_RESOURCE) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncUnregisterResourceDelegate(
            encoder As IntPtr, registeredResource As IntPtr) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncDestroyEncoderDelegate(
            encoder As IntPtr) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncInitializeEncoderDelegate(
            encoder As IntPtr,
            ByRef initParams As NV_ENC_INITIALIZE_PARAMS) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncCreateBitstreamBufferDelegate(
            encoder As IntPtr,
            ByRef createParams As NV_ENC_CREATE_BITSTREAM_BUFFER) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncDestroyBitstreamBufferDelegate(
            encoder As IntPtr, bitstreamBuffer As IntPtr) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncMapInputResourceDelegate(
            encoder As IntPtr,
            ByRef mapParams As NV_ENC_MAP_INPUT_RESOURCE) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncUnmapInputResourceDelegate(
            encoder As IntPtr, mappedInputResource As IntPtr) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncEncodePictureDelegate(
            encoder As IntPtr,
            ByRef picParams As NV_ENC_PIC_PARAMS) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncLockBitstreamDelegate(
            encoder As IntPtr,
            ByRef lockParams As NV_ENC_LOCK_BITSTREAM) As UInteger

        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncUnlockBitstreamDelegate(
            encoder As IntPtr, bitstreamBuffer As IntPtr) As UInteger

        ''' <summary>
        ''' PHASE 1 VIDEO RUNTIME WIRING: NvEncGetEncodePresetConfig — legacy
        ''' preset retrieval kept only as a compatibility fallback.
        ''' </summary>
        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetEncodePresetConfigDelegate(
            encoder As IntPtr, encodeGUID As Guid, presetGUID As Guid,
            ByRef presetConfig As NV_ENC_PRESET_CONFIG) As UInteger

        ''' <summary>
        ''' SDK 13.x primary preset retrieval path. The Ex form accepts the
        ''' tuningInfo selected for NvEncInitializeEncoder and is required by
        ''' current drivers for p1..p7 preset GUIDs.
        ''' C: nvEncGetEncodePresetConfigEx(encoder, encodeGUID, presetGUID,
        ''' tuningInfo, presetConfig)
        ''' </summary>
        <UnmanagedFunctionPointer(CallingConvention.StdCall)>
        Public Delegate Function NvEncGetEncodePresetConfigExDelegate(
            encoder As IntPtr, encodeGUID As Guid, presetGUID As Guid,
            tuningInfo As UInteger, ByRef presetConfig As NV_ENC_PRESET_CONFIG) As UInteger

        ' === P/Invoke loader functions ===
        <DllImport("nvEncodeAPI64.dll", CallingConvention:=CallingConvention.StdCall,
                   SetLastError:=False, BestFitMapping:=False, ThrowOnUnmappableChar:=True)>
        Public Shared Function NvEncodeAPICreateInstance(
            ByRef functionList As NV_ENCODE_API_FUNCTION_LIST) As UInteger
        End Function

        <DllImport("nvEncodeAPI64.dll", CallingConvention:=CallingConvention.StdCall,
                   SetLastError:=False, BestFitMapping:=False, ThrowOnUnmappableChar:=True)>
        Public Shared Function NvEncodeAPIGetMaxSupportedVersion(
            ByRef version As UInteger) As UInteger
        End Function

        Public Shared Function NvencStatusToString(status As UInteger) As String
            Select Case status
                Case NV_ENC_SUCCESS : Return "NV_ENC_SUCCESS"
                Case NV_ENC_ERR_NO_ENCODE_DEVICE : Return "NV_ENC_ERR_NO_ENCODE_DEVICE"
                Case NV_ENC_ERR_UNSUPPORTED_DEVICE : Return "NV_ENC_ERR_UNSUPPORTED_DEVICE"
                Case NV_ENC_ERR_INVALID_ENCODERDEVICE : Return "NV_ENC_ERR_INVALID_ENCODERDEVICE"
                Case NV_ENC_ERR_INVALID_DEVICE : Return "NV_ENC_ERR_INVALID_DEVICE"
                Case NV_ENC_ERR_DEVICE_NOT_EXIST : Return "NV_ENC_ERR_DEVICE_NOT_EXIST"
                Case NV_ENC_ERR_INVALID_PTR : Return "NV_ENC_ERR_INVALID_PTR"
                Case NV_ENC_ERR_INVALID_EVENT : Return "NV_ENC_ERR_INVALID_EVENT"
                Case NV_ENC_ERR_INVALID_PARAM : Return "NV_ENC_ERR_INVALID_PARAM"
                Case NV_ENC_ERR_INVALID_CALL : Return "NV_ENC_ERR_INVALID_CALL"
                Case NV_ENC_ERR_OUT_OF_MEMORY : Return "NV_ENC_ERR_OUT_OF_MEMORY"
                Case NV_ENC_ERR_ENCODER_NOT_INITIALIZED : Return "NV_ENC_ERR_ENCODER_NOT_INITIALIZED"
                Case NV_ENC_ERR_UNSUPPORTED_PARAM : Return "NV_ENC_ERR_UNSUPPORTED_PARAM"
                Case NV_ENC_ERR_LOCK_BUSY : Return "NV_ENC_ERR_LOCK_BUSY"
                Case NV_ENC_ERR_NOT_ENOUGH_INPUT_DATA : Return "NV_ENC_ERR_NOT_ENOUGH_INPUT_DATA"
                Case NV_ENC_ERR_INVALID_VERSION : Return "NV_ENC_ERR_INVALID_VERSION"
                Case NV_ENC_ERR_MAP_FAILED : Return "NV_ENC_ERR_MAP_FAILED"
                Case NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY : Return "NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY"
                Case NV_ENC_ERR_UNIMPLEMENTED : Return "NV_ENC_ERR_UNIMPLEMENTED"
                Case NV_ENC_ERR_RESOURCE_REGISTER_FAILED : Return "NV_ENC_ERR_RESOURCE_REGISTER_FAILED"
                Case NV_ENC_ERR_RESOURCE_NOT_REGISTERED : Return "NV_ENC_ERR_RESOURCE_NOT_REGISTERED"
                Case NV_ENC_ERR_RESOURCE_NOT_MAPPED : Return "NV_ENC_ERR_RESOURCE_NOT_MAPPED"
                Case Else : Return $"NV_ENC_ERR_UNKNOWN({status})"
            End Select
        End Function

    End Class

End Namespace
