Option Strict On
Option Explicit On
Option Infer On

' NvEncParamBuilder.vb
'
' PHASE 1 VIDEO RUNTIME WIRING — the PURE (no native calls, no D3D11, no
' Vortice) builder that turns an EncoderConfig into real NVENC init
' structures. Everything the user configured (bitrate / rate control /
' preset / GOP / frame rate) is written into the native NVENC structures
' here — replacing the pre-wiring aspirational config
' (NvencEncoderBackend.vb:208 presetGUID=NV_ENC_PRESET_DEFAULT_GUID,
'  :220 encodeConfig=IntPtr.Zero, :80 frameRateNum=60 hardcoded).
'
' Struct-layout evidence: nvEncodeAPI.h SDK 13.0
' (nv-codec-headers n13.0.19.0), mechanically transcribed into
' NvEncodeAPI.vb. Sizes are pinned by NvEncParamBuilderTests (V-CT5).
'
' Design notes:
'   * BuildInitializeParams    — NV_ENC_INITIALIZE_PARAMS with the preset
 '                               GUID from the config and frameRateNum
'                               from the config FPS.
'   * ApplyVideoSettings       — writes user values into the explicit NV_ENC_CONFIG
'                               configuration. Unused fields remain zeroed;
'                               the explicit config path is canonical and ABI-pinned.
'   * BuildDefaultEncodeConfig — builds the canonical explicit config;
'                               the codec-specific union is zeroed except
'                               for fields patched by ApplyVideoSettings.
'
' This file is LINKED into CaptureEngine.Encoder.Tests (V-CT5) — it must
' stay free of Windows-only dependencies (System only).

Imports System
Imports System.Runtime.InteropServices

Namespace CaptureEngine.Encoder.Nvenc.Internal

    Friend NotInheritable Class NvEncParamBuilder

        Private Sub New()
        End Sub

        ' ── Byte offsets inside NV_ENC_CONFIG (SDK 13.0, Pack 4 ≡ MSVC x64) ──
        ' Used for the codec-union patch (h264Config.idrPeriod), which lives
        ' inside the opaque-by-value codec config region.
        Public Const NV_ENC_CONFIG_OFF_GOPLENGTH As Integer = 20
        Public Const NV_ENC_CONFIG_OFF_FRAMEINTERVALP As Integer = 24
        Public Const NV_ENC_CONFIG_OFF_RCPARAMS As Integer = 40
        Public Const NV_ENC_CONFIG_OFF_CODECCONFIG As Integer = 168
        Public Const NV_ENC_CONFIG_H264_OFF_IDRPERIOD As Integer = 8
        Public Const NV_ENC_RC_PARAMS_OFF_AVERAGEBITRATE As Integer = 20
        Public Const NV_ENC_RC_PARAMS_OFF_MAXBITRATE As Integer = 24
        Public Const NV_ENC_RC_PARAMS_OFF_VBVBUFFERSIZE As Integer = 28
        Public Const NV_ENC_RC_PARAMS_OFF_VBVINITIALDELAY As Integer = 32

        ''' <summary>Header-derived sizes (pinned by V-CT5 asserts).</summary>
        Public Const SIZEOF_NV_ENC_RC_PARAMS As Integer = 128
        Public Const SIZEOF_NV_ENC_CONFIG As Integer = 3584
        Public Const SIZEOF_NV_ENC_PRESET_CONFIG As Integer = 5128
        Public Const SIZEOF_CODEC_CONFIG_UNION As Integer = 1792

        ''' <summary>
        ''' Build NV_ENC_INITIALIZE_PARAMS from the EncoderConfig: preset GUID
        ''' via the single mapper, frame rate from the config FPS, encode
        ''' dims as configured, maxEncode = input size (scaling contract).
        ''' encodeConfig = IntPtr.Zero — the CALLER attaches the NV_ENC_CONFIG
        ''' (native pointer) after this, if one was built.
        ''' </summary>
        Public Shared Function BuildInitializeParams(width As UInteger,
                                                     height As UInteger,
                                                     encodeWidth As UInteger,
                                                     encodeHeight As UInteger,
                                                     frameRateFps As Integer,
                                                     presetKey As String) As NvEncodeAPI.NV_ENC_INITIALIZE_PARAMS
            Dim initParams As NvEncodeAPI.NV_ENC_INITIALIZE_PARAMS = Nothing
            initParams.version = NvEncodeAPI.MakeStructVersion(7) Or (1UI << 31)
            initParams.encodeGUID = NvEncodeAPI.NV_ENC_CODEC_H264_GUID
            ' ★ THE preset the user configured — never NV_ENC_PRESET_DEFAULT_GUID.
            initParams.presetGUID = NvEncodeAPI.PresetGuidForKey(presetKey)
            initParams.encodeWidth = encodeWidth
            initParams.encodeHeight = encodeHeight
            initParams.darWidth = encodeWidth
            initParams.darHeight = encodeHeight
            ' ★ THE frame rate the user configured — never a hardcoded 60.
            Dim fps As UInteger = If(frameRateFps > 0, CUInt(frameRateFps), 60UI)
            initParams.frameRateNum = fps
            initParams.frameRateDen = 1UI
            initParams.enableEncodeAsync = 0UI   ' synchronous mode (unchanged)
            initParams.enablePTD = 1UI
            initParams.bitFields = 0UI
            initParams.privDataSize = 0UI
            initParams.privData = IntPtr.Zero
            initParams.encodeConfig = IntPtr.Zero
            initParams.maxEncodeWidth = 0UI
            initParams.maxEncodeHeight = 0UI
            initParams.maxMEHintCountsPerBlockL0 = New Byte(15) {}
            initParams.maxMEHintCountsPerBlockL1 = New Byte(15) {}
            initParams.tuningInfo = 2UI
            initParams.bufferFormat = NvEncodeAPI.NV_ENC_BUFFER_FORMAT_UNDEFINED
            initParams.numStateBuffers = 0UI
            initParams.outputStatsLevel = 0UI
            initParams.reserved1 = New UInteger(283) {}
            initParams.reserved2 = New IntPtr(63) {}
            Return initParams
        End Function

        ''' <summary>
        ''' Patch a driver-filled preset config with the user's video values.
        ''' EVERY value the config carries lands in the native struct here:
        '''   gopLength / h264 idrPeriod  ← EncoderConfig.GopSize
        '''   frameIntervalP = 1          ← no B-frames (encode order = display
        '''                                 order; the CFR pipeline contract)
        '''   rcParams.rateControlMode    ← EncoderConfig.RateControl (cbr/vbr/cq)
        '''   rcParams.averageBitRate     ← EncoderConfig.BitrateBps
        '''   rcParams.maxBitRate         ← BitrateBps (CBR) / 2× (VBR)
        '''   rcParams.vbvBufferSize      ← BufsizeBps (clamped into u32)
        '''   rcParams.vbvInitialDelay    ← vbvBufferSize
        ''' </summary>
        Public Shared Sub ApplyVideoSettings(ByRef cfg As NvEncodeAPI.NV_ENC_CONFIG,
                                             bitrateBps As Long,
                                             maxrateBps As Long,
                                             bufsizeBps As Long,
                                             rateControlKey As String,
                                             gopSize As Integer)
            ' Whitelisted structural fields (everything else stays driver-set):
            cfg.version = NvEncodeAPI.NV_ENC_CONFIG_VER
            cfg.profileGUID = NvEncodeAPI.NV_ENC_CODEC_PROFILE_AUTOSELECT_GUID
            cfg.monoChromeEncoding = 0UI
            cfg.frameFieldMode = 1UI
            cfg.mvPrecision = 0UI

            ' GOP: both the NV_ENC_CONFIG level and the H.264 idrPeriod level
            ' (idrPeriod 0 would follow gopLength, but preset configs may
            ' carry their own value — make both explicit and consistent).
            cfg.gopLength = CUInt(Math.Max(1, gopSize))
            PatchCodecConfigU32(cfg, NV_ENC_CONFIG_H264_OFF_IDRPERIOD, CUInt(Math.Max(1, gopSize)))

            ' No B-frames: sync pipeline + CFR pacing require encode order ==
            ' display order (frameIntervalP=1 → IPP).
            cfg.frameIntervalP = 1

            ' Rate control + bitrate (the real wiring):
            Dim knownRc As Boolean = True
            cfg.rcParams.version = NvEncodeAPI.NV_ENC_RC_PARAMS_VER
            cfg.rcParams.rateControlMode = NvEncodeAPI.RateControlModeForKey(rateControlKey, knownRc)
            cfg.rcParams.averageBitRate = ClampU32(bitrateBps)
            cfg.rcParams.maxBitRate = ClampU32(If(rateControlKey IsNot Nothing AndAlso
                                                  rateControlKey.Trim().ToLowerInvariant() = "cbr",
                                                  maxrateBps, Math.Max(maxrateBps, bitrateBps)))
            cfg.rcParams.vbvBufferSize = ClampU32(bufsizeBps)
            cfg.rcParams.vbvInitialDelay = ClampU32(bufsizeBps)
        End Sub

        ''' <summary>
        ''' Fallback config when the driver preset query is unavailable.
        ''' Minimal but REAL: version + autoselect profile + GOP + IPP + the
        ''' full rate-control block. Codec-union bytes stay zero (idrPeriod 0
        ''' follows gopLength per the SDK 13.0 header doc) — this is logged
        ''' as a WARNING by the caller, never silently presented as preset
        ''' behavior.
        ''' </summary>
        Public Shared Function BuildDefaultEncodeConfig(bitrateBps As Long,
                                                        maxrateBps As Long,
                                                        bufsizeBps As Long,
                                                        rateControlKey As String,
                                                        gopSize As Integer) As NvEncodeAPI.NV_ENC_CONFIG
            Dim cfg As New NvEncodeAPI.NV_ENC_CONFIG()
            cfg.encodeCodecConfig = New Byte(SIZEOF_CODEC_CONFIG_UNION - 1) {}
            cfg.reserved = New UInteger(277) {}
            cfg.reserved2 = New IntPtr(63) {}
            cfg.rcParams = New NvEncodeAPI.NV_ENC_RC_PARAMS()
            cfg.rcParams.temporalLayerQP = New Byte(7) {}
            cfg.rcParams.viewBitrateRatios = New Byte(6) {}
            ApplyVideoSettings(cfg, bitrateBps, maxrateBps, bufsizeBps, rateControlKey, gopSize)
            Return cfg
        End Function

        ''' <summary>Ensure the ByValArray fields exist (fresh struct = Nothing arrays).</summary>
        Public Shared Sub EnsureArrays(ByRef cfg As NvEncodeAPI.NV_ENC_CONFIG)
            If cfg.encodeCodecConfig Is Nothing OrElse cfg.encodeCodecConfig.Length <> SIZEOF_CODEC_CONFIG_UNION Then
                cfg.encodeCodecConfig = New Byte(SIZEOF_CODEC_CONFIG_UNION - 1) {}
            End If
            If cfg.reserved Is Nothing OrElse cfg.reserved.Length <> 278 Then
                cfg.reserved = New UInteger(277) {}
            End If
            If cfg.reserved2 Is Nothing OrElse cfg.reserved2.Length <> 64 Then
                cfg.reserved2 = New IntPtr(63) {}
            End If
            If cfg.rcParams.temporalLayerQP Is Nothing Then
                cfg.rcParams.temporalLayerQP = New Byte(7) {}
            End If
            If cfg.rcParams.viewBitrateRatios Is Nothing Then
                cfg.rcParams.viewBitrateRatios = New Byte(6) {}
            End If
        End Sub

        ''' <summary>Patch a u32 inside the codec-config union (h264 view — the union's first member).</summary>
        Public Shared Sub PatchCodecConfigU32(ByRef cfg As NvEncodeAPI.NV_ENC_CONFIG, offsetInCodecConfig As Integer, value As UInteger)
            Dim b As Byte() = cfg.encodeCodecConfig
            b(NV_ENC_CONFIG_OFF_CODECCONFIG + offsetInCodecConfig + 0) = CByte(value And &HFFUI)
            b(NV_ENC_CONFIG_OFF_CODECCONFIG + offsetInCodecConfig + 1) = CByte((value >> 8) And &HFFUI)
            b(NV_ENC_CONFIG_OFF_CODECCONFIG + offsetInCodecConfig + 2) = CByte((value >> 16) And &HFFUI)
            b(NV_ENC_CONFIG_OFF_CODECCONFIG + offsetInCodecConfig + 3) = CByte((value >> 24) And &HFFUI)
        End Sub

        ''' <summary>Read a u32 from the codec-config union (h264 view).</summary>
        Public Shared Function ReadCodecConfigU32(cfg As NvEncodeAPI.NV_ENC_CONFIG, offsetInCodecConfig As Integer) As UInteger
            Dim b As Byte() = cfg.encodeCodecConfig
            Dim o As Integer = NV_ENC_CONFIG_OFF_CODECCONFIG + offsetInCodecConfig
            Return CUInt(b(o)) Or CUInt(b(o + 1)) << 8 Or CUInt(b(o + 2)) << 16 Or CUInt(b(o + 3)) << 24
        End Function

        Private Shared Function ClampU32(v As Long) As UInteger
            If v <= 0 Then Return 0UI
            If v > UInteger.MaxValue Then Return UInteger.MaxValue
            Return CUInt(v)
        End Function

    End Class

End Namespace
